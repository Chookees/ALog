using ALog.Config;
using ALog.Core;
using ALog.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALog.Tests;

/// <summary>
/// A real host must log ASP.NET Core and EF Core text the way MEL does, with the template values under their names (C3-03).
/// </summary>
public sealed class MelHostBindingTests
{
    /// <summary>
    /// <c>GET /</c> on a test server produces the same "Request starting" and "Request finished" text as MEL,
    /// and the properties are <c>Method</c>, <c>Scheme</c>, <c>Host</c>, <c>Path</c>, and <c>StatusCode</c>.
    /// An EF Core SQLite command keeps <c>elapsed</c> and <c>commandText</c> under those names.
    /// </summary>
    /// <returns>A completed task after the host has stopped.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the text or a property name does not match MEL.</exception>
    /// <remarks>
    /// Starts an in-memory test server and an in-memory SQLite database. Does not open a TCP port.
    /// One host, one request, then one database round-trip. Safe to run alone; the suite does not run tests in parallel.
    /// </remarks>
    [Fact(Timeout = 60000)]
    public async Task HostAndEfCore_MatchMelText_AndKeepPropertyNames()
    {
        var writer = new RecordingWriter();
        var memory = new MemoryMelProvider();
        var config = new LoggerConfig()
            .AddWriter(writer)
            .SetMinimumLevel(ALog.LogLevel.Trace)
            .UseAsync(false);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "C3MelHost",
            EnvironmentName = "Production",
            ContentRootPath = Path.GetTempPath()
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", Microsoft.Extensions.Logging.LogLevel.Information);
        builder.Logging.AddProvider(memory);
        builder.Logging.AddALog(config);
        builder.Services.AddDbContext<MelProbeContext>(options => options.UseSqlite(connection));

        var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        try
        {
            var client = app.GetTestClient();
            var response = await client.GetAsync("/");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MelProbeContext>();
            await db.Database.EnsureCreatedAsync();
            db.Rows.Add(new MelProbeRow { Name = "c3" });
            await db.SaveChangesAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        AssertSameText(memory, writer, "Request starting ");
        AssertSameText(memory, writer, "Request finished ");
        var starting = Find(writer, "Request starting ");
        var finished = Find(writer, "Request finished ");
        AssertProperty(starting, "Method", "GET");
        AssertProperty(starting, "Scheme", "http");
        AssertProperty(starting, "Host", "localhost");
        AssertProperty(starting, "Path", "/");
        AssertProperty(finished, "Method", "GET");
        AssertProperty(finished, "Scheme", "http");
        AssertProperty(finished, "Host", "localhost");
        AssertProperty(finished, "Path", "/");
        AssertProperty(finished, "StatusCode", 200);

        AssertSameText(memory, writer, "Executed DbCommand ");
        var command = Find(writer, "Executed DbCommand ");
        Assert.NotNull(command.Properties);
        Assert.True(command.Properties!.ContainsKey("elapsed"), "Missing property elapsed.");
        Assert.True(command.Properties.ContainsKey("commandText"), "Missing property commandText.");
        var commandText = Assert.IsType<string>(command.Properties["commandText"]);
        Assert.False(string.IsNullOrWhiteSpace(commandText));
        Assert.NotNull(command.Properties["elapsed"]);
    }

    /// <summary>
    /// Every ALog line with the prefix equals the MEL line in the same position.
    /// </summary>
    /// <param name="memory">MEL provider that stored the formatter text.</param>
    /// <param name="writer">ALog writer that stored the bridge output.</param>
    /// <param name="prefix">Start of the message, including the trailing space. Compared ordinally.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the counts or any line differ.</exception>
    /// <remarks>
    /// No I/O. Both sequences must be non-empty.
    /// </remarks>
    private static void AssertSameText(MemoryMelProvider memory, RecordingWriter writer, string prefix)
    {
        var mel = Texts(memory.Entries, prefix);
        var alog = Texts(writer.Events, prefix);
        Assert.NotEmpty(mel);
        Assert.Equal(mel, alog);
    }

    /// <summary>
    /// Returns the first ALog event whose message starts with the prefix.
    /// </summary>
    /// <param name="writer">Writer filled by the host.</param>
    /// <param name="prefix">Start of the message. Compared ordinally.</param>
    /// <returns>The matching event.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no event matches.</exception>
    /// <remarks>
    /// No I/O.
    /// </remarks>
    private static LogEvent Find(RecordingWriter writer, string prefix)
    {
        for (var i = 0; i < writer.Events.Count; i++)
        {
            var message = writer.Events[i].Message;
            if (message is not null && message.StartsWith(prefix, StringComparison.Ordinal))
                return writer.Events[i];
        }

        throw new InvalidOperationException("No ALog event starts with " + prefix);
    }

    /// <summary>
    /// Collects messages that start with the prefix, in the order they were logged.
    /// </summary>
    /// <param name="events">ALog events.</param>
    /// <param name="prefix">Start of the message. Compared ordinally.</param>
    /// <returns>The matching messages. Empty when none match.</returns>
    /// <remarks>
    /// No I/O. Allocates the result list.
    /// </remarks>
    private static List<string> Texts(List<LogEvent> events, string prefix)
    {
        var found = new List<string>();
        for (var i = 0; i < events.Count; i++)
        {
            var message = events[i].Message;
            if (message is not null && message.StartsWith(prefix, StringComparison.Ordinal))
                found.Add(message);
        }

        return found;
    }

    /// <summary>
    /// Collects MEL formatter texts that start with the prefix, in the order they were logged.
    /// </summary>
    /// <param name="entries">Lines the in-memory MEL provider stored.</param>
    /// <param name="prefix">Start of the text. Compared ordinally.</param>
    /// <returns>The matching texts. Empty when none match.</returns>
    /// <remarks>
    /// No I/O. Allocates the result list.
    /// </remarks>
    private static List<string> Texts(List<MemoryMelEntry> entries, string prefix)
    {
        var found = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Text.StartsWith(prefix, StringComparison.Ordinal))
                found.Add(entries[i].Text);
        }

        return found;
    }

    /// <summary>
    /// Asserts that one template property holds the expected value.
    /// </summary>
    /// <param name="evt">Event to read.</param>
    /// <param name="name">Property name.</param>
    /// <param name="expected">Value MEL put under that key.</param>
    /// <exception cref="Xunit.Sdk.XunitException">Thrown when the key is missing or the value differs.</exception>
    /// <remarks>
    /// No I/O.
    /// </remarks>
    private static void AssertProperty(LogEvent evt, string name, object expected)
    {
        Assert.NotNull(evt.Properties);
        Assert.True(evt.Properties!.ContainsKey(name), "Missing property " + name + ".");
        Assert.Equal(expected, evt.Properties[name]);
    }
}

/// <summary>
/// One row so EF Core has a table to create and read. The command text contains the type name.
/// </summary>
internal sealed class MelProbeRow
{
    /// <summary>Primary key. EF assigns it.</summary>
    public int Id { get; set; }

    /// <summary>Stored text. The test writes <c>c3</c>.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// SQLite context used only to force an "Executed DbCommand" log.
/// </summary>
internal sealed class MelProbeContext : DbContext
{
    /// <summary>
    /// Stores the options the host built, including the open SQLite connection.
    /// </summary>
    /// <param name="options">Options from <c>AddDbContext</c>. Null is rejected by the base constructor.</param>
    public MelProbeContext(DbContextOptions<MelProbeContext> options)
        : base(options)
    {
    }

    /// <summary>The single table the test creates.</summary>
    public DbSet<MelProbeRow> Rows => Set<MelProbeRow>();
}

/// <summary>
/// One line captured from MEL's own formatter, before ALog sees it.
/// </summary>
/// <param name="Category">Logger category. Not used in the comparison.</param>
/// <param name="Text">Text the formatter returned.</param>
internal sealed record MemoryMelEntry(string Category, string Text);

/// <summary>
/// In-memory MEL provider registered beside ALog so the test can read the formatter text.
/// </summary>
internal sealed class MemoryMelProvider : ILoggerProvider
{
    /// <summary>Lines in the order <see cref="ILogger.Log"/> was called.</summary>
    public List<MemoryMelEntry> Entries { get; } = new();

    /// <summary>
    /// Returns a logger that appends to <see cref="Entries"/>.
    /// </summary>
    /// <param name="categoryName">Category stored on each line. Null is stored as empty.</param>
    /// <returns>A logger for that category.</returns>
    /// <remarks>
    /// No I/O. The logger keeps the provider list. Safe to call from the host's logging lock.
    /// </remarks>
    public ILogger CreateLogger(string categoryName)
        => new MemoryMelLogger(categoryName ?? string.Empty, Entries);

    /// <summary>
    /// Does nothing. The list is owned by the test.
    /// </summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// Records every enabled log call's formatter text.
/// </summary>
internal sealed class MemoryMelLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly string category;
    private readonly List<MemoryMelEntry> entries;

    /// <summary>
    /// Stores the category and the shared list.
    /// </summary>
    /// <param name="category">Category name. Empty is allowed.</param>
    /// <param name="entries">List to append to. The caller owns it.</param>
    public MemoryMelLogger(string category, List<MemoryMelEntry> entries)
    {
        this.category = category;
        this.entries = entries;
    }

    /// <summary>
    /// Records the formatter text. Does not format the template itself.
    /// </summary>
    /// <typeparam name="TState">State type MEL passed.</typeparam>
    /// <param name="logLevel">Ignored. The factory already applied its filter.</param>
    /// <param name="eventId">Ignored.</param>
    /// <param name="state">Passed to <paramref name="formatter"/>.</param>
    /// <param name="exception">Passed to <paramref name="formatter"/>.</param>
    /// <param name="formatter">MEL formatter. Null is rejected.</param>
    /// <remarks>
    /// Appends one entry. Not safe for concurrent calls on the same list; the host logs this request on one thread.
    /// </remarks>
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var text = formatter(state, exception) ?? string.Empty;
        entries.Add(new MemoryMelEntry(category, text));
    }

    /// <summary>
    /// Accepts every level except <see cref="LogLevel.None"/> so the factory's filter is the only gate.
    /// </summary>
    /// <param name="logLevel">Level the factory is asking about.</param>
    /// <returns>False only for <see cref="LogLevel.None"/>.</returns>
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        => logLevel != Microsoft.Extensions.Logging.LogLevel.None;

    /// <summary>
    /// Returns a no-op scope. The test reads messages, not scopes, from this provider.
    /// </summary>
    /// <typeparam name="TState">Scope state. Ignored.</typeparam>
    /// <param name="state">Ignored.</param>
    /// <returns>A disposable that does nothing.</returns>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => MemoryMelScope.Instance;

    /// <summary>
    /// Shared no-op scope. One instance is enough because dispose does nothing.
    /// </summary>
    private sealed class MemoryMelScope : IDisposable
    {
        /// <summary>The only instance.</summary>
        internal static readonly MemoryMelScope Instance = new();

        private MemoryMelScope()
        {
        }

        /// <summary>Does nothing.</summary>
        public void Dispose()
        {
        }
    }
}
