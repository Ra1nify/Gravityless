using System.Diagnostics;
using System.Security.Cryptography;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// Downloads an app installer and hands it to the shell.
///
/// The payload is executable, so the hash from the manifest is verified before the file is
/// launched. A mismatch deletes the download rather than asking the user to judge it: a
/// partial or substituted installer has no safe interpretation.
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

        if (!string.IsNullOrWhiteSpace(asset.Sha512))
        {
            var actual = await ComputeSha512Async(path, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, asset.Sha512.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                return (null, $"SHA-512 mismatch: expected {asset.Sha512}, got {actual}");
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

    /// <summary>Lowercase hex SHA-512 of a file.</summary>
    public static async Task<string> ComputeSha512Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
