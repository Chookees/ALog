/// <summary>Exception in the global namespace. The plain-text label has no namespace prefix.</summary>
public sealed class GlobalNameException : Exception
{
    /// <summary>Creates the exception with the caller's message.</summary>
    /// <param name="message">Message text.</param>
    public GlobalNameException(string message)
        : base(message)
    {
    }
}
