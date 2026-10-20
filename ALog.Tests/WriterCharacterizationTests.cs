using ALog.Core;
using ALog.Formatters;
using ALog.Writers.Cloud;
using ALog.Writers.Console;
using ALog.Writers.File;
using ALog.Writers.Http;

namespace ALog.Tests;

/// <summary>
/// Pins what each writer emits for a template property and a context property.
/// Default output that drops the template bag is marked with the Ist-Verhalten note.
/// No production code is changed.
/// </summary>
public sealed class WriterCharacterizationTests
{
    /// <summary>
    /// Console fallback keeps the rendered message and context, and drops the template bag.
    /// JsonFormatter writes both.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Console_DefaultOmitsTemplateProperties_JsonKeepsThem()
    {
        await Task.CompletedTask;
        var fallback = CaptureConsole(() =>
        {
            using var writer = new ConsoleLogWriter(useColors: false);
            writer.Write(Sample());
        });
        Assert.Contains("hello alpha", fallback, StringComparison.Ordinal);
        Assert.Contains("User=ada", fallback, StringComparison.Ordinal);
        // Ist-Verhalten 3a113bf, Befund an DC gemeldet
        Assert.DoesNotContain("A=alpha", fallback, StringComparison.Ordinal);

        var json = CaptureConsole(() =>
        {
            using var writer = new ConsoleLogWriter(useColors: false, formatter: new JsonFormatter());
            writer.Write(Sample());
        });
        Assert.Contains("\"A\":\"alpha\"", json, StringComparison.Ordinal);
        Assert.Contains("\"User\":\"ada\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// File fallback keeps the rendered message and context, and drops the template bag.
    /// JsonFormatter writes both. The file is deleted afterwards.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task File_DefaultOmitsTemplateProperties_JsonKeepsThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "alog-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plainPath = Path.Combine(directory, "plain.log");
            var jsonPath = Path.Combine(directory, "json.log");
            using (var plain = new FileLogWriter(plainPath))
            {
                plain.Write(Sample());
                await plain.WriteAsync(Sample());
            }

            using (var json = new FileLogWriter(jsonPath, new JsonFormatter()))
            {
                json.Write(Sample());
            }

            var plainText = await File.ReadAllTextAsync(plainPath);
            Assert.Contains("hello alpha", plainText, StringComparison.Ordinal);
            Assert.Contains("User=ada", plainText, StringComparison.Ordinal);
            // Ist-Verhalten 3a113bf, Befund an DC gemeldet
            Assert.DoesNotContain("A=alpha", plainText, StringComparison.Ordinal);

            var jsonText = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("\"A\":\"alpha\"", jsonText, StringComparison.Ordinal);
            Assert.Contains("\"User\":\"ada\"", jsonText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// HTTP default JSON carries context and not the template bag, with content type text/plain.
    /// JsonFormatter sends application/json and includes the template bag.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Http_DefaultOmitsTemplateProperties_JsonKeepsThem()
    {
        var plainHandler = new CapturingHttpHandler();
        using (var client = new HttpClient(plainHandler))
        using (var writer = new HttpLogWriter("https://example.test/logs", httpClient: client))
        {
            writer.Write(Sample());
            await writer.WriteAsync(Sample());
        }

        Assert.Equal(2, plainHandler.Requests.Count);
        Assert.All(plainHandler.Requests, request =>
        {
            Assert.Contains("User", request.Body, StringComparison.Ordinal);
            Assert.Contains("ada", request.Body, StringComparison.Ordinal);
            Assert.Contains("hello alpha", request.Body, StringComparison.Ordinal);
            // Ist-Verhalten 3a113bf, Befund an DC gemeldet
            Assert.DoesNotContain("\"A\"", request.Body, StringComparison.Ordinal);
            Assert.Equal("text/plain; charset=utf-8", request.ContentType);
        });

        var jsonHandler = new CapturingHttpHandler();
        using var jsonClient = new HttpClient(jsonHandler);
        using var jsonWriter = new HttpLogWriter(
            "https://example.test/logs",
            formatter: new JsonFormatter(),
            httpClient: jsonClient);
        jsonWriter.Write(Sample());
        var body = jsonHandler.Requests[0].Body;
        Assert.Contains("\"A\":\"alpha\"", body, StringComparison.Ordinal);
        Assert.Contains("\"User\":\"ada\"", body, StringComparison.Ordinal);
        Assert.Contains("application/json", jsonHandler.Requests[0].ContentType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Azure default track payload puts context on the properties map and uses the rendered message.
    /// The template bag is not copied. JsonFormatter embeds both inside the message string.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Azure_DefaultOmitsTemplateProperties_JsonKeepsThem()
    {
        var plainHandler = new CapturingHttpHandler();
        using (var client = new HttpClient(plainHandler))
        using (var writer = new AzureLogWriter(Key, httpClient: client))
        {
            writer.Write(Sample());
            await writer.WriteAsync(Sample());
            var payload = writer.BuildTrackPayload(Sample());
            Assert.Contains("\"User\":\"ada\"", payload, StringComparison.Ordinal);
            Assert.Contains("hello alpha", payload, StringComparison.Ordinal);
            // Ist-Verhalten 3a113bf, Befund an DC gemeldet
            Assert.DoesNotContain("\"A\":\"alpha\"", payload, StringComparison.Ordinal);
        }

        Assert.Equal(2, plainHandler.Requests.Count);
        Assert.All(plainHandler.Requests, request =>
        {
            Assert.Contains("\"User\":\"ada\"", request.Body, StringComparison.Ordinal);
            // Ist-Verhalten 3a113bf, Befund an DC gemeldet
            Assert.DoesNotContain("\"A\":\"alpha\"", request.Body, StringComparison.Ordinal);
        });

        var jsonHandler = new CapturingHttpHandler();
        using var jsonClient = new HttpClient(jsonHandler);
        using var jsonWriter = new AzureLogWriter(Key, formatter: new JsonFormatter(), httpClient: jsonClient);
        jsonWriter.Write(Sample());
        Assert.Contains("\\u0022A\\u0022:\\u0022alpha\\u0022", jsonHandler.Requests[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a formatter, AWS sends the rendered message, the template property, and the context.
    /// JsonFormatter sends both inside the formatted message and nothing is appended after it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Aws_DefaultSendsRenderedMessageOnly_JsonKeepsProperties()
    {
        var plain = new RecordingAwsTransport();
        using (var writer = new AwsCloudWatchWriter("group", "stream", plain))
        {
            writer.Write(Sample());
            await writer.WriteAsync(Sample());
        }

        Assert.Equal(2, plain.Calls.Count);
        Assert.All(plain.Calls, call =>
        {
            Assert.Contains("hello alpha", call.Message, StringComparison.Ordinal);
            Assert.Contains("ada", call.Message, StringComparison.Ordinal);
            Assert.Contains("A=", call.Message, StringComparison.Ordinal);
        });

        var json = new RecordingAwsTransport();
        using var jsonWriter = new AwsCloudWatchWriter("group", "stream", json, new JsonFormatter());
        jsonWriter.Write(Sample());
        Assert.Contains("\"A\":\"alpha\"", json.Calls[0].Message, StringComparison.Ordinal);
        Assert.Contains("\"User\":\"ada\"", json.Calls[0].Message, StringComparison.Ordinal);
    }

    private const string Key = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// Builds the event every writer characterization uses.
    /// Message is the rendered template. Properties hold A. Context holds User.
    /// </summary>
    /// <returns>One info event at the fixed coverage clock.</returns>
    private static LogEvent Sample()
        => new(
            CoverageClock.UtcNow,
            LogLevel.Info,
            "hello alpha",
            null,
            new Dictionary<string, object> { ["User"] = "ada" },
            new Dictionary<string, object> { ["A"] = "alpha" },
            "hello {A}");

    /// <summary>
    /// Redirects <see cref="Console.Out"/> for one action and restores it afterwards.
    /// </summary>
    /// <param name="body">Writes that should land in the returned text.</param>
    /// <returns>Everything written to console output during <paramref name="body"/>.</returns>
    /// <remarks>Restores the previous writer even when <paramref name="body"/> throws.</remarks>
    private static string CaptureConsole(Action body)
    {
        var previous = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            body();
            return buffer.ToString();
        }
        finally
        {
            Console.SetOut(previous);
        }
    }
}
