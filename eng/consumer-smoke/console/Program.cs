using ALog.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Console consumer for the S4 smoke (arc_spec 4.1 point 7).
// ALog.sln builds this project through its project reference.
// The smoke script sets ALogSmokeFromFeed and restores the packed package instead.

/// <summary>
/// Hosts one information line so CI can count where that line is written.
/// MEL keeps its default console provider. ALog is added beside it and does not
/// add a console writer, because this host sets no <c>ALog:Console:Enabled</c> key.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Builds the generic host, writes one <c>SMOKE-{Guid}</c> line, and stops.
    /// The file sink is configured only by the smoke script's environment
    /// (<c>ALog__File__Enabled</c> and <c>ALog__File__Path</c>). No other ALog
    /// key is set here, and the MEL providers are not cleared, so the line is
    /// printed once on the console (by MEL) and once in the file (by ALog).
    /// </summary>
    /// <param name="args">Arguments forwarded to the generic host. Null is rejected. Empty is valid.</param>
    /// <returns>A task that completes after the host has stopped and its writers are disposed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the generated smoke id is empty.</exception>
    /// <remarks>
    /// Reads environment configuration through the host builder. Writes one log
    /// line to the console and, when the file sink is enabled, to that file.
    /// Safe to call once per process.
    /// </remarks>
    public static async Task Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = Host.CreateApplicationBuilder(args);
        // Leave MEL's console provider in place. ALog's console writer stays off
        // because no ALog:Console:Enabled key is set, so the line is not doubled.
        builder.Logging.AddALog(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var smokeId = Guid.NewGuid();
        if (smokeId == Guid.Empty)
        {
            throw new InvalidOperationException("Smoke id was empty.");
        }

        logger.LogInformation("SMOKE-{Guid}", smokeId);
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
