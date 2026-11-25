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
    private const int PatchVersion = 59;
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
                '[data-testid="file-preview"]', '[data-testid="pdf-viewport"]'
            ].join(', ');

            const LEAF_EXCLUDED = 'pre, code, samp, kbd, .monaco-editor, .monaco-diff-editor, [data-code-block], .xterm, .terminal';

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
                if (el.closest('.monaco-editor, .monaco-diff-editor, .xterm, .terminal')) return;
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
                    scheduleApplyMaterialToWindows();
                };
                contents.on('dom-ready', function() {
                    applyOnContents();
                    try { injectI18n(contents); } catch (_) {}
                });
                contents.on('did-finish-load', applyOnContents);
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
                for (const wc of allWc) attach(wc);
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
                    const code = 'if (window.__gravityless_set_lang) { window.__gravityless_set_lang('
                        + JSON.stringify(newLang) + ', ' + JSON.stringify(readDict(newLang)) + '); }';
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
