namespace Microsoft.Data.Demo;

/// <summary>
/// Exception in a <c>Microsoft.*</c> namespace. The plain-text label keeps the full name.
/// </summary>
public sealed class DemoException : Exception
{
    /// <summary>Creates the exception with the caller's message.</summary>
    /// <param name="message">Message text.</param>
    public DemoException(string message)
        : base(message)
    {
    }
}
