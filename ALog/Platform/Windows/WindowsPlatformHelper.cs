namespace ALog.Platform.Windows;

using System;
using System.IO;

public class WindowsPlatformHelper : IPlatformHelper
{
    public string ResolveLogFilePath(string relativePath)
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "ALog", relativePath);
    }

    public string GetPlatformName() => "Windows";
}
