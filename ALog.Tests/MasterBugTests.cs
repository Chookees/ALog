using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using ALog.Formatters;
using ALog.Public.Interfaces;
using ALog.Writers.Console;
using ALog.Writers.File;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// M1 on master: a scalar where <c>ALog:Console</c> or <c>ALog:File</c> is bound
/// stops startup, plain text still writes when a property's <c>ToString</c> throws,
/// and <c>Console:Enabled</c> is not applied again when configuration reloads.
/// <c>ALog:MinimumLevel</c> is characterized, not reloaded: that assertion flips when the level-reload item lands.
/// </summary>
public sealed class MasterBugTests : IDisposable
{
    private const string CapturePlaceholder = "[capture failed: InvalidOperationException]";

    private const string WriterSentence =
        "Writer settings (Enabled, paths) are read at startup; only levels reload at runtime.";

    private const string StartupSentence =
        "ALog reads its settings, including MinimumLevel, once at startup. Changes while the app runs have no effect; restart the app.";

    private const string LevelsReloadPhrase = "only levels reload at runtime";

    private const string SkipSentence =
        "To see why a test was skipped, run `dotnet test --logger \"console;verbosity=normal\"`.";

    private const string Remarks =
        "Read once at startup. Changes while the app runs have no effect; restart the app.";

    private const string BreakingLine =
        "- Breaking (behavior): a single value or a list where ALog expects a section (for example `\"ALog\": { \"Console\": true }`) now stops startup with an error that names the path. Before, ALog ignored it and wrote nothing.";

    private const string FixedLine =
        "- Fixed: `ConsoleLogWriter` and `FileLogWriter` write the line even when the `ToString()` of a property or scope value throws. That value shows `[capture failed: ExceptionType]`.";

    private const string AwsBreakingLine =
        "- Breaking (dependency): `AWSSDK.CloudWatchLogs` moves from 3.7.509.3 (AWS SDK for .NET V3, end of support since 2026-06-01) to 4.0.106 (V4). If your app references other `AWSSDK.*` packages, upgrade all of them to V4 together. Mixing V3 and V4 either stops restore with NU1107 (version conflict on `AWSSDK.Core`) or restores with warning NU1608 and then throws `MissingMethodException` at runtime.";

    private const string AwsFixedLine =
        "- Fixed: `AwsCloudWatchWriter` now sends the message-template properties and the context with each event, not only the rendered message.";

    private const string AwsSdkParagraph =
        "`AwsCloudWatchWriter` uses the AWS SDK for .NET V4 (`AWSSDK.CloudWatchLogs` 4.0.106). If your app references other `AWSSDK.*` packages, use their V4 versions too. Mixing V3 and V4 either stops restore with NU1107 (version conflict on `AWSSDK.Core`) or restores with warning NU1608 and then throws `MissingMethodException` at runtime.";

    private const string ConsoleSingleValueMessage =
        "Invalid ALog configuration at 'ALog:Console': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";

    private const string FileSingleValueMessage =
        "Invalid ALog configuration at 'ALog:File': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";

    private const string FileListMessage =
        "Invalid ALog configuration at 'ALog:File': expected a section with settings, but found a list. Put the settings inside the section, as shown in the README.";

    private const string ConsoleListMessage =
        "Invalid ALog configuration at 'ALog:Console': expected a section with settings, but found a list. Put the settings inside the section, as shown in the README.";

    private const string ConsoleValueAndFileListMessage =
        "Invalid ALog configuration at 'ALog:Console' (single value) and 'ALog:File' (list): expected a section with settings at each path. Put the settings inside each section, as shown in the README.";

    private const string BothSingleValuesMessage =
        "Invalid ALog configuration at 'ALog:Console' (single value) and 'ALog:File' (single value): expected a section with settings at each path. Put the settings inside each section, as shown in the README.";

    /// <summary>Drops a static logger left by another test so this class starts clean.</summary>
    public MasterBugTests() => Log.Shutdown();

    /// <summary>Drops the static logger after the test, including when an assert fails.</summary>
    public void Dispose() => Log.Shutdown();

    /// <summary>
    /// <c>"ALog": { "Console": true }</c> stops startup from every
    /// <c>AddALog(IConfiguration)</c> overload. The message names <c>ALog:Console</c>
    /// and does not repeat the value. <c>Log.Init(ALogOptions)</c> still starts.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ScalarConsoleSection_StopsStartup()
    {
        await Task.CompletedTask;
        var init = Record.Exception(() =>
        {
            Log.Init(new ALogOptions());
            Log.Shutdown();
        });
        Assert.Null(init);

        const string json = """{ "ALog": { "Console": true } }""";
        RequireScalarRejected(json, "ALog:Console", "True");
    }

    /// <summary>
    /// A string where <c>ALog:File</c> is bound stops startup with the same sentence
    /// and the path <c>ALog:File</c>. The connection string is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ScalarFileSection_StopsStartup()
    {
        await Task.CompletedTask;
        const string json = """{ "ALog": { "File": "Server=secret-db;Password=hunter2" } }""";
        RequireScalarRejected(json, "ALog:File", "hunter2");
    }

    /// <summary>
    /// A section that has a value and a child is malformed.
    /// <c>AddALog</c> and <c>GetALogOptions</c> throw the single-path sentence.
    /// The value <c>true</c> is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConsoleValueAndChild_StopsStartup()
    {
        await Task.CompletedTask;
        var values = new Dictionary<string, string?>
        {
            ["ALog:Console"] = "true",
            ["ALog:Console:Enabled"] = "true"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        RequireConfigEntries(configuration, ExpectedScalarMessage("ALog:Console"), "true");
    }

    /// <summary>
    /// <c>GetALogOptions</c> rejects <c>"Console": true</c> with the same sentence as <c>AddALog</c>.
    /// <c>Log.Init(ALogOptions)</c> is not given configuration and still starts.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GetALogOptions_ScalarConsole_StopsStartup()
    {
        await Task.CompletedTask;
        var init = Record.Exception(() =>
        {
            Log.Init(new ALogOptions());
            Log.Shutdown();
        });
        Assert.Null(init);

        var configuration = LoadJson("""{ "ALog": { "Console": true } }""");
        var error = Record.Exception(() => configuration.GetALogOptions());
        var actual = error is null ? "(no exception from GetALogOptions)" : error.Message;
        Assert.Equal(ConsoleSingleValueMessage, actual);
        Assert.DoesNotContain("True", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>AddALog(ILoggingBuilder, IConfiguration)</c> stops on <c>"Console": true</c>
    /// with the single-value sentence. The value is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddALog_ConsoleSingleValue_StopsStartup()
    {
        await Task.CompletedTask;
        var actual = CallAddALog(LoadJson("""{ "ALog": { "Console": true } }"""));
        Assert.Equal(ConsoleSingleValueMessage, actual);
        Assert.DoesNotContain("True", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>AddALog(ILoggingBuilder, IConfiguration)</c> stops on a string at <c>ALog:File</c>
    /// with the single-value sentence. The connection string is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddALog_FileSingleValue_StopsStartup()
    {
        await Task.CompletedTask;
        var actual = CallAddALog(LoadJson(
            """{ "ALog": { "File": "Server=secret-db;Password=hunter2" } }"""));
        Assert.Equal(FileSingleValueMessage, actual);
        Assert.DoesNotContain("hunter2", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>GetALogOptions</c> stops on <c>"Console": true</c> with the same single-value sentence as <c>AddALog</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GetALogOptions_ConsoleSingleValue_StopsStartup()
    {
        await Task.CompletedTask;
        var actual = CallGetALogOptions(LoadJson("""{ "ALog": { "Console": true } }"""));
        Assert.Equal(ConsoleSingleValueMessage, actual);
        Assert.DoesNotContain("True", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>GetALogOptions</c> stops on a string at <c>ALog:File</c> with the single-value sentence.
    /// The connection string is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GetALogOptions_FileSingleValue_StopsStartup()
    {
        await Task.CompletedTask;
        var actual = CallGetALogOptions(LoadJson(
            """{ "ALog": { "File": "Server=secret-db;Password=hunter2" } }"""));
        Assert.Equal(FileSingleValueMessage, actual);
        Assert.DoesNotContain("hunter2", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// A JSON array at <c>ALog:File</c> is a list. Both config entries stop with the list sentence.
    /// The element text is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FileList_StopsStartup()
    {
        await Task.CompletedTask;
        var configuration = LoadJson("""{ "ALog": { "File": [true] } }""");
        Assert.Equal(FileListMessage, CallAddALog(configuration));
        Assert.Equal(FileListMessage, CallGetALogOptions(configuration));
        Assert.DoesNotContain("True", CallAddALog(configuration), StringComparison.Ordinal);
    }

    /// <summary>
    /// Console is a single value and File is a list. One exception names Console first,
    /// with each kind in parentheses.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConsoleSingleValueAndFileList_StopsStartupOnce()
    {
        await Task.CompletedTask;
        var configuration = LoadJson("""{ "ALog": { "Console": true, "File": [true] } }""");
        Assert.Equal(ConsoleValueAndFileListMessage, CallAddALog(configuration));
        Assert.Equal(ConsoleValueAndFileListMessage, CallGetALogOptions(configuration));
        Assert.DoesNotContain("True", CallAddALog(configuration), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lowercase keys still name the canonical path <c>ALog:Console</c>.
    /// The configuration spelling is not copied into the message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LowercaseConsoleKey_ReportsCanonicalPath()
    {
        await Task.CompletedTask;
        var configuration = LoadJson("""{ "alog": { "console": true } }""");
        var actual = CallAddALog(configuration);
        Assert.Equal(ConsoleSingleValueMessage, actual);
        Assert.Equal(ConsoleSingleValueMessage, CallGetALogOptions(configuration));
        Assert.DoesNotContain("alog:console", actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty string is a value. Both config entries stop with the single-path sentence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EmptyConsole_StopsStartup()
    {
        await Task.CompletedTask;
        RequireConfigEntries(
            LoadJson("""{ "ALog": { "Console": "" } }"""),
            ExpectedScalarMessage("ALog:Console"));
    }

    /// <summary>
    /// Whitespace is a value. The spaces are not copied into the sentence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhitespaceConsole_StopsStartup()
    {
        await Task.CompletedTask;
        RequireConfigEntries(
            LoadJson("""{ "ALog": { "Console": "   " } }"""),
            ExpectedScalarMessage("ALog:Console"),
            "   ");
    }

    /// <summary>
    /// A JSON array is only numeric children. Both entries stop with the list sentence. The element text is not in the sentence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ArrayConsole_StopsStartup()
    {
        await Task.CompletedTask;
        RequireConfigEntries(
            LoadJson("""{ "ALog": { "Console": [true] } }"""),
            ConsoleListMessage,
            "True");
    }

    /// <summary>
    /// A JSON object plus <c>ALog__Console=true</c> has a value and a child.
    /// Both entries stop. The env value is not in the sentence.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EnvScalarOverObject_StopsStartup()
    {
        await Task.CompletedTask;
        const string name = "ALog__Console";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "true");
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
                """{ "ALog": { "Console": { "Enabled": false } } }"""));
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(stream)
                .AddEnvironmentVariables()
                .Build();
            RequireConfigEntries(configuration, ExpectedScalarMessage("ALog:Console"), "true");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    /// <summary>
    /// Console and File are both single values. One exception names Console before File,
    /// with each kind in parentheses.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConsoleAndFileScalars_StopStartupOnce()
    {
        await Task.CompletedTask;
        var configuration = LoadJson(
            """{ "ALog": { "Console": true, "File": "Server=secret-db;Password=hunter2" } }""");
        RequireConfigEntries(
            configuration,
            BothSingleValuesMessage,
            "True",
            "hunter2");
    }

    /// <summary>
    /// A real object section is not malformed. Neither config entry throws.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ObjectSection_DoesNotStopStartup()
    {
        await Task.CompletedTask;
        var configuration = LoadJson("""{ "ALog": { "Console": { "Enabled": true } } }""");
        var add = Record.Exception(() => InvokeEveryOverload(configuration));
        var got = Record.Exception(() => configuration.GetALogOptions());
        Assert.Null(add);
        Assert.Null(got);
        Assert.True(configuration.GetALogOptions().Console!.Enabled);
    }

    /// <summary>
    /// Facade and MEL both keep the plain-text console line and the file line when
    /// one property's <c>ToString</c> throws. That property's text is
    /// <c>[capture failed: InvalidOperationException]</c>. The other property and a
    /// third writer still see the original object. The caller sees no exception,
    /// stderr has no sync-write failure, and no ALOG id is added.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThrowingPropertyToString_StillWritesTheLine()
    {
        await Task.CompletedTask;
        var problems = new StringBuilder();
        CollectFacade(problems);
        CollectMel(problems);
        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>
    /// A scope value whose <c>ToString</c> throws still leaves a console line when the writer has no formatter.
    /// <see cref="Log.BeginScope"/> and <see cref="LogContext.Push"/> both keep the placeholder.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThrowingScopeToString_ConsoleStillWrites()
    {
        await Task.CompletedTask;
        var problems = new StringBuilder();
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousSelf = SelfLog.Target;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            SelfLog.Enable(line =>
            {
                if (self.Count < 8)
                    self.Add(line);
            });
            Log.Init(new LoggerConfig()
                .AddWriter(new ConsoleLogWriter(useColors: false))
                .AddWriter(probe)
                .UseAsync(false)
                .SetMinimumLevel(LogLevel.Trace));
            var caller = Record.Exception(() =>
            {
                using (Log.BeginScope("FromBegin", new ThrowingProperty()))
                    Log.Write(LogLevel.Info, "begin-scope-line");
                using (LogContext.Push([new KeyValuePair<string, object?>("FromContext", new ThrowingProperty())]))
                    Log.Write(LogLevel.Info, "context-scope-line");
            });
            var text = stdout.ToString();
            Require(problems, caller is null, "console scope threw " + (caller is null ? "" : caller.Message));
            Require(problems, text.Contains("begin-scope-line", StringComparison.Ordinal), "console missing begin line: " + Trim(text));
            Require(problems, text.Contains("context-scope-line", StringComparison.Ordinal), "console missing context line: " + Trim(text));
            Require(problems, text.Contains("FromBegin=" + CapturePlaceholder, StringComparison.Ordinal), "console missing begin placeholder: " + Trim(text));
            Require(problems, text.Contains("FromContext=" + CapturePlaceholder, StringComparison.Ordinal), "console missing context placeholder: " + Trim(text));
            Require(problems, !stderr.ToString().Contains("Sync write failed", StringComparison.Ordinal), "console stderr: " + Trim(stderr.ToString()));
            Require(problems, self.Count == 0, "console scope SelfLog " + self.Count + " [" + Joined(self) + "]");
            Require(problems, probe.Events.Count == 2, "console probe " + probe.Events.Count);
        }
        finally
        {
            Log.Shutdown();
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            SelfLog.Target = previousSelf;
        }

        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>
    /// A MEL scope value whose <c>ToString</c> throws still leaves a file line when the writer has no formatter.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThrowingScopeToString_FileStillWrites()
    {
        await Task.CompletedTask;
        var problems = new StringBuilder();
        var directory = NewDirectory();
        var path = Path.Combine(directory, "scope.log");
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stderr = new StringWriter();
        var previousErr = Console.Error;
        var previousSelf = SelfLog.Target;
        try
        {
            Console.SetError(stderr);
            SelfLog.Enable(line =>
            {
                if (self.Count < 8)
                    self.Add(line);
            });
            var config = new LoggerConfig()
                .AddWriter(new FileLogWriter(path))
                .AddWriter(probe)
                .UseAsync(false)
                .SetMinimumLevel(LogLevel.Trace);
            using (var provider = new ALogLoggerProvider(config))
            {
                var logger = provider.CreateLogger("M1");
                var caller = Record.Exception(() =>
                {
                    using (logger.BeginScope(new Dictionary<string, object?> { ["FromMel"] = new ThrowingProperty() }))
                        logger.LogInformation("mel-scope-line");
                });
                Require(problems, caller is null, "file scope threw " + (caller is null ? "" : caller.Message));
            }

            var fileText = ReadFileOrEmpty(path);
            Require(problems, fileText.Contains("mel-scope-line", StringComparison.Ordinal), "file missing line: " + Trim(fileText));
            Require(problems, fileText.Contains("FromMel=" + CapturePlaceholder, StringComparison.Ordinal), "file missing placeholder: " + Trim(fileText));
            Require(problems, !stderr.ToString().Contains("Write failed", StringComparison.Ordinal), "file stderr: " + Trim(stderr.ToString()));
            Require(problems, !stderr.ToString().Contains("Sync write failed", StringComparison.Ordinal), "file sync stderr: " + Trim(stderr.ToString()));
            Require(problems, self.Count == 0, "file scope SelfLog " + self.Count + " [" + Joined(self) + "]");
            Require(problems, probe.Events.Count == 1, "file probe " + probe.Events.Count);
            if (probe.Events.Count == 1)
            {
                var context = probe.Events[0].Context;
                Require(problems, context is not null && context["FromMel"] is ThrowingProperty, "file probe lost the scope value");
            }
        }
        finally
        {
            Console.SetError(previousErr);
            SelfLog.Target = previousSelf;
            DeleteDirectory(directory);
        }

        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>
    /// A scope or context value whose <c>ToString</c> throws still leaves a console line
    /// when the writer uses <see cref="PlainTextFormatter"/>. The placeholder is on the line.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThrowingScopeToString_ConsoleWithFormatter_StillWrites()
    {
        await Task.CompletedTask;
        var problems = new StringBuilder();
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousSelf = SelfLog.Target;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            SelfLog.Enable(line =>
            {
                if (self.Count < 8)
                    self.Add(line);
            });
            Log.Init(new LoggerConfig()
                .AddWriter(new ConsoleLogWriter(useColors: false, formatter: new PlainTextFormatter()))
                .AddWriter(probe)
                .UseAsync(false)
                .SetMinimumLevel(LogLevel.Trace));
            var caller = Record.Exception(() =>
            {
                using (Log.BeginScope("FromBegin", new ThrowingProperty()))
                    Log.Write(LogLevel.Info, "begin-scope-formatted");
                using (LogContext.Push([new KeyValuePair<string, object?>("FromContext", new ThrowingProperty())]))
                    Log.Write(LogLevel.Info, "context-scope-formatted");
            });
            var text = stdout.ToString();
            Require(problems, caller is null, "formatted console scope threw " + (caller is null ? "" : caller.Message));
            Require(problems, text.Contains("begin-scope-formatted", StringComparison.Ordinal), "formatted console missing begin line: " + Trim(text));
            Require(problems, text.Contains("context-scope-formatted", StringComparison.Ordinal), "formatted console missing context line: " + Trim(text));
            Require(problems, text.Contains("FromBegin=" + CapturePlaceholder, StringComparison.Ordinal), "formatted console missing begin placeholder: " + Trim(text));
            Require(problems, text.Contains("FromContext=" + CapturePlaceholder, StringComparison.Ordinal), "formatted console missing context placeholder: " + Trim(text));
            Require(problems, !stderr.ToString().Contains("Sync write failed", StringComparison.Ordinal), "formatted console stderr: " + Trim(stderr.ToString()));
            Require(problems, self.Count == 0, "formatted console SelfLog " + self.Count + " [" + Joined(self) + "]");
            Require(problems, probe.Events.Count == 2, "formatted console probe " + probe.Events.Count);
        }
        finally
        {
            Log.Shutdown();
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            SelfLog.Target = previousSelf;
        }

        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>
    /// A MEL scope value whose <c>ToString</c> throws still leaves a file line
    /// when the writer uses <see cref="PlainTextFormatter"/>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThrowingScopeToString_FileWithFormatter_StillWrites()
    {
        await Task.CompletedTask;
        var problems = new StringBuilder();
        var directory = NewDirectory();
        var path = Path.Combine(directory, "scope-formatted.log");
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stderr = new StringWriter();
        var previousErr = Console.Error;
        var previousSelf = SelfLog.Target;
        try
        {
            Console.SetError(stderr);
            SelfLog.Enable(line =>
            {
                if (self.Count < 8)
                    self.Add(line);
            });
            var config = new LoggerConfig()
                .AddWriter(new FileLogWriter(path, new PlainTextFormatter()))
                .AddWriter(probe)
                .UseAsync(false)
                .SetMinimumLevel(LogLevel.Trace);
            using (var provider = new ALogLoggerProvider(config))
            {
                var logger = provider.CreateLogger("M1");
                var caller = Record.Exception(() =>
                {
                    using (logger.BeginScope(new Dictionary<string, object?> { ["FromMel"] = new ThrowingProperty() }))
                        logger.LogInformation("mel-scope-formatted");
                });
                Require(problems, caller is null, "formatted file scope threw " + (caller is null ? "" : caller.Message));
            }

            var fileText = ReadFileOrEmpty(path);
            Require(problems, fileText.Contains("mel-scope-formatted", StringComparison.Ordinal), "formatted file missing line: " + Trim(fileText));
            Require(problems, fileText.Contains("FromMel=" + CapturePlaceholder, StringComparison.Ordinal), "formatted file missing placeholder: " + Trim(fileText));
            Require(problems, !stderr.ToString().Contains("Write failed", StringComparison.Ordinal), "formatted file stderr: " + Trim(stderr.ToString()));
            Require(problems, !stderr.ToString().Contains("Sync write failed", StringComparison.Ordinal), "formatted file sync stderr: " + Trim(stderr.ToString()));
            Require(problems, self.Count == 0, "formatted file SelfLog " + self.Count + " [" + Joined(self) + "]");
            Require(problems, probe.Events.Count == 1, "formatted file probe " + probe.Events.Count);
        }
        finally
        {
            Console.SetError(previousErr);
            SelfLog.Target = previousSelf;
            DeleteDirectory(directory);
        }

        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>
    /// <c>AddALog(Action&lt;ALogOptions&gt;)</c> takes options, not a configuration section.
    /// A console options object is not a scalar section and does not throw.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AddALog_ActionOptions_DoesNotThrow()
    {
        await Task.CompletedTask;
        var error = Record.Exception(() =>
        {
            using var provider = new ServiceCollection().AddLogging(logging =>
            {
                logging.AddALog(options =>
                {
                    options.Console = new ConsoleWriterOptions { Enabled = true };
                });
            }).BuildServiceProvider();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// <c>Log.Init(ALogOptions)</c> takes options, not a configuration section.
    /// A console options object is not a scalar section and does not throw.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LogInit_Options_DoesNotThrow()
    {
        await Task.CompletedTask;
        var error = Record.Exception(() =>
        {
            Log.Init(new ALogOptions
            {
                Console = new ConsoleWriterOptions { Enabled = true }
            });
            Log.Shutdown();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// One plain-text line carries both the template render marker and the capture marker.
    /// SelfLog keeps the existing argument failure and does not add a capture line.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task RenderFailureAndCaptureFailure_AreBothOnTheLine()
    {
        await Task.CompletedTask;
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stdout = new StringWriter();
        var previousOut = Console.Out;
        var previousSelf = SelfLog.Target;
        try
        {
            Console.SetOut(stdout);
            SelfLog.Enable(line =>
            {
                if (self.Count < 8)
                    self.Add(line);
            });
            Log.Init(new LoggerConfig()
                .AddWriter(new ConsoleLogWriter(useColors: false))
                .AddWriter(probe)
                .SetFormatter(new PlainTextFormatter())
                .UseAsync(false)
                .SetMinimumLevel(LogLevel.Trace));
            var caller = Record.Exception(() => Log.Write(LogLevel.Info, "Value {A}", new ThrowingProperty()));
            Assert.Null(caller);
            var line = stdout.ToString();
            Assert.Contains(CapturePlaceholder, line, StringComparison.Ordinal);
            var formatAt = line.IndexOf("{A:<format error>}", StringComparison.Ordinal);
            var captureAt = line.IndexOf(CapturePlaceholder, StringComparison.Ordinal);
            Assert.True(formatAt >= 0 && captureAt > formatAt, line);
            var selfLine = Assert.Single(self);
            Assert.Contains("Message template argument 'A' failed", selfLine, StringComparison.Ordinal);
            Assert.DoesNotContain("capture failed", Joined(self), StringComparison.Ordinal);
        }
        finally
        {
            Log.Shutdown();
            Console.SetOut(previousOut);
            SelfLog.Target = previousSelf;
        }
    }

    /// <summary>
    /// Changing <c>ALog:Console:Enabled</c> from false to true on the configuration root
    /// does not start the console writer and does not throw.
    /// The test checks that the new value is visible after <see cref="IConfigurationRoot.Reload"/>
    /// before it checks the log line. Writer settings are read only when <c>AddALog</c> runs.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Reload_ConsoleEnabledFalseToTrue_WritesNothingAndDoesNotThrow()
    {
        await Task.CompletedTask;
        var values = new Dictionary<string, string?>
        {
            ["ALog:Console:Enabled"] = "false",
            ["ALog:MinimumLevel"] = "Trace"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        const string marker = "m1-reload-marker";
        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        Exception? error = null;
        try
        {
            using var provider = new ServiceCollection().AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(MelLogLevel.Trace);
                logging.AddALog(configuration);
            }).BuildServiceProvider();

            Assert.Equal("false", configuration["ALog:Console:Enabled"]);
            configuration["ALog:Console:Enabled"] = "true";
            configuration.Reload();
            Assert.Equal("true", configuration["ALog:Console:Enabled"]);
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("M1");
            error = Record.Exception(() => logger.LogInformation("reload {Marker}", marker));
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Null(error);
        Assert.DoesNotContain(marker, buffer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Characterization, not an M1 fix. Console is on and the Microsoft minimum is Trace.
    /// ALog starts at Warning. The test writes Debug through the configuration root, waits on the
    /// change token with a 30 second guard, and checks that Debug is visible before it logs.
    /// The Debug line is still not written. This assertion flips when the level-reload item lands.
    /// </summary>
    [Fact(Timeout = 45000)]
    public async Task Reload_MinimumLevelWarningToDebug_IsNotReloadedToday()
    {
        var values = new Dictionary<string, string?>
        {
            ["ALog:Console:Enabled"] = "true",
            ["ALog:MinimumLevel"] = "Warning"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        const string debugMarker = "m1-level-debug-not-reloaded";
        const string warningMarker = "m1-level-warning-still-written";
        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var provider = new ServiceCollection().AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(MelLogLevel.Trace);
                logging.AddALog(configuration);
            }).BuildServiceProvider();

            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = configuration.GetSection("ALog:MinimumLevel").GetReloadToken();
            using (token.RegisterChangeCallback(static state =>
            {
                ((TaskCompletionSource)state!).TrySetResult();
            }, fired))
            {
                Assert.Equal("Warning", configuration["ALog:MinimumLevel"]);
                configuration["ALog:MinimumLevel"] = "Debug";
                configuration.Reload();
                var finished = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.True(ReferenceEquals(finished, fired.Task), "ALog:MinimumLevel change token did not fire within 30 seconds.");
                Assert.Equal("Debug", configuration["ALog:MinimumLevel"]);
            }

            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("M1.Level");
            logger.LogDebug("probe {Marker}", debugMarker);
            logger.LogWarning("probe {Marker}", warningMarker);
        }
        finally
        {
            Console.SetOut(previous);
        }

        var text = buffer.ToString();
        Assert.DoesNotContain(debugMarker, text, StringComparison.Ordinal);
        Assert.Contains(warningMarker, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Thirty-three numeric children plus <c>Enabled</c> is a section of settings.
    /// Numeric keys are ordered first, so a cap of 32 would report this as a list.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ThirtyThreeIndexesPlusEnabled_IsASection()
    {
        await Task.CompletedTask;
        var values = new Dictionary<string, string?>(40);
        for (var i = 0; i < 33; i++)
            values.Add("ALog:Console:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "x");

        values.Add("ALog:Console:Enabled", "true");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Equal("(no exception from AddALog)", CallAddALog(configuration));
        Assert.Equal("(no exception from GetALogOptions)", CallGetALogOptions(configuration));
        Assert.True(configuration.GetALogOptions().Console!.Enabled);
    }

    /// <summary>
    /// Plain text renders every property. Two thousand properties are all on the line.
    /// The loop is bounded by the property collection.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PlainText_RendersTwoThousandProperties()
    {
        await Task.CompletedTask;
        const int count = 2000;
        var properties = new Dictionary<string, object>(count);
        for (var i = 0; i < count; i++)
            properties.Add("p" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture), "v");

        var text = new PlainTextFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "bound",
            null,
            null,
            properties));

        for (var i = 0; i < count; i++)
        {
            var token = "p" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "=v";
            Assert.Contains(token, text, StringComparison.Ordinal);
        }

        var nullText = new PlainTextFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "nulls",
            null,
            null,
            new Dictionary<string, object> { ["N"] = null! }));
        Assert.Contains("N=", nullText, StringComparison.Ordinal);
        Assert.DoesNotContain(CapturePlaceholder, nullText, StringComparison.Ordinal);

        var emptyToString = new PlainTextFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "empty",
            null,
            null,
            new Dictionary<string, object> { ["E"] = new NullToString() }));
        Assert.Contains("E=", emptyToString, StringComparison.Ordinal);
        Assert.DoesNotContain(CapturePlaceholder, emptyToString, StringComparison.Ordinal);
    }

    /// <summary>
    /// ALog renders an IFormattable value with ToString(null, CultureInfo.CurrentCulture). For BCL values this matches master byte for byte.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PlainText_IFormattable_UsesCurrentCulture()
    {
        await Task.CompletedTask;
        var value = new DistinctFormattable();
        var expected = value.ToString(null, System.Globalization.CultureInfo.CurrentCulture);
        Assert.NotEqual(value.ToString(), expected);

        var text = new PlainTextFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "form",
            null,
            null,
            new Dictionary<string, object> { ["P"] = value }));

        Assert.Contains("P=" + expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("P=" + value.ToString(), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A throwing <see cref="IFormattable.ToString(string?, IFormatProvider?)"/> shows the capture placeholder.
    /// Plain <c>ToString()</c> is not used as a fallback.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PlainText_ThrowingIFormattable_ShowsCapturePlaceholder()
    {
        await Task.CompletedTask;
        var value = new ThrowingFormattable();
        var text = new PlainTextFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "form",
            null,
            null,
            new Dictionary<string, object> { ["P"] = value }));

        Assert.Contains("P=" + CapturePlaceholder, text, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-tostring", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Locks the M1 sentences: writer remarks, install lines without a version flag,
    /// the M1 changelog bullets, the AWS V4 changelog and README sentences, the skip hint,
    /// and the startup sentence under the JSON example.
    /// <c>only levels reload at runtime</c> is absent.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Docs_LockMasterBugTexts()
    {
        await Task.CompletedTask;
        var readme = ReadRepoFile("README.md");
        var packageReadme = ReadRepoFile("ALog.Extensions.Logging/README.md");
        var aspnet = Slice(readme, "### ASP.NET Core", "## Console output");
        var contributing = Slice(readme, "## Contributing", "## License");

        var jsonAt = aspnet.IndexOf("{ \"ALog\": { \"Console\": { \"Enabled\": true } } }", StringComparison.Ordinal);
        var startupAt = aspnet.IndexOf(StartupSentence, StringComparison.Ordinal);
        Assert.True(jsonAt >= 0 && startupAt > jsonAt, "Startup sentence must sit under the JSON example.");
        Assert.Equal(1, CountOf(readme, StartupSentence));
        Assert.Equal(0, CountOf(readme, WriterSentence));
        Assert.DoesNotContain(LevelsReloadPhrase, readme, StringComparison.Ordinal);
        Assert.Contains(SkipSentence, contributing, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(readme, SkipSentence));
        Assert.Contains("dotnet add package ALog", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet add package ALog.Extensions.Logging", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--version", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--version", packageReadme, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(readme, AwsBreakingLine));
        Assert.Equal(1, CountOf(readme, AwsSdkParagraph));
        var shipping = Slice(readme, "### Step 4: Shipping Writers", "## Limitations");
        Assert.Contains(AwsSdkParagraph, shipping, StringComparison.Ordinal);
        var migrating = Slice(readme, "### AWS SDK V4", "## Contributing");
        Assert.Contains(AwsBreakingLine, migrating, StringComparison.Ordinal);

        var notes = ReadRepoFile("ALog/Config/ALogOptions.cs");
        Assert.Equal(2, CountOf(notes, "<remarks>" + Remarks + "</remarks>"));

        var changelog = Slice(ReadRepoFile("CHANGELOG.md"), "## 0.2.1\n", "\n## 0.2.0");
        var lines = NonEmptyLines(changelog);
        Assert.True(lines.Count >= 3, "0.2.1 section is too short.");
        Assert.StartsWith("- Breaking (dependency): `Microsoft.Data.SqlClient`", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("- Fixed: with the background queue,", lines[lines.Count - 5], StringComparison.Ordinal);
        Assert.Equal(BreakingLine, lines[lines.Count - 4]);
        Assert.Equal(FixedLine, lines[lines.Count - 3]);
        Assert.Equal(AwsBreakingLine, lines[lines.Count - 2]);
        Assert.Equal(AwsFixedLine, lines[lines.Count - 1]);

        var project = ReadRepoFile("ALog/ALog.csproj");
        Assert.Contains("GHSA-846f-f764-76mx", project, StringComparison.Ordinal);
        Assert.Contains("GHSA-2fmv-rv2c-59x7", project, StringComparison.Ordinal);
        var projectLines = NonEmptyLines(project);
        for (var i = 0; i < projectLines.Count; i++)
        {
            if (!projectLines[i].Contains("<!--", StringComparison.Ordinal))
                continue;

            Assert.False(HasSqlClientVersion(projectLines[i]), projectLines[i]);
        }
    }

    /// <summary>
    /// Calls every <c>AddALog</c> overload that takes <see cref="IConfiguration"/>
    /// and requires the scalar message. A missing exception fails with the output,
    /// which is empty for the triage JSON on master before the fix.
    /// </summary>
    /// <param name="json">One JSON document. Not null.</param>
    /// <param name="path">Section path expected in the message, such as <c>ALog:Console</c>.</param>
    /// <param name="secret">Text from the scalar that must not appear in the message.</param>
    private static void RequireScalarRejected(string json, string path, string secret)
    {
        var configuration = LoadJson(json);
        var methods = ConfigurationOverloads();
        Assert.NotEmpty(methods);
        Assert.True(methods.Length <= 4, "Refusing to call more than 4 AddALog overloads.");

        for (var i = 0; i < methods.Length; i++)
        {
            var output = string.Empty;
            var method = methods[i];
            var error = Record.Exception(() => output = InvokeAndLog(method, configuration));
            var actual = error is null
                ? "(no exception from " + Signature(method) + "). Output: [" + Trim(output) + "]"
                : error.Message;
            Assert.Equal(ExpectedScalarMessage(path), actual);
            Assert.DoesNotContain(secret, actual, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Builds a host logger from one overload and writes one information line.
    /// The returned text is what the console received. An invalid section throws
    /// before that line.
    /// </summary>
    /// <param name="method">One <c>AddALog</c> overload. Not null.</param>
    /// <param name="configuration">Configuration passed to that overload. Not null.</param>
    /// <returns>Console text captured while the logger was created and used.</returns>
    private static string InvokeAndLog(MethodInfo method, IConfiguration configuration)
    {
        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var provider = new ServiceCollection().AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(MelLogLevel.Trace);
                Invoke(method, logging, configuration);
            }).BuildServiceProvider();

            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("M1");
            logger.LogInformation("triage-marker");
            return buffer.ToString();
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    /// <summary>Calls every configuration overload and ignores success. Used when startup must continue.</summary>
    /// <param name="configuration">Configuration to bind. Not null.</param>
    private static void InvokeEveryOverload(IConfiguration configuration)
    {
        var methods = ConfigurationOverloads();
        Assert.InRange(methods.Length, 1, 4);
        for (var i = 0; i < methods.Length; i++)
            InvokeAndLog(methods[i], configuration);
    }

    /// <summary>
    /// Invokes one static <c>AddALog</c> method. A target-invocation wrapper is removed
    /// so the test sees the exception <c>AddALog</c> threw.
    /// </summary>
    /// <param name="method">Static method. Not null. At most 4 parameters.</param>
    /// <param name="builder">Logging builder for the extension's first argument. Not null.</param>
    /// <param name="configuration">Configuration argument. Not null.</param>
    /// <exception cref="InvalidOperationException">Thrown when a parameter is neither builder nor configuration.</exception>
    private static void Invoke(MethodInfo method, ILoggingBuilder builder, IConfiguration configuration)
    {
        var parameters = method.GetParameters();
        if (parameters.Length > 4)
            throw new InvalidOperationException("AddALog has more than 4 parameters.");

        var args = new object[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(ILoggingBuilder))
                args[i] = builder;
            else if (type == typeof(IConfiguration))
                args[i] = configuration;
            else
                throw new InvalidOperationException("Unexpected AddALog parameter " + type.Name);
        }

        try
        {
            method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    /// <summary>Public static <c>AddALog</c> methods on the builder extensions that take configuration.</summary>
    /// <returns>At least the overload master ships. Never null. At most 4, or the caller stops.</returns>
    private static MethodInfo[] ConfigurationOverloads()
    {
        var found = typeof(ALogLoggingBuilderExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        var matches = new List<MethodInfo>(4);
        for (var i = 0; i < found.Length && matches.Count < 4; i++)
        {
            var method = found[i];
            if (!string.Equals(method.Name, "AddALog", StringComparison.Ordinal))
                continue;

            var parameters = method.GetParameters();
            var takesConfiguration = false;
            for (var p = 0; p < parameters.Length; p++)
            {
                if (parameters[p].ParameterType == typeof(IConfiguration))
                    takesConfiguration = true;
            }

            if (takesConfiguration)
                matches.Add(method);
        }

        return matches.ToArray();
    }

    /// <summary>
    /// The one-path single-value sentence.
    /// </summary>
    /// <param name="path">Canonical path, <c>ALog:Console</c> or <c>ALog:File</c>.</param>
    /// <returns>The full message a caller must see. It does not contain the configuration value.</returns>
    private static string ExpectedScalarMessage(string path)
    {
        return "Invalid ALog configuration at '" + path
            + "': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";
    }

    /// <summary>
    /// Calls <c>AddALog(ILoggingBuilder, IConfiguration)</c> and returns the exception message.
    /// </summary>
    /// <param name="configuration">Configuration passed to that overload. Not null.</param>
    /// <returns>The message, or a no-exception marker.</returns>
    private static string CallAddALog(IConfiguration configuration)
    {
        var error = Record.Exception(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddALog(configuration));
            using var provider = services.BuildServiceProvider();
        });
        return error is null ? "(no exception from AddALog)" : error.Message;
    }

    /// <summary>
    /// Calls <c>GetALogOptions</c> and returns the exception message.
    /// </summary>
    /// <param name="configuration">Configuration passed to that entry. Not null.</param>
    /// <returns>The message, or a no-exception marker.</returns>
    private static string CallGetALogOptions(IConfiguration configuration)
    {
        var error = Record.Exception(() => configuration.GetALogOptions());
        return error is null ? "(no exception from GetALogOptions)" : error.Message;
    }

    /// <summary>
    /// Requires <c>AddALog</c> and <c>GetALogOptions</c> to throw <paramref name="expected"/>.
    /// Each secret must be absent from both messages.
    /// </summary>
    /// <param name="configuration">Configuration passed to both entries. Not null.</param>
    /// <param name="expected">The full sentence. Not null.</param>
    /// <param name="secrets">Value texts that must not appear. May be empty.</param>
    private static void RequireConfigEntries(IConfiguration configuration, string expected, params string[] secrets)
    {
        var problems = new StringBuilder();
        var add = FailureOf(() => InvokeEveryOverload(configuration));
        NoteEntry(problems, "AddALog", add, expected, secrets);
        var got = FailureOf(() => configuration.GetALogOptions());
        NoteEntry(problems, "GetALogOptions", got, expected, secrets);
        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>Runs <paramref name="action"/> and returns the exception message, or a no-exception marker.</summary>
    /// <param name="action">One config entry. Not null.</param>
    /// <returns>The message, or <c>(no exception)</c>.</returns>
    private static string FailureOf(Action action)
    {
        var error = Record.Exception(action);
        return error is null ? "(no exception)" : error.Message;
    }

    /// <summary>Appends a mismatch or a leaked secret for one config entry.</summary>
    /// <param name="problems">Failure text. Not null.</param>
    /// <param name="entry"><c>AddALog</c> or <c>GetALogOptions</c>.</param>
    /// <param name="actual">Observed message. Not null.</param>
    /// <param name="expected">Required message. Not null.</param>
    /// <param name="secrets">Texts that must not appear.</param>
    private static void NoteEntry(StringBuilder problems, string entry, string actual, string expected, string[] secrets)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            if (problems.Length > 0)
                problems.Append('\n');
            problems.Append(entry).Append(": [").Append(actual).Append(']');
            return;
        }

        for (var i = 0; i < secrets.Length; i++)
        {
            if (actual.Contains(secrets[i], StringComparison.Ordinal))
            {
                if (problems.Length > 0)
                    problems.Append('\n');
                problems.Append(entry).Append(" contains ").Append(secrets[i]);
            }
        }
    }

    /// <summary>Loads a JSON object into configuration. The stream is read before this returns.</summary>
    /// <param name="json">One JSON object. Not null.</param>
    /// <returns>The bound configuration. Not null.</returns>
    private static IConfiguration LoadJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    /// <summary>Short signature for a failure line, such as <c>AddALog(ILoggingBuilder, IConfiguration)</c>.</summary>
    /// <param name="method">Method to describe. Not null.</param>
    /// <returns>The name and parameter type names. At most 4 parameters are included.</returns>
    private static string Signature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var limit = parameters.Length > 4 ? 4 : parameters.Length;
        var text = method.Name + "(";
        for (var i = 0; i < limit; i++)
        {
            if (i > 0)
                text += ", ";

            text += parameters[i].ParameterType.Name;
        }

        return text + ")";
    }

    /// <summary>
    /// Writes one facade line with a throwing property and a normal property.
    /// Problems are appended; this method does not assert, so the MEL case still runs.
    /// </summary>
    /// <param name="problems">Failure text. Not null. Unchanged when the line is kept.</param>
    private static void CollectFacade(StringBuilder problems)
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "facade.log");
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousSelf = SelfLog.Target;
        Exception? caller = null;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            SelfLog.Enable(line =>
            {
                if (self.Count < 20)
                    self.Add(line);
            });
            Log.Init(BuildPlainConfig(path, probe));
            caller = Record.Exception(() =>
                Log.Write(LogLevel.Info, "User {UserId} ok {Ok}", new ThrowingProperty(), "kept"));
            RequireLine(problems, "facade", stdout.ToString(), stderr.ToString(), ReadFileOrEmpty(path), caller, self, probe);
            Require(problems, self.Count == 1, "facade SelfLog count " + self.Count + " [" + Joined(self) + "]");
        }
        finally
        {
            Log.Shutdown();
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            SelfLog.Target = previousSelf;
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Writes one MEL line whose formatter does not call <c>ToString</c>.
    /// The property is read later by plain text. Problems are appended.
    /// </summary>
    /// <param name="problems">Failure text. Not null.</param>
    private static void CollectMel(StringBuilder problems)
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "mel.log");
        var probe = new RecordingWriter();
        var self = new List<string>(4);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousSelf = SelfLog.Target;
        Exception? caller = null;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            SelfLog.Enable(line =>
            {
                if (self.Count < 20)
                    self.Add(line);
            });
            using (var provider = new ALogLoggerProvider(BuildPlainConfig(path, probe)))
            {
                var logger = provider.CreateLogger("M1");
                caller = Record.Exception(() => logger.Log(
                    MelLogLevel.Information,
                    new EventId(1),
                    MelState(),
                    null,
                    static (_, _) => "User rendered ok kept"));
            }

            RequireLine(problems, "mel", stdout.ToString(), stderr.ToString(), ReadFileOrEmpty(path), caller, self, probe);
            Require(problems, self.Count == 0, "mel SelfLog count " + self.Count + " [" + Joined(self) + "]");
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            SelfLog.Target = previousSelf;
            DeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Checks one captured line: placeholder, the other property, no caller exception,
    /// no sync-write failure, no new SelfLog id, and the probe still holds the object.
    /// </summary>
    /// <param name="problems">Failure text. Not null.</param>
    /// <param name="label"><c>facade</c> or <c>mel</c>.</param>
    /// <param name="stdout">Console text. Not null.</param>
    /// <param name="stderr">Error text. Not null.</param>
    /// <param name="fileText">File text, or empty when the file was not created.</param>
    /// <param name="caller">Exception from the log call. Null when the call returned.</param>
    /// <param name="self">SelfLog lines captured for this call. Not null.</param>
    /// <param name="probe">Writer that must still see the original property. Not null.</param>
    private static void RequireLine(
        StringBuilder problems,
        string label,
        string stdout,
        string stderr,
        string fileText,
        Exception? caller,
        List<string> self,
        RecordingWriter probe)
    {
        var callerText = caller is null ? string.Empty : caller.GetType().Name + ": " + caller.Message;
        Require(problems, caller is null, label + " threw " + callerText);
        Require(problems, stdout.Contains(CapturePlaceholder, StringComparison.Ordinal), label + " console missing placeholder: " + Trim(stdout));
        Require(problems, stdout.Contains("Ok=kept", StringComparison.Ordinal), label + " console missing Ok=kept: " + Trim(stdout));
        Require(problems, !stdout.Contains("to-string-boom", StringComparison.Ordinal), label + " console contains the exception message");
        Require(problems, !stdout.Contains("System.InvalidOperationException", StringComparison.Ordinal), label + " console used the full type name");
        Require(problems, fileText.Contains(CapturePlaceholder, StringComparison.Ordinal), label + " file missing placeholder: " + Trim(fileText));
        Require(problems, fileText.Contains("Ok=kept", StringComparison.Ordinal), label + " file missing Ok=kept: " + Trim(fileText));
        Require(problems, !stderr.Contains("Sync write failed", StringComparison.Ordinal), label + " stderr has Sync write failed: " + Trim(stderr));
        Require(problems, !stderr.Contains("[FileLogWriter] Write failed", StringComparison.Ordinal), label + " stderr has file write failed: " + Trim(stderr));
        var selfText = Joined(self);
        Require(problems, !selfText.Contains("ALOG0068", StringComparison.Ordinal), label + " SelfLog has ALOG0068");
        Require(problems, !selfText.Contains("capture failed", StringComparison.Ordinal), label + " SelfLog mentions capture failed");
        Require(problems, probe.Events.Count == 1, label + " probe count " + probe.Events.Count);
        if (probe.Events.Count != 1)
            return;

        var properties = probe.Events[0].Properties;
        if (properties is null)
            return;
        Require(problems, properties["UserId"] is ThrowingProperty, label + " probe lost the throwing value");
        Require(problems, Equals(properties["Ok"], "kept"), label + " probe changed Ok");
    }

    /// <summary>Appends one failure. A true condition leaves <paramref name="problems"/> unchanged.</summary>
    /// <param name="problems">Failure text. Not null.</param>
    /// <param name="ok">True when this check passed.</param>
    /// <param name="message">Text used only when <paramref name="ok"/> is false.</param>
    private static void Require(StringBuilder problems, bool ok, string message)
    {
        if (ok)
            return;

        if (problems.Length > 0)
            problems.Append('\n');

        problems.Append(message);
    }

    /// <summary>
    /// Console and file writers with no formatter of their own, plus the probe.
    /// The engine copies one plain-text formatter onto the console and the file.
    /// </summary>
    /// <param name="path">File the file writer creates. Not null.</param>
    /// <param name="probe">Extra writer that must receive the original property. Not null.</param>
    /// <returns>A configuration the caller passes to one engine. Not shared.</returns>
    private static LoggerConfig BuildPlainConfig(string path, RecordingWriter probe)
    {
        return new LoggerConfig()
            .AddWriter(new ConsoleLogWriter(useColors: false))
            .AddWriter(new FileLogWriter(path))
            .AddWriter(probe)
            .SetFormatter(new PlainTextFormatter())
            .UseAsync(false)
            .SetMinimumLevel(LogLevel.Trace);
    }

    /// <summary>MEL state whose formatter text does not read the property. The value stays on the event.</summary>
    /// <returns>UserId, Ok, and the original format. Not null.</returns>
    private static List<KeyValuePair<string, object?>> MelState()
    {
        return new List<KeyValuePair<string, object?>>
        {
            new("UserId", new ThrowingProperty()),
            new("Ok", "kept"),
            new("{OriginalFormat}", "User {UserId} ok {Ok}")
        };
    }

    /// <summary>Creates a temp directory for one file writer. The caller deletes it.</summary>
    /// <returns>Absolute path of the new directory.</returns>
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-m1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes a temp directory. A failure to delete does not replace the test result.</summary>
    /// <param name="path">Directory from <see cref="NewDirectory"/>. Not null.</param>
    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Scratch space only. The asserts already ran.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as an IO failure: the directory is outside the repository.
        }
    }

    /// <summary>Reads a log file. A missing file is empty text, which is what a dropped line looks like.</summary>
    /// <param name="path">File the writer was given. Not null.</param>
    /// <returns>The file text, or empty when the writer never created it.</returns>
    private static string ReadFileOrEmpty(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        return File.ReadAllText(path);
    }

    /// <summary>One physical line of captured text, capped so a stack trace cannot flood the assert.</summary>
    /// <param name="text">Captured output. Not null.</param>
    /// <returns>At most 400 characters, with newlines turned into spaces.</returns>
    private static string Trim(string text)
    {
        var limit = text.Length > 400 ? 400 : text.Length;
        return text.Substring(0, limit).Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>Joins at most 10 SelfLog lines for an assert message.</summary>
    /// <param name="lines">Captured lines. Not null.</param>
    /// <returns>The lines separated by <c> | </c>.</returns>
    private static string Joined(List<string> lines)
    {
        var count = lines.Count > 10 ? 10 : lines.Count;
        return string.Join(" | ", lines.GetRange(0, count));
    }

    /// <summary>Walks up from the test output until <c>ALog.sln</c> is found.</summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="InvalidOperationException">The walk passed 8 parents without finding the solution.</exception>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ALog.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("ALog.sln was not found.");
    }

    /// <summary>Reads one repository file. Files over 1_000_000 bytes are rejected.</summary>
    /// <param name="relative">Path relative to the repository root, with slashes. Not null.</param>
    /// <returns>The file text.</returns>
    /// <exception cref="InvalidOperationException">The file is missing or larger than the cap.</exception>
    private static string ReadRepoFile(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new InvalidOperationException(relative + " is missing.");

        if (info.Length > 1_000_000)
            throw new InvalidOperationException(relative + " is larger than 1000000 bytes.");

        return File.ReadAllText(path);
    }

    /// <summary>Returns the text after <paramref name="start"/> and before the next <paramref name="end"/>.</summary>
    /// <param name="text">Source. Not null.</param>
    /// <param name="start">Opening marker. Not null.</param>
    /// <param name="end">Closing marker. Not null.</param>
    /// <returns>The slice, or empty when the opening marker is missing.</returns>
    private static string Slice(string text, string start, string end)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        if (at < 0)
            return string.Empty;

        var from = at + start.Length;
        var next = text.IndexOf(end, from, StringComparison.Ordinal);
        if (next < 0)
            return text.Substring(from);

        return text.Substring(from, next - from);
    }

    /// <summary>Counts non-overlapping hits, stopping at 20.</summary>
    /// <param name="text">Text to scan. Not null.</param>
    /// <param name="needle">Exact text. Not null or empty.</param>
    /// <returns>The number of hits, or 20 when there are at least 20.</returns>
    private static int CountOf(string text, string needle)
    {
        var count = 0;
        var start = 0;
        while (count < 20)
        {
            var found = text.IndexOf(needle, start, StringComparison.Ordinal);
            if (found < 0)
                return count;

            count++;
            var next = found + needle.Length;
            if (next <= start)
                return count;

            start = next;
        }

        return count;
    }

    /// <summary>Splits on newlines and keeps non-empty lines, at most 80.</summary>
    /// <param name="text">Section text. Not null.</param>
    /// <returns>The lines without the line ending. Never null.</returns>
    private static List<string> NonEmptyLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length && lines.Count < 80; i++)
        {
            if (i != text.Length && text[i] != '\n')
                continue;

            var line = text.Substring(start, i - start).TrimEnd('\r');
            start = i + 1;
            if (line.Length > 0)
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// True when a comment names a SqlClient version: <c>SqlClient</c> followed by spaces and a digit.
    /// A PackageReference version on its own line is not a comment and is not passed here.
    /// </summary>
    /// <param name="line">One comment line. Not null.</param>
    /// <returns>True when a version digit follows <c>SqlClient</c>.</returns>
    private static bool HasSqlClientVersion(string line)
    {
        const string name = "SqlClient";
        var at = 0;
        var scans = 0;
        while (at < line.Length && scans < 20)
        {
            scans++;
            var found = line.IndexOf(name, at, StringComparison.Ordinal);
            if (found < 0)
                return false;

            var cursor = found + name.Length;
            while (cursor < line.Length && (line[cursor] == ' ' || line[cursor] == '\t'))
                cursor++;

            if (cursor < line.Length && line[cursor] >= '0' && line[cursor] <= '9')
                return true;

            var next = found + name.Length;
            if (next <= at)
                return true;

            at = next;
        }

        return false;
    }

    /// <summary>
    /// A property value whose text cannot be read.
    /// Plain text must keep the line and show the exception type, not this object's name.
    /// </summary>
    private sealed class ThrowingProperty
    {
        /// <summary>Throws <see cref="InvalidOperationException"/> with the text <c>to-string-boom</c>.</summary>
        /// <returns>This method does not return.</returns>
        /// <exception cref="InvalidOperationException">Always thrown.</exception>
        public override string ToString() => throw new InvalidOperationException("to-string-boom");
    }

    /// <summary>
    /// A property whose text is null. Plain text keeps the name and an empty value.
    /// </summary>
    private sealed class NullToString
    {
        /// <summary>Returns null, which is what a broken <c>ToString</c> is allowed to do.</summary>
        /// <returns>Null. The formatter turns that into an empty value.</returns>
        public override string? ToString() => null;
    }

    /// <summary>
    /// <see cref="IFormattable"/> text differs from <see cref="object.ToString"/>.
    /// The formatted text is returned only when the provider is <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.
    /// </summary>
    private sealed class DistinctFormattable : IFormattable
    {
        /// <summary>The plain text, which plain-text logging must not use.</summary>
        /// <returns><c>plain-tostring</c>.</returns>
        public override string ToString() => "plain-tostring";

        /// <summary>The culture-formatted text ALog writes for this value.</summary>
        /// <param name="format">Ignored. Master passes null.</param>
        /// <param name="formatProvider">Must be <see cref="System.Globalization.CultureInfo.CurrentCulture"/>.</param>
        /// <returns><c>iformattable-current</c> for the current culture. Otherwise <c>wrong-provider</c>.</returns>
        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            if (!ReferenceEquals(formatProvider, System.Globalization.CultureInfo.CurrentCulture))
                return "wrong-provider";

            return "iformattable-current";
        }
    }

    /// <summary>
    /// <see cref="IFormattable"/> whose formatted text throws. Plain <c>ToString()</c> still returns.
    /// </summary>
    private sealed class ThrowingFormattable : IFormattable
    {
        /// <summary>Plain text that must not appear when formatting throws.</summary>
        /// <returns><c>plain-tostring</c>.</returns>
        public override string ToString() => "plain-tostring";

        /// <summary>Throws <see cref="InvalidOperationException"/>.</summary>
        /// <param name="format">Ignored.</param>
        /// <param name="formatProvider">Ignored.</param>
        /// <returns>This method does not return.</returns>
        /// <exception cref="InvalidOperationException">Always thrown.</exception>
        public string ToString(string? format, IFormatProvider? formatProvider)
            => throw new InvalidOperationException("format-boom");
    }
}
