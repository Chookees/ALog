using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALog.Core;
using ALog.Formatters;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Tests.Naming;
using ALog.Writers.Http;

namespace ALog.Tests;

/// <summary>
/// Locks the plain-text and JSON exception shape: one helper, the five sinks, and the case list from uiux §5c.
/// </summary>
public sealed class ExceptionFormatTests
{
    private const string TargetTail =
        "Payment failed | InvalidOperationException: Card declined | at CheckoutService.Pay in CheckoutService.cs:42 | ctx: orderId=17";

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
        Assert.Equal(
            "SystemsCorp.FooException: bill",
            ExceptionReport.FormatText(new SystemsCorp.FooException("bill")));
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
        Assert.Equal("ALog.Tests.ExceptionFormatTests.SilentException", ExceptionReport.FormatText(new SilentException()));
        Assert.Equal("ALog.Tests.ExceptionFormatTests.EmptyMessage", ExceptionReport.FormatText(new EmptyMessage()));
        Assert.Equal("ALog.Tests.ExceptionFormatTests.NullMessage", ExceptionReport.FormatText(new NullMessage()));
        Assert.Equal("ALog.Tests.ExceptionFormatTests.WhitespaceMessage", ExceptionReport.FormatText(new WhitespaceMessage()));
        Assert.DoesNotContain("+", ExceptionReport.FormatText(new SilentException()), StringComparison.Ordinal);
        Assert.Equal(typeof(SilentException).FullName, ExceptionReport.CreateBody(new SilentException()).Type);
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
    /// Newlines become <c>\r</c> and <c>\n</c>, Unicode stays, and the cut is before escaping.
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
        Assert.Equal(string.Concat(Enumerable.Repeat("\\|", 251)), pipes);
        Assert.DoesNotContain("[truncated]", pipes, StringComparison.Ordinal);
        Assert.Same("plain", ExceptionReport.EscapeLine("plain"));
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
    /// A backslash is doubled only when the next raw character would read as an escape.
    /// A backslash before a dash stays single, and the arrow that follows is still escaped.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EscapeOrder_BackslashBeforePipeAndArrow()
    {
        await Task.CompletedTask;
        Assert.Equal("\\|", ExceptionReport.Escape("|", messageLimit: true));
        Assert.Same("\\", ExceptionReport.Escape("\\", messageLimit: true));
        Assert.Equal("--\\>", ExceptionReport.Escape("-->", messageLimit: true));
        Assert.Equal("\\--\\>", ExceptionReport.Escape("\\-->", messageLimit: true));
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
        Assert.Contains("a \\| b | InvalidOperationException: a \\| b --\\> c\\d | props: p=a\\|b | ctx: k=x \\| y --\\> z", line, StringComparison.Ordinal);
        var segments = SplitSegments(line);
        Assert.Contains(segments, segment => segment.Contains("InvalidOperationException:", StringComparison.Ordinal));
        var exceptionSegment = segments.First(segment => segment.StartsWith("InvalidOperationException:", StringComparison.Ordinal));
        Assert.Equal("a | b --> c\\d", Unescape(exceptionSegment.Substring("InvalidOperationException: ".Length)));
        var ctx = segments.First(segment => segment.StartsWith("ctx:", StringComparison.Ordinal));
        Assert.Equal("x | y --> z", Unescape(ctx.Substring("ctx: k=".Length)));
        var props = segments.First(segment => segment.StartsWith("props:", StringComparison.Ordinal));
        Assert.Equal("a|b", Unescape(props.Substring("props: p=".Length)));
    }

    /// <summary>
    /// A backslash is doubled only when the next raw character would read as an escape.
    /// Plain text applies that rule to the category, the message, and context or property names and values.
    /// The console and file fallbacks escape the message and context and do not write the category.
    /// JSON keeps those characters raw.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Escape_BackslashDoublesOnlyWhereNeeded()
    {
        const string windows = "C:\\temp\\file.txt";
        const string newPath = "C:\\new";
        const string newReadme = "C:\\new\\readme";
        Assert.Same(windows, ExceptionReport.EscapeLine(windows));
        Assert.Equal("C:\\\\new", ExceptionReport.EscapeLine(newPath));
        Assert.Equal("C:\\\\new\\\\readme", ExceptionReport.EscapeLine(newReadme));
        Assert.Equal(windows, Unescape(ExceptionReport.EscapeLine(windows)));
        Assert.Equal(newPath, Unescape(ExceptionReport.EscapeLine(newPath)));
        Assert.Equal(newReadme, Unescape(ExceptionReport.EscapeLine(newReadme)));

        Assert.Equal("--\\\\>", ExceptionReport.EscapeLine("--\\>"));
        Assert.Equal("\\\\>", ExceptionReport.EscapeLine("\\>"));
        Assert.Equal("--\\>", Unescape(ExceptionReport.EscapeLine("--\\>")));
        Assert.Equal("\\>", Unescape(ExceptionReport.EscapeLine("\\>")));

        Assert.Equal("\\\\\\n", ExceptionReport.EscapeLine("\\\n"));
        Assert.Equal("\\\\\\r", ExceptionReport.EscapeLine("\\\r"));
        Assert.Equal("\\\n", Unescape(ExceptionReport.EscapeLine("\\\n")));
        Assert.Equal("\\\r", Unescape(ExceptionReport.EscapeLine("\\\r")));
        Assert.Equal("\\\\\\|", ExceptionReport.EscapeLine("\\|"));
        Assert.Equal("\\|", Unescape(ExceptionReport.EscapeLine("\\|")));
        Assert.Equal("\\|C:\\temp\\", ExceptionReport.EscapeLine("|C:\\temp\\"));
        Assert.Equal("|C:\\temp\\", Unescape(ExceptionReport.EscapeLine("|C:\\temp\\")));

        var time = new DateTime(2026, 10, 21, 20, 55, 19);
        var trailing = new LogEvent(time, LogLevel.Error, "pay\\", null, new Dictionary<string, object> { ["k"] = "v" });
        var trailingLine = new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(trailing);
        Assert.Equal("[2026-10-21 20:55:19] [Error] pay\\ | ctx: k=v", trailingLine);
        Assert.Equal("pay\\", Unescape(SplitSegments(trailingLine)[0].Substring("[2026-10-21 20:55:19] [Error] ".Length)));

        var exact = new string('x', 498) + "\\n";
        Assert.Equal(500, exact.Length);
        var exactEscaped = ExceptionReport.Escape(exact, messageLimit: true);
        Assert.Equal(new string('x', 498) + "\\\\n", exactEscaped);
        Assert.DoesNotContain("[truncated]", exactEscaped, StringComparison.Ordinal);
        Assert.Equal(exact, Unescape(exactEscaped));

        var boundary = new string('x', 498) + "\\ny";
        var boundaryEscaped = ExceptionReport.Escape(boundary, messageLimit: true);
        Assert.Equal(new string('x', 498) + "\\\\n [truncated]", boundaryEscaped);
        Assert.Equal(new string('x', 498) + "\\n", Unescape(boundaryEscaped.Substring(0, boundaryEscaped.Length - " [truncated]".Length)));

        var endSlash = new string('x', 499) + "\\|";
        var endEscaped = ExceptionReport.Escape(endSlash, messageLimit: true);
        Assert.Equal(new string('x', 499) + "\\ [truncated]", endEscaped);
        Assert.Equal(new string('x', 499) + "\\", Unescape(endEscaped.Substring(0, endEscaped.Length - " [truncated]".Length)));

        const string category = "C:\\new | forged\n";
        var forged = new LogEvent(
            time,
            LogLevel.Error,
            windows,
            new InvalidOperationException("boom"),
            new Dictionary<string, object> { ["k"] = newPath },
            new Dictionary<string, object> { [newReadme] = "--\\>" },
            Category: category);
        const string plainLine =
            "[2026-10-21 20:55:19] [Error] [C:\\\\new \\| forged\\n] C:\\temp\\file.txt | InvalidOperationException: boom | props: C:\\\\new\\\\readme=--\\\\> | ctx: k=C:\\\\new";
        var plain = new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(forged);
        Assert.Equal(plainLine, plain);
        Assert.DoesNotContain("\n", plain, StringComparison.Ordinal);
        Assert.DoesNotContain(" | forged", plain, StringComparison.Ordinal);
        var categoryAt = plain.IndexOf("] [Error] [", StringComparison.Ordinal) + "] [Error] [".Length;
        var categoryEnd = plain.IndexOf("] ", categoryAt, StringComparison.Ordinal);
        Assert.Equal(category, Unescape(plain.Substring(categoryAt, categoryEnd - categoryAt)));
        Assert.Equal(newPath, Unescape(plain.Substring(plain.IndexOf("ctx: k=", StringComparison.Ordinal) + "ctx: k=".Length)));

        var noException = forged with { Exception = null, Context = null, Properties = null, Message = newPath };
        Assert.Equal(
            "[2026-10-21 20:55:19] [Error] [C:\\\\new \\| forged\\n] C:\\\\new",
            new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(noException));

        const string fallback =
            "[2026-10-21 20:55:19] [Error] C:\\temp\\file.txt | InvalidOperationException: boom | ctx: k=C:\\\\new";
        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var console = new ConsoleLogWriter(useColors: false);
            console.Write(forged);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(fallback + Environment.NewLine, buffer.ToString());
        Assert.DoesNotContain("props:", buffer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[C:\\\\new", buffer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\n-->", buffer.ToString(), StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "alog-exf-slash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "app.log");
            using (var file = new FileLogWriter(path))
                file.Write(forged);
            Assert.Equal(fallback + Environment.NewLine, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var console = new ConsoleLogWriter(useColors: false, formatter: new PlainTextFormatter("yyyy-MM-dd HH:mm:ss"));
            console.Write(forged);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(plainLine + Environment.NewLine, buffer.ToString());

        using var document = JsonDocument.Parse(new JsonFormatter().Format(forged));
        Assert.Equal(category, document.RootElement.GetProperty("category").GetString());
        Assert.Equal(windows, document.RootElement.GetProperty("message").GetString());
        Assert.Equal(newPath, document.RootElement.GetProperty("context").GetProperty("k").GetString());
        Assert.Equal("--\\>", document.RootElement.GetProperty("properties").GetProperty(newReadme).GetString());

        AssertEscapeAlphabet();
    }

    /// <summary>
    /// A <c>props:</c> value is escaped in the same order as a context value.
    /// Plain text, the console writer, and the file writer share that line. JSON keeps the raw value.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Props_ValuesEscapeLikeContext()
    {
        await Task.CompletedTask;
        Assert.Null(ExceptionReport.FormatProps(null));
        Assert.Null(ExceptionReport.FormatProps(new Dictionary<string, object>()));
        const string raw = "a \\| b --> c\r\n";
        var evt = new LogEvent(
            new DateTime(2026, 10, 21, 20, 55, 19),
            LogLevel.Error,
            "pay",
            null,
            new Dictionary<string, object> { ["k"] = raw },
            new Dictionary<string, object> { ["note"] = raw });
        const string line =
            "[2026-10-21 20:55:19] [Error] pay | props: note=a \\\\\\| b --\\> c\\r\\n | ctx: k=a \\\\\\| b --\\> c\\r\\n";
        Assert.Equal(line, new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(evt));
        Assert.Equal(raw, Unescape(line.Substring(line.IndexOf("note=", StringComparison.Ordinal) + "note=".Length).Split(" | ", 2)[0]));

        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            using var console = new ConsoleLogWriter(useColors: false, formatter: new PlainTextFormatter("yyyy-MM-dd HH:mm:ss"));
            console.Write(evt);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(line + Environment.NewLine, buffer.ToString());

        var directory = Path.Combine(Path.GetTempPath(), "alog-exf-props-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "app.log");
            using (var file = new FileLogWriter(path, new PlainTextFormatter("yyyy-MM-dd HH:mm:ss")))
                file.Write(evt);
            Assert.Equal(line + Environment.NewLine, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        var readOnly = evt with
        {
            Properties = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(
                new Dictionary<string, object> { ["note"] = raw }),
            Context = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(
                new Dictionary<string, object> { ["k"] = raw })
        };
        Assert.Equal(line, new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(readOnly));

        const string forgeKey = "user=bob | forged\n";
        const string forgeEscaped = "user=bob \\| forged\\n";
        var forged = evt with
        {
            Properties = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(
                new Dictionary<string, object> { [forgeKey] = "v" }),
            Context = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(
                new Dictionary<string, object> { [forgeKey] = "v" })
        };
        var forgedLine = new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(forged);
        Assert.Contains("props: " + forgeEscaped + "=v", forgedLine, StringComparison.Ordinal);
        Assert.Contains("ctx: " + forgeEscaped + "=v", forgedLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", forgedLine, StringComparison.Ordinal);
        Assert.DoesNotContain(" | forged", forgedLine, StringComparison.Ordinal);

        var json = new JsonFormatter().Format(evt);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(raw, document.RootElement.GetProperty("properties").GetProperty("note").GetString());
        Assert.Equal(raw, document.RootElement.GetProperty("context").GetProperty("k").GetString());
        Assert.DoesNotContain("--\\>", json, StringComparison.Ordinal);

        var longMessage = new string('m', 501);
        var longLine = new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(new LogEvent(
            new DateTime(2026, 10, 21, 20, 55, 19),
            LogLevel.Error,
            longMessage,
            null));
        Assert.Contains(longMessage, longLine, StringComparison.Ordinal);
        Assert.DoesNotContain("[truncated]", longLine, StringComparison.Ordinal);
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
    /// Starts the top-level probe as its own process so <c>&lt;Main&gt;$</c> is the real entry point.
    /// A test library cannot host top-level statements, and reflecting the method is a different call.
    /// The child prints one console line. The origin is <c>at Program.Main in Program.cs:10</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_TopLevelProcessPrintsMain()
    {
        var exeName = OperatingSystem.IsWindows() ? "ExfTopLevel.exe" : "ExfTopLevel";
        var exe = Path.Combine(AppContext.BaseDirectory, exeName);
        Assert.True(File.Exists(exe), exe);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.StartInfo.Environment["EXF_TOPLEVEL_PRINT"] = "1";
        process.StartInfo.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        Assert.True(process.Start());
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(20000);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!exited)
            process.Kill(entireProcessTree: true);
        Assert.True(exited, stderr);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(
            "[2026-10-21 20:55:19] [Error] Payment failed | InvalidOperationException: Card declined | at Program.Main in Program.cs:10" + Environment.NewLine,
            stdout);
        Assert.Equal(string.Empty, stderr);
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
        Assert.True(ExceptionReport.IsSkipped("System.Private.CoreLib", null));
        Assert.True(ExceptionReport.IsSkipped("System.Private.CoreLib", string.Empty));
        Assert.True(ExceptionReport.IsSkipped("System.Private.CoreLib.Extra", null));
        Assert.Equal(-1, ExceptionReport.SelectFrame(Array.Empty<FrameView>()));

        var thrown = Catch(() => _ = new FileLogWriter(null!));
        var text = ExceptionReport.FormatText(thrown);
        Assert.Contains("ArgumentNullException:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FileLogWriter", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Origin_SkipsALogAssemblyAndFrameworkNamespace), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>File.ReadAllText</c> fails inside <c>Interop</c>, which has no namespace.
    /// That frame is still runtime code because its assembly is <c>System.Private.CoreLib</c>,
    /// so the line names <c>CheckoutService.ReadIo</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_CoreLibInteropUsesUserFrame()
    {
        await Task.CompletedTask;
        var frames = new[]
        {
            new FrameView("System.Private.CoreLib", null, "Interop", "ThrowExceptionForIoErrno", null, 0),
            new FrameView("System.Private.CoreLib", null, "OtherHelper", "Boom", null, 0),
            new FrameView("ALog.Tests", "ALog.QuickstartSample", "CheckoutService", "ReadIo", "Cls.cs", 7)
        };
        Assert.Equal("at CheckoutService.ReadIo in Cls.cs:7", ExceptionReport.FormatAt(frames));

        var io = Catch(() => ALog.QuickstartSample.CheckoutService.ReadIo());
        Assert.Equal(
            "DirectoryNotFoundException: Could not find a part of the path '/nope/y'. | at CheckoutService.ReadIo in Cls.cs:7",
            ExceptionReport.FormatText(io));
        Assert.DoesNotContain("Interop", ExceptionReport.FormatText(io), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowExceptionForIoErrno", ExceptionReport.FormatText(io), StringComparison.Ordinal);
    }

    /// <summary>
    /// An <see cref="HttpClient"/> call to a closed local port throws <see cref="HttpRequestException"/>
    /// whose inner <see cref="SocketException"/> has only framework frames.
    /// The origin walks outward to the user method. The port is chosen by binding and releasing it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Origin_ClosedPortUsesOuterUserFrame()
    {
        await Task.CompletedTask;
        var thrown = CallClosedPort();
        var text = ExceptionReport.FormatText(thrown);
        Assert.Contains("at ExceptionFormatTests.CallClosedPort in ExceptionFormatTests.cs:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowException", text, StringComparison.Ordinal);

        var body = ExceptionReport.CreateBody(thrown);
        Assert.NotNull(body.Source);
        Assert.Equal("ExceptionFormatTests.CallClosedPort", body.Source.Method);
        Assert.DoesNotContain("ThrowException", body.Source.Method, StringComparison.Ordinal);
        var json = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", thrown));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "ExceptionFormatTests.CallClosedPort",
            doc.RootElement.GetProperty("exception").GetProperty("source").GetProperty("method").GetString());
    }

    /// <summary>
    /// Calls <see cref="HttpClient.GetAsync(string)"/> on a port that was bound and then released.
    /// </summary>
    /// <returns>The <see cref="HttpRequestException"/> from the refused connection.</returns>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Exception CallClosedPort()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var client = new HttpClient();
            try
            {
                client.GetAsync("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/").GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                return ex;
            }
        }

        throw new InvalidOperationException("The closed port accepted the request.");
    }

    /// <summary>
    /// A runtime assembly is framework even when the type has no namespace.
    /// <c>System</c>, <c>System.*</c>, <c>Microsoft.*</c>, <c>mscorlib</c>, and <c>netstandard</c> are skipped.
    /// <c>SystemsCorp</c> and <c>Microsofty</c> are not, and the real IO failure still names <c>ReadIo</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Origin_FrameworkAssembliesSkipToUserFrame()
    {
        await Task.CompletedTask;
        var frames = new[]
        {
            new FrameView("System.Private.CoreLib", null, "Interop", "ThrowExceptionForIoErrno", null, 0),
            new FrameView("mscorlib", null, "Legacy", "Throw", null, 0),
            new FrameView("netstandard", null, "NetStd", "Throw", null, 0),
            new FrameView("System", null, "SystemHelper", "Throw", null, 0),
            new FrameView("Microsoft.Win32.Primitives", null, "Native", "Throw", null, 0),
            new FrameView("SystemsCorp", "SystemsCorp", "CheckoutService", "ReadIo", "Cls.cs", 7)
        };
        Assert.Equal("at CheckoutService.ReadIo in Cls.cs:7", ExceptionReport.FormatAt(frames));

        var microsofty = new[]
        {
            new FrameView("Microsoft.Extensions.Logging", null, "Logger", "Log", null, 0),
            new FrameView("Microsofty", "Microsofty", "App", "Run", "App.cs", 4)
        };
        Assert.Equal("at App.Run in App.cs:4", ExceptionReport.FormatAt(microsofty));

        Assert.True(ExceptionReport.IsFrameworkAssembly("System"));
        Assert.True(ExceptionReport.IsFrameworkAssembly("System.Runtime"));
        Assert.True(ExceptionReport.IsFrameworkAssembly("System.Private.CoreLib"));
        Assert.True(ExceptionReport.IsFrameworkAssembly("System.Private.CoreLib.Extra"));
        Assert.True(ExceptionReport.IsFrameworkAssembly("Microsoft.Extensions.Logging"));
        Assert.True(ExceptionReport.IsFrameworkAssembly("mscorlib"));
        Assert.True(ExceptionReport.IsFrameworkAssembly("netstandard"));
        Assert.False(ExceptionReport.IsFrameworkAssembly("SystemsCorp"));
        Assert.False(ExceptionReport.IsFrameworkAssembly("SystemExtra"));
        Assert.False(ExceptionReport.IsFrameworkAssembly("Microsofty"));
        Assert.False(ExceptionReport.IsFrameworkAssembly("Microsoft"));
        Assert.False(ExceptionReport.IsFrameworkAssembly("ALog.QuickstartSample"));
        Assert.False(ExceptionReport.IsFrameworkAssembly("ALogSample"));
        Assert.False(ExceptionReport.IsFrameworkAssembly(null));
        Assert.False(ExceptionReport.IsFrameworkAssembly(string.Empty));

        Assert.True(ExceptionReport.IsSkipped("System", null));
        Assert.True(ExceptionReport.IsSkipped("System.Runtime", "Contoso"));
        Assert.True(ExceptionReport.IsSkipped("Microsoft.X", null));
        Assert.True(ExceptionReport.IsSkipped("mscorlib", null));
        Assert.True(ExceptionReport.IsSkipped("netstandard", null));
        Assert.False(ExceptionReport.IsSkipped("SystemsCorp", "SystemsCorp"));
        Assert.False(ExceptionReport.IsSkipped("SystemExtra", null));
        Assert.False(ExceptionReport.IsSkipped("Microsofty", "Microsofty"));
        Assert.False(ExceptionReport.IsSkipped("Microsoft", null));
        Assert.False(ExceptionReport.IsSkipped("ALog.QuickstartSample", "ALog.QuickstartSample"));
        Assert.False(ExceptionReport.IsSkipped("ALogSample", "ALogSample"));
        Assert.False(ExceptionReport.IsSkipped(null, null));

        var io = Catch(() => ALog.QuickstartSample.CheckoutService.ReadIo());
        Assert.Equal(
            "DirectoryNotFoundException: Could not find a part of the path '/nope/y'. | at CheckoutService.ReadIo in Cls.cs:7",
            ExceptionReport.FormatText(io));
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
        Assert.DoesNotContain("(+1+ more)", ExceptionReport.FormatText(four), StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="AggregateException"/> drops its own message, flattens nested aggregates, and counts that flat list.
    /// Non-aggregate siblings are emitted before a nested aggregate is opened.
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
        Assert.Equal(
            "AggregateException --> IOException: c; InvalidOperationException: d; InvalidOperationException: a (+1 more)",
            ExceptionReport.FormatText(outer));
        Assert.DoesNotContain(": b", ExceptionReport.FormatText(outer), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nested aggregates follow <see cref="AggregateException.Flatten"/>: non-aggregate siblings first,
    /// nested aggregates later. Text and JSON <c>inner</c> use that order.
    /// Duplicate instances are kept. The trees are acyclic, because <c>Flatten()</c> has no cycle guard.
    /// One thousand random acyclic trees, from a fixed seed, match <c>Flatten().InnerExceptions</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_OrderMatchesFlatten()
    {
        await Task.CompletedTask;
        var x = new InvalidOperationException("x");
        var y = new InvalidOperationException("y");
        var z = new InvalidOperationException("z");
        var first = new AggregateException(x, new AggregateException(y), z);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: x; InvalidOperationException: z; InvalidOperationException: y",
            ExceptionReport.FormatText(first));
        AssertSameOrder(first, first.Flatten().InnerExceptions);

        var wide = new AggregateException(
            new AggregateException(new InvalidOperationException("a"), new InvalidOperationException("b")),
            new InvalidOperationException("c"),
            new AggregateException(
                new InvalidOperationException("d"),
                new AggregateException(new InvalidOperationException("e"))));
        var wideText = ExceptionReport.FormatText(wide);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: c; InvalidOperationException: a; InvalidOperationException: b (+2 more)",
            wideText);
        Assert.DoesNotContain(": d", wideText, StringComparison.Ordinal);
        Assert.DoesNotContain(": e", wideText, StringComparison.Ordinal);
        Assert.Contains("(+2 more)", wideText, StringComparison.Ordinal);
        Assert.DoesNotContain("(+2+ more)", wideText, StringComparison.Ordinal);
        AssertSameOrder(wide, wide.Flatten().InnerExceptions);

        var sharedLeaf = new InvalidOperationException("x");
        var duplicated = new AggregateException(sharedLeaf, sharedLeaf);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: x; InvalidOperationException: x",
            ExceptionReport.FormatText(duplicated));
        AssertSameOrder(duplicated, duplicated.Flatten().InnerExceptions);

        var sharedAggregate = new AggregateException(new InvalidOperationException("a"), new InvalidOperationException("b"));
        var sharedTree = new AggregateException(sharedAggregate, new InvalidOperationException("c"), sharedAggregate);
        AssertSameOrder(sharedTree, sharedTree.Flatten().InnerExceptions);
        var sharedBody = ExceptionReport.CreateBody(sharedTree);
        Assert.NotNull(sharedBody.Inner);
        Assert.Equal(5, sharedBody.Inner.Count);
        Assert.Equal(
            new[] { "c", "a", "b", "a", "b" },
            sharedBody.Inner.Select(item => item.Message).ToArray());

        var random = new Random(20261021);
        for (var trial = 0; trial < 1000; trial++)
        {
            var nextId = 0;
            var leavesLeft = 12;
            var aggsLeft = 5;
            var finished = new List<Exception>();
            var root = RandomAggregate(random, ref nextId, ref leavesLeft, ref aggsLeft, 0, finished);
            AssertSameOrder(root, root.Flatten().InnerExceptions);
        }
    }

    /// <summary>
    /// A binary DAG of shared aggregates is acyclic and would expand to 2^30 entries.
    /// The walk stops at 64 nodes, finishes quickly, and writes <c>(+n+ more)</c>.
    /// Text and JSON list the same entries.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_SharedDagStopsAtNodeCap()
    {
        await Task.CompletedTask;
        var leaf = new InvalidOperationException("x");
        var node = new AggregateException(leaf);
        for (var level = 0; level < 30; level++)
            node = new AggregateException(node, node);

        _ = ExceptionReport.FormatText(node);
        _ = ExceptionReport.CreateBody(node);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var text = ExceptionReport.FormatText(node);
        var body = ExceptionReport.CreateBody(node);
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Matches(new Regex(@"\(\+\d+\+ more\)"), text);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(50), elapsed.ToString());
        Assert.True(allocated < 256 * 1024, allocated.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(text.Length < 500, text);
        var jsonMessages = new List<string>();
        if (body.Inner != null)
        {
            Assert.True(body.Inner.Count <= 16);
            for (var i = 0; i < body.Inner.Count; i++)
                jsonMessages.Add(body.Inner[i].Message ?? string.Empty);
        }

        var json = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", node));
        using var doc = JsonDocument.Parse(json);
        if (jsonMessages.Count == 0)
            Assert.False(doc.RootElement.GetProperty("exception").TryGetProperty("inner", out _));
        else
        {
            var inner = doc.RootElement.GetProperty("exception").GetProperty("inner");
            Assert.Equal(jsonMessages.Count, inner.GetArrayLength());
            for (var i = 0; i < jsonMessages.Count; i++)
                Assert.Equal(jsonMessages[i], inner[i].GetProperty("message").GetString());
        }
    }

    /// <summary>
    /// One nested aggregate plus 64 leaves is 65 nodes under the root.
    /// The root is already rendered, and the nested aggregate and the leaves share one counter of 64,
    /// so the walk stops before the last leaf and writes <c>(+n+ more)</c>.
    /// JSON lists the same prefix, at most 16 entries.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_NestedLeavesShareTheNodeCap()
    {
        await Task.CompletedTask;
        var nested = new AggregateException(Enumerable.Range(0, 64).Select(i =>
            new InvalidOperationException(i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        var root = new AggregateException(nested);
        var text = ExceptionReport.FormatText(root);
        const string suffix = " (+60+ more)";
        Assert.EndsWith(suffix, text, StringComparison.Ordinal);
        Assert.Contains("+ more)", text, StringComparison.Ordinal);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2" + suffix,
            text);
        Assert.DoesNotContain("(+60 more)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(": 63", text, StringComparison.Ordinal);
        AssertInnerMessages(
            root,
            Enumerable.Range(0, 16).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }

    /// <summary>
    /// Sixty-four empty aggregates fill the visit counter and have no children left to read.
    /// They are fully visited, so the text is the aggregate type with no suffix.
    /// JSON has no inner entries.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_SixtyFourEmptyAggregatesHaveNoSuffix()
    {
        await Task.CompletedTask;
        var children = new Exception[64];
        for (var i = 0; i < children.Length; i++)
            children[i] = new AggregateException();
        var root = new AggregateException(children);
        Assert.Equal("AggregateException", ExceptionReport.FormatText(root));
        var body = ExceptionReport.CreateBody(root);
        Assert.True(body.Inner == null || body.Inner.Count == 0);
    }

    /// <summary>
    /// Sixty-three empty aggregates and one leaf fill the visit counter.
    /// The leaf is a direct child, and each empty aggregate has no child left to read.
    /// Text shows the leaf and no <c>(+n more)</c> suffix. JSON lists that same leaf.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_EmptyAggregatesThenLeafHaveNoSuffix()
    {
        await Task.CompletedTask;
        var children = new Exception[64];
        for (var i = 0; i < 63; i++)
            children[i] = new AggregateException();
        children[63] = new InvalidOperationException("last");
        var root = new AggregateException(children);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: last",
            ExceptionReport.FormatText(root));
        AssertInnerMessages(root, "last");
    }

    /// <summary>
    /// Sixty-three leaves and an empty aggregate in the last slot fill the visit counter.
    /// The empty aggregate was counted and has no child left to read, so the suffix has no extra plus.
    /// JSON lists the first 16 leaves from that same list.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_EmptyAggregateAfterSixtyThreeLeavesIsCounted()
    {
        await Task.CompletedTask;
        var children = new Exception[64];
        for (var i = 0; i < 63; i++)
            children[i] = new InvalidOperationException(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        children[63] = new AggregateException();
        var root = new AggregateException(children);
        var text = ExceptionReport.FormatText(root);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60 more)",
            text);
        Assert.DoesNotContain("(+60+ more)", text, StringComparison.Ordinal);
        AssertInnerMessages(
            root,
            Enumerable.Range(0, 16).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }

    /// <summary>
    /// A self-reference or a null after 63 or 64 leaves does not count as a real child.
    /// The walk finishes, so the suffix has no extra plus.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_NullAndCycleAfterLeavesStayComplete()
    {
        await Task.CompletedTask;
        const string sixty = "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60 more)";
        const string sixtyOne = "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+61 more)";

        var selfAfter63 = new AggregateException();
        var after63 = NumberedLeaves(63);
        var withSelf = new Exception[64];
        Array.Copy(after63, withSelf, 63);
        withSelf[63] = selfAfter63;
        SetChildren(selfAfter63, withSelf);
        Assert.Equal(sixty, ExceptionReport.FormatText(selfAfter63));

        var nullAfter63 = new Exception[64];
        Array.Copy(after63, nullAfter63, 63);
        var nullRoot = new AggregateException();
        SetChildren(nullRoot, nullAfter63);
        Assert.Equal(sixty, ExceptionReport.FormatText(nullRoot));

        var selfAfter64 = new AggregateException();
        var withSelf64 = new Exception[65];
        Array.Copy(NumberedLeaves(64), withSelf64, 64);
        withSelf64[64] = selfAfter64;
        SetChildren(selfAfter64, withSelf64);
        Assert.Equal(sixtyOne, ExceptionReport.FormatText(selfAfter64));

        var nullsAfter64 = new Exception[66];
        Array.Copy(NumberedLeaves(64), nullsAfter64, 64);
        var nullsRoot = new AggregateException();
        SetChildren(nullsRoot, nullsAfter64);
        Assert.Equal(sixtyOne, ExceptionReport.FormatText(nullsRoot));
    }

    /// <summary>
    /// Leading self-references and leading nulls do not consume the 64 real-child counter.
    /// Sixty-three leaves after those skips are all counted, so the suffix is <c>(+60 more)</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_LeadingSkipsDoNotDropLeaves()
    {
        await Task.CompletedTask;
        const string text = "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60 more)";

        var cycled = new AggregateException();
        var cycleChildren = new Exception[73];
        for (var i = 0; i < 10; i++)
            cycleChildren[i] = cycled;
        Array.Copy(NumberedLeaves(63), 0, cycleChildren, 10, 63);
        SetChildren(cycled, cycleChildren);
        Assert.Equal(text, ExceptionReport.FormatText(cycled));

        var padded = new Exception[68];
        Array.Copy(NumberedLeaves(63), 0, padded, 5, 63);
        var paddedRoot = new AggregateException();
        SetChildren(paddedRoot, padded);
        Assert.Equal(text, ExceptionReport.FormatText(paddedRoot));
    }

    /// <summary>
    /// More than 4096 null slots stop the walk before a later leaf.
    /// The suffix is <c>(+1+ more)</c> and the walk finishes quickly.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_SlotBudgetStops()
    {
        await Task.CompletedTask;
        var children = new Exception[4097];
        children[4096] = new InvalidOperationException("past");
        var root = new AggregateException();
        SetChildren(root, children);
        var start = Stopwatch.GetTimestamp();
        var text = ExceptionReport.FormatText(root);
        var elapsed = Stopwatch.GetElapsedTime(start);
        Assert.Equal("AggregateException (+1+ more)", text);
        Assert.DoesNotContain("past", text, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(50), elapsed.ToString());
    }

    /// <summary>
    /// An unread aggregate whose only child is itself is a cycle, not a real child.
    /// The suffix keeps the counted leaves and has no extra plus.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_UnreadAggregateCycleDoesNotStop()
    {
        await Task.CompletedTask;
        var nested = new AggregateException();
        SetChildren(nested, new Exception[] { nested });
        var text = ExceptionReport.FormatText(LeavesThen(nested));
        Assert.Equal(
            "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60 more)",
            text);
        Assert.DoesNotContain("(+60+ more)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unread aggregate whose only slot is null has no real child.
    /// The suffix keeps the counted leaves and has no extra plus.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_UnreadNullDoesNotStop()
    {
        await Task.CompletedTask;
        var nested = new AggregateException();
        SetChildren(nested, new Exception[] { null! });
        var text = ExceptionReport.FormatText(LeavesThen(nested));
        Assert.Equal(
            "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60 more)",
            text);
        Assert.DoesNotContain("(+60+ more)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sixty-three empty aggregates and one later aggregate that holds <c>y</c> fill the visit counter.
    /// The later leaf is still unread, so the text is <c>AggregateException (+1+ more)</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_LaterUnreadAggregateIsIncomplete()
    {
        await Task.CompletedTask;
        var root = new AggregateException(Enumerable.Range(0, 63).Select(_ => (Exception)new AggregateException()).Append(new AggregateException(new InvalidOperationException("y"))));
        Assert.Equal("AggregateException (+1+ more)", ExceptionReport.FormatText(root));
    }

    /// <summary>
    /// An unread aggregate whose inner-exceptions getter throws counts as no children.
    /// The suffix keeps the counted leaves and has no extra plus.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_ThrowingUnreadInnerDoesNotStop()
    {
        await Task.CompletedTask;
        var nested = new AggregateException();
        var inners = typeof(AggregateException).GetField("_innerExceptions", BindingFlags.Instance | BindingFlags.NonPublic);
        var view = typeof(AggregateException).GetField("_rocView", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(inners);
        Assert.NotNull(view);
        inners.SetValue(nested, null);
        view.SetValue(nested, null);
        var text = ExceptionReport.FormatText(LeavesThen(nested));
        Assert.Equal(
            "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60 more)",
            text);
        Assert.DoesNotContain("(+60+ more)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unread aggregate whose first slot is null and whose second slot is a leaf still has a real child.
    /// The visit counter is full, so the leaf is not shown and the suffix has the extra plus.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aggregate_NullBeforeUnreadLeafIsIncomplete()
    {
        await Task.CompletedTask;
        var nested = new AggregateException();
        SetChildren(nested, new Exception[] { null!, new InvalidOperationException("y") });
        var text = ExceptionReport.FormatText(LeavesThen(nested));
        Assert.Equal(
            "AggregateException --> InvalidOperationException: 0; InvalidOperationException: 1; InvalidOperationException: 2 (+60+ more)",
            text);
        Assert.DoesNotContain(": y", text, StringComparison.Ordinal);
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
    /// A chain longer than 16 also locks the text walk: 18 nodes show <c>(+13+ more)</c>, not the true remainder.
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
        Assert.Contains("InvalidOperationException: m0 --> InvalidOperationException: m1 --> InvalidOperationException: m2 (+13+ more)", capped, StringComparison.Ordinal);
        Assert.DoesNotContain("(+13 more)", capped, StringComparison.Ordinal);
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
        Assert.Equal("ALog.Tests.ExceptionFormatTests.ThrowingMessage: [capture failed: InvalidOperationException]", message);
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

        var tail = new InvalidOperationException("e");
        var fourth = new InvalidOperationException("d", tail);
        var third = new InvalidOperationException("c", fourth);
        var second = new InvalidOperationException("b", third);
        var first = new InvalidOperationException("a", second);
        field.SetValue(tail, first);
        var counted = ExceptionReport.FormatText(first);
        Assert.Equal(
            "InvalidOperationException: a --> InvalidOperationException: b --> InvalidOperationException: c (+2 more)",
            counted);
        Assert.DoesNotContain("(+2+ more)", counted, StringComparison.Ordinal);
        var countedBody = ExceptionReport.CreateBody(first);
        Assert.NotNull(countedBody.Inner);
        Assert.Equal(4, countedBody.Inner.Count);
    }

    /// <summary>
    /// The event message, a context key, a property key, and a property value are escaped on the text sinks.
    /// A line feed in any of them stays inside the same line. JSON keeps the raw characters.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Line_EscapesMessageAndKeys()
    {
        const string raw = "user=bob | InvalidOperationException: forged | at Fake.M in F.cs:1\n";
        const string escaped = "user=bob \\| InvalidOperationException: forged \\| at Fake.M in F.cs:1\\n";
        var evt = new LogEvent(
            new DateTime(2026, 10, 21, 20, 55, 19),
            LogLevel.Error,
            raw,
            null,
            new Dictionary<string, object> { [raw] = "v" },
            new Dictionary<string, object> { [raw] = raw });
        var plain = "[2026-10-21 20:55:19] [Error] " + escaped
            + " | props: " + escaped + "=" + escaped
            + " | ctx: " + escaped + "=v";
        Assert.Equal(plain, new PlainTextFormatter("yyyy-MM-dd HH:mm:ss").Format(evt));
        Assert.DoesNotContain("\n", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", plain, StringComparison.Ordinal);
        Assert.DoesNotContain(" | InvalidOperationException: forged | ", plain, StringComparison.Ordinal);

        var fallback = "[2026-10-21 20:55:19] [Error] " + escaped + " | ctx: " + escaped + "=v";
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

        Assert.Equal(fallback + Environment.NewLine, buffer.ToString());
        Assert.DoesNotContain("props:", buffer.ToString(), StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "alog-exf-forge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "app.log");
            using (var file = new FileLogWriter(path))
                file.Write(evt);
            var fileText = await File.ReadAllTextAsync(path);
            Assert.Equal(fallback + Environment.NewLine, fileText);
            Assert.DoesNotContain("props:", fileText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        using var document = JsonDocument.Parse(new JsonFormatter().Format(evt));
        Assert.Equal(raw, document.RootElement.GetProperty("message").GetString());
        Assert.Equal(raw, document.RootElement.GetProperty("properties").GetProperty(raw).GetString());
        Assert.Equal("v", document.RootElement.GetProperty("context").GetProperty(raw).GetString());
    }

    /// <summary>
    /// The raw message is cut to 500 characters before escaping, and the marker is appended after that.
    /// A pipe or an arrow that straddles the cut is not left as a split escape.
    /// Context values use the same order at 8192.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Message_TruncatesRawThenEscapes()
    {
        await Task.CompletedTask;
        var clean = new string('b', 4);
        Assert.Same(clean, ExceptionReport.Escape(clean, messageLimit: true));

        var boundary = new string('x', 499) + "|";
        Assert.Equal(500, boundary.Length);
        var boundaryText = ExceptionReport.Escape(boundary, messageLimit: true);
        Assert.Equal(new string('x', 499) + "\\|", boundaryText);
        Assert.DoesNotContain("[truncated]", boundaryText, StringComparison.Ordinal);
        Assert.Equal(
            "InvalidOperationException: " + new string('x', 499) + "\\|",
            ExceptionReport.FormatText(new InvalidOperationException(boundary)));

        var over = ExceptionReport.Escape(new string('x', 500) + "|", messageLimit: true);
        Assert.Equal(new string('x', 500) + " [truncated]", over);
        Assert.DoesNotContain("|", over, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", over, StringComparison.Ordinal);

        var arrow = ExceptionReport.Escape(new string('x', 498) + "-->", messageLimit: true);
        Assert.Equal(new string('x', 498) + "-- [truncated]", arrow);
        Assert.DoesNotContain("\\", arrow, StringComparison.Ordinal);
        Assert.DoesNotContain("--\\>", arrow, StringComparison.Ordinal);

        var arrowTail = ExceptionReport.Escape(new string('x', 499) + "-->", messageLimit: true);
        Assert.Equal(new string('x', 499) + "- [truncated]", arrowTail);

        var ctxBoundary = ExceptionReport.Escape(new string('x', 8191) + "|", messageLimit: false);
        Assert.Equal(new string('x', 8191) + "\\|", ctxBoundary);
        Assert.DoesNotContain("[truncated]", ctxBoundary, StringComparison.Ordinal);
        var ctxPipes = ExceptionReport.Escape(new string('|', 8192), messageLimit: false);
        Assert.Equal(string.Concat(Enumerable.Repeat("\\|", 8192)), ctxPipes);
        Assert.DoesNotContain("[truncated]", ctxPipes, StringComparison.Ordinal);
        var ctxOver = ExceptionReport.Escape(new string('x', 8192) + "|", messageLimit: false);
        Assert.Equal(new string('x', 8192) + " [truncated]", ctxOver);
        Assert.DoesNotContain("\\|", ctxOver, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>(+N more)</c> is written only when the walk counted every node.
    /// A 17-level chain stops at 16 and writes <c>(+13+ more)</c>.
    /// An aggregate whose flatten stops at 64 nodes writes <c>(+N+ more)</c>.
    /// A chain of 15 aggregate wrappers stays under that cap and is fully counted.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Chain_SuffixMarksAStoppedWalk()
    {
        await Task.CompletedTask;
        var full = ChainOf(16);
        var fullText = ExceptionReport.FormatText(full);
        Assert.Contains("InvalidOperationException: m0 --> InvalidOperationException: m1 --> InvalidOperationException: m2 (+13 more)", fullText, StringComparison.Ordinal);
        Assert.DoesNotContain("(+13+ more)", fullText, StringComparison.Ordinal);

        var stopped = ChainOf(17);
        var stoppedText = ExceptionReport.FormatText(stopped);
        Assert.Contains("InvalidOperationException: m0 --> InvalidOperationException: m1 --> InvalidOperationException: m2 (+13+ more)", stoppedText, StringComparison.Ordinal);
        Assert.DoesNotContain("(+13 more)", stoppedText, StringComparison.Ordinal);

        var exact = new Exception[64];
        for (var i = 0; i < exact.Length; i++)
            exact[i] = new InvalidOperationException("e" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var exactText = ExceptionReport.FormatText(new AggregateException(exact));
        Assert.Contains("(+61 more)", exactText, StringComparison.Ordinal);
        Assert.DoesNotContain("(+61+ more)", exactText, StringComparison.Ordinal);

        var extra = new Exception[65];
        for (var i = 0; i < extra.Length; i++)
            extra[i] = new InvalidOperationException("e" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var extraText = ExceptionReport.FormatText(new AggregateException(extra));
        Assert.Contains("(+61+ more)", extraText, StringComparison.Ordinal);
        Assert.DoesNotContain("(+61 more)", extraText, StringComparison.Ordinal);

        var hidden = new InvalidOperationException("hidden");
        var more = new AggregateException(hidden);
        Exception current = new AggregateException(
            new InvalidOperationException("e0"),
            new InvalidOperationException("e1"),
            new InvalidOperationException("e2"),
            new InvalidOperationException("e3"),
            more);
        for (var i = 0; i < 15; i++)
            current = new AggregateException(current);
        var capped = ExceptionReport.FormatText(current);
        Assert.Contains("InvalidOperationException: e0; InvalidOperationException: e1; InvalidOperationException: e2 (+2 more)", capped, StringComparison.Ordinal);
        Assert.DoesNotContain("(+2+ more)", capped, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", capped, StringComparison.Ordinal);
        Assert.DoesNotContain(": e3", capped, StringComparison.Ordinal);
    }

    /// <summary>
    /// An aggregate that contains itself does not spin, in text or JSON.
    /// A leaf stored beside that cycle is rendered once. Removing the reference check repeats the leaf.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Robustness_AggregateCycleStops()
    {
        await Task.CompletedTask;
        var loop = new AggregateException();
        SetChildren(loop, new Exception[] { loop });
        Assert.Equal("AggregateException", ExceptionReport.FormatText(loop));
        var loopBody = ExceptionReport.CreateBody(loop);
        Assert.True(loopBody.Inner == null || loopBody.Inner.Count == 0);
        var loopJson = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", loop));
        using (var loopDoc = JsonDocument.Parse(loopJson))
        {
            Assert.False(loopDoc.RootElement.GetProperty("exception").TryGetProperty("inner", out _));
        }

        var leaf = new InvalidOperationException("leaf");
        var both = new AggregateException();
        SetChildren(both, new Exception[] { both, leaf });
        Assert.Equal("AggregateException --> InvalidOperationException: leaf", ExceptionReport.FormatText(both));
        var bothJson = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", both));
        using var bothDoc = JsonDocument.Parse(bothJson);
        var inner = bothDoc.RootElement.GetProperty("exception").GetProperty("inner");
        Assert.Equal(1, inner.GetArrayLength());
        Assert.Equal("leaf", inner[0].GetProperty("message").GetString());
    }

    /// <summary>
    /// A cycle below the root, root to A to B and back to A, is an ancestor and is not entered again.
    /// A holds leaf <c>la</c> and B holds leaf <c>lb</c>. Text and JSON list those two leaves once.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Robustness_CycleBelowRootStops()
    {
        await Task.CompletedTask;
        var la = new InvalidOperationException("la");
        var lb = new InvalidOperationException("lb");
        var aggregateA = new AggregateException();
        var aggregateB = new AggregateException();
        SetChildren(aggregateA, new Exception[] { la, aggregateB });
        SetChildren(aggregateB, new Exception[] { lb, aggregateA });
        var root = new AggregateException();
        SetChildren(root, new Exception[] { aggregateA });

        var text = ExceptionReport.FormatText(root);
        Assert.Equal(
            "AggregateException --> InvalidOperationException: la; InvalidOperationException: lb",
            text);
        Assert.DoesNotContain("la; lb; la", text, StringComparison.Ordinal);
        Assert.DoesNotContain("+ more)", text, StringComparison.Ordinal);
        AssertInnerMessages(root, "la", "lb");
    }

    /// <summary>
    /// A wrap whose inner aggregate points back at the wrap is one shared walk.
    /// Text and JSON list the same instances, and the wrap is not repeated inside the aggregate.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Robustness_CycleThroughAggregateEntryIsShared()
    {
        await Task.CompletedTask;
        var wrap = new InvalidOperationException("wrap");
        var aggregate = new AggregateException(wrap);
        var field = typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(wrap, aggregate);

        var text = ExceptionReport.FormatText(wrap);
        Assert.Equal("InvalidOperationException: wrap --> AggregateException", text);
        var again = text.IndexOf("InvalidOperationException: wrap", StringComparison.Ordinal);
        Assert.True(again >= 0);
        Assert.Equal(-1, text.IndexOf("InvalidOperationException: wrap", again + 1, StringComparison.Ordinal));

        var body = ExceptionReport.CreateBody(wrap);
        Assert.Equal(wrap.GetType().FullName, body.Type);
        Assert.NotNull(body.Inner);
        Assert.Single(body.Inner);
        Assert.Equal(aggregate.GetType().FullName, body.Inner[0].Type);

        var json = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", wrap));
        using var doc = JsonDocument.Parse(json);
        var node = doc.RootElement.GetProperty("exception");
        Assert.Equal(body.Type, node.GetProperty("type").GetString());
        var jsonInner = node.GetProperty("inner");
        Assert.Equal(1, jsonInner.GetArrayLength());
        Assert.Equal(body.Inner[0].Type, jsonInner[0].GetProperty("type").GetString());
        Assert.Equal(body.Inner[0].Message, jsonInner[0].GetProperty("message").GetString());
    }

    /// <summary>
    /// JSON and HTTP <c>type</c> stay <see cref="Type.FullName"/> for a nested type and a generic type.
    /// Text keeps the readable name, and <c>inner[].type</c> uses the same raw full name.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Json_TypeMatchesRuntimeFullName()
    {
        await Task.CompletedTask;
        var nested = new Outer.NestedEx("boom");
        var generic = new GenEx<int>("n");
        Assert.Equal("ALog.Tests.Naming.Outer.NestedEx: boom", ExceptionReport.FormatText(nested));
        Assert.DoesNotContain("+", ExceptionReport.FormatText(nested), StringComparison.Ordinal);
        Assert.Equal(nested.GetType().FullName, ExceptionReport.CreateBody(nested).Type);
        Assert.Contains("+", nested.GetType().FullName, StringComparison.Ordinal);
        Assert.Equal(generic.GetType().FullName, ExceptionReport.CreateBody(generic).Type);
        Assert.Contains("`", generic.GetType().FullName, StringComparison.Ordinal);

        var wrapped = new InvalidOperationException("outer", nested);
        var wrappedBody = ExceptionReport.CreateBody(wrapped);
        Assert.NotNull(wrappedBody.Inner);
        Assert.Equal(nested.GetType().FullName, wrappedBody.Inner[0].Type);

        var evt = new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", generic);
        var json = new JsonFormatter().Format(evt);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(generic.GetType().FullName, document.RootElement.GetProperty("exception").GetProperty("type").GetString());

        var handler = new CapturingHttpHandler();
        using var client = new HttpClient(handler);
        using var http = new HttpLogWriter("https://example.test/logs", httpClient: client);
        http.Write(evt);
        AssertEqualException(json, handler.Requests[0].Body);
    }

    /// <summary>
    /// Generic and nested types use C# syntax, frames use that same form, and text omits an aggregate entry's inner exception.
    /// JSON keeps the entry and that inner exception, in that order.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Names_GenericNestedFramesAndEntryInners()
    {
        await Task.CompletedTask;
        Assert.Equal(
            "ALog.Tests.Naming.GenEx<Int32>: n",
            ExceptionReport.FormatText(new GenEx<int>("n")));
        var genericEx = new GenEx<int>("n");
        Assert.Equal(genericEx.GetType().FullName, ExceptionReport.CreateBody(genericEx).Type);
        var genericJson = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", genericEx));
        using (var genericDoc = JsonDocument.Parse(genericJson))
        {
            var genericType = genericDoc.RootElement.GetProperty("exception").GetProperty("type").GetString();
            Assert.Equal(genericEx.GetType().FullName, genericType);
            Assert.Contains("`", genericType, StringComparison.Ordinal);
            Assert.Contains("PublicKeyToken", genericType, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("GenEx<Int32>", genericJson, StringComparison.Ordinal);
        Assert.Equal(
            "ALog.Tests.Naming.GenEx<List<Int32>>: n",
            ExceptionReport.FormatText(new GenEx<List<int>>("n")));
        Assert.Equal(
            "ALog.Tests.Naming.GenEx<Int32[]>: n",
            ExceptionReport.FormatText(new GenEx<int[]>("n")));
        Assert.Equal(
            "ALog.Tests.Naming.GenEx<ALog.QuickstartSample.CardException[]>: n",
            ExceptionReport.FormatText(new GenEx<ALog.QuickstartSample.CardException[]>("n")));
        Assert.Equal(
            "ALog.Tests.Naming.GenEx<Nullable<Int32>>: n",
            ExceptionReport.FormatText(new GenEx<int?>("n")));
        Assert.Equal(
            "ALog.Tests.Naming.Box<ALog.QuickstartSample.CardException>: n",
            ExceptionReport.FormatText(new Box<ALog.QuickstartSample.CardException>("n")));
        Assert.Equal(
            "ALog.Tests.Naming.Box<Microsoft.Data.Demo.DemoException>: n",
            ExceptionReport.FormatText(new Box<Microsoft.Data.Demo.DemoException>("n")));

        var nested = ExceptionReport.FormatText(new Outer.NestedEx("boom"));
        Assert.Equal("ALog.Tests.Naming.Outer.NestedEx: boom", nested);
        Assert.DoesNotContain("+", nested, StringComparison.Ordinal);
        Assert.Equal(
            "ALog.Tests.Naming.Holder<Int32>.Inner<ALog.QuickstartSample.CardException>: nest",
            ExceptionReport.FormatText(new Holder<int>.Inner<ALog.QuickstartSample.CardException>("nest")));

        Assert.Equal("Outer.Inner2<T>", ExceptionReport.FrameTypeName(typeof(Outer.Inner2<>)));
        Assert.Equal("Outer.Inner2<Int32>", ExceptionReport.FrameTypeName(typeof(Outer.Inner2<int>)));
        Assert.Equal("System.Collections.Generic.List<Int32>", ExceptionReport.FrameTypeName(typeof(List<int>)));
        Assert.Equal("System.Collections.Generic.List<T>", ExceptionReport.FrameTypeName(typeof(List<>)));
        Assert.Equal("System.Collections.Generic.Dictionary<Int32, String>", ExceptionReport.FrameTypeName(typeof(Dictionary<int, string>)));
        Assert.Equal("Int32[,]", ExceptionReport.FrameTypeName(typeof(int[,])));
        Assert.Equal("System.Collections.Generic.List<Int32>*", ExceptionReport.FrameTypeName(typeof(List<int>).MakePointerType()));
        Assert.Equal("System.Collections.Generic.List<Int32>&", ExceptionReport.FrameTypeName(typeof(List<int>).MakeByRefType()));
        Assert.DoesNotContain("`", ExceptionReport.FrameTypeName(typeof(List<int>).MakePointerType()), StringComparison.Ordinal);
        Assert.Equal(string.Empty, ExceptionReport.FrameTypeName(null));

        var deep = typeof(int);
        for (var i = 0; i < 20; i++)
            deep = typeof(List<>).MakeGenericType(deep);
        var deepName = ExceptionReport.FrameTypeName(deep);
        Assert.Contains("`", deepName, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicKeyToken", deepName, StringComparison.Ordinal);

        var thrown = Catch(() => new Outer.Inner2<int>().Go());
        var frame = ExceptionReport.FormatText(thrown);
        Assert.Contains("at Outer.Inner2<T>.Go in ExceptionNameSamples.cs:", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("+", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("`", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("ALog.Tests.Naming.Outer.Inner2", frame, StringComparison.Ordinal);

        var entry = new InvalidOperationException("entry", new InvalidOperationException("deep"));
        var aggregate = new AggregateException(entry);
        var aggregateText = ExceptionReport.FormatText(aggregate);
        Assert.Equal("AggregateException --> InvalidOperationException: entry", aggregateText);
        Assert.DoesNotContain("deep", aggregateText, StringComparison.Ordinal);
        var aggregateBody = ExceptionReport.CreateBody(aggregate);
        Assert.NotNull(aggregateBody.Inner);
        Assert.Collection(
            aggregateBody.Inner,
            item => Assert.Equal("entry", item.Message),
            item => Assert.Equal("deep", item.Message));
    }

    /// <summary>
    /// Caps that are not on the happy path still run: message safety, flatten room, arity, rank, and compiler names.
    /// Each one is a fixed bound, and each one changes the text when it trips.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Bounds_CapsChangeTheText()
    {
        await Task.CompletedTask;
        Assert.Equal(string.Empty, ExceptionReport.EscapeLine(null));
        Assert.Equal(string.Empty, ExceptionReport.EscapeLine(string.Empty));
        Assert.Equal("GlobalNameException: g", ExceptionReport.FormatText(new GlobalNameException("g")));

        Exception deep = new AggregateException(new InvalidOperationException("leaf"));
        for (var i = 0; i < 16; i++)
            deep = new AggregateException(deep);
        Assert.DoesNotContain("leaf", ExceptionReport.CreateBody(deep).Message ?? string.Empty, StringComparison.Ordinal);

        var wideKids = new Exception[65];
        for (var i = 0; i < wideKids.Length; i++)
            wideKids[i] = new InvalidOperationException("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("k0", ExceptionReport.CreateBody(new AggregateException(wideKids)).Message ?? string.Empty, StringComparison.Ordinal);

        var crowded = new Exception[64];
        for (var i = 0; i < crowded.Length; i++)
            crowded[i] = new InvalidOperationException("p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var crowdedText = ExceptionReport.FormatText(new AggregateException(new AggregateException(crowded), new InvalidOperationException("side")));
        Assert.Contains("InvalidOperationException: side; InvalidOperationException: p0; InvalidOperationException: p1 (+60+ more)", crowdedText, StringComparison.Ordinal);
        Assert.DoesNotContain("p63", crowdedText, StringComparison.Ordinal);

        var left = new Exception[40];
        var right = new Exception[40];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = new InvalidOperationException("L" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            right[i] = new InvalidOperationException("R" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var split = ExceptionReport.FormatText(new AggregateException(new AggregateException(left), new AggregateException(right)));
        Assert.Contains("(+59+ more)", split, StringComparison.Ordinal);
        Assert.DoesNotContain("R39", split, StringComparison.Ordinal);

        var array = typeof(int);
        for (var i = 0; i < 20; i++)
            array = array.MakeArrayType();
        var arrayName = ExceptionReport.FrameTypeName(array);
        Assert.Contains("Int32", arrayName, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicKeyToken", arrayName, StringComparison.Ordinal);
        Assert.Equal("Int32[" + new string(',', 15) + "]", ExceptionReport.FrameTypeName(typeof(int).MakeArrayType(17)));

        var wideName = ExceptionReport.FormatText(new WideException<int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, int, string>());
        Assert.Contains("WideException<Int32", wideName, StringComparison.Ordinal);
        Assert.DoesNotContain("String", wideName, StringComparison.Ordinal);

        var peeled = "M";
        for (var i = 0; i < 9; i++)
            peeled = "<" + peeled + ">b__0_0";
        var stopped = ExceptionReport.RenameCompilerMethod(string.Empty, peeled);
        Assert.Contains(">b__0_0", stopped, StringComparison.Ordinal);
        Assert.NotEqual("M", stopped);
        Assert.Equal("<NoClose", ExceptionReport.RenameCompilerMethod(string.Empty, "<NoClose"));
        Assert.Equal("<M>b__1_", ExceptionReport.RenameCompilerMethod(string.Empty, "<M>b__1_"));

        var broken = new AggregateException();
        var innersField = typeof(AggregateException).GetField("_innerExceptions", BindingFlags.Instance | BindingFlags.NonPublic);
        var viewField = typeof(AggregateException).GetField("_rocView", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(innersField);
        Assert.NotNull(viewField);
        innersField.SetValue(broken, null);
        viewField.SetValue(broken, null);
        Assert.Equal("AggregateException", ExceptionReport.FormatText(broken));
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
        var wideReadOnly = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(wide);
        var readOnlyCtx = ExceptionReport.FormatContext(wideReadOnly);
        Assert.NotNull(readOnlyCtx);
        Assert.Contains("k0=v", readOnlyCtx, StringComparison.Ordinal);
        Assert.DoesNotContain("k4096=", readOnlyCtx, StringComparison.Ordinal);
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

    /// <summary>
    /// JSON <c>inner</c> messages equal <paramref name="expected"/>, which is a real <c>Flatten()</c> list.
    /// </summary>
    /// <param name="exception">Aggregate under test. Not null. Acyclic.</param>
    /// <param name="expected">Entries from <see cref="AggregateException.Flatten"/>. Not null.</param>
    private static void AssertSameOrder(AggregateException exception, System.Collections.ObjectModel.ReadOnlyCollection<Exception> expected)
    {
        var body = ExceptionReport.CreateBody(exception);
        var actual = new List<string>(expected.Count);
        if (body.Inner != null)
        {
            for (var i = 0; i < body.Inner.Count; i++)
                actual.Add(body.Inner[i].Message ?? string.Empty);
        }

        var wanted = new List<string>(expected.Count);
        var limit = expected.Count;
        if (limit > 16)
            limit = 16;
        for (var i = 0; i < limit; i++)
            wanted.Add(expected[i].Message);
        Assert.Equal(wanted, actual);
    }

    /// <summary>Picks a finished non-aggregate so a random tree can repeat an instance without forming a cycle.</summary>
    /// <param name="finished">Nodes already built. Not null.</param>
    /// <param name="random">Fixed-seed generator. Not null.</param>
    /// <returns>A leaf to insert again, or null when this slot should be a new node.</returns>
    private static Exception? LeafToReuse(List<Exception> finished, Random random)
    {
        if (finished.Count == 0 || random.Next(0, 4) != 0)
            return null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var candidate = finished[random.Next(finished.Count)];
            if (candidate is not AggregateException)
                return candidate;
        }

        return null;
    }

    /// <summary>Builds one acyclic aggregate. Finished nodes may be reused, which keeps the graph a DAG.</summary>
    /// <param name="random">Fixed-seed generator. Not null.</param>
    /// <param name="nextId">Next leaf id. Updated.</param>
    /// <param name="leavesLeft">Leaves still allowed. Updated.</param>
    /// <param name="aggsLeft">Nested aggregates still allowed. Updated.</param>
    /// <param name="depth">Current depth. The walk stops nesting at 4.</param>
    /// <param name="finished">Nodes whose construction has finished. Not null. Reuse does not create a cycle.</param>
    /// <returns>A new aggregate. Not null.</returns>
    private static AggregateException RandomAggregate(
        Random random,
        ref int nextId,
        ref int leavesLeft,
        ref int aggsLeft,
        int depth,
        List<Exception> finished)
    {
        var width = 1 + random.Next(3);
        var children = new List<Exception>(width);
        for (var i = 0; i < width; i++)
        {
            var reuse = LeafToReuse(finished, random);
            var nest = depth < 3 && aggsLeft > 0 && leavesLeft > 1 && random.Next(0, 2) == 0;
            if (reuse != null)
            {
                children.Add(reuse);
            }
            else if (nest)
            {
                aggsLeft--;
                children.Add(RandomAggregate(random, ref nextId, ref leavesLeft, ref aggsLeft, depth + 1, finished));
            }
            else if (leavesLeft > 0)
            {
                leavesLeft--;
                var leaf = new InvalidOperationException("n" + nextId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                nextId++;
                children.Add(leaf);
                finished.Add(leaf);
            }
        }

        if (children.Count == 0)
        {
            var leaf = new InvalidOperationException("n" + nextId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            nextId++;
            children.Add(leaf);
            finished.Add(leaf);
        }

        var built = new AggregateException(children.ToArray());
        finished.Add(built);
        return built;
    }

    /// <summary>JSON <c>inner</c> messages, in walk order, match <paramref name="messages"/>.</summary>
    /// <param name="exception">Exception whose body is read. Not null.</param>
    /// <param name="messages">Expected messages. Not null.</param>
    private static void AssertInnerMessages(Exception exception, params string[] messages)
    {
        var body = ExceptionReport.CreateBody(exception);
        Assert.NotNull(body.Inner);
        Assert.Equal(messages.Length, body.Inner.Count);
        for (var i = 0; i < messages.Length; i++)
            Assert.Equal(messages[i], body.Inner[i].Message);

        var json = new JsonFormatter().Format(new LogEvent(CoverageClock.UtcNow, LogLevel.Error, "m", exception));
        using var doc = JsonDocument.Parse(json);
        var inner = doc.RootElement.GetProperty("exception").GetProperty("inner");
        Assert.Equal(messages.Length, inner.GetArrayLength());
        for (var i = 0; i < messages.Length; i++)
            Assert.Equal(messages[i], inner[i].GetProperty("message").GetString());
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

    /// <summary>Builds a chain of <paramref name="count"/> invalid-operation exceptions, outermost message <c>m0</c>.</summary>
    /// <param name="count">How many nodes. At least one.</param>
    /// <returns>The outermost exception.</returns>
    private static Exception ChainOf(int count)
    {
        Exception current = new InvalidOperationException("m" + (count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = count - 2; i >= 0; i--)
            current = new InvalidOperationException("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), current);
        return current;
    }

    /// <summary>An aggregate of 63 numbered leaves followed by <paramref name="tail"/>.</summary>
    /// <param name="tail">The 64th child. Not null.</param>
    /// <returns>The root. Not null.</returns>
    private static AggregateException LeavesThen(Exception tail)
    {
        var children = new Exception[64];
        Array.Copy(NumberedLeaves(63), children, 63);
        children[63] = tail;
        var root = new AggregateException();
        SetChildren(root, children);
        return root;
    }

    /// <summary>Builds <paramref name="count"/> leaves whose messages are <c>0</c> through <c>count - 1</c>.</summary>
    /// <param name="count">How many leaves. Not negative.</param>
    /// <returns>The leaves. Not null.</returns>
    private static Exception[] NumberedLeaves(int count)
    {
        var leaves = new Exception[count];
        for (var i = 0; i < count; i++)
            leaves[i] = new InvalidOperationException(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return leaves;
    }

    /// <summary>Replaces an aggregate's children, including a child that is the aggregate itself.</summary>
    /// <param name="aggregate">Aggregate to rewrite. Not null.</param>
    /// <param name="children">New children. Not null. May contain <paramref name="aggregate"/>.</param>
    private static void SetChildren(AggregateException aggregate, Exception[] children)
    {
        var inners = typeof(AggregateException).GetField("_innerExceptions", BindingFlags.Instance | BindingFlags.NonPublic);
        var view = typeof(AggregateException).GetField("_rocView", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(inners);
        Assert.NotNull(view);
        inners.SetValue(aggregate, children);
        view.SetValue(aggregate, new System.Collections.ObjectModel.ReadOnlyCollection<Exception>(children));
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

    /// <summary>
    /// Encodes every string of length 0 through 6 on the escape alphabet and decodes it.
    /// The alphabet is backslash, pipe, <c>r</c>, <c>n</c>, <c>&gt;</c>, dash, <c>x</c>, CR, and LF.
    /// </summary>
    private static void AssertEscapeAlphabet()
    {
        var alphabet = new[] { '\\', '|', 'r', 'n', '>', '-', 'x', '\r', '\n' };
        var buffer = new char[6];
        AssertRoundTrip(string.Empty);
        for (var length = 1; length <= buffer.Length; length++)
        {
            var remaining = 1;
            for (var n = 0; n < length; n++)
                remaining *= alphabet.Length;
            for (var step = 0; step < remaining; step++)
            {
                var value = step;
                for (var i = length - 1; i >= 0; i--)
                {
                    buffer[i] = alphabet[value % alphabet.Length];
                    value /= alphabet.Length;
                }

                AssertRoundTrip(new string(buffer, 0, length));
            }
        }
    }

    /// <summary>Encodes one string and checks that decoding returns it, with no raw line break left in the encoding.</summary>
    /// <param name="raw">Original text. Not null. At most 6 characters in the alphabet walk.</param>
    private static void AssertRoundTrip(string raw)
    {
        var escaped = ExceptionReport.EscapeLine(raw);
        var decoded = Unescape(escaped);
        if (!string.Equals(raw, decoded, StringComparison.Ordinal))
            Assert.Equal(raw, decoded);
        if (escaped.IndexOf('\r') >= 0 || escaped.IndexOf('\n') >= 0 || escaped.Contains("-->", StringComparison.Ordinal))
            Assert.Fail("encoded text is still ambiguous: " + raw);
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
