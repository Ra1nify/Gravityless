using System.Text;
using System.Text.Json;

namespace Gravityless.Core.Services;

/// <summary>
/// Writes JSON state files without the risk of a half-written document.
///
/// Every persistence point in the app (instances, accounts, projects, theme, proxy) shares
/// the same shape: serialise the in-memory list, then overwrite a file. A plain
/// <see cref="File.WriteAllText(string, string)"/> leaves a truncated file if the write is
/// interrupted, and throws when the file is briefly locked — which silently desynchronises
/// the in-memory state from disk. Writing to a sibling file and swapping avoids both.
/// </summary>
public static class JsonStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Serialises <paramref name="value"/> and replaces <paramref name="path"/> atomically.
    /// Throws on failure so callers can roll their in-memory change back.
    /// </summary>
    public static void Write<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, Options);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);

        // File.Move with overwrite is atomic on the same volume and preserves the target on
        // failure, unlike deleting the target first.
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Write that reports failure instead of throwing. Use where the caller has already
    /// committed its in-memory change and cannot roll back meaningfully.
    /// </summary>
    public static bool TryWrite<T>(string path, T value)
    {
        try
        {
            Write(path, value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
