﻿//-----------------------------------------------------------------------
// <copyright file="BarrierCoordinator.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Event;

namespace Akka.Remote.TestKit
{
    /// <summary>
    ///
    /// This barrier coordinator gets informed of players connecting (NodeInfo),
    /// players being deliberately removed (RemoveClient) or failing (ClientDisconnected)
    /// by the controller. It also receives EnterBarrier requests, where upon the first
    /// one received the name of the current barrier is set and all other known clients
    /// are expected to join the barrier, whereupon all of the will be sent the successful
    /// EnterBarrier return message. In case of planned removals, this may just happen
    /// earlier, in case of failures the current barrier (and all subsequent ones) will
    /// be failed by sending BarrierFailed responses.
    ///
    ///INTERNAL API.
    /// </summary>
    internal sealed class BarrierCoordinator : FSM<BarrierCoordinator.State, BarrierCoordinator.Data>, ILoggingFSM
    {
        #region State types and messages

        public enum State
        {
            Idle,
            Waiting
        };

        public sealed class RemoveClient
        {
            public RemoveClient(RoleName name)
            {
                Name = name;
            }

            public RoleName Name { get; }
        }

        public sealed class Data
        {
            public Data(ImmutableHashSet<Controller.NodeInfo> clients, string barrier, ImmutableHashSet<IActorRef> arrived, Deadline deadline)
            {
                Deadline = deadline;
                Arrived = arrived;
                Barrier = barrier;
                Clients = clients;
            }

            public ImmutableHashSet<Controller.NodeInfo> Clients { get; }

            public string Barrier { get; }

            public ImmutableHashSet<IActorRef> Arrived { get; }

            public Deadline Deadline { get; }

            public Data Copy(ImmutableHashSet<Controller.NodeInfo> clients = null, string barrier = null,
                ImmutableHashSet<IActorRef> arrived = null, Deadline deadline = null)
            {
                return new Data(clients ?? Clients,
                    barrier ?? Barrier,
                    arrived ?? Arrived,
                    deadline ?? Deadline);
            }

            private bool Equals(Data other)
            {
                return (ReferenceEquals(Clients, other.Clients) || Clients.SequenceEqual(other.Clients))
                    && string.Equals(Barrier, other.Barrier)
                    && (ReferenceEquals(Arrived, other.Arrived) || Arrived.SequenceEqual(other.Arrived))
                    && Equals(Deadline, other.Deadline);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is Data data && Equals(data);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = (Clients != null ? Clients.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (Barrier != null ? Barrier.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (Arrived != null ? Arrived.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (Deadline != null ? Deadline.GetHashCode() : 0);
                    return hashCode;
                }
            }

            /// <summary>
            /// Compares two specified <see cref="Data"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="Data"/> used for comparison</param>
            /// <param name="right">The second <see cref="Data"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="Data"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(Data left, Data right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="Data"/> for inequality.
            /// </summary>
            /// <param name="left">The first <see cref="Data"/> used for comparison</param>
            /// <param name="right">The second <see cref="Data"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="Data"/> are not equal; otherwise <c>false</c></returns>
            public static bool operator !=(Data left, Data right)
            {
                return !Equals(left, right);
            }
        }

        public sealed class BarrierTimeoutException : Exception
        {
            public BarrierTimeoutException(Data barrierData)
                : base($"timeout while waiting for barrier '{barrierData.Barrier}'")
            {
                BarrierData = barrierData;
            }

            public Data BarrierData { get; }

            private bool Equals(BarrierTimeoutException other)
            {
                return Equals(BarrierData, other.BarrierData);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is BarrierTimeoutException exception && Equals(exception);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                return (BarrierData != null ? BarrierData.GetHashCode() : 0);
            }

            /// <summary>
            /// Compares two specified <see cref="BarrierTimeoutException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="BarrierTimeoutException"/> used for comparison</param>
            /// <param name="right">The second <see cref="BarrierTimeoutException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="BarrierTimeoutException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(BarrierTimeoutException left, BarrierTimeoutException right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="BarrierTimeoutException"/> for inequality.
            /// </summary>
            /// <param name="left">The first <see cref="BarrierTimeoutException"/> used for comparison</param>
            /// <param name="right">The second <see cref="BarrierTimeoutException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="BarrierTimeoutException"/> are not equal; otherwise <c>false</c></returns>
            public static bool operator !=(BarrierTimeoutException left, BarrierTimeoutException right)
            {
                return !Equals(left, right);
            }
        }

        public sealed class FailedBarrierException : Exception
        {
            public FailedBarrierException(Data barrierData)
                : base($"failing barrier '{barrierData.Barrier}'")
            {
                BarrierData = barrierData;
            }

            public Data BarrierData { get; }

            private bool Equals(FailedBarrierException other)
            {
                return Equals(BarrierData, other.BarrierData);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is FailedBarrierException exception && Equals(exception);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                return (BarrierData != null ? BarrierData.GetHashCode() : 0);
            }

            /// <summary>
            /// Compares two specified <see cref="FailedBarrierException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="FailedBarrierException"/> used for comparison</param>
            /// <param name="right">The second <see cref="FailedBarrierException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="FailedBarrierException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(FailedBarrierException left, FailedBarrierException right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="FailedBarrierException"/> for inequality.
            /// </summary>
            /// <param name="left">The first <see cref="FailedBarrierException"/> used for comparison</param>
            /// <param name="right">The second <see cref="FailedBarrierException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="FailedBarrierException"/> are not equal; otherwise <c>false</c></returns>
            public static bool operator !=(FailedBarrierException left, FailedBarrierException right)
            {
                return !Equals(left, right);
            }
        }

        public sealed class DuplicateNodeException : Exception
        {
            public DuplicateNodeException(Data barrierData, Controller.NodeInfo node)
                : base(string.Format(node.ToString()))
            {
                Node = node;
                BarrierData = barrierData;
            }

            public Data BarrierData { get; }

            public Controller.NodeInfo Node { get; }

            private bool Equals(DuplicateNodeException other)
            {
                return Equals(BarrierData, other.BarrierData) && Equals(Node, other.Node);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is DuplicateNodeException exception && Equals(exception);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((BarrierData != null ? BarrierData.GetHashCode() : 0)*397) ^ (Node != null ? Node.GetHashCode() : 0);
                }
            }

            /// <summary>
            /// Compares two specified <see cref="DuplicateNodeException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="DuplicateNodeException"/> used for comparison</param>
            /// <param name="right">The second <see cref="DuplicateNodeException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="DuplicateNodeException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(DuplicateNodeException left, DuplicateNodeException right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="DuplicateNodeException"/> for inequality.
            /// </summary>
            /// <param name="left">The first <see cref="DuplicateNodeException"/> used for comparison</param>
            /// <param name="right">The second <see cref="DuplicateNodeException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="DuplicateNodeException"/> are not equal; otherwise <c>false</c></returns>
            public static bool operator !=(DuplicateNodeException left, DuplicateNodeException right)
            {
                return !Equals(left, right);
            }
        }

        public sealed class WrongBarrierException : Exception
        {
            public WrongBarrierException(string barrier, IActorRef client, RoleName roleName, Data barrierData)
                : base($"[{client}] [{roleName}] tried to enter '{barrier}' while we were waiting for '{barrierData.Barrier}'")
            {
                BarrierData = barrierData;
                Client = client;
                Barrier = barrier;
            }

            public string Barrier { get; }

            public IActorRef Client { get; }

            public Data BarrierData { get; }

            private bool Equals(WrongBarrierException other)
            {
                return string.Equals(Barrier, other.Barrier) && Equals(Client, other.Client) && Equals(BarrierData, other.BarrierData);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is WrongBarrierException exception && Equals(exception);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = (Barrier != null ? Barrier.GetHashCode() : 0);
                    hashCode = (hashCode*397) ^ (Client != null ? Client.GetHashCode() : 0);
                    hashCode = (hashCode*397) ^ (BarrierData != null ? BarrierData.GetHashCode() : 0);
                    return hashCode;
                }
            }

            /// <summary>
            /// Compares two specified <see cref="WrongBarrierException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="WrongBarrierException"/> used for comparison</param>
            /// <param name="right">The second <see cref="WrongBarrierException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="WrongBarrierException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(WrongBarrierException left, WrongBarrierException right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="WrongBarrierException"/> for inequality.
            /// </summary>
            /// <param name="left">The first <see cref="WrongBarrierException"/> used for comparison</param>
            /// <param name="right">The second <see cref="WrongBarrierException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="WrongBarrierException"/> are not equal; otherwise <c>false</c></returns>
            public static bool operator !=(WrongBarrierException left, WrongBarrierException right)
            {
                return !Equals(left, right);
            }
        }

        public sealed class BarrierEmptyException : Exception
        {
            public BarrierEmptyException(Data barrierData, string message)
                : base(message)
            {
                BarrierData = barrierData;
            }

            public Data BarrierData { get; }

            private bool Equals(BarrierEmptyException other)
            {
                return Equals(BarrierData, other.BarrierData);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is BarrierEmptyException exception && Equals(exception);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                return (BarrierData != null ? BarrierData.GetHashCode() : 0);
            }

            /// <summary>
            /// Compares two specified <see cref="BarrierEmptyException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="BarrierEmptyException"/> used for comparison</param>
            /// <param name="right">The second <see cref="BarrierEmptyException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="BarrierEmptyException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(BarrierEmptyException left, BarrierEmptyException right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="BarrierEmptyException"/> for inequality.
            /// </summary>
            /// <param name="left">The first <see cref="BarrierEmptyException"/> used for comparison</param>
            /// <param name="right">The second <see cref="BarrierEmptyException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="BarrierEmptyException"/> are not equal; otherwise <c>false</c></returns>
            public static bool operator !=(BarrierEmptyException left, BarrierEmptyException right)
            {
                return !Equals(left, right);
            }
        }

        public sealed class ClientLostException : Exception
        {
            public ClientLostException(Data barrierData, RoleName client)
                : base($"unannounced disconnect of {client}")
            {
                Client = client;
                BarrierData = barrierData;
            }

            public Data BarrierData { get; }

            public RoleName Client { get; }

            private bool Equals(ClientLostException other)
            {
                return Equals(BarrierData, other.BarrierData) && Equals(Client, other.Client);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                return obj is ClientLostException exception && Equals(exception);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((BarrierData != null ? BarrierData.GetHashCode() : 0) * 397)
                        ^ (Client != null ? Client.GetHashCode() : 0);
                }
            }

            /// <summary>
            /// Compares two specified <see cref="ClientLostException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="ClientLostException"/> used for comparison</param>
            /// <param name="right">The second <see cref="ClientLostException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="ClientLostException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator ==(ClientLostException left, ClientLostException right)
            {
                return Equals(left, right);
            }

            /// <summary>
            /// Compares two specified <see cref="ClientLostException"/> for equality.
            /// </summary>
            /// <param name="left">The first <see cref="ClientLostException"/> used for comparison</param>
            /// <param name="right">The second <see cref="ClientLostException"/> used for comparison</param>
            /// <returns><c>true</c> if both <see cref="ClientLostException"/> are equal; otherwise <c>false</c></returns>
            public static bool operator !=(ClientLostException left, ClientLostException right)
            {
                return !Equals(left, right);
            }
        }

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="BarrierCoordinator"/> class.
        /// </summary>
        public BarrierCoordinator()
        {
            InitFSM();
        }

        //this shall be set to true if all subsequent barriers shall fail
        private bool _failed = false;
        private readonly ILoggingAdapter _log = Context.GetLogger();

        protected override void PreRestart(Exception reason, object message) { }
        protected override void PostRestart(Exception reason)
        {
            _failed = true;
        }

        protected void InitFSM()
        {
            StartWith(State.Idle, new Data(ImmutableHashSet.Create<Controller.NodeInfo>(), "", ImmutableHashSet.Create<IActorRef>(), null));

            WhenUnhandled(@event =>
            {
                var clients = @event.StateData.Clients;
                var arrived = @event.StateData.Arrived;
                switch (@event.FsmEvent)
                {
                    case Controller.NodeInfo node:
                        if (clients.Any(x => x.Name == node.Name)) throw new DuplicateNodeException(@event.StateData, node);
                        return Stay().Using(@event.StateData.Copy(clients.Add(node)));
                    
                    case Controller.ClientDisconnected disconnected:
                        if (arrived == null || arrived.Count == 0)
                            return Stay()
                                .Using(@event.StateData.Copy(clients.Where(x => x.Name != disconnected.Name).ToImmutableHashSet()));
                        
                        var client = clients.FirstOrDefault(x => x.Name == disconnected.Name);
                        if (client == null) 
                            return Stay();
                        
                        throw new ClientLostException(@event.StateData.Copy(clients.Remove(client), arrived:arrived.Where(x => x != client.FSM).ToImmutableHashSet()), disconnected.Name);
                    
                    default:
                        return null;
                }
            });

            When(State.Idle, @event =>
            {
                var clients = @event.StateData.Clients;
                switch (@event.FsmEvent)
                {
                    case EnterBarrier barrier:
                        if (_failed)
                            return Stay().Replying(new ToClient<BarrierResult>(new BarrierResult(barrier.Name, false)));
                        
                        if (clients.Select(x => x.FSM).SequenceEqual(new List<IActorRef>() {Sender}))
                            return Stay().Replying(new ToClient<BarrierResult>(new BarrierResult(barrier.Name, true)));
                        
                        if (clients.All(x => !Equals(x.FSM, Sender)))
                            return Stay().Replying(new ToClient<BarrierResult>(new BarrierResult(barrier.Name, false)));
                        
                        return GoTo(State.Waiting)
                            .Using(@event.StateData.Copy(
                                barrier: barrier.Name,
                                arrived: ImmutableHashSet.Create(Sender),
                                deadline: GetDeadline(barrier.Timeout)));
                    
                    case RemoveClient client:
                        if (clients.Count == 0)
                            throw new BarrierEmptyException(@event.StateData, $"cannot remove {client.Name}: no client to remove");
                        
                        return Stay().Using(@event.StateData.Copy(clients.Where(x => x.Name != client.Name).ToImmutableHashSet()));
                    
                    default:
                        return null;
                }
            });

            When(State.Waiting, @event =>
            {
                var currentBarrier = @event.StateData.Barrier;
                var clients = @event.StateData.Clients;
                var arrived = @event.StateData.Arrived;

                switch (@event.FsmEvent)
                {
                    case EnterBarrier barrier:
                        if (barrier.Name != currentBarrier)
                            throw new WrongBarrierException(barrier.Name, Sender, barrier.Role, @event.StateData);
                        var together = clients.Any(x => Equals(x.FSM, Sender))
                            ? @event.StateData.Arrived.Add(Sender)
                            : @event.StateData.Arrived;
                        var enterDeadline = GetDeadline(barrier.Timeout);
                        //we only allow the deadlines to get shorter
                        if (enterDeadline.TimeLeft < @event.StateData.Deadline.TimeLeft)
                        {
                            SetTimer("Timeout", StateTimeout.Instance, enterDeadline.TimeLeft, false);
                            return HandleBarrier(@event.StateData.Copy(arrived: together, deadline: enterDeadline));
                        }
                        
                        return HandleBarrier(@event.StateData.Copy(arrived: together));
                    
                    case RemoveClient client:
                        var removedClient = clients.FirstOrDefault(x => x.Name == client.Name);
                        if (removedClient == null) 
                            return Stay();
                        
                        return HandleBarrier(@event.StateData.Copy(clients.Remove(removedClient),
                                arrived: arrived.Where(x => !Equals(x, removedClient.FSM)).ToImmutableHashSet()));
                    
                    case FailBarrier barrier:
                        if(barrier.Name != currentBarrier) 
                            throw new WrongBarrierException(barrier.Name, Sender, barrier.Role, @event.StateData);
                        throw new FailedBarrierException(@event.StateData);
                        
                    case StateTimeout _:
                        throw new BarrierTimeoutException(@event.StateData);
                    
                    default:
                        return null;
                }
            });

            OnTransition((state, nextState) =>
            {
                if (state == State.Idle && nextState == State.Waiting) SetTimer("Timeout", StateTimeout.Instance, NextStateData.Deadline.TimeLeft, false);
                else if(state == State.Waiting && nextState == State.Idle) CancelTimer("Timeout");
            });

            Initialize();
        }

        public State<State,Data> HandleBarrier(Data data)
        {
            _log.Debug("handleBarrier({0})", data.Barrier);
            if (data.Arrived.Count == 0)
            {
                return GoTo(State.Idle).Using(data.Copy(barrier: string.Empty));
            }
            else if (data.Clients.Select(x => x.FSM).ToImmutableHashSet().Except(data.Arrived).Count == 0)
            {
                foreach (var arrived in data.Arrived)
                {
                    arrived.Tell(new ToClient<BarrierResult>(new BarrierResult(data.Barrier, true)));
                }
                return
                    GoTo(State.Idle)
                        .Using(data.Copy(barrier: string.Empty, arrived: ImmutableHashSet.Create<IActorRef>()));
            }
            else
            {
                return Stay().Using(data);
            }
        }

        public static Deadline GetDeadline(TimeSpan? timeout)
        {
            return Deadline.Now + (timeout ?? TestConductor.Get(Context.System).Settings.BarrierTimeout);
        }
    }
}

