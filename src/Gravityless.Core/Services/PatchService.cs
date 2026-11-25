using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gravityless.Core.Services;

public class PatchService
{
    private const string BootstrapEntryMarker = "/* GRAVITYLESS_BOOTSTRAP_ENTRY */";
    private const string BootstrapFileName = "gravityless-bootstrap.js";

    /// <summary>
    /// Version stamp for the runtime bootstrap script. Bump whenever template semantics change
    /// to trigger re-generation of gravityless-bootstrap.js.
    /// </summary>
    private const int PatchVersion = 60;
    private static readonly string PatchStamp = $"/* GRAVITYLESS_BOOTSTRAP_VERSION: {PatchVersion} */";

    /// <summary>Unpacks an Electron ASAR archive into outputDir using only managed code.</summary>
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
            uint headerSizeMinus4 = br.ReadUInt32();
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
            }
            else
            {
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
                }
                else
                {
                    long size = 0;
                    if (item.TryGetProperty("size", out var sizeProp))
                    {
                        size = sizeProp.GetInt64();
                    }

                    long offset = 0;
                    if (item.TryGetProperty("offset", out var offsetProp))
                    {
                        string offsetStr = offsetProp.GetString() ?? "0";
                        long.TryParse(offsetStr, out offset);
                    }

                    var parent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

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
    }

    /// <summary>
    /// Ensures resources/app exists. Unpacks resources/app.asar (or app.asar.stock) when missing.
    /// </summary>
    private static bool EnsureAppUnpacked(string antigravityExePath, out string appDir, out string message)
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

        if (!ExtractAsar(sourceAsar, appDir, out var extractErr))
        {
            message = $"自动解包核心源码失败: {extractErr}";
            return false;
        }

        return true;
    }

    private static string NormalizeMaterialName(string? material) =>
        material?.Trim().ToLowerInvariant() switch
        {
            "tabbed" => "tabbed",
            "acrylic" => "acrylic",
            "none" => "none",
            _ => "mica"
        };

    /// <summary>
    /// Applies runtime bootstrap injection (思路 B):
    /// 1. Ensures resources/app is unpacked.
    /// 2. Restores any legacy static modifications on utils.js, ipcHandlers.js, languageServer.js.
    /// 3. Generates and writes gravityless-bootstrap.js.
    /// 4. Hooks dist/main.js entry with a single-line require.
    /// 5. Ensures app.asar is moved to app.asar.stock.
    /// </summary>
    public static bool ApplyPatch(string antigravityExePath, string material, double opacity, out string message)
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

            // 2. Generate gravityless-bootstrap.js
            var bootstrapContent = BootstrapScriptTemplate
                .Replace("__PATCH_VERSION_STAMP__", PatchStamp)
                .Replace("__INITIAL_MAT__", material)
                .Replace("__SAFE_OPACITY__", safeOpacity);

            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(bootstrapJs, bootstrapContent, utf8NoBom);

            // 3. Inject single-line require at line 1 of main.js
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

            // 4. Ensure app.asar is swapped to app.asar.stock so resources/app is executed
            if (File.Exists(appAsar))
            {
                try
                {
                    if (File.Exists(appAsarStock)) File.Delete(appAsarStock);
                    File.Move(appAsar, appAsarStock);
                }
                catch (IOException) { }
            }

            message = "成功注入 Runtime Bootstrap 补丁。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"注入补丁异常: {ex.Message}";
            return false;
        }
    }

    public static bool ApplyPatch(string antigravityExePath, out string message) =>
        ApplyPatch(antigravityExePath, "mica", 0.72, out message);

    /// <summary>
    /// Restores any legacy static patches from .stock files to ensure pristine codebase.
    /// </summary>
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

    /// <summary>True when both bootstrap script and main.js hook are present and current.</summary>
    public static bool IsPatchCurrent(string antigravityExePath)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(antigravityExePath);
            if (string.IsNullOrEmpty(exeDir)) return false;

            var distDir = Path.Combine(exeDir, "resources", "app", "dist");
            var bootstrapJs = Path.Combine(distDir, BootstrapFileName);
            var mainJs = Path.Combine(distDir, "main.js");

            if (!File.Exists(bootstrapJs) || !File.Exists(mainJs)) return false;

            if (!File.ReadAllText(mainJs, Encoding.UTF8).Contains(BootstrapEntryMarker, StringComparison.Ordinal))
            {
                return false;
            }

            return File.ReadAllText(bootstrapJs, Encoding.UTF8).Contains(PatchStamp, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores .stock files and moves app.asar back.</summary>
    public static bool RevertPatch(string antigravityExePath, out string message)
    {
        message = string.Empty;
        var exeDir = Path.GetDirectoryName(antigravityExePath);
        if (string.IsNullOrEmpty(exeDir)) return false;

        var resourcesDir = Path.Combine(exeDir, "resources");
        var appDir = Path.Combine(resourcesDir, "app");
        var distDir = Path.Combine(appDir, "dist");
        var bootstrapJs = Path.Combine(distDir, BootstrapFileName);
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

    // Backward compatibility aliases
    public static bool ApplyMicaPatch(string antigravityExePath, string material, double opacity, out string message) =>
        ApplyPatch(antigravityExePath, material, opacity, out message);

    public static bool ApplyMicaPatch(string antigravityExePath, out string message) =>
        ApplyPatch(antigravityExePath, out message);

    public static bool IsMicaPatchCurrent(string antigravityExePath) =>
        IsPatchCurrent(antigravityExePath);

    public static bool RevertMicaPatch(string antigravityExePath, out string message) =>
        RevertPatch(antigravityExePath, out message);

    /// <summary>
    /// Runtime bootstrap script template implementing 思路 B.
    /// Loaded synchronously at line 1 of dist/main.js before any other modules run.
    /// Dynamically wraps electron APIs, child_process.spawn, window creation, i18n, and SSE daemon sync.
    /// </summary>
    private static readonly string BootstrapScriptTemplate = """
__PATCH_VERSION_STAMP__
/**
 * Gravityless Runtime Bootstrap Hook (思路 B)
 * Dynamically intercepts Electron APIs, Language Server spawn, window styling, and localization.
 */
(function() {
    if (global.__gravitylessBootstrapped) return;
    global.__gravitylessBootstrapped = true;

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

    const electron = require('electron');
    const cp = require('child_process');
    const http = require('http');
    const fs = require('fs');
    const path = require('path');

    // Suppress unwanted wizard popups & auto-updater in multi-instance sandbox
    try {
        const ideWizardPath = path.join(__dirname, 'ideInstall', 'wizard.js');
        if (fs.existsSync(ideWizardPath)) {
            const ideWizard = require(ideWizardPath);
            if (ideWizard) {
                ideWizard.maybeShowIdeInstallWizard = async function() { return false; };
                ideWizard.showIdeInstallWizard = async function() { return; };
            }
        }
    } catch (_) {}

    try {
        const updaterPath = path.join(__dirname, 'updater.js');
        if (fs.existsSync(updaterPath)) {
            const updater = require(updaterPath);
            if (updater) {
                updater.initAutoUpdater = function() {};
                updater.checkForUpdates = function() {};
            }
        }
    } catch (_) {}

    // ── 1. Hook child_process.spawn / fork for Language Server arguments ──
    function patchArgs(args) {
        if (!Array.isArray(args)) return args;
        const geminiDir = process.env.GRAVITYLESS_GEMINI_DIR;
        if (!geminiDir) return args;

        const appDataIdx = args.indexOf('--app_data_dir');
        if (appDataIdx !== -1 && !args.includes('--gemini_dir')) {
            args.splice(appDataIdx + 2, 0, '--gemini_dir', geminiDir);
        } else if (!args.includes('--gemini_dir')) {
            args.push('--gemini_dir', geminiDir);
        }
        return args;
    }

    const origSpawn = cp.spawn;
    cp.spawn = function(cmd, args, opts) {
        if (typeof cmd === 'string' && (cmd.includes('language_server') || (Array.isArray(args) && args.includes('--app_data_dir')))) {
            args = patchArgs(args);
        }
        return origSpawn.call(this, cmd, args, opts);
    };

    const origFork = cp.fork;
    cp.fork = function(modulePath, args, opts) {
        if (Array.isArray(args) && args.includes('--app_data_dir')) {
            args = patchArgs(args);
        }
        return origFork.call(this, modulePath, args, opts);
    };

    // ── 2. Hook protocol self-registration ──
    if (electron.app) {
        electron.app.setAsDefaultProtocolClient = function() {
            // Gravityless manages antigravity://; prevent host app from reclaiming
            return false;
        };

        // ── 3. Inject Chromium lang switch ──
        if (process.env.GRAVITYLESS_LOCALE) {
            try {
                electron.app.commandLine.appendSwitch('lang', process.env.GRAVITYLESS_LOCALE);
            } catch (_) {}
        }
    }

    // ── 4. Hook dialog.showMessageBox / showErrorBox for desktop prompt i18n ──
    function isZhLocale() {
        return (global.__gravitylessLocale || process.env.GRAVITYLESS_LOCALE || 'zh-CN').startsWith('zh');
    }

    function getAppDisplayName() {
        return (electron.app && typeof electron.app.getName === 'function') ? electron.app.getName() : 'Antigravity';
    }

    function translateDesktopLabel(label, appName) {
        if (typeof label !== 'string' || !label) return label;
        if (label === 'No agents running') return '无正在运行的 Agent';
        const countMatch = label.match(/^(\d+)\s+agents?\s+running$/i);
        if (countMatch) return countMatch[1] + ' 个 Agent 正在运行';
        if (label === 'Quit') return '退出';
        if (label.startsWith('Open ') || label.startsWith(`Open ${appName}`)) {
            return `打开 ${label.slice(5)}`;
        }
        if (label === 'Connect to WSL') return '连接到 WSL';
        if (label === 'Reopen Locally') return '在本地重新打开';
        if (label === 'New Window') return '新建窗口';
        if (label === 'Docs') return '文档';
        if (label === 'Check for Updates') return '检查更新';
        return label;
    }

    if (electron.dialog) {
        function localizeDialogOptions(opts) {
            if (!opts || typeof opts !== 'object' || !isZhLocale()) return opts;
            if (opts.title === 'Confirm Quit' || (opts.message && String(opts.message).includes('Are you sure you want to quit'))) {
                return Object.assign({}, opts, {
                    type: opts.type || 'question',
                    buttons: ['取消', '退出'],
                    defaultId: 1,
                    cancelId: 0,
                    title: '确认退出',
                    message: '确定要退出吗？',
                    detail: '可能仍有 Agent 或后台任务正在运行。'
                });
            }
            if (opts.title === 'WSL distro not found') {
                const detail = (opts.detail && String(opts.detail).includes('opened on Windows instead'))
                    ? '已改为在 Windows 本地打开 Antigravity。'
                    : opts.detail;
                const msgMatch = typeof opts.message === 'string' && opts.message.match(/^The WSL distro "(.+)" is no longer installed\.$/);
                return Object.assign({}, opts, {
                    title: '未找到 WSL 发行版',
                    message: msgMatch ? `WSL 发行版 "${msgMatch[1]}" 已不再安装。` : opts.message,
                    detail
                });
            }
            return opts;
        }

        const origShowBox = electron.dialog.showMessageBox;
        electron.dialog.showMessageBox = function() {
            if (arguments.length === 1) {
                arguments[0] = localizeDialogOptions(arguments[0]);
            } else if (arguments.length >= 2) {
                arguments[1] = localizeDialogOptions(arguments[1]);
            }
            return origShowBox.apply(this, arguments);
        };

        const origShowBoxSync = electron.dialog.showMessageBoxSync;
        if (typeof origShowBoxSync === 'function') {
            electron.dialog.showMessageBoxSync = function() {
                if (arguments.length === 1) {
                    arguments[0] = localizeDialogOptions(arguments[0]);
                } else if (arguments.length >= 2) {
                    arguments[1] = localizeDialogOptions(arguments[1]);
                }
                return origShowBoxSync.apply(this, arguments);
            };
        }

        const origShowErrorBox = electron.dialog.showErrorBox;
        if (typeof origShowErrorBox === 'function') {
            electron.dialog.showErrorBox = function(title, content) {
                if (isZhLocale() && title === 'WSL setup failed') {
                    title = 'WSL 环境配置失败';
                    if (typeof content === 'string' && content.startsWith('Failed to install the server into WSL distro ')) {
                        content = content.replace('Failed to install the server into WSL distro ', '向 WSL 发行版安装服务端失败 ');
                    }
                }
                return origShowErrorBox.call(this, title, content);
            };
        }
    }

    // ── 5. Hook Menu / MenuItem / Tray for tray and submenu i18n ──
    function localizeTrayItems(items) {
        if (!isZhLocale() || !Array.isArray(items)) return;
        const appName = getAppDisplayName();
        for (const item of items) {
            if (!item || typeof item.label !== 'string') continue;
            const next = translateDesktopLabel(item.label, appName);
            if (next !== item.label) {
                try { item.label = next; } catch (_) {}
            }
        }
    }

    if (electron.Menu && typeof electron.Menu.buildFromTemplate === 'function') {
        const origBuild = electron.Menu.buildFromTemplate;
        electron.Menu.buildFromTemplate = function(template) {
            if (isZhLocale() && Array.isArray(template)) {
                const appName = getAppDisplayName();
                for (const item of template) {
                    if (!item || typeof item.label !== 'string') continue;
                    item.label = translateDesktopLabel(item.label, appName);
                }
            }
            return origBuild.call(this, template);
        };
    }

    if (typeof electron.MenuItem === 'function') {
        const OrigMenuItem = electron.MenuItem;
        function PatchedMenuItem(opts) {
            if (!(this instanceof PatchedMenuItem)) {
                return new PatchedMenuItem(opts);
            }
            if (isZhLocale() && opts && typeof opts === 'object' && typeof opts.label === 'string') {
                opts = Object.assign({}, opts, { label: translateDesktopLabel(opts.label, getAppDisplayName()) });
            }
            return new OrigMenuItem(opts);
        }
        Object.setPrototypeOf(PatchedMenuItem, OrigMenuItem);
        PatchedMenuItem.prototype = OrigMenuItem.prototype;
        electron.MenuItem = PatchedMenuItem;
    }

    if (electron.Tray && electron.Tray.prototype && typeof electron.Tray.prototype.setContextMenu === 'function') {
        const origSetContextMenu = electron.Tray.prototype.setContextMenu;
        electron.Tray.prototype.setContextMenu = function(menu) {
            if (menu && Array.isArray(menu.items)) {
                localizeTrayItems(menu.items);
            }
            return origSetContextMenu.call(this, menu);
        };
    }

    // ── 6. State & Appearance Defaults ──
    const initialMaterial = process.env.GRAVITYLESS_MICA_MATERIAL || loadedConfig?.Material || '__INITIAL_MAT__';
    let currentMaterial = (initialMaterial === 'tabbed' || initialMaterial === 'mica' || initialMaterial === 'acrylic' || initialMaterial === 'none')
        ? initialMaterial : 'mica';
    let currentAlpha = Number(process.env.GRAVITYLESS_MICA_OPACITY ?? loadedConfig?.Opacity ?? '__SAFE_OPACITY__');
    if (isNaN(currentAlpha)) currentAlpha = 0.72;
    let currentLight = (function() {
        const mode = String(loadedConfig?.ThemeMode || 'auto').trim().toLowerCase();
        if (mode === 'light') return true;
        if (mode === 'dark') return false;
        try {
            return Boolean(electron.nativeTheme && !electron.nativeTheme.shouldUseDarkColors);
        } catch (_) {
            return false;
        }
    })();
    let currentPopupAlpha = Number(loadedConfig?.PopupOpacity) || 0.60;
    let currentAccent = loadedConfig?.AccentColor || '#4285f4';
    let currentAccentInteraction = loadedConfig?.AccentInteraction !== false;
    let currentNoiseAlpha = Number(loadedConfig?.NoiseAlpha) || 0.55;
    let currentNoiseFreq = Number(loadedConfig?.NoiseFrequency) || 0.85;
    let currentNoiseTint = Number(loadedConfig?.NoiseTintRatio) || 0.28;
    let currentNoiseBase = Number(loadedConfig?.NoiseBaseAlpha) || 0.35;
    let daemonPort = process.env.GRAVITYLESS_DAEMON_PORT || loadedConfig?.DaemonPort;

    function scheduleWindowMaterialSync(win, delayMs) {
        if (!win || win.isDestroyed()) return;
        const run = () => {
            try {
                if (!win.isDestroyed()) applyMaterialToWindow(win, currentMaterial);
            } catch (_) {}
        };
        if (typeof delayMs === 'number' && delayMs > 0) {
            setTimeout(run, delayMs);
        } else if (typeof setImmediate === 'function') {
            setImmediate(run);
        } else {
            setTimeout(run, 0);
        }
    }

    function applyWindowEffects(win) {
        if (!win || win.isDestroyed()) return;

        // BrowserWindow exposes ready-to-show; BaseWindow does not.
        if (typeof win.webContents === 'object' && win.webContents) {
            win.once('ready-to-show', () => {
                try {
                    if (currentMaterial !== 'none') win.show();
                } catch (_) {}
                scheduleWindowMaterialSync(win, 0);
            });
        }

        if (currentMaterial !== 'none' && typeof win.setBackgroundMaterial === 'function') {
            try { win.setBackgroundMaterial(currentMaterial); } catch (_) {}
        }

        // Re-apply after initial addChildView / maximize / window show on Windows DWM (BaseWindow in Electron 44+).
        scheduleWindowMaterialSync(win, 0);
        scheduleWindowMaterialSync(win, 80);

        const reapply = () => scheduleWindowMaterialSync(win, 20);
        win.once('show', reapply);
        win.on('show', reapply);
        win.on('maximize', reapply);
        win.on('unmaximize', reapply);
        win.on('restore', reapply);
        win.once('focus', reapply);
    }

    // ── 7. Hook BrowserWindow & BaseWindow ──
    if (electron.BrowserWindow) {
        const OrigBW = electron.BrowserWindow;

        // Force titlebar overlay color to transparent
        if (OrigBW.prototype && typeof OrigBW.prototype.setTitleBarOverlay === 'function') {
            const origSetOverlay = OrigBW.prototype.setTitleBarOverlay;
            OrigBW.prototype.setTitleBarOverlay = function(opts) {
                if (currentMaterial !== 'none' && opts && typeof opts === 'object') {
                    opts = Object.assign({}, opts, { color: '#00000000' });
                }
                return origSetOverlay.call(this, opts);
            };
        }

        // Prevent solid background reset
        if (OrigBW.prototype && typeof OrigBW.prototype.setBackgroundColor === 'function') {
            const origSetBg = OrigBW.prototype.setBackgroundColor;
            OrigBW.prototype.setBackgroundColor = function(color) {
                if (currentMaterial !== 'none') {
                    return origSetBg.call(this, '#00000000');
                }
                return origSetBg.call(this, color);
            };
        }

        function PatchedBrowserWindow(options) {
            if (!(this instanceof PatchedBrowserWindow)) {
                return new PatchedBrowserWindow(options);
            }
            options = options || {};
            if (currentMaterial !== 'none') {
                options.backgroundMaterial = currentMaterial;
                options.backgroundColor = '#00000000';
                options.show = false;
                if (options.titleBarOverlay && typeof options.titleBarOverlay === 'object') {
                    options.titleBarOverlay = Object.assign({}, options.titleBarOverlay, { color: '#00000000' });
                }
            }
            const win = new OrigBW(options);
            if (currentMaterial !== 'none') {
                applyWindowEffects(win);
            }
            return win;
        }
        Object.setPrototypeOf(PatchedBrowserWindow, OrigBW);
        PatchedBrowserWindow.prototype = OrigBW.prototype;
        electron.BrowserWindow = PatchedBrowserWindow;
    }

    if (electron.WebContentsView) {
        const OrigWCV = electron.WebContentsView;

        // Prevent solid background reset on any WebContentsView (hubWcv, curtainView, etc.)
        if (OrigWCV.prototype && typeof OrigWCV.prototype.setBackgroundColor === 'function') {
            const origWcvSetBg = OrigWCV.prototype.setBackgroundColor;
            OrigWCV.prototype.setBackgroundColor = function(color) {
                if (currentMaterial !== 'none') {
                    return origWcvSetBg.call(this, '#00000000');
                }
                return origWcvSetBg.call(this, color);
            };
        }

        function sanitizeDataUrl(url) {
            if (typeof url !== 'string' || url.indexOf('data:text/html') !== 0) return url;
            if (url.indexOf('%') >= 0) {
                try {
                    const commaIdx = url.indexOf(',');
                    const prefix = url.substring(0, commaIdx + 1);
                    const body = url.substring(commaIdx + 1);
                    const decoded = decodeURIComponent(body);
                    const replaced = decoded.replace(/background:\s*[^;"'\s]+/gi, 'background: transparent !important');
                    return prefix + encodeURIComponent(replaced);
                } catch (_) {
                    return url.replace(/background:\s*[^;"'\s]+/gi, 'background: transparent !important');
                }
            }
            return url.replace(/background:\s*[^;"'\s]+/gi, 'background: transparent !important');
        }

        function PatchedWebContentsView(options) {
            if (!(this instanceof PatchedWebContentsView)) {
                return new PatchedWebContentsView(options);
            }
            options = options || {};
            if (currentMaterial !== 'none') {
                if (!options.webPreferences) {
                    options.webPreferences = {};
                }
                options.webPreferences.transparent = true;
            }
            const view = new OrigWCV(options);
            if (currentMaterial !== 'none') {
                try {
                    view.setBackgroundColor('#00000000');
                } catch (_) {}
            }
            if (view.webContents && typeof view.webContents.loadURL === 'function') {
                const origLoadURL = view.webContents.loadURL;
                view.webContents.__origLoadURL = origLoadURL;
                view.webContents.loadURL = function(url, opts) {
                    if (currentMaterial !== 'none' && typeof url === 'string' && url.indexOf('data:text/html') === 0) {
                        return origLoadURL.call(this, sanitizeDataUrl(url), opts);
                    }
                    return origLoadURL.call(this, url, opts);
                };
            }
            return view;
        }
        Object.setPrototypeOf(PatchedWebContentsView, OrigWCV);
        PatchedWebContentsView.prototype = OrigWCV.prototype;
        electron.WebContentsView = PatchedWebContentsView;
    }

    if (electron.BaseWindow) {
        const OrigBase = electron.BaseWindow;

        // Force titlebar overlay color to transparent
        if (OrigBase.prototype && typeof OrigBase.prototype.setTitleBarOverlay === 'function') {
            const origSetOverlay = OrigBase.prototype.setTitleBarOverlay;
            OrigBase.prototype.setTitleBarOverlay = function(opts) {
                if (currentMaterial !== 'none' && opts && typeof opts === 'object') {
                    opts = Object.assign({}, opts, { color: '#00000000' });
                }
                return origSetOverlay.call(this, opts);
            };
        }

        // Prevent solid background reset
        if (OrigBase.prototype && typeof OrigBase.prototype.setBackgroundColor === 'function') {
            const origSetBg = OrigBase.prototype.setBackgroundColor;
            OrigBase.prototype.setBackgroundColor = function(color) {
                if (currentMaterial !== 'none') {
                    return origSetBg.call(this, '#00000000');
                }
                return origSetBg.call(this, color);
            };
        }

        function PatchedBaseWindow(options) {
            if (!(this instanceof PatchedBaseWindow)) {
                return new PatchedBaseWindow(options);
            }
            options = options || {};
            if (currentMaterial !== 'none') {
                options.backgroundMaterial = currentMaterial;
                options.backgroundColor = '#00000000';
                if (options.titleBarOverlay && typeof options.titleBarOverlay === 'object') {
                    options.titleBarOverlay = Object.assign({}, options.titleBarOverlay, { color: '#00000000' });
                }
            }
            const win = new OrigBase(options);
            if (currentMaterial !== 'none') {
                applyWindowEffects(win);
            }

            // Antigravity WCV architecture:
            // - 2-layer mode: children[0] is opaque curtainView (WebContentsView, has .webContents) -> suppress to unblock Mica.
            // - 3-layer mode: children[0] is clipView (pure View, no .webContents) -> keep visible/bounds intact for browser tabs, only clear bg.
            if (win.contentView && typeof win.contentView.addChildView === 'function') {
                const origAddChild = win.contentView.addChildView;
                win.contentView.addChildView = function(view, index) {
                    try {
                        const kids = win.contentView.children || [];
                        if (kids.length === 0 && view) {
                            const isCurtainUnderlay = Boolean(view.webContents);
                            view.__gravitylessIsCurtain = isCurtainUnderlay;
                            if (typeof view.setBackgroundColor === 'function' && !view.__origSetBg) {
                                const origViewBg = view.setBackgroundColor;
                                view.__origSetBg = origViewBg;
                                view.setBackgroundColor = function(color) {
                                    if (currentMaterial !== 'none') return origViewBg.call(this, '#00000000');
                                    return origViewBg.call(this, color);
                                };
                                if (currentMaterial !== 'none') {
                                    try { origViewBg.call(view, '#00000000'); } catch (_) {}
                                }
                            }
                            if (isCurtainUnderlay) {
                                if (typeof view.setVisible === 'function' && !view.__origSetVisible) {
                                    const origSetVisible = view.setVisible;
                                    view.__origSetVisible = origSetVisible;
                                    view.setVisible = function(visible) {
                                        if (currentMaterial !== 'none') return origSetVisible.call(this, false);
                                        return origSetVisible.call(this, visible);
                                    };
                                    if (currentMaterial !== 'none') {
                                        try { origSetVisible.call(view, false); } catch (_) {}
                                    }
                                }
                                if (typeof view.setBounds === 'function' && !view.__origSetBounds) {
                                    const origSetBounds = view.setBounds;
                                    view.__origSetBounds = origSetBounds;
                                    view.setBounds = function(bounds) {
                                        if (currentMaterial !== 'none') {
                                            return origSetBounds.call(this, { x: 0, y: 0, width: 0, height: 0 });
                                        }
                                        return origSetBounds.call(this, bounds);
                                    };
                                    if (currentMaterial !== 'none') {
                                        try { origSetBounds.call(view, { x: 0, y: 0, width: 0, height: 0 }); } catch (_) {}
                                    }
                                }
                            }
                        } else if (view) {
                            if (currentMaterial !== 'none' && typeof view.setBackgroundColor === 'function') {
                                try { view.setBackgroundColor('#00000000'); } catch (_) {}
                            }
                        }
                    } catch (_) {}
                    const result = origAddChild.call(this, view, index);
                    scheduleWindowMaterialSync(win, 0);
                    return result;
                };
            }

            return win;
        }
        Object.setPrototypeOf(PatchedBaseWindow, OrigBase);
        PatchedBaseWindow.prototype = OrigBase.prototype;
        electron.BaseWindow = PatchedBaseWindow;
    }

    // Intercept wcv:set-theme-color IPC to avoid resetting solid opaque background during material mode
    if (electron.ipcMain && typeof electron.ipcMain.handle === 'function') {
        const origHandle = electron.ipcMain.handle;
        electron.ipcMain.handle = function(channel, listener) {
            if (channel === 'wcv:set-theme-color') {
                const origListener = listener;
                return origHandle.call(this, channel, async function(event, color) {
                    if (currentMaterial !== 'none') {
                        return true;
                    }
                    return origListener.call(this, event, color);
                });
            }
            return origHandle.call(this, channel, listener);
        };
    }

    // ── 8. CSS & i18n Helpers ──
    function clamp(val, min, max) {
        return Math.max(min, Math.min(max, val));
    }

    function buildCss(alpha, popupAlpha, shellAlpha, isLight,
                      accentHex, accentInteraction,
                      noiseAlpha, noiseFreq, noiseTint, noiseBase) {
        const a = clamp(Number(alpha) || 0, 0, 1);
        const pa = clamp(Number(popupAlpha) || 0, 0, 1);
        const surface = isLight ? '243, 243, 243' : '20, 20, 20';
        const popupTint = isLight ? '250, 250, 250' : '24, 24, 24';
        const dialogTint = isLight ? '250, 250, 250' : '24, 24, 24';
        const composerRing = isLight ? '243, 243, 243' : '20, 20, 20';
        const composerFill = isLight ? '250, 250, 250' : '24, 24, 24';
        const ringA = (isLight ? 0.55 : 0.30).toFixed(2);
        const fillA = (isLight ? 0.45 : 0.22).toFixed(2);

        const STOCK_BLUE = '#4285f4';
        const useSystemAccent = !(accentInteraction === false || accentInteraction === 'false');
        const accentSrc = useSystemAccent ? (accentHex || STOCK_BLUE) : STOCK_BLUE;
        const accentRaw = String(accentSrc).trim().replace('#', '');
        let ar = 66, ag = 133, ab = 244;
        if (accentRaw.length === 6 || accentRaw.length === 8) {
            const hex = accentRaw.length === 8 ? accentRaw.slice(2) : accentRaw;
            const r = parseInt(hex.slice(0, 2), 16);
            const g = parseInt(hex.slice(2, 4), 16);
            const b = parseInt(hex.slice(4, 6), 16);
            if (!isNaN(r) && !isNaN(g) && !isNaN(b)) { ar = r; ag = g; ab = b; }
        }
        const lum = 0.299 * ar + 0.587 * ag + 0.114 * ab;
        const desat = (v) => Math.round(lum + (v - lum) * 0.45);
        const cr = desat(ar), cg = desat(ag), cb = desat(ab);
        const rowHover = 'rgba(' + cr + ', ' + cg + ', ' + cb + ', 0.08)';
        const rowActive = 'rgba(' + cr + ', ' + cg + ', ' + cb + ', 0.14)';
        const rowActiveStroke = 'rgba(' + cr + ', ' + cg + ', ' + cb + ', 0.22)';

        const nA = Math.max(0, Math.min(1, Number(noiseAlpha) || 0));
        const nF = Math.max(0.5, Math.min(2.0, Number(noiseFreq) || 0.85));
        const nT = Math.max(0, Math.min(1, Number(noiseTint) || 0));
        const nB = Math.max(0, Math.min(1, Number(noiseBase) || 0));

        const mixWith = isLight ? 255 : 0;
        const mixCh = (c) => Math.round(mixWith + (c - mixWith) * nT);
        const tintR = mixCh(cr) / 255, tintG = mixCh(cg) / 255, tintB = mixCh(cb) / 255;

        const coarseFreq = (nF * 0.82).toFixed(3);
        const fineFreq = (nF * 1.55).toFixed(3);
        const coarseA = (nA * 0.62).toFixed(3);
        const fineA = (nA * 0.28).toFixed(3);
        const noiseSvg = (freq, noiseOp) =>
            'url("data:image/svg+xml;utf8,<svg xmlns=\'http://www.w3.org/2000/svg\' width=\'140\' height=\'140\'>'
            + '<filter id=\'n\'><feTurbulence type=\'fractalNoise\' baseFrequency=\'' + freq + '\' numOctaves=\'3\' stitchTiles=\'stitch\'/>'
            + '<feColorMatrix type=\'matrix\' values=\'0 0 0 0 ' + tintR.toFixed(3) + '  0 0 0 0 ' + tintG.toFixed(3)
            + '  0 0 0 0 ' + tintB.toFixed(3) + '  0 0 0 ' + noiseOp + ' 0\'/></filter>'
            + '<rect width=\'140\' height=\'140\' filter=\'url(%23n)\'/></svg>")';

        const blurCss = nA <= 0 && nB <= 0 ? '' : `
[role="menu"],
[role="dialog"],
[role="alertdialog"],
[role="listbox"]:not([aria-label="Typeahead menu"]),
[aria-modal="true"],
[data-select-popup],
[data-testid="bottom-sheet"],
.settings-modal-container,
[cmdk-root],
.command-palette {
    background-color: rgba(${mixCh(cr)}, ${mixCh(cg)}, ${mixCh(cb)}, ${nB.toFixed(3)}) !important;
    background-image: ${noiseSvg(coarseFreq, coarseA)}, ${noiseSvg(fineFreq, fineA)} !important;
    background-blend-mode: normal, overlay;
    backdrop-filter: blur(28px) saturate(1.8) !important;
    -webkit-backdrop-filter: blur(28px) saturate(1.8) !important;
}

[data-floating-ui-portal] > div,
[data-radix-popper-content-wrapper] > div,
[data-radix-dropdown-menu-content],
[data-radix-menu-content],
[data-radix-context-menu-content],
.surface-glass,
body[data-agy-theme] .surface-glass,
body[data-agy-theme] .agy-fastpick.surface-glass,
body[data-agy-theme] .quick-input-widget.surface-glass {
    background-color: rgba(${mixCh(cr)}, ${mixCh(cg)}, ${mixCh(cb)}, ${nB.toFixed(3)}) !important;
    background-image: ${noiseSvg(coarseFreq, coarseA)}, ${noiseSvg(fineFreq, fineA)} !important;
    background-blend-mode: normal, overlay;
    backdrop-filter: blur(28px) saturate(1.8) !important;
    -webkit-backdrop-filter: blur(28px) saturate(1.8) !important;
}
`;
        const strokeStatic = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.07)';
        const strokeFlyout = isLight ? 'rgba(0, 0, 0, 0.12)' : 'rgba(255, 255, 255, 0.10)';
        const shadowFlyout = isLight
            ? '0 8px 16px -4px rgba(0, 0, 0, 0.14), 0 0 2px 0 rgba(0, 0, 0, 0.12)'
            : '0 8px 16px -4px rgba(0, 0, 0, 0.36), 0 0 2px 0 rgba(0, 0, 0, 0.28)';
        const shadowTooltip = isLight
            ? '0 4px 8px -2px rgba(0, 0, 0, 0.10), 0 0 2px 0 rgba(0, 0, 0, 0.08)'
            : '0 4px 8px -2px rgba(0, 0, 0, 0.30), 0 0 2px 0 rgba(0, 0, 0, 0.20)';
        const shadowDialog = isLight
            ? '0 32px 64px -12px rgba(0, 0, 0, 0.24), 0 0 2px 0 rgba(0, 0, 0, 0.14)'
            : '0 32px 64px -12px rgba(0, 0, 0, 0.56), 0 0 2px 0 rgba(0, 0, 0, 0.36)';

        const cardRgb = isLight ? '255, 255, 255' : '35, 35, 42';
        const cardAlpha = isLight ? (0.28 + a * 0.45).toFixed(2) : (0.12 + a * 0.35).toFixed(2);

        const composerCss = `
.surface-composer,
body[data-agy-theme] .surface-composer,
body[data-agy-theme].dark .surface-composer {
    background-color: rgba(${composerRing}, ${ringA}) !important;
}

.surface-composer > .surface-composer-card,
body[data-agy-theme] .surface-composer > .surface-composer-card,
body[data-agy-theme].dark .surface-composer > .surface-composer-card {
    background-color: rgba(${composerFill}, ${fillA}) !important;
}
`;

        return `
/* Gravityless: Mica baseline (Antigravity 2.35+) */
html,
body,
#root,
.agy-shell,
body[data-agy-theme] .agy-shell,
.agy-inset-shell,
.dark .agy-inset-shell,
body[data-agy-theme] .agy-inset-shell,
.agy-sheet[data-sheet-inset],
body[data-agy-theme] .agy-sheet[data-sheet-inset],
body[data-agy-home-glow] .agy-sheet:has(.agy-home) {
    background: transparent !important;
    background-color: transparent !important;
    background-image: none !important;
}

.agy-sheet:not([data-sheet-inset]),
body[data-agy-theme] .agy-sheet:not([data-sheet-inset]) {
    background-color: var(--background) !important;
    background-image: none !important;
}

:root,
body[data-agy-theme] {
    /* Shell / Canvas transparency: driven by the Mica opacity slider (alpha) */
    --background: rgba(${surface}, ${a.toFixed(2)}) !important;
    --color-background: rgba(${surface}, ${a.toFixed(2)}) !important;
    --body-background: transparent !important;
    --shell: transparent !important;
    --shell-canvas: transparent !important;
    --home-glow: transparent !important;
    --scrim: transparent !important;

    /* Sidebar transparency: slightly higher opacity for subtle contrast */
    --sidebar: rgba(${surface}, ${Math.min(1, a + 0.08).toFixed(2)}) !important;
    --color-sidebar: rgba(${surface}, ${Math.min(1, a + 0.08).toFixed(2)}) !important;
    --sidebar-secondary: ${isLight ? 'rgba(0, 0, 0, 0.04)' : 'rgba(255, 255, 255, 0.06)'} !important;
    --color-sidebar-secondary: ${isLight ? 'rgba(0, 0, 0, 0.04)' : 'rgba(255, 255, 255, 0.06)'} !important;
    --sidebar-muted: transparent !important;
    --color-sidebar-muted: transparent !important;
    --gl-popup-bg: transparent;
    --gl-dialog-bg: transparent;
    --gl-row-hover: ${rowHover};
    --gl-row-active: ${rowActive};
    --gl-row-active-stroke: ${rowActiveStroke};
    --gl-surface-mask: rgba(${cardRgb}, ${cardAlpha});
    --gl-surface-mask-hover: rgba(${cardRgb}, ${Math.min(1, Number(cardAlpha) + 0.10).toFixed(2)});
    --card: rgba(${cardRgb}, ${cardAlpha}) !important;
    --color-card: rgba(${cardRgb}, ${cardAlpha}) !important;
    --card-border: ${strokeStatic} !important;
    --color-card-border: ${strokeStatic} !important;
    --glass: rgba(${cardRgb}, ${cardAlpha}) !important;
    --glass-border: ${strokeStatic} !important;
    --glass-highlight: transparent !important;
    --onyx-frame: rgba(${composerRing}, ${ringA}) !important;
    --onyx-well: ${isLight ? 'rgba(0, 0, 0, 0.03)' : 'rgba(255, 255, 255, 0.04)'} !important;
    --onyx-raised: rgba(${composerFill}, ${fillA}) !important;
    --onyx-selected: ${rowActive} !important;
    --onyx-key: ${isLight ? 'rgba(0, 0, 0, 0.05)' : 'rgba(255, 255, 255, 0.06)'} !important;
    --gl-stroke-static: ${strokeStatic};
    --gl-stroke-flyout: ${strokeFlyout};
    --gl-shadow-flyout: ${shadowFlyout};
    --gl-shadow-tooltip: ${shadowTooltip};
    --gl-shadow-dialog: ${shadowDialog};
}

${composerCss}
${blurCss}

#root *,
#root *::before,
#root *::after {
    box-shadow: none !important;
}

#root .agy-card,
#root .bg-card,
#root .bg-card-border,
#root [data-testid="user-message-bubble"] {
    background-color: var(--gl-surface-mask) !important;
    box-shadow: inset 0 0 0 1px var(--gl-stroke-static) !important;
}

#root .surface-composer,
#root .surface-composer-card,
body[data-agy-theme] .surface-composer > .surface-composer-card {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-static) !important;
}

#root .agy-sidebar-row {
    --row-bg: transparent !important;
    background-color: transparent !important;
}
#root .agy-sidebar-row:hover {
    --row-bg: var(--gl-row-hover) !important;
    background-color: var(--gl-row-hover) !important;
}
#root .agy-sidebar-row[data-selected="true"],
body[data-agy-theme] .agy-sidebar-row[data-selected="true"],
body[data-agy-theme].dark .agy-sidebar-row[data-selected="true"],
#root .agy-sidebar-raised,
body[data-agy-theme] .agy-sidebar-raised,
body[data-agy-theme].dark .agy-sidebar-raised {
    --row-bg: var(--gl-row-active) !important;
    background-color: var(--gl-row-active) !important;
    box-shadow: inset 0 0 0 1px var(--gl-row-active-stroke) !important;
}

[role="menu"],
[role="alertdialog"],
[role="listbox"],
[data-select-popup],
[data-hover-card-popup],
[data-slot$="-content"],
.surface-glass,
.agy-fastpick.surface-glass,
.absolute.bg-sidebar,
.absolute.bg-background,
.absolute.bg-card {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-flyout), var(--gl-shadow-flyout) !important;
}

[role="tooltip"],
.react-tooltip {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-flyout), var(--gl-shadow-tooltip) !important;
}

[role="dialog"],
[aria-modal="true"],
[data-testid="bottom-sheet"],
.settings-modal-container,
[cmdk-root],
.command-palette {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-flyout), var(--gl-shadow-dialog) !important;
}
`;
    }

    function attach(target, rawAlpha, rawPopupAlpha) {
        if (!target) return;
        const wc = target.webContents ? target.webContents : target;
        if (!wc || typeof wc.insertCSS !== 'function') return;
        if (typeof target.isDestroyed === 'function' && target.isDestroyed()) return;

        try {
            const prev = wc.__gravitylessCssKey;
            if (prev) Promise.resolve(wc.removeInsertedCSS(prev)).catch(function () {});
            wc.__gravitylessCssKey = null;
        } catch (_) {}

        if (currentMaterial === 'none') {
            return;
        }

        const alpha = (rawAlpha === undefined || rawAlpha === null) ? currentAlpha : rawAlpha;
        const popupAlpha = (rawPopupAlpha === undefined || rawPopupAlpha === null) ? currentPopupAlpha : rawPopupAlpha;
        const css = buildCss(alpha, popupAlpha, currentAlpha, currentLight,
                             currentAccent, currentAccentInteraction,
                             currentNoiseAlpha, currentNoiseFreq, currentNoiseTint, currentNoiseBase);

        try {
            const p = wc.insertCSS(css, { cssOrigin: 'user' });
            if (p && typeof p.then === 'function') {
                p.then(function (key) { wc.__gravitylessCssKey = key; }).catch(function () {});
            } else {
                wc.__gravitylessCssKey = p;
            }
        } catch (_) {}
    }

    function isHubUrl(url) {
        return typeof url === 'string' && /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?(\/|$)/i.test(url);
    }

    function buildWidgetCss(isLight, accentHex, accentInteraction, noiseAlpha, noiseFreq, noiseTint, noiseBase) {
        const STOCK_BLUE = '#4285f4';
        const useSystemAccent = !(accentInteraction === false || accentInteraction === 'false');
        const accentSrc = useSystemAccent ? (accentHex || STOCK_BLUE) : STOCK_BLUE;
        const accentRaw = String(accentSrc).trim().replace('#', '');
        let ar = 66, ag = 133, ab = 244;
        if (accentRaw.length === 6 || accentRaw.length === 8) {
            const hex = accentRaw.length === 8 ? accentRaw.slice(2) : accentRaw;
            const r = parseInt(hex.slice(0, 2), 16);
            const g = parseInt(hex.slice(2, 4), 16);
            const b = parseInt(hex.slice(4, 6), 16);
            if (!isNaN(r) && !isNaN(g) && !isNaN(b)) { ar = r; ag = g; ab = b; }
        }
        const accentRgb = ar + ', ' + ag + ', ' + ab;
        const lum = 0.299 * ar + 0.587 * ag + 0.114 * ab;
        const desat = (v) => Math.round(lum + (v - lum) * 0.45);
        const cr = desat(ar), cg = desat(ag), cb = desat(ab);

        const nA = Math.max(0, Math.min(1, Number(noiseAlpha) || 0));
        const nF = Math.max(0.5, Math.min(2.0, Number(noiseFreq) || 0.85));
        const nT = Math.max(0, Math.min(1, Number(noiseTint) || 0));
        const nB = Math.max(0, Math.min(1, Number(noiseBase) || 0));
        const mixWith = isLight ? 255 : 0;
        const mixCh = (c) => Math.round(mixWith + (c - mixWith) * nT);
        const tintR = mixCh(cr) / 255, tintG = mixCh(cg) / 255, tintB = mixCh(cb) / 255;

        const coarseFreq = (nF * 0.82).toFixed(3);
        const fineFreq = (nF * 1.55).toFixed(3);
        const coarseA = (nA * 0.62).toFixed(3);
        const fineA = (nA * 0.28).toFixed(3);
        const noiseSvg = (freq, noiseOp) =>
            'url("data:image/svg+xml;utf8,<svg xmlns=\'http://www.w3.org/2000/svg\' width=\'140\' height=\'140\'>'
            + '<filter id=\'n\'><feTurbulence type=\'fractalNoise\' baseFrequency=\'' + freq + '\' numOctaves=\'3\' stitchTiles=\'stitch\'/>'
            + '<feColorMatrix type=\'matrix\' values=\'0 0 0 0 ' + tintR.toFixed(3) + '  0 0 0 0 ' + tintG.toFixed(3)
            + '  0 0 0 0 ' + tintB.toFixed(3) + '  0 0 0 ' + noiseOp + ' 0\'/></filter>'
            + '<rect width=\'140\' height=\'140\' filter=\'url(%23n)\'/></svg>")';

        const useGlassNoise = currentMaterial !== 'none' && (nA > 0 || nB > 0);
        const flyoutBgColor = useGlassNoise
            ? 'rgba(' + mixCh(cr) + ', ' + mixCh(cg) + ', ' + mixCh(cb) + ', ' + Math.max(0.72, nB).toFixed(3) + ')'
            : (isLight ? 'rgba(249, 249, 249, 0.96)' : 'rgba(44, 44, 44, 0.96)');
        const flyoutBgImage = useGlassNoise ? (noiseSvg(coarseFreq, coarseA) + ', ' + noiseSvg(fineFreq, fineA)) : 'none';

        const fgPrimary = isLight ? 'rgba(0, 0, 0, 0.8956)' : 'rgba(255, 255, 255, 0.92)';
        const fgSecondary = isLight ? 'rgba(0, 0, 0, 0.6063)' : 'rgba(255, 255, 255, 0.68)';
        const fgTertiary = isLight ? 'rgba(0, 0, 0, 0.4458)' : 'rgba(255, 255, 255, 0.46)';
        const ctrlHover = isLight ? 'rgba(0, 0, 0, 0.0373)' : 'rgba(255, 255, 255, 0.0605)';
        const ctrlPress = isLight ? 'rgba(0, 0, 0, 0.0241)' : 'rgba(255, 255, 255, 0.0419)';
        const strokeCtrl = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.09)';
        const strokeFlyout = isLight ? 'rgba(0, 0, 0, 0.12)' : 'rgba(255, 255, 255, 0.10)';
        const divider = isLight ? 'rgba(0, 0, 0, 0.0578)' : 'rgba(255, 255, 255, 0.0837)';
        const trackBg = isLight ? 'rgba(0, 0, 0, 0.12)' : 'rgba(255, 255, 255, 0.15)';
        const barTrackBg = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.10)';
        const shadowFlyout = isLight
            ? '0 8px 16px -4px rgba(0, 0, 0, 0.14), 0 0 2px 0 rgba(0, 0, 0, 0.12)'
            : '0 8px 16px -4px rgba(0, 0, 0, 0.36), 0 0 2px 0 rgba(0, 0, 0, 0.28)';

        const accentColor = (isLight && !useSystemAccent) ? '#0B57D0' : ((!isLight && !useSystemAccent) ? '#8AB4F8' : ('rgb(' + accentRgb + ')'));
        const accentSoft = 'rgba(' + accentRgb + ', 0.14)';
        const cautionColor = isLight ? '#E37400' : '#FBBC04';
        const cautionSoft = isLight ? 'rgba(227, 116, 0, 0.14)' : 'rgba(251, 188, 4, 0.16)';
        const criticalColor = isLight ? '#D93025' : '#EA4335';
        const criticalSoft = isLight ? 'rgba(217, 48, 37, 0.14)' : 'rgba(234, 67, 53, 0.18)';

        return `
:root {
    --gl-ctx-fg: ${fgPrimary};
    --gl-ctx-fg-muted: ${fgSecondary};
    --gl-ctx-fg-tertiary: ${fgTertiary};
    --gl-ctx-hover: ${ctrlHover};
    --gl-ctx-press: ${ctrlPress};
    --gl-ctx-stroke-ctrl: ${strokeCtrl};
    --gl-ctx-stroke-flyout: ${strokeFlyout};
    --gl-ctx-divider: ${divider};
    --gl-ctx-track: ${trackBg};
    --gl-ctx-bar-track: ${barTrackBg};
    --gl-ctx-shadow: ${shadowFlyout};
    --gl-ctx-flyout-bg: ${flyoutBgColor};
    --gl-ctx-flyout-img: ${flyoutBgImage};
    --gl-ctx-accent: ${accentColor};
    --gl-ctx-accent-soft: ${accentSoft};
    --gl-ctx-caution: ${cautionColor};
    --gl-ctx-caution-soft: ${cautionSoft};
    --gl-ctx-critical: ${criticalColor};
    --gl-ctx-critical-soft: ${criticalSoft};
    --gl-ctx-state: var(--gl-ctx-accent);
    --gl-ctx-state-soft: var(--gl-ctx-accent-soft);
}

.gl-ctx-host {
    display: inline-flex;
    align-items: center;
    flex-shrink: 0;
}

.gl-ctx-btn {
    position: relative;
    width: 28px;
    height: 28px;
    padding: 0;
    margin: 0;
    border-radius: 9999px;
    border: 1px solid transparent;
    background: transparent;
    color: var(--gl-ctx-fg-muted);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    user-select: none;
    outline: none;
    transition: background-color 120ms cubic-bezier(0.1, 0.9, 0.2, 1), border-color 120ms ease, transform 80ms ease;
    --gl-ctx-state: var(--gl-ctx-accent);
}
.gl-ctx-btn[data-level="caution"] { --gl-ctx-state: var(--gl-ctx-caution); }
.gl-ctx-btn[data-level="critical"] { --gl-ctx-state: var(--gl-ctx-critical); }

.gl-ctx-btn:hover {
    background-color: var(--gl-ctx-hover);
    border-color: var(--gl-ctx-divider);
    color: var(--gl-ctx-fg);
}
.gl-ctx-btn:active {
    background-color: var(--gl-ctx-press);
    transform: scale(0.96);
}
.gl-ctx-btn[aria-expanded="true"] {
    background-color: var(--gl-ctx-hover);
    border-color: var(--gl-ctx-stroke-ctrl);
    color: var(--gl-ctx-fg);
}
.gl-ctx-btn:focus-visible {
    outline: 2px solid var(--gl-ctx-fg);
    outline-offset: 1px;
}

.gl-ctx-svg {
    width: 18px;
    height: 18px;
    display: block;
    overflow: visible;
}
.gl-ctx-ring-track {
    stroke: var(--gl-ctx-track);
    transition: stroke 160ms ease;
}
.gl-ctx-btn:hover .gl-ctx-ring-track,
.gl-ctx-btn[aria-expanded="true"] .gl-ctx-ring-track {
    stroke: var(--gl-ctx-fg-tertiary);
}
.gl-ctx-ring-arc {
    stroke: var(--gl-ctx-state);
    transition: stroke-dashoffset 260ms cubic-bezier(0.1, 0.9, 0.2, 1), stroke 180ms ease, opacity 140ms ease;
}

.gl-ctx-flyout {
    position: fixed;
    z-index: 6500;
    width: 280px;
    max-width: calc(100vw - 24px);
    box-sizing: border-box;
    padding: 13px 15px;
    border-radius: 8px;
    border: 1px solid var(--gl-ctx-stroke-flyout);
    background-color: var(--gl-ctx-flyout-bg);
    background-image: var(--gl-ctx-flyout-img);
    background-blend-mode: normal, overlay;
    backdrop-filter: blur(28px) saturate(1.8);
    -webkit-backdrop-filter: blur(28px) saturate(1.8);
    box-shadow: var(--gl-ctx-shadow);
    color: var(--gl-ctx-fg);
    font-family: 'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, sans-serif;
    font-size: 12px;
    line-height: 1.4;
    pointer-events: auto;
    user-select: text;
    outline: none;
    --gl-ctx-state: var(--gl-ctx-accent);
    --gl-ctx-state-soft: var(--gl-ctx-accent-soft);
    animation: glCtxFlyoutIn 167ms cubic-bezier(0.1, 0.9, 0.2, 1) both;
}
.gl-ctx-flyout[data-level="caution"] {
    --gl-ctx-state: var(--gl-ctx-caution);
    --gl-ctx-state-soft: var(--gl-ctx-caution-soft);
}
.gl-ctx-flyout[data-level="critical"] {
    --gl-ctx-state: var(--gl-ctx-critical);
    --gl-ctx-state-soft: var(--gl-ctx-critical-soft);
}
@keyframes glCtxFlyoutIn {
    from { opacity: 0; transform: translateY(6px); }
    to { opacity: 1; transform: translateY(0); }
}

.gl-ctx-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    margin-bottom: 8px;
}
.gl-ctx-title-wrap {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
}
.gl-ctx-title {
    font-size: 12px;
    font-weight: 600;
    color: var(--gl-ctx-fg);
    letter-spacing: -0.01em;
}
.gl-ctx-subtitle {
    font-size: 11px;
    color: var(--gl-ctx-fg-muted);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.gl-ctx-head-right {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    flex-shrink: 0;
}
.gl-ctx-refresh {
    width: 24px;
    height: 24px;
    padding: 0;
    border-radius: 4px;
    border: 1px solid transparent;
    background: transparent;
    color: var(--gl-ctx-fg-muted);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    outline: none;
    transition: background-color 110ms ease, color 110ms ease;
}
.gl-ctx-refresh:hover {
    background-color: var(--gl-ctx-hover);
    color: var(--gl-ctx-fg);
}
.gl-ctx-refresh:active {
    background-color: var(--gl-ctx-press);
}
.gl-ctx-refresh:focus-visible {
    outline: 2px solid var(--gl-ctx-fg);
    outline-offset: 1px;
}
.gl-ctx-refresh[data-spinning="true"] svg {
    animation: glCtxSpin 700ms linear infinite;
}
@keyframes glCtxSpin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
}

.gl-ctx-badge {
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    font-weight: 600;
    line-height: 1;
    padding: 4px 7px;
    border-radius: 4px;
    background-color: var(--gl-ctx-state-soft);
    color: var(--gl-ctx-state);
}

.gl-ctx-hero {
    display: flex;
    align-items: baseline;
    gap: 5px;
    font-variant-numeric: tabular-nums;
}
.gl-ctx-used {
    font-size: 20px;
    font-weight: 600;
    line-height: 1.2;
    letter-spacing: -0.02em;
    color: var(--gl-ctx-fg);
}
.gl-ctx-cap {
    font-size: 13px;
    font-weight: 500;
    color: var(--gl-ctx-fg-muted);
}
.gl-ctx-exact {
    margin-top: 2px;
    font-size: 11px;
    color: var(--gl-ctx-fg-muted);
    font-variant-numeric: tabular-nums;
}

.gl-ctx-bar {
    margin-top: 9px;
    height: 4px;
    width: 100%;
    border-radius: 9999px;
    background-color: var(--gl-ctx-bar-track);
    overflow: hidden;
}
.gl-ctx-bar-fill {
    height: 100%;
    border-radius: 9999px;
    background-color: var(--gl-ctx-state);
    transition: width 240ms cubic-bezier(0.1, 0.9, 0.2, 1), background-color 180ms ease;
}

.gl-ctx-divider {
    height: 1px;
    margin: 10px 0;
    background-color: var(--gl-ctx-divider);
}

.gl-ctx-rows {
    display: flex;
    flex-direction: column;
    gap: 6px;
}
.gl-ctx-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 10px;
    font-size: 11.5px;
    min-height: 18px;
}
.gl-ctx-row-label {
    color: var(--gl-ctx-fg-muted);
    white-space: nowrap;
}
.gl-ctx-row-val {
    color: var(--gl-ctx-fg);
    font-variant-numeric: tabular-nums;
    font-weight: 500;
    text-align: right;
}
.gl-ctx-row-sub {
    color: var(--gl-ctx-fg-tertiary);
    font-weight: 400;
    margin-left: 4px;
}

.gl-ctx-groups {
    margin-top: 8px;
    padding-top: 8px;
    border-top: 1px solid var(--gl-ctx-divider);
    display: flex;
    flex-direction: column;
    gap: 4px;
}
.gl-ctx-group-title {
    font-size: 10.5px;
    font-weight: 600;
    color: var(--gl-ctx-fg-tertiary);
    margin-bottom: 2px;
}

.gl-ctx-note {
    font-size: 11.5px;
    color: var(--gl-ctx-fg-muted);
    line-height: 1.45;
}
.gl-ctx-error-box {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 6px 8px;
    border-radius: 4px;
    background-color: var(--gl-ctx-critical-soft);
    color: var(--gl-ctx-critical);
    font-size: 11.5px;
}
.gl-ctx-retry-btn {
    border: 1px solid var(--gl-ctx-stroke-ctrl);
    background-color: var(--gl-ctx-hover);
    color: var(--gl-ctx-fg);
    border-radius: 4px;
    padding: 2px 8px;
    font-size: 11px;
    cursor: pointer;
    flex-shrink: 0;
}
.gl-ctx-retry-btn:hover {
    background-color: var(--gl-ctx-press);
}

@media (prefers-reduced-motion: reduce) {
    .gl-ctx-btn,
    .gl-ctx-ring-arc,
    .gl-ctx-bar-fill,
    .gl-ctx-flyout,
    .gl-ctx-refresh[data-spinning="true"] svg {
        animation: none !important;
        transition: none !important;
    }
}

@media (forced-colors: active) {
    .gl-ctx-btn {
        background: ButtonFace !important;
        color: ButtonText !important;
        border-color: ButtonText !important;
    }
    .gl-ctx-ring-track {
        stroke: GrayText !important;
    }
    .gl-ctx-ring-arc {
        stroke: Highlight !important;
    }
    .gl-ctx-flyout {
        background: Canvas !important;
        background-image: none !important;
        color: CanvasText !important;
        border-color: CanvasText !important;
        box-shadow: none !important;
    }
    .gl-ctx-badge {
        background: Highlight !important;
        color: HighlightText !important;
    }
    .gl-ctx-bar {
        background: ButtonFace !important;
        border: 1px solid ButtonText;
    }
    .gl-ctx-bar-fill {
        background: Highlight !important;
    }
}
`;
    }

    function attachWidgetCss(target) {
        if (!target) return;
        const wc = target.webContents ? target.webContents : target;
        if (!wc || typeof wc.insertCSS !== 'function') return;
        if (typeof target.isDestroyed === 'function' && target.isDestroyed()) return;
        try {
            const url = typeof wc.getURL === 'function' ? wc.getURL() : '';
            if (url && !isHubUrl(url)) return;
        } catch (_) {}

        try {
            const prev = wc.__gravitylessWidgetCssKey;
            if (prev) Promise.resolve(wc.removeInsertedCSS(prev)).catch(function () {});
            wc.__gravitylessWidgetCssKey = null;
        } catch (_) {}

        const css = buildWidgetCss(currentLight, currentAccent, currentAccentInteraction,
                                   currentNoiseAlpha, currentNoiseFreq, currentNoiseTint, currentNoiseBase);
        try {
            const p = wc.insertCSS(css, { cssOrigin: 'user' });
            if (p && typeof p.then === 'function') {
                p.then(function (key) { wc.__gravitylessWidgetCssKey = key; }).catch(function () {});
            } else {
                wc.__gravitylessWidgetCssKey = p;
            }
        } catch (_) {}
    }

    global.__gravitylessMica = {
        buildCss: buildCss,
        attach: attach,
        getAlpha: function () { return currentAlpha; },
        setAlpha: function (v) { currentAlpha = clamp(Number(v) || 0, 0, 1); }
    };

    function findLocalesDir() {
        const candidates = [
            process.env.GRAVITYLESS_LOCALES_DIR,
            loadedConfig?.LocalesDir,
            path.resolve(__dirname, '..', '..', '..', '..', 'locales'),
            path.resolve(__dirname, '..', '..', '..', '..', 'data', 'locales'),
            path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Gravityless', 'locales'),
            path.join(process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Gravityless', 'locales'),
            path.join(process.env.LOCALAPPDATA || '', 'Gravityless', 'locales'),
            path.join(process.env.USERPROFILE || '', '.gravityless', 'locales')
        ].filter(Boolean);
        for (let i = 0; i < candidates.length; i++) {
            try {
                if (fs.existsSync(candidates[i])) return candidates[i];
            } catch (_) {}
        }
        return null;
    }

    function readDict(lang) {
        try {
            const dir = findLocalesDir();
            if (dir) {
                const fp = path.join(dir, lang + '.json');
                if (fs.existsSync(fp)) {
                    const parsed = JSON.parse(fs.readFileSync(fp, 'utf8'));
                    delete parsed._meta;
                    delete parsed['@metadata'];
                    return parsed;
                }
            }
        } catch (_) {}
        return {};
    }

    function translateDynamicZh(text) {
        const lang = window.__gravityless_lang || '';
        if (!lang.startsWith('zh')) return null;

        let m = text.match(/^Available AI credits:\s*(.+)$/);
        if (m) return '可用 AI 积分: ' + m[1];

        m = text.match(/^Resets in\s+(.+)$/);
        if (m) {
            const dur = m[1]
                .replace(/<\s*1m/gi, '不到 1 分钟')
                .replace(/(\d+)\s*d\b/gi, '$1 天 ')
                .replace(/(\d+)\s*h\b/gi, '$1 小时 ')
                .replace(/(\d+)\s*m\b/gi, '$1 分钟')
                .replace(/\s+/g, ' ')
                .trim();
            return '重置倒计时: ' + dur;
        }

        m = text.match(/^(\d+[smhd])\s+limit$/i);
        if (m) return m[1] + ' 配额限制';

        m = text.match(/^(\d+)\s+days?$/i);
        if (m) return m[1] + ' 天';

        m = text.match(/^(\d+)\s+comments?$/i);
        if (m) return m[1] + ' 条批注';

        m = text.match(/^(\d+)\s+agents?\s+running$/i);
        if (m) return m[1] + ' 个 Agent 正在运行';

        return null;
    }

    function injectI18n(wc) {
        if (!wc || typeof wc.executeJavaScript !== 'function') return;
        const loc = global.__gravitylessLocale || process.env.GRAVITYLESS_LOCALE || 'zh-CN';
        const dict = readDict(loc);
        const ds = JSON.stringify(dict);
        const code = `
        (function() {
            window.__gravityless_lang = ${JSON.stringify(loc)};
            window.__gravityless_i18n_dict = ${ds};

            const EXCLUDED_TAGS = ['PRE', 'CODE', 'SAMP', 'KBD'];
            const EXCLUDED_SELECTORS = [
                'pre', 'code', 'samp', 'kbd',
                '.monaco-editor', '.monaco-diff-editor',
                '[data-code-block]', '.syntax-highlighter',
                '.xterm', '.terminal', '.console-output',
                '[data-testid="user-message-bubble"]',
                '[data-testid="planner-response-text"]',
                '.agent-thought-content', '.file-content', '.diff-line',
                '[data-testid="file-preview"]', '[data-testid="pdf-viewport"]',
                '.gl-ctx-host', '.gl-ctx-flyout'
            ].join(', ');

            const LEAF_EXCLUDED = 'pre, code, samp, kbd, .monaco-editor, .monaco-diff-editor, [data-code-block], .xterm, .terminal, .gl-ctx-host, .gl-ctx-flyout';

            function isAllowed(el) {
                if (!el || !el.closest) return false;
                if (EXCLUDED_TAGS.includes(el.tagName)) return false;
                if (el.closest(EXCLUDED_SELECTORS)) return false;
                return true;
            }

            ${translateDynamicZh.toString()}

            function resolveTranslation(trimmed) {
                const d = window.__gravityless_i18n_dict;
                if (d && d[trimmed]) return d[trimmed];
                if (window.__gravityless_lang === 'en-US') return trimmed;
                return translateDynamicZh(trimmed);
            }

            function procText(node) {
                if (!node || node.nodeType !== Node.TEXT_NODE) return;
                if (!node.parentElement || !isAllowed(node.parentElement)) return;
                const currentVal = node.nodeValue;
                if (node.__gravityless_written !== undefined && currentVal !== node.__gravityless_written) {
                    node.__gravityless_orig = currentVal;
                }
                if (node.__gravityless_orig === undefined) {
                    node.__gravityless_orig = currentVal;
                }
                const orig = node.__gravityless_orig;
                const trimmed = orig.trim();
                if (!trimmed) return;
                const target = resolveTranslation(trimmed);
                if (target) {
                    const trans = orig.replace(trimmed, target);
                    if (node.nodeValue !== trans) {
                        node.nodeValue = trans;
                    }
                    node.__gravityless_written = trans;
                }
            }

            function procAttrs(el) {
                if (!el || el.nodeType !== Node.ELEMENT_NODE) return;
                if (el.closest('.monaco-editor, .monaco-diff-editor, .xterm, .terminal, .gl-ctx-host, .gl-ctx-flyout')) return;
                const d = window.__gravityless_i18n_dict;
                const isEn = window.__gravityless_lang === 'en-US';
                if (!d && !isEn) return;

                const attrs = ['placeholder', 'title', 'aria-label', 'aria-description', 'data-tooltip-content', 'data-tooltip', 'data-tip', 'data-description'];
                for (let i = 0; i < attrs.length; i++) {
                    const attr = attrs[i];
                    if (!el.hasAttribute(attr)) continue;
                    const origKey = '__gravityless_orig_' + attr;
                    const writtenKey = '__gravityless_written_' + attr;
                    const currentVal = el.getAttribute(attr);
                    if (el[writtenKey] !== undefined && currentVal !== el[writtenKey]) {
                        el[origKey] = currentVal;
                    }
                    if (el[origKey] === undefined) {
                        el[origKey] = currentVal;
                    }
                    const orig = el[origKey];
                    const trimmed = (orig || '').trim();
                    if (!trimmed) continue;
                    const target = resolveTranslation(trimmed);
                    if (target) {
                        const trans = orig.replace(trimmed, target);
                        if (el.getAttribute(attr) !== trans) {
                            el.setAttribute(attr, trans);
                        }
                        el[writtenKey] = trans;
                    }
                }
            }

            function walk(root) {
                if (!root) return;
                if (root.nodeType === Node.ELEMENT_NODE) {
                    if (root.matches && root.matches(LEAF_EXCLUDED)) return;
                    procAttrs(root);
                } else if (root.nodeType === Node.TEXT_NODE) {
                    procText(root);
                    return;
                }
                let child = root.firstChild;
                while (child) {
                    walk(child);
                    child = child.nextSibling;
                }
            }

            let pending = false;
            function scheduleWalk() {
                if (pending) return;
                pending = true;
                requestAnimationFrame(() => {
                    pending = false;
                    walk(document.body);
                });
            }

            window.__gravityless_set_lang = function(newLang, newDict) {
                window.__gravityless_lang = newLang;
                window.__gravityless_i18n_dict = newDict || {};
                scheduleWalk();
            };

            const obs = new MutationObserver((mutations) => {
                let shouldWalk = false;
                for (let i = 0; i < mutations.length; i++) {
                    const m = mutations[i];
                    if (m.type === 'childList' && m.addedNodes.length > 0) {
                        shouldWalk = true;
                        break;
                    } else if (m.type === 'characterData') {
                        if (m.target.nodeValue !== m.target.__gravityless_written) {
                            shouldWalk = true;
                            break;
                        }
                    } else if (m.type === 'attributes') {
                        const writtenKey = '__gravityless_written_' + m.attributeName;
                        if (m.target[writtenKey] === undefined || m.target.getAttribute(m.attributeName) !== m.target[writtenKey]) {
                            shouldWalk = true;
                            break;
                        }
                    }
                }
                if (shouldWalk) scheduleWalk();
            });

            if (document.body) {
                obs.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['placeholder', 'title', 'aria-label', 'aria-description', 'data-tooltip-content', 'data-tooltip', 'data-tip', 'data-description'] });
                scheduleWalk();
            } else {
                document.addEventListener('DOMContentLoaded', () => {
                    obs.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['placeholder', 'title', 'aria-label', 'aria-description', 'data-tooltip-content', 'data-tooltip', 'data-tip', 'data-description'] });
                    scheduleWalk();
                });
            }
        })();
        `;
        try {
            wc.executeJavaScript(code).catch(() => {});
        } catch (_) {}
    }

    function gravitylessContextIndicatorMain() {
        if (!/^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/i.test(window.location.origin || '')) return;
        if (window.__gravitylessContextIndicatorInstalled) return;
        window.__gravitylessContextIndicatorInstalled = true;

        const SVG_NS = 'http://www.w3.org/2000/svg';
        const RING_R = 6.25;
        const RING_C = 2 * Math.PI * RING_R;
        const TOKEN_GROUP_LABELS = {
            1: ['系统提示', 'System prompt'],
            SYSTEM_PROMPT: ['系统提示', 'System prompt'],
            TOKEN_TYPE_SYSTEM_PROMPT: ['系统提示', 'System prompt'],
            2: ['工具定义', 'Tools'],
            TOOLS: ['工具定义', 'Tools'],
            TOKEN_TYPE_TOOLS: ['工具定义', 'Tools'],
            3: ['规则', 'Rules'],
            RULES: ['规则', 'Rules'],
            TOKEN_TYPE_RULES: ['规则', 'Rules'],
            4: ['MCP 工具', 'MCP tools'],
            MCP_TOOLS: ['MCP 工具', 'MCP tools'],
            TOKEN_TYPE_MCP_TOOLS: ['MCP 工具', 'MCP tools'],
            5: ['技能', 'Skills'],
            SKILLS: ['技能', 'Skills'],
            TOKEN_TYPE_SKILLS: ['技能', 'Skills'],
            6: ['子智能体', 'Subagents'],
            SUBAGENTS: ['子智能体', 'Subagents'],
            TOKEN_TYPE_SUBAGENTS: ['子智能体', 'Subagents'],
            7: ['对话消息', 'Messages'],
            CHAT_MESSAGES: ['对话消息', 'Messages'],
            TOKEN_TYPE_CHAT_MESSAGES: ['对话消息', 'Messages'],
            8: ['工作流', 'Workflows'],
            WORKFLOWS: ['工作流', 'Workflows'],
            TOKEN_TYPE_WORKFLOWS: ['工作流', 'Workflows'],
            9: ['插件', 'Plugins'],
            PLUGINS: ['插件', 'Plugins'],
            TOKEN_TYPE_PLUGINS: ['插件', 'Plugins'],
            10: ['流程', 'Flows'],
            FLOWS: ['流程', 'Flows'],
            TOKEN_TYPE_FLOWS: ['流程', 'Flows']
        };

        const cacheByCascade = new Map();
        const footerRecords = new Map();
        let activeFlyout = null;
        let flyoutPortalOpen = false;
        let syncRaf = 0;

        function isZh() {
            const lang = String(window.__gravityless_lang || document.documentElement.lang || '').toLowerCase();
            return lang.startsWith('zh') || (!lang && String(navigator.language || '').toLowerCase().startsWith('zh'));
        }
        function tr(zh, en) {
            return isZh() ? zh : en;
        }
        function parseNum(v) {
            if (typeof v === 'number') return Number.isFinite(v) ? v : 0;
            if (typeof v === 'string' && v) {
                const n = Number(v);
                return Number.isFinite(n) ? n : 0;
            }
            return 0;
        }
        function formatK(n) {
            const v = Math.max(0, Number(n) || 0);
            if (v <= 0) return '0K';
            if (v >= 100000) return Math.round(v / 1000) + 'K';
            return (v / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
        }
        function formatInt(n) {
            return Math.max(0, Math.round(Number(n) || 0)).toLocaleString('en-US');
        }
        function getCsrfToken() {
            try {
                if (window.__APP_CONFIG__ && window.__APP_CONFIG__.csrfToken) {
                    return String(window.__APP_CONFIG__.csrfToken);
                }
                if (window.localStorage) {
                    const tok = window.localStorage.getItem('agy_session_csrf_token');
                    if (tok) return tok;
                }
            } catch (_) {}
            return '';
        }

        function el(tag, className, text) {
            const node = document.createElement(tag);
            if (className) node.className = className;
            if (text !== undefined && text !== null) node.textContent = String(text);
            return node;
        }
        function svgEl(tag, attrs) {
            const node = document.createElementNS(SVG_NS, tag);
            if (attrs) {
                for (const k in attrs) {
                    if (Object.prototype.hasOwnProperty.call(attrs, k)) {
                        node.setAttribute(k, String(attrs[k]));
                    }
                }
            }
            return node;
        }

        function createRingIcon() {
            const svg = svgEl('svg', { viewBox: '0 0 18 18', 'aria-hidden': 'true', class: 'gl-ctx-svg' });
            const track = svgEl('circle', {
                cx: 9, cy: 9, r: RING_R,
                fill: 'none', 'stroke-width': 2, class: 'gl-ctx-ring-track'
            });
            const arc = svgEl('circle', {
                cx: 9, cy: 9, r: RING_R,
                fill: 'none', 'stroke-width': 2.1, 'stroke-linecap': 'round',
                transform: 'rotate(-90 9 9)',
                'stroke-dasharray': RING_C.toFixed(2),
                'stroke-dashoffset': RING_C.toFixed(2),
                class: 'gl-ctx-ring-arc'
            });
            arc.style.opacity = '0';
            svg.appendChild(track);
            svg.appendChild(arc);
            return { svg, arc };
        }

        function createRefreshSvg() {
            const svg = svgEl('svg', { viewBox: '0 0 16 16', width: 13, height: 13, fill: 'none', 'aria-hidden': 'true' });
            const p = svgEl('path', {
                d: 'M13.2 8a5.2 5.2 0 1 1-1.38-3.54M13.5 2.4v2.7h-2.7',
                stroke: 'currentColor', 'stroke-width': 1.4, 'stroke-linecap': 'round', 'stroke-linejoin': 'round'
            });
            svg.appendChild(p);
            return svg;
        }

        function readFooterContext(footer) {
            const out = { cascadeId: '', isRunning: false, modelLabel: '', trajCtx: null };
            try {
                const trigger = footer.querySelector('button[data-testid="model-selector-trigger"]');
                if (trigger) {
                    const attrLabel = trigger.getAttribute('data-model-label');
                    out.modelLabel = (attrLabel && attrLabel.trim()) ? attrLabel.trim() : (trigger.textContent || '').trim();
                }
                const keys = Object.keys(footer);
                let fk = '';
                for (let i = 0; i < keys.length; i++) {
                    if (keys[i].startsWith('__reactFiber$') || keys[i].startsWith('__reactInternalInstance$')) {
                        fk = keys[i];
                        break;
                    }
                }
                if (!fk) return out;
                let cur = footer[fk];
                let hops = 0;
                while (cur && hops < 250) {
                    const props = cur.memoizedProps;
                    if (props && typeof props === 'object') {
                        if (!out.cascadeId && typeof props.cascadeId === 'string' && props.cascadeId) {
                            out.cascadeId = props.cascadeId;
                        }
                        if (typeof props.isRunning === 'boolean' && !out.isRunning && ('sendBlock' in props || 'cancelInvocation' in props)) {
                            out.isRunning = props.isRunning;
                        }
                        if (!out.modelLabel && props.currentModelConfig && props.currentModelConfig.label) {
                            out.modelLabel = String(props.currentModelConfig.label);
                        }
                        if (!out.trajCtx && props.value && typeof props.value === 'object' &&
                            typeof props.value.peek === 'function' && typeof props.value.listen === 'function') {
                            out.trajCtx = props.value;
                        }
                    }
                    cur = cur.return;
                    hops++;
                }
            } catch (_) {}
            if (!out.isRunning && footer.querySelector('[data-testid="input-cancel-button"]')) {
                out.isRunning = true;
            }
            return out;
        }

        function extractChatModel(item) {
            if (!item || typeof item !== 'object') return null;
            if (item.chatModel && typeof item.chatModel === 'object') return item.chatModel;
            if (item.metadata && item.metadata.case === 'chatModel' && item.metadata.value) return item.metadata.value;
            return null;
        }

        function computeMetrics(entry, agentState) {
            const cm = entry ? entry.latestChatModel : null;
            const usage = cm && cm.usage ? cm.usage : null;
            const ctxMeta = cm && cm.chatStartMetadata && cm.chatStartMetadata.contextWindowMetadata
                ? cm.chatStartMetadata.contextWindowMetadata : null;
            const cwu = (agentState && agentState.contextWindowUsage) || (entry && entry.agentCwu) || null;

            const input = parseNum(usage && usage.inputTokens);
            const cacheRead = parseNum(usage && usage.cacheReadTokens);
            const cacheWrite = parseNum(usage && usage.cacheWriteTokens);
            const actualInput = input + cacheRead + cacheWrite;
            const output = parseNum(usage && usage.outputTokens);
            const thinking = parseNum(usage && usage.thinkingOutputTokens);
            const est = parseNum(ctxMeta && ctxMeta.estimatedTokensUsed) || parseNum(cwu && cwu.estimatedTokensUsed);
            const max = parseNum(ctxMeta && ctxMeta.maxContextTokens) || parseNum(cwu && cwu.maxContextTokens);
            const used = actualInput > 0 ? actualInput : est;
            const isEstimated = actualInput <= 0 && est > 0;
            const ratio = max > 0 ? Math.min(1, Math.max(0, used / max)) : 0;
            const pct = max > 0 ? (used / max) * 100 : 0;
            const level = pct >= 90 ? 'critical' : (pct >= 70 ? 'caution' : 'healthy');

            const rawGroups = (ctxMeta && ctxMeta.tokenBreakdown && Array.isArray(ctxMeta.tokenBreakdown.groups) && ctxMeta.tokenBreakdown.groups.length)
                ? ctxMeta.tokenBreakdown.groups
                : ((cwu && Array.isArray(cwu.tokenGroups)) ? cwu.tokenGroups : []);
            const groups = [];
            for (let i = 0; i < rawGroups.length; i++) {
                const g = rawGroups[i];
                if (!g) continue;
                const num = parseNum(g.numTokens);
                if (num <= 0) continue;
                const mapped = TOKEN_GROUP_LABELS[g.type];
                const label = mapped ? tr(mapped[0], mapped[1]) : (g.name || String(g.type || 'Other'));
                groups.push({ label, tokens: num });
            }
            groups.sort((a, b) => b.tokens - a.tokens);

            return {
                hasData: max > 0 && (used > 0 || actualInput > 0 || est > 0),
                hasModelCap: max > 0,
                used,
                max,
                actualInput,
                input,
                cacheRead,
                cacheWrite,
                output,
                thinking,
                est,
                isEstimated,
                ratio,
                pct,
                level,
                modelDisplayName: (cm && cm.modelDisplayName) ? String(cm.modelDisplayName) : '',
                groups
            };
        }

        function ingestFromAgentState(cascadeId, agentState) {
            if (!cascadeId || !agentState || typeof agentState !== 'object') return;
            const entry = ensureCache(cascadeId);
            const slice = agentState.trajectorySlice;
            if (slice) {
                const total = parseNum(slice.totalGeneratorMetadataLength);
                if (total > 0 && total < entry.offset) {
                    entry.offset = 0;
                    entry.latestChatModel = null;
                }
                const list = slice.generatorMetadatasInSlice;
                if (Array.isArray(list) && list.length > 0) {
                    for (let i = list.length - 1; i >= 0; i--) {
                        const cm = extractChatModel(list[i]);
                        if (cm) {
                            entry.latestChatModel = cm;
                            break;
                        }
                    }
                    if (total > 0 && total >= list.length) {
                        entry.offset = Math.max(entry.offset, total);
                    }
                }
            }
            if (agentState.contextWindowUsage) {
                entry.agentCwu = agentState.contextWindowUsage;
            }
        }

        function ensureCache(cascadeId) {
            let entry = cacheByCascade.get(cascadeId);
            if (!entry) {
                entry = {
                    offset: 0,
                    latestChatModel: null,
                    agentCwu: null,
                    loading: false,
                    error: '',
                    lastFetchedAt: 0,
                    inflight: null
                };
                cacheByCascade.set(cascadeId, entry);
            }
            return entry;
        }

        async function callLsRpc(method, payload) {
            const headers = {
                'Content-Type': 'application/json',
                'Connect-Protocol-Version': '1'
            };
            const csrf = getCsrfToken();
            if (csrf) headers['x-codeium-csrf-token'] = csrf;
            const resp = await window.fetch(window.location.origin + '/exa.language_server_pb.LanguageServerService/' + method, {
                method: 'POST',
                headers,
                body: JSON.stringify(payload || {})
            });
            if (!resp.ok) {
                throw new Error('HTTP ' + resp.status);
            }
            return await resp.json();
        }

        function refreshCascade(cascadeId, forceTailCheck, trajCtx) {
            if (!cascadeId) return Promise.resolve();
            const entry = ensureCache(cascadeId);
            if (entry.inflight) return entry.inflight;

            let startOffset = entry.offset;
            if (forceTailCheck && startOffset > 2) {
                startOffset = Math.max(0, startOffset - 2);
            }
            if (startOffset === 0 && trajCtx && typeof trajCtx.peek === 'function') {
                try {
                    const st = trajCtx.peek(cascadeId);
                    ingestFromAgentState(cascadeId, st);
                    const total = st && st.trajectorySlice ? parseNum(st.trajectorySlice.totalGeneratorMetadataLength) : 0;
                    if (total > 6) startOffset = total - 6;
                } catch (_) {}
            }

            entry.loading = true;
            entry.error = '';
            updateAllUi();

            const task = (async () => {
                try {
                    let curOffset = startOffset;
                    let pages = 0;
                    while (pages < 30) {
                        pages++;
                        const res = await callLsRpc('GetCascadeTrajectoryGeneratorMetadata', {
                            cascadeId,
                            generatorMetadataOffset: curOffset
                        });
                        const list = res && Array.isArray(res.generatorMetadata) ? res.generatorMetadata : [];
                        if (list.length === 0) break;
                        for (let i = list.length - 1; i >= 0; i--) {
                            const cm = extractChatModel(list[i]);
                            if (cm) {
                                entry.latestChatModel = cm;
                                break;
                            }
                        }
                        curOffset += list.length;
                    }
                    if (curOffset > entry.offset) entry.offset = curOffset;
                    entry.lastFetchedAt = Date.now();
                } catch (err) {
                    entry.error = err && err.message ? String(err.message) : 'RPC error';
                } finally {
                    entry.loading = false;
                    entry.inflight = null;
                    updateAllUi();
                }
            })();
            entry.inflight = task;
            return task;
        }

        function setNativePortal(open) {
            try {
                const wcv = window.electronNative && window.electronNative.wcvBrowser;
                if (!wcv || typeof wcv.setPortalOverlayOpen !== 'function') return;
                if (open) {
                    flyoutPortalOpen = true;
                    Promise.resolve(wcv.setPortalOverlayOpen(true)).catch(() => {});
                } else if (flyoutPortalOpen) {
                    flyoutPortalOpen = false;
                    const otherOpen = document.querySelector('[data-popover-popup], [data-radix-popper-content-wrapper], [role="dialog"]:not(#gl-ctx-flyout)');
                    if (!otherOpen) {
                        Promise.resolve(wcv.setPortalOverlayOpen(false)).catch(() => {});
                    }
                }
            } catch (_) {}
        }

        function updateButtonRing(rec) {
            const entry = rec.cascadeId ? cacheByCascade.get(rec.cascadeId) : null;
            let agentState = null;
            if (rec.cascadeId && rec.trajCtx && typeof rec.trajCtx.peek === 'function') {
                try {
                    agentState = rec.trajCtx.peek(rec.cascadeId);
                    if (agentState) ingestFromAgentState(rec.cascadeId, agentState);
                } catch (_) {}
            }
            const m = computeMetrics(entry, agentState);
            const visRatio = (m.max > 0 && m.used > 0) ? Math.max(0.02, Math.min(1, m.ratio)) : 0;
            const offset = RING_C * (1 - visRatio);
            rec.arc.style.strokeDasharray = RING_C.toFixed(2);
            rec.arc.style.strokeDashoffset = offset.toFixed(2);
            rec.arc.style.opacity = visRatio > 0 ? '1' : '0';
            rec.btn.setAttribute('data-level', m.level);

            const ariaText = m.hasData
                ? `${tr('上下文窗口', 'Context window')}: ${formatK(m.used)} / ${formatK(m.max)} (${m.pct.toFixed(1)}%)`
                : tr('上下文窗口: 暂无数据', 'Context window: no data yet');
            rec.btn.setAttribute('aria-label', ariaText);
            rec.btn.setAttribute('title', ariaText);
        }

        function addKvRow(parent, label, value, subValue) {
            const row = el('div', 'gl-ctx-row');
            row.appendChild(el('span', 'gl-ctx-row-label', label));
            const valWrap = el('span', 'gl-ctx-row-val', value);
            if (subValue) {
                valWrap.appendChild(el('span', 'gl-ctx-row-sub', subValue));
            }
            row.appendChild(valWrap);
            parent.appendChild(row);
        }

        function renderFlyout() {
            if (!activeFlyout || !activeFlyout.flyout) return;
            const { flyout, rec } = activeFlyout;
            const cascadeId = rec.cascadeId;
            const entry = cascadeId ? cacheByCascade.get(cascadeId) : null;
            let agentState = null;
            if (cascadeId && rec.trajCtx && typeof rec.trajCtx.peek === 'function') {
                try { agentState = rec.trajCtx.peek(cascadeId); } catch (_) {}
            }
            const m = computeMetrics(entry, agentState);
            flyout.setAttribute('data-level', m.level);
            while (flyout.firstChild) flyout.removeChild(flyout.firstChild);

            // Header
            const head = el('div', 'gl-ctx-head');
            const titleWrap = el('div', 'gl-ctx-title-wrap');
            titleWrap.appendChild(el('div', 'gl-ctx-title', tr('上下文窗口', 'Context window')));
            const subLabel = rec.modelLabel || m.modelDisplayName || (cascadeId ? tr('当前对话', 'Current conversation') : tr('新对话', 'New conversation'));
            titleWrap.appendChild(el('div', 'gl-ctx-subtitle', subLabel));
            head.appendChild(titleWrap);

            const headRight = el('div', 'gl-ctx-head-right');
            if (cascadeId) {
                const refBtn = el('button', 'gl-ctx-refresh');
                refBtn.type = 'button';
                refBtn.setAttribute('aria-label', tr('刷新上下文统计', 'Refresh context stats'));
                refBtn.setAttribute('title', tr('刷新', 'Refresh'));
                refBtn.setAttribute('data-spinning', entry && entry.loading ? 'true' : 'false');
                refBtn.appendChild(createRefreshSvg());
                refBtn.addEventListener('mousedown', (ev) => { ev.preventDefault(); ev.stopPropagation(); });
                refBtn.addEventListener('click', (ev) => {
                    ev.preventDefault();
                    ev.stopPropagation();
                    refreshCascade(cascadeId, true, rec.trajCtx);
                });
                headRight.appendChild(refBtn);
            }
            const badgeText = m.hasData ? m.pct.toFixed(1) + '%' : '--%';
            headRight.appendChild(el('span', 'gl-ctx-badge', badgeText));
            head.appendChild(headRight);
            flyout.appendChild(head);

            // Error banner if RPC failed and no data yet
            if (entry && entry.error && !m.hasData) {
                const errBox = el('div', 'gl-ctx-error-box');
                errBox.appendChild(el('span', '', tr('读取失败: ', 'Failed: ') + entry.error));
                const retryBtn = el('button', 'gl-ctx-retry-btn', tr('重试', 'Retry'));
                retryBtn.type = 'button';
                retryBtn.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    refreshCascade(cascadeId, true, rec.trajCtx);
                });
                errBox.appendChild(retryBtn);
                flyout.appendChild(errBox);
                return;
            }

            // Hero readout
            const hero = el('div', 'gl-ctx-hero');
            hero.appendChild(el('span', 'gl-ctx-used', m.hasData ? formatK(m.used) : '0K'));
            hero.appendChild(el('span', 'gl-ctx-cap', m.hasModelCap ? ('/ ' + formatK(m.max)) : '/ --'));
            flyout.appendChild(hero);

            if (m.hasData) {
                const rem = Math.max(0, m.max - m.used);
                const prefix = m.isEstimated ? (tr('估算 · ', 'Est. · ')) : '';
                flyout.appendChild(el('div', 'gl-ctx-exact', `${prefix}${formatInt(m.used)} / ${formatInt(m.max)} tokens · ${tr('剩余', 'left')} ${formatK(rem)}`));
            } else if (entry && entry.loading) {
                flyout.appendChild(el('div', 'gl-ctx-exact', tr('正在读取上下文统计...', 'Loading context stats...')));
            } else {
                flyout.appendChild(el('div', 'gl-ctx-exact', tr('发送首条消息后展示窗口占用', 'Usage appears after the first turn')));
            }

            // Progress bar
            const bar = el('div', 'gl-ctx-bar');
            const fill = el('div', 'gl-ctx-bar-fill');
            fill.style.width = m.hasData ? Math.min(100, Math.max(2, m.ratio * 100)).toFixed(1) + '%' : '0%';
            bar.appendChild(fill);
            flyout.appendChild(bar);

            if (!m.hasData) {
                return;
            }

            flyout.appendChild(el('div', 'gl-ctx-divider'));

            const rows = el('div', 'gl-ctx-rows');
            if (m.actualInput > 0) {
                const cacheSub = m.cacheRead > 0 ? tr(`(缓存 ${formatK(m.cacheRead)})`, `(${formatK(m.cacheRead)} cached)`) : '';
                addKvRow(rows, tr('实测输入', 'Actual input'), formatInt(m.actualInput), cacheSub);
            }
            if (m.est > 0) {
                addKvRow(rows, tr('请求前估算', 'Pre-send est.'), formatInt(m.est));
            }
            if (m.output > 0) {
                const thinkSub = m.thinking > 0 ? tr(`(思考 ${formatK(m.thinking)})`, `(${formatK(m.thinking)} think)`) : '';
                addKvRow(rows, tr('上轮输出', 'Last output'), formatInt(m.output), thinkSub);
            }
            addKvRow(rows, tr('窗口上限', 'Context limit'), formatInt(m.max));
            flyout.appendChild(rows);

            if (m.groups.length > 0) {
                const gWrap = el('div', 'gl-ctx-groups');
                gWrap.appendChild(el('div', 'gl-ctx-group-title', tr('上下文构成 (估算)', 'Context breakdown (est.)')));
                const limit = Math.min(5, m.groups.length);
                for (let i = 0; i < limit; i++) {
                    const g = m.groups[i];
                    addKvRow(gWrap, g.label, formatK(g.tokens));
                }
                flyout.appendChild(gWrap);
            }
        }

        function positionFlyout() {
            if (!activeFlyout || !activeFlyout.flyout || !activeFlyout.rec) return;
            const { flyout, rec } = activeFlyout;
            if (!rec.btn.isConnected) {
                closeFlyout(false);
                return;
            }
            const br = rec.btn.getBoundingClientRect();
            const fw = flyout.offsetWidth || 280;
            const fh = flyout.offsetHeight || 200;
            const vw = window.innerWidth || document.documentElement.clientWidth;
            const vh = window.innerHeight || document.documentElement.clientHeight;
            const gap = 8;
            let left = Math.min(Math.max(12, br.left), Math.max(12, vw - fw - 12));
            let top = br.top - fh - gap;
            if (top < 12 && (br.bottom + gap + fh) <= vh - 12) {
                top = br.bottom + gap;
            } else {
                top = Math.max(12, top);
            }
            flyout.style.left = Math.round(left) + 'px';
            flyout.style.top = Math.round(top) + 'px';
        }

        function closeFlyout(returnFocus) {
            if (!activeFlyout) return;
            const { flyout, rec } = activeFlyout;
            activeFlyout = null;
            if (flyout && flyout.parentNode) flyout.parentNode.removeChild(flyout);
            if (rec && rec.btn) {
                rec.btn.setAttribute('aria-expanded', 'false');
                if (returnFocus && rec.btn.isConnected) {
                    try { rec.btn.focus(); } catch (_) {}
                }
            }
            setNativePortal(false);
        }

        function openFlyout(rec) {
            if (activeFlyout && activeFlyout.rec === rec) {
                closeFlyout(false);
                return;
            }
            closeFlyout(false);
            const flyout = el('div', 'gl-ctx-flyout');
            flyout.id = 'gl-ctx-flyout';
            flyout.setAttribute('role', 'dialog');
            flyout.setAttribute('aria-modal', 'false');
            flyout.setAttribute('aria-label', tr('上下文窗口详情', 'Context window details'));
            flyout.style.visibility = 'hidden';
            document.body.appendChild(flyout);
            rec.btn.setAttribute('aria-expanded', 'true');
            activeFlyout = { flyout, rec };
            setNativePortal(true);
            renderFlyout();
            positionFlyout();
            flyout.style.visibility = '';
            if (rec.cascadeId) {
                const entry = cacheByCascade.get(rec.cascadeId);
                if (!entry || Date.now() - entry.lastFetchedAt > 1200) {
                    refreshCascade(rec.cascadeId, true, rec.trajCtx);
                }
            }
        }

        function updateAllUi() {
            for (const [footer, rec] of footerRecords.entries()) {
                if (!footer.isConnected) {
                    if (rec.unlisten) { try { rec.unlisten(); } catch (_) {} }
                    footerRecords.delete(footer);
                    if (activeFlyout && activeFlyout.rec === rec) closeFlyout(false);
                    continue;
                }
                updateButtonRing(rec);
            }
            if (activeFlyout) {
                renderFlyout();
                positionFlyout();
            }
        }

        function syncTrajListener(rec, nextCascadeId, nextTrajCtx) {
            if (rec.listenedId === nextCascadeId && rec.listenedCtx === nextTrajCtx) return;
            if (rec.unlisten) {
                try { rec.unlisten(); } catch (_) {}
                rec.unlisten = null;
            }
            rec.listenedId = nextCascadeId;
            rec.listenedCtx = nextTrajCtx;
            if (nextCascadeId && nextTrajCtx && typeof nextTrajCtx.listen === 'function') {
                try {
                    rec.unlisten = nextTrajCtx.listen(nextCascadeId, () => {
                        try {
                            const st = nextTrajCtx.peek(nextCascadeId);
                            ingestFromAgentState(nextCascadeId, st);
                            updateAllUi();
                            const total = st && st.trajectorySlice ? parseNum(st.trajectorySlice.totalGeneratorMetadataLength) : 0;
                            const entry = ensureCache(nextCascadeId);
                            if (total > entry.offset) {
                                refreshCascade(nextCascadeId, false, nextTrajCtx);
                            }
                        } catch (_) {}
                    });
                } catch (_) {}
            }
        }

        function syncFooters() {
            syncRaf = 0;
            const footers = document.querySelectorAll('.agy-composer-footer');
            for (let i = 0; i < footers.length; i++) {
                const footer = footers[i];
                const trigger = footer.querySelector('button[data-testid="model-selector-trigger"]');
                if (!trigger) continue;
                const anchor = trigger.closest('.no-focus-agent-input') || trigger.parentElement;
                if (!anchor || !anchor.parentElement) continue;

                let rec = footerRecords.get(footer);
                if (!rec) {
                    const host = el('div', 'gl-ctx-host');
                    const btn = el('button', 'gl-ctx-btn');
                    btn.type = 'button';
                    btn.setAttribute('aria-haspopup', 'dialog');
                    btn.setAttribute('aria-expanded', 'false');
                    const { svg, arc } = createRingIcon();
                    btn.appendChild(svg);
                    host.appendChild(btn);

                    rec = {
                        footer,
                        host,
                        btn,
                        arc,
                        cascadeId: '',
                        isRunning: false,
                        modelLabel: '',
                        trajCtx: null,
                        listenedId: '',
                        listenedCtx: null,
                        unlisten: null
                    };
                    btn.addEventListener('mousedown', (ev) => {
                        ev.preventDefault();
                        ev.stopPropagation();
                    });
                    btn.addEventListener('click', (ev) => {
                        ev.preventDefault();
                        ev.stopPropagation();
                        openFlyout(rec);
                    });
                    footerRecords.set(footer, rec);
                }

                if (rec.host.previousElementSibling !== anchor || rec.host.parentElement !== anchor.parentElement) {
                    anchor.parentElement.insertBefore(rec.host, anchor.nextSibling);
                }

                const ctx = readFooterContext(footer);
                const cascadeChanged = ctx.cascadeId !== rec.cascadeId;
                const finishedTurn = rec.isRunning && !ctx.isRunning;
                rec.cascadeId = ctx.cascadeId;
                rec.isRunning = ctx.isRunning;
                rec.modelLabel = ctx.modelLabel;
                rec.trajCtx = ctx.trajCtx;

                syncTrajListener(rec, ctx.cascadeId, ctx.trajCtx);

                if (ctx.cascadeId && (cascadeChanged || !cacheByCascade.has(ctx.cascadeId))) {
                    refreshCascade(ctx.cascadeId, false, ctx.trajCtx);
                } else if (ctx.cascadeId && finishedTurn) {
                    setTimeout(() => refreshCascade(ctx.cascadeId, true, rec.trajCtx), 250);
                    setTimeout(() => refreshCascade(ctx.cascadeId, true, rec.trajCtx), 950);
                }
            }
            updateAllUi();
        }

        function scheduleSync() {
            if (syncRaf) return;
            syncRaf = window.requestAnimationFrame(syncFooters);
        }

        document.addEventListener('pointerdown', (ev) => {
            if (!activeFlyout) return;
            const target = ev.target;
            if (activeFlyout.flyout.contains(target)) return;
            if (activeFlyout.rec && activeFlyout.rec.btn.contains(target)) return;
            closeFlyout(false);
        }, true);

        document.addEventListener('keydown', (ev) => {
            if (activeFlyout && ev.key === 'Escape') {
                ev.preventDefault();
                ev.stopPropagation();
                closeFlyout(true);
            }
        }, true);

        window.addEventListener('resize', () => {
            if (activeFlyout) positionFlyout();
        });

        window.addEventListener('gl:lang-changed', () => {
            updateAllUi();
        });

        window.setInterval(() => {
            if (document.hidden) return;
            const seen = new Set();
            for (const rec of footerRecords.values()) {
                if (!rec.cascadeId || seen.has(rec.cascadeId)) continue;
                const isOpen = activeFlyout && activeFlyout.rec === rec;
                if (rec.isRunning || isOpen) {
                    seen.add(rec.cascadeId);
                    refreshCascade(rec.cascadeId, true, rec.trajCtx);
                }
            }
        }, 1800);

        const obs = new MutationObserver(() => scheduleSync());
        const mountRoot = document.getElementById('root') || document.body;
        if (mountRoot) {
            obs.observe(mountRoot, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-model-label'] });
            scheduleSync();
        } else {
            document.addEventListener('DOMContentLoaded', () => {
                obs.observe(document.getElementById('root') || document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-model-label'] });
                scheduleSync();
            });
        }
    }

    function injectContextIndicator(wc) {
        if (!wc || typeof wc.executeJavaScript !== 'function') return;
        try {
            const url = typeof wc.getURL === 'function' ? wc.getURL() : '';
            if (url && !isHubUrl(url)) return;
        } catch (_) {}
        const loc = global.__gravitylessLocale || process.env.GRAVITYLESS_LOCALE || 'zh-CN';
        const code = 'window.__gravityless_lang = window.__gravityless_lang || ' + JSON.stringify(loc) + ';\n'
            + '(' + gravitylessContextIndicatorMain.toString() + ')();';
        try {
            wc.executeJavaScript(code).catch(() => {});
        } catch (_) {}
    }

    // ── 9. Auto-attach dom-ready & did-finish-load on all webContents ──
    let windowMaterialDebounceTimer = null;
    function scheduleApplyMaterialToWindows() {
        if (windowMaterialDebounceTimer) return;
        windowMaterialDebounceTimer = setTimeout(function() {
            windowMaterialDebounceTimer = null;
            applyMaterialToWindows(currentMaterial);
        }, 30);
    }

    if (electron.app) {
        electron.app.on('web-contents-created', function(_evt, contents) {
            try {
                const applyOnContents = function() {
                    try { attach(contents); } catch (_) {}
                    try { attachWidgetCss(contents); } catch (_) {}
                    scheduleApplyMaterialToWindows();
                };
                contents.on('dom-ready', function() {
                    applyOnContents();
                    try { injectI18n(contents); } catch (_) {}
                    try { injectContextIndicator(contents); } catch (_) {}
                });
                contents.on('did-finish-load', function() {
                    applyOnContents();
                    try { injectContextIndicator(contents); } catch (_) {}
                });
            } catch (_) {}
        });

        if (typeof electron.app.whenReady === 'function') {
            electron.app.whenReady().then(function() {
                scheduleApplyMaterialToWindows();
            }).catch(function() {});
        }
    }

    // ── 10. Daemon SSE & Real-time Theme Sync ──
    function applyMaterialToWindow(win, material) {
        if (!win || !material) return;
        const revertAll = (material === 'none');
        const solidBg = currentLight ? '#FAFAFA' : '#131313';
        try {
            if (win.isDestroyed()) return;
            if (typeof win.setBackgroundMaterial === 'function') {
                try { win.setBackgroundMaterial(revertAll ? 'none' : material); } catch (_) {}
            }
            if (typeof win.setBackgroundColor === 'function') {
                try { win.setBackgroundColor(revertAll ? solidBg : '#00000000'); } catch (_) {}
            }
            if (typeof win.setTitleBarOverlay === 'function') {
                try {
                    win.setTitleBarOverlay({
                        color: revertAll ? solidBg : '#00000000',
                        symbolColor: currentLight ? '#383A42' : '#FAFAFA'
                    });
                } catch (_) {}
            }
            if (win.contentView && Array.isArray(win.contentView.children)) {
                const kids = win.contentView.children;
                const hubContents = win.webContents;
                const contentSize = typeof win.getContentSize === 'function' ? win.getContentSize() : (typeof win.getSize === 'function' ? win.getSize() : [1280, 800]);
                const [w, h] = contentSize;

                for (let k = 0; k < kids.length; k++) {
                    const child = kids[k];
                    const isHubWcv = hubContents ? Boolean(child && child.webContents === hubContents) : (k === 1);
                    const isCurtain = child && (child.__gravitylessIsCurtain !== undefined
                        ? child.__gravitylessIsCurtain
                        : (k === 0 && Boolean(child.webContents) && !isHubWcv));
                    const isClipView = Boolean(child && !child.webContents && !isCurtain && !isHubWcv);

                    if (isHubWcv) {
                        if (typeof child.__origSetVisible === 'function') {
                            child.__origSetVisible.call(child, true);
                        } else if (typeof child.setVisible === 'function') {
                            child.setVisible(true);
                        }
                        if (typeof child.__origSetBg === 'function') {
                            child.__origSetBg.call(child, revertAll ? solidBg : '#00000000');
                        } else if (typeof child.setBackgroundColor === 'function') {
                            child.setBackgroundColor(revertAll ? solidBg : '#00000000');
                        }
                        if (typeof child.__origSetBounds === 'function') {
                            child.__origSetBounds.call(child, { x: 0, y: 0, width: w, height: h });
                        } else if (typeof child.setBounds === 'function') {
                            child.setBounds({ x: 0, y: 0, width: w, height: h });
                        }
                    } else if (isCurtain) {
                        // 2-layer curtainView underlay: hide/0x0 under material mode, restore on none
                        if (typeof child.__origSetVisible === 'function') {
                            child.__origSetVisible.call(child, revertAll ? true : false);
                        } else if (typeof child.setVisible === 'function') {
                            child.setVisible(revertAll ? true : false);
                        }
                        if (typeof child.__origSetBg === 'function') {
                            child.__origSetBg.call(child, revertAll ? solidBg : '#00000000');
                        } else if (typeof child.setBackgroundColor === 'function') {
                            child.setBackgroundColor(revertAll ? solidBg : '#00000000');
                        }
                        if (typeof child.__origSetBounds === 'function') {
                            child.__origSetBounds.call(child, revertAll ? { x: 0, y: 0, width: w, height: h } : { x: 0, y: 0, width: 0, height: 0 });
                        } else if (typeof child.setBounds === 'function') {
                            child.setBounds(revertAll ? { x: 0, y: 0, width: w, height: h } : { x: 0, y: 0, width: 0, height: 0 });
                        }
                    } else if (isClipView) {
                        // 3-layer clipView: never alter visibility or bounds; only sync background color
                        if (typeof child.__origSetBg === 'function') {
                            child.__origSetBg.call(child, revertAll ? solidBg : '#00000000');
                        } else if (typeof child.setBackgroundColor === 'function') {
                            child.setBackgroundColor(revertAll ? solidBg : '#00000000');
                        }
                    } else if (!revertAll && typeof child.setBackgroundColor === 'function') {
                        child.setBackgroundColor('#00000000');
                    }
                }
            }
        } catch (_) {}
    }

    function applyMaterialToWindows(material) {
        if (!material) return;
        const baseWins = typeof electron.BaseWindow?.getAllWindows === 'function' ? electron.BaseWindow.getAllWindows() : [];
        const browserWins = typeof electron.BrowserWindow?.getAllWindows === 'function' ? electron.BrowserWindow.getAllWindows() : [];
        const seen = new Set();
        for (const win of [...baseWins, ...browserWins]) {
            if (!win || seen.has(win)) continue;
            seen.add(win);
            applyMaterialToWindow(win, material);
        }
    }

    if (!daemonPort) return;

    function syncTheme() {
        http.get('http://127.0.0.1:' + daemonPort + '/api/v1/theme.json?t=' + Date.now(), function(res) {
            let data = '';
            res.on('data', function(c) { data += c; });
            res.on('end', function() {
                let theme = null;
                try { theme = JSON.parse(data); } catch (_) { return; }
                if (!theme) return;
                if (theme.opacity !== undefined && theme.opacity !== null) currentAlpha = clamp(Number(theme.opacity), 0, 1);
                if (theme.popupOpacity !== undefined && theme.popupOpacity !== null) currentPopupAlpha = clamp(Number(theme.popupOpacity), 0, 1);
                currentLight = (theme.theme === 'light');
                if (theme.material !== undefined && theme.material !== null) currentMaterial = String(theme.material).trim().toLowerCase();
                if (theme.accent !== undefined && theme.accent !== null && theme.accent !== '') currentAccent = String(theme.accent);
                if (theme.accentInteraction !== undefined && theme.accentInteraction !== null) currentAccentInteraction = String(theme.accentInteraction) !== 'false';
                if (theme.noiseAlpha !== undefined && theme.noiseAlpha !== null) currentNoiseAlpha = Number(theme.noiseAlpha);
                if (theme.noiseFrequency !== undefined && theme.noiseFrequency !== null) currentNoiseFreq = Number(theme.noiseFrequency);
                if (theme.noiseTintRatio !== undefined && theme.noiseTintRatio !== null) currentNoiseTint = Number(theme.noiseTintRatio);
                if (theme.noiseBaseAlpha !== undefined && theme.noiseBaseAlpha !== null) currentNoiseBase = Number(theme.noiseBaseAlpha);

                applyMaterialToWindows(currentMaterial);
                const allWc = (electron.webContents && typeof electron.webContents.getAllWebContents === 'function')
                    ? electron.webContents.getAllWebContents()
                    : [];
                for (const wc of allWc) {
                    attach(wc);
                    attachWidgetCss(wc);
                }
            });
        }).on('error', function() {});
    }

    function connectSSE() {
        const req = http.get('http://127.0.0.1:' + daemonPort + '/api/v1/events', function(res) {
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
                    const code = 'window.__gravityless_lang = ' + JSON.stringify(newLang) + '; '
                        + 'if (window.__gravityless_set_lang) { window.__gravityless_set_lang('
                        + JSON.stringify(newLang) + ', ' + JSON.stringify(readDict(newLang)) + '); } '
                        + 'try { window.dispatchEvent(new CustomEvent("gl:lang-changed", { detail: ' + JSON.stringify(newLang) + ' })); } catch (_) {}';
                    const allWc = (electron.webContents && typeof electron.webContents.getAllWebContents === 'function')
                        ? electron.webContents.getAllWebContents()
                        : [];
                    for (const wc of allWc) {
                        try {
                            if (!wc.isDestroyed()) wc.executeJavaScript(code).catch(function () {});
                        } catch (_) {}
                    }
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
