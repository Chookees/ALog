using System.Runtime.CompilerServices;

#pragma warning disable CA1710 // The short names are the locked display text: GenEx, NestedEx.
#pragma warning disable CA1715 // Nested type argument U is part of the locked name, not a public API.

namespace ALog.Tests.Naming;

/// <summary>Generic exception used to lock C# type syntax.</summary>
/// <typeparam name="T">Type argument shown in angle brackets.</typeparam>
public sealed class GenEx<T> : Exception
{
    /// <summary>Creates the exception with the caller's message.</summary>
    /// <param name="message">Message text.</param>
    public GenEx(string message)
        : base(message)
    {
    }
}

/// <summary>Generic exception whose type argument is not a <c>System</c> type.</summary>
/// <typeparam name="T">Type argument that keeps its namespace when it is not in <c>System</c>.</typeparam>
public sealed class Box<T> : Exception
{
    /// <summary>Creates the exception with the caller's message.</summary>
    /// <param name="message">Message text.</param>
    public Box(string message)
        : base(message)
    {
    }
}

/// <summary>Outer type for a nested exception and a generic frame.</summary>
public sealed class Outer
{
    /// <summary>Nested exception. The text uses a dot, not <c>+</c>.</summary>
    public sealed class NestedEx : Exception
    {
        /// <summary>Creates the exception with the caller's message.</summary>
        /// <param name="message">Message text.</param>
        public NestedEx(string message)
            : base(message)
        {
        }
    }

    /// <summary>Generic nested type. A frame reads <c>Outer.Inner2&lt;T&gt;.Go</c>.</summary>
    /// <typeparam name="T">Type argument shown on the frame.</typeparam>
    public sealed class Inner2<T>
    {
        /// <summary>Throws so the stack frame names this method.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Go()
        {
            throw new InvalidOperationException("from-go");
        }
    }
}

/// <summary>Generic outer type, so a nested generic keeps only its own argument.</summary>
/// <typeparam name="T">Argument of the outer type.</typeparam>
public sealed class Holder<T>
{
    /// <summary>Inner exception. The runtime hands it the outer argument as well as its own.</summary>
    /// <typeparam name="U">Argument that belongs to this nested type.</typeparam>
    public sealed class Inner<U> : Exception
    {
        /// <summary>Creates the exception with the caller's message.</summary>
        /// <param name="message">Message text.</param>
        public Inner(string message)
            : base(message)
        {
        }
    }
}

/// <summary>Seventeen type arguments, so the name walk stops at sixteen.</summary>
/// <typeparam name="T1">Shown.</typeparam>
/// <typeparam name="T2">Shown.</typeparam>
/// <typeparam name="T3">Shown.</typeparam>
/// <typeparam name="T4">Shown.</typeparam>
/// <typeparam name="T5">Shown.</typeparam>
/// <typeparam name="T6">Shown.</typeparam>
/// <typeparam name="T7">Shown.</typeparam>
/// <typeparam name="T8">Shown.</typeparam>
/// <typeparam name="T9">Shown.</typeparam>
/// <typeparam name="T10">Shown.</typeparam>
/// <typeparam name="T11">Shown.</typeparam>
/// <typeparam name="T12">Shown.</typeparam>
/// <typeparam name="T13">Shown.</typeparam>
/// <typeparam name="T14">Shown.</typeparam>
/// <typeparam name="T15">Shown.</typeparam>
/// <typeparam name="T16">Shown. The last argument the walk keeps.</typeparam>
/// <typeparam name="T17">Dropped. The walk stops at sixteen.</typeparam>
public sealed class WideException<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17> : Exception
{
}
