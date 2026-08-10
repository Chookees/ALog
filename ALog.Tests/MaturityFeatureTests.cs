using ALog;
using ALog.Config;
using ALog.Core;
using ALog.Enrichers;
using ALog.Formatters;
using ALog.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ALog.Tests;

public class MaturityFeatureTests : IDisposable
{
    public MaturityFeatureTests() => Log.Shutdown();
    public void Dispose() => Log.Shutdown();

    [Fact]
    public void MessageTemplate_Formats_And_Captures_Properties()
    {
        var (rendered, props) = MessageTemplate.Format("User {UserId} did {Action}", 7, "login");
        Assert.Equal("User 7 did login", rendered);
        Assert.Equal(7, props["UserId"]);
        Assert.Equal("login", props["Action"]);
    }

    [Fact]
    public void Enrichers_And_Templates_Reach_Writer()
    {
        var writer = new RecordingWriter();
        Log.Init(new LoggerConfig()
            .AddWriter(writer)
            .AddEnricher(new PropertyEnricher("App", "ALogTests"))
            .AddEnricher(new MachineNameEnricher())
            .SetFormatter(new JsonFormatter()));

        Log.Write(LogLevel.Info, "Order {OrderId} placed", 99);
        Log.Shutdown();

        Assert.Single(writer.Events);
        var evt = writer.Events[0];
        Assert.Equal("Order 99 placed", evt.Message);
        Assert.Equal("Order {OrderId} placed", evt.MessageTemplate);
        Assert.Equal(99, evt.Properties!["OrderId"]);
        Assert.Equal("ALogTests", evt.Properties["App"]);
        Assert.True(evt.Properties.ContainsKey(MachineNameEnricher.PropertyName));
    }

    [Fact]
    public void LoggerConfigFactory_FromOptions_Builds_Writers()
    {
        var options = new ALogOptions
        {
            MinimumLevel = "Debug",
            Formatter = "json",
            Enrichers = ["MachineName"],
            Console = new ConsoleWriterOptions { Enabled = true },
            File = new FileWriterOptions { Enabled = false }
        };

        var config = LoggerConfigFactory.FromOptions(options);
        Assert.Equal(LogLevel.Debug, config.MinimumLevel);
        Assert.Single(config.Writers);
        Assert.Single(config.Enrichers);
        Assert.NotNull(config.Formatter);
    }

    [Fact]
    public void Configuration_Bind_ALogOptions()
    {
        var json = """
        {
          "ALog": {
            "MinimumLevel": "Warn",
            "Enrichers": [ "ThreadId" ],
            "Console": { "Enabled": true, "UseColors": false },
            "File": { "Enabled": false }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = config.GetALogOptions();
        Assert.Equal("Warn", options.MinimumLevel);
        Assert.Contains("ThreadId", options.Enrichers);
        Assert.False(options.Console!.UseColors);
    }

    [Fact]
    public void Mel_Provider_Writes_Through_Engine()
    {
        var writer = new RecordingWriter();
        var loggerConfig = new LoggerConfig().AddWriter(writer).SetMinimumLevel(LogLevel.Trace);
        using var provider = new ALogLoggerProvider(loggerConfig);

        var mel = provider.CreateLogger("Tests.Category");
        mel.LogWarning("hello {Name}", "world");

        Assert.NotEmpty(writer.Events);
        Assert.Equal("Tests.Category", writer.Events[0].Category);
        Assert.Contains("world", writer.Events[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mel_DI_AddALog_Registers_Provider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddALog(o =>
            {
                o.MinimumLevel = "Info";
                o.Console = new ConsoleWriterOptions { Enabled = true };
                o.File = new FileWriterOptions { Enabled = false };
            });
        });

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("DiTest");
        logger.LogInformation("from DI");
        Assert.NotNull(logger);
    }
}
