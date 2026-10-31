using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

public class EngineService : IDisposable
{
    private const string ManifestUrl = "https://antigravity-hub-auto-updater-974169037036.us-central1.run.app/manifest/latest-x64-win.yml";
    private readonly HttpClient _httpClient;
    private readonly string _baseDir;

    public EngineService(string? baseDir = null)
    {
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;

        // One client for the process lifetime; the manifest fetch is the only use.
        // A timeout keeps a stalled endpoint from hanging the update check forever.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Gravityless");

        _ = CleanTempInstallersAsync(); // Fire-and-forget: constructors cannot await
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public string GetManagedCoreDir() => Path.Combine(_baseDir, "core");

    public string GetManagedExePath() => Path.Combine(GetManagedCoreDir(), "Antigravity.exe");

    public string GetTempInstallerDir() => Path.Combine(Path.GetTempPath(), "Gravityless");

    [Obsolete("Deprecated: the user locates a .gemini or custom workspace directory instead.")]
    public AntigravityEngine DetectStockEngine()
    {
        var engine = new AntigravityEngine
        {
            Type = EngineType.Stock,
            IsAvailable = false,
            StatusDescription = "未找到本机已安装的 Antigravity"
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "Programs", "antigravity", "Antigravity.exe");

        if (File.Exists(defaultPath))
        {
            engine.ExecutablePath = defaultPath;
            engine.IsAvailable = true;
            engine.Version = GetExeVersion(defaultPath);
            engine.StatusDescription = $"已在标准路径发现本机安装版 ({engine.Version})";
            return engine;
        }

        // Fallback: UninstallString from the per-user uninstall registry keys
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key != null)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    var displayName = subKey?.GetValue("DisplayName")?.ToString();
                    if (!string.IsNullOrEmpty(displayName) && displayName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
                    {
                        var uninstallStr = subKey?.GetValue("UninstallString")?.ToString();
                        if (!string.IsNullOrEmpty(uninstallStr))
                        {
                            var dir = Path.GetDirectoryName(uninstallStr.Trim('"', ' '));
                            if (dir != null)
                            {
                                var exe = Path.Combine(dir, "Antigravity.exe");
                                if (File.Exists(exe))
                                {
                                    engine.ExecutablePath = exe;
                                    engine.IsAvailable = true;
                                    engine.Version = GetExeVersion(exe);
                                    engine.StatusDescription = $"在注册表发现本机安装版 ({engine.Version})";
                                    return engine;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Unreadable registry keys are ignored
        }

        return engine;
    }

    public AntigravityEngine DetectManagedEngine()
    {
        var exePath = GetManagedExePath();
        var isAvailable = File.Exists(exePath);

        return new AntigravityEngine
        {
            Type = EngineType.Managed,
            ExecutablePath = exePath,
            IsAvailable = isAvailable,
            Version = isAvailable ? GetExeVersion(exePath) : string.Empty,
            StatusDescription = isAvailable 
                ? $"Managed 托管核心已就绪 ({GetExeVersion(exePath)})" 
                : "尚未在 core 目录部署 Managed 托管核心"
        };
    }

    public async Task<UpdateManifest?> FetchLatestManifestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        request.Headers.Add("x-app-version", "1.0.0");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var yamlContent = await response.Content.ReadAsStringAsync(cancellationToken);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var dict = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);

        if (!dict.TryGetValue("version", out var versionObj))
            return null;

        var manifest = new UpdateManifest
        {
            Version = versionObj?.ToString() ?? string.Empty
        };

        if (dict.TryGetValue("path", out var pathObj))
            manifest.Path = pathObj?.ToString() ?? "Antigravity-x64.exe";

        if (dict.TryGetValue("sha512", out var shaObj))
            manifest.Sha512 = shaObj?.ToString() ?? string.Empty;

        // The download entry is the first item of the "files" list
        if (dict.TryGetValue("files", out var filesObj) && filesObj is List<object> filesList && filesList.Count > 0)
        {
            if (filesList[0] is Dictionary<object, object> fileDict)
            {
                if (fileDict.TryGetValue("url", out var urlVal))
                    manifest.DownloadUrl = urlVal?.ToString() ?? string.Empty;
                if (fileDict.TryGetValue("size", out var sizeVal) && long.TryParse(sizeVal?.ToString(), out var sizeNum))
                    manifest.Size = sizeNum;
                if (string.IsNullOrEmpty(manifest.Sha512) && fileDict.TryGetValue("sha512", out var fSha))
                    manifest.Sha512 = fSha?.ToString() ?? string.Empty;
            }
        }

        return manifest;
    }

    /// <summary>Streams the installer into the temp directory; reuses an existing file that matches the SHA512.</summary>
    public async Task<string> DownloadInstallerAsync(
        UpdateManifest manifest, 
        IProgress<double>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        var tempDir = GetTempInstallerDir();
        Directory.CreateDirectory(tempDir);
        var destFilePath = Path.Combine(tempDir, $"Antigravity-{manifest.Version}-setup.exe");

        if (File.Exists(destFilePath) && VerifySha512(destFilePath, manifest.Sha512))
        {
            progress?.Report(1.0);
            return destFilePath;
        }

        using var response = await _httpClient.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? manifest.Size;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var fileStream = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            var buffer = new byte[65536];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                if (totalBytes > 0)
                {
                    progress?.Report((double)totalRead / totalBytes);
                }
            }
            await fileStream.FlushAsync(cancellationToken);
        }

        if (!string.IsNullOrEmpty(manifest.Sha512) && !VerifySha512(destFilePath, manifest.Sha512))
        {
            await TryDeleteFileAsync(destFilePath);
            throw new InvalidOperationException("安装包下载哈希 (SHA512) 校验失败，文件可能已损坏，已自动清理。");
        }

        return destFilePath;
    }

    /// <summary>Kills Antigravity and electron processes whose executable lives under <paramref name="dirPath"/>.</summary>
    public static void KillProcessesInDirectory(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return;
        var normalizedDir = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.ProcessName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
                    proc.ProcessName.Contains("electron", StringComparison.OrdinalIgnoreCase))
                {
                    string? exePath = null;
                    try { exePath = proc.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrEmpty(exePath) && 
                        Path.GetFullPath(exePath).StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                }
            }
            catch
            {
                // Exited or access denied
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    /// <summary>Stops core processes, runs the NSIS uninstaller silently, then purges registry keys and the core directory.</summary>
    public async Task UninstallExistingCoreAsync(CancellationToken cancellationToken = default)
    {
        var coreDir = GetManagedCoreDir();

        KillProcessesInDirectory(coreDir);

        var uninstallerPath = Path.Combine(coreDir, "Uninstall Antigravity.exe");
        if (File.Exists(uninstallerPath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = uninstallerPath,
                    // _?=<dir> keeps NSIS running in place instead of copying itself to temp and returning early
                    Arguments = $"/currentuser /S _?={coreDir}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
            }
            catch
            {
                // Uninstaller failure or timeout; the directory wipe below covers it
            }
        }

        KillProcessesInDirectory(coreDir);

        CleanAntigravityRegistry(coreDir);

        await CleanManagedCoreDirAsync();
    }

    /// <summary>Deletes uninstall keys that point at <paramref name="coreDir"/> or at a missing executable.</summary>
    private static void CleanAntigravityRegistry(string coreDir)
    {
        try
        {
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            if (uninstallKey != null)
            {
                var normalizedCoreDir = Path.GetFullPath(coreDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var displayName = subKey.GetValue("DisplayName")?.ToString();
                    if (!string.IsNullOrEmpty(displayName) && displayName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
                    {
                        var uninstallStr = subKey.GetValue("UninstallString")?.ToString() ?? string.Empty;
                        var installLoc = subKey.GetValue("InstallLocation")?.ToString() ?? string.Empty;

                        bool isOurCore = (!string.IsNullOrEmpty(uninstallStr) && uninstallStr.Contains(normalizedCoreDir, StringComparison.OrdinalIgnoreCase))
                                      || (!string.IsNullOrEmpty(installLoc) && installLoc.Contains(normalizedCoreDir, StringComparison.OrdinalIgnoreCase));

                        var rawPath = uninstallStr.Split('"').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
                        bool isDeadEntry = !string.IsNullOrEmpty(rawPath) && !File.Exists(rawPath);

                        if (isOurCore || isDeadEntry)
                        {
                            uninstallKey.DeleteSubKeyTree(subKeyName, false);
                        }
                    }
                }
            }
        }
        catch
        {
            // Registry cleanup failures are ignored
        }
    }

    /// <summary>Deletes and recreates the core directory; retries the delete five times with 200 ms waits.</summary>
    public async Task CleanManagedCoreDirAsync()
    {
        var targetDir = GetManagedCoreDir();
        if (Directory.Exists(targetDir))
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Directory.Delete(targetDir, true);
                    break;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }
        }
        Directory.CreateDirectory(targetDir);
    }

    /// <summary>Silently extracts the installer into the core directory, drops app-update.yml, then applies the Mica patch.</summary>
    public async Task InstallToManagedCoreAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        var targetDir = GetManagedCoreDir();
        Directory.CreateDirectory(targetDir);

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = $"/S /D={targetDir}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动安装程序进行静默解包");
        await process.WaitForExitAsync(cancellationToken);

        var exePath = GetManagedExePath();
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException($"安装未能成功完成，未在目标目录找到 {exePath}。");
        }

        // Removing app-update.yml blocks Electron auto-update
        var appUpdateYml = Path.Combine(targetDir, "resources", "app-update.yml");
        if (File.Exists(appUpdateYml))
        {
            try { File.Delete(appUpdateYml); } catch { }
        }
        try
        {
            MicaPatchService.ApplyMicaPatch(exePath, out _);
        }
        catch { }
    }

    /// <summary>Deletes the file with up to three attempts.</summary>
    public static async Task TryDeleteFileAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                File.Delete(filePath);
                break;
            }
            catch
            {
                await Task.Delay(200);
            }
        }
    }

    public async Task CleanTempInstallersAsync()
    {
        try
        {
            var tempDir = GetTempInstallerDir();
            if (Directory.Exists(tempDir))
            {
                foreach (var file in Directory.GetFiles(tempDir, "Antigravity-*.exe"))
                {
                    await TryDeleteFileAsync(file);
                }
            }
        }
        catch { }
    }

    private static bool VerifySha512(string filePath, string expectedBase64)
    {
        if (string.IsNullOrEmpty(expectedBase64) || !File.Exists(filePath))
            return false;

        using var sha512 = SHA512.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha512.ComputeHash(stream);
        var actualBase64 = Convert.ToBase64String(hash);

        return string.Equals(actualBase64, expectedBase64, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExeVersion(string filePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            return info.FileVersion ?? info.ProductVersion ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}
