using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// Fetches and validates the app update manifest.
///
/// The manifest lives on a static branch, so the client only needs one URL. Failures are
/// classified rather than collapsed into "network error": a 404 or a malformed document
/// means the publisher is at fault, and telling the user to check their connection would
/// send them looking in the wrong place.
/// </summary>
public class AppUpdateService
{
    /// <summary>Default manifest location. Overridable so a mirror can be tried first.</summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/Ra1nify/Gravityless/info/update.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public AppUpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Gravityless");
    }

    /// <summary>Builds the URL list to try in order: caller-provided mirrors, then the default.</summary>
    public static List<string> BuildEndpoints(params string?[] mirrors)
    {
        var list = new List<string>();

        foreach (var mirror in mirrors)
        {
            if (string.IsNullOrWhiteSpace(mirror)) continue;
            var url = mirror.Trim().TrimEnd('/') + "/" + DefaultManifestUrl;
            if (!list.Contains(url)) list.Add(url);
        }

        if (!list.Contains(DefaultManifestUrl)) list.Add(DefaultManifestUrl);
        return list;
    }

    /// <summary>
    /// Tries each endpoint until one answers. A mirror that fails does not stop the chain;
    /// the last failure is returned.
    /// </summary>
    public async Task<AppUpdateResult> CheckAsync(
        IEnumerable<string> endpoints,
        CancellationToken cancellationToken = default)
    {
        AppUpdateResult? last = null;

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await FetchAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess) return result;

            last = result;
        }

        return last ?? new AppUpdateResult
        {
            Failure = AppUpdateFailure.NoUrl,
            Error = "no endpoint configured",
        };
    }

    /// <summary>Fetches and parses one manifest URL.</summary>
    public async Task<AppUpdateResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail(AppUpdateFailure.NoUrl, "empty url");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // A refused or reset connection is local; a proxy denial looks the same, so it is
            // reported separately.
            var blocked = ex.InnerException is System.Net.Sockets.SocketException se
                && se.SocketErrorCode is System.Net.Sockets.SocketError.AccessDenied
                    or System.Net.Sockets.SocketError.NetworkUnreachable;

            return Fail(blocked ? AppUpdateFailure.Blocked : AppUpdateFailure.Network, ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(AppUpdateFailure.Network, ex.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Fail(AppUpdateFailure.NotFound, "manifest not found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(AppUpdateFailure.Http, $"HTTP {(int)response.StatusCode}");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail(AppUpdateFailure.Network, ex.Message);
            }

            return Parse(body);
        }
    }

    /// <summary>Parses a manifest body. Exposed for tests and for a cached payload.</summary>
    public static AppUpdateResult Parse(string json)
    {
        AppUpdateInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<AppUpdateInfo>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // HTML returned by a captive portal or an error page lands here.
            return Fail(AppUpdateFailure.Malformed, ex.Message);
        }

        if (info == null)
        {
            return Fail(AppUpdateFailure.Malformed, "empty document");
        }

        if (info.VersionCode <= 0)
        {
            // A zero code would otherwise read as "already up to date" for every local build.
            return Fail(AppUpdateFailure.Invalid, $"version_code={info.VersionCode}");
        }

        info.Assets ??= [];

        return new AppUpdateResult { Info = info };
    }

    /// <summary>Picks the installer matching the running process architecture.</summary>
    public static AppUpdateAsset? SelectAsset(AppUpdateInfo info, string? arch = null)
    {
        var wanted = arch ?? CurrentArch();

        return info.Assets.FirstOrDefault(a =>
            string.Equals(a.Arch, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>x64</c> or <c>arm64</c>, matching the manifest's asset names.</summary>
    public static string CurrentArch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    /// <summary>Fetches a changelog document, following the link in the manifest.</summary>
    public async Task<AppChangelog?> FetchChangelogAsync(
        AppUpdateInfo info,
        CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(info.LatestChangelog) ? info.ChangelogUrl : info.LatestChangelog;
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var body = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppChangelog>(body, JsonOptions);
        }
        catch
        {
            // A missing changelog must not block the update itself.
            return null;
        }
    }

    private static AppUpdateResult Fail(AppUpdateFailure failure, string error) => new()
    {
        Failure = failure,
        Error = error,
    };
}
