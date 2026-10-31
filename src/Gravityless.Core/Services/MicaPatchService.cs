using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gravityless.Core.Services;

public class MicaPatchService
{
    private const string MicaMarker = "/* GRAVITYLESS_MICA_PATCH */";

    private const string MicaHookMarker = "/* GRAVITYLESS_MICA_WIN_HOOK */";
    private const string MicaSyncMarker = "/* GRAVITYLESS_MICA_DAEMON_SYNC */";
    private const string BridgeMarker = "/* GRAVITYLESS_DAEMON_BRIDGE */";

    /// <summary>Marks a main.js where the core protocol self-registration was disabled.</summary>
    private const string ProtocolOwnerMarker = "/* GRAVITYLESS_PROTOCOL_OWNER */";

    /// <summary>Patch anchor: the core's protocol self-registration statement in main.js.</summary>
    private const string ProtocolCallTarget = "electron_1.app.setAsDefaultProtocolClient(PROTOCOL);";

    /// <summary>Bump when template semantics change; a stale stamp triggers a rebuild from *.stock and re-injection.</summary>
    private const int MicaPatchVersion = 39;
    private static readonly string MicaPatchStamp = $"/* GRAVITYLESS_MICA_PATCH_VERSION: {MicaPatchVersion} */";

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
                        var offsetStr = offsetProp.GetString();
                        if (offsetStr != null && long.TryParse(offsetStr, out var parsedOffset))
                        {
                            offset = parsedOffset;
                        }
                        else if (offsetProp.ValueKind == JsonValueKind.Number)
                        {
                            offset = offsetProp.GetInt64();
                        }
                    }

                    var parent = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    if (size == 0)
                    {
                        File.WriteAllBytes(destPath, Array.Empty<byte>());
                    }
                    else
                    {
                        long fileAbsOffset = baseOffset + offset;
                        asarStream.Seek(fileAbsOffset, SeekOrigin.Begin);

                        using var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        byte[] buffer = new byte[65536];
                        long remaining = size;
                        while (remaining > 0)
                        {
                            int read = asarStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read <= 0) break;
                            outStream.Write(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Ensures resources/app exists; unpacked from app.asar when utils.js is missing.</summary>
    public static bool EnsureAppUnpacked(string antigravityExePath, out string appDir, out string message)
    {
        message = string.Empty;
        var exeDir = Path.GetDirectoryName(antigravityExePath);
        if (string.IsNullOrEmpty(exeDir))
        {
            appDir = string.Empty;
            message = "无效的可执行文件路径。";
            return false;
        }

        var resourcesDir = Path.Combine(exeDir, "resources");
        appDir = Path.Combine(resourcesDir, "app");
        var utilsJs = Path.Combine(appDir, "dist", "utils.js");

        if (File.Exists(utilsJs))
        {
            return true;
        }

        var asar = Path.Combine(resourcesDir, "app.asar");
        var asarStock = Path.Combine(resourcesDir, "app.asar.stock");

        string? sourceAsar = null;
        if (File.Exists(asar)) sourceAsar = asar;
        else if (File.Exists(asarStock)) sourceAsar = asarStock;

        if (string.IsNullOrEmpty(sourceAsar))
        {
            message = "未找到 resources/app.asar 归档文件，无法自动解包。";
            return false;
        }

        if (!ExtractAsar(sourceAsar, appDir, out var extractErr))
        {
            message = $"自动解包核心源码失败: {extractErr}";
            return false;
        }

        return true;
    }

    /// <summary>Normalizes the material name to mica / tabbed / acrylic / none; anything else becomes mica.</summary>
    private static string NormalizeMaterialName(string? material) =>
        material?.Trim().ToLowerInvariant() switch
        {
            "tabbed" => "tabbed",
            "acrylic" => "acrylic",
            "none" => "none",
            _ => "mica"
        };

    public static bool ApplyMicaPatch(string antigravityExePath, string material, double opacity, out string message)
    {
        message = string.Empty;
        material = NormalizeMaterialName(material);
        var safeOpacity = Math.Clamp(opacity, 0.00, 1.0).ToString("F2", CultureInfo.InvariantCulture);

        // Ensure resources/app is unpacked before patching.
        if (!EnsureAppUnpacked(antigravityExePath, out var appDir, out var unpackMsg))
        {
            message = unpackMsg;
            return false;
        }

        var resourcesDir = Path.Combine(Path.GetDirectoryName(antigravityExePath)!, "resources");
        var utilsJs = Path.Combine(appDir, "dist", "utils.js");
        var utilsStock = Path.Combine(appDir, "dist", "utils.js.stock");
        var ipcJs = Path.Combine(appDir, "dist", "ipcHandlers.js");
        var ipcStock = Path.Combine(appDir, "dist", "ipcHandlers.js.stock");
        var mainJs = Path.Combine(appDir, "dist", "main.js");
        var mainStock = Path.Combine(appDir, "dist", "main.js.stock");
        var preloadJs = Path.Combine(appDir, "dist", "preload.js");
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var appAsarStock = Path.Combine(resourcesDir, "app.asar.stock");

        if (!File.Exists(utilsJs))
        {
            message = "未找到 dist/utils.js 源码文件，补丁注入受阻。";
            return false;
        }

        // Drop a legacy preload.js injection by truncating at the bridge marker.
        if (File.Exists(preloadJs))
        {
            try
            {
                var preloadContent = File.ReadAllText(preloadJs);
                var markerIdx = preloadContent.IndexOf(BridgeMarker, StringComparison.Ordinal);
                if (markerIdx != -1)
                {
                    preloadContent = preloadContent.Substring(0, markerIdx).TrimEnd() + Environment.NewLine;
                    File.WriteAllText(preloadJs, preloadContent, Encoding.UTF8);
                }
             }
            catch { }
        }

        // Patch ipcHandlers.js: title-bar controls get a fully transparent overlay color.
        if (File.Exists(ipcJs))
        {
            try
            {
                if (!File.Exists(ipcStock))
                {
                    File.Copy(ipcJs, ipcStock, true);
                }

                var ipcContent = File.ReadAllText(ipcStock, Encoding.UTF8);
                if (ipcContent.Contains("color: options.color,"))
                {
                    ipcContent = ipcContent.Replace("color: options.color,", "color: '#00000000', /* GRAVITYLESS_MICA_OVERLAY */");
                    File.WriteAllText(ipcJs, ipcContent, Encoding.UTF8);
                }
            }
            catch { }
        }

        // Patch main.js: replace the protocol self-registration call with a no-op.
        if (File.Exists(mainJs))
        {
            try
            {
                // .stock must stay pristine, so it is only captured once.
                if (!File.Exists(mainStock))
                {
                    File.Copy(mainJs, mainStock, true);
                }

                var mainContent = File.ReadAllText(mainStock, Encoding.UTF8);
                if (!mainContent.Contains(ProtocolOwnerMarker, StringComparison.Ordinal) &&
                    mainContent.Contains(ProtocolCallTarget, StringComparison.Ordinal))
                {
                    mainContent = mainContent.Replace(
                        ProtocolCallTarget,
                        $"{ProtocolOwnerMarker} void 0; // Gravityless owns antigravity://; host must not reclaim it");
                    File.WriteAllText(mainJs, mainContent, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        // Patch utils.js (main process core).
        try
        {
            var sourceAsar = File.Exists(appAsar) ? appAsar : (File.Exists(appAsarStock) ? appAsarStock : null);

            // A .stock that already contains the marker was captured after patching; rebuild it from the ASAR.
            bool stockCorrupted = File.Exists(utilsStock) &&
                File.ReadAllText(utilsStock, Encoding.UTF8).Contains(MicaMarker, StringComparison.Ordinal);

            if (!File.Exists(utilsStock) || stockCorrupted)
            {
                if (File.Exists(utilsJs) && !File.ReadAllText(utilsJs, Encoding.UTF8).Contains(MicaMarker, StringComparison.Ordinal))
                {
                    File.Copy(utilsJs, utilsStock, true);
                }
                else if (!string.IsNullOrEmpty(sourceAsar))
                {
                    // Re-extract a clean utils.js from the ASAR.
                    ExtractAsar(sourceAsar, appDir, out _);
                    if (File.Exists(utilsJs))
                    {
                        File.Copy(utilsJs, utilsStock, true);
                    }
                }
            }

            var utilsContent = File.ReadAllText(utilsStock, Encoding.UTF8);

            // A. Inject backgroundMaterial + transparent title bar into the BrowserWindow options.
            var targetPattern = @"titleBarOverlay:\s*isMacOS\(\)\s*\?\s*false\s*:\s*\{\s*color:\s*backgroundColor,\s*symbolColor:\s*foregroundColor,\s*height:\s*30,\s*\},?\s*backgroundColor,";
            var replacementPattern = $@"{MicaMarker}
        backgroundMaterial: (process.env.GRAVITYLESS_MICA_MATERIAL === 'mica' || process.env.GRAVITYLESS_MICA_MATERIAL === 'tabbed' || process.env.GRAVITYLESS_MICA_MATERIAL === 'acrylic' || process.env.GRAVITYLESS_MICA_MATERIAL === 'none') ? process.env.GRAVITYLESS_MICA_MATERIAL : '{material}',
        backgroundColor: '#00000000',
        // Transparent background means the window would show an empty frame until the
        // renderer paints. Stay hidden until the first frame is ready.
        show: false,
        titleBarOverlay: isMacOS()
            ? false
            : {{
                color: '#00000000',
                symbolColor: foregroundColor,
                height: 30,
            }},";

            var rgx = new Regex(targetPattern);
            if (rgx.IsMatch(utilsContent))
            {
                utilsContent = rgx.Replace(utilsContent, replacementPattern, 1);
            }
            else
            {
                var simpleTarget = "backgroundColor,";
                var simpleReplacement = $"{MicaMarker}\n        backgroundMaterial: (process.env.GRAVITYLESS_MICA_MATERIAL === 'mica' || process.env.GRAVITYLESS_MICA_MATERIAL === 'tabbed' || process.env.GRAVITYLESS_MICA_MATERIAL === 'acrylic' || process.env.GRAVITYLESS_MICA_MATERIAL === 'none') ? process.env.GRAVITYLESS_MICA_MATERIAL : '{material}',\n        backgroundColor: '#00000000',";
                if (utilsContent.Contains(simpleTarget))
                {
                    utilsContent = utilsContent.Replace(simpleTarget, simpleReplacement);
                }
            }

            // B. Append the post-creation window hook (DWM material re-apply + CSS injection).
            if (!utilsContent.Contains(MicaHookMarker))
            {
                var winCreatedPattern = @"webviewTag:\s*true,\s*\},?\s*\}\);";
                var winCreatedRgx = new Regex(winCreatedPattern);
                if (winCreatedRgx.IsMatch(utilsContent))
                {
                    var winHook = WinHookTemplate
                        .Replace("__MICA_HOOK_MARKER__", MicaHookMarker)
                        .Replace("__INITIAL_MAT__", material)
                        .Replace("__SAFE_OPACITY__", safeOpacity);

                    utilsContent = winCreatedRgx.Replace(utilsContent, winHook, 1);
                }
            }

            // C. Append the main-process daemon sync block (uses native Node, no sandbox limits).
            if (!utilsContent.Contains(MicaSyncMarker))
            {
                var syncHook = SyncHookTemplate
                    .Replace("__MICA_SYNC_MARKER__", MicaSyncMarker)
                    .Replace("__MICA_PATCH_STAMP__", MicaPatchStamp);
                utilsContent += syncHook;
            }

            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(utilsJs, utilsContent, utf8NoBom);

            // Take over the ASAR: app.asar becomes app.asar.stock so resources/app is loaded.
            bool asarLocked = false;
            if (File.Exists(appAsar))
            {
                try
                {
                    if (File.Exists(appAsarStock))
                    {
                        File.Delete(appAsarStock);
                    }
                    File.Move(appAsar, appAsarStock);
                }
                catch (IOException)
                {
                    asarLocked = true;
                }
            }

            if (asarLocked)
            {
                message = $"原生 {material.ToUpperInvariant()} 材质补丁已注入完成！由于当前有正在运行的 Antigravity 实例占用了 app.asar，请在【实例管理】中重启该实例，即可直接享受原生 Mica 特性！";
            }
            else
            {
                message = $"原生 {material.ToUpperInvariant()} 材质与透明度 ({safeOpacity}) 补丁注入成功！已切换为纯净主进程调度，完全杜绝 Preload 越界错误。";
            }
            return true;
        }
        catch (Exception ex)
        {
            message = $"注入补丁异常: {ex.Message}";
            return false;
        }
    }

    public static bool ApplyMicaPatch(string antigravityExePath, out string message)
    {
        return ApplyMicaPatch(antigravityExePath, "mica", 0.72, out message);
    }

    /// <summary>True when both the patch version stamp and the protocol-owner marker are present.</summary>
    public static bool IsMicaPatchCurrent(string antigravityExePath)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(antigravityExePath);
            if (string.IsNullOrEmpty(exeDir)) return false;

            var utilsJs = Path.Combine(exeDir, "resources", "app", "dist", "utils.js");
            if (!File.Exists(utilsJs)) return false;

            if (!File.ReadAllText(utilsJs, Encoding.UTF8).Contains(MicaPatchStamp, StringComparison.Ordinal))
            {
                return false;
            }

            // main.js is patched in the same pass; a missing marker means main.js still needs it too.
            var mainJs = Path.Combine(exeDir, "resources", "app", "dist", "main.js");
            if (!File.Exists(mainJs)) return false;

            return File.ReadAllText(mainJs, Encoding.UTF8).Contains(ProtocolOwnerMarker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores the .stock files and moves app.asar back.</summary>
    public static bool RevertMicaPatch(string antigravityExePath, out string message)
    {
        message = string.Empty;
        var exeDir = Path.GetDirectoryName(antigravityExePath);
        if (string.IsNullOrEmpty(exeDir)) return false;

        var resourcesDir = Path.Combine(exeDir, "resources");
        var appDir = Path.Combine(resourcesDir, "app");
        var utilsJs = Path.Combine(appDir, "dist", "utils.js");
        var utilsStock = Path.Combine(appDir, "dist", "utils.js.stock");
        var ipcJs = Path.Combine(appDir, "dist", "ipcHandlers.js");
        var ipcStock = Path.Combine(appDir, "dist", "ipcHandlers.js.stock");
        var mainJs = Path.Combine(appDir, "dist", "main.js");
        var mainStock = Path.Combine(appDir, "dist", "main.js.stock");
        var preloadJs = Path.Combine(appDir, "dist", "preload.js");
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var appAsarStock = Path.Combine(resourcesDir, "app.asar.stock");

        try
        {
            // Restore app.asar.
            if (File.Exists(appAsarStock) && !File.Exists(appAsar))
            {
                try { File.Move(appAsarStock, appAsar); } catch (Exception ex) { DiagLog.Swallowed("patch.asar-restore", ex); }
            }

            if (File.Exists(utilsStock))
            {
                try { File.Copy(utilsStock, utilsJs, true); } catch (Exception ex) { DiagLog.Swallowed("patch.utils-restore", ex); }
            }

            if (File.Exists(ipcStock))
            {
                try { File.Copy(ipcStock, ipcJs, true); } catch (Exception ex) { DiagLog.Swallowed("patch.ipc-restore", ex); }
            }

            if (File.Exists(mainStock))
            {
                try { File.Copy(mainStock, mainJs, true); } catch (Exception ex) { DiagLog.Swallowed("patch.main-restore", ex); }
            }

            if (File.Exists(preloadJs))
            {
                var preloadContent = File.ReadAllText(preloadJs);
                var markerIdx = preloadContent.IndexOf(BridgeMarker, StringComparison.Ordinal);
                if (markerIdx != -1)
                {
                    preloadContent = preloadContent.Substring(0, markerIdx).TrimEnd() + Environment.NewLine;
                    File.WriteAllText(preloadJs, preloadContent, Encoding.UTF8);
                }
            }

            message = "已成功移除 Mica 与守护进程注入，恢复官方原生默认状态。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"还原失败: {ex.Message}";
            return false;
        }
    }

    private static readonly string WinHookTemplate = """
webviewTag: true,
        },
    });
    __MICA_HOOK_MARKER__
    // The window is created hidden so the transparent frame never flashes. Show it as soon
    // as the first frame is ready; this must run regardless of backdrop support.
    win.once('ready-to-show', () => {
        try { win.show(); } catch (_) {}
    });
    if (typeof win.setBackgroundMaterial === 'function') {
        const _envMat = process.env.GRAVITYLESS_MICA_MATERIAL;
        const _initialMat = (_envMat === 'tabbed' || _envMat === 'mica' || _envMat === 'acrylic' || _envMat === 'none') ? _envMat : '__INITIAL_MAT__';
        win.setBackgroundMaterial(_initialMat);
        win.once('ready-to-show', () => {
            try { win.setBackgroundMaterial(_initialMat); } catch (_) {}
        });
    }
    // Material CSS is built only by global.__gravitylessMica at the end of utils.js.
    const _initA = Number(
        (process.env.GRAVITYLESS_MICA_OPACITY !== undefined && String(process.env.GRAVITYLESS_MICA_OPACITY).trim() !== '')
            ? process.env.GRAVITYLESS_MICA_OPACITY
            : '__SAFE_OPACITY__'
    );
    win.webContents.on('dom-ready', () => {
        try {
            if (global.__gravitylessMica) global.__gravitylessMica.attach(win, _initA);
        } catch (_) {}

        try {
            const _loc = global.__gravitylessLocale || process.env.GRAVITYLESS_LOCALE || 'zh-CN';
            const fs = require('fs');
            const path = require('path');
            const _locDir = process.env.GRAVITYLESS_LOCALES_DIR;
            let _dict = {};
            if (_locDir) {
                const _fp = path.join(_locDir, _loc + '.json');
                if (fs.existsSync(_fp)) {
                    try {
                        const _parsed = JSON.parse(fs.readFileSync(_fp, 'utf8'));
                        delete _parsed._meta;
                        delete _parsed['@metadata'];
                        _dict = _parsed;
                    } catch (_) {}
                }
            }
            const _ds = JSON.stringify(_dict);
            const _i18nCode = `
                (function() {
                    window.__gravityless_lang = ${JSON.stringify(_loc)};
                    window.__gravityless_i18n_dict = ${_ds};

                    const WHITELIST = [
                        'header', 'nav', 'aside', '[role="navigation"]',
                        '[data-testid="title-menu-bar"]',
                        '[data-testid="pane-titlebar-toolbar"]',
                        '[data-testid="conversation-list-sidebar"]',
                        '[data-testid="overview-panel"]',
                        '[data-testid="sidebar"]',
                        '[role="dialog"]', '[role="menu"]', '[role="menuitem"]',
                        '[role="menubar"]', '[role="tablist"]', '[role="tab"]', '[role="listbox"]',
                        '[role="option"]', '[role="combobox"]', '[role="tooltip"]', '[data-tooltip-id]',
                        '[data-side]', '[data-popup-open]', '[aria-modal="true"]',
                        '.settings-modal', '.settings-modal-container', '.settings-modal-backdrop',
                        '[aria-label="Settings"]', '[data-testid="bottom-sheet"]',
                        '[data-testid="input-footer"]',
                        '[data-testid="permissions-selector-trigger"]',
                        '[data-testid="model-selector-trigger"]',
                        '[data-testid="chat-input-actions"]',
                        '[data-testid="chat-input-container"]',
                        '[data-testid="attach-context-menu"]',
                        '[data-testid="plus-menu-button"]',
                        '[data-testid="context-picker"]',
                        '[data-testid="permissions-project-options"]',
                        '[cmdk-root]', '[cmdk-item]', '[cmdk-group]', '[cmdk-list]', '[cmdk-input]', '.command-palette',
                        '[data-radix-popper-content-wrapper]', '[data-radix-dropdown-menu-content]',
                        '[data-radix-menu-content]', '[data-radix-context-menu-content]',
                        '[data-floating-ui-portal]', '[data-floating-ui]', '[data-radix-select-content]',
                        '[role="toolbar"]', 'button', '[role="button"]', '.tab-bar', '[data-testid*="toolbar"]', '[data-testid*="tab"]'
                    ].join(', ');

                    const EXCLUDED = [
                        'pre', 'code', 'samp', 'kbd', 'textarea',
                        'input', '[contenteditable="true"]',
                        '.monaco-editor', '.monaco-diff-editor',
                        '[data-code-block]', '.syntax-highlighter',
                        '.xterm', '.terminal', '.console-output',
                        '[data-testid="conversation-view"]',
                        '[data-testid="user-input-step"]',
                        '[data-testid="user-message-bubble"]',
                        '[data-testid="planner-response-text"]',
                        '[data-testid="turn-cards-container"]',
                        '[data-testid="file-preview"]',
                        '[data-testid="pdf-viewport"]',
                        '.file-content', '.diff-line',
                        '.agent-thought-content', '.chat-message-content', '.markdown-body',
                        '[data-testid="conversation-row-sidebar"]',
                        '[data-testid="project-header"]'
                    ].join(', ');

                    function isAllowed(el) {
                        if (!el || !el.closest) return false;
                        // Interactive controls (buttons, menus, tooltips, dialogs, command palette) are always translated.
                        if (el.closest('button, [role="button"], [role="menuitem"], [role="menu"], [role="tooltip"], [data-tooltip-id], [role="dialog"], [aria-modal="true"], [cmdk-root], [cmdk-item], [data-radix-popper-content-wrapper]')) {
                            return true;
                        }
                        if (el.closest(EXCLUDED)) return false;
                        if (!el.closest(WHITELIST)) return false;
                        return true;
                    }

                    function procText(node) {
                        if (!node || node.nodeType !== Node.TEXT_NODE) return;
                        if (!node.parentElement || !isAllowed(node.parentElement)) return;
                        const currentVal = node.nodeValue;
                        // Node reused by the framework: value differs from what we wrote, so refresh the cache.
                        if (node.__gravityless_written !== undefined && currentVal !== node.__gravityless_written) {
                            node.__gravityless_orig = currentVal;
                        }
                        if (node.__gravityless_orig === undefined) {
                            node.__gravityless_orig = currentVal;
                        }
                        const orig = node.__gravityless_orig;
                        const trimmed = orig.trim();
                        if (!trimmed) return;
                        const d = window.__gravityless_i18n_dict;
                        const isEn = window.__gravityless_lang === 'en-US';
                        const target = (d && d[trimmed]) ? d[trimmed] : (isEn ? trimmed : null);
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
                        if (!isAllowed(el)) return;
                        const d = window.__gravityless_i18n_dict;
                        const isEn = window.__gravityless_lang === 'en-US';
                        if (!d && !isEn) return;

                        const attrs = ['placeholder', 'title', 'aria-label', 'aria-description', 'data-tooltip-content', 'data-tooltip', 'data-tip', 'data-description'];
                        for (let i = 0; i < attrs.length; i++) {
                            const attr = attrs[i];
                            const val = el.getAttribute ? el.getAttribute(attr) : el[attr];
                            if (val && typeof val === 'string') {
                                const origKey = '__gravityless_orig_' + attr;
                                const writtenKey = '__gravityless_written_' + attr;
                                // Element reused: attribute value differs from what we wrote, so refresh the cache.
                                if (el[writtenKey] !== undefined && val !== el[writtenKey]) {
                                    el[origKey] = val;
                                }
                                if (el[origKey] === undefined) el[origKey] = val;
                                const origVal = el[origKey];
                                const trimmed = origVal.trim();
                                const target = (d && d[trimmed]) ? d[trimmed] : (isEn ? trimmed : null);
                                if (target) {
                                    const trans = origVal.replace(trimmed, target);
                                    if (el.getAttribute(attr) !== trans) {
                                        el.setAttribute(attr, trans);
                                        if (attr in el) {
                                            try { el[attr] = trans; } catch (_) {}
                                        }
                                    }
                                    el[writtenKey] = trans;
                                }
                            }
                        }
                    }

                    function procSubtree(root) {
                        if (!root) return;
                        if (root.nodeType === Node.ELEMENT_NODE && root.closest && root.closest(EXCLUDED)) return;
                        if (root.nodeType === Node.TEXT_NODE) {
                            procText(root);
                            return;
                        }
                        if (root.nodeType === Node.ELEMENT_NODE) {
                            procAttrs(root);
                            const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                            let n;
                            while (n = walker.nextNode()) {
                                procText(n);
                            }
                            const elWalker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                            let el;
                            while (el = elWalker.nextNode()) {
                                procAttrs(el);
                            }
                        }
                    }

                    window.__gravityless_set_lang = function(lang, dict) {
                        window.__gravityless_lang = lang;
                        window.__gravityless_i18n_dict = dict || {};
                        procSubtree(document.body || document.documentElement);
                    };

                    if (!window.__gravityless_i18n_active) {
                        window.__gravityless_i18n_active = true;
                        const obs = new MutationObserver((muts) => {
                            if (!window.__gravityless_i18n_dict || Object.keys(window.__gravityless_i18n_dict).length === 0) return;
                            for (let i = 0; i < muts.length; i++) {
                                const m = muts[i];
                                if (m.type === 'childList') {
                                    for (let j = 0; j < m.addedNodes.length; j++) {
                                        procSubtree(m.addedNodes[j]);
                                    }
                                } else if (m.type === 'characterData') {
                                    procText(m.target);
                                }
                            }
                        });

                        obs.observe(document.body || document.documentElement, {
                            childList: true,
                            subtree: true,
                            characterData: true
                        });
                    }

                    procSubtree(document.body || document.documentElement);
                })();
            `;
            win.webContents.executeJavaScript(_i18nCode).catch(() => {});
        } catch (_) {}


    });
""";

    private static readonly string SyncHookTemplate = """

__MICA_SYNC_MARKER__
__MICA_PATCH_STAMP__
(function() {
    if (global.__gravitylessMica) return;

    const clamp = function (v, lo, hi) { return Math.max(lo, Math.min(hi, v)); };

    // -----------------------------------------------------------------
    // Material layering (single definition point for the whole app).
    // Theme tokens are opaque hex and are shared with popups (--background)
    // and the title-bar dropdown (--sidebar), so only the shell and sidebar
    // are made transparent; flyouts restore opacity via structural selectors.
    // -----------------------------------------------------------------
    // ── Mica baseline ───────────────────────────────────────────────────
    function buildCss(rawAlpha, rawPopupAlpha, fallback, isLight, newUx, accentHex, accentInteraction,
                      noiseAlpha, noiseFreq, noiseTint, noiseBase) {
        const surface = isLight ? '243, 243, 243' : '20, 20, 20';
        // Flyout tint alpha is driven by the popup-opacity slider (0.25 floor for readability).
        // The closer to 1, the less the backdrop-filter blur shows.
        const pa = Math.max(0.25, Math.min(1, Number(rawPopupAlpha) || 0.6));
        const popupTint = isLight ? '250, 250, 250' : '25, 25, 25';
        const dialogTint = isLight ? '250, 250, 250' : '24, 24, 24';
        const popupBg = 'rgba(' + popupTint + ', ' + pa.toFixed(2) + ')';
        const dialogBg = 'rgba(' + dialogTint + ', ' + Math.min(1, pa + 0.15).toFixed(2) + ')';


        // Composer is the only surface that shows Mica: only its two opaque layers
        // (bg-card-border ring, bg-card fill) are overridden; tokens stay untouched.
        const composerRing = isLight ? '243, 243, 243' : '20, 20, 20';
        const composerFill = isLight ? '250, 250, 250' : '24, 24, 24';
        const ringA = (isLight ? 0.55 : 0.30).toFixed(2);
        const fillA = (isLight ? 0.45 : 0.22).toFixed(2);

        // ── Accent: hex -> desaturated -> rgba interaction states ──
        // Toggle off falls back to the stock Google Blue.
        const STOCK_BLUE = '#4285f4';
        const useSystemAccent = !(accentInteraction === false || accentInteraction === 'false');
        const accentSrc = useSystemAccent ? (accentHex || STOCK_BLUE) : STOCK_BLUE;
        const accentRaw = String(accentSrc).trim().replace('#', '');
        let ar = 66, ag = 133, ab = 244;   // default #4285f4
        if (accentRaw.length === 6 || accentRaw.length === 8) {
            const hex = accentRaw.length === 8 ? accentRaw.slice(2) : accentRaw;   // accept AARRGGBB
            const r = parseInt(hex.slice(0, 2), 16);
            const g = parseInt(hex.slice(2, 4), 16);
            const b = parseInt(hex.slice(4, 6), 16);
            if (!isNaN(r) && !isNaN(g) && !isNaN(b)) { ar = r; ag = g; ab = b; }
        }
        // Desaturate toward the color's own luminance (saturation x 0.45).
        const lum = 0.299 * ar + 0.587 * ag + 0.114 * ab;
        const desat = (v) => Math.round(lum + (v - lum) * 0.45);
        const cr = desat(ar), cg = desat(ag), cb = desat(ab);
        const accentSoft = 'rgba(' + cr + ', ' + cg + ', ' + cb + ', 0.08)';   // row hover
        const accentStrong = 'rgba(' + cr + ', ' + cg + ', ' + cb + ', 0.14)'; // row selected
        const accentStroke = 'rgba(' + cr + ', ' + cg + ', ' + cb + ', 0.22)'; // selection stroke
        const rowHover = accentSoft;
        const rowActive = accentStrong;
        const rowActiveStroke = accentStroke;

        // ── Noise texture: two SVG noise layers tinted by the accent ──
        // Combined opacity = 1 - (1 - baseAlpha) * (1 - noiseAlpha).
        const nA = Math.max(0, Math.min(1, Number(noiseAlpha) || 0));
        const nF = Math.max(0.5, Math.min(2.0, Number(noiseFreq) || 0.85));
        const nT = Math.max(0, Math.min(1, Number(noiseTint) || 0));
        const nB = Math.max(0, Math.min(1, Number(noiseBase) || 0));

        // Blend the accent with black (dark theme) or white (light theme) by ratio.
        const mixWith = isLight ? 255 : 0;
        const mixCh = (c) => Math.round(mixWith + (c - mixWith) * nT);
        const tintR = mixCh(cr) / 255, tintG = mixCh(cg) / 255, tintB = mixCh(cb) / 255;

        // Coarse noise: low frequency, high alpha for hue; fine noise: high frequency, low alpha for edges.
        const coarseFreq = (nF * 0.82).toFixed(3);
        const fineFreq = (nF * 1.55).toFixed(3);
        const coarseA = (nA * 0.62).toFixed(3);
        const fineA = (nA * 0.28).toFixed(3);
        const noiseSvg = (freq, a) =>
            'url("data:image/svg+xml;utf8,<svg xmlns=\'http://www.w3.org/2000/svg\' width=\'140\' height=\'140\'>'
            + '<filter id=\'n\'><feTurbulence type=\'fractalNoise\' baseFrequency=\'' + freq + '\' numOctaves=\'3\' stitchTiles=\'stitch\'/>'
            + '<feColorMatrix type=\'matrix\' values=\'0 0 0 0 ' + tintR.toFixed(3) + '  0 0 0 0 ' + tintG.toFixed(3)
            + '  0 0 0 0 ' + tintB.toFixed(3) + '  0 0 0 ' + a + ' 0\'/></filter>'
            + '<rect width=\'140\' height=\'140\' filter=\'url(%23n)\'/></svg>")';

        // Base tint: accent blended with black/white; alpha comes from noiseBase.
        const baseAlpha = nB.toFixed(3);

        const blurCss = nA <= 0 && nB <= 0 ? '' : `
/* =====================================================================
   Flyout material: two noise layers (in-app acrylic substitute).
   Coarse noise adds hue, fine noise breaks up edges; baseAlpha controls see-through.
   ===================================================================== */
[role="menu"],
[role="dialog"],
[role="alertdialog"],
[role="listbox"]:not([aria-label="Typeahead menu"]),
[aria-modal="true"],
[data-select-popup],
[data-testid="bottom-sheet"],
.settings-modal-container,
.absolute.bg-sidebar,
.absolute.bg-background,
.absolute.bg-card {
    background-color: rgba(${mixCh(cr)}, ${mixCh(cg)}, ${mixCh(cb)}, ${baseAlpha}) !important;
    background-image: ${noiseSvg(coarseFreq, coarseA)}, ${noiseSvg(fineFreq, fineA)} !important;
    background-blend-mode: normal, overlay;
    -webkit-backdrop-filter: none;
    backdrop-filter: none;
}

/* Inline code / code blocks: transparent, hairline stroke, no noise */
pre,
.md-code-block,
[class*="shiki"],
[data-testid*="code-block"] {
    background-color: transparent !important;
    background-image: none !important;
    box-shadow: inset 0 0 0 1px var(--gl-stroke-static) !important;
}

/* Inline code keeps a faint tint to stay distinguishable from body text */
code {
    background-color: ${isLight ? 'rgba(0, 0, 0, 0.04)' : 'rgba(255, 255, 255, 0.05)'} !important;
    background-image: none !important;
}

/* Sticky user message: same noise, clipped so it does not overflow into the title bar */
.sticky.top-0 {
    overflow: hidden !important;
    isolation: isolate;
}
.md-sticky-message-bleed,
.sticky > div > div > [data-testid="user-input-step"] > div > .bg-card-border {
    background-color: rgba(${mixCh(cr)}, ${mixCh(cg)}, ${mixCh(cb)}, ${Math.min(1, nB + 0.22).toFixed(3)}) !important;
    background-image: ${noiseSvg(coarseFreq, (nA * 0.55).toFixed(3))} !important;
    background-blend-mode: normal;
    border-radius: 10px;
    overflow: hidden;
}
`;

        // ── WinUI 3 layering: static surfaces use strokes, flyouts add ThemeShadow ──
        // Static surface stroke (SurfaceStrokeColorDefaultBrush equivalent).
        const strokeStatic = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.07)';
        // Flyout stroke (SurfaceStrokeColorFlyoutBrush equivalent).
        const strokeFlyout = isLight ? 'rgba(0, 0, 0, 0.10)' : 'rgba(255, 255, 255, 0.09)';
        // ThemeShadow tiers: 16 tooltip / 32 flyout / 128 dialog.
        const shadowFlyout = isLight
            ? '0 8px 16px -4px rgba(15,23,42,0.16), 0 2px 4px -2px rgba(15,23,42,0.10)'
            : '0 8px 16px -4px rgba(0,0,0,0.50), 0 2px 4px -2px rgba(0,0,0,0.35)';
        const shadowTooltip = isLight
            ? '0 4px 10px -2px rgba(15,23,42,0.14), 0 2px 4px -2px rgba(15,23,42,0.08)'
            : '0 4px 10px -2px rgba(0,0,0,0.45), 0 2px 4px -2px rgba(0,0,0,0.30)';
        const shadowDialog = isLight
            ? '0 24px 48px -12px rgba(15,23,42,0.22), 0 6px 12px -4px rgba(15,23,42,0.12)'
            : '0 24px 48px -12px rgba(0,0,0,0.62), 0 6px 12px -4px rgba(0,0,0,0.42)';

        // The newux skin ships its own composer glass recipe, so skip ours.
        const composerCss = newUx ? '' : `
/* =====================================================================
   Composer: substrate that shows Mica.
   Only the two composer layers (stroke ring + fill) are overridden;
   bubbles / turn cards / cards / buttons keep --card untouched.
   ===================================================================== */
.surface-composer {
    background-color: rgba(${composerRing}, ${ringA}) !important;
}

.surface-composer > .surface-composer-card {
    background-color: rgba(${composerFill}, ${fillA}) !important;
}
`;

        return `
/* Gravityless: Mica baseline */
html,
body,
#root {
    background: transparent !important;
    background-color: transparent !important;
}

:root {
    /* Shell transparency: Mica shows through here */
    --background: transparent !important;

    /* Sidebar / title bar: fully delegated to the material. --sidebar is shared with
       the title-bar dropdown, whose opacity is restored by the structural rules below. */
    --sidebar: transparent !important;
    /* Hover/selected use a neutral lift instead of a dark overlay; accent states come from --gl-row-*. */
    --sidebar-secondary: ${isLight ? 'rgba(0, 0, 0, 0.04)' : 'rgba(255, 255, 255, 0.06)'} !important;
    --sidebar-muted: transparent !important;

    /* Flyouts paint no background of their own */
    --gl-popup-bg: transparent;
    --gl-dialog-bg: transparent;

    /* Accent interaction states */
    --gl-row-hover: ${rowHover};
    --gl-row-active: ${rowActive};
    --gl-row-active-stroke: ${rowActiveStroke};

    /* WinUI 3 layering: static surface mask (brightness delta) + stroke */
    --gl-surface-mask: ${isLight ? 'rgba(0, 0, 0, 0.02)' : 'rgba(255, 255, 255, 0.03)'};
    --gl-surface-mask-hover: ${isLight ? 'rgba(0, 0, 0, 0.05)' : 'rgba(255, 255, 255, 0.06)'};
    --gl-stroke-static: ${strokeStatic};
    --gl-stroke-flyout: ${strokeFlyout};
    --gl-shadow-flyout: ${shadowFlyout};
    --gl-shadow-tooltip: ${shadowTooltip};
    --gl-shadow-dialog: ${shadowDialog};
}

${composerCss}

${blurCss}
/* =====================================================================
   Flyouts / dialogs: no self-painted background (tint / noise / highlight / shadow removed).
   Only backdrop-filter blur remains; readability relies on the material behind.
   ===================================================================== */

/* =====================================================================
   WinUI 3 layering: clear existing in-page box-shadows.
   In-page surfaces express depth with strokes, not shadows.
   ===================================================================== */
#root *,
#root *::before,
#root *::after {
    box-shadow: none !important;
}

/* =====================================================================
   Static surfaces: 1px inset ring (no layout cost).
   The #root prefix is required so these beat the clear rule above.
   ===================================================================== */
#root .bg-card,
#root .bg-card-border,
#root [data-testid="user-message-bubble"] {
    background-color: var(--gl-surface-mask) !important;
    box-shadow: inset 0 0 0 1px var(--gl-stroke-static) !important;
}

#root .surface-composer,
#root .surface-composer-card {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-static) !important;
}

/* Sidebar conversation rows: hover/selected use a brightness delta + faint accent,
   no dark blocks. --row-bg is set inline by the app, so !important is required. */
#root .agy-sidebar-row {
    --row-bg: transparent !important;
    background-color: transparent !important;
}
#root .agy-sidebar-row:hover {
    --row-bg: var(--gl-row-hover) !important;
    background-color: var(--gl-row-hover) !important;
}
#root .agy-sidebar-row[data-selected="true"] {
    --row-bg: var(--gl-row-active) !important;
    background-color: var(--gl-row-active) !important;
    box-shadow: inset 0 0 0 1px var(--gl-row-active-stroke) !important;
}

/* 1) Structural markers: stroke + ThemeShadow tier 32 (flyout / menu) */
[role="menu"],
[role="alertdialog"],
[role="listbox"],
[data-select-popup],
[data-hover-card-popup],
[data-slot$="-content"],
.absolute.bg-sidebar,
.absolute.bg-background,
.absolute.bg-card {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-flyout), var(--gl-shadow-flyout) !important;
}

/* tooltip: ThemeShadow tier 16 */
[role="tooltip"],
.react-tooltip {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-flyout), var(--gl-shadow-tooltip) !important;
}

/* dialog: ThemeShadow tier 128 */
[role="dialog"],
[aria-modal="true"],
[data-testid="bottom-sheet"],
.settings-modal-container {
    box-shadow: inset 0 0 0 1px var(--gl-stroke-flyout), var(--gl-shadow-dialog) !important;
}

`;
    }

    let currentAlpha = 0.72;
    let currentLight = false;
    let currentPopupAlpha = 0.60;
    let currentNewUx = '';
    let currentMaterial = 'mica';
    let currentAccent = '#4285f4';
    let currentAccentInteraction = true;
    let currentNoiseAlpha = 0.55;
    let currentNoiseFreq = 0.85;
    let currentNoiseTint = 0.28;
    let currentNoiseBase = 0.35;

    // New UX skin: stamp data-agy-theme / data-newux-theme on body (empty value removes them).
    function applyNewUx(wc) {
        try {
            const setCode = currentNewUx
                ? 'var b=document.body;if(b){b.setAttribute("data-agy-theme","1");b.setAttribute("data-newux-theme",' + JSON.stringify(currentNewUx) + ');}'
                : 'var b=document.body;if(b){b.removeAttribute("data-agy-theme");b.removeAttribute("data-newux-theme");}';
            wc.executeJavaScript(setCode).catch(function () {});
        } catch (_) {}
    }

    function attach(target, rawAlpha, rawPopupAlpha) {
        if (!target) return;
        const wc = target.webContents ? target.webContents : target;
        if (!wc || typeof wc.insertCSS !== 'function') return;
        if (typeof target.isDestroyed === 'function' && target.isDestroyed()) return;

        const alpha = (rawAlpha === undefined || rawAlpha === null) ? currentAlpha : rawAlpha;
        const popupAlpha = (rawPopupAlpha === undefined || rawPopupAlpha === null) ? currentPopupAlpha : rawPopupAlpha;
        const css = buildCss(alpha, popupAlpha, currentAlpha, currentLight, currentNewUx,
                             currentAccent, currentAccentInteraction,
                             currentNoiseAlpha, currentNoiseFreq, currentNoiseTint, currentNoiseBase);

        try {
            const prev = wc.__gravitylessCssKey;
            if (prev) Promise.resolve(wc.removeInsertedCSS(prev)).catch(function () {});
            wc.__gravitylessCssKey = null;
        } catch (_) {}

        // Material disabled: inject nothing so Antigravity keeps its stock look.
        if (currentMaterial === 'none') {
            applyNewUx(wc);
            return;
        }

        try {
            const p = wc.insertCSS(css, { cssOrigin: 'user' });
            if (p && typeof p.then === 'function') {
                p.then(function (key) { wc.__gravitylessCssKey = key; }).catch(function () {});
            } else {
                wc.__gravitylessCssKey = p;
            }
        } catch (_) {}

        applyNewUx(wc);
    }

    global.__gravitylessMica = {
        buildCss: buildCss,
        attach: attach,
        getAlpha: function () { return currentAlpha; },
        setAlpha: function (v) { currentAlpha = clamp(Number(v) || 0, 0, 1); }
    };

    // Apply the same material to every new window, not just the main one.
    try {
        electron_1.app.on('web-contents-created', function (_evt, contents) {
            try {
                contents.on('dom-ready', function () { attach(contents); });
            } catch (_) {}
        });
    } catch (_) {}

    const daemonPort = process.env.GRAVITYLESS_DAEMON_PORT;
    if (!daemonPort) return;

    const http = require('http');

    function applyMaterialToWindows(material) {
        if (!material) return;
        const revertAll = (material === 'none');
        // Material 'none' reverts to an opaque window background (otherwise the title-bar overlay leaks the desktop).
        const solidBg = currentLight ? '#ffffff' : '#101010';
        for (const win of electron_1.BrowserWindow.getAllWindows()) {
            try {
                if (win.isDestroyed()) continue;
                if (typeof win.setBackgroundMaterial === 'function') {
                    win.setBackgroundMaterial(material);
                }
                if (typeof win.setBackgroundColor === 'function') {
                    win.setBackgroundColor(revertAll ? solidBg : '#00000000');
                }
            } catch (_) {}
        }
    }

    function syncTheme() {
        http.get('http://127.0.0.1:' + daemonPort + '/api/v1/theme.json?t=' + Date.now(), function (res) {
            let data = '';
            res.on('data', function (c) { data += c; });
            res.on('end', function () {
                let theme = null;
                try { theme = JSON.parse(data); } catch (_) { return; }
                if (!theme) return;
                if (theme.opacity !== undefined && theme.opacity !== null) {
                    currentAlpha = clamp(Number(theme.opacity), 0, 1);
                }
                if (theme.popupOpacity !== undefined && theme.popupOpacity !== null) {
                    currentPopupAlpha = clamp(Number(theme.popupOpacity), 0, 1);
                }
                currentLight = (theme.theme === 'light');
                if (theme.material !== undefined && theme.material !== null) {
                    currentMaterial = String(theme.material).trim().toLowerCase();
                }
                if (theme.accent !== undefined && theme.accent !== null && theme.accent !== '') {
                    currentAccent = String(theme.accent);
                }
                if (theme.accentInteraction !== undefined && theme.accentInteraction !== null) {
                    currentAccentInteraction = String(theme.accentInteraction) !== 'false';
                }
                if (theme.noiseAlpha !== undefined && theme.noiseAlpha !== null) currentNoiseAlpha = Number(theme.noiseAlpha);
                if (theme.noiseFrequency !== undefined && theme.noiseFrequency !== null) currentNoiseFreq = Number(theme.noiseFrequency);
                if (theme.noiseTintRatio !== undefined && theme.noiseTintRatio !== null) currentNoiseTint = Number(theme.noiseTintRatio);
                if (theme.noiseBaseAlpha !== undefined && theme.noiseBaseAlpha !== null) currentNoiseBase = Number(theme.noiseBaseAlpha);
                if (theme.newUxTheme !== undefined && theme.newUxTheme !== null) {
                    currentNewUx = String(theme.newUxTheme).trim().toLowerCase();
                }
                applyMaterialToWindows(theme.material);
                for (const win of electron_1.BrowserWindow.getAllWindows()) attach(win);
            });
        }).on('error', function () {});
    }

    function readDict(lang) {
        const fs = require('fs');
        const path = require('path');
        const dir = process.env.GRAVITYLESS_LOCALES_DIR;
        if (!dir) return {};
        try {
            const fp = path.join(dir, lang + '.json');
            if (!fs.existsSync(fp)) return {};
            const parsed = JSON.parse(fs.readFileSync(fp, 'utf8'));
            delete parsed._meta;
            delete parsed['@metadata'];
            return parsed;
        } catch (_) {
            return {};
        }
    }

    function connectSSE() {
        const req = http.get('http://127.0.0.1:' + daemonPort + '/api/v1/events', function (res) {
            res.on('data', function (chunk) {
                const str = chunk.toString();
                if (str.indexOf('theme_reload') >= 0) syncTheme();
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
                    for (const win of electron_1.BrowserWindow.getAllWindows()) {
                        try {
                            if (!win.isDestroyed()) win.webContents.executeJavaScript(code).catch(function () {});
                        } catch (_) {}
                    }
                }
            });
            res.on('end', function () { setTimeout(connectSSE, 3000); });
        });
        req.on('error', function () { setTimeout(connectSSE, 3000); });
    }

    syncTheme();
    connectSSE();
})();
""";
}
