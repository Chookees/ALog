namespace ALog.Platform.Linux;

using System;
using System.IO;

public class LinuxPlatformHelper : IPlatformHelper
{
    public string ResolveLogFilePath(string relativePath)
    {
        var basePath = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(basePath, "alog", relativePath);
    }

    public string GetPlatformName() => "Linux";
}
