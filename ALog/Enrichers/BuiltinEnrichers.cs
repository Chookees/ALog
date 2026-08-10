namespace ALog.Enrichers;

using System.Diagnostics;
using ALog.Core;
using ALog.Public.Interfaces;

public sealed class MachineNameEnricher : ILogEnricher
{
    public const string PropertyName = "MachineName";

    public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
    {
        properties[PropertyName] = Environment.MachineName;
    }
}

public sealed class ThreadIdEnricher : ILogEnricher
{
    public const string PropertyName = "ThreadId";

    public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
    {
        properties[PropertyName] = Environment.CurrentManagedThreadId;
    }
}

public sealed class ProcessIdEnricher : ILogEnricher
{
    public const string PropertyName = "ProcessId";

    public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
    {
        properties[PropertyName] = Environment.ProcessId;
    }
}

public sealed class ActivityEnricher : ILogEnricher
{
    public const string TraceIdProperty = "TraceId";
    public const string SpanIdProperty = "SpanId";

    public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        properties[TraceIdProperty] = activity.TraceId.ToString();
        properties[SpanIdProperty] = activity.SpanId.ToString();
    }
}

/// <summary>
/// Adds a fixed property (e.g. application name/version).
/// </summary>
public sealed class PropertyEnricher : ILogEnricher
{
    private readonly string _name;
    private readonly object _value;

    public PropertyEnricher(string name, object value)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void Enrich(IDictionary<string, object> properties, LogEvent logEvent)
    {
        properties[_name] = _value;
    }
}
