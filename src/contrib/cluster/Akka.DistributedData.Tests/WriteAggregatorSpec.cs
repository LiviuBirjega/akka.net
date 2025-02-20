﻿//-----------------------------------------------------------------------
// <copyright file="WriteAggregatorSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Configuration;
using Akka.DistributedData.Internal;
using Akka.TestKit;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.DistributedData.Tests
{
    [Collection("DistributedDataSpec")]
    public class WriteAggregatorSpec : Akka.TestKit.Xunit2.TestKit
    {
        internal class TestWriteAggregator<T> : WriteAggregator where T : IReplicatedData
        {
            private readonly IImmutableDictionary<Address, IActorRef> _probes;

            public TestWriteAggregator(
                IKey<T> key,
                T data,
                Delta delta,
                IWriteConsistency consistency,
                IImmutableDictionary<Address, IActorRef> probes,
                IImmutableList<Address> nodes,
                IImmutableSet<Address> unreachable,
                IActorRef replyTo,
                bool durable)
                : base(key, new DataEnvelope(data), delta, consistency, null, nodes, unreachable, false, replyTo, durable)
            {
                _probes = probes;
            }

            protected override ActorSelection Replica(Address address) => Context.ActorSelection(_probes[address].Path);
            protected override Address SenderAddress => _probes.First(kv => Equals(kv.Value, Sender)).Key;
        }

        internal class WriteAckAdapter : ReceiveActor
        {
            public WriteAckAdapter(IActorRef replica)
            {
                IActorRef replicator = null;
                Receive<WriteAck>(ack => replicator?.Tell(ack));
                Receive<WriteNack>(ack => replicator?.Tell(ack));
                Receive<DeltaNack>(ack => replicator?.Tell(ack));
                ReceiveAny(msg =>
                {
                    replicator = Sender;
                    replica.Tell(msg);
                });
            }
        }

        private static Props TestWriteAggregatorProps(GSet<string> data,
            IWriteConsistency consistency,
            IImmutableDictionary<Address, IActorRef> probes,
            IImmutableList<Address> nodes,
            IImmutableSet<Address> unreachable,
            IActorRef replyTo,
            bool durable) => Actor.Props.Create(() => new TestWriteAggregator<GSet<string>>(KeyA, data, null, consistency, probes, nodes, unreachable, replyTo, durable));

        private static Props TestWriteAggregatorPropsWithDelta(ORSet<string> data,
            Delta delta,
            IWriteConsistency consistency,
            IImmutableDictionary<Address, IActorRef> probes,
            IImmutableList<Address> nodes,
            IImmutableSet<Address> unreachable,
            IActorRef replyTo,
            bool durable) => Actor.Props.Create(() => new TestWriteAggregator<ORSet<string>>(KeyB, data, delta, consistency, probes, nodes, unreachable, replyTo, durable));

        private static readonly GSetKey<string> KeyA = new("a");
        private static readonly ORSetKey<string> KeyB = new("b");

        private readonly Address _nodeA = new("akka.tcp", "Sys", "a", 2552);
        private readonly Address _nodeB = new("akka.tcp", "Sys", "b", 2552);
        private readonly Address _nodeC = new("akka.tcp", "Sys", "c", 2552);
        private readonly Address _nodeD = new("akka.tcp", "Sys", "d", 2552);
        private readonly IImmutableList<Address> _nodes;

        private readonly GSet<string> _data = GSet.Create("A", "B");
        private readonly WriteTo _writeThree = new(3, TimeSpan.FromSeconds(3));
        private readonly WriteMajority _writeMajority = new(TimeSpan.FromSeconds(3));
        private readonly WriteAll _writeAll;

        private readonly ORSet<string> _fullState1;
        private readonly ORSet<string> _fullState2;
        private readonly Delta _delta;

        public WriteAggregatorSpec(ITestOutputHelper output) : base(ConfigurationFactory.ParseString($@"
            akka.actor.provider = ""Akka.Cluster.ClusterActorRefProvider, Akka.Cluster""
            akka.remote.dot-netty.tcp.port = 0
            akka.cluster.distributed-data.durable.lmdb {{
                dir = ""target/WriteAggregatorSpec-{DateTime.UtcNow.Ticks}-ddata""
                map-size = 10MiB
            }}"), "WriteAggregatorSpec", output)
        {
            _nodes = ImmutableList.CreateRange(new[] {_nodeA, _nodeB, _nodeC, _nodeD});

            var cluster = Akka.Cluster.Cluster.Get(Sys);
            _fullState1 = ORSet<string>.Empty.Add(cluster, "a").Add(cluster, "b");
            _fullState2 = _fullState1.ResetDelta().Add(cluster, "c");
            _delta = new Delta(new DataEnvelope(_fullState2.Delta), 2L, 2L);
            _writeAll = new WriteAll(Dilated(TimeSpan.FromSeconds(3)));
        }

        [Fact]
        public void WriteAggregator_must_send_at_least_half_N_plus_1_replicas_when_WriteMajority()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeMajority, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, false));

            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            ExpectMsg(new UpdateSuccess(KeyA, null));
            Watch(aggregator);
            ExpectTerminated(aggregator);
        }

        [Fact]
        public void WriteAggregator_must_send_to_more_when_no_immediate_reply()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeMajority, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, false));

            probe.ExpectMsg<Write>();
            // no reply
            probe.ExpectMsg<Write>();
            // no reply
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            ExpectMsg(new UpdateSuccess(KeyA, null));
            Watch(aggregator);
            ExpectTerminated(aggregator);
        }

        [Fact]
        public void WriteAggregator_must_timeout_when_less_than_required_ACKs()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeMajority, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, false));

            probe.ExpectMsg<Write>();
            // no reply
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            // no reply
            probe.ExpectMsg<Write>();
            // no reply

            Within(Dilated(TimeSpan.FromSeconds(10)), () => // have to pad the time here, since default timeout is ~3s which is also default wait time
            {
                ExpectMsg(new UpdateTimeout(KeyA, null));
                Watch(aggregator);
                ExpectTerminated(aggregator);
            });

        }

        [Fact]
        public void WriteAggregator_must_callculate_majority_with_min_capactiy()
        {
            var minCap = 5;

            ReadWriteAggregator.CalculateMajority(minCap, 3, 0).Should().Be(3);
            ReadWriteAggregator.CalculateMajority(minCap, 4, 0).Should().Be(4);
            ReadWriteAggregator.CalculateMajority(minCap, 5, 0).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(minCap, 6, 0).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(minCap, 7, 0).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(minCap, 8, 0).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(minCap, 9, 0).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(minCap, 10, 0).Should().Be(6);
            ReadWriteAggregator.CalculateMajority(minCap, 11, 0).Should().Be(6);
            ReadWriteAggregator.CalculateMajority(minCap, 12, 0).Should().Be(7);
        }

        [Fact]
        public void WriteAggregator_must_callculate_majority_with_additional()
        {
            ReadWriteAggregator.CalculateMajority(0, 3, 1).Should().Be(3);
            ReadWriteAggregator.CalculateMajority(0, 3, 2).Should().Be(3);
            ReadWriteAggregator.CalculateMajority(0, 4, 1).Should().Be(4);
            ReadWriteAggregator.CalculateMajority(0, 5, 1).Should().Be(4);
            ReadWriteAggregator.CalculateMajority(0, 5, 2).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(0, 6, 1).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(0, 7, 1).Should().Be(5);
            ReadWriteAggregator.CalculateMajority(0, 8, 1).Should().Be(6);
            ReadWriteAggregator.CalculateMajority(0, 8, 2).Should().Be(7);
            ReadWriteAggregator.CalculateMajority(0, 9, 1).Should().Be(6);
            ReadWriteAggregator.CalculateMajority(0, 10, 1).Should().Be(7);
            ReadWriteAggregator.CalculateMajority(0, 11, 1).Should().Be(7);
            ReadWriteAggregator.CalculateMajority(0, 11, 3).Should().Be(9);
        }

        [Fact]
        public void WriteAggregator_must_callculate_majority_with_additional_and_min_capactiy()
        {
            ReadWriteAggregator.CalculateMajority(5, 9, 1).Should().Be(6);
            ReadWriteAggregator.CalculateMajority(7, 9, 1).Should().Be(7);
            ReadWriteAggregator.CalculateMajority(10, 9, 1).Should().Be(9);
        }

        [Fact]
        public void WriteAggregator_with_delta_must_send_delta_first()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorPropsWithDelta(_fullState2, _delta, _writeMajority, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, false));

            Watch(aggregator);

            probe.ExpectMsg<DeltaPropagation>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<DeltaPropagation>();
            probe.LastSender.Tell(WriteAck.Instance);
            ExpectMsg(new UpdateSuccess(KeyB, null));

            ExpectTerminated(aggregator);
        }

        [Fact]
        public void WriteAggregator_with_delta_must_retry_with_full_state_when_no_immediate_reply_or_nack()
        {
            var testProbes = Probes();
            var testProbeRefs = testProbes.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.Item2);
            var aggregator = Sys.ActorOf(TestWriteAggregatorPropsWithDelta(_fullState2, _delta, _writeAll, testProbeRefs, _nodes, ImmutableHashSet<Address>.Empty, TestActor, false));

            Watch(aggregator);

            testProbes[_nodeA].Item1.ExpectMsg<DeltaPropagation>();
            // no reply
            testProbes[_nodeB].Item1.ExpectMsg<DeltaPropagation>();
            testProbes[_nodeB].Item1.LastSender.Tell(WriteAck.Instance);
            testProbes[_nodeC].Item1.ExpectMsg<DeltaPropagation>();
            testProbes[_nodeC].Item1.LastSender.Tell(WriteAck.Instance);
            testProbes[_nodeD].Item1.ExpectMsg<DeltaPropagation>();
            testProbes[_nodeD].Item1.LastSender.Tell(DeltaNack.Instance);

            // second round
            testProbes[_nodeA].Item1.ExpectMsg<Write>();
            testProbes[_nodeA].Item1.LastSender.Tell(WriteAck.Instance);
            testProbes[_nodeD].Item1.ExpectMsg<Write>();
            testProbes[_nodeD].Item1.LastSender.Tell(WriteAck.Instance);
            testProbes[_nodeB].Item1.ExpectNoMsg(TimeSpan.FromMilliseconds(100));
            testProbes[_nodeC].Item1.ExpectNoMsg(TimeSpan.FromMilliseconds(100));

            ExpectMsg(new UpdateSuccess(KeyB, null));
            ExpectTerminated(aggregator);
        }

        [Fact]
        public void WriteAggregator_with_delta_must_timeout_when_less_than_required_ACKs()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorPropsWithDelta(_fullState2, _delta, _writeAll, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, false));

            Watch(aggregator);

            probe.ExpectMsg<DeltaPropagation>();
            // no reply
            probe.ExpectMsg<DeltaPropagation>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<DeltaPropagation>();
            probe.ExpectMsg<DeltaPropagation>();
            
            // nack - will force a write to be sent right away
            probe.LastSender.Tell(DeltaNack.Instance);
            probe.ExpectMsg<Write>();

            // only 1 ack so we expect 3 full state Write
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.ExpectMsg<Write>();

            // still not enough acks
            Within(Dilated(TimeSpan.FromSeconds(10)), () => // have to pad the time here, since default timeout is ~3s which is also default wait time
            {
                ExpectMsg(new UpdateTimeout(KeyB, null));
                ExpectTerminated(aggregator);
            });
        }

        [Fact]
        public void Durable_WriteAggregator_must_not_reply_before_local_confirmation()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeThree, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, true));
            Watch(aggregator);

            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            ExpectNoMsg(TimeSpan.FromMilliseconds(200));

            // the local write
            aggregator.Tell(new UpdateSuccess(KeyA, null));

            ExpectMsg(new UpdateSuccess(KeyA, null));
            ExpectTerminated(aggregator);
        }

        [Fact]
        public void Durable_WriteAggregator_must_tolerate_WriteNack_if_enough_WriteAck()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeThree, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, true));
            Watch(aggregator);

            aggregator.Tell(new UpdateSuccess(KeyA, null));
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteNack.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);

            ExpectMsg(new UpdateSuccess(KeyA, null));
            ExpectTerminated(aggregator);
        }

        [Fact]
        public void Durable_WriteAggregator_must_reply_with_StoreFailure_when_too_many_nacks()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeMajority, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, true));
            Watch(aggregator);

            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteNack.Instance);
            aggregator.Tell(new UpdateSuccess(KeyA, null));
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteNack.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteNack.Instance);

            ExpectMsg(new StoreFailure(KeyA, null));
            ExpectTerminated(aggregator);
        }

        [Fact]
        public void Durable_WriteAggregator_must_timeout_when_less_than_required_ACKs()
        {
            var probe = CreateTestProbe();
            var aggregator = Sys.ActorOf(TestWriteAggregatorProps(_data, _writeMajority, Probes(probe.Ref), _nodes, ImmutableHashSet<Address>.Empty, TestActor, true));
            Watch(aggregator);

            probe.ExpectMsg<Write>();
            // no reply
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteAck.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteNack.Instance);
            probe.ExpectMsg<Write>();
            probe.LastSender.Tell(WriteNack.Instance);


            Within(Dilated(TimeSpan.FromSeconds(10)), () => // have to pad the time here, since default timeout is ~3s which is also default wait time
            {
                ExpectMsg(new UpdateTimeout(KeyA, null));
                ExpectTerminated(aggregator);
            });
        }

        private IImmutableDictionary<Address, IActorRef> Probes(IActorRef probe) =>
            _nodes.Select(address =>
                new KeyValuePair<Address, IActorRef>(
                    address,
                    Sys.ActorOf(Props.Create(() => new WriteAckAdapter(probe)))))
                .ToImmutableDictionary();

        private IImmutableDictionary<Address, (TestProbe, IActorRef)> Probes() =>
            _nodes.Select(address =>
                {
                    var probe = CreateTestProbe("probe-" + address.Host);
                    return new KeyValuePair<Address, (TestProbe, IActorRef)>(address,
                        (probe, Sys.ActorOf(Props.Create(() => new WriteAckAdapter(probe.Ref)))));
                })
                .ToImmutableDictionary();
    }
}
