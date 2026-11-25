namespace Gravityless.Core.Services.Patches.Plugins;

/// <summary>
/// Hooks Language Server spawn arguments (--gemini_dir), disables host protocol hijacking,
/// suppresses IDE install wizard / updater popups, and applies Chromium --lang switch.
/// </summary>
public sealed class ProcessSandboxPlugin : IPatchPlugin
{
    public string Id => "process-sandbox";
    public string FileName => "01-process-sandbox.js";
    public int Order => 10;

    public string BuildScript(PatchContext context)
    {
        return $$"""
{{context.PatchStamp}}
module.exports = {
    id: 'process-sandbox',
    init: function(ctx) {
        const { electron, cp, fs, path, distDir } = ctx;

        // Suppress unwanted wizard popups & auto-updater in multi-instance sandbox
        try {
            const ideWizardPath = path.join(distDir, 'ideInstall', 'wizard.js');
            if (fs.existsSync(ideWizardPath)) {
                const ideWizard = require(ideWizardPath);
                if (ideWizard) {
                    ideWizard.maybeShowIdeInstallWizard = async function() { return false; };
                    ideWizard.showIdeInstallWizard = async function() { return; };
                }
            }
        } catch (_) {}

        try {
            const updaterPath = path.join(distDir, 'updater.js');
            if (fs.existsSync(updaterPath)) {
                const updater = require(updaterPath);
                if (updater) {
                    updater.initAutoUpdater = function() {};
                    updater.checkForUpdates = function() {};
                }
            }
        } catch (_) {}

        // Hook child_process.spawn / fork for Language Server arguments
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

        // Prevent host app from reclaiming antigravity:// protocol and enforce Chromium locale switch
        if (electron.app) {
            electron.app.setAsDefaultProtocolClient = function() {
                return false;
            };

            if (process.env.GRAVITYLESS_LOCALE) {
                try {
                    electron.app.commandLine.appendSwitch('lang', process.env.GRAVITYLESS_LOCALE);
                } catch (_) {}
            }
        }
    }
};
""";
    }
}
