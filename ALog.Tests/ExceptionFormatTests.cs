using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Writers.Http;

namespace ALog.Tests;

/// <summary>
/// Locks the plain-text and JSON exception shape: one helper, the five sinks, and the case list from uiux §5c.
/// </summary>
public sealed class ExceptionFormatTests
{
    private const string TargetTail =
        "Payment failed | InvalidOperationException: Card declined | at CheckoutService.Pay in CheckoutService.cs:42 | ctx: orderId=17";

    private const string BreakingBullet =
        "- Plain text, console, and file output write exceptions as \"Type: Message\", followed by inner exceptions and the source line (\"at Class.Method in File.cs:42\"). The \"EX:\" label is gone; update log parsers that match it. \"|\" and \"-->\" inside messages are escaped as \"\\|\" and \"--\\>\".";

    private const string AddedSourceBullet =
        "- JSON and HTTP exception output add \"source\" (method, file, line) and \"inner\" (type, message, and source of each inner exception, up to 16).";

    private const string AddedStackBullet =
        "- PlainTextFormatter(includeStackTrace: true) appends the full stack trace.";

    /// <summary>
    /// The README example line is what plain text emits for a thrown <c>InvalidOperationException</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TargetLine_MatchesConsoleExample()
    {
        await Task.CompletedTask;
        var line = new PlainTextFormatter("HH:mm:ss").Format(PaymentEvent());
        Assert.Equal("[20:55:19] [Error] " + TargetTail, line);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("EX:", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>System.*</c> uses the short name. Any other namespace keeps the full name.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TextSegment_SystemShortName_CustomFullName()
    {
        await Task.CompletedTask;
        Assert.Equal(
            "InvalidOperationException: Card declined",
            ExceptionReport.FormatText(new InvalidOperationException("Card declined")));
        var card = new ALog.QuickstartSample.CardException("declined");
        Assert.Equal("ALog.QuickstartSample.CardException: declined", ExceptionReport.FormatText(card));
        Assert.Equal(
            "Microsoft.Data.Demo.DemoException: no",
            ExceptionReport.FormatText(new Microsoft.Data.Demo.DemoException("no")));
        Assert.Equal("System.IO.File", ExceptionReport.FrameTypeName(typeof(File)));
        Assert.Equal("CheckoutService", ExceptionReport.FrameTypeName(typeof(ALog.QuickstartSample.CheckoutService)));
    }

    /// <summary>
    /// Empty, null, whitespace, and the framework default sentence print the type with no colon.
    /// A message that only looks similar, because it has more words, stays.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TextSegment_EmptyNullWhitespaceAndDefaultMessage()
    {
        await Task.CompletedTask;
        Assert.Equal("Exception", ExceptionReport.FormatText(new Exception()));
        Assert.Equal(typeof(SilentException).FullName, ExceptionReport.FormatText(new SilentException()));
        Assert.Equal(typeof(EmptyMessage).FullName, ExceptionReport.FormatText(new EmptyMessage()));
        Assert.Equal(typeof(NullMessage).FullName, ExceptionReport.FormatText(new NullMessage()));
        Assert.Equal(typeof(WhitespaceMessage).FullName, ExceptionReport.FormatText(new WhitespaceMessage()));
        Assert.Equal("InvalidOperationException:   hi  ", ExceptionReport.FormatText(new InvalidOperationException("  hi  ")));
        Assert.Equal(
            "Exception: Exception of type 'X' was thrown. extra",
            ExceptionReport.FormatText(new Exception("Exception of type 'X' was thrown. extra")));
        Assert.DoesNotContain(":", ExceptionReport.FormatText(new Exception()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A message that already starts with the type is kept. The type is not stripped and not doubled away.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TextSegment_KeepsExceptionPrefix()
    {
        await Task.CompletedTask;
        Assert.Equal("Exception: Exception: x", ExceptionReport.FormatText(new Exception("Exception: x")));
    }

    /// <summary>
    /// Newlines become <c>\r</c> and <c>\n</c>, Unicode stays, and the cut is after escaping.
    /// A surrogate pair on the 500-character boundary is not split.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TextSegment_MultilineUnicodeAndLongMessage()
    {
        await Task.CompletedTask;
        var emoji = "\U0001F600";
        Assert.Equal(2, emoji.Length);
        var multiline = ExceptionReport.FormatText(new InvalidOperationException("a\r\nb\nc"));
        Assert.Equal("InvalidOperationException: a\\r\\nb\\nc", multiline);
        Assert.DoesNotContain("\n", multiline, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", multiline, StringComparison.Ordinal);
        Assert.Equal(
            "InvalidOperationException: héllo " + emoji,
            ExceptionReport.FormatText(new InvalidOperationException("héllo " + emoji)));

        Assert.Equal(new string('a', 500), ExceptionReport.Escape(new string('a', 500), messageLimit: true));
        Assert.Equal(new string('a', 500) + " [truncated]", ExceptionReport.Escape(new string('a', 501), messageLimit: true));

        var splitPair = new string('a', 499) + emoji;
        var split = ExceptionReport.Escape(splitPair, messageLimit: true);
        Assert.Equal(new string('a', 499) + " [truncated]", split);
        Assert.DoesNotContain("\uD83D", split, StringComparison.Ordinal);

        var keptPair = new string('a', 498) + emoji + "b";
        var kept = ExceptionReport.Escape(keptPair, messageLimit: true);
        Assert.Equal(new string('a', 498) + emoji + " [truncated]", kept);

        var pipes = ExceptionReport.Escape(new string('|', 251), messageLimit: true);
        Assert.Equal(string.Concat(Enumerable.Repeat("\\|", 250)) + " [truncated]", pipes);
        Assert.Equal(500 + " [truncated]".Length, pipes.Length);
    }

    /// <summary>
    /// <c>FileName</c>, <c>HResult</c>, and <c>Data</c> are not extra segments.
    /// The message is the only place a file name can appear.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TextSegment_DoesNotAddFileNameOrHResult()
    {
        await Task.CompletedTask;
        var ex = new FileNotFoundException("missing", "secret.txt");
        var text = ExceptionReport.FormatText(ex);
        Assert.Equal("FileNotFoundException: " + ex.Message, text);
        Assert.DoesNotContain("HResult", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FileName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Backslash is escaped before pipe and arrow, so the backslash those escapes insert is not doubled.
    /// Swapping that order changes these expected strings.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EscapeOrder_BackslashBeforePipeAndArrow()
    {
        await Task.CompletedTask;
        Assert.Equal("\\|", ExceptionReport.Escape("|", messageLimit: true));
        Assert.Equal("\\\\", ExceptionReport.Escape("\\", messageLimit: true));
        Assert.Equal("--\\>", ExceptionReport.Escape("-->", messageLimit: true));
        Assert.Equal("\\\\--\\>", ExceptionReport.Escape("\\-->", messageLimit: true));
        Assert.Equal("\\r\\n", ExceptionReport.Escape("\r\n", messageLimit: true));
        Assert.Equal("a \\| b --\\> c", ExceptionReport.Escape("a | b --> c", messageLimit: true));
        Assert.DoesNotContain("-->", ExceptionReport.Escape("-->", messageLimit: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// Escaping then unescaping returns the original text, and the escaped form contains neither raw separator.
    /// A context value takes the same trip through the line.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Escape_RoundTrip_SeparatorsStayUnambiguous()
    {
        await Task.CompletedTask;
        var samples = new[]
        {
            "|",
            "\\",
            "-->",
            "\\-->",
            "a | b",
            "a --> b",
            "\r\n",
            "-->|\\",
            "line1\nline2",
            "mix \\ | --> \r\n end",
            "a | b --> c\\d"
        };
        for (var i = 0; i < samples.Length; i++)
        {
            var escaped = ExceptionReport.Escape(samples[i], messageLimit: true);
            Assert.Equal(samples[i], Unescape(escaped));
            Assert.DoesNotContain(" | ", escaped, StringComparison.Ordinal);
            Assert.DoesNotContain("-->", escaped, StringComparison.Ordinal);
        }

        var evt = new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Error,
            "a | b",
            new InvalidOperationException("a | b --> c\\d"),
            new Dictionary<string, object> { ["k"] = "x | y --> z" },
            new Dictionary<string, object> { ["p"] = "a|b" });
        var line = new PlainTextFormatter().Format(evt);
        Assert.Contains("a | b | InvalidOperationException: a \\| b --\\> c\\\\d | props: p=a|b | ctx: k=x \\| y --\\> z", line, StringComparison.Ordinal);
        var segments = SplitSegments(line);
        Assert.Contains(segments, segment => segment.Contains("InvalidOperationException:", StringComparison.Ordinal));
        var exceptionSegment = segments.First(segment => segment.StartsWith("InvalidOperationException:", StringComparison.Ordinal));
        Assert.Equal("a | b --> c\\d", Unescape(exceptionSegment.Substring("InvalidOperationException: ".Length)));
        var ctx = segments.First(segment => segment.StartsWith("ctx:", StringComparison.Ordinal));
        Assert.Equal("x | y --> z", Unescape(ctx.Substring("ctx: k=".Length)));
    }

    /// <summary>An exception that was never thrown has no <c>at</c> segment and no JSON source.</summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_NoStackOmitsAt()
    {
        await Task.CompletedTask;
        var ex = new InvalidOperationException("Card declined");
        Assert.Equal("InvalidOperationException: Card declined", ExceptionReport.FormatText(ex));
        Assert.Null(ExceptionReport.CreateBody(ex).Source);
        Assert.Null(ExceptionReport.CreateBody(ex).StackTrace);
    }

    /// <summary>
    /// Without a PDB the origin is <c>at Type.Method</c> and does not say <c>in</c>.
    /// A file with no line number still names the file and omits the colon.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_NoPdbOmitsFile()
    {
        await Task.CompletedTask;
        var synthetic = new FrameView("ALog.Tests", "ALog.QuickstartSample", "CheckoutService", "Pay", null, 0);
        Assert.Equal("at CheckoutService.Pay", ExceptionReport.FormatAt(new[] { synthetic }));
        var fileOnly = new FrameView("ALog.Tests", "Demo", "CheckoutService", "Pay", "CheckoutService.cs", 0);
        Assert.Equal("at CheckoutService.Pay in CheckoutService.cs", ExceptionReport.FormatAt(new[] { fileOnly }));

        var method = new DynamicMethod("Pay", typeof(void), Type.EmptyTypes, typeof(ExceptionFormatTests).Module);
        var il = method.GetILGenerator();
        il.ThrowException(typeof(InvalidOperationException));
        Exception? thrown = null;
        try
        {
            method.Invoke(null, null);
        }
        catch (TargetInvocationException ex)
        {
            thrown = ex.InnerException;
        }

        var text = ExceptionReport.FormatText(thrown!);
        Assert.Contains("at Pay", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" in ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// User code in <c>ALog.QuickstartSample</c>, <c>ALogSample</c>, and <c>SystemsCorp</c> each names its own frame.
    /// A namespace prefix of <c>System</c> or an assembly prefix of <c>ALog</c> would hide one of them.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_UserNamespacesShowTheirOwnFrame()
    {
        await Task.CompletedTask;
        var pay = Catch(() => ALog.QuickstartSample.CheckoutService.Pay());
        var sample = Catch(() => ALogSample.Worker.Run());
        var corp = Catch(() => SystemsCorp.Billing.Charge());

        Assert.Contains("at CheckoutService.Pay in CheckoutService.cs:42", ExceptionReport.FormatText(pay), StringComparison.Ordinal);
        Assert.Contains("at Worker.Run in Worker.cs:3", ExceptionReport.FormatText(sample), StringComparison.Ordinal);
        Assert.Contains("at Billing.Charge in Billing.cs:11", ExceptionReport.FormatText(corp), StringComparison.Ordinal);
        Assert.DoesNotContain("Worker", ExceptionReport.FormatText(pay), StringComparison.Ordinal);
        Assert.DoesNotContain("Billing", ExceptionReport.FormatText(sample), StringComparison.Ordinal);
        Assert.DoesNotContain("CheckoutService", ExceptionReport.FormatText(corp), StringComparison.Ordinal);
    }

    /// <summary>
    /// Async state machines, lambdas, and local functions show the name from source, not <c>MoveNext</c> or <c>b__</c>.
    /// The innermost thrown exception supplies the frame when a wrapper is logged around it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_AsyncLambdaAndLocalNames()
    {
        var asyncEx = await CatchAsync(() => ALog.QuickstartSample.CheckoutService.PayAsync());
        var lambda = Catch(() => ALog.QuickstartSample.CheckoutService.Run());
        var local = Catch(() => ALog.QuickstartSample.CheckoutService.Bill());
        Assert.Contains("at CheckoutService.PayAsync in BlobClient.cs:88", ExceptionReport.FormatText(asyncEx), StringComparison.Ordinal);
        Assert.DoesNotContain("MoveNext", ExceptionReport.FormatText(asyncEx), StringComparison.Ordinal);
        Assert.Contains("at CheckoutService.Run in Lambda.cs:7", ExceptionReport.FormatText(lambda), StringComparison.Ordinal);
        Assert.DoesNotContain("b__", ExceptionReport.FormatText(lambda), StringComparison.Ordinal);
        Assert.Contains("at CheckoutService.ChargeLocal in Local.cs:9", ExceptionReport.FormatText(local), StringComparison.Ordinal);
        Assert.DoesNotContain("g__", ExceptionReport.FormatText(local), StringComparison.Ordinal);

        var wrapped = Catch(() => throw new InvalidOperationException("wrap", Catch(() => ALog.QuickstartSample.CheckoutService.Pay())));
        var wrappedText = ExceptionReport.FormatText(wrapped);
        Assert.Contains("InvalidOperationException: wrap --> InvalidOperationException: Card declined", wrappedText, StringComparison.Ordinal);
        Assert.Contains("at CheckoutService.Pay in CheckoutService.cs:42", wrappedText, StringComparison.Ordinal);

        Assert.Equal("PayAsync", ExceptionReport.RenameCompilerMethod("<PayAsync>d__3", "MoveNext"));
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod("CheckoutService", "MoveNext"));
        Assert.Equal("Main", ExceptionReport.RenameCompilerMethod("Program", "<Main>$"));
        Assert.Equal("Run", ExceptionReport.RenameCompilerMethod("<>c", "<Run>b__0_0"));
        Assert.Equal("Charge", ExceptionReport.RenameCompilerMethod("CheckoutService", "<Bill>g__Charge|0_1"));
        Assert.Equal("Pay", ExceptionReport.RenameCompilerMethod("CheckoutService", "Pay"));
        Assert.Equal("<bad>", ExceptionReport.RenameCompilerMethod("<>c", "<bad>"));
        Assert.Equal("<X>b__1", ExceptionReport.RenameCompilerMethod("<>c", "<X>b__1"));
    }

    /// <summary>
    /// A top-level program that awaits names <c>Main</c>, not <c>MoveNext</c>.
    /// Nested lambda, local, async-lambda, and async-local names peel to the source name.
    /// The peel stops at eight, so a deeper bracket nest is left unchanged.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_TopLevelAsyncAndNestedNames()
    {
        await RunTopLevelAsync();
        Assert.Equal(
            "InvalidOperationException: Card declined | at Program.Main in Program.cs:10",
            ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.Awaited!));
        Assert.Equal(
            "InvalidOperationException: lam | at Program.Main in LambdaTop.cs:4",
            ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.Lambda!));
        Assert.Equal(
            "InvalidOperationException: loc | at Program.Local in LocalTop.cs:3",
            ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.Local!));
        Assert.Equal(
            "InvalidOperationException: al | at Program.Main in AsyncLambda.cs:5",
            ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.AsyncLambda!));
        Assert.Equal(
            "InvalidOperationException: hl | at Program.Helper in AsyncLocal.cs:6",
            ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.AsyncLocal!));

        Assert.Equal("Main", ExceptionReport.RenameCompilerMethod("<<Main>$>d__0", "MoveNext"));
        Assert.Equal("Main", ExceptionReport.RenameCompilerMethod("<>c", "<<Main>$>b__0_5"));
        Assert.Equal("Local", ExceptionReport.RenameCompilerMethod("Program", "<<Main>$>g__Local|0_1"));
        Assert.Equal("Main", ExceptionReport.RenameCompilerMethod("<<<Main>$>b__0_11>d", "MoveNext"));
        Assert.Equal("Helper", ExceptionReport.RenameCompilerMethod("<<<Main>$>g__Helper|0_12>d", "MoveNext"));
        Assert.DoesNotContain("MoveNext", ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.Awaited!), StringComparison.Ordinal);
        Assert.DoesNotContain("<Main>$", ExceptionReport.FormatText(ExfTopLevel.TopLevelCapture.Lambda!), StringComparison.Ordinal);

        var deep = new string('<', 9) + "X" + new string('>', 9) + "d__1";
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod(deep, "MoveNext"));
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod(new string('a', 513), "MoveNext"));
    }

    /// <summary>
    /// When every frame is <c>System</c> or <c>Microsoft</c>, the top frame is used and keeps its namespace.
    /// A later <c>SystemExtra</c> or <c>MyMicrosoft</c> frame is user code and wins over a real framework frame.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_FrameworkOnlyUsesTopFrame()
    {
        await Task.CompletedTask;
        var onlyFramework = new[]
        {
            new FrameView("System.Private.CoreLib", "System.IO", "System.IO.File", "ReadAllText", null, 0),
            new FrameView("System.Private.CoreLib", "System", "System.ThrowHelper", "Throw", null, 0)
        };
        Assert.Equal("at System.IO.File.ReadAllText", ExceptionReport.FormatAt(onlyFramework));

        var systemExtra = new[]
        {
            new FrameView("System.Private.CoreLib", "System.IO", "System.IO.File", "ReadAllText", null, 0),
            new FrameView("App", "SystemExtra", "Clock", "Now", "Clock.cs", 4)
        };
        Assert.Equal("at Clock.Now in Clock.cs:4", ExceptionReport.FormatAt(systemExtra));

        var myMicrosoft = new[]
        {
            new FrameView("App", "Microsoft.Extensions.Logging", "Logger", "Log", null, 0),
            new FrameView("App", "MyMicrosoft", "Mine", "Run", "Mine.cs", 2)
        };
        Assert.Equal("at Mine.Run in Mine.cs:2", ExceptionReport.FormatAt(myMicrosoft));
        Assert.False(ExceptionReport.IsFrameworkNamespace(null));
        Assert.False(ExceptionReport.IsFrameworkNamespace(string.Empty));
        Assert.True(ExceptionReport.IsFrameworkNamespace("System"));
        Assert.True(ExceptionReport.IsFrameworkNamespace("System.IO"));
        Assert.True(ExceptionReport.IsFrameworkNamespace("Microsoft"));
        Assert.True(ExceptionReport.IsFrameworkNamespace("Microsoft.Extensions"));
        Assert.False(ExceptionReport.IsFrameworkNamespace("SystemExtra"));
        Assert.False(ExceptionReport.IsFrameworkNamespace("SystemsCorp"));
        Assert.False(ExceptionReport.IsFrameworkNamespace("MyMicrosoft"));
        Assert.False(ExceptionReport.IsFrameworkNamespace("ALog.QuickstartSample"));
    }

    /// <summary>
    /// Frames from assemblies <c>ALog</c> and <c>ALog.Extensions.Logging</c> are skipped by exact name.
    /// A real throw inside <c>FileLogWriter</c> therefore shows the caller, not the library constructor.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_SkipsALogAssemblyAndFrameworkNamespace()
    {
        await Task.CompletedTask;
        var frames = new[]
        {
            new FrameView("ALog", "ALog.Writers.File", "FileLogWriter", ".ctor", "FileLogWriter.cs", 20),
            new FrameView("ALog.Extensions.Logging", "ALog.Extensions.Logging", "ALogLogger", "Log", null, 0),
            new FrameView("ALog.QuickstartSample", "ALog.QuickstartSample", "CheckoutService", "Pay", "CheckoutService.cs", 42)
        };
        Assert.Equal("at CheckoutService.Pay in CheckoutService.cs:42", ExceptionReport.FormatAt(frames));

        var extra = new[]
        {
            new FrameView("ALog.Extensions.Logging.Extra", "Contoso", "Extra", "Go", "Extra.cs", 1),
            new FrameView("Other", "Contoso", "Other", "Run", null, 0)
        };
        Assert.Equal("at Extra.Go in Extra.cs:1", ExceptionReport.FormatAt(extra));
        Assert.True(ExceptionReport.IsSkipped("ALog", "ALog.QuickstartSample"));
        Assert.True(ExceptionReport.IsSkipped("ALog.Extensions.Logging", "Contoso"));
        Assert.False(ExceptionReport.IsSkipped("ALog.Tests", "ALog.QuickstartSample"));
        Assert.False(ExceptionReport.IsSkipped("ALog.QuickstartSample", "ALog.QuickstartSample"));
        Assert.False(ExceptionReport.IsSkipped("ALogSample", "ALogSample"));
        Assert.False(ExceptionReport.IsSkipped("ALog.Extensions.Logging.Extra", "Contoso"));
        Assert.Equal(-1, ExceptionReport.SelectFrame(Array.Empty<FrameView>()));

        var thrown = Catch(() => _ = new FileLogWriter(null!));
        var text = ExceptionReport.FormatText(thrown);
        Assert.Contains("ArgumentNullException:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FileLogWriter", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Origin_SkipsALogAssemblyAndFrameworkNamespace), text, StringComparison.Ordinal);
    }

    /// <summary>A normal chain shows three nodes and then <c>(+n more)</c>. The fourth message is absent.</summary>
    [Fact(Timeout = 30000)]
    public async Task Chain_StopsAtThree()
    {
        await Task.CompletedTask;
        var three = new InvalidOperationException("a", new InvalidOperationException("b", new InvalidOperationException("c")));
        Assert.Equal(
            "InvalidOperationException: a --> InvalidOperationException: b --> InvalidOperationException: c",
            ExceptionReport.FormatText(three));

        var four = new InvalidOperationException(
            "a",
            new InvalidOperationException("b", new InvalidOperationException("c", new InvalidOperationException("d"))));
        Assert.Equal(
            "InvalidOperationException: a --> InvalidOperationException: b --> InvalidOperationException: c (+1 more)",
            ExceptionReport.FormatText(four));
        Assert.DoesNotContain(": d", ExceptionReport.FormatText(four), StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="AggregateException"/> drops its own message, flattens nested aggregates, and counts that flat list.
    /// The order matches <see cref="AggregateException.Flatten"/>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_FlattensAndCountsFlattenedList()
    {
        await Task.CompletedTask;
        var pair = new AggregateException(new InvalidOperationException("a"), new TimeoutException("b"));
        Assert.Equal(
            "AggregateException --> InvalidOperationException: a; TimeoutException: b",
            ExceptionReport.FormatText(pair));

        var custom = new AggregateException("custom-message", new InvalidOperationException("a"));
        Assert.Equal("AggregateException --> InvalidOperationException: a", ExceptionReport.FormatText(custom));
        Assert.DoesNotContain("custom-message", ExceptionReport.FormatText(custom), StringComparison.Ordinal);
        Assert.Equal(custom.Message, ExceptionReport.CreateBody(custom).Message);
        Assert.Contains("custom-message", custom.Message, StringComparison.Ordinal);
        Assert.Equal("AggregateException", ExceptionReport.FormatText(new AggregateException()));
        Assert.Equal("One or more errors occurred.", ExceptionReport.CreateBody(new AggregateException()).Message);

        var nested = new AggregateException(new InvalidOperationException("a"), new TimeoutException("b"));
        var outer = new AggregateException(nested, new IOException("c"), new InvalidOperationException("d"));
        var flat = outer.Flatten().InnerExceptions;
        Assert.Equal(4, flat.Count);
        var expected = "AggregateException --> "
            + flat[0].GetType().Name + ": " + flat[0].Message + "; "
            + flat[1].GetType().Name + ": " + flat[1].Message + "; "
            + flat[2].GetType().Name + ": " + flat[2].Message
            + " (+1 more)";
        Assert.Equal(expected, ExceptionReport.FormatText(outer));
        Assert.DoesNotContain(flat[3].Message, ExceptionReport.FormatText(outer), StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON keeps <c>type</c>, <c>message</c>, and <c>stackTrace</c> and adds <c>source</c> and <c>inner</c>.
    /// Compact output stays one line. Pretty output stays indented. The message is raw and full.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Json_AddsSourceAndInnerWithoutChangingExistingFields()
    {
        await Task.CompletedTask;
        var ex = Catch(() => ALog.QuickstartSample.CheckoutService.Pay());
        var evt = new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "Payment failed", ex, new Dictionary<string, object> { ["orderId"] = 17 });
        var compact = new JsonFormatter().Format(evt);
        var pretty = new JsonFormatter(pretty: true).Format(evt);
        Assert.DoesNotContain("\n", compact, StringComparison.Ordinal);
        Assert.Contains("\n", pretty, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(compact);
        var node = doc.RootElement.GetProperty("exception");
        Assert.Equal(ex.GetType().FullName, node.GetProperty("type").GetString());
        Assert.Equal(ex.Message, node.GetProperty("message").GetString());
        Assert.Equal(ex.StackTrace, node.GetProperty("stackTrace").GetString());
        Assert.Equal("CheckoutService.Pay", node.GetProperty("source").GetProperty("method").GetString());
        Assert.Equal("CheckoutService.cs", node.GetProperty("source").GetProperty("file").GetString());
        Assert.Equal(42, node.GetProperty("source").GetProperty("line").GetInt32());
        Assert.False(node.TryGetProperty("inner", out _));
        Assert.DoesNotContain("\\", node.GetProperty("source").GetProperty("file").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/", node.GetProperty("source").GetProperty("file").GetString(), StringComparison.Ordinal);

        var plain = new InvalidOperationException("a", new InvalidOperationException("b"));
        var plainJson = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", plain));
        using var plainDoc = JsonDocument.Parse(plainJson);
        var plainNode = plainDoc.RootElement.GetProperty("exception");
        Assert.False(plainNode.TryGetProperty("stackTrace", out _));
        Assert.False(plainNode.TryGetProperty("source", out _));
        Assert.Equal("b", plainNode.GetProperty("inner")[0].GetProperty("message").GetString());
        Assert.False(plainNode.GetProperty("inner")[0].TryGetProperty("source", out _));

        var longMessage = new string('x', 600) + "|";
        var longJson = new JsonFormatter().Format(new LogEvent(
            CoverageClock.UtcNow,
            LogLevel.Error,
            "m",
            new InvalidOperationException(longMessage)));
        using var longDoc = JsonDocument.Parse(longJson);
        Assert.Equal(longMessage, longDoc.RootElement.GetProperty("exception").GetProperty("message").GetString());
    }

    /// <summary>
    /// JSON keeps at most 16 inner entries. An entry with no stack omits <c>source</c>.
    /// The top-level <c>source</c> is the innermost exception that has a stack, which can differ from an entry's own source.
    /// A chain longer than 16 also locks the text walk: 18 nodes show <c>(+13 more)</c>, not the true remainder.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Json_InnerCapAndEntrySource()
    {
        await Task.CompletedTask;
        var deep = Catch(() => SystemsCorp.Billing.Charge());
        var mid = new InvalidOperationException("mid", deep);
        var outer = Catch(() => throw new InvalidOperationException("outer", mid));
        var text = ExceptionReport.FormatText(outer);
        Assert.Contains("InvalidOperationException: outer --> InvalidOperationException: mid --> InvalidOperationException: corp", text, StringComparison.Ordinal);
        Assert.Contains("at Billing.Charge in Billing.cs:11", text, StringComparison.Ordinal);

        var json = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", outer));
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("exception");
        Assert.Equal("Billing.Charge", node.GetProperty("source").GetProperty("method").GetString());
        var inner = node.GetProperty("inner");
        Assert.Equal(2, inner.GetArrayLength());
        Assert.Equal("mid", inner[0].GetProperty("message").GetString());
        Assert.False(inner[0].TryGetProperty("source", out _));
        Assert.Equal("corp", inner[1].GetProperty("message").GetString());
        Assert.Equal("Billing.Charge", inner[1].GetProperty("source").GetProperty("method").GetString());

        Exception current = new InvalidOperationException("m17");
        for (var i = 16; i >= 0; i--)
            current = new InvalidOperationException("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), current);
        var capped = ExceptionReport.FormatText(current);
        Assert.Contains("InvalidOperationException: m0 --> InvalidOperationException: m1 --> InvalidOperationException: m2 (+13 more)", capped, StringComparison.Ordinal);
        Assert.DoesNotContain(": m3", capped, StringComparison.Ordinal);
        var cappedJson = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", current));
        using var cappedDoc = JsonDocument.Parse(cappedJson);
        var cappedInner = cappedDoc.RootElement.GetProperty("exception").GetProperty("inner");
        Assert.Equal(16, cappedInner.GetArrayLength());
        Assert.Equal("m1", cappedInner[0].GetProperty("message").GetString());
        Assert.Equal("m16", cappedInner[15].GetProperty("message").GetString());
        Assert.DoesNotContain("\"m17\"", cappedJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// A throwing message getter fills the message slot and does not throw.
    /// A throwing stack getter omits <c>at</c> and still returns the line.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Robustness_ThrowingMessageGetterAndFrameAccess()
    {
        await Task.CompletedTask;
        var message = ExceptionReport.FormatText(new ThrowingMessage());
        Assert.Equal(typeof(ThrowingMessage).FullName + ": [capture failed: InvalidOperationException]", message);
        var body = ExceptionReport.CreateBody(new ThrowingMessage());
        Assert.Equal("[capture failed: InvalidOperationException]", body.Message);

        var stack = Catch(() => throw new ThrowingStack());
        var text = ExceptionReport.FormatText(stack);
        Assert.Contains("ThrowingStack: still-here", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
        Assert.Null(ExceptionReport.CreateBody(stack).StackTrace);
        Assert.Null(ExceptionReport.CreateBody(stack).Source);
    }

    /// <summary>
    /// <see cref="Exception.ToString"/> runs only for <c>PlainTextFormatter(includeStackTrace: true)</c>.
    /// The default line stays one line. JSON and HTTP do not call it. A throw becomes an indented placeholder.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Robustness_ToStringOnlyWhenRequested()
    {
        await Task.CompletedTask;
        var counted = new CountingToString("ok");
        var evt = new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", counted);
        var plain = new PlainTextFormatter().Format(evt);
        Assert.Equal(0, counted.Calls);
        Assert.DoesNotContain("\n", plain, StringComparison.Ordinal);
        Assert.Contains("CountingToString: ok", plain, StringComparison.Ordinal);

        var withStack = new PlainTextFormatter(includeStackTrace: true).Format(evt);
        Assert.Equal(1, counted.Calls);
        Assert.Contains("\n    LINE1\n    LINE2", withStack, StringComparison.Ordinal);

        _ = new JsonFormatter().Format(evt);
        Assert.Equal(1, counted.Calls);

        var throwing = new ThrowingToString("kept");
        var safe = new PlainTextFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", throwing));
        Assert.Contains("ThrowingToString: kept", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("capture failed", safe, StringComparison.Ordinal);
        var indented = new PlainTextFormatter(includeStackTrace: true).Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", throwing));
        Assert.Contains("ThrowingToString: kept", indented, StringComparison.Ordinal);
        Assert.Contains("\n    [capture failed: InvalidOperationException]", indented, StringComparison.Ordinal);
    }

    /// <summary>An inner exception that points at itself is rendered once. The walk does not spin.</summary>
    [Fact(Timeout = 30000)]
    public async Task Robustness_CycleStops()
    {
        await Task.CompletedTask;
        var ex = new InvalidOperationException("loop");
        var field = typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(ex, ex);
        var text = ExceptionReport.FormatText(ex);
        Assert.Equal("InvalidOperationException: loop", text);
        Assert.DoesNotContain("-->", text, StringComparison.Ordinal);
        var body = ExceptionReport.CreateBody(ex);
        Assert.True(body.Inner == null || body.Inner.Count == 0);
    }

    /// <summary>
    /// Plain text, the console fallback, and the file fallback contain the same exception segment.
    /// <c>JsonFormatter</c> and the HTTP default body agree on the exception object.
    /// Context escaping is the same on every text sink.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Sinks_TextAndJsonAgree()
    {
        var ex = Catch(() => throw new InvalidOperationException("wrap", Catch(() => ALog.QuickstartSample.CheckoutService.Pay())));
        var evt = new LogEvent(
            new DateTime(2026, 10, 21, 20, 55, 19),
            LogLevel.Error,
            "Payment failed",
            ex,
            new Dictionary<string, object> { ["orderId"] = "17|a --> b" });
        var segment = ExceptionReport.FormatText(ex);
        Assert.Contains("InvalidOperationException: wrap --> InvalidOperationException: Card declined", segment, StringComparison.Ordinal);
        Assert.Contains("at CheckoutService.Pay in CheckoutService.cs:42", segment, StringComparison.Ordinal);

        var plain = new PlainTextFormatter("HH:mm:ss").Format(evt);
        Assert.Contains(segment, plain, StringComparison.Ordinal);
        Assert.Contains("ctx: orderId=17\\|a --\\> b", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("EX:", plain, StringComparison.Ordinal);

        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var console = new ConsoleLogWriter(useColors: false);
            console.Write(evt);
        }
        finally
        {
            Console.SetOut(previous);
        }

        var consoleText = buffer.ToString();
        Assert.Contains(segment, consoleText, StringComparison.Ordinal);
        Assert.Contains("ctx: orderId=17\\|a --\\> b", consoleText, StringComparison.Ordinal);
        Assert.DoesNotContain("EX:", consoleText, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "alog-exf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "app.log");
            using (var file = new FileLogWriter(path))
                file.Write(evt);
            var fileText = await File.ReadAllTextAsync(path);
            Assert.Contains(segment, fileText, StringComparison.Ordinal);
            Assert.Contains("ctx: orderId=17\\|a --\\> b", fileText, StringComparison.Ordinal);
            Assert.DoesNotContain("EX:", fileText, StringComparison.Ordinal);
            Assert.Single(fileText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        var json = new JsonFormatter().Format(evt);
        var handler = new CapturingHttpHandler();
        using var client = new HttpClient(handler);
        using var http = new HttpLogWriter("https://example.test/logs", httpClient: client);
        http.Write(evt);
        var httpBody = handler.Requests[0].Body;
        Assert.DoesNotContain("\n", httpBody, StringComparison.Ordinal);
        AssertEqualException(json, httpBody);
    }

    /// <summary>
    /// Bounds and rejected compiler names are part of the helper, not only the happy path.
    /// A stack string with no frames omits <c>at</c> and <c>source</c>. A nested aggregate is expanded in JSON.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Bounds_ParsersAndNestedAggregate()
    {
        await Task.CompletedTask;
        Assert.Equal(string.Empty, ExceptionReport.Escape(string.Empty, true));
        Assert.Equal(string.Empty, ExceptionReport.Escape(string.Empty, false));
        Assert.Equal("only", ExceptionReport.JoinLine("   ", "only", " ", null));
        Assert.Null(ExceptionReport.FormatAt(Array.Empty<FrameView>()));
        Assert.Equal(
            "Exception: Exception of type 'A'B' was thrown.",
            ExceptionReport.FormatText(new Exception("Exception of type 'A'B' was thrown.")));

        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod("<", "MoveNext"));
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod("<>d__1", "MoveNext"));
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod("<X>", "MoveNext"));
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod("<X>x__1", "MoveNext"));
        Assert.Equal("MoveNext", ExceptionReport.RenameCompilerMethod("<X>d__a", "MoveNext"));
        Assert.Equal("<M>b__x_1", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>b__x_1"));
        Assert.Equal("<M>b__1_x", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>b__1_x"));
        Assert.Equal("<M>g__|1_1", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>g__|1_1"));
        Assert.Equal("<M>g__N|x_1", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>g__N|x_1"));
        Assert.Equal("<M>g__N|1_x", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>g__N|1_x"));
        Assert.Equal("<M>g__N|1_", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>g__N|1_"));

        var frames = new FrameView[65];
        for (var i = 0; i < frames.Length; i++)
            frames[i] = new FrameView("ALog", "System", "X", "M" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, 0);
        frames[64] = new FrameView("App", "App", "User", "Run", "User.cs", 3);
        Assert.Equal(0, ExceptionReport.SelectFrame(frames));
        Assert.Equal("at X.M0", ExceptionReport.FormatAt(frames));

        var wide = new Dictionary<string, object>();
        for (var i = 0; i < 4097; i++)
            wide.Add("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "v");
        var ctx = ExceptionReport.FormatContext(wide);
        Assert.NotNull(ctx);
        Assert.Contains("k0=v", ctx, StringComparison.Ordinal);
        Assert.DoesNotContain("k4096=", ctx, StringComparison.Ordinal);
        var eight = new Dictionary<string, object>();
        for (var i = 0; i < 8; i++)
            eight.Add("e" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i);
        Assert.Contains("e7=7", ExceptionReport.FormatContext(eight), StringComparison.Ordinal);

        var nested = new InvalidOperationException(
            "outer",
            new AggregateException(new IOException("a"), new TimeoutException("b")));
        Assert.Equal(
            "InvalidOperationException: outer --> AggregateException --> IOException: a; TimeoutException: b",
            ExceptionReport.FormatText(nested));
        var nestedBody = ExceptionReport.CreateBody(nested);
        Assert.NotNull(nestedBody.Inner);
        Assert.Collection(
            nestedBody.Inner,
            item => Assert.Equal(typeof(IOException).FullName, item.Type),
            item => Assert.Equal(typeof(TimeoutException).FullName, item.Type));

        var painted = new InvalidOperationException("has-string");
        var field = typeof(Exception).GetField("_stackTraceString", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(painted, "   not a real stack   ");
        Assert.Equal("InvalidOperationException: has-string", ExceptionReport.FormatText(painted));
        Assert.Null(ExceptionReport.CreateBody(painted).Source);
        Assert.Equal("   not a real stack   ", ExceptionReport.CreateBody(painted).StackTrace);
    }

    /// <summary>Product source no longer contains the old <c>EX:</c> label.</summary>
    [Fact(Timeout = 30000)]
    public async Task ProductCode_HasNoExLabel()
    {
        await Task.CompletedTask;
        var root = RepoRoot();
        var hits = 0;
        var files = 0;
        foreach (var project in new[] { "ALog", "ALog.Extensions.Logging" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (files == 5000)
                    break;
                files++;
                var name = file.Replace('\\', '/');
                if (name.Contains("/obj/", StringComparison.Ordinal) || name.Contains("/bin/", StringComparison.Ordinal))
                    continue;
                var text = File.ReadAllText(file);
                if (text.Contains("EX:", StringComparison.Ordinal))
                    hits++;
            }
        }

        Assert.Equal(0, hits);
    }

    /// <summary>
    /// README console and formatter sections, and the changelog bullets, use the decided sentences.
    /// Quick Start step 1 and step 2 stay the sample that the documentation pull request owns.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Docs_LockExceptionFormatText()
    {
        await Task.CompletedTask;
        var root = RepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        var console = Slice(readme, "## Console output", "## Quick Start");
        var formatters = Slice(readme, "## Formatters", "## Contextual Logging");
        Assert.Contains("[20:55:19] [Error] " + TargetTail, console, StringComparison.Ordinal);
        Assert.Contains("EX:", console, StringComparison.Ordinal);
        Assert.Contains("includeStackTrace: true", console, StringComparison.Ordinal);
        Assert.Contains("Type: Message", formatters, StringComparison.Ordinal);
        Assert.Contains("source", formatters, StringComparison.Ordinal);
        Assert.Contains("inner", formatters, StringComparison.Ordinal);
        Assert.Contains("Log.Write(new Exception(\"Test failure\"), \"Something went wrong\", LogLevel.Error);", readme, StringComparison.Ordinal);
        Assert.Contains("new PlainTextFormatter(\"HH:mm:ss\")", readme, StringComparison.Ordinal);
        Assert.Contains("### Breaking (output)", changelog, StringComparison.Ordinal);
        Assert.Contains(BreakingBullet, changelog, StringComparison.Ordinal);
        Assert.Contains("### Added", changelog, StringComparison.Ordinal);
        Assert.Contains(AddedSourceBullet, changelog, StringComparison.Ordinal);
        Assert.Contains(AddedStackBullet, changelog, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the top-level probe once so its captured exceptions have real stacks.
    /// Later calls do nothing, because the entry point is not safe to run twice in one process.
    /// </summary>
    /// <returns>A completed task after <c>&lt;Main&gt;$</c> has stored the exceptions.</returns>
    private static async Task RunTopLevelAsync()
    {
        if (ExfTopLevel.TopLevelCapture.Awaited != null)
            return;

        var program = typeof(ExfTopLevel.TopLevelCapture).Assembly.GetType("Program", throwOnError: true);
        MethodInfo? entry = null;
        var methods = program!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (var i = 0; i < methods.Length; i++)
        {
            if (methods[i].Name == "<Main>$")
            {
                entry = methods[i];
                break;
            }
        }

        Assert.NotNull(entry);
        var parameters = entry.GetParameters();
        object? result = parameters.Length == 0
            ? entry.Invoke(null, null)
            : entry.Invoke(null, new object[] { Array.Empty<string>() });
        if (result is Task task)
            await task;
        Assert.NotNull(ExfTopLevel.TopLevelCapture.Awaited);
    }

    /// <summary>The event behind the README example, including the thrown card exception and one context value.</summary>
    /// <returns>The event. The exception has a stack and a PDB line.</returns>
    private static LogEvent PaymentEvent()
    {
        return new LogEvent(
            new DateTime(2026, 10, 21, 20, 55, 19),
            LogLevel.Error,
            "Payment failed",
            Catch(() => ALog.QuickstartSample.CheckoutService.Pay()),
            new Dictionary<string, object> { ["orderId"] = 17 });
    }

    /// <summary>Runs an action and returns the exception it threw.</summary>
    /// <param name="body">Code that throws. Not null.</param>
    /// <returns>The caught exception.</returns>
    private static Exception Catch(Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The action returned without throwing.");
    }

    /// <summary>Awaits a task and returns the exception it faulted with.</summary>
    /// <param name="body">Task that faults. Not null.</param>
    /// <returns>The task exception.</returns>
    private static async Task<Exception> CatchAsync(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The task completed without throwing.");
    }

    /// <summary>Reverses <see cref="ExceptionReport.Escape"/> so a round trip can prove the separators.</summary>
    /// <param name="text">Escaped text. Not null.</param>
    /// <returns>The text before escaping.</returns>
    private static string Unescape(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        var steps = 0;
        while (i < text.Length && steps < 20000)
        {
            steps++;
            if (i + 3 < text.Length
                && text[i] == '-'
                && text[i + 1] == '-'
                && text[i + 2] == '\\'
                && text[i + 3] == '>')
            {
                builder.Append("-->");
                i += 4;
                continue;
            }

            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '\\')
                {
                    builder.Append('\\');
                    i += 2;
                    continue;
                }

                if (next == '|')
                {
                    builder.Append('|');
                    i += 2;
                    continue;
                }

                if (next == 'r')
                {
                    builder.Append('\r');
                    i += 2;
                    continue;
                }

                if (next == 'n')
                {
                    builder.Append('\n');
                    i += 2;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Splits a plain line on <c> | </c> whose pipe is not escaped.</summary>
    /// <param name="line">One formatted line. Not null.</param>
    /// <returns>The segments. At least one.</returns>
    private static List<string> SplitSegments(string line)
    {
        var parts = new List<string>();
        var start = 0;
        var i = 0;
        var steps = 0;
        while (i + 2 < line.Length && steps < 20000)
        {
            steps++;
            if (line[i] == ' ' && line[i + 1] == '|' && line[i + 2] == ' ' && !IsEscaped(line, i + 1))
            {
                parts.Add(line.Substring(start, i - start));
                start = i + 3;
                i = start;
                continue;
            }

            i++;
        }

        parts.Add(line.Substring(start));
        return parts;
    }

    /// <summary>True when an odd number of backslashes sits immediately before the index.</summary>
    /// <param name="text">Line being scanned. Not null.</param>
    /// <param name="index">Index of the pipe.</param>
    /// <returns>True when the pipe is an escape, not a separator.</returns>
    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        var i = index - 1;
        var steps = 0;
        while (i >= 0 && text[i] == '\\' && steps < 32)
        {
            slashes++;
            i--;
            steps++;
        }

        return slashes % 2 == 1;
    }

    /// <summary>Compares the exception object in two JSON documents field by field.</summary>
    /// <param name="left">JsonFormatter output. Not null.</param>
    /// <param name="right">HTTP default body. Not null.</param>
    private static void AssertEqualException(string left, string right)
    {
        using var leftDoc = JsonDocument.Parse(left);
        using var rightDoc = JsonDocument.Parse(right);
        AssertEqualNode(leftDoc.RootElement.GetProperty("exception"), rightDoc.RootElement.GetProperty("exception"));
    }

    /// <summary>Compares one exception node, including a missing source and each inner entry.</summary>
    /// <param name="left">Left exception object.</param>
    /// <param name="right">Right exception object.</param>
    private static void AssertEqualNode(JsonElement left, JsonElement right)
    {
        Assert.Equal(ReadString(left, "type"), ReadString(right, "type"));
        Assert.Equal(ReadString(left, "message"), ReadString(right, "message"));
        Assert.Equal(ReadString(left, "stackTrace"), ReadString(right, "stackTrace"));
        AssertEqualSource(left, right);
        var leftInner = left.TryGetProperty("inner", out var leftArray);
        var rightInner = right.TryGetProperty("inner", out var rightArray);
        Assert.Equal(leftInner, rightInner);
        if (!leftInner)
            return;
        Assert.Equal(leftArray.GetArrayLength(), rightArray.GetArrayLength());
        var count = leftArray.GetArrayLength();
        for (var i = 0; i < count && i < 16; i++)
            AssertEqualNode(leftArray[i], rightArray[i]);
    }

    /// <summary>Compares <c>source</c> on two nodes. Both must omit it, or both must carry the same method, file, and line.</summary>
    /// <param name="left">Left node.</param>
    /// <param name="right">Right node.</param>
    private static void AssertEqualSource(JsonElement left, JsonElement right)
    {
        var leftHas = left.TryGetProperty("source", out var leftSource);
        var rightHas = right.TryGetProperty("source", out var rightSource);
        Assert.Equal(leftHas, rightHas);
        if (!leftHas)
            return;
        Assert.Equal(ReadString(leftSource, "method"), ReadString(rightSource, "method"));
        Assert.Equal(ReadString(leftSource, "file"), ReadString(rightSource, "file"));
        Assert.Equal(ReadNumber(leftSource, "line"), ReadNumber(rightSource, "line"));
    }

    /// <summary>Reads a string property. A missing property and JSON null are both null.</summary>
    /// <param name="element">Object to read.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The string, or null.</returns>
    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.GetString();
    }

    /// <summary>Reads a numeric property. A missing property is null.</summary>
    /// <param name="element">Object to read.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The number, or null.</returns>
    private static int? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.GetInt32();
    }

    /// <summary>Walks at most eight parents looking for <c>ALog.sln</c>.</summary>
    /// <returns>The repository root.</returns>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ALog.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("ALog.sln was not found above the test output.");
    }

    /// <summary>Returns the text between two headings, including the start heading.</summary>
    /// <param name="text">Full document. Not null.</param>
    /// <param name="start">Start heading. Not null.</param>
    /// <param name="end">Heading that ends the slice. Not null.</param>
    /// <returns>The slice.</returns>
    private static string Slice(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return text.Substring(from, to - from);
    }

    /// <summary>Exception with no message of its own, so the framework sentence must be recognized.</summary>
    private sealed class SilentException : Exception
    {
    }

    /// <summary>Exception whose message is empty.</summary>
    private sealed class EmptyMessage : Exception
    {
        /// <summary>Empty on purpose, so the line shows the type only.</summary>
        public override string Message => string.Empty;
    }

    /// <summary>Exception whose message getter returns null.</summary>
    private sealed class NullMessage : Exception
    {
        /// <summary>Null on purpose. The formatter treats it as no message.</summary>
        public override string Message => null!;
    }

    /// <summary>Exception whose message is only whitespace, including a newline.</summary>
    private sealed class WhitespaceMessage : Exception
    {
        /// <summary>Whitespace on purpose.</summary>
        public override string Message => " \t\r\n ";
    }

    /// <summary>Exception whose message getter throws.</summary>
    private sealed class ThrowingMessage : Exception
    {
        /// <summary>Always throws, so the formatter must catch it.</summary>
        public override string Message => throw new InvalidOperationException("no-message");
    }

    /// <summary>Exception whose stack getter throws.</summary>
    private sealed class ThrowingStack : Exception
    {
        /// <summary>Creates an exception with a readable message and a broken stack getter.</summary>
        public ThrowingStack()
            : base("still-here")
        {
        }

        /// <summary>Always throws, so the origin must be omitted.</summary>
        public override string? StackTrace => throw new InvalidOperationException("no-stack");
    }

    /// <summary>Counts <see cref="ToString"/> so the default formatter can be shown not to call it.</summary>
    private sealed class CountingToString : Exception
    {
        /// <summary>Creates an exception with a stable message.</summary>
        /// <param name="message">Message text.</param>
        public CountingToString(string message)
            : base(message)
        {
        }

        /// <summary>How many times <see cref="ToString"/> has run.</summary>
        public int Calls { get; private set; }

        /// <summary>Returns two lines and counts the call.</summary>
        /// <returns><c>LINE1</c> and <c>LINE2</c>.</returns>
        public override string ToString()
        {
            Calls++;
            return "LINE1\nLINE2";
        }
    }

    /// <summary>Exception whose <see cref="ToString"/> throws.</summary>
    private sealed class ThrowingToString : Exception
    {
        /// <summary>Creates an exception whose message can still be read.</summary>
        /// <param name="message">Message text.</param>
        public ThrowingToString(string message)
            : base(message)
        {
        }

        /// <summary>Always throws.</summary>
        /// <returns>Nothing. The method throws.</returns>
        public override string ToString() => throw new InvalidOperationException("no-tostring");
    }
}
