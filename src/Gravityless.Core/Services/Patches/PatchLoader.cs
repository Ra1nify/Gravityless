using System.Globalization;
using System.Text;
using System.Text.Json;
using Gravityless.Core.Services.Patches.Plugins;

namespace Gravityless.Core.Services.Patches;

/// <summary>
/// Orchestrates Antigravity runtime bootstrap injection by composing modular <see cref="IPatchPlugin"/> units
/// and generating the lightweight <c>gravityless-bootstrap.js</c> loader alongside <c>dist/gravityless-plugins/*.js</c>.
/// </summary>
public sealed class PatchLoader
{
    public const string BootstrapEntryMarker = "/* GRAVITYLESS_BOOTSTRAP_ENTRY */";
    public const string BootstrapFileName = "gravityless-bootstrap.js";
    public const string PluginsDirName = "gravityless-plugins";
    public const int PatchVersion = 65;
    public static readonly string PatchStamp = $"/* GRAVITYLESS_BOOTSTRAP_VERSION: {PatchVersion} */";

    private readonly IReadOnlyList<IPatchPlugin> _plugins;

    /// <summary>Default shared loader configured with all built-in Gravityless patch plugins.</summary>
    public static PatchLoader Default { get; } = new(new IPatchPlugin[]
    {
        new ProcessSandboxPlugin(),
        new DesktopLocalizationPlugin(),
        new WindowMaterialPlugin(),
        new RendererLocalizationPlugin(),
        new ContextIndicatorPlugin()
    });

    public PatchLoader(IEnumerable<IPatchPlugin> plugins)
    {
        _plugins = (plugins ?? throw new ArgumentNullException(nameof(plugins)))
            .OrderBy(p => p.Order)
            .ToArray();
    }

    public IReadOnlyList<IPatchPlugin> Plugins => _plugins;

    public static string NormalizeMaterialName(string? material) =>
        material?.Trim().ToLowerInvariant() switch
        {
            "tabbed" => "tabbed",
            "acrylic" => "acrylic",
            "none" => "none",
            _ => "mica"
        };

    /// <summary>
    /// Ensures resources/app exists, unpacking resources/app.asar (or app.asar.stock) when missing.
    /// </summary>
    public static bool EnsureAppUnpacked(string antigravityExePath, out string appDir, out string message)
    {
        message = string.Empty;
        appDir = string.Empty;

        var exeDir = Path.GetDirectoryName(antigravityExePath);
        if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir))
        {
            message = $"无效的 Antigravity 路径: {antigravityExePath}";
            return false;
        }

        var resourcesDir = Path.Combine(exeDir, "resources");
        appDir = Path.Combine(resourcesDir, "app");
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var appAsarStock = Path.Combine(resourcesDir, "app.asar.stock");

        if (Directory.Exists(appDir) && File.Exists(Path.Combine(appDir, "package.json")))
        {
            return true;
        }

        var sourceAsar = File.Exists(appAsar) ? appAsar : (File.Exists(appAsarStock) ? appAsarStock : null);
        if (string.IsNullOrEmpty(sourceAsar))
        {
            message = "未找到核心 app.asar 或 app.asar.stock 文件，无法自动解包。";
            return false;
        }

        if (!AsarExtractor.ExtractAsar(sourceAsar, appDir, out var extractErr))
        {
            message = $"自动解包核心源码失败: {extractErr}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies runtime bootstrap and modular plugin scripts to <paramref name="antigravityExePath"/>.
    /// </summary>
    public bool ApplyPatch(string antigravityExePath, string material, double opacity, out string message)
    {
        message = string.Empty;
        material = NormalizeMaterialName(material);
        var safeOpacity = Math.Clamp(opacity, 0.00, 1.0).ToString("F2", CultureInfo.InvariantCulture);

        if (!EnsureAppUnpacked(antigravityExePath, out var appDir, out var unpackMsg))
        {
            message = unpackMsg;
            return false;
        }

        var resourcesDir = Path.Combine(Path.GetDirectoryName(antigravityExePath)!, "resources");
        var distDir = Path.Combine(appDir, "dist");
        var pluginsDir = Path.Combine(distDir, PluginsDirName);
        var mainJs = Path.Combine(distDir, "main.js");
        var mainStock = Path.Combine(distDir, "main.js.stock");
        var bootstrapJs = Path.Combine(distDir, BootstrapFileName);
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var appAsarStock = Path.Combine(resourcesDir, "app.asar.stock");

        if (!File.Exists(mainJs))
        {
            message = "未找到 dist/main.js 核心入口文件。";
            return false;
        }

        try
        {
            // 1. Clean up legacy static patches so files remain pristine
            CleanupLegacyStaticPatches(distDir);

            // 2. Write modular plugin scripts under dist/gravityless-plugins/
            var context = new PatchContext(material, safeOpacity, PatchVersion, PatchStamp);
            var utf8NoBom = new UTF8Encoding(false);
            Directory.CreateDirectory(pluginsDir);

            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in _plugins)
            {
                expectedFiles.Add(plugin.FileName);
                var pluginPath = Path.Combine(pluginsDir, plugin.FileName);
                File.WriteAllText(pluginPath, plugin.BuildScript(context), utf8NoBom);
            }

            // Remove stale plugin files that are no longer registered
            foreach (var existingFile in Directory.GetFiles(pluginsDir, "*.js"))
            {
                var name = Path.GetFileName(existingFile);
                if (!expectedFiles.Contains(name))
                {
                    try { File.Delete(existingFile); } catch { }
                }
            }

            // 3. Generate gravityless-bootstrap.js loader
            var bootstrapContent = BuildBootstrapScript(context, _plugins);
            File.WriteAllText(bootstrapJs, bootstrapContent, utf8NoBom);

            // 4. Inject single-line require at line 1 of main.js
            if (!File.Exists(mainStock))
            {
                File.Copy(mainJs, mainStock, true);
            }

            var pristineMain = File.ReadAllText(mainStock, Encoding.UTF8);
            if (!pristineMain.Contains(BootstrapEntryMarker, StringComparison.Ordinal))
            {
                var patchedMain = $"{BootstrapEntryMarker}\ntry {{ require('./{BootstrapFileName}'); }} catch (e) {{ console.error('[Gravityless] bootstrap failed:', e); }}\n" + pristineMain;
                File.WriteAllText(mainJs, patchedMain, utf8NoBom);
            }
            else
            {
                File.WriteAllText(mainJs, pristineMain, utf8NoBom);
            }

            // 5. Ensure app.asar is swapped to app.asar.stock so resources/app is executed
            if (File.Exists(appAsar))
            {
                try
                {
                    if (File.Exists(appAsarStock)) File.Delete(appAsarStock);
                    File.Move(appAsar, appAsarStock);
                }
                catch (IOException) { }
            }

            message = $"成功注入 Runtime Bootstrap 补丁 ({_plugins.Count} 个插件)。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"注入补丁异常: {ex.Message}";
            return false;
        }
    }

    /// <summary>True when main.js hook, bootstrap loader, and all registered plugin scripts match <see cref="PatchStamp"/>.</summary>
    public bool IsPatchCurrent(string antigravityExePath)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(antigravityExePath);
            if (string.IsNullOrEmpty(exeDir)) return false;

            var distDir = Path.Combine(exeDir, "resources", "app", "dist");
            var bootstrapJs = Path.Combine(distDir, BootstrapFileName);
            var pluginsDir = Path.Combine(distDir, PluginsDirName);
            var mainJs = Path.Combine(distDir, "main.js");

            if (!File.Exists(bootstrapJs) || !File.Exists(mainJs)) return false;

            if (!File.ReadAllText(mainJs, Encoding.UTF8).Contains(BootstrapEntryMarker, StringComparison.Ordinal))
            {
                return false;
            }

            if (!File.ReadAllText(bootstrapJs, Encoding.UTF8).Contains(PatchStamp, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var plugin in _plugins)
            {
                var pluginPath = Path.Combine(pluginsDir, plugin.FileName);
                if (!File.Exists(pluginPath)) return false;
                if (!File.ReadAllText(pluginPath, Encoding.UTF8).Contains(PatchStamp, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes bootstrap loader and plugin scripts, restores main.js.stock, and moves app.asar back.</summary>
    public bool RevertPatch(string antigravityExePath, out string message)
    {
        message = string.Empty;
        var exeDir = Path.GetDirectoryName(antigravityExePath);
        if (string.IsNullOrEmpty(exeDir)) return false;

        var resourcesDir = Path.Combine(exeDir, "resources");
        var appDir = Path.Combine(resourcesDir, "app");
        var distDir = Path.Combine(appDir, "dist");
        var bootstrapJs = Path.Combine(distDir, BootstrapFileName);
        var pluginsDir = Path.Combine(distDir, PluginsDirName);
        var mainJs = Path.Combine(distDir, "main.js");
        var mainStock = Path.Combine(distDir, "main.js.stock");
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var appAsarStock = Path.Combine(resourcesDir, "app.asar.stock");

        try
        {
            if (File.Exists(bootstrapJs))
            {
                try { File.Delete(bootstrapJs); } catch { }
            }

            if (Directory.Exists(pluginsDir))
            {
                try { Directory.Delete(pluginsDir, true); } catch { }
            }

            if (File.Exists(mainStock))
            {
                try { File.Copy(mainStock, mainJs, true); } catch { }
            }

            CleanupLegacyStaticPatches(distDir);

            if (File.Exists(appAsarStock) && !File.Exists(appAsar))
            {
                try { File.Move(appAsarStock, appAsar); } catch { }
            }

            message = "已成功移除运行时补丁，恢复原生状态。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"还原失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>Restores any legacy static patches from .stock files so dist files stay pristine.</summary>
    private static void CleanupLegacyStaticPatches(string distDir)
    {
        try
        {
            var filesToRestore = new[] { "utils.js", "ipcHandlers.js", "languageServer.js", "preload.js" };
            foreach (var file in filesToRestore)
            {
                var current = Path.Combine(distDir, file);
                var stock = Path.Combine(distDir, file + ".stock");
                if (File.Exists(stock))
                {
                    try { File.Copy(stock, current, true); } catch { }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Generates <c>gravityless-bootstrap.js</c>, the runtime Patch Loader that builds the shared context
    /// and initializes every registered <see cref="IPatchPlugin"/> module in <c>./gravityless-plugins/</c>.
    /// </summary>
    public static string BuildBootstrapScript(PatchContext context, IReadOnlyList<IPatchPlugin> plugins)
    {
        var pluginFileArrayJson = JsonSerializer.Serialize(plugins.Select(p => p.FileName).ToArray());

        return $$"""
{{context.PatchStamp}}
/**
 * Gravityless Runtime Patch Loader
 * Loads shared config & Electron/Node runtime context, then initializes decoupled patch plugins.
 */
(function() {
    if (global.__gravitylessBootstrapped) return;
    global.__gravitylessBootstrapped = true;

    const electron = require('electron');
    const cp = require('child_process');
    const http = require('http');
    const fs = require('fs');
    const path = require('path');

    function findConfigFile() {
        const candidates = [
            path.resolve(__dirname, '..', '..', '..', '..', 'data', 'theme_config.json'),
            path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Gravityless', 'data', 'theme_config.json'),
            path.join(process.env.LOCALAPPDATA || '', 'Gravityless', 'data', 'theme_config.json')
        ];
        for (let i = 0; i < candidates.length; i++) {
            try {
                if (fs.existsSync(candidates[i])) return candidates[i];
            } catch (_) {}
        }
        return null;
    }

    let loadedConfig = null;
    try {
        const cfgPath = findConfigFile();
        if (cfgPath) loadedConfig = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
    } catch (_) {}

    function clamp(val, min, max) {
        return Math.max(min, Math.min(max, val));
    }

    function isHubUrl(url) {
        return typeof url === 'string' && /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?(\/|$)/i.test(url);
    }

    function isZhLocale() {
        return (global.__gravitylessLocale || process.env.GRAVITYLESS_LOCALE || 'zh-CN').startsWith('zh');
    }

    const initialMaterial = process.env.GRAVITYLESS_MICA_MATERIAL || loadedConfig?.Material || '{{context.Material}}';
    const resolvedMaterial = (initialMaterial === 'tabbed' || initialMaterial === 'mica' || initialMaterial === 'acrylic' || initialMaterial === 'none')
        ? initialMaterial : 'mica';
    let parsedAlpha = Number(process.env.GRAVITYLESS_MICA_OPACITY ?? loadedConfig?.Opacity ?? '{{context.SafeOpacity}}');
    if (isNaN(parsedAlpha)) parsedAlpha = 0.72;

    const initialLight = (function() {
        const mode = String(loadedConfig?.ThemeMode || 'auto').trim().toLowerCase();
        if (mode === 'light') return true;
        if (mode === 'dark') return false;
        try {
            return Boolean(electron.nativeTheme && !electron.nativeTheme.shouldUseDarkColors);
        } catch (_) {
            return false;
        }
    })();

    const theme = {
        material: resolvedMaterial,
        alpha: parsedAlpha,
        light: initialLight,
        popupAlpha: Number(loadedConfig?.PopupOpacity) || 0.60,
        accent: loadedConfig?.AccentColor || '#4285f4',
        accentInteraction: loadedConfig?.AccentInteraction !== false,
        noiseAlpha: Number(loadedConfig?.NoiseAlpha) || 0.55,
        noiseFreq: Number(loadedConfig?.NoiseFrequency) || 0.85,
        noiseTint: Number(loadedConfig?.NoiseTintRatio) || 0.28,
        noiseBase: Number(loadedConfig?.NoiseBaseAlpha) || 0.35,
        daemonPort: process.env.GRAVITYLESS_DAEMON_PORT || loadedConfig?.DaemonPort
    };

    const hooks = {
        ready: [],
        domReady: [],
        didFinishLoad: [],
        themeReload: [],
        localeChanged: []
    };

    const ctx = {
        electron,
        cp,
        http,
        fs,
        path,
        distDir: __dirname,
        loadedConfig,
        theme,
        utils: { clamp, isHubUrl, isZhLocale },
        onReady: function(fn) { if (typeof fn === 'function') hooks.ready.push(fn); },
        onDomReady: function(fn) { if (typeof fn === 'function') hooks.domReady.push(fn); },
        onDidFinishLoad: function(fn) { if (typeof fn === 'function') hooks.didFinishLoad.push(fn); },
        onThemeReload: function(fn) { if (typeof fn === 'function') hooks.themeReload.push(fn); },
        onLocaleChanged: function(fn) { if (typeof fn === 'function') hooks.localeChanged.push(fn); }
    };

    const pluginFiles = {{pluginFileArrayJson}};
    for (let i = 0; i < pluginFiles.length; i++) {
        const file = pluginFiles[i];
        try {
            const pluginPath = path.join(__dirname, '{{PluginsDirName}}', file);
            const mod = require(pluginPath);
            if (mod && typeof mod.init === 'function') {
                mod.init(ctx);
            } else if (typeof mod === 'function') {
                mod(ctx);
            }
        } catch (err) {
            try { console.error('[Gravityless] patch plugin failed:', file, err); } catch (_) {}
        }
    }

    function runHooks(list, arg1, arg2) {
        for (let i = 0; i < list.length; i++) {
            try { list[i](arg1, arg2); } catch (_) {}
        }
    }

    if (electron.app) {
        electron.app.on('web-contents-created', function(_evt, contents) {
            try {
                contents.on('dom-ready', function() {
                    runHooks(hooks.domReady, contents);
                });
                contents.on('did-finish-load', function() {
                    runHooks(hooks.didFinishLoad, contents);
                });
            } catch (_) {}
        });

        if (typeof electron.app.whenReady === 'function') {
            electron.app.whenReady().then(function() {
                runHooks(hooks.ready);
            }).catch(function() {});
        }
    }

    if (!theme.daemonPort) return;

    function getAllWebContents() {
        return (electron.webContents && typeof electron.webContents.getAllWebContents === 'function')
            ? electron.webContents.getAllWebContents()
            : [];
    }

    function syncTheme() {
        http.get('http://127.0.0.1:' + theme.daemonPort + '/api/v1/theme.json?t=' + Date.now(), function(res) {
            let data = '';
            res.on('data', function(c) { data += c; });
            res.on('end', function() {
                let next = null;
                try { next = JSON.parse(data); } catch (_) { return; }
                if (!next) return;
                if (next.opacity !== undefined && next.opacity !== null) theme.alpha = clamp(Number(next.opacity), 0, 1);
                if (next.popupOpacity !== undefined && next.popupOpacity !== null) theme.popupAlpha = clamp(Number(next.popupOpacity), 0, 1);
                theme.light = (next.theme === 'light');
                if (next.material !== undefined && next.material !== null) theme.material = String(next.material).trim().toLowerCase();
                if (next.accent !== undefined && next.accent !== null && next.accent !== '') theme.accent = String(next.accent);
                if (next.accentInteraction !== undefined && next.accentInteraction !== null) theme.accentInteraction = String(next.accentInteraction) !== 'false';
                if (next.noiseAlpha !== undefined && next.noiseAlpha !== null) theme.noiseAlpha = Number(next.noiseAlpha);
                if (next.noiseFrequency !== undefined && next.noiseFrequency !== null) theme.noiseFreq = Number(next.noiseFrequency);
                if (next.noiseTintRatio !== undefined && next.noiseTintRatio !== null) theme.noiseTint = Number(next.noiseTintRatio);
                if (next.noiseBaseAlpha !== undefined && next.noiseBaseAlpha !== null) theme.noiseBase = Number(next.noiseBaseAlpha);

                runHooks(hooks.themeReload, getAllWebContents());
            });
        }).on('error', function() {});
    }

    function connectSSE() {
        const req = http.get('http://127.0.0.1:' + theme.daemonPort + '/api/v1/events', function(res) {
            res.on('data', function(chunk) {
                const str = chunk.toString();
                if (str.indexOf('event: connected') >= 0 || str.indexOf('theme_reload') >= 0) syncTheme();
                if (str.indexOf('i18n_reload') >= 0) {
                    let newLang = '';
                    for (const line of str.split('\n')) {
                        if (line.indexOf('data:') === 0) newLang = line.substring(5).trim();
                    }
                    if (!newLang) return;
                    global.__gravitylessLocale = newLang;
                    process.env.GRAVITYLESS_LOCALE = newLang;
                    runHooks(hooks.localeChanged, newLang, getAllWebContents());
                }
            });
            res.on('end', function() { setTimeout(connectSSE, 3000); });
        });
        req.on('error', function() { setTimeout(connectSSE, 3000); });
    }

    syncTheme();
    connectSSE();
})();
""";
    }
}
