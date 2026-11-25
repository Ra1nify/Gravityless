namespace Gravityless.Core.Services.Patches.Plugins;

/// <summary>
/// Injects live DOM dictionary & dynamic pattern i18n into Antigravity renderer webContents
/// while preserving exclusion zones (editors, message bubbles, and custom Gravityless widgets).
/// </summary>
public sealed class RendererLocalizationPlugin : IPatchPlugin
{
    public string Id => "renderer-i18n";
    public string FileName => "04-renderer-i18n.js";
    public int Order => 40;

    public string BuildScript(PatchContext context)
    {
        return $$"""
{{context.PatchStamp}}
module.exports = {
    id: 'renderer-i18n',
    init: function(ctx) {
        const { fs, path, loadedConfig } = ctx;

        function findLocalesDir() {
            const candidates = [
                process.env.GRAVITYLESS_LOCALES_DIR,
                loadedConfig?.LocalesDir,
                path.resolve(ctx.distDir, '..', '..', '..', '..', 'locales'),
                path.resolve(ctx.distDir, '..', '..', '..', '..', 'data', 'locales'),
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

        ctx.onDomReady(injectI18n);
        ctx.onLocaleChanged(function(newLang, allWc) {
            const dict = readDict(newLang);
            const code = 'window.__gravityless_lang = ' + JSON.stringify(newLang) + '; '
                + 'if (window.__gravityless_set_lang) { window.__gravityless_set_lang('
                + JSON.stringify(newLang) + ', ' + JSON.stringify(dict) + '); } '
                + 'try { window.dispatchEvent(new CustomEvent("gl:lang-changed", { detail: ' + JSON.stringify(newLang) + ' })); } catch (_) {}';
            for (const wc of allWc) {
                try {
                    if (!wc.isDestroyed()) wc.executeJavaScript(code).catch(function () {});
                } catch (_) {}
            }
        });
    }
};
""";
    }
}
