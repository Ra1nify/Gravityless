using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

/// <summary>
/// Supplies the proxy configuration at request time rather than construction time.
///
/// The setting is editable while the app is running, so services must not cache a client
/// built from a stale config. Keeping this as a delegate avoids wiring every service to the
/// UI and lets tests inject a fixed value.
/// </summary>
public sealed class ProxyConfigProvider
{
    private readonly Func<ProxyConfig> _getter;

    public ProxyConfigProvider(Func<ProxyConfig> getter)
    {
        _getter = getter ?? throw new ArgumentNullException(nameof(getter));
    }

    /// <summary>A provider that always reports the OS-default configuration.</summary>
    public static ProxyConfigProvider SystemDefault { get; } =
        new(() => new ProxyConfig { Mode = ProxyMode.System });

    public ProxyConfig Current => _getter();
}
