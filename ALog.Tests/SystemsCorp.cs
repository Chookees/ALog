namespace SystemsCorp;

/// <summary>Exception whose namespace starts with the letters of <c>System</c> and keeps its full name.</summary>
public sealed class FooException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    /// <param name="message">Text shown after the type. Not null.</param>
    public FooException(string message)
        : base(message)
    {
    }
}

/// <summary>User code whose namespace starts with the letters of System and must still be shown.</summary>
public static class Billing
{
    /// <summary>Throws from line 11 of <c>Billing.cs</c>.</summary>
    public static void Charge()
    {
#line 11 "Billing.cs"
        throw new InvalidOperationException("corp");
#line default
    }
}
