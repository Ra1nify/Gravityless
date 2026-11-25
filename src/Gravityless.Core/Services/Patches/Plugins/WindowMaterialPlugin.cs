namespace Gravityless.Core.Services.Patches.Plugins;

/// <summary>
/// Hooks BrowserWindow/BaseWindow/WebContentsView transparency, DWM Mica/Tabbed/Acrylic materials,
/// WCV curtainView suppression, and injects global surface + elevation CSS across renderer contents.
/// </summary>
public sealed class WindowMaterialPlugin : IPatchPlugin
{
    public string Id => "window-material";
    public string FileName => "03-window-material.js";
    public int Order => 30;

    public string BuildScript(PatchContext context)
    {
        return $$"""
{{context.PatchStamp}}
module.exports = {
    id: 'window-material',
    init: function(ctx) {
        const { electron, theme } = ctx;
        const clamp = ctx.utils.clamp;

        let windowMaterialDebounceTimer = null;

        function scheduleWindowMaterialSync(win, delayMs) {
            if (!win || win.isDestroyed()) return;
            const run = () => {
                try {
                    if (!win.isDestroyed()) applyMaterialToWindow(win, theme.material);
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

            if (typeof win.webContents === 'object' && win.webContents) {
                win.once('ready-to-show', () => {
                    try {
                        if (theme.material !== 'none') win.show();
                    } catch (_) {}
                    scheduleWindowMaterialSync(win, 0);
                });
            }

            if (theme.material !== 'none' && typeof win.setBackgroundMaterial === 'function') {
                try { win.setBackgroundMaterial(theme.material); } catch (_) {}
            }

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

        if (electron.BrowserWindow) {
            const OrigBW = electron.BrowserWindow;

            if (OrigBW.prototype && typeof OrigBW.prototype.setTitleBarOverlay === 'function') {
                const origSetOverlay = OrigBW.prototype.setTitleBarOverlay;
                OrigBW.prototype.setTitleBarOverlay = function(opts) {
                    if (theme.material !== 'none' && opts && typeof opts === 'object') {
                        opts = Object.assign({}, opts, { color: '#00000000' });
                    }
                    return origSetOverlay.call(this, opts);
                };
            }

            if (OrigBW.prototype && typeof OrigBW.prototype.setBackgroundColor === 'function') {
                const origSetBg = OrigBW.prototype.setBackgroundColor;
                OrigBW.prototype.setBackgroundColor = function(color) {
                    if (theme.material !== 'none') {
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
                if (theme.material !== 'none') {
                    options.backgroundMaterial = theme.material;
                    options.backgroundColor = '#00000000';
                    options.show = false;
                    if (options.titleBarOverlay && typeof options.titleBarOverlay === 'object') {
                        options.titleBarOverlay = Object.assign({}, options.titleBarOverlay, { color: '#00000000' });
                    }
                }
                const win = new OrigBW(options);
                if (theme.material !== 'none') {
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

            if (OrigWCV.prototype && typeof OrigWCV.prototype.setBackgroundColor === 'function') {
                const origWcvSetBg = OrigWCV.prototype.setBackgroundColor;
                OrigWCV.prototype.setBackgroundColor = function(color) {
                    if (theme.material !== 'none') {
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
                if (theme.material !== 'none') {
                    if (!options.webPreferences) {
                        options.webPreferences = {};
                    }
                    options.webPreferences.transparent = true;
                }
                const view = new OrigWCV(options);
                if (theme.material !== 'none') {
                    try {
                        view.setBackgroundColor('#00000000');
                    } catch (_) {}
                }
                if (view.webContents && typeof view.webContents.loadURL === 'function') {
                    const origLoadURL = view.webContents.loadURL;
                    view.webContents.__origLoadURL = origLoadURL;
                    view.webContents.loadURL = function(url, opts) {
                        if (theme.material !== 'none' && typeof url === 'string' && url.indexOf('data:text/html') === 0) {
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

            if (OrigBase.prototype && typeof OrigBase.prototype.setTitleBarOverlay === 'function') {
                const origSetOverlay = OrigBase.prototype.setTitleBarOverlay;
                OrigBase.prototype.setTitleBarOverlay = function(opts) {
                    if (theme.material !== 'none' && opts && typeof opts === 'object') {
                        opts = Object.assign({}, opts, { color: '#00000000' });
                    }
                    return origSetOverlay.call(this, opts);
                };
            }

            if (OrigBase.prototype && typeof OrigBase.prototype.setBackgroundColor === 'function') {
                const origSetBg = OrigBase.prototype.setBackgroundColor;
                OrigBase.prototype.setBackgroundColor = function(color) {
                    if (theme.material !== 'none') {
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
                if (theme.material !== 'none') {
                    options.backgroundMaterial = theme.material;
                    options.backgroundColor = '#00000000';
                    if (options.titleBarOverlay && typeof options.titleBarOverlay === 'object') {
                        options.titleBarOverlay = Object.assign({}, options.titleBarOverlay, { color: '#00000000' });
                    }
                }
                const win = new OrigBase(options);
                if (theme.material !== 'none') {
                    applyWindowEffects(win);
                }

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
                                        if (theme.material !== 'none') return origViewBg.call(this, '#00000000');
                                        return origViewBg.call(this, color);
                                    };
                                    if (theme.material !== 'none') {
                                        try { origViewBg.call(view, '#00000000'); } catch (_) {}
                                    }
                                }
                                if (isCurtainUnderlay) {
                                    if (typeof view.setVisible === 'function' && !view.__origSetVisible) {
                                        const origSetVisible = view.setVisible;
                                        view.__origSetVisible = origSetVisible;
                                        view.setVisible = function(visible) {
                                            if (theme.material !== 'none') return origSetVisible.call(this, false);
                                            return origSetVisible.call(this, visible);
                                        };
                                        if (theme.material !== 'none') {
                                            try { origSetVisible.call(view, false); } catch (_) {}
                                        }
                                    }
                                    if (typeof view.setBounds === 'function' && !view.__origSetBounds) {
                                        const origSetBounds = view.setBounds;
                                        view.__origSetBounds = origSetBounds;
                                        view.setBounds = function(bounds) {
                                            if (theme.material !== 'none') {
                                                return origSetBounds.call(this, { x: 0, y: 0, width: 0, height: 0 });
                                            }
                                            return origSetBounds.call(this, bounds);
                                        };
                                        if (theme.material !== 'none') {
                                            try { origSetBounds.call(view, { x: 0, y: 0, width: 0, height: 0 }); } catch (_) {}
                                        }
                                    }
                                }
                            } else if (view) {
                                if (theme.material !== 'none' && typeof view.setBackgroundColor === 'function') {
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

        if (electron.ipcMain && typeof electron.ipcMain.handle === 'function') {
            const origHandle = electron.ipcMain.handle;
            electron.ipcMain.handle = function(channel, listener) {
                if (channel === 'wcv:set-theme-color') {
                    const origListener = listener;
                    return origHandle.call(this, channel, async function(event, color) {
                        if (theme.material !== 'none') {
                            return true;
                        }
                        return origListener.call(this, event, color);
                    });
                }
                return origHandle.call(this, channel, listener);
            };
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
[role="dialog"]:not(#gl-ctx-flyout),
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
}
`;

            const strokeFlyout = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.10)';
            const shadowFlyout = isLight
                ? '0 8px 16px -4px rgba(0, 0, 0, 0.14), 0 0 2px 0 rgba(0, 0, 0, 0.12)'
                : '0 8px 16px -4px rgba(0, 0, 0, 0.36), 0 0 2px 0 rgba(0, 0, 0, 0.28)';
            const shadowTooltip = isLight
                ? '0 4px 8px -2px rgba(0, 0, 0, 0.12), 0 0 2px 0 rgba(0, 0, 0, 0.10)'
                : '0 4px 8px -2px rgba(0, 0, 0, 0.32), 0 0 2px 0 rgba(0, 0, 0, 0.24)';
            const shadowDialog = isLight
                ? '0 24px 48px -8px rgba(0, 0, 0, 0.18), 0 2px 6px 0 rgba(0, 0, 0, 0.08)'
                : '0 24px 48px -8px rgba(0, 0, 0, 0.48), 0 2px 6px 0 rgba(0, 0, 0, 0.24)';

            return `
:root,
body,
body[data-agy-theme],
body[data-agy-theme].dark,
body[data-agy-theme].light {
    --gl-alpha: ${a.toFixed(2)};
    --gl-popup-alpha: ${pa.toFixed(2)};
    --gl-composer-ring-a: ${ringA};
    --gl-composer-fill-a: ${fillA};
    --gl-row-hover: ${rowHover};
    --gl-row-active: ${rowActive};
    --gl-row-active-stroke: ${rowActiveStroke};
    --gl-stroke-flyout: ${strokeFlyout};
    --gl-shadow-flyout: ${shadowFlyout};
    --gl-shadow-tooltip: ${shadowTooltip};
    --gl-shadow-dialog: ${shadowDialog};
    --background: transparent !important;
    --surface: transparent !important;
    --composer-fill: rgba(${composerFill}, ${fillA}) !important;
    --composer-border: rgba(${composerRing}, ${ringA}) !important;
    --sidebar: transparent !important;
    --sidebar-border: transparent !important;
    --sidebar-accent: rgba(${surface}, 0.06) !important;
    --card: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --popover: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --color-sidebar: transparent !important;
    --color-background: transparent !important;
    --color-popover: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --color-card: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --agy-pane-bg: transparent !important;
    --agy-chrome-bg: transparent !important;
    --agy-surface-card: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --agy-surface-panel: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --agy-surface-glass: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    --agy-surface-base: transparent !important;
    --agy-surface-rail: transparent !important;
    --agy-surface-composer: rgba(${composerFill}, ${fillA}) !important;
    --agy-surface-composer-raised: rgba(${composerFill}, ${fillA}) !important;
    --agy-border-subtle: rgba(${composerRing}, 0.14) !important;
    --agy-border-composer: rgba(${composerRing}, ${ringA}) !important;
    --agy-backdrop-filter-composer: none !important;
    --agy-backdrop-filter-glass: none !important;
    --vscode-sideBar-background: transparent !important;
    --vscode-editor-background: transparent !important;
    --vscode-panel-background: transparent !important;
    --vscode-editorGroupHeader-tabsBackground: transparent !important;
    --vscode-titleBar-activeBackground: transparent !important;
    --vscode-titleBar-inactiveBackground: transparent !important;
    --vscode-statusBar-background: transparent !important;
    --vscode-activityBar-background: transparent !important;
}

html, body {
    background: transparent !important;
    background-color: transparent !important;
}

#root, #app {
    background: rgba(${surface}, ${a.toFixed(2)}) !important;
    background-color: rgba(${surface}, ${a.toFixed(2)}) !important;
}

#root > div,
.bg-background,
.bg-surface,
.bg-sidebar,
.bg-panel,
.bg-canvas,
.surface-rail,
.surface-base,
.agy-bg-background,
.agy-shell-rail,
.agy-pane-content,
.agy-sidebar-panel,
.agy-sidebar-raised,
body[data-agy-theme] .surface-base,
body[data-agy-theme] .surface-rail,
body[data-agy-theme] .agy-shell-rail,
body[data-agy-theme] .agy-sidebar-panel,
body[data-agy-theme] .agy-sidebar-raised,
body[data-agy-theme] .agy-pane-content,
body[data-agy-theme].dark .agy-shell-rail,
body[data-agy-theme].dark .agy-sidebar-panel,
body[data-agy-theme].dark .agy-sidebar-raised,
[data-testid="sidebar"],
[data-testid="main-content"],
.monaco-workbench,
.monaco-workbench .part.sidebar,
.monaco-workbench .part.panel,
.monaco-workbench .part.editor,
.monaco-workbench .part.titlebar,
.monaco-workbench .part.statusbar,
.monaco-workbench .part.activitybar,
.monaco-workbench .pane-header,
.monaco-editor,
.monaco-editor .overflow-guard,
.monaco-editor-background,
.monaco-editor .margin,
.monaco-list-rows,
.xterm-viewport {
    background: transparent !important;
    background-color: transparent !important;
}

.monaco-workbench .part.sidebar,
.monaco-workbench .part.activitybar,
.monaco-workbench .part.titlebar,
.monaco-workbench .part.panel,
.monaco-workbench .part.statusbar,
.monaco-workbench .pane-header,
[data-testid="sidebar"] {
    border-color: transparent !important;
}

.surface-composer,
.surface-composer-raised,
.agy-surface-composer,
[data-testid="composer"],
.composer-container,
body[data-agy-theme] .surface-composer,
body[data-agy-theme] .surface-composer-raised,
body[data-agy-theme].dark .surface-composer,
body[data-agy-theme].dark .surface-composer-raised {
    background: rgba(${composerFill}, ${fillA}) !important;
    background-color: rgba(${composerFill}, ${fillA}) !important;
    border-color: rgba(${composerRing}, ${ringA}) !important;
    box-shadow: 0 0 0 1px rgba(${composerRing}, ${ringA}), 0 8px 32px rgba(0,0,0,${(a * 0.25).toFixed(2)}) !important;
    backdrop-filter: blur(28px) saturate(1.8) !important;
    -webkit-backdrop-filter: blur(28px) saturate(1.8) !important;
}

[role="menu"],
[role="listbox"]:not([aria-label="Typeahead menu"]),
[data-select-popup],
[data-popover-popup],
[data-hover-card-popup],
[data-floating-ui-portal] > div,
[data-radix-popper-content-wrapper] > div,
[data-radix-dropdown-menu-content],
[data-radix-menu-content],
[data-radix-context-menu-content],
[data-slot="popover-content"],
[data-slot="dropdown-menu-content"],
[data-slot="select-content"],
[data-slot="context-menu-content"],
.surface-glass,
body[data-agy-theme] .surface-glass,
body[data-agy-theme] .agy-fastpick.surface-glass,
body[data-agy-theme] .quick-input-widget.surface-glass,
.bg-popover,
.bg-card,
.absolute.bg-sidebar,
.absolute.bg-background,
.absolute.bg-card,
.react-tooltip {
    background-color: rgba(${popupTint}, ${pa.toFixed(2)}) !important;
    backdrop-filter: blur(28px) saturate(1.8) !important;
    -webkit-backdrop-filter: blur(28px) saturate(1.8) !important;
}

[role="dialog"]:not(#gl-ctx-flyout),
[role="alertdialog"],
[aria-modal="true"],
[data-testid="bottom-sheet"],
.settings-modal-container,
[cmdk-root],
.command-palette {
    background-color: rgba(${dialogTint}, ${Math.max(pa, 0.85).toFixed(2)}) !important;
    backdrop-filter: blur(28px) saturate(1.8) !important;
    -webkit-backdrop-filter: blur(28px) saturate(1.8) !important;
}

[data-radix-dialog-overlay],
[data-state="open"].fixed.inset-0,
.fixed.inset-0.bg-black\/50,
.fixed.inset-0.bg-black\/40 {
    background-color: rgba(0, 0, 0, ${(a * 0.35).toFixed(2)}) !important;
    backdrop-filter: blur(6px) !important;
}

${blurCss}

[data-slot$="-item"]:hover,
[role="menuitem"]:hover,
[role="option"]:hover,
[cmdk-item]:hover,
.agy-fastpick .fastpick-row:hover {
    background-color: var(--gl-row-hover) !important;
}

[data-slot$="-item"][data-highlighted],
[data-slot$="-item"][aria-selected="true"],
[role="menuitem"][data-highlighted],
[role="option"][aria-selected="true"],
[cmdk-item][aria-selected="true"],
[cmdk-item][data-selected="true"] {
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

[role="dialog"]:not(#gl-ctx-flyout),
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

            if (theme.material === 'none') {
                return;
            }

            const alpha = (rawAlpha === undefined || rawAlpha === null) ? theme.alpha : rawAlpha;
            const popupAlpha = (rawPopupAlpha === undefined || rawPopupAlpha === null) ? theme.popupAlpha : rawPopupAlpha;
            const css = buildCss(alpha, popupAlpha, theme.alpha, theme.light,
                                 theme.accent, theme.accentInteraction,
                                 theme.noiseAlpha, theme.noiseFreq, theme.noiseTint, theme.noiseBase);

            try {
                const p = wc.insertCSS(css, { cssOrigin: 'user' });
                if (p && typeof p.then === 'function') {
                    p.then(function (key) { wc.__gravitylessCssKey = key; }).catch(function () {});
                } else {
                    wc.__gravitylessCssKey = p;
                }
            } catch (_) {}
        }

        function applyMaterialToWindow(win, material) {
            if (!win || !material) return;
            const revertAll = (material === 'none');
            const solidBg = theme.light ? '#FAFAFA' : '#131313';
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
                            symbolColor: theme.light ? '#383A42' : '#FAFAFA'
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

        function scheduleApplyMaterialToWindows() {
            if (windowMaterialDebounceTimer) return;
            windowMaterialDebounceTimer = setTimeout(function() {
                windowMaterialDebounceTimer = null;
                applyMaterialToWindows(theme.material);
            }, 30);
        }

        global.__gravitylessMica = {
            buildCss: buildCss,
            attach: attach,
            getAlpha: function () { return theme.alpha; },
            setAlpha: function (v) { theme.alpha = clamp(Number(v) || 0, 0, 1); }
        };

        ctx.onReady(scheduleApplyMaterialToWindows);
        ctx.onDomReady(function(wc) {
            attach(wc);
            scheduleApplyMaterialToWindows();
        });
        ctx.onDidFinishLoad(function(wc) {
            attach(wc);
            scheduleApplyMaterialToWindows();
        });
        ctx.onThemeReload(function(allWc) {
            applyMaterialToWindows(theme.material);
            for (const wc of allWc) {
                attach(wc);
            }
        });
    }
};
""";
    }
}
