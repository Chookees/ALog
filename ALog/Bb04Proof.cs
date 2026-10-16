namespace ALog;

/// <summary>
/// Throwaway F-BB-04 proof. This file exists only on the proof branch.
/// The unused local is a compiler warning. CI sets CI=true, so TreatWarningsAsErrors
/// turns that warning into an error and the build step goes red.
/// </summary>
internal static class Bb04Proof
{
    /// <summary>
    /// Returns zero and leaves a local assigned but unused.
    /// </summary>
    /// <returns>Always 0. The assigned local is the warning under proof.</returns>
    /// <remarks>
    /// CS0219 is not in the analyzer ratchet, so the suggestion block does not hide it.
    /// </remarks>
    public static int Unused()
    {
        int value = 1;
        return 0;
    }
}
