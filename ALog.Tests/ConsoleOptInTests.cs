using System.Collections;
using ALog.Config;
using ALog.Extensions.Logging;
using ALog.Writers.Console;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

/// <summary>
/// Pins A2a-61 (DC-AB2): configuration adds a console writer only when
/// <c>ALog:Console:Enabled</c> is true. A host that keeps the Microsoft console
/// provider therefore prints each line once. An explicit <c>AddWriter</c> is separate.
/// Next file: <c>ALog/Config/LoggerConfigFactory.cs</c>.
/// </summary>
public sealed class ConsoleOptInTests : IDisposable
{
    /// <summary>Clears the static logger so a previous test cannot print into this one.</summary>
    public ConsoleOptInTests()
    {
        Log.Shutdown();
    }

    /// <summary>Clears the static logger after the test, including when an assert fails.</summary>
    public void Dispose()
    {
        Log.Shutdown();
    }

    /// <summary>
    /// A new console options object leaves the writer off.
    /// Callers opt in by setting <see cref="ConsoleWriterOptions.Enabled"/>.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task Enabled_DefaultsToFalse()
    {
        await Task.CompletedTask;
        var options = new ConsoleWriterOptions();
        Assert.False(options.Enabled);
    }

    /// <summary>
    /// <see cref="LoggerConfigFactory.FromOptions"/> adds no console writer when the
    /// console section is missing, and exactly one when it is enabled.
    /// An enabled-false section also adds none.
    /// </summary>
    /// <returns>A task that finishes after the asserts.</returns>
    [Fact(Timeout = 30000)]
    public async Task FromOptions_AddsConsoleWriterOnlyWhenEnabled()
    {
        await Task.CompletedTask;
        var missing = LoggerConfigFactory.FromOptions(new ALogOptions());
        Assert.Empty(missing.Writers.OfType<ConsoleLogWriter>());
        DisposeWriters(missing);

        var enabled = LoggerConfigFactory.FromOptions(new ALogOptions
        {
            Console = new ConsoleWriterOptions { Enabled = true }
        });
        Assert.Single(enabled.Writers.OfType<ConsoleLogWriter>());
        DisposeWriters(enabled);

        var disabled = LoggerConfigFactory.FromOptions(new ALogOptions
        {
            Console = new ConsoleWriterOptions { Enabled = false }
        });
        Assert.Empty(disabled.Writers.OfType<ConsoleLogWriter>());
        DisposeWriters(disabled);
    }

    /// <summary>
    /// <c>AddALog(IConfiguration)</c> with no <c>ALog:Console</c> section writes nothing
    /// to the redirected console. The Microsoft console provider is not registered here.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task AddALog_WithoutConsoleSection_WritesNothing()
    {
        await Task.CompletedTask;
        var marker = NewMarker();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ALog:MinimumLevel"] = "Info"
        }).Build();

        var text = CaptureConsole(() =>
        {
            using var provider = new ServiceCollection()
                .AddLogging(logging => logging.AddALog(configuration))
                .BuildServiceProvider();
            LogMarker(provider.GetRequiredService<ILoggerFactory>().CreateLogger("C2"), marker);
        });

        Assert.Equal(0, CountOf(text, marker));
    }

    /// <summary>
    /// <c>Log.Init(ALogOptions)</c> without a console section writes nothing.
    /// The same call with <see cref="ConsoleWriterOptions.Enabled"/> true writes the line once.
    /// </summary>
    /// <returns>A task that finishes after the asserts.</returns>
    [Fact(Timeout = 30000)]
    public async Task LogInit_WritesConsoleOnlyWhenEnabled()
    {
        await Task.CompletedTask;
        var off = NewMarker();
        var offText = CaptureConsole(() =>
        {
            Log.Init(new ALogOptions());
            Log.Write(off);
            Log.Shutdown();
        });
        Assert.Equal(0, CountOf(offText, off));

        var on = NewMarker();
        var onText = CaptureConsole(() =>
        {
            Log.Init(new ALogOptions
            {
                Console = new ConsoleWriterOptions { Enabled = true, UseColors = false }
            });
            Log.Write(on);
            Log.Shutdown();
        });
        Assert.Equal(1, CountOf(onText, on));
    }

    /// <summary>
    /// <c>AddALog(Action)</c> that leaves console options unset writes nothing.
    /// This is the options-callback entry point of A2a-61.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task AddALog_ActionWithoutConsole_WritesNothing()
    {
        await Task.CompletedTask;
        var marker = NewMarker();
        var text = CaptureConsole(() =>
        {
            using var provider = new ServiceCollection()
                .AddLogging(logging => logging.AddALog(options => options.MinimumLevel = "Info"))
                .BuildServiceProvider();
            LogMarker(provider.GetRequiredService<ILoggerFactory>().CreateLogger("C2"), marker);
        });

        Assert.Equal(0, CountOf(text, marker));
    }

    /// <summary>
    /// The usual host keeps the Microsoft console provider and has no <c>ALog:Console</c>
    /// section. One information line is printed once. Providers are not cleared.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task Host_WithoutClearProviders_PrintsEachLineOnce()
    {
        await Task.CompletedTask;
        var marker = NewMarker();
        var text = CaptureConsole(() =>
        {
            WithoutInheritedALogVariables(() =>
            {
                var contentRoot = NewEmptyDirectory();
                try
                {
                    using var host = BuildHost(contentRoot, clearProviders: false, consoleEnabled: false);
                    LogMarker(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("C2"), marker);
                }
                finally
                {
                    Directory.Delete(contentRoot, recursive: true);
                }
            });
        });

        Assert.Equal(1, CountOf(text, marker));
    }

    /// <summary>
    /// <c>AddConsole</c> plus <c>AddALog</c> on an empty configuration prints one line once.
    /// This is the Microsoft console provider beside ALog's default options.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task AddConsole_AndAddALog_PrintsEachLineOnce()
    {
        await Task.CompletedTask;
        var marker = NewMarker();
        var configuration = new ConfigurationBuilder().Build();
        var text = CaptureConsole(() =>
        {
            using var provider = new ServiceCollection()
                .AddLogging(logging =>
                {
                    logging.AddConsole();
                    logging.SetMinimumLevel(MelLogLevel.Information);
                    logging.AddALog(configuration);
                })
                .BuildServiceProvider();
            LogMarker(provider.GetRequiredService<ILoggerFactory>().CreateLogger("C2"), marker);
        });

        Assert.Equal(1, CountOf(text, marker));
    }

    /// <summary>
    /// Clearing the Microsoft providers and setting <c>ALog:Console:Enabled</c> true
    /// still prints the line, from ALog alone.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task Host_ClearProviders_AndConsoleEnabled_PrintsTheLine()
    {
        await Task.CompletedTask;
        var marker = NewMarker();
        var text = CaptureConsole(() =>
        {
            WithoutInheritedALogVariables(() =>
            {
                var contentRoot = NewEmptyDirectory();
                try
                {
                    using var host = BuildHost(contentRoot, clearProviders: true, consoleEnabled: true);
                    LogMarker(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("C2"), marker);
                }
                finally
                {
                    Directory.Delete(contentRoot, recursive: true);
                }
            });
        });

        Assert.Equal(1, CountOf(text, marker));
    }

    /// <summary>
    /// <c>AddWriter(new ConsoleLogWriter())</c> still prints when options would not
    /// add a console writer. A2a-61 does not change an explicit writer.
    /// </summary>
    /// <returns>A task that finishes after the assert.</returns>
    [Fact(Timeout = 30000)]
    public async Task ExplicitConsoleWriter_StillPrints()
    {
        await Task.CompletedTask;
        var marker = NewMarker();
        var text = CaptureConsole(() =>
        {
            Log.Init(new LoggerConfig().AddWriter(new ConsoleLogWriter(useColors: false)));
            Log.Write(marker);
            Log.Shutdown();
        });

        Assert.Equal(1, CountOf(text, marker));
    }

    /// <summary>
    /// Builds a generic host the way an application does, with an empty content root.
    /// </summary>
    /// <param name="contentRoot">Existing directory. The host reads no appsettings from anywhere else.</param>
    /// <param name="clearProviders">When true, the Microsoft console provider is removed.</param>
    /// <param name="consoleEnabled">When true, configuration sets <c>ALog:Console:Enabled</c>.</param>
    /// <returns>A host the caller disposes. It is not started.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="contentRoot"/> is empty or missing.</exception>
    private static IHost BuildHost(string contentRoot, bool clearProviders, bool consoleEnabled)
    {
        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
        {
            throw new ArgumentException("Content root must be an existing directory.", nameof(contentRoot));
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            ApplicationName = "ALog.ConsoleOptIn",
            ContentRootPath = contentRoot,
            EnvironmentName = Environments.Production
        });
        builder.Logging.SetMinimumLevel(MelLogLevel.Information);
        if (clearProviders)
        {
            builder.Logging.ClearProviders();
        }

        var values = new Dictionary<string, string?>
        {
            ["ALog:MinimumLevel"] = "Info",
            ["Logging:LogLevel:Default"] = "Information"
        };
        if (consoleEnabled)
        {
            values["ALog:Console:Enabled"] = "true";
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.AddALog(builder.Configuration);
        return builder.Build();
    }

    /// <summary>
    /// Sends console output and error into one buffer for the action, then restores both.
    /// </summary>
    /// <param name="body">Work whose console writes are captured. Null is rejected.</param>
    /// <returns>The text written while <paramref name="body"/> ran.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="body"/> is null.</exception>
    /// <remarks>
    /// Replaces process-wide console writers. Tests in this assembly do not run in parallel.
    /// The buffer is memory only.
    /// </remarks>
    private static string CaptureConsole(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var buffer = new StringWriter();
        var synced = TextWriter.Synchronized(buffer);
        Console.SetOut(synced);
        Console.SetError(synced);
        try
        {
            body();
            return buffer.ToString();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>
    /// Drops inherited <c>ALog__</c> variables for one call so the host cannot see a console section.
    /// At most 50 names are accepted, and at most 400 environment entries are scanned.
    /// Either cap throws before any variable is changed.
    /// </summary>
    /// <param name="body">Work that builds a host. Null is rejected.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="body"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when more than 50 <c>ALog__</c> variables are set, or when the process has more than 400 environment variables.</exception>
    /// <remarks>Writes process environment variables and restores them afterwards, including on failure.</remarks>
    private static void WithoutInheritedALogVariables(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        const int maxVariables = 50;
        const int maxEntries = 400;
        var saved = new List<KeyValuePair<string, string>>(maxVariables);
        var seen = 0;
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            seen++;
            if (seen > maxEntries)
            {
                throw new InvalidOperationException("Refusing to scan more than 400 environment variables.");
            }

            if (entry.Key is not string name || !name.StartsWith("ALog__", StringComparison.Ordinal))
            {
                continue;
            }

            if (saved.Count >= maxVariables)
            {
                throw new InvalidOperationException("More than 50 ALog__ variables are set.");
            }

            saved.Add(new KeyValuePair<string, string>(name, entry.Value as string ?? string.Empty));
        }

        try
        {
            for (var index = 0; index < saved.Count; index++)
            {
                Environment.SetEnvironmentVariable(saved[index].Key, null);
            }

            body();
        }
        finally
        {
            for (var index = 0; index < saved.Count; index++)
            {
                Environment.SetEnvironmentVariable(saved[index].Key, saved[index].Value);
            }
        }
    }

    /// <summary>
    /// Disposes the writers a factory built. More than 20 writers is treated as a bug in the test.
    /// </summary>
    /// <param name="config">Configuration whose writers are disposed. Null is rejected.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the configuration holds more than 20 writers.</exception>
    private static void DisposeWriters(LoggerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        const int maxWriters = 20;
        if (config.Writers.Count > maxWriters)
        {
            throw new InvalidOperationException("Refusing to dispose more than 20 writers.");
        }

        for (var index = 0; index < config.Writers.Count; index++)
        {
            config.Writers[index].Dispose();
        }
    }

    /// <summary>
    /// Creates an empty directory the host can use as its content root.
    /// </summary>
    /// <returns>The absolute path of the new directory. The caller deletes it.</returns>
    /// <exception cref="IOException">Thrown when the directory cannot be created.</exception>
    /// <remarks>Allocates one directory under the process temp path. Does not write a file.</remarks>
    private static string NewEmptyDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "alog-c2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!Directory.Exists(path))
        {
            throw new IOException("Content root was not created.");
        }

        return path;
    }

    /// <summary>
    /// Writes one information line whose rendered text contains the marker.
    /// The template is fixed so the call does not build a message template from data.
    /// </summary>
    /// <param name="logger">Microsoft logger to write to. Null is rejected.</param>
    /// <param name="marker">Token embedded in the line. Empty is rejected.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="marker"/> is empty.</exception>
    private static void LogMarker(ILogger logger, string marker)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrEmpty(marker))
        {
            throw new ArgumentException("Marker is required.", nameof(marker));
        }

        logger.LogInformation("c2-marker {Token}", marker);
    }

    /// <summary>Returns a marker that will not appear in framework startup text.</summary>
    /// <returns>A 34-character token beginning with <c>c2</c>.</returns>
    private static string NewMarker()
    {
        return "c2" + Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Counts non-overlapping occurrences of a marker, stopping at 10.
    /// </summary>
    /// <param name="text">Captured console text. Null is rejected. Empty yields 0.</param>
    /// <param name="marker">Token to find. Length must be from 1 to 200.</param>
    /// <returns>The number of hits, or 10 when the text contains at least 10.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="marker"/> is empty or longer than 200 characters.</exception>
    private static int CountOf(string text, string marker)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(marker) || marker.Length > 200)
        {
            throw new ArgumentException("Marker must be 1 to 200 characters.", nameof(marker));
        }

        const int maxHits = 10;
        var count = 0;
        var start = 0;
        while (count < maxHits)
        {
            var found = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            var next = found + marker.Length;
            if (next < start)
            {
                return count;
            }

            start = next;
        }

        return count;
    }
}
