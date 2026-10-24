using System.Diagnostics;
using System.Globalization;

namespace AlogComparison.SpeedTests;

/// <summary>
/// The verifier's self-check and the Serilog time-zone matrix. These run without <c>ALOG_SPEED</c>.
/// </summary>
public sealed class HarnessChecksTests
{
    /// <summary>
    /// <c>verify-self</c> exits 0 and the verifier prints a reason.
    /// </summary>
    /// <returns>A task that finishes after the process exits.</returns>
    /// <remarks>Does not throw. The wait is 60 seconds. Not thread-safe. One process at a time.</remarks>
    [Fact(Timeout = 60000)]
    public async Task VerifySelf_PrintsTheReason()
    {
        await Task.CompletedTask;
        var (code, stdout, stderr) = Run("verify-self");
        Assert.True(code == 0, "verify-self failed: " + stderr);
        Assert.Contains("Verify:", stderr, StringComparison.Ordinal);
        Assert.Contains("verify-self plain-ok matched", stderr, StringComparison.Ordinal);
        Assert.Contains("verify-self plain-backwards matched", stderr, StringComparison.Ordinal);
        Assert.Contains("verify-self json-offset matched", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 5% tie accepts an equal median and a median on the limit, and rejects a slower one.
    /// </summary>
    /// <returns>A task that finishes after the comparisons.</returns>
    /// <remarks>Does not throw. Does not start a cell. The wait is 5 seconds. Safe for the test thread.</remarks>
    [Fact(Timeout = 5000)]
    public async Task TieMargin_AllowsFivePercentAndRejectsMore()
    {
        await Task.CompletedTask;
        Assert.Equal(2d, RankedSpeedTests.Median(3, 1, 2));
        Assert.Equal(5d, RankedSpeedTests.Median(5, 1, 5));
        var fastest = 100.0;
        var limit = fastest * RankedSpeedTests.TieFactor;
        Assert.Null(RankedSpeedTests.RankProblem(limit, fastest, "nlog"));
        Assert.Null(RankedSpeedTests.RankProblem(fastest, fastest, "nlog"));
        Assert.NotNull(RankedSpeedTests.RankProblem(limit + 0.001, fastest, "nlog"));
        Assert.Contains("within the 5% tie", RankedSpeedTests.RankReason(limit, fastest, "nlog"), StringComparison.Ordinal);
        Assert.Contains("at least as fast", RankedSpeedTests.RankReason(fastest - 1, fastest, "nlog"), StringComparison.Ordinal);
        Assert.Contains("slower than the 5% tie", RankedSpeedTests.RankReason(limit + 0.001, fastest, "nlog"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Each zone's child writes a UTC plain stamp and a UTC JSON stamp.
    /// </summary>
    /// <returns>A task that finishes after the four children exit.</returns>
    /// <remarks>
    /// Does not throw. A zone the operating system ignores still passes when the stamps are UTC.
    /// The wait is 120 seconds. Not thread-safe.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task TzMatrix_StampsAreUtc()
    {
        await Task.CompletedTask;
        var (code, stdout, stderr) = Run("tz-matrix");
        Assert.True(code == 0, "tz-matrix failed: " + stderr + " " + stdout);
        Assert.Contains("TZ UTC utc", stdout, StringComparison.Ordinal);
        Assert.Contains("TZ Europe/Berlin utc", stdout, StringComparison.Ordinal);
        Assert.Contains("TZ Asia/Kolkata utc", stdout, StringComparison.Ordinal);
        Assert.Contains("TZ Pacific/Chatham utc", stdout, StringComparison.Ordinal);
    }

    /// <summary>Runs one harness command and returns its exit code and streams.</summary>
    /// <param name="command">Command name. Not null.</param>
    /// <returns>Exit code, stdout, and stderr.</returns>
    /// <remarks>
    /// Does not throw. The host is <c>DOTNET_ROOT</c> or <c>PATH</c>, never a hard-coded user path.
    /// Waits at most 90 seconds. Not thread-safe.
    /// </remarks>
    private static (int Code, string Stdout, string Stderr) Run(string command)
    {
        var root = RepoRoot();
        var dll = Path.Combine(root, "eng", "comparison-bench", "bin", "Release", "net10.0", "ALog.Comparison.dll");
        Assert.True(File.Exists(dll), "Release comparison build missing: " + dll);
        var psi = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add(command);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            process.WaitForExit();
            return (124, "", "timeout");
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>Walks up from the test output to the repo root.</summary>
    /// <returns>The directory that contains <c>global.json</c> and the bench project. Not null.</returns>
    /// <remarks>Does not throw when the root is found. Throws when it is not. Not thread-safe.</remarks>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json"))
                && File.Exists(Path.Combine(dir.FullName, "eng", "comparison-bench", "ALog.Comparison.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    /// <summary>The dotnet host on <c>DOTNET_ROOT</c> or <c>PATH</c>.</summary>
    /// <returns>A path or the name <c>dotnet</c>. Not null.</returns>
    /// <remarks>Does not throw. Does not use a hard-coded user directory. Safe for the test thread.</remarks>
    private static string DotnetHost()
    {
        var file = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var underRoot = Path.Combine(root, file);
            if (File.Exists(underRoot))
                return underRoot;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(parts[i], file);
            if (File.Exists(candidate))
                return candidate;
        }

        return file;
    }
}
