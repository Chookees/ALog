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
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// M1 on master: a scalar where <c>ALog:Console</c> or <c>ALog:File</c> is bound
/// stops startup, plain text still writes when a property's <c>ToString</c> throws,
/// and <c>Console:Enabled</c> is not applied again when configuration reloads.
/// </summary>
public sealed class MasterBugTests : IDisposable
{
    private const string CapturePlaceholder = "[capture failed: InvalidOperationException]";

    private const string WriterSentence =
        "Writer settings (Enabled, paths) are read at startup; only levels reload at runtime.";

    private const string SkipSentence =
        "To see why a test was skipped, run `dotnet test --logger \"console;verbosity=normal\"`.";

    private const string Remarks =
        "Read once at startup. Changes while the app runs have no effect; restart the app.";

    private const string BreakingLine =
        "- Breaking (behavior): a single value where ALog expects a section (for example `\"ALog\": { \"Console\": true }`) now stops startup with an error that names the path. Before, ALog ignored it and wrote nothing.";

    private const string FixedLine =
        "- Fixed: `ConsoleLogWriter` and `FileLogWriter` write the line even when a property's `ToString()` throws. That property shows `[capture failed: ExceptionType]`.";

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
    /// A section that has both a value and a child is not a single value.
    /// Startup continues. This locks the child check so a real <c>Enabled</c> key is kept.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConsoleValueAndChild_DoesNotStopStartup()
    {
        await Task.CompletedTask;
        var values = new Dictionary<string, string?>
        {
            ["ALog:Console"] = "true",
            ["ALog:Console:Enabled"] = "true"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var error = Record.Exception(() => InvokeEveryOverload(configuration));
        Assert.Null(error);
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
    /// Changing <c>ALog:Console:Enabled</c> from false to true through
    /// <see cref="IConfigurationRoot.Reload"/> does not start the console writer
    /// and does not throw. Writer settings are read only when <c>AddALog</c> runs.
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

            values["ALog:Console:Enabled"] = "true";
            configuration.Reload();
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
    /// Plain text keeps the first 1024 properties and drops the rest so the render loop
    /// has a fixed bound. Property 1024 (the 1025th) is not on the line.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PlainText_RendersAtMost1024Properties()
    {
        await Task.CompletedTask;
        const int cap = 1024;
        var properties = new Dictionary<string, object>(cap + 1);
        for (var i = 0; i < cap + 1; i++)
            properties.Add("p" + i.ToString("0000"), "v");

        var text = new PlainTextFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "bound",
            null,
            null,
            properties));

        Assert.Contains("p0000=v", text, StringComparison.Ordinal);
        Assert.Contains("p1023=v", text, StringComparison.Ordinal);
        Assert.DoesNotContain("p1024=v", text, StringComparison.Ordinal);

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
    /// Locks the M1 sentences: writer remarks, install lines without a version,
    /// the two changelog bullets, and the skip hint.
    /// The runtime-reload sentence is absent: <c>ALog:MinimumLevel</c> is not re-read.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Docs_LockMasterBugTexts()
    {
        await Task.CompletedTask;
        var readme = ReadRepoFile("README.md");
        var packageReadme = ReadRepoFile("ALog.Extensions.Logging/README.md");
        var aspnet = Slice(readme, "### ASP.NET Core", "## Console output");
        var contributing = Slice(readme, "## Contributing", "## License");

        Assert.Contains("{ \"ALog\": { \"Console\": { \"Enabled\": true } } }", aspnet, StringComparison.Ordinal);
        Assert.Equal(0, CountOf(readme, WriterSentence));
        Assert.Contains(SkipSentence, contributing, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(readme, SkipSentence));
        Assert.Contains("dotnet add package ALog", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet add package ALog.Extensions.Logging", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--version", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("--version", packageReadme, StringComparison.Ordinal);

        var notes = ReadRepoFile("ALog/Config/ALogOptions.cs");
        Assert.Equal(2, CountOf(notes, "<remarks>" + Remarks + "</remarks>"));

        var changelog = Slice(ReadRepoFile("CHANGELOG.md"), "## 0.2.1\n", "\n## 0.2.0");
        var lines = NonEmptyLines(changelog);
        Assert.True(lines.Count >= 3, "0.2.1 section is too short.");
        Assert.StartsWith("- Breaking (dependency): `Microsoft.Data.SqlClient`", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("- Fixed: with the background queue,", lines[lines.Count - 3], StringComparison.Ordinal);
        Assert.Equal(BreakingLine, lines[lines.Count - 2]);
        Assert.Equal(FixedLine, lines[lines.Count - 1]);

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

    /// <summary>The startup sentence for one section path. The path is the only inserted text.</summary>
    /// <param name="path">Section path, such as <c>ALog:Console</c>. Not null.</param>
    /// <returns>The full message a caller must see.</returns>
    private static string ExpectedScalarMessage(string path)
    {
        return "Invalid ALog configuration at '" + path
            + "': expected a section with settings, but found a single value. Put the settings inside the section, as shown in the README.";
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
}
