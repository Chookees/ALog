namespace ALog.Public.Interfaces;

/// <summary>
/// Optional: writers that accept a formatter. Config formatter is applied when this is null.
/// </summary>
public interface IFormattableLogWriter
{
    ILogFormatter? Formatter { get; set; }
}
