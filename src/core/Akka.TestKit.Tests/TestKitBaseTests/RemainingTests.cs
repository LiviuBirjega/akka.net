﻿//-----------------------------------------------------------------------
// <copyright file="RemainingTests.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Xunit;

namespace Akka.TestKit.Tests.Xunit2.TestKitBaseTests
{
    public class RemainingTests : TestKit.Xunit2.TestKit
    {
        [Fact]
        public void Throw_if_remaining_is_called_outside_Within()
        {
            Assert.Throws<InvalidOperationException>(() => Remaining);
        }
    }
}

