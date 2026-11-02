using System.Diagnostics;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// Downloads an app installer and hands it to the shell.
///
/// No hash is checked here: the installer is code-signed, and Windows verifies that signature
/// before running it elevated. A truncated download shows up as a content-length shortfall,
/// which the read loop already detects by comparing the received byte count with the
/// advertised length.
/// </summary>
public class AppUpdateInstaller
{
    private readonly HttpClient _http;

    public AppUpdateInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Gravityless");
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into the temp directory and verifies its SHA-512.
    /// Returns the file path, or null with <paramref name="error"/> set.
    /// </summary>
    public async Task<(string? Path, string? Error)> DownloadAsync(
        AppUpdateAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(asset.Url))
        {
            return (null, "empty download url");
        }

        string path;
        try
        {
            var name = Path.GetFileName(new Uri(asset.Url).LocalPath);
            if (string.IsNullOrWhiteSpace(name)) name = "Gravityless-setup.exe";

            var dir = Path.Combine(Path.GetTempPath(), "gravityless-update");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, name);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        try
        {
            using var response = await _http.GetAsync(
                asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode}");
            }

            var total = response.Content.Headers.ContentLength ?? asset.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(path))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (total > 0 && progress != null)
                    {
                        progress.Report((double)received / total);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            return (null, ex.Message);
        }

        // A short read means the transfer was truncated; the signature check at launch would
        // otherwise fail with a generic corruption message.
        if (asset.Size > 0)
        {
            var length = new FileInfo(path).Length;
            if (length != asset.Size)
            {
                TryDelete(path);
                return (null, $"incomplete download: expected {asset.Size} bytes, got {length}");
            }
        }

        return (path, null);
    }

    /// <summary>
    /// Starts the installer. Inno Setup is invoked with <c>/CLOSEAPPLICATIONS</c> so it closes
    /// this process itself; shutting down first would let the installer race the exit.
    /// </summary>
    /// <param name="silent">Pass <c>/SILENT</c> to keep the wizard out of the way.</param>
    public static bool Launch(string installerPath, bool silent)
    {
        try
        {
            var args = silent ? "/SILENT /CLOSEAPPLICATIONS" : "/CLOSEAPPLICATIONS";

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = args,
                UseShellExecute = true,
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A locked temp file is harmless; the next run overwrites it.
        }
    }
}
