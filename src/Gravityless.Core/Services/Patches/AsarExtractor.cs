using System.Text;
using System.Text.Json;

namespace Gravityless.Core.Services.Patches;

/// <summary>
/// Managed-code Electron ASAR archive extractor used when unpacking Antigravity resources/app.
/// </summary>
internal static class AsarExtractor
{
    /// <summary>Unpacks an Electron ASAR archive into <paramref name="outputDir"/> using only managed code.</summary>
    public static bool ExtractAsar(string asarPath, string outputDir, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!File.Exists(asarPath))
        {
            errorMessage = $"ASAR 文件不存在: {asarPath}";
            return false;
        }

        try
        {
            using var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            // ASAR header: 4 x uint32 (magic, header size, header size - 4, JSON length).
            uint magic = br.ReadUInt32(); // 4
            uint totalHeaderSize = br.ReadUInt32();
            _ = br.ReadUInt32(); // headerSizeMinus4
            uint jsonLength = br.ReadUInt32();

            if (magic != 4 || jsonLength == 0 || jsonLength > totalHeaderSize)
            {
                errorMessage = "无效或损坏的 ASAR 文件头结构。";
                return false;
            }

            // Header size counts itself, so file data starts at 8 + totalHeaderSize.
            byte[] jsonBytes = br.ReadBytes((int)jsonLength);
            string jsonStr = Encoding.UTF8.GetString(jsonBytes);

            using var doc = JsonDocument.Parse(jsonStr);
            if (!doc.RootElement.TryGetProperty("files", out var rootFiles))
            {
                errorMessage = "ASAR 头部缺少 files 目录索引。";
                return false;
            }

            long baseOffset = 8 + totalHeaderSize;
            var unpackedDir = Path.Combine(
                Path.GetDirectoryName(asarPath) ?? "",
                Path.GetFileName(asarPath) + ".unpacked"
            );

            Directory.CreateDirectory(outputDir);

            ExtractDirectoryRecursive(fs, baseOffset, rootFiles, outputDir, unpackedDir, "");

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"解包 ASAR 异常: {ex.Message}";
            return false;
        }
    }

    private static void ExtractDirectoryRecursive(
        FileStream asarStream,
        long baseOffset,
        JsonElement dirElement,
        string targetDir,
        string unpackedDir,
        string relativePath)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var prop in dirElement.EnumerateObject())
        {
            var name = prop.Name;
            var item = prop.Value;
            var destPath = Path.Combine(targetDir, name);
            var itemRelPath = string.IsNullOrEmpty(relativePath) ? name : relativePath + "/" + name;

            if (item.TryGetProperty("files", out var subFiles))
            {
                ExtractDirectoryRecursive(asarStream, baseOffset, subFiles, destPath, unpackedDir, itemRelPath);
                continue;
            }

            bool isUnpacked = item.TryGetProperty("unpacked", out var u) && u.GetBoolean();
            if (isUnpacked)
            {
                var srcUnpacked = Path.Combine(unpackedDir, itemRelPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(srcUnpacked))
                {
                    var parent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.Copy(srcUnpacked, destPath, true);
                }
                continue;
            }

            long size = item.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
            long offset = 0;
            if (item.TryGetProperty("offset", out var offsetProp))
            {
                long.TryParse(offsetProp.GetString() ?? "0", out offset);
            }

            var fileParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(fileParent)) Directory.CreateDirectory(fileParent);

            if (size == 0)
            {
                File.WriteAllBytes(destPath, Array.Empty<byte>());
            }
            else
            {
                asarStream.Seek(baseOffset + offset, SeekOrigin.Begin);
                using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[81920];
                long remaining = size;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = asarStream.Read(buffer, 0, toRead);
                    if (read <= 0) break;
                    destStream.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }
    }
}
