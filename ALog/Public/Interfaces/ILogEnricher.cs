namespace ALog.Public.Interfaces;

using ALog.Core;

/// <summary>
/// Adds ambient properties to every <see cref="LogEvent"/> before dispatch.
/// </summary>
public interface ILogEnricher
{
    void Enrich(IDictionary<string, object> properties, LogEvent logEvent);
}
