// ALog.Comparison: ALog comparison harness (arc §8c BEN). See README.md.
//   ALog.Comparison run --runs R --agent NAME --out DIR [--seed S]   all cells, one process each (use run.sh)
//   ALog.Comparison cell <lib> <scenario> <workdir>                  one cell in this process, prints "RESULT {json}"
//   ALog.Comparison micro [BenchmarkDotNet args]                     BenchmarkDotNet (MemoryDiagnoser, ThreadingDiagnoser)
//   ALog.Comparison versions                                         package versions as JSON
//   ALog.Comparison queue-mode                                       ALog async queue_full_mode read back from the built configuration
using System.Text.Json;

namespace AlogComparison;

/// <summary>Command-line entry.</summary>
public static class Program
{
    /// <summary>Dispatches the sub-command.</summary>
    /// <param name="args">The command and its arguments. Empty prints usage.</param>
    /// <returns>The command's exit code. 2 means the command was not recognised.</returns>
    /// <remarks>
    /// Does not throw for a known command. <c>verify-self</c> and <c>tz-matrix</c> check the verifier and the
    /// Serilog UTC stamp without measuring speed. One process, the main thread. Not thread-safe.
    /// </remarks>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();
        switch (args[0])
        {
            case "run":
                return Runner.Run(args[1..]);
            case "cell" when args.Length == 4:
                return Cell.Run(args[1], args[2], args[3]);
            case "micro":
                Micro.Run(args[1..]);
                return 0;
            case "versions":
                Console.Out.WriteLine(JsonSerializer.Serialize(Libs.Versions()));
                return 0;
            case "queue-mode":
                Console.Out.WriteLine(new AlogLib(useQueue: true).QueueFullModeRecord);
                return 0;
            case "verify-self":
                return HarnessChecks.VerifySelf();
            case "tz-matrix":
                return HarnessChecks.TzMatrix();
            case "tz-child" when args.Length == 2:
                return HarnessChecks.TzChild(args[1]);
            default:
                return Usage();
        }
    }

    /// <summary>Writes the command list to standard error.</summary>
    /// <returns>2, the usage exit code.</returns>
    /// <remarks>Does not throw. Does not run a cell. Safe to call from the main thread.</remarks>
    private static int Usage()
    {
        Console.Error.WriteLine("usage: ALog.Comparison run --runs R --agent NAME --out DIR [--seed S] | cell <lib> <scenario> <workdir> | micro [...] | versions | queue-mode | verify-self | tz-matrix | tz-child <dir>");
        Console.Error.WriteLine("libs: " + string.Join(", ", Libs.All) + "; scenarios: " + string.Join(", ", Scenario.All));
        return 2;
    }
}
