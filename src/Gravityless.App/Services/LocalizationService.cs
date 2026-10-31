using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Gravityless.App.Services;

public class LanguageProfile
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsNative => string.Equals(Id, "en-US", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}

public static class LocalizationService
{
    private static readonly string ConfigDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    private static readonly string LangFile = Path.Combine(ConfigDir, "language.txt");
    private static FileSystemWatcher? _localesWatcher;
    private static System.Timers.Timer? _debounceTimer;

    public static string CurrentLanguage { get; private set; } = "zh-CN";
    public static LanguageProfile? CurrentProfile { get; private set; }
    public static ObservableCollection<LanguageProfile> AvailableLanguages { get; } = [];

    public static event Action<string>? LanguageChanged;
    public static event Action? AvailableLanguagesChanged;

    public static string GetLocalesDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir1 = Path.Combine(baseDir, "locales");
        var dir2 = Path.Combine(baseDir, "data", "locales");

        if (Directory.Exists(dir2) && !Directory.Exists(dir1)) return dir2;
        if (!Directory.Exists(dir1)) Directory.CreateDirectory(dir1);
        return dir1;
    }

    public static void Initialize()
    {
        EnsureDefaultLocales();
        RefreshLanguages();
        SetupLocalesWatcher();

        try
        {
            if (File.Exists(LangFile))
            {
                var saved = File.ReadAllText(LangFile).Trim();
                if (!string.IsNullOrEmpty(saved) && AvailableLanguages.Any(l => string.Equals(l.Id, saved, StringComparison.OrdinalIgnoreCase)))
                {
                    CurrentLanguage = saved;
                }
            }
        }
        catch { }

        CurrentProfile = AvailableLanguages.FirstOrDefault(l => string.Equals(l.Id, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            ?? AvailableLanguages.FirstOrDefault()
            ?? new LanguageProfile { Id = "zh-CN", DisplayName = "简体中文" };

        ApplyDictionary(CurrentLanguage);
    }

    public static void RefreshLanguages()
    {
        try
        {
            var dir = GetLocalesDir();
            var list = new List<LanguageProfile>();

            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        var displayName = id;
                        var content = File.ReadAllText(file, Encoding.UTF8);
                        var node = JsonNode.Parse(content);
                        if (node != null)
                        {
                            var meta = node["_meta"] as JsonObject;
                            var name = meta?["name"]?.ToString() ?? node["name"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                displayName = name;
                            }
                        }

                        list.Add(new LanguageProfile
                        {
                            Id = id,
                            DisplayName = displayName,
                            FilePath = file
                        });
                    }
                    catch { }
                }
            }

            if (!list.Any(l => string.Equals(l.Id, "zh-CN", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new LanguageProfile { Id = "zh-CN", DisplayName = "简体中文" });
            }
            if (!list.Any(l => string.Equals(l.Id, "en-US", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new LanguageProfile { Id = "en-US", DisplayName = "English (US)" });
            }

            // Order: zh-CN, en-US, then the rest by display name.
            var sorted = list.OrderBy(l =>
            {
                if (string.Equals(l.Id, "zh-CN", StringComparison.OrdinalIgnoreCase)) return 0;
                if (string.Equals(l.Id, "en-US", StringComparison.OrdinalIgnoreCase)) return 1;
                return 2;
            }).ThenBy(l => l.DisplayName).ToList();

            var action = new Action(() =>
            {
                AvailableLanguages.Clear();
                foreach (var item in sorted)
                {
                    AvailableLanguages.Add(item);
                }
                CurrentProfile = AvailableLanguages.FirstOrDefault(l => string.Equals(l.Id, CurrentLanguage, StringComparison.OrdinalIgnoreCase));
                AvailableLanguagesChanged?.Invoke();
            });

            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        }
        catch { }
    }

    // Coalesces watcher bursts into one refresh via a 300 ms non-resetting timer.
    private static void SetupLocalesWatcher()
    {
        try
        {
            var dir = GetLocalesDir();
            _localesWatcher?.Dispose();
            _localesWatcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
            _debounceTimer.Elapsed += (_, _) => RefreshLanguages();

            void OnChanged(object sender, FileSystemEventArgs e)
            {
                _debounceTimer?.Stop();
                _debounceTimer?.Start();
            }

            _localesWatcher.Created += OnChanged;
            _localesWatcher.Deleted += OnChanged;
            _localesWatcher.Renamed += (s, e) => OnChanged(s, e);
            _localesWatcher.Changed += OnChanged;
        }
        catch { }
    }

    public static void SetLanguage(string lang)
    {
        CurrentLanguage = lang;
        CurrentProfile = AvailableLanguages.FirstOrDefault(l => string.Equals(l.Id, lang, StringComparison.OrdinalIgnoreCase))
            ?? new LanguageProfile { Id = lang, DisplayName = lang };

        ApplyDictionary(lang);

        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(LangFile, lang);
        }
        catch { }

        LanguageChanged?.Invoke(lang);
    }

    // Merges the XAML dictionary first, then JSON entries as per-key overrides; non en-US falls back to zh-CN.
    private static void ApplyDictionary(string lang)
    {
        var app = Application.Current;
        if (app == null) return;

        string xamlLang = string.Equals(lang, "en-US", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
        try
        {
            var uri = new Uri($"pack://application:,,,/Resources/Languages/{xamlLang}.xaml", UriKind.Absolute);
            var newDict = new ResourceDictionary { Source = uri };

            var oldDict = app.Resources.MergedDictionaries.FirstOrDefault(d => 
                d.Source != null && d.Source.OriginalString.Contains("/Languages/"));

            if (oldDict != null)
            {
                int index = app.Resources.MergedDictionaries.IndexOf(oldDict);
                app.Resources.MergedDictionaries[index] = newDict;
            }
            else
            {
                app.Resources.MergedDictionaries.Add(newDict);
            }
        }
        catch { }

        try
        {
            var dir = GetLocalesDir();
            var jsonPath = Path.Combine(dir, $"{lang}.json");
            if (File.Exists(jsonPath))
            {
                var text = File.ReadAllText(jsonPath, Encoding.UTF8);
                var node = JsonNode.Parse(text) as JsonObject;
                if (node != null)
                {
                    foreach (var prop in node)
                    {
                        if (prop.Key.StartsWith("_")) continue;
                        var valStr = prop.Value?.ToString();
                        if (!string.IsNullOrEmpty(valStr))
                        {
                            app.Resources[prop.Key] = valStr;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static void EnsureDefaultLocales()
    {
        try
        {
            var dir = GetLocalesDir();
            var enFile = Path.Combine(dir, "en-US.json");
            var zhFile = Path.Combine(dir, "zh-CN.json");

            var zhObj = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["name"] = "简体中文",
                    ["code"] = "zh-CN",
                    ["author"] = "Gravityless"
                },
                ["Antigravity"] = "Antigravity",
                ["File"] = "文件",
                ["View"] = "视图",
                ["Window"] = "窗口",
                ["New Conversation"] = "新建对话",
                ["New Chat"] = "新建对话",
                ["Conversation History"] = "对话历史",
                ["History"] = "历史记录",
                ["Automations"] = "自动化",
                ["Customizations"] = "自定义设置",
                ["Projects"] = "项目",
                ["No conversations yet"] = "暂无对话",
                ["Settings"] = "设置",
                ["Workspaces"] = "工作区",
                ["Extensions"] = "扩展",
                ["Rules"] = "规则",
                ["Skills"] = "技能",
                ["Plugins"] = "插件",
                ["Preferences"] = "首选项",
                ["Clear conversation"] = "清空对话",
                ["Send"] = "发送",
                ["Send message"] = "发送消息",
                ["Stop generating"] = "停止生成",
                ["Regenerate"] = "重新生成",
                ["Copy"] = "复制",
                ["Copied!"] = "已复制!",
                ["Apply"] = "应用",
                ["Discard"] = "放弃",
                ["Retry"] = "重试",
                ["Cancel"] = "取消",
                ["Save"] = "保存",
                ["Delete"] = "删除",
                ["Close"] = "关闭",
                ["Edit"] = "编辑",
                ["Select a model"] = "选择模型",
                ["Select Model"] = "选择模型",
                ["Model"] = "模型",
                ["Temperature"] = "采样温度",
                ["Max Tokens"] = "最大输出 Token",
                ["Ask anything, @ to mention, / for actions"] = "输入任何问题，使用 @ 提及，使用 / 执行操作",
                ["Type a message..."] = "输入消息...",
                ["Search"] = "搜索",
                ["Search models..."] = "搜索模型...",
                ["No workspaces found"] = "未找到工作区",
                ["Open Folder"] = "打开文件夹",
                ["Add Workspace"] = "添加工作区",
                ["Overview"] = "概览",
                ["Review"] = "审查",
                ["Files"] = "文件",
                ["Terminal"] = "终端",
                ["Goals"] = "目标",
                ["Tasks"] = "任务",
                ["Artifacts"] = "产物",
                ["Artifact"] = "产物",
                ["Scratch"] = "草稿",
                ["Chat"] = "对话",
                ["General"] = "常规",
                ["Application"] = "应用",
                ["Appearance"] = "外观",
                ["Theme"] = "主题",
                ["Light"] = "浅色",
                ["Dark"] = "深色",
                ["Language"] = "语言",
                ["Version"] = "版本",
                ["Check for Updates"] = "检查更新",
                ["About"] = "关于",
                ["Keybindings"] = "快捷键",
                ["Notifications"] = "通知",
                ["Shortcuts"] = "快捷键",
                ["Keyboard Shortcuts"] = "键盘快捷键",
                ["Global Shortcut"] = "全局快捷键",
                ["Toggle Visibility"] = "切换显示/隐藏",
                ["Proxy"] = "代理",
                ["Advanced"] = "高级",
                ["API Key"] = "API 密钥",
                ["Account"] = "账号",
                ["Profile"] = "个人资料",
                ["Logout"] = "退出登录",
                ["Sign Out"] = "退出登录",
                ["Feedback"] = "反馈",
                ["Give Feedback"] = "提供反馈",
                ["Privacy"] = "隐私",
                ["Default Model"] = "默认模型",
                ["System Prompt"] = "系统提示词",
                ["Search Engine"] = "搜索引擎",
                ["Web Search"] = "网页搜索",
                ["Enable Web Access"] = "启用网页访问",
                ["Hardware Acceleration"] = "硬件加速",
                ["Enable Telemetry"] = "启用遥测数据",
                ["Marketing Emails"] = "接收营销邮件",
                ["Your Plan:"] = "当前套餐计划:",
                ["Upgrade"] = "升级套餐",
                ["Email"] = "电子邮箱",
                ["Terms of Service"] = "服务条款",
                ["Global Permissions"] = "全局权限",
                ["Permission Preset"] = "权限预设",
                ["Tool Permissions"] = "工具权限",
                ["Network Access Rules"] = "网络访问规则",
                ["Agent Behavior"] = "智能体行为",
                ["Learn more"] = "了解详情",
                ["Manage your plan, credentials, and general preferences."] = "管理您的套餐计划、凭据以及常规首选项。",
                ["Open Settings"] = "打开设置",
                ["Start first conversation"] = "开始首次对话",
                ["Main Agent"] = "主智能体",
                ["Add Context"] = "添加上下文",
                ["Toggle Auxiliary Pane"] = "切换辅助面板",
                ["Maximize Pane"] = "最大化面板",
                ["Minimize Pane"] = "最小化面板",
                ["Restore Pane"] = "还原面板",
                ["Files Changed"] = "已修改文件",
                ["Uploads"] = "已上传文件",
                ["See all"] = "查看全部",
                ["Background Tasks"] = "后台任务",
                ["Terminals"] = "终端",
                ["Delete Terminal"] = "删除终端",
                ["Split Terminal"] = "拆分终端",
                ["Add Terminal"] = "添加终端",
                ["Command Palette"] = "命令面板",
                ["Zoom In"] = "放大",
                ["Zoom Out"] = "缩小",
                ["Reset Zoom"] = "重置缩放",
                ["Delete Conversation"] = "删除对话",
                ["Undo changes"] = "撤销更改",
                ["Redo changes"] = "重做更改",
                ["Record Audio"] = "录制音频",
                ["New Window"] = "新建窗口",
                ["Close Window"] = "关闭窗口",
                ["Select All"] = "全选",
                ["Quit Antigravity"] = "退出 Antigravity",
                ["About Antigravity"] = "关于 Antigravity",
                ["Services"] = "服务",
                ["Hide Antigravity"] = "隐藏 Antigravity",
                ["Hide Others"] = "隐藏其他",
                ["Show All"] = "显示全部",
                ["Force Reload"] = "强制重新加载",
                ["Reload"] = "重新加载",
                ["Actual Size"] = "实际大小",
                ["Toggle Full Screen"] = "切换全屏"
            };

            if (!File.Exists(zhFile))
            {
                File.WriteAllText(zhFile, zhObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }

            if (!File.Exists(enFile))
            {
                var enObj = new JsonObject
                {
                    ["_meta"] = new JsonObject
                    {
                        ["name"] = "English (US)",
                        ["code"] = "en-US",
                        ["author"] = "Google"
                    }
                };
                foreach (var prop in zhObj)
                {
                    if (!prop.Key.StartsWith("_"))
                    {
                        enObj[prop.Key] = prop.Key;
                    }
                }
                File.WriteAllText(enFile, enObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }
        catch { }
    }

    // Lookup order: merged resource dictionaries, then fallback, then the key itself.
    public static string GetString(string key, string fallback = "")
    {
        var app = Application.Current;
        if (app == null) return fallback;
        if (app.TryFindResource(key) is string str)
        {
            return str;
        }
        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    public static string GetFormatted(string key, params object[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }
}
