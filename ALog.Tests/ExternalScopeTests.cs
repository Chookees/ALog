using System.Diagnostics;
using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using ALog.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALog.Tests;

/// <summary>
/// The factory keeps scopes on <see cref="IExternalScopeProvider"/>, including the
/// activity scope. Dropping <see cref="ISupportExternalScope"/> loses both.
/// </summary>
public sealed class ExternalScopeTests : IDisposable
{
    public ExternalScopeTests()
    {
        Log.ClearContext();
        Log.Shutdown();
    }

    public void Dispose()
    {
        Log.ClearContext();
        Log.Shutdown();
    }

    [Fact]
    public void Factory_Activity_Trace_And_Span_Are_On_The_Event()
    {
        var writer = new RecordingWriter();
        using var sp = HostFactory(writer);
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Activity.Scope");

        using var activity = new Activity("work");
        activity.Start();
        try
        {
            logger.LogInformation("with-activity");
        }
        finally
        {
            activity.Stop();
        }

        var context = ContextOf(writer, "with-activity");
        Assert.Equal(TraceId(activity), Text(context, "TraceId"));
        Assert.Equal(SpanId(activity), Text(context, "SpanId"));
    }

    [Fact]
    public void Factory_External_Scope_Text_And_Kvp_Reach_The_Event()
    {
        var writer = new RecordingWriter();
        using var sp = HostFactory(writer);
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("External.Scope");

        // LogContext is the ALog chain. The factory must not push onto it:
        // once ISupportExternalScope is implemented, BeginScope stays on the
        // factory provider and is copied onto the event at emit time.
        using (LogContext.PushScope("from-alog"))
        using (logger.BeginScope("Batch 7"))
        using (logger.BeginScope(new Dictionary<string, object> { ["Job"] = "j-1" }))
        {
            logger.LogInformation("external-scope");

            var ambient = ContextManager.GetContext();
            Assert.Equal(new[] { "from-alog" }, Assert.IsType<string[]>(ambient["Scope"]));
            Assert.False(ambient.ContainsKey("Job"));
        }

        var during = ContextOf(writer, "external-scope");
        Assert.Equal("j-1", during["Job"]);
        Assert.Equal(new[] { "from-alog", "Batch 7" }, Assert.IsType<string[]>(during["Scope"]));
    }

    [Fact]
    public void Provider_Implements_ISupportExternalScope()
    {
        Assert.Contains(
            typeof(ISupportExternalScope),
            typeof(ALogLoggerProvider).GetInterfaces());
    }

    private static ServiceProvider HostFactory(RecordingWriter writer)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.Configure(options =>
            {
                options.ActivityTrackingOptions =
                    ActivityTrackingOptions.TraceId
                    | ActivityTrackingOptions.SpanId
                    | ActivityTrackingOptions.ParentId;
            });
            builder.AddALog(new LoggerConfig()
                .AddWriter(writer)
                .SetMinimumLevel(LogLevel.Trace));
        });
        return services.BuildServiceProvider();
    }

    private static string TraceId(Activity activity)
        => activity.IdFormat == ActivityIdFormat.W3C
            ? activity.TraceId.ToHexString()
            : activity.RootId ?? string.Empty;

    private static string SpanId(Activity activity)
        => activity.IdFormat == ActivityIdFormat.W3C
            ? activity.SpanId.ToHexString()
            : activity.Id ?? string.Empty;

    private static string Text(IReadOnlyDictionary<string, object> context, string key)
    {
        Assert.True(context.ContainsKey(key), "missing " + key);
        return context[key]?.ToString() ?? string.Empty;
    }

    private static Dictionary<string, object> ContextOf(RecordingWriter writer, string message)
    {
        LogEvent? found = null;
        foreach (var evt in writer.Events)
        {
            if (evt.Message == message)
            {
                found = evt;
                break;
            }
        }

        Assert.NotNull(found);
        Assert.NotNull(found!.Context);
        return new Dictionary<string, object>(found.Context!, StringComparer.Ordinal);
    }
}
