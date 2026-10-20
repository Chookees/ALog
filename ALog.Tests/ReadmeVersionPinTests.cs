// G10. The M1-02 README pin check names the file, the line number, and the line text.
// Next file: ALog.Tests/MasterBugTests.cs.

using System.Diagnostics;
using System.Text;

namespace ALog.Tests;

/// <summary>
/// G10 forms fail the README version-pin check and the fail line names line 2.
/// Control lines and the repository READMEs stay quiet.
/// </summary>
public sealed class ReadmeVersionPinTests
{
    private const string Header = "header";

    private const string FailPrefix = "FAIL M1-02 pin in ";

    /// <summary>
    /// One new install form is a pin. The fail line is the file, line 2, then the text.
    /// </summary>
    /// <param name="line">Install line that must fail the check. Not null.</param>
    /// <returns>A task that finishes when the check exits.</returns>
    /// <remarks>Line 1 is a header so the reported number is 2. Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    [Theory(Timeout = 120000)]
    [InlineData("dotnet add package ALog --version=0.2.0")]
    [InlineData("dotnet add package ALog --Version 0.2.0")]
    [InlineData("dotnet add package ALog --version [0.2.0]")]
    [InlineData("<PackageReference Include=\"ALog\" Version='0.2.0' />")]
    [InlineData("<PackageReference Include=\"ALog\" Version = \"0.2.0\" />")]
    [InlineData("<PackageReference Include=\"ALog\" VersionOverride=\"0.2.0\" />")]
    [InlineData("dotnet add package alog --version 0.2.0")]
    [InlineData("Install-Package ALog -Version 0.2.0")]
    public async Task G10Form_FailsAndReportsLineNumber(string line)
    {
        var directory = Directory.CreateTempSubdirectory("alog-m1-02-");
        try
        {
            var path = Path.Combine(directory.FullName, "README.md");
            await File.WriteAllTextAsync(path, Header + "\n" + line + "\n");
            var result = await RunPinCheck(path);
            Assert.Equal(FailPrefix + path + ":2: " + line, result.Stdout.TrimEnd('\r', '\n'));
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A PackageReference pin still fails when Include uses either quote, spaces around <c>=</c>, or any case of ALog.
    /// The fail line names line 2.
    /// </summary>
    /// <param name="line">PackageReference line that must fail the check. Not null.</param>
    /// <returns>A task that finishes when the check exits.</returns>
    /// <remarks>Line 1 is a header so the reported number is 2. Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    [Theory(Timeout = 120000)]
    [InlineData("<PackageReference Include='ALog' Version='0.2.0' />")]
    [InlineData("<PackageReference Include = \"ALog\" Version = \"0.2.0\" />")]
    [InlineData("<PackageReference Include=\"alog\" Version=\"0.2.0\" />")]
    [InlineData("<PackageReference Include=\"ALOG\" Version=\"0.2.0\" />")]
    [InlineData("<PackageReference Include='ALog' Version=\"0.2.0\" />")]
    public async Task PackageReferenceInclude_FailsAndReportsLineNumber(string line)
    {
        var directory = Directory.CreateTempSubdirectory("alog-m1-02-");
        try
        {
            var path = Path.Combine(directory.FullName, "README.md");
            await File.WriteAllTextAsync(path, Header + "\n" + line + "\n");
            var result = await RunPinCheck(path);
            Assert.Equal(FailPrefix + path + ":2: " + line, result.Stdout.TrimEnd('\r', '\n'));
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A CRLF README reports the pin text with no trailing carriage return.
    /// </summary>
    /// <returns>A task that finishes when the check exits.</returns>
    /// <remarks>The file bytes use CR LF. The expected fail line has no CR. Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    [Fact(Timeout = 120000)]
    public async Task CrlfReadme_FailLineHasNoCarriageReturn()
    {
        const string line = "dotnet add package ALog --version=0.2.0";
        var directory = Directory.CreateTempSubdirectory("alog-m1-02-");
        try
        {
            var path = Path.Combine(directory.FullName, "README.md");
            await File.WriteAllTextAsync(path, Header + "\r\n" + line + "\r\n");
            var result = await RunPinCheck(path);
            var actual = result.Stdout.TrimEnd('\n');
            Assert.Equal(FailPrefix + path + ":2: " + line, actual);
            Assert.DoesNotContain("\r", actual, StringComparison.Ordinal);
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The four control lines are not pins: prerelease, build verbosity, test verbosity, and Hosting.
    /// </summary>
    /// <returns>A task that finishes when the check exits.</returns>
    /// <remarks>Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    [Fact(Timeout = 120000)]
    public async Task ControlLines_Pass()
    {
        var lines = new string[4];
        lines[0] = "dotnet add package ALog --prerelease";
        lines[1] = "dotnet build ALog.sln -v q";
        lines[2] = "dotnet test ALog.Tests -v n";
        lines[3] = "<PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.0\" />";
        var directory = Directory.CreateTempSubdirectory("alog-m1-02-");
        try
        {
            var path = Path.Combine(directory.FullName, "README.md");
            await File.WriteAllTextAsync(path, JoinLines(lines));
            var result = await RunPinCheck(path);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Existing pins still fail and name their lines: a spaced flag, <c>-v</c>, quotes,
    /// attribute order, a tab, a double space, the extensions package, and an ALog.csproj line.
    /// </summary>
    /// <returns>A task that finishes when the check exits.</returns>
    /// <remarks>The csproj line is a pin because the line names ALog. Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    [Fact(Timeout = 120000)]
    public async Task LegacyForms_StillFailAndReportLineNumbers()
    {
        var pins = new string[8];
        pins[0] = "dotnet add package ALog --version 1.0.0";
        pins[1] = "dotnet add package ALog.Extensions.Logging -v 0.2.0";
        pins[2] = "<PackageReference Include=\"ALog\" Version=\"0.2.0\" />";
        pins[3] = "<PackageReference Version=\"0.2.0\" Include=\"ALog\" />";
        pins[4] = "<PackageReference Include=\"ALog\"\tVersion=\"0.2.0\" />";
        pins[5] = "<PackageReference Include=\"ALog\"  Version=\"0.2.0\" />";
        pins[6] = "<PackageReference Include=\"ALog.Extensions.Logging\" Version=\"0.2.0\" />";
        pins[7] = "dotnet add ALog.csproj package Foo --version 1.0.0";
        var directory = Directory.CreateTempSubdirectory("alog-m1-02-");
        try
        {
            var path = Path.Combine(directory.FullName, "README.md");
            var body = new StringBuilder();
            body.Append(Header).Append('\n');
            for (var i = 0; i < pins.Length && i < 8; i++)
                body.Append(pins[i]).Append('\n');

            await File.WriteAllTextAsync(path, body.ToString());
            var result = await RunPinCheck(path);
            var expected = new StringBuilder();
            for (var i = 0; i < pins.Length && i < 8; i++)
            {
                if (i > 0)
                    expected.Append('\n');

                expected.Append(FailPrefix).Append(path).Append(':').Append(i + 2).Append(": ").Append(pins[i]);
            }

            Assert.Equal(expected.ToString(), result.Stdout.TrimEnd('\r', '\n'));
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The two repository READMEs have no install pin.</summary>
    /// <returns>A task that finishes when both checks exit.</returns>
    /// <remarks>At most two files. Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    [Fact(Timeout = 120000)]
    public async Task RepositoryReadmes_Pass()
    {
        var root = RepoRoot();
        var files = new string[2];
        files[0] = Path.Combine(root, "README.md");
        files[1] = Path.Combine(root, "ALog.Extensions.Logging", "README.md");
        for (var i = 0; i < files.Length && i < 2; i++)
        {
            var result = await RunPinCheck(files[i]);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(0, result.ExitCode);
        }
    }

    /// <summary>Joins at most four lines with newlines.</summary>
    /// <param name="lines">Lines to join. Not null. Length is at most 4.</param>
    /// <returns>The lines, each ending with a newline.</returns>
    private static string JoinLines(string[] lines)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Length && i < 4; i++)
            text.Append(lines[i]).Append('\n');

        return text.ToString();
    }

    /// <summary>Runs the M1-02 fail-line mode on one file.</summary>
    /// <param name="path">File to scan. Not null. Must exist.</param>
    /// <returns>Exit code and captured text. Stdout and stderr are never null.</returns>
    /// <remarks>Each wait is a 30 s hang guard. Not thread-safe.</remarks>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPinCheck(string path)
    {
        var start = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("eng/test_s4_grep.sh");
        start.ArgumentList.Add("--m1-fail-lines");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(30_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            var killed = process.WaitForExit(30_000);
            Assert.True(killed, "pin check did not exit after kill");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(exited, "pin check did not exit. stdout=" + stdout + " stderr=" + stderr);
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Walks up from the test output to the directory that contains <c>ALog.sln</c>.</summary>
    /// <returns>The repository root. Not null.</returns>
    /// <exception cref="InvalidOperationException">The walk passed eight directories without finding the solution.</exception>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ALog.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("ALog.sln was not found.");
    }
}
