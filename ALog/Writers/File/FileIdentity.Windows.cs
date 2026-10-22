namespace ALog.Writers.File;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Compares an open file with a path by volume serial and file index.
/// </summary>
/// <remarks>
/// Uses <c>GetFileInformationByHandle</c>. Compiled only on Windows.
/// The path is opened with <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/> so the comparison
/// can run while this process already holds the file for write. A missing path is not the same file.
/// </remarks>
internal static partial class FileIdentity
{
    /// <summary>
    /// True when <paramref name="path"/> names the same file as <paramref name="handle"/>, and reports that file's length.
    /// </summary>
    /// <param name="handle">The open stream. Not null. Not closed.</param>
    /// <param name="path">The configured path. Not null.</param>
    /// <param name="openLength">The open file's length in bytes when this returns true. Zero when this returns false.</param>
    /// <returns>True when the volume and file index match. False when the path is missing, names another file, or the call fails.</returns>
    /// <remarks>Does not throw. A sharing failure while opening the path is treated as not the same file.</remarks>
    internal static bool TryMatch(SafeFileHandle handle, string path, out long openLength)
    {
        openLength = 0;
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsWindows())
            return false;

        if (handle.IsClosed || handle.IsInvalid)
            return false;

        if (!TryRead(handle, out var open))
            return false;

        try
        {
            using var named = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!TryRead(named.SafeFileHandle, out var other))
                return false;

            if (open.VolumeSerialNumber != other.VolumeSerialNumber
                || open.FileIndexHigh != other.FileIndexHigh
                || open.FileIndexLow != other.FileIndexLow)
                return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        openLength = ((long)open.FileSizeHigh << 32) | open.FileSizeLow;
        return true;
    }

    /// <summary>Reads the file id and size for <paramref name="handle"/>.</summary>
    /// <param name="handle">An open file. Not null.</param>
    /// <param name="info">The status block when this returns true.</param>
    /// <returns>True when the system call succeeds.</returns>
    /// <remarks>Does not throw. Windows only.</remarks>
    [SupportedOSPlatform("windows")]
    private static bool TryRead(SafeFileHandle handle, out ByHandleFileInformation info)
        => GetFileInformationByHandle(handle, out info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
