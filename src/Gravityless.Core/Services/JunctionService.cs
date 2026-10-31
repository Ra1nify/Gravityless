using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gravityless.Core.Services;

public class JunctionService
{
    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        out ByHandleFileInformation info);

    /// <summary>
    /// True when the file has more than one directory entry, i.e. it is a hard link.
    /// Hard links carry no reparse point, so FileAttributes cannot be used to detect them.
    /// </summary>
    public static bool IsFileHardLink(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) return false;

            return info.NumberOfLinks > 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces <paramref name="linkPath"/> with a hard link to <paramref name="targetPath"/>,
    /// moving the current content into the target when the target does not exist yet.
    /// Returns false when the file system refuses the link (different volume, FAT32, ...).
    /// </summary>
    public static bool EnsureFileHardLink(string linkPath, string targetPath)
    {
        try
        {
            var linkExists = File.Exists(linkPath);
            var targetExists = File.Exists(targetPath);

            // Already linked to the target: nothing to do.
            if (linkExists && targetExists && IsFileHardLink(linkPath))
            {
                return true;
            }

            if (linkExists && !targetExists)
            {
                // Adopt the existing instance copy as the canonical one.
                var parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Move(linkPath, targetPath);
                targetExists = true;
            }

            if (!targetExists) return false;

            if (linkExists)
            {
                try { File.Delete(linkPath); } catch { return false; }
            }

            return CreateFileHardLink(linkPath, targetPath) && IsFileHardLink(linkPath);
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("junction.hardlink", ex);
            return false;
        }
    }

    /// <summary>Creates an NTFS hard link; falls back to a file copy if the link call fails.</summary>
    public static bool CreateFileHardLink(string linkPath, string targetPath)
    {
        if (!File.Exists(targetPath)) return false;

        if (File.Exists(linkPath))
        {
            try { File.Delete(linkPath); } catch { }
        }

        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            if (CreateHardLink(linkPath, targetPath, IntPtr.Zero))
            {
                return true;
            }
        }
        catch { }

        try
        {
            File.Copy(targetPath, linkPath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RemoveFileHardLink(string linkPath)
    {
        if (File.Exists(linkPath))
        {
            try { File.Delete(linkPath); } catch { }
        }
    }

    /// <summary>Creates a directory junction via <c>mklink /J</c>; replaces an existing junction.</summary>
    public static bool CreateJunction(string junctionPath, string targetPath)
    {
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        if (Directory.Exists(junctionPath))
        {
            RemoveJunction(junctionPath);
        }

        var parentDir = Path.GetDirectoryName(junctionPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        process?.WaitForExit();

        return Directory.Exists(junctionPath);
    }

    /// <summary>Removes a junction entry without touching the link target.</summary>
    public static void RemoveJunction(string junctionPath)
    {
        if (!Directory.Exists(junctionPath))
            return;

        var di = new DirectoryInfo(junctionPath);
        // ReparsePoint marks the entry as a link; Delete removes only the link itself
        if ((di.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
        {
            di.Delete();
        }
        else
        {
            Directory.Delete(junctionPath, true);
        }
    }

    public static bool IsJunction(string path)
    {
        if (!Directory.Exists(path))
            return false;

        var di = new DirectoryInfo(path);
        return (di.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }
}
