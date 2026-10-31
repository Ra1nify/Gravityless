using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gravityless.Core.Services;

public class JunctionService
{
    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

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
