namespace ALog.Tests;

/// <summary>
/// Shared file reads for tests that inspect a log while a writer may still hold it.
/// </summary>
internal static class TestFiles
{
    /// <summary>Reads a log path while a writer may still hold a write or read handle.</summary>
    /// <param name="path">The log file. Not null.</param>
    /// <returns>The file text, or empty when the path is missing.</returns>
    /// <remarks>
    /// Opens with <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/> so the read
    /// succeeds against an ALog write handle on Windows. <c>File.ReadAllText</c> asks only for
    /// <see cref="FileShare.Read"/> and throws while that write handle is open.
    /// </remarks>
    internal static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
