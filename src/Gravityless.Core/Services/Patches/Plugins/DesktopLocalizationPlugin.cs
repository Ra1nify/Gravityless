namespace Gravityless.Core.Services.Patches.Plugins;

/// <summary>
/// Localizes native Electron dialogs, menus, and tray context menus when running in Chinese locale.
/// </summary>
public sealed class DesktopLocalizationPlugin : IPatchPlugin
{
    public string Id => "desktop-i18n";
    public string FileName => "02-desktop-i18n.js";
    public int Order => 20;

    public string BuildScript(PatchContext context)
    {
        return $$"""
{{context.PatchStamp}}
module.exports = {
    id: 'desktop-i18n',
    init: function(ctx) {
        const { electron } = ctx;
        const isZhLocale = ctx.utils.isZhLocale;

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
    }
};
""";
    }
}
