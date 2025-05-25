namespace ALog.Platform.iOS;

using System;
using System.IO;

public class IOSPlatformHelper : IPlatformHelper
{
    public string ResolveLogFilePath(string relativePath)
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "..", "Library", "Logs");

        return Path.Combine(basePath, "ALog", relativePath);
    }

    public string GetPlatformName() => "iOS";
}