namespace ALog.Writers.File;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Compares an open file with a path by device and inode.
/// </summary>
/// <remarks>
/// Uses the runtime's <c>SystemNative_Stat</c> and <c>SystemNative_FStat</c> so Linux and macOS share one layout.
/// Compiled only when the build is not Windows. A missing path or a failed status call is not the same file.
/// </remarks>
internal static partial class FileIdentity
{
    /// <summary>
    /// True when <paramref name="path"/> names the same file as <paramref name="handle"/>, and reports that file's length.
    /// </summary>
    /// <param name="handle">The open stream. Not null. Not closed.</param>
    /// <param name="path">The configured path. Not null.</param>
    /// <param name="openLength">The open file's length in bytes when this returns true. Zero when this returns false.</param>
    /// <returns>True when the device and inode match. False when the path is missing, names another file, or the status call fails.</returns>
    /// <remarks>Does not throw. Does not follow a replacement of the path: a new file at the same name is a different inode.</remarks>
    internal static bool TryMatch(SafeFileHandle handle, string path, out long openLength)
    {
        openLength = 0;
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(path);
        if (handle.IsClosed || handle.IsInvalid)
            return false;

        if (FStat(handle, out var open) != 0)
            return false;

        if (Stat(path, out var named) != 0)
            return false;

        if (open.Dev != named.Dev || open.Ino != named.Ino)
            return false;

        openLength = open.Size;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        public int Flags;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long ATime;
        public long ATimeNsec;
        public long MTime;
        public long MTimeNsec;
        public long CTime;
        public long CTimeNsec;
        public long BirthTime;
        public long BirthTimeNsec;
        public long Dev;
        public long RDev;
        public long Ino;
        public uint UserFlags;
    }

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static partial int FStat(SafeFileHandle fd, out FileStatus output);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_Stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Stat(string path, out FileStatus output);
}
