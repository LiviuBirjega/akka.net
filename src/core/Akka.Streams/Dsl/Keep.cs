﻿//-----------------------------------------------------------------------
// <copyright file="Keep.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Reflection;

namespace Akka.Streams.Dsl
{
    /// <summary>
    /// Convenience functions for often-encountered purposes like keeping only the
    /// left (first) or only the right (second) of two input values.
    /// </summary> 
    public static class Keep
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="TLeft">TBD</typeparam>
        /// <typeparam name="TRight">TBD</typeparam>
        /// <param name="left">TBD</param>
        /// <param name="right">TBD</param>
        /// <returns>TBD</returns>
        public static TLeft Left<TLeft, TRight>(TLeft left, TRight right) => left;

        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="TLeft">TBD</typeparam>
        /// <typeparam name="TRight">TBD</typeparam>
        /// <param name="left">TBD</param>
        /// <param name="right">TBD</param>
        /// <returns>TBD</returns>
        public static TRight Right<TLeft, TRight>(TLeft left, TRight right) => right;

        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="TLeft">TBD</typeparam>
        /// <typeparam name="TRight">TBD</typeparam>
        /// <param name="left">TBD</param>
        /// <param name="right">TBD</param>
        /// <returns>TBD</returns>
        public static (TLeft, TRight) Both<TLeft, TRight>(TLeft left, TRight right) => (left, right);

        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="TLeft">TBD</typeparam>
        /// <typeparam name="TRight">TBD</typeparam>
        /// <param name="left">TBD</param>
        /// <param name="right">TBD</param>
        /// <returns>TBD</returns>
        public static NotUsed None<TLeft, TRight>(TLeft left, TRight right) => NotUsed.Instance;

        private static readonly RuntimeMethodHandle KeepRightMethodhandle = typeof(Keep).GetMethod(nameof(Right)).MethodHandle;

        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="T1">TBD</typeparam>
        /// <typeparam name="T2">TBD</typeparam>
        /// <typeparam name="T3">TBD</typeparam>
        /// <param name="fn">TBD</param>
        /// <returns>TBD</returns>
        public static bool IsRight<T1, T2, T3>(Func<T1, T2, T3> fn)
        {
            return fn.GetMethodInfo().IsGenericMethod && fn.GetMethodInfo().GetGenericMethodDefinition().MethodHandle.Value == KeepRightMethodhandle.Value;
        }

        private static readonly RuntimeMethodHandle KeepLeftMethodhandle = typeof(Keep).GetMethod(nameof(Left)).MethodHandle;

        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="T1">TBD</typeparam>
        /// <typeparam name="T2">TBD</typeparam>
        /// <typeparam name="T3">TBD</typeparam>
        /// <param name="fn">TBD</param>
        /// <returns>TBD</returns>
        public static bool IsLeft<T1, T2, T3>(Func<T1, T2, T3> fn)
        {
            return fn.GetMethodInfo().IsGenericMethod && fn.GetMethodInfo().GetGenericMethodDefinition().MethodHandle.Value == KeepLeftMethodhandle.Value;
        }
    }
}
