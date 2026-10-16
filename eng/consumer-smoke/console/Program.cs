using ALog.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Console consumer for the S4 smoke (arc_spec 4.1 point 7).
// The smoke script packs ALog and restores this project from that feed.
// This file stays out of ALog.sln so the solution build cannot skip the pack.

/// <summary>
/// Hosts one information line so CI can count where that line is written.
/// MEL keeps its default console provider. ALog is added beside it and, until
/// the console writer becomes opt-in, also writes the line to the console.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Builds the generic host, writes one <c>SMOKE-{Guid}</c> line, and stops.
    /// The file sink is configured only by the smoke script's environment
    /// (<c>ALog__File__Enabled</c> and <c>ALog__File__Path</c>). No other ALog
    /// key is set here, and the MEL providers are not cleared, so the current
    /// tree prints the line twice on the console and once in the file.
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
        // Leave MEL's console provider in place. Clearing it would hide the
        // double console line that this smoke exists to show until 2a-2.
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
