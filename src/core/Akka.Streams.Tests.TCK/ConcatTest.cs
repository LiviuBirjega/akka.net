﻿//-----------------------------------------------------------------------
// <copyright file="ConcatTest.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using Akka.Streams.Dsl;
using Reactive.Streams;

namespace Akka.Streams.Tests.TCK
{
    class ConcatTest : AkkaPublisherVerification<int>
    {
        public override IPublisher<int> CreatePublisher(long elements) =>
            Source.From(Enumerate(elements/2))
                .Concat(Source.From(Enumerate((elements + 1)/2)))
                .RunWith(Sink.AsPublisher<int>(false), Materializer);
    }
}
