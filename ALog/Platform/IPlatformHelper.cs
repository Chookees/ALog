namespace ALog.Platform;

public interface IPlatformHelper
{
    /// <summary>
    /// Resolves a writable file path depending on platform conventions.
    /// </summary>
    string ResolveLogFilePath(string relativePath);

    /// <summary>
    /// Returns the current platform name ("Windows", "Linux", "iOS", etc.)
    /// </summary>
    string GetPlatformName();
}
