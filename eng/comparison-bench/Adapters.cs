// One adapter per library, native API only (BEN-02: no third-party substitutes).
// Every adapter writes the same fields (BEN-03): timestamp ISO-8601 UTC, level, category "Bench", message,
// values or exception. Plain lines start with "[<timestamp>]" so the verifier can find record starts.
// Each configuration follows the library's own performance documentation; the source is linked next to it.
using System.Diagnostics;
using System.Reflection;
using Serilog; // WriteTo.Async / WriteTo.File extension methods

namespace AlogComparison;

/// <summary>Settings for one initialisation.</summary>
/// <param name="Path">Output file. The directory exists and is empty.</param>
/// <param name="Format">Plain or JSON.</param>
/// <param name="Filtered">True: minimum level Warn, calls at Info are filtered (T5).</param>
public sealed record LibConfig(string Path, Fmt Format, bool Filtered);

/// <summary>Common surface of the four libraries.</summary>
public interface ILib
{
    /// <summary>Harness id, e.g. alog-async.</summary>
    string Id { get; }

    /// <summary>Framework column: ALog, NLog, Serilog, log4net.</summary>
    string Framework { get; }

    /// <summary>Mode column: async, sync, buffered.</summary>
    string Mode { get; }

    /// <summary>Configuration summary with documentation links, written into details.jsonl.</summary>
    string Notes { get; }

    /// <summary>
    /// False for extra rows that are reported but never ranked or counted as a win (DC 2026-10-22, question 7:
    /// ALog sync). merge.py applies the same rule.
    /// </summary>
    bool Ranked { get; }

    /// <summary>Null when the scenario is supported; otherwise the n/a reason with a documentation link.</summary>
    string? NotSupported(string scenario);

    /// <summary>Builds the logger. Not timed except in T6.</summary>
    void Init(LibConfig config);

    /// <summary>Info: <see cref="Texts.Simple"/>.</summary>
    void Simple();

    /// <summary>Info: <see cref="Texts.Template"/> with (i, <see cref="Texts.Ip"/>).</summary>
    void Structured(int i);

    /// <summary>Error: <see cref="Texts.Error"/> with the exception.</summary>
    void Error(Exception ex);

    /// <summary>Warn: <see cref="Texts.Sentinel"/> (T5 only).</summary>
    void Sentinel();

    /// <summary>Waits until everything written so far is in the file. Used after warm-up, not timed.</summary>
    void Drain();

    /// <summary>Flush and dispose. Returns when every event is in the file. Timed.</summary>
    void Close();
}

/// <summary>Adapter factory and version helpers.</summary>
public static class Libs
{
    /// <summary>All adapter ids.</summary>
    public static readonly string[] All = ["alog-async", "alog-sync", "nlog", "serilog", "log4net"];

    /// <summary>
    /// Async queue capacity, the same for every library that has a queue (DC 2026-10-22, question 1): NLog
    /// AsyncTargetWrapper QueueLimit, Serilog.Sinks.Async bufferSize, ALog UseBackgroundQueue capacity. NLog,
    /// Serilog, and ALog wait when the queue is full (ALog: <see cref="ALog.Config.QueueFullMode.Block"/>). A lost
    /// event still makes the cell invalid. log4net's BufferingForwardingAppender is a synchronous batch buffer,
    /// not a queue; it keeps its default 512.
    /// </summary>
    public const int QueueCapacity = 10_000;

    /// <summary>Creates an adapter by id.</summary>
    public static ILib Create(string id) => id switch
    {
        "alog-async" => new AlogLib(useQueue: true),
        "alog-sync" => new AlogLib(useQueue: false),
        "nlog" => new NLogLib(),
        "serilog" => new SerilogLib(),
        "log4net" => new Log4NetLib(),
        _ => throw new ArgumentException("unknown library " + id, nameof(id)),
    };

    /// <summary>Informational version of the assembly that defines <paramref name="t"/>.</summary>
    public static string FullVersion(Type t)
        => t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? t.Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>Version without build metadata (the part before '+').</summary>
    public static string ShortVersion(Type t)
    {
        var v = FullVersion(t);
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? v : v[..plus];
    }

    /// <summary>Package (NuGet or project) versions from the deps.json file, plus informational versions and the runtime.</summary>
    public static Dictionary<string, string> Versions() => new()
    {
        ["ALog"] = PackageVersion("ALog"),
        ["NLog"] = PackageVersion("NLog"),
        ["Serilog"] = PackageVersion("Serilog"),
        ["Serilog.Sinks.File"] = PackageVersion("Serilog.Sinks.File"),
        ["Serilog.Sinks.Async"] = PackageVersion("Serilog.Sinks.Async"),
        ["log4net"] = PackageVersion("log4net"),
        ["BenchmarkDotNet"] = PackageVersion("BenchmarkDotNet"),
        ["ALog.informational"] = FullVersion(typeof(ALog.Core.LoggerEngine)),
        ["runtime"] = Environment.Version.ToString(),
    };

    /// <summary>
    /// Version of a referenced package as restored (key "Name/Version" in ALog.Comparison.deps.json). Falls back to the
    /// assembly's informational version without build metadata when the deps file is missing.
    /// </summary>
    public static string PackageVersion(string name)
    {
        var deps = Path.Combine(AppContext.BaseDirectory, typeof(Libs).Assembly.GetName().Name + ".deps.json");
        if (File.Exists(deps))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(deps));
            if (doc.RootElement.TryGetProperty("libraries", out var libs))
            {
                foreach (var p in libs.EnumerateObject())
                {
                    var slash = p.Name.IndexOf('/', StringComparison.Ordinal);
                    if (slash > 0 && string.Equals(p.Name[..slash], name, StringComparison.OrdinalIgnoreCase))
                        return p.Name[(slash + 1)..];
                }
            }
        }

        return name switch
        {
            "ALog" => ShortVersion(typeof(ALog.Core.LoggerEngine)),
            "NLog" => ShortVersion(typeof(NLog.Logger)),
            "Serilog" => ShortVersion(typeof(Serilog.Log)),
            "log4net" => ShortVersion(typeof(log4net.LogManager)),
            _ => "unknown",
        };
    }
}

/// <summary>
/// ALog through the public <see cref="ALog.Core.LoggerEngine"/>: <c>LogStructured</c> is the only public call that
/// carries a category (the static <c>Log</c> class has none). Writer: <c>FileLogWriter</c> keeps the file open
/// (64 KiB buffer, flush at least every 1 s). Formatter: <c>PlainTextFormatter</c> with its
/// standard exception format (one-line form, no forced stack; DC 2026-10-22, question 5) or <c>JsonFormatter</c>.
/// </summary>
/// <remarks>
/// Async mode: <c>UseBackgroundQueue</c> as in the README "Step 3: Background Queue"
/// (https://github.com/Chookees/ALog#step-3-background-queue-optional), capacity <see cref="Libs.QueueCapacity"/>,
/// batch 10, 100 ms, <see cref="ALog.Config.QueueFullMode.Block"/>. A full queue waits. A lost event still makes
/// the cell invalid. Close calls <c>FlushAsync</c> once, then <c>Dispose</c>. Both honour <c>FlushTimeout</c>
/// (default 5 s).
/// </remarks>
public sealed class AlogLib(bool useQueue) : ILib
{
    /// <summary>
    /// Queue-full behaviour of the async row: <see cref="ALog.Config.QueueFullMode.Block"/>. A full queue waits.
    /// Written into Notes and env.json (<c>alog_full_mode</c>).
    /// </summary>
    public const string FullMode = "block (QueueFullMode.Block)";

    private ALog.Core.LoggerEngine? engine;

    public string Id => useQueue ? "alog-async" : "alog-sync";

    public string Framework => "ALog";

    public string Mode => useQueue ? "async" : "sync";

    /// <summary>
    /// Value of <c>queue_full_mode</c> for this row. Async is <c>QueueFullMode=Block</c> unless a proof run
    /// selects another mode. Sync is <c>n/a (no queue)</c>.
    /// </summary>
    public string QueueFullModeRecord
    {
        get
        {
            if (!useQueue)
                return "n/a (no queue)";
            var raw = Environment.GetEnvironmentVariable("BENCH_MUTANT_FULL_MODE");
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "Block", StringComparison.OrdinalIgnoreCase))
                return "QueueFullMode=Block";
            raw = raw.Trim();
            if (string.Equals(raw, "Drop", StringComparison.OrdinalIgnoreCase))
                return "QueueFullMode=Drop";
            return "QueueFullMode=" + raw;
        }
    }

    public string Notes => useQueue
        ? "ALog LoggerEngine + FileLogWriter; the file stays open (64 KiB buffer, flush every 1 s); UseBackgroundQueue(capacity=10000, batch 10, 100 ms) per README Step 3; full mode: " + FullMode + "; FlushTimeout 5 s; Close flushes once, then disposes; PlainTextFormatter standard exception format / JsonFormatter"
        : "ALog LoggerEngine + FileLogWriter on the caller thread (no queue); extra row, not ranked (decided 2026-10-22); the file stays open (64 KiB buffer, flush every 1 s); PlainTextFormatter standard exception format / JsonFormatter";

    public bool Ranked => useQueue;

    public string? NotSupported(string scenario) => null;

    public void Init(LibConfig config)
    {
        ALog.Public.Interfaces.ILogFormatter formatter = config.Format == Fmt.Json
            ? new ALog.Formatters.JsonFormatter()
            : new ALog.Formatters.PlainTextFormatter("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var cfg = new ALog.Config.LoggerConfig()
            .AddWriter(new ALog.Writers.File.FileLogWriter(config.Path, formatter))
            .SetMinimumLevel(config.Filtered ? ALog.LogLevel.Warn : ALog.LogLevel.Info)
            .SetFileBufferSize(65536)
            .SetFileFlushInterval(TimeSpan.FromSeconds(1))
            .SetFlushTimeout(TimeSpan.FromSeconds(5));
        if (useQueue)
        {
            cfg.UseBackgroundQueue(true, Libs.QueueCapacity);
            cfg.SetQueueFullMode(ConfiguredMode());
        }

        engine = new ALog.Core.LoggerEngine(cfg);
    }

    public void Simple() => engine!.LogStructured(Texts.Category, ALog.LogLevel.Info, null, Texts.Simple);

    public void Structured(int i) => engine!.LogStructured(Texts.Category, ALog.LogLevel.Info, null, Texts.Template, i, Texts.Ip);

    public void Error(Exception ex) => engine!.LogStructured(Texts.Category, ALog.LogLevel.Error, ex, Texts.Error);

    public void Sentinel() => engine!.LogStructured(Texts.Category, ALog.LogLevel.Warn, null, Texts.Sentinel);

    public void Drain()
    {
        if (!useQueue || engine is null)
            return;
        engine.FlushAsync().GetAwaiter().GetResult();
    }

    public void Close()
    {
        if (engine is null)
            return;
        Drain();
        engine.Dispose();
        engine = null;
    }

    /// <summary>
    /// Mode passed to <see cref="ALog.Config.LoggerConfig.SetQueueFullMode"/>. Block, unless
    /// <c>BENCH_MUTANT_FULL_MODE</c> names something else; any other value builds with Drop so the
    /// recorded <see cref="QueueFullModeRecord"/> cannot be Block.
    /// </summary>
    private ALog.Config.QueueFullMode ConfiguredMode()
    {
        var raw = Environment.GetEnvironmentVariable("BENCH_MUTANT_FULL_MODE");
        if (!string.IsNullOrWhiteSpace(raw) && !string.Equals(raw.Trim(), "Block", StringComparison.OrdinalIgnoreCase))
            return ALog.Config.QueueFullMode.Drop;
        return ALog.Config.QueueFullMode.Block;
    }
}

/// <summary>
/// NLog: FileTarget wrapped in AsyncTargetWrapper with OverflowAction=Block.
/// Performance wiki (https://github.com/NLog/NLog/wiki/Performance, "File Logging Performance"): keepFileOpen=true,
/// AsyncWrapper, overflowAction="Block" for file targets, flush before exit.
/// AsyncWrapper: https://github.com/NLog/NLog/wiki/AsyncWrapper-target (QueueLimit default 10000).
/// File target: https://github.com/NLog/NLog/wiki/File-target. JSON: https://github.com/NLog/NLog/wiki/JsonLayout.
/// </summary>
public sealed class NLogLib : ILib
{
    private NLog.LogFactory? factory;
    private NLog.Logger? log;

    public string Id => "nlog";

    public string Framework => "NLog";

    public string Mode => "async";

    public string Notes => "NLog FileTarget(KeepFileOpen=true, AutoFlush=false) in AsyncTargetWrapper(QueueLimit=10000, OverflowAction=Block); exception via ${exception} with its default format (ToString,Data, https://github.com/NLog/NLog/wiki/Exception-Layout-Renderer); Performance wiki https://github.com/NLog/NLog/wiki/Performance; AsyncWrapper https://github.com/NLog/NLog/wiki/AsyncWrapper-target; JsonLayout https://github.com/NLog/NLog/wiki/JsonLayout";

    public bool Ranked => true;

    public string? NotSupported(string scenario) => null;

    public void Init(LibConfig config)
    {
        // AutoFlush=false: no flush per write batch, same as the other libraries (BEN-03); Close flushes.
        var file = new NLog.Targets.FileTarget("file") { FileName = config.Path, KeepFileOpen = true, AutoFlush = false };
        if (config.Format == Fmt.Json)
        {
            var json = new NLog.Layouts.JsonLayout { IncludeEventProperties = true };
            json.Attributes.Add(new NLog.Layouts.JsonAttribute("timestamp", "${date:universalTime=true:format=o}"));
            json.Attributes.Add(new NLog.Layouts.JsonAttribute("level", "${level}"));
            json.Attributes.Add(new NLog.Layouts.JsonAttribute("category", "${logger}"));
            json.Attributes.Add(new NLog.Layouts.JsonAttribute("message", "${message}"));
            json.Attributes.Add(new NLog.Layouts.JsonAttribute("exception", "${exception}"));
            file.Layout = json;
        }
        else
        {
            // ${exception} without format: NLog's default (ToString,Data), https://github.com/NLog/NLog/wiki/Exception-Layout-Renderer
            file.Layout = "[${date:universalTime=true:format=yyyy-MM-dd'T'HH\\:mm\\:ss.fff'Z'}] [${level}] [${logger}] ${message}${onexception:inner=${newline}${exception}}";
        }

        var async = new NLog.Targets.Wrappers.AsyncTargetWrapper("async", file)
        {
            QueueLimit = Libs.QueueCapacity,
            OverflowAction = NLog.Targets.Wrappers.AsyncTargetWrapperOverflowAction.Block,
        };
        var cfg = new NLog.Config.LoggingConfiguration();
        cfg.AddRule(config.Filtered ? NLog.LogLevel.Warn : NLog.LogLevel.Info, NLog.LogLevel.Fatal, async);
        factory = new NLog.LogFactory { Configuration = cfg };
        log = factory.GetLogger(Texts.Category);
    }

    public void Simple() => log!.Info(Texts.Simple);

    public void Structured(int i) => log!.Info(Texts.Template, i, Texts.Ip);

    public void Error(Exception ex) => log!.Error(ex, Texts.Error);

    public void Sentinel() => log!.Warn(Texts.Sentinel);

    public void Drain() => factory!.Flush(TimeSpan.FromMinutes(10));

    public void Close()
    {
        factory!.Flush(TimeSpan.FromMinutes(10));
        factory.Shutdown();
        factory.Dispose();
        factory = null;
    }
}

/// <summary>
/// Serilog: Serilog.Sinks.Async (blockWhenFull: true, bufferSize default 10000) around Serilog.Sinks.File.
/// Async README: https://github.com/serilog/serilog-sinks-async/blob/dev/README.md ("blockWhenFull").
/// File README, section Performance: https://github.com/serilog/serilog-sinks-file/blob/dev/README.md
/// ("By default, the file sink will flush each event ... specifying buffered: true"). buffered: true is set so no
/// library flushes per event (BEN-03); the deviation from the default is in Notes.
/// JSON: Serilog.Formatting.Json.JsonFormatter from the Serilog package (no extra package).
/// Output template: https://github.com/serilog/serilog/wiki/Formatting-Output.
/// </summary>
public sealed class SerilogLib : ILib
{
    private Serilog.Core.Logger? root;
    private Serilog.ILogger? log;
    private Monitor? monitor;

    public string Id => "serilog";

    public string Framework => "Serilog";

    public string Mode => "async";

    public bool Ranked => true;

    public string Notes => "Serilog WriteTo.Async(bufferSize=10000, blockWhenFull=true) -> File(buffered=true; default false flushes per event, changed so it does not flush every event); README https://github.com/serilog/serilog-sinks-async/blob/dev/README.md and https://github.com/serilog/serilog-sinks-file/blob/dev/README.md#performance; JSON = Serilog.Formatting.Json.JsonFormatter(renderMessage=true); timestamps in JSON are DateTimeOffset local, run with TZ=UTC";

    public string? NotSupported(string scenario) => null;

    public void Init(LibConfig config)
    {
        monitor = new Monitor();
        var lc = new Serilog.LoggerConfiguration();
        lc = config.Filtered ? lc.MinimumLevel.Warning() : lc.MinimumLevel.Information();
        if (config.Format == Fmt.Json)
        {
            var formatter = new Serilog.Formatting.Json.JsonFormatter(renderMessage: true);
            lc = lc.WriteTo.Async(a => a.File(formatter, config.Path, buffered: true), bufferSize: Libs.QueueCapacity, blockWhenFull: true, monitor: monitor);
        }
        else
        {
            // {NewLine}{Exception} as in Serilog's default output template: the exception is rendered with its standard ToString().
            const string template = "[{UtcTimestamp:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}] [{Level}] [{SourceContext:l}] {Message:lj}{NewLine}{Exception}";
            lc = lc.WriteTo.Async(
                a => a.File(config.Path, outputTemplate: template, buffered: true, formatProvider: System.Globalization.CultureInfo.InvariantCulture),
                bufferSize: Libs.QueueCapacity,
                blockWhenFull: true,
                monitor: monitor);
        }

        root = lc.CreateLogger();
        log = root.ForContext(Serilog.Core.Constants.SourceContextPropertyName, Texts.Category);
    }

    public void Simple() => log!.Information(Texts.Simple);

    public void Structured(int i) => log!.Information(Texts.Template, i, Texts.Ip);

    public void Error(Exception ex) => log!.Error(ex, Texts.Error);

    public void Sentinel() => log!.Warning(Texts.Sentinel);

    public void Drain()
    {
        // Serilog has no flush API for the async sink; wait until its buffer is empty (inspector, README "Sink State Inspection").
        var inspector = monitor!.Inspector;
        if (inspector is null)
            return;
        var sw = Stopwatch.StartNew();
        while (inspector.Count > 0 && sw.Elapsed < TimeSpan.FromMinutes(10))
            Thread.Sleep(1);
    }

    public void Close()
    {
        // Disposing the logger completes the async buffer, joins the worker and disposes (flushes) the file sink.
        root!.Dispose();
        root = null;
        log = null;
    }

    private sealed class Monitor : Serilog.Sinks.Async.IAsyncLogEventSinkMonitor
    {
        public Serilog.Sinks.Async.IAsyncLogEventSinkInspector? Inspector { get; private set; }

        public void StartMonitoring(Serilog.Sinks.Async.IAsyncLogEventSinkInspector inspector) => Inspector = inspector;

        public void StopMonitoring(Serilog.Sinks.Async.IAsyncLogEventSinkInspector inspector) => Inspector = null;
    }
}

/// <summary>
/// log4net: no built-in async appender, so BufferingForwardingAppender (lossy=false, Fix=Partial) in front of a
/// FileAppender with ImmediateFlush=false (BEN-04).
/// BufferingForwardingAppender: https://logging.apache.org/log4net/manual/configuration/appenders/bufferingforwardingappender.html
/// ("Partial is the recommended starting point"; default bufferSize 512).
/// FileAppender: https://logging.apache.org/log4net/manual/configuration/appenders/fileappender.html;
/// ImmediateFlush: https://github.com/apache/logging-log4net/blob/master/src/log4net/Appender/TextWriterAppender.cs
/// ("Avoiding the flush operation at the end of each append results in a performance gain").
/// The buffer is a synchronous batch buffer, not a queue: it keeps its documented default size 512 (DC 2026-10-22,
/// question 1). Exceptions use the standard %exception (Exception.ToString()).
/// No named properties: T2 is n/a (DC question 6). ILog.InfoFormat formats with String.Format only
/// (https://github.com/apache/logging-log4net/blob/master/src/log4net/ILog.cs).
/// No built-in JSON layout: T7 is n/a (https://logging.apache.org/log4net/manual/configuration/layouts.html lists
/// JsonLayout only "via the external project log4net.Ext.Json"; BEN-02: no third-party substitute).
/// </summary>
public sealed class Log4NetLib : ILib
{
    private static int repoCounter;
    private log4net.Repository.Hierarchy.Hierarchy? repo;
    private log4net.Appender.BufferingForwardingAppender? buffer;
    private log4net.ILog? log;

    public string Id => "log4net";

    public string Framework => "log4net";

    public string Mode => "buffered";

    public string Notes => "log4net BufferingForwardingAppender(bufferSize=512 default, synchronous batch buffer, not a queue; lossy=false, Fix=Partial) -> FileAppender(ImmediateFlush=false, ExclusiveLock); no built-in async appender; standard %exception; docs https://logging.apache.org/log4net/manual/configuration/appenders/bufferingforwardingappender.html";

    public bool Ranked => true;

    public string? NotSupported(string scenario) => scenario switch
    {
        "T2" => "log4net has no message templates or named properties; ILog.InfoFormat only formats with String.Format (https://github.com/apache/logging-log4net/blob/master/src/log4net/ILog.cs, checked 2026-10-22, log4net 3.5.0); n/a, decided 2026-10-22",
        "T7" => "log4net has no built-in JSON layout; JsonLayout only via the external project log4net.Ext.Json (https://logging.apache.org/log4net/manual/configuration/layouts.html, checked 2026-10-22, log4net 3.5.0); no third-party substitutes",
        _ => null,
    };

    public void Init(LibConfig config)
    {
        var name = "bench-" + Environment.ProcessId + "-" + Interlocked.Increment(ref repoCounter);
        repo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.CreateRepository(name);
        var layout = new log4net.Layout.PatternLayout("[%utcdate{yyyy-MM-dd'T'HH:mm:ss.fff'Z'}] [%level] [%logger] %message%newline%exception");
        layout.ActivateOptions();
        var file = new log4net.Appender.FileAppender
        {
            File = config.Path,
            AppendToFile = false,
            ImmediateFlush = false,
            Layout = layout,
            LockingModel = new log4net.Appender.FileAppender.ExclusiveLock(),
            Encoding = new System.Text.UTF8Encoding(false),
        };
        file.ActivateOptions();
        buffer = new log4net.Appender.BufferingForwardingAppender { Lossy = false, Fix = log4net.Core.FixFlags.Partial };
        buffer.AddAppender(file);
        buffer.ActivateOptions();
        repo.Root.AddAppender(buffer);
        repo.Root.Level = config.Filtered ? log4net.Core.Level.Warn : log4net.Core.Level.Info;
        repo.Configured = true;
        log = log4net.LogManager.GetLogger(name, Texts.Category);
    }

    public void Simple() => log!.Info(Texts.Simple);

    public void Structured(int i) => log!.InfoFormat(System.Globalization.CultureInfo.InvariantCulture, Texts.PositionalTemplate, i, Texts.Ip);

    public void Error(Exception ex) => log!.Error(Texts.Error, ex);

    public void Sentinel() => log!.Warn(Texts.Sentinel);

    public void Drain() => buffer!.Flush(true);

    public void Close()
    {
        buffer!.Flush(true);
        repo!.Shutdown();
        repo = null;
        buffer = null;
        log = null;
    }
}
