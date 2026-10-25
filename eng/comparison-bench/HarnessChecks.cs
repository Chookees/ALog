// Self-check for Verify, and a child-process time-zone matrix for the Serilog UTC stamp.
using System.Diagnostics;
using System.Globalization;

namespace AlogComparison;

/// <summary>Commands the ranked tests run without measuring speed.</summary>
public static class HarnessChecks
{
    private static readonly string[] Zones = ["UTC", "Europe/Berlin", "Asia/Kolkata", "Pacific/Chatham"];

    /// <summary>
    /// Proves <see cref="Verify"/> accepts a UTC file and rejects a backwards clock, a broken sequence, and a non-zero offset.
    /// </summary>
    /// <returns>0 when every check matched. 1 when a check did not.</returns>
    /// <remarks>
    /// Does not throw. Writes one line per check to standard error. The files live under the temp directory and are deleted.
    /// Not thread-safe. One process runs it.
    /// </remarks>
    public static int VerifySelf()
    {
        var directory = Path.Combine(Path.GetTempPath(), "alog-verify-self-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ok = true;
            ok &= Expect(true, "plain-ok", directory, Plain("2026-10-24T20:10:22.904Z", "Application event processed"), ScenarioFor(1, Fmt.Plain, CallKind.Simple));
            ok &= Expect(false, "plain-backwards", directory, Plain("2026-10-24T20:10:22.904Z", "Application event processed") + Plain("2026-10-24T20:10:22.100Z", "Application event processed"), ScenarioFor(2, Fmt.Plain, CallKind.Simple));
            ok &= Expect(true, "sequence-ok", directory, Plain("2026-10-24T20:10:22.100Z", "User 0 logged in from 192.168.1.10") + Plain("2026-10-24T20:10:22.200Z", "User 1 logged in from 192.168.1.10"), ScenarioFor(2, Fmt.Plain, CallKind.Structured));
            ok &= Expect(false, "sequence-gap", directory, Plain("2026-10-24T20:10:22.100Z", "User 0 logged in from 192.168.1.10") + Plain("2026-10-24T20:10:22.200Z", "User 2 logged in from 192.168.1.10"), ScenarioFor(2, Fmt.Plain, CallKind.Structured));
            ok &= Expect(false, "json-offset", directory, Json("2026-10-24T20:10:22.9040000+02:00", 0) + "\n", ScenarioFor(1, Fmt.Json, CallKind.Structured));
            ok &= Expect(true, "json-utc", directory, Json("2026-10-24T20:10:22.9040000Z", 0) + "\n", ScenarioFor(1, Fmt.Json, CallKind.Structured));
            return ok ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory does not change the exit code.
            }
        }
    }

    /// <summary>
    /// Starts one child per time zone. Each child writes a Serilog plain line and a Serilog JSON line.
    /// Both stamps must be UTC. A zone the operating system ignores still passes when the stamps are UTC.
    /// </summary>
    /// <returns>0 when every zone wrote UTC. 1 when a child failed or a stamp was not UTC.</returns>
    /// <remarks>
    /// Does not throw. Four zones, one child each. The child is this same program with <c>tz-child</c>.
    /// Prints <c>TZ &lt;zone&gt; utc</c> for each success. Not thread-safe.
    /// </remarks>
    public static int TzMatrix()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ALog.Comparison.dll");
        var host = DotnetHost();
        var failed = false;
        for (var i = 0; i < Zones.Length; i++)
        {
            var zone = Zones[i];
            var directory = Path.Combine(Path.GetTempPath(), "alog-tz-" + i.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                if (!RunChild(host, dll, zone, directory))
                    failed = true;
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // The stamps were already read.
                }
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Writes one Serilog plain event and one Serilog JSON event, then prints both stamps.
    /// </summary>
    /// <param name="directory">Directory for the two files. Not null. Must exist.</param>
    /// <returns>0 when both files have a stamp. 1 when a write produced no stamp.</returns>
    /// <remarks>
    /// Does not throw. The process time zone is whatever the parent set. <c>UtcDateTime(@t)</c> does not use it.
    /// Prints <c>plain=</c> and <c>json=</c> on standard output. Not thread-safe.
    /// </remarks>
    public static int TzChild(string directory)
    {
        var plainPath = Path.Combine(directory, "plain.log");
        var jsonPath = Path.Combine(directory, "json.log");
        WriteSerilog(plainPath, Fmt.Plain);
        WriteSerilog(jsonPath, Fmt.Json);
        var plain = FirstStamp(plainPath, Fmt.Plain);
        var json = FirstStamp(jsonPath, Fmt.Json);
        if (plain is null || json is null)
        {
            Console.Error.WriteLine("tz-child: missing stamp");
            return 1;
        }

        Console.Out.WriteLine("plain=" + plain);
        Console.Out.WriteLine("json=" + json);
        return 0;
    }

    /// <summary>The dotnet host on PATH, or under <c>DOTNET_ROOT</c> when that variable is set.</summary>
    /// <returns>A path or the name <c>dotnet</c>. Not null.</returns>
    /// <remarks>Does not throw. Does not look at a hard-coded user directory. Safe for any thread.</remarks>
    public static string DotnetHost()
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

    /// <summary>Runs <see cref="Verify.Run"/> and compares its outcome with <paramref name="shouldPass"/>.</summary>
    /// <param name="shouldPass">True when the file is supposed to be valid.</param>
    /// <param name="name">Check name written to standard error. Not null.</param>
    /// <param name="directory">Directory for the file. Not null.</param>
    /// <param name="text">File contents. Not null.</param>
    /// <param name="scenario">What <see cref="Verify"/> expects. Not null.</param>
    /// <returns>True when the outcome matched <paramref name="shouldPass"/>.</returns>
    /// <remarks>Does not throw. Writes the check name and the verifier's own <c>Verify:</c> line. Not thread-safe.</remarks>
    private static bool Expect(bool shouldPass, string name, string directory, string text, Scenario scenario)
    {
        var path = Path.Combine(directory, name + ".log");
        File.WriteAllText(path, text);
        var result = Verify.Run(path, scenario);
        var matched = result.Ok == shouldPass;
        Console.Error.WriteLine("verify-self " + name + " " + (matched ? "matched" : "MISMATCH"));
        return matched;
    }

    /// <summary>One plain record. The stamp is placed between brackets.</summary>
    /// <param name="stamp">UTC text. Not null.</param>
    /// <param name="message">Message text. Not null.</param>
    /// <returns>One line ending in a newline.</returns>
    /// <remarks>Does not throw. The shape is what <see cref="Verify"/> treats as a record start.</remarks>
    private static string Plain(string stamp, string message)
        => "[" + stamp + "] [Information] [Bench] " + message + "\n";

    /// <summary>One JSON record with the fields <see cref="Verify"/> reads.</summary>
    /// <param name="stamp">Timestamp text, UTC or not. Not null.</param>
    /// <param name="user">User id in the message.</param>
    /// <returns>One JSON object without a newline.</returns>
    /// <remarks>Does not throw. The message matches the structured pattern, including the IP.</remarks>
    private static string Json(string stamp, int user)
        => "{\"timestamp\":\"" + stamp + "\",\"level\":\"Information\",\"message\":\"User " + user.ToString(CultureInfo.InvariantCulture) + " logged in from 192.168.1.10\",\"renderedMessage\":\"User " + user.ToString(CultureInfo.InvariantCulture) + " logged in from 192.168.1.10\",\"Ip\":\"192.168.1.10\",\"SourceContext\":\"Bench\"}";

    /// <summary>A scenario whose expected record count is <paramref name="records"/>.</summary>
    /// <param name="records">N. Warm-up is zero, so the file must contain this many records.</param>
    /// <param name="format">Plain or JSON.</param>
    /// <param name="call">Simple or structured.</param>
    /// <returns>The scenario. Not null.</returns>
    /// <remarks>Does not throw. One thread, not filtered. Used only by the self-check.</remarks>
    private static Scenario ScenarioFor(long records, Fmt format, CallKind call)
        => new("self", records, 0, format, call, 1, false, "verify-self");

    /// <summary>Starts the child for <paramref name="zone"/> and requires both stamps to be UTC.</summary>
    /// <param name="host">dotnet path. Not null.</param>
    /// <param name="dll">Harness dll. Not null.</param>
    /// <param name="zone">Time zone assigned to the child. Not null.</param>
    /// <param name="directory">Work directory. Not null.</param>
    /// <returns>True when both stamps have offset zero.</returns>
    /// <remarks>
    /// Does not throw. Waits at most 60 seconds. A child that ignores <c>TZ</c> still passes when the stamps are UTC.
    /// Prints the zone line. Not thread-safe.
    /// </remarks>
    private static bool RunChild(string host, string dll, string zone, string directory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("tz-child");
        psi.ArgumentList.Add(directory);
        psi.Environment["TZ"] = zone;
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already left.
            }

            process.WaitForExit();
            Console.Error.WriteLine("TZ " + zone + " timed out");
            return false;
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine("TZ " + zone + " exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " " + stderr);
            return false;
        }

        var plain = ReadField(stdout, "plain=");
        var json = ReadField(stdout, "json=");
        if (!IsUtc(plain) || !IsUtc(json))
        {
            Console.Error.WriteLine("TZ " + zone + " not utc plain=" + plain + " json=" + json);
            return false;
        }

        Console.Out.WriteLine("TZ " + zone + " utc plain=" + plain + " json=" + json);
        return true;
    }

    /// <summary>Writes one Serilog event into <paramref name="path"/>.</summary>
    /// <param name="path">File to create. Not null.</param>
    /// <param name="format">Plain or JSON.</param>
    /// <remarks>Does not throw for a normal write. Closes the logger so the file is flushed. Not thread-safe.</remarks>
    private static void WriteSerilog(string path, Fmt format)
    {
        var lib = new SerilogLib();
        lib.Init(new LibConfig(path, format, false));
        if (format == Fmt.Json)
            lib.Structured(0);
        else
            lib.Simple();
        lib.Close();
    }

    /// <summary>The timestamp of the first record, or null when the file has none.</summary>
    /// <param name="path">File the child wrote. Not null.</param>
    /// <param name="format">Plain or JSON.</param>
    /// <returns>The stamp text, or null.</returns>
    /// <remarks>Does not throw. Reads the first line only. Not thread-safe.</remarks>
    private static string? FirstStamp(string path, Fmt format)
    {
        if (!File.Exists(path))
            return null;
        string? line = null;
        foreach (var candidate in File.ReadLines(path))
        {
            if (candidate.Length > 0)
            {
                line = candidate;
                break;
            }
        }

        if (line is null)
            return null;
        if (format == Fmt.Plain)
        {
            if (line.Length < 2 || line[0] != '[')
                return null;
            var end = line.IndexOf(']');
            if (end <= 1)
                return null;
            return line.Substring(1, end - 1);
        }

        const string key = "\"timestamp\":\"";
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = at + key.Length;
        var stop = line.IndexOf('"', start);
        if (stop < 0)
            return null;
        return line.Substring(start, stop - start);
    }

    /// <summary>True when <paramref name="text"/> parses as a timestamp with offset zero.</summary>
    /// <param name="text">Stamp text. Null is not UTC.</param>
    /// <returns>True only for offset zero.</returns>
    /// <remarks>Does not throw. Uses the invariant culture. Safe for any thread.</remarks>
    private static bool IsUtc(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp))
            return false;
        return stamp.Offset == TimeSpan.Zero;
    }

    /// <summary>The value after <paramref name="prefix"/> on its line.</summary>
    /// <param name="text">Child output. Not null.</param>
    /// <param name="prefix">Field name including <c>=</c>. Not null.</param>
    /// <returns>The value, or null when the line is absent.</returns>
    /// <remarks>Does not throw. Stops at the line break. The scan is bounded by the text length.</remarks>
    private static string? ReadField(string text, string prefix)
    {
        var at = text.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = at + prefix.Length;
        var end = text.IndexOf('\n', start);
        if (end < 0)
            end = text.Length;
        return text.Substring(start, end - start).Trim();
    }
}
