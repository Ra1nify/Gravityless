namespace Gravityless.Core.Services.Patches.Plugins;

/// <summary>
/// Injects the WinUI 3 Fluent-styled circular context-window ring indicator next to the model selector
/// and its Acrylic/Mica flyout breakdown popover.
/// Uses !important on layout, margin, padding, border, and 5-tier color rules so Author Tailwind v4 Preflight
/// (* { margin: 0; padding: 0; border: 0 solid; }) cannot override cssOrigin:'user' rules.
/// </summary>
public sealed class ContextIndicatorPlugin : IPatchPlugin
{
    public string Id => "context-indicator";
    public string FileName => "05-context-indicator.js";
    public int Order => 50;

    public string BuildScript(PatchContext context)
    {
        return $$"""
{{context.PatchStamp}}
module.exports = {
    id: 'context-indicator',
    init: function(ctx) {
        const { theme } = ctx;
        const isHubUrl = ctx.utils.isHubUrl;

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

            const flyoutBgColor = isLight
                ? (nB > 0 ? ('rgba(' + mixCh(cr) + ', ' + mixCh(cg) + ', ' + mixCh(cb) + ', ' + Math.max(0.86, nB).toFixed(3) + ')') : 'rgba(252, 252, 252, 0.94)')
                : (nB > 0 ? ('rgba(' + mixCh(cr) + ', ' + mixCh(cg) + ', ' + mixCh(cb) + ', ' + Math.max(0.82, nB).toFixed(3) + ')') : 'rgba(32, 32, 32, 0.92)');
            const flyoutBgImage = (nA > 0 && nB > 0) ? (noiseSvg(coarseFreq, coarseA) + ', ' + noiseSvg(fineFreq, fineA)) : 'none';

            const fgPrimary = isLight ? 'rgba(0, 0, 0, 0.8956)' : '#FFFFFF';
            const fgSecondary = isLight ? 'rgba(0, 0, 0, 0.6063)' : 'rgba(255, 255, 255, 0.786)';
            const fgTertiary = isLight ? 'rgba(0, 0, 0, 0.4458)' : 'rgba(255, 255, 255, 0.5442)';
            const ctrlHover = isLight ? 'rgba(0, 0, 0, 0.0373)' : 'rgba(255, 255, 255, 0.0605)';
            const ctrlPress = isLight ? 'rgba(0, 0, 0, 0.0241)' : 'rgba(255, 255, 255, 0.0419)';
            const strokeCtrl = isLight ? 'rgba(0, 0, 0, 0.0578)' : 'rgba(255, 255, 255, 0.093)';
            const strokeFlyout = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.10)';
            const divider = isLight ? 'rgba(0, 0, 0, 0.0578)' : 'rgba(255, 255, 255, 0.0837)';
            const trackBg = isLight ? 'rgba(0, 0, 0, 0.14)' : 'rgba(255, 255, 255, 0.16)';
            const barTrackBg = isLight ? 'rgba(0, 0, 0, 0.08)' : 'rgba(255, 255, 255, 0.10)';
            const shadowFlyout = isLight
                ? '0 8px 16px -4px rgba(0, 0, 0, 0.14), 0 0 2px 0 rgba(0, 0, 0, 0.12)'
                : '0 8px 16px -4px rgba(0, 0, 0, 0.36), 0 0 2px 0 rgba(0, 0, 0, 0.28)';

            // 5-tier semantic color palette by percentage: 蓝 -> 绿 -> 黄 -> 橙 -> 红
            const blueColor = isLight ? '#0078D4' : '#4CC2FF';
            const blueSoft = isLight ? 'rgba(0, 120, 212, 0.14)' : 'rgba(76, 194, 255, 0.16)';
            const greenColor = isLight ? '#107C10' : '#6CCB5F';
            const greenSoft = isLight ? 'rgba(16, 124, 16, 0.14)' : 'rgba(108, 203, 95, 0.16)';
            const yellowColor = isLight ? '#B88600' : '#FCE100';
            const yellowSoft = isLight ? 'rgba(184, 134, 0, 0.15)' : 'rgba(252, 225, 0, 0.16)';
            const orangeColor = isLight ? '#CA5010' : '#FF8C00';
            const orangeSoft = isLight ? 'rgba(202, 80, 16, 0.15)' : 'rgba(255, 140, 0, 0.18)';
            const redColor = isLight ? '#C42B1C' : '#FF6B6B';
            const redSoft = isLight ? 'rgba(196, 43, 28, 0.14)' : 'rgba(255, 107, 107, 0.18)';

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
    --gl-ctx-blue: ${blueColor};
    --gl-ctx-blue-soft: ${blueSoft};
    --gl-ctx-green: ${greenColor};
    --gl-ctx-green-soft: ${greenSoft};
    --gl-ctx-yellow: ${yellowColor};
    --gl-ctx-yellow-soft: ${yellowSoft};
    --gl-ctx-orange: ${orangeColor};
    --gl-ctx-orange-soft: ${orangeSoft};
    --gl-ctx-red: ${redColor};
    --gl-ctx-red-soft: ${redSoft};
    --gl-ctx-state: var(--gl-ctx-blue);
    --gl-ctx-state-soft: var(--gl-ctx-blue-soft);
}

.gl-ctx-host {
    display: inline-flex !important;
    align-items: center !important;
    flex-shrink: 0 !important;
    margin: 0 2px !important;
    padding: 0 !important;
}

.gl-ctx-btn {
    position: relative !important;
    appearance: none !important;
    -webkit-appearance: none !important;
    width: 28px !important;
    height: 28px !important;
    padding: 0 !important;
    margin: 0 !important;
    border-radius: 9999px !important;
    border: 0 solid transparent !important;
    background: transparent !important;
    background-color: transparent !important;
    box-shadow: none !important;
    color: var(--gl-ctx-fg-muted) !important;
    display: inline-flex !important;
    align-items: center !important;
    justify-content: center !important;
    cursor: pointer !important;
    user-select: none !important;
    outline: none !important;
    transition: background-color 150ms cubic-bezier(0.1, 0.9, 0.2, 1), color 150ms ease, transform 80ms ease !important;
}
.gl-ctx-btn:hover {
    background-color: var(--secondary, var(--gl-ctx-hover)) !important;
    border: 0 solid transparent !important;
    box-shadow: none !important;
    color: var(--gl-ctx-fg) !important;
}
.gl-ctx-btn:active {
    background-color: var(--secondary, var(--gl-ctx-press)) !important;
    transform: scale(0.96) !important;
}
.gl-ctx-btn[aria-expanded="true"] {
    background-color: var(--secondary, var(--gl-ctx-hover)) !important;
    border: 0 solid transparent !important;
    box-shadow: none !important;
    color: var(--gl-ctx-fg) !important;
}
.gl-ctx-btn:focus:not(:focus-visible) {
    outline: none !important;
    box-shadow: none !important;
}
.gl-ctx-btn:focus-visible {
    outline: 2px solid var(--nux-focus-ring, var(--gl-ctx-fg)) !important;
    outline-offset: 2px !important;
}

.gl-ctx-svg {
    width: 18px !important;
    height: 18px !important;
    display: block !important;
    overflow: visible !important;
}
.gl-ctx-ring-track {
    stroke: var(--gl-ctx-track) !important;
    transition: stroke 160ms ease !important;
}
.gl-ctx-btn:hover .gl-ctx-ring-track,
.gl-ctx-btn[aria-expanded="true"] .gl-ctx-ring-track {
    stroke: var(--gl-ctx-fg-tertiary) !important;
}
.gl-ctx-ring-arc {
    stroke: var(--gl-ctx-blue) !important;
    transition: stroke-dashoffset 260ms cubic-bezier(0.1, 0.9, 0.2, 1), stroke 180ms ease, opacity 140ms ease !important;
}
.gl-ctx-btn[data-level="blue"] .gl-ctx-ring-arc { stroke: var(--gl-ctx-blue) !important; }
.gl-ctx-btn[data-level="green"] .gl-ctx-ring-arc { stroke: var(--gl-ctx-green) !important; }
.gl-ctx-btn[data-level="yellow"] .gl-ctx-ring-arc { stroke: var(--gl-ctx-yellow) !important; }
.gl-ctx-btn[data-level="orange"] .gl-ctx-ring-arc { stroke: var(--gl-ctx-orange) !important; }
.gl-ctx-btn[data-level="red"] .gl-ctx-ring-arc { stroke: var(--gl-ctx-red) !important; }

.gl-ctx-flyout {
    position: fixed !important;
    z-index: 6500 !important;
    width: 288px !important;
    max-width: calc(100vw - 32px) !important;
    max-height: calc(100vh - 32px) !important;
    overflow-y: auto !important;
    box-sizing: border-box !important;
    margin: 0 !important;
    padding: 16px 18px !important;
    border-radius: 8px !important;
    border: 1px solid var(--gl-ctx-stroke-flyout) !important;
    background-color: var(--gl-ctx-flyout-bg) !important;
    background-image: var(--gl-ctx-flyout-img) !important;
    background-blend-mode: normal, overlay !important;
    backdrop-filter: blur(28px) saturate(1.8) !important;
    -webkit-backdrop-filter: blur(28px) saturate(1.8) !important;
    box-shadow: inset 0 0 0 1px var(--gl-ctx-stroke-flyout), var(--gl-ctx-shadow) !important;
    color: var(--gl-ctx-fg) !important;
    font-family: 'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, sans-serif !important;
    font-size: 12px !important;
    line-height: 1.4 !important;
    pointer-events: auto !important;
    user-select: text !important;
    outline: none !important;
    --gl-ctx-state: var(--gl-ctx-blue);
    --gl-ctx-state-soft: var(--gl-ctx-blue-soft);
    animation: glCtxFlyoutIn 167ms cubic-bezier(0.1, 0.9, 0.2, 1) both !important;
}
.gl-ctx-flyout[data-level="blue"] {
    --gl-ctx-state: var(--gl-ctx-blue);
    --gl-ctx-state-soft: var(--gl-ctx-blue-soft);
}
.gl-ctx-flyout[data-level="green"] {
    --gl-ctx-state: var(--gl-ctx-green);
    --gl-ctx-state-soft: var(--gl-ctx-green-soft);
}
.gl-ctx-flyout[data-level="yellow"] {
    --gl-ctx-state: var(--gl-ctx-yellow);
    --gl-ctx-state-soft: var(--gl-ctx-yellow-soft);
}
.gl-ctx-flyout[data-level="orange"] {
    --gl-ctx-state: var(--gl-ctx-orange);
    --gl-ctx-state-soft: var(--gl-ctx-orange-soft);
}
.gl-ctx-flyout[data-level="red"] {
    --gl-ctx-state: var(--gl-ctx-red);
    --gl-ctx-state-soft: var(--gl-ctx-red-soft);
}
@keyframes glCtxFlyoutIn {
    from { opacity: 0; transform: translateY(6px); }
    to { opacity: 1; transform: translateY(0); }
}

.gl-ctx-head {
    display: flex !important;
    align-items: center !important;
    justify-content: space-between !important;
    gap: 10px !important;
    margin: 0 0 10px 0 !important;
    padding: 0 !important;
}
.gl-ctx-title-wrap {
    min-width: 0 !important;
    display: flex !important;
    flex-direction: column !important;
    gap: 2px !important;
}
.gl-ctx-title {
    font-size: 12px !important;
    font-weight: 600 !important;
    color: var(--gl-ctx-fg) !important;
    letter-spacing: -0.01em !important;
    margin: 0 !important;
    padding: 0 !important;
}
.gl-ctx-subtitle {
    font-size: 11px !important;
    color: var(--gl-ctx-fg-muted) !important;
    white-space: nowrap !important;
    overflow: hidden !important;
    text-overflow: ellipsis !important;
    margin: 0 !important;
    padding: 0 !important;
}
.gl-ctx-head-right {
    display: inline-flex !important;
    align-items: center !important;
    gap: 6px !important;
    flex-shrink: 0 !important;
}
.gl-ctx-refresh {
    width: 24px !important;
    height: 24px !important;
    padding: 0 !important;
    margin: 0 !important;
    border-radius: 4px !important;
    border: 1px solid transparent !important;
    background: transparent !important;
    color: var(--gl-ctx-fg-muted) !important;
    display: inline-flex !important;
    align-items: center !important;
    justify-content: center !important;
    cursor: pointer !important;
    outline: none !important;
    transition: background-color 110ms ease, color 110ms ease !important;
}
.gl-ctx-refresh:hover {
    background-color: var(--gl-ctx-hover) !important;
    color: var(--gl-ctx-fg) !important;
}
.gl-ctx-refresh:active {
    background-color: var(--gl-ctx-press) !important;
}
.gl-ctx-refresh:focus-visible {
    outline: 2px solid var(--gl-ctx-fg) !important;
    outline-offset: 1px !important;
}
.gl-ctx-refresh[data-spinning="true"] svg {
    animation: glCtxSpin 700ms linear infinite !important;
}
@keyframes glCtxSpin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
}

.gl-ctx-badge {
    font-variant-numeric: tabular-nums !important;
    font-size: 11px !important;
    font-weight: 600 !important;
    line-height: 1 !important;
    margin: 0 !important;
    padding: 4px 7px !important;
    border-radius: 4px !important;
    background-color: var(--gl-ctx-state-soft) !important;
    color: var(--gl-ctx-state) !important;
}

.gl-ctx-hero {
    display: flex !important;
    align-items: baseline !important;
    gap: 5px !important;
    margin: 0 !important;
    padding: 0 !important;
    font-variant-numeric: tabular-nums !important;
}
.gl-ctx-used {
    font-size: 20px !important;
    font-weight: 600 !important;
    line-height: 1.2 !important;
    letter-spacing: -0.02em !important;
    color: var(--gl-ctx-fg) !important;
}
.gl-ctx-cap {
    font-size: 13px !important;
    font-weight: 500 !important;
    color: var(--gl-ctx-fg-muted) !important;
}
.gl-ctx-exact {
    margin: 4px 0 0 0 !important;
    padding: 0 !important;
    font-size: 11px !important;
    color: var(--gl-ctx-fg-muted) !important;
    font-variant-numeric: tabular-nums !important;
}

.gl-ctx-bar {
    margin: 10px 0 0 0 !important;
    padding: 0 !important;
    height: 4px !important;
    width: 100% !important;
    border-radius: 9999px !important;
    background-color: var(--gl-ctx-bar-track) !important;
    overflow: hidden !important;
}
.gl-ctx-bar-fill {
    height: 100% !important;
    border-radius: 9999px !important;
    background-color: var(--gl-ctx-state) !important;
    transition: width 240ms cubic-bezier(0.1, 0.9, 0.2, 1), background-color 180ms ease !important;
}
.gl-ctx-flyout[data-level="blue"] .gl-ctx-bar-fill { background-color: var(--gl-ctx-blue) !important; }
.gl-ctx-flyout[data-level="green"] .gl-ctx-bar-fill { background-color: var(--gl-ctx-green) !important; }
.gl-ctx-flyout[data-level="yellow"] .gl-ctx-bar-fill { background-color: var(--gl-ctx-yellow) !important; }
.gl-ctx-flyout[data-level="orange"] .gl-ctx-bar-fill { background-color: var(--gl-ctx-orange) !important; }
.gl-ctx-flyout[data-level="red"] .gl-ctx-bar-fill { background-color: var(--gl-ctx-red) !important; }

.gl-ctx-divider {
    height: 1px !important;
    margin: 12px 0 !important;
    padding: 0 !important;
    background-color: var(--gl-ctx-divider) !important;
}

.gl-ctx-rows {
    display: flex !important;
    flex-direction: column !important;
    gap: 6px !important;
    margin: 0 !important;
    padding: 0 !important;
}
.gl-ctx-row {
    display: flex !important;
    align-items: center !important;
    justify-content: space-between !important;
    gap: 10px !important;
    font-size: 11.5px !important;
    min-height: 18px !important;
    margin: 0 !important;
    padding: 0 !important;
}
.gl-ctx-row-label {
    color: var(--gl-ctx-fg-muted) !important;
    white-space: nowrap !important;
}
.gl-ctx-row-val {
    color: var(--gl-ctx-fg) !important;
    font-variant-numeric: tabular-nums !important;
    font-weight: 500 !important;
    text-align: right !important;
}
.gl-ctx-row-sub {
    color: var(--gl-ctx-fg-tertiary) !important;
    font-weight: 400 !important;
    margin: 0 0 0 4px !important;
}

.gl-ctx-groups {
    margin: 10px 0 0 0 !important;
    padding: 10px 0 0 0 !important;
    border-top: 1px solid var(--gl-ctx-divider) !important;
    display: flex !important;
    flex-direction: column !important;
    gap: 5px !important;
}
.gl-ctx-group-title {
    font-size: 10.5px !important;
    font-weight: 600 !important;
    color: var(--gl-ctx-fg-tertiary) !important;
    margin: 0 0 2px 0 !important;
    padding: 0 !important;
}

.gl-ctx-note {
    font-size: 11.5px !important;
    color: var(--gl-ctx-fg-muted) !important;
    line-height: 1.45 !important;
}
.gl-ctx-error-box {
    display: flex !important;
    align-items: center !important;
    justify-content: space-between !important;
    gap: 8px !important;
    margin: 0 !important;
    padding: 8px 10px !important;
    border-radius: 4px !important;
    background-color: var(--gl-ctx-red-soft) !important;
    color: var(--gl-ctx-red) !important;
    font-size: 11.5px !important;
}
.gl-ctx-retry-btn {
    border: 1px solid var(--gl-ctx-stroke-ctrl) !important;
    background-color: var(--gl-ctx-hover) !important;
    color: var(--gl-ctx-fg) !important;
    border-radius: 4px !important;
    margin: 0 !important;
    padding: 3px 9px !important;
    font-size: 11px !important;
    cursor: pointer !important;
    flex-shrink: 0 !important;
}
.gl-ctx-retry-btn:hover {
    background-color: var(--gl-ctx-press) !important;
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
    .gl-ctx-btn .gl-ctx-ring-arc,
    .gl-ctx-btn[data-level] .gl-ctx-ring-arc {
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
        border: 1px solid ButtonText !important;
    }
    .gl-ctx-flyout .gl-ctx-bar-fill,
    .gl-ctx-flyout[data-level] .gl-ctx-bar-fill {
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

            const css = buildWidgetCss(theme.light, theme.accent, theme.accentInteraction,
                                       theme.noiseAlpha, theme.noiseFreq, theme.noiseTint, theme.noiseBase);
            try {
                const p = wc.insertCSS(css, { cssOrigin: 'user' });
                if (p && typeof p.then === 'function') {
                    p.then(function (key) { wc.__gravitylessWidgetCssKey = key; }).catch(function () {});
                } else {
                    wc.__gravitylessWidgetCssKey = p;
                }
            } catch (_) {}
        }

        function gravitylessContextIndicatorMain() {
            if (!/^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/i.test(window.location.origin || '')) return;
            if (window.__gravitylessContextIndicatorInstalled) return;
            window.__gravitylessContextIndicatorInstalled = true;

            (function ensureBaseStyle() {
                try {
                    if (document.getElementById('gl-ctx-inline-style')) return;
                    const st = document.createElement('style');
                    st.id = 'gl-ctx-inline-style';
                    st.textContent = [
                        '.gl-ctx-host{display:inline-flex!important;align-items:center!important;flex-shrink:0!important;margin:0 2px!important;padding:0!important;background:transparent!important;border:0!important;box-shadow:none!important;}',
                        '.gl-ctx-btn{position:relative!important;width:28px!important;height:28px!important;min-width:28px!important;min-height:28px!important;padding:0!important;margin:0!important;border:0!important;border-radius:9999px!important;background:transparent!important;background-color:transparent!important;box-shadow:none!important;outline:none!important;display:inline-flex!important;align-items:center!important;justify-content:center!important;cursor:pointer!important;user-select:none!important;}',
                        '.gl-ctx-btn:not(:hover):not([aria-expanded="true"]){background:transparent!important;background-color:transparent!important;border:0!important;box-shadow:none!important;}',
                        '.gl-ctx-btn:hover,.gl-ctx-btn[aria-expanded="true"]{background-color:var(--secondary,var(--gl-ctx-hover))!important;border:0!important;border-radius:9999px!important;box-shadow:none!important;}',
                        '.gl-ctx-btn:focus:not(:focus-visible){outline:none!important;box-shadow:none!important;}'
                    ].join('\n');
                    (document.head || document.documentElement).appendChild(st);
                } catch (_) {}
            })();

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

            function levelForPct(pct) {
                if (pct >= 90) return 'red';
                if (pct >= 75) return 'orange';
                if (pct >= 50) return 'yellow';
                if (pct >= 25) return 'green';
                return 'blue';
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
                const level = levelForPct(pct);

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

                const head = el('div', 'gl-ctx-head');
                const titleWrap = el('div', 'gl-ctx-title-wrap');
                titleWrap.appendChild(el('div', 'gl-ctx-title', tr('上下文窗口', 'Context window')));
                const subLabel = rec.modelLabel || m.modelDisplayName || (cascadeId ? tr('当前对话', 'Current conversation') : tr('新对话', 'New conversation'));
                titleWrap.appendChild(el('div', 'gl-ctx-subtitle', subLabel));
                head.appendChild(titleWrap);

                const headRight = el('div', 'gl-ctx-head-right');
                if (cascadeId) {
                    const refBtn = el('div', 'gl-ctx-refresh');
                    refBtn.setAttribute('role', 'button');
                    refBtn.setAttribute('tabindex', '0');
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
                    refBtn.addEventListener('keydown', (ev) => {
                        if (ev.key === 'Enter' || ev.key === ' ') {
                            ev.preventDefault();
                            ev.stopPropagation();
                            refreshCascade(cascadeId, true, rec.trajCtx);
                        }
                    });
                    headRight.appendChild(refBtn);
                }
                const badgeText = m.hasData ? m.pct.toFixed(1) + '%' : '--%';
                headRight.appendChild(el('span', 'gl-ctx-badge', badgeText));
                head.appendChild(headRight);
                flyout.appendChild(head);

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
                const fw = flyout.offsetWidth || 288;
                const fh = flyout.offsetHeight || 220;
                const vw = window.innerWidth || document.documentElement.clientWidth;
                const vh = window.innerHeight || document.documentElement.clientHeight;
                const edge = 16;
                const gap = 12;
                const left = Math.min(Math.max(edge, br.left), Math.max(edge, vw - fw - edge));
                let top = br.top - fh - gap;
                if (top < edge && (br.bottom + gap + fh) <= vh - edge) {
                    top = br.bottom + gap;
                }
                top = Math.min(Math.max(edge, top), Math.max(edge, vh - fh - edge));
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
                        host.style.cssText = 'display:inline-flex;align-items:center;flex-shrink:0;margin:0 2px;padding:0;background:transparent;border:0;box-shadow:none;';
                        const btn = el('div', 'gl-ctx-btn flex cursor-pointer items-center justify-center h-7 w-7 rounded-full select-none outline-none transition-colors');
                        btn.style.cssText = 'width:28px;height:28px;min-width:28px;min-height:28px;padding:0;margin:0;border:0;border-radius:9999px;background:transparent;box-shadow:none;outline:none;display:inline-flex;align-items:center;justify-content:center;';
                        btn.setAttribute('role', 'button');
                        btn.setAttribute('tabindex', '0');
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
                        btn.addEventListener('keydown', (ev) => {
                            if (ev.key === 'Enter' || ev.key === ' ') {
                                ev.preventDefault();
                                ev.stopPropagation();
                                openFlyout(rec);
                            }
                        });
                        footerRecords.set(footer, rec);
                    }

                    if (rec.host.previousElementSibling !== anchor || rec.host.parentElement !== anchor.parentElement) {
                        anchor.parentElement.insertBefore(rec.host, anchor.nextSibling);
                    }

                    const ctxInfo = readFooterContext(footer);
                    const cascadeChanged = ctxInfo.cascadeId !== rec.cascadeId;
                    const finishedTurn = rec.isRunning && !ctxInfo.isRunning;
                    rec.cascadeId = ctxInfo.cascadeId;
                    rec.isRunning = ctxInfo.isRunning;
                    rec.modelLabel = ctxInfo.modelLabel;
                    rec.trajCtx = ctxInfo.trajCtx;

                    syncTrajListener(rec, ctxInfo.cascadeId, ctxInfo.trajCtx);

                    if (ctxInfo.cascadeId && (cascadeChanged || !cacheByCascade.has(ctxInfo.cascadeId))) {
                        refreshCascade(ctxInfo.cascadeId, false, ctxInfo.trajCtx);
                    } else if (ctxInfo.cascadeId && finishedTurn) {
                        setTimeout(() => refreshCascade(ctxInfo.cascadeId, true, rec.trajCtx), 250);
                        setTimeout(() => refreshCascade(ctxInfo.cascadeId, true, rec.trajCtx), 950);
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

        ctx.onDomReady(function(wc) {
            attachWidgetCss(wc);
            injectContextIndicator(wc);
        });
        ctx.onDidFinishLoad(function(wc) {
            attachWidgetCss(wc);
            injectContextIndicator(wc);
        });
        ctx.onThemeReload(function(allWc) {
            for (const wc of allWc) {
                attachWidgetCss(wc);
            }
        });
    }
};
""";
    }
}
