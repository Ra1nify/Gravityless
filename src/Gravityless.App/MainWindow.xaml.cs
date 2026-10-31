using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gravityless.App.Dialogs;
using Gravityless.App.Services;
using Gravityless.Core.Models;
using Gravityless.Core.Services;
using Wpf.Ui.Controls;
using MenuItem = Wpf.Ui.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Separator = System.Windows.Controls.Separator;
namespace Gravityless.App;
public partial class MainWindow
{
    private const string ProjectUrl = "https://github.com/Ra1nify/Gravityless";

    private readonly GravitylessDaemon _daemon;
    private readonly EngineService _engineService;
    private readonly AccountPoolService _accountPool;
    private readonly ProjectService _projectService;
    private readonly InstanceService _instanceService;
    private readonly ProxyService _proxyService;
    private readonly ObservableCollection<ProjectDisplayItem> _projectDisplayList = [];
    private readonly ObservableCollection<InstanceDisplayItem> _instanceDisplayList = [];
    private readonly ObservableCollection<AccountDisplayItem> _accountDisplayList = [];
    private readonly ToolService _toolService;
    private readonly ObservableCollection<SkillItem> _skillsDisplayList = [];
    private readonly ObservableCollection<PluginItem> _pluginsDisplayList = [];
    private readonly ObservableCollection<McpServerItem> _mcpDisplayList = [];
    private UpdateManifest? _currentManifest;
    private ProxyConfig _currentProxyConfig = new();
    private readonly string _proxyConfigFile;
    private bool _isInitializingTheme;
    private readonly TrayIconService _trayService;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
        ApplyAppIcons();
        ApplyLocalizedChrome();
        if (RepoUrlText != null) RepoUrlText.Text = ProjectUrl;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _proxyConfigFile = Path.Combine(baseDir, "data", "proxy_config.json");
        // Daemon starts before the services that depend on it.
        _daemon = new GravitylessDaemon(baseDir);
        _daemon.Language = LocalizationService.CurrentLanguage;
        _daemon.Start();
        // Lets App forward system accent changes to the injector.
        if (Application.Current is App app) app.Daemon = _daemon;
        ThemeService.Update(_daemon.MicaMaterial, _daemon.MicaOpacity);
        ThemeService.ApplyTheme(this);
        ApplyHostWindowOpacity(_daemon.MicaOpacity);
        // Shared state lives in the services; slots (instances) only reference it.
        _engineService = new EngineService(baseDir);
        _accountPool = new AccountPoolService(baseDir);
        _projectService = new ProjectService(baseDir);
        _instanceService = new InstanceService(_accountPool, _engineService, _daemon, _projectService, baseDir);
        _proxyService = new ProxyService();
        // The shared tool pool must live at <install dir>\shared, matching the InstanceService junctions.
        _toolService = new ToolService(baseDir);
        _instanceService.InstanceStateChanged += _ => Dispatcher.Invoke(() => { RefreshInstancesUi(); UpdateSidebarStatus(); });
        _projectService.ProjectStateChanged += _ => Dispatcher.Invoke(() => { RefreshProjectsUi(); UpdateSidebarStatus(); });
        ProjectsListView.ItemsSource = _projectDisplayList;
        InstancesListView.ItemsSource = _instanceDisplayList;
        AccountsListView.ItemsSource = _accountDisplayList;
        SkillsListView.ItemsSource = _skillsDisplayList;
        PluginsListView.ItemsSource = _pluginsDisplayList;
        McpListView.ItemsSource = _mcpDisplayList;
        LoadProxyConfig();
        UpdateSidebarStatus();
        RefreshEngineStatus();
        AccountPoolService.EnsureProtocolInterceptorRegistered();
        RefreshAccountsUi();
        RefreshProjectsUi();
        RefreshInstancesUi();
        RefreshToolsUi();
        // NavigationView can reset IsActive internally, so re-apply the initial selection.
        SwitchView("overview");
        Closing += OnWindowClosing;
        _trayService = new TrayIconService(this)
        {
            DoubleClickAction = ShowFromTray,
            ContextMenuProvider = BuildTrayContextMenu
        };
        Loaded += (_, _) =>
        {
            // A second launch broadcasts this message; restore from tray and focus instead of
            // letting the duplicate process create its own window.
            SingleInstance.Listen(this, ShowFromTray);

            // Guard suppresses persistence while controls are re-populated from saved state.
            _isInitializingTheme = true;
            try
            {
                var mat = _daemon.MicaMaterial;
                var opacity = _daemon.MicaOpacity;
                switch (_daemon.ThemeMode)
                {
                    case "light" when ThemeLightRadio != null:
                        ThemeLightRadio.IsChecked = true;
                        break;
                    case "dark" when ThemeDarkRadio != null:
                        ThemeDarkRadio.IsChecked = true;
                        break;
                    default:
                        if (ThemeAutoRadio != null) ThemeAutoRadio.IsChecked = true;
                        break;
                }
                // Tabbed backdrop needs Win11 22H2; disable the option where unsupported.
                if (TabbedRadioBtn != null && !ThemeService.SupportsTabbed)
                {
                    TabbedRadioBtn.IsEnabled = false;
                    TabbedRadioBtn.ToolTip = LocalizationService.GetString(
                        "Appearance_MaterialTabbedUnsupported", "需要 Windows 11 22H2 及以上版本");
                    if (mat == "tabbed") mat = "mica";
                }

                if (AcrylicRadioBtn != null && !ThemeService.SupportsAcrylic)
                {
                    AcrylicRadioBtn.IsEnabled = false;
                    AcrylicRadioBtn.ToolTip = LocalizationService.GetString(
                        "Appearance_MaterialAcrylicUnsupported", "需要 Windows 11 22H2 及以上版本");
                    if (mat == "acrylic") mat = "mica";
                }

                if (mat == "none" && NoneRadioBtn != null)
                {
                    NoneRadioBtn.IsChecked = true;
                    this.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
                }
                else if (mat == "acrylic" && AcrylicRadioBtn != null)
                {
                    AcrylicRadioBtn.IsChecked = true;
                    this.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Acrylic;
                }
                else if (mat == "tabbed" && TabbedRadioBtn != null)
                {
                    TabbedRadioBtn.IsChecked = true;
                    this.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Tabbed;
                }
                else if (MicaRadioBtn != null)
                {
                    MicaRadioBtn.IsChecked = true;
                    this.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
                }

                // Restore the new-UX skin selection by matching ComboBoxItem.Tag.
                if (NewUxThemeCombo != null)
                {
                    var want = _daemon.NewUxTheme ?? string.Empty;
                    foreach (var item in NewUxThemeCombo.Items.OfType<ComboBoxItem>())
                    {
                        if (string.Equals(item.Tag as string ?? string.Empty, want, StringComparison.OrdinalIgnoreCase))
                        {
                            NewUxThemeCombo.SelectedItem = item;
                            break;
                        }
                    }
                    if (NewUxThemeCombo.SelectedItem == null) NewUxThemeCombo.SelectedIndex = 0;
                }
                // Slider shows transparency: 0% = opaque, 100% = fully transparent.
                var transparency = Math.Clamp(1.0 - opacity, 0.00, 1.00);
                var popupTransparency = Math.Clamp(1.0 - _daemon.PopupOpacity, 0.00, 1.00);
                if (MicaOpacitySlider != null)
                {
                    MicaOpacitySlider.Value = transparency;
                }
                if (MicaOpacityLabel != null)
                {
                    MicaOpacityLabel.Text = $"{(int)Math.Round(transparency * 100)}%";
                }
                if (NoiseAlphaSlider != null)
                {
                    NoiseAlphaSlider.Value = Math.Clamp(_daemon.NoiseAlpha, 0.0, 1.0);
                }
                if (NoiseAlphaLabel != null)
                {
                    NoiseAlphaLabel.Text = $"{(int)Math.Round(_daemon.NoiseAlpha * 100)}%";
                }
                if (NoiseFreqSlider != null)
                {
                    NoiseFreqSlider.Value = Math.Clamp(_daemon.NoiseFrequency, 0.5, 2.0);
                }
                if (NoiseFreqLabel != null)
                {
                    NoiseFreqLabel.Text = _daemon.NoiseFrequency.ToString("F2");
                }
                if (NoiseTintSlider != null)
                {
                    NoiseTintSlider.Value = Math.Clamp(_daemon.NoiseTintRatio, 0.0, 1.0);
                }
                if (NoiseTintLabel != null)
                {
                    NoiseTintLabel.Text = $"{(int)Math.Round(_daemon.NoiseTintRatio * 100)}%";
                }
                if (NoiseBaseSlider != null)
                {
                    NoiseBaseSlider.Value = Math.Clamp(_daemon.NoiseBaseAlpha, 0.0, 1.0);
                }
                if (NoiseBaseLabel != null)
                {
                    NoiseBaseLabel.Text = $"{(int)Math.Round(_daemon.NoiseBaseAlpha * 100)}%";
                }
                if (AccentInteractionCheck != null)
                {
                    AccentInteractionCheck.IsChecked = _daemon.AccentInteraction;
                }
                ApplyHostWindowOpacity(opacity);
            }
            finally
            {
                _isInitializingTheme = false;
            }
            if (LanguageCombo != null)
            {
                LanguageCombo.ItemsSource = LocalizationService.AvailableLanguages;
                LanguageCombo.SelectedValue = LocalizationService.CurrentLanguage;
            }
            TriggerStartupUpdateCheck();
        };
        LocalizationService.AvailableLanguagesChanged += () => Dispatcher.Invoke(() =>
        {
            if (LanguageCombo != null && LanguageCombo.SelectedValue == null)
            {
                LanguageCombo.SelectedValue = LocalizationService.CurrentLanguage;
            }
        });
    }

    /// <summary>Window/taskbar icon comes from app.ico; the overview logo from brand.png.</summary>
    private void ApplyAppIcons()
    {
        try
        {
            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

            var appIco = Path.Combine(assetsDir, "app.ico");
            if (File.Exists(appIco))
            {
                using var stream = File.OpenRead(appIco);
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32)).FirstOrDefault();
                if (frame != null)
                {
                    Icon = frame;
                }
            }

            var brandPng = Path.Combine(assetsDir, "brand.png");
            if (File.Exists(brandPng))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(brandPng, UriKind.Absolute);
                bitmap.EndInit();

                if (AppLogoImage != null) AppLogoImage.Source = bitmap;
            }
        }
        catch
        {
            // Missing assets must not block startup.
        }
    }

    /// <summary>Re-applies localized window chrome; call again after a language switch.</summary>
    private void ApplyLocalizedChrome()
    {
        var version = GetAppVersion();

        try
        {
            MainTitleBar.Title = string.Format(
                LocalizationService.GetString("App_TitleBarFormat", "Gravityless v{0} - Yet Another Spicy Companion Of Antigravity"),
                version);
        }
        catch { }

        if (AppVersionText != null) AppVersionText.Text = "v" + version;
    }

    /// <summary>Version comes from the csproj &lt;Version&gt; via AssemblyInformationalVersion.</summary>
    private static string GetAppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            if (asm != null)
            {
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)
                    System.Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var info = attr?.InformationalVersion;

                if (!string.IsNullOrWhiteSpace(info))
                {
                    // Strip the "+<commit sha>" build metadata suffix.
                    var cut = info!.IndexOf('+');
                    return (cut > 0 ? info[..cut] : info).Trim();
                }

                var v = asm.GetName().Version;
                if (v != null)
                {
                    return $"{v.Major}.{v.Minor}.{v.Build}";
                }
            }

            return "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    private void OnOpenRepoClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ModernDialog.ShowError(LocalizationService.GetString("Common_Error", "错误"), ex.Message, this);
        }
    }

    private void UpdateSidebarStatus()
    {
        var managed = _engineService.DetectManagedEngine();
        var hasRunning = _instanceService.Instances.Any(i => i.IsRunning) || _projectService.Projects.Any(p => p.IsRunning);
        var hasUpdate = _currentManifest != null && managed.IsAvailable && !string.IsNullOrEmpty(_currentManifest.Version) && _currentManifest.Version != managed.Version;
        // Core badge has four states: not ready / running / update available / idle.
        if (!managed.IsAvailable)
        {
            CoreStatusDot.Foreground = Palette.Critical;
            SidebarCoreVersionText.Text = LocalizationService.GetString("Sidebar_CoreNotReady", "Core: 未就绪");
            SidebarCoreVersionText.Foreground = CoreStatusDot.Foreground;
            CoreStatusBadgeContainer.ToolTip = LocalizationService.GetString("Sidebar_CoreTooltipNotReady", "托管核心未就绪，请前往【概览】下载安装便携核心");
        }
        else if (hasRunning)
        {
            CoreStatusDot.Foreground = Palette.Accent;
            SidebarCoreVersionText.Text = string.Format(LocalizationService.GetString("Sidebar_CoreRunningFormat", "Core: v{0} (运行中)"), managed.Version);
            SidebarCoreVersionText.Foreground = CoreStatusDot.Foreground;
            CoreStatusBadgeContainer.ToolTip = string.Format(LocalizationService.GetString("Sidebar_CoreTooltipRunningFormat", "当前有活动实例或项目正在运行中 (Core v{0})"), managed.Version);
        }
        else if (hasUpdate)
        {
            CoreStatusDot.Foreground = Palette.Caution;
            SidebarCoreVersionText.Text = string.Format(LocalizationService.GetString("Sidebar_CoreUpdateFormat", "Core: v{0} (有新版)"), managed.Version);
            SidebarCoreVersionText.Foreground = CoreStatusDot.Foreground;
            CoreStatusBadgeContainer.ToolTip = string.Format(LocalizationService.GetString("Sidebar_CoreTooltipUpdateFormat", "官方检测到新版: v{0}，可前往【概览】升级"), _currentManifest?.Version);
        }
        else
        {
            CoreStatusDot.Foreground = Palette.Success;
            SidebarCoreVersionText.Text = string.Format(LocalizationService.GetString("Sidebar_CoreIdleFormat", "Core: v{0} (空闲)"), managed.Version);
            SidebarCoreVersionText.Foreground = CoreStatusDot.Foreground;
            CoreStatusBadgeContainer.ToolTip = string.Format(LocalizationService.GetString("Sidebar_CoreTooltipReadyFormat", "Antigravity 托管核心就绪 (Core v{0})，当前处于空闲状态"), managed.Version);
        }
        // Daemon badge has two states: active / offline.
        if (_daemon.IsRunning)
        {
            DaemonStatusDot.Foreground = Palette.Success;
            SidebarDaemonPortText.Text = string.Format(LocalizationService.GetString("Sidebar_DaemonActiveFormat", "Daemon: :{0} (活跃)"), _daemon.Port);
            DaemonStatusBadgeContainer.ToolTip = string.Format(LocalizationService.GetString("Sidebar_DaemonTooltipActiveFormat", "Gravityless 内嵌守护服务正常工作中 (127.0.0.1:{0})"), _daemon.Port);
        }
        else
        {
            DaemonStatusDot.Foreground = Palette.Critical;
            SidebarDaemonPortText.Text = LocalizationService.GetString("Sidebar_DaemonOffline", "Daemon: 离线");
            DaemonStatusBadgeContainer.ToolTip = LocalizationService.GetString("Sidebar_DaemonTooltipOffline", "守护服务未能正常监听，外联样式与热重载可能不可用");
        }
    }

    private void OnNavItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            SwitchView(tag);
        }
    }

    private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
    {
        if (RootNav != null)
        {
            RootNav.IsPaneOpen = !RootNav.IsPaneOpen;
        }
    }

    private void SwitchView(string tag)
    {
        OverviewView.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        InstancesView.Visibility = tag == "instances" ? Visibility.Visible : Visibility.Collapsed;
        ProjectsView.Visibility = tag == "projects" ? Visibility.Visible : Visibility.Collapsed;
        ToolsView.Visibility = tag == "tools" ? Visibility.Visible : Visibility.Collapsed;
        ProxyView.Visibility = tag == "proxy" ? Visibility.Visible : Visibility.Collapsed;
        AccountsView.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceView.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "tools")
        {
            RefreshToolsUi();
        }

        // WPF-UI only fills SymbolIcon inside its own Activate() and only in LeftFluent mode; mirror it here.
        try
        {
            foreach (var navItem in RootNav.MenuItems.OfType<NavigationViewItem>())
            {
                navItem.IsActive = string.Equals(navItem.Tag as string, tag, StringComparison.OrdinalIgnoreCase);

                if (navItem.Icon is SymbolIcon icon)
                {
                    icon.Filled = navItem.IsActive;
                }
            }
        }
        catch { }
    }

    private void LoadProxyConfig()
    {
        if (File.Exists(_proxyConfigFile))
        {
            try
            {
                var json = File.ReadAllText(_proxyConfigFile);
                _currentProxyConfig = JsonSerializer.Deserialize<ProxyConfig>(json) ?? new ProxyConfig();
            }
            catch
            {
                _currentProxyConfig = new ProxyConfig();
            }
        }
        ProxyDirectRadio.IsChecked = _currentProxyConfig.Mode == ProxyMode.Direct;
        ProxySystemRadio.IsChecked = _currentProxyConfig.Mode == ProxyMode.System;
        ProxyManualRadio.IsChecked = _currentProxyConfig.Mode == ProxyMode.Manual;
        // Combo order: SOCKS5, HTTP, HTTPS.
        ProxyProtocolCombo.SelectedIndex = _currentProxyConfig.Protocol switch
        {
            ProxyProtocol.Http => 1,
            ProxyProtocol.Https => 2,
            _ => 0
        };
        ProxyHostBox.Text = _currentProxyConfig.Host;
        ProxyPortBox.Text = _currentProxyConfig.Port.ToString();
        ProxyBypassBox.Text = _currentProxyConfig.BypassList;
        ManualProxyCard.Visibility = _currentProxyConfig.Mode == ProxyMode.Manual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnProxyModeRadioChanged(object sender, RoutedEventArgs e)
    {
        if (ManualProxyCard == null) return;
        ManualProxyCard.Visibility = ProxyManualRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveCurrentProxyConfigFromUi()
    {
        _currentProxyConfig.Mode = ProxyDirectRadio.IsChecked == true 
            ? ProxyMode.Direct 
            : ProxySystemRadio.IsChecked == true 
                ? ProxyMode.System 
                : ProxyMode.Manual;
        _currentProxyConfig.Protocol = ProxyProtocolCombo.SelectedIndex switch
        {
            1 => ProxyProtocol.Http,
            2 => ProxyProtocol.Https,
            _ => ProxyProtocol.Socks5
        };
        _currentProxyConfig.Host = ProxyHostBox.Text.Trim();
        if (int.TryParse(ProxyPortBox.Text.Trim(), out var port))
        {
            _currentProxyConfig.Port = port;
        }
        _currentProxyConfig.BypassList = ProxyBypassBox.Text.Trim();
        var dir = Path.GetDirectoryName(_proxyConfigFile)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(_currentProxyConfig, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_proxyConfigFile, json);
    }

    private void RefreshEngineStatus()
    {
        var managed = _engineService.DetectManagedEngine();
        UpdateSidebarStatus();
        if (managed.IsAvailable)
        {
            // Badge background is the status color at ~9% alpha.
            CoreStatusBadgeBorder.Background = Palette.WithAlpha(Palette.Success, 0x18);
            CoreStatusBadgeText.Text = LocalizationService.GetString("Overview_CoreReady", "就绪");
            CoreStatusBadgeText.Foreground = Palette.Success;
            ManagedCoreVersionText.Text = $"v{managed.Version}";
            ManagedCorePathText.Text = managed.ExecutablePath;
        }
        else
        {
            CoreStatusBadgeBorder.Background = Palette.WithAlpha(Palette.Critical, 0x18);
            CoreStatusBadgeText.Text = LocalizationService.GetString("Overview_CoreNotReady", "未就绪");
            CoreStatusBadgeText.Foreground = Palette.Critical;
            ManagedCoreVersionText.Text = LocalizationService.GetString("Overview_CoreNotInstalled", "未安装托管便携核心");
            ManagedCorePathText.Text = "-";
        }
        if (UninstallCoreButton != null)
        {
            UninstallCoreButton.IsEnabled = managed.IsAvailable;
        }
    }

    private void OnCorePathRowClicked(object sender, RoutedEventArgs e)
    {
        var managed = _engineService.DetectManagedEngine();
        var dir = Path.GetDirectoryName(managed.ExecutablePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                Process.Start("explorer.exe", dir);
            }
            catch { }
        }
    }

    private void RefreshAccountsUi()
    {
        _accountDisplayList.Clear();
        foreach (var acc in _accountPool.Accounts)
        {
            var boundInst = !string.IsNullOrEmpty(acc.BoundInstanceId)
                ? _instanceService.Instances.FirstOrDefault(i => i.Id == acc.BoundInstanceId)
                : null;
            var boundSubtitle = boundInst != null 
                ? string.Format(LocalizationService.GetString("Accounts_BoundInstanceFormat", "绑定实例: {0}"), boundInst.DisplayNameWithShortId) 
                : (!string.IsNullOrEmpty(acc.BoundInstanceId) ? string.Format(LocalizationService.GetString("Accounts_BoundInstanceIdFormat", "绑定实例 ID: {0}"), acc.BoundInstanceId) : "");
            string quotaText = acc.QuotaCardSummaryText;
            _accountDisplayList.Add(new AccountDisplayItem
            {
                Id = acc.Id,
                Email = acc.Email,
                DisplayName = acc.DisplayName,
                PrimaryName = !string.IsNullOrWhiteSpace(acc.DisplayName) ? acc.DisplayName : acc.Email,
                InstallationId = acc.InstallationId,
                IsRunning = acc.IsRunning,
                StatusText = acc.IsRunning 
                    ? (boundInst != null ? string.Format(LocalizationService.GetString("Accounts_StatusInUseFormat", "占用中 ({0})"), boundInst.Name) : LocalizationService.GetString("Accounts_StatusInUse", "占用中")) 
                    : LocalizationService.GetString("Accounts_StatusIdle", "闲置可用"),
                BoundInstanceSubtitle = boundSubtitle,
                ShowBoundInstanceSubtitle = !string.IsNullOrEmpty(boundSubtitle),
                QuotaSummaryText = quotaText,
                ShowQuotaSummary = !string.IsNullOrEmpty(quotaText)
            });
        }
    }

    private void RefreshProjectsUi()
    {
        _projectDisplayList.Clear();
        var allInstances = _instanceService.Instances.ToList();

        // Antigravity keeps project-less chats outside any project. Surface them first so the
        // distinction stays visible.
        if (_projectService.HasLooseChats())
        {
            var loose = _projectService.CreateLooseChatsProfile();
            loose.Name = LocalizationService.GetString("Projects_LooseChatsName", "无项目聊天");
            _projectDisplayList.Add(new ProjectDisplayItem(loose, allInstances));
        }

        foreach (var proj in _projectService.Projects)
        {
            _projectDisplayList.Add(new ProjectDisplayItem(proj, allInstances));
        }
    }

    private void RefreshInstancesUi()
    {
        _instanceDisplayList.Clear();
        var accountsList = _accountPool.Accounts.ToList();
        var projectsList = _projectService.Projects;
        foreach (var inst in _instanceService.Instances)
        {
            _instanceDisplayList.Add(new InstanceDisplayItem(inst, accountsList, projectsList));
        }
    }

    private async void OnImportFromGeminiClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("Dialogs_SelectWorkspaceTitle", "选择工作区目录"),
            Multiselect = false
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ProjectsStatusStrip.ShowBusy(LocalizationService.GetString("Projects_ImportingStatus", "正在导入记忆库..."));
            // Import is synchronous; yield so the busy state renders before the UI thread blocks.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var importDir = ProjectService.NormalizeWorkspacePath(dialog.FolderName);
            if (string.IsNullOrEmpty(importDir))
            {
                ProjectsStatusStrip.Clear();
                ModernDialog.ShowWarning(
                    LocalizationService.GetString("Dialogs_InvalidPathTitle", "路径无效"),
                    string.Format(LocalizationService.GetString("Dialogs_InvalidPathFormat",
                        "无法解析为本地文件夹路径：{0}"), dialog.FolderName),
                    owner: this);
                return;
            }
            // Copying a memory store touches thousands of files; keep it off the dispatcher.
            var (importOk, importMsg) = await Task.Run(() =>
            {
                var ok = _projectService.ImportFromGeminiDir(importDir, out _, out _, out var message);
                return (ok, message);
            });

            if (importOk)
            {
                ProjectsStatusStrip.Clear();
                RefreshProjectsUi();
                UpdateSidebarStatus();
                ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_ImportDoneTitle", "已导入"), importMsg, owner: this);
            }
            else
            {
                ProjectsStatusStrip.Clear();
                ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_ImportNoticeTitle", "导入提示"), importMsg, owner: this);
            }
        }
    }

    private void OnRelocateProjectPathClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            if (ProjectService.IsLooseChats(projId)) return;
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = string.Format(LocalizationService.GetString("Dialogs_RelocateTitleFormat", "重新指定「{0}」的代码目录"), proj.Name),
                Multiselect = false
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                var resolved = ProjectService.NormalizeWorkspacePath(dialog.FolderName);
                if (string.IsNullOrEmpty(resolved))
                {
                    ModernDialog.ShowWarning(
                        LocalizationService.GetString("Dialogs_InvalidPathTitle", "路径无效"),
                        string.Format(LocalizationService.GetString("Dialogs_InvalidPathFormat",
                            "无法解析为本地文件夹路径：{0}"), dialog.FolderName),
                        owner: this);
                    return;
                }
                proj.Path = resolved;
                _projectService.SaveProjects();
                RefreshProjectsUi();
                ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_RelocateDoneTitle", "路径已更新"), string.Format(LocalizationService.GetString("Dialogs_RelocateDoneFormat", "「{0}」的代码目录已更新:\n{1}"), proj.Name, proj.Path), owner: this);
            }
        }
    }

    private void OnRefreshProjectsClicked(object sender, RoutedEventArgs e)
    {
        RefreshProjectsUi();
        RefreshAccountsUi();
        UpdateSidebarStatus();
    }

    private async void OnOpenProjectConversationsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            var dlg = new ProjectConversationsDialog(_projectService, proj, this);
            dlg.ShowDialog();

            // Recount after the dialog: the query hits SQLite and can be slow on large stores.
            await Task.Run(() => _projectService.RefreshProjectMemoryStats(proj));
            RefreshProjectsUi();
        }
    }

    private void OnOpenProjectActionsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            if (ProjectService.IsLooseChats(projId)) return;
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            var dlg = new ProjectActionsDialog(_projectService, _instanceService, proj, this);
            dlg.ShowDialog();
            if (dlg.HasModified)
            {
                RefreshProjectsUi();
                RefreshInstancesUi();
            }
        }
    }

    private void OnPreviewProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            var dlg = new ConversationPreviewDialog(_projectService, proj, null, this)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }
    }

    private async void OnDuplicateProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            if (ProjectService.IsLooseChats(projId)) return;
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            var newName = ModernDialog.Prompt(
                LocalizationService.GetString("Dialogs_CloneTitle", "克隆项目"), 
                string.Format(LocalizationService.GetString("Dialogs_ClonePromptFormat", "复制「{0}」及其记忆库。\n输入新项目名称:"), proj.Name), 
                string.Format(LocalizationService.GetString("Dialogs_CloneDefaultNameFormat", "{0} 副本"), proj.Name), 
                owner: this);
            if (string.IsNullOrWhiteSpace(newName)) return;
            ProjectsStatusStrip.ShowBusy(string.Format(LocalizationService.GetString("Projects_CloningStatusFormat", "复制「{0}」…"), proj.Name));
            // Deep copy touches every file in the memory store; keep it off the dispatcher.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var cloneName = newName.Trim();
            var (clone, cloneErr) = await Task.Run(() =>
            {
                var c = _projectService.DuplicateProject(projId, cloneName, out var e);
                return (c, e);
            });
            var err = cloneErr;
            ProjectsStatusStrip.Clear();
            if (clone != null)
            {
                RefreshProjectsUi();
                ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_CloneDoneTitle", "克隆完成"), string.Format(LocalizationService.GetString("Dialogs_CloneDoneFormat", "已创建「{0}」。"), clone.Name), owner: this);
            }
            else
            {
                ModernDialog.ShowError(LocalizationService.GetString("Dialogs_CloneFailedTitle", "克隆失败"), err, owner: this);
            }
        }
    }

    private void OnRenameProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            if (ProjectService.IsLooseChats(projId)) return;
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            var newName = ModernDialog.Prompt(
                LocalizationService.GetString("Dialogs_RenameTitle", "重命名项目"), 
                string.Format(LocalizationService.GetString("Dialogs_RenamePromptFormat", "输入「{0}」的新名称:"), proj.Name), 
                proj.Name, 
                owner: this);
            if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == proj.Name) return;
            if (_projectService.RenameProject(projId, newName.Trim(), out var err))
            {
                RefreshProjectsUi();
                RefreshInstancesUi();
            }
            else
            {
                ModernDialog.ShowError(LocalizationService.GetString("Dialogs_RenameFailedTitle", "重命名失败"), err, owner: this);
            }
        }
    }

    private void OnOpenProjectMemoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            var memDir = _projectService.GetProjectMemoryDir(projId);
            Directory.CreateDirectory(memDir);
            try
            {
                Process.Start("explorer.exe", memDir);
            }
            catch (Exception ex)
            {
                ModernDialog.ShowError(
                    LocalizationService.GetString("Common_Error", "错误"),
                    string.Format(LocalizationService.GetString("Projects_OpenMemoryFailedFormat", "打开记忆目录失败: {0}"), ex.Message),
                    this);
            }
        }
    }

    private void OnOpenProjectFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            var proj = _projectService.FindProject(projId);
            if (proj != null && Directory.Exists(proj.Path))
            {
                try
                {
                    Process.Start("explorer.exe", proj.Path);
                }
                catch (Exception ex)
                {
                    ModernDialog.ShowError(
                        LocalizationService.GetString("Common_Error", "错误"),
                        string.Format(LocalizationService.GetString("Projects_OpenCodeFailedFormat", "打开代码目录失败: {0}"), ex.Message),
                        this);
                }
            }
        }
    }

    private async void OnDeleteProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string projId })
        {
            if (ProjectService.IsLooseChats(projId)) return;
            var proj = _projectService.FindProject(projId);
            if (proj == null) return;
            if (proj.IsRunning)
            {
                ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_CannotDeleteTitle", "无法删除"), string.Format(LocalizationService.GetString("Dialogs_CannotDeleteRunningFormat", "「{0}」正在运行 (PID: {1})，请先停止。"), proj.Name, proj.ProcessId), owner: this);
                return;
            }
            if (ModernDialog.Confirm(LocalizationService.GetString("Dialogs_RemoveProjectTitle", "移除项目"), string.Format(LocalizationService.GetString("Dialogs_RemoveProjectMessageFormat", "移除「{0}」？记忆库和会话将被清理，源码保留。"), proj.Name), isDestructive: true, owner: this))
            {
                // Deleting a memory store can involve thousands of files.
                var (deleted, delErr) = await Task.Run(() =>
                {
                    var ok = _projectService.DeleteProject(projId, out var e);
                    return (ok, e);
                });

                if (deleted)
                {
                    // Drop dangling references left on instances.
                    foreach (var inst in _instanceService.Instances)
                    {
                        inst.BoundProjectIds.Remove(projId);
                    }
                    _instanceService.TrySaveInstances();
                    RefreshProjectsUi();
                    RefreshInstancesUi();
                    UpdateSidebarStatus();

                    if (!string.IsNullOrEmpty(delErr))
                    {
                        ModernDialog.ShowWarning(LocalizationService.GetString("Common_Notice", "提示"), delErr, owner: this);
                    }
                }
                else
                {
                    ModernDialog.ShowError(LocalizationService.GetString("Dialogs_DeleteFailedTitle", "删除失败"), delErr, owner: this);
                }
            }
        }
    }

    private void OnCreateInstanceClicked(object sender, RoutedEventArgs e)
    {
        var count = _instanceService.Instances.Count + 1;
        var firstFreeAcc = _accountPool.Accounts.FirstOrDefault(a => !a.IsRunning);
        var newInst = new InstanceProfile
        {
            Name = string.Format(LocalizationService.GetString("Instances_DefaultNameFormat", "实例 #{0}"), count),
            BoundAccountId = firstFreeAcc?.Id
        };
        var dialog = new InstanceConfigDialog(newInst, _accountPool.Accounts.ToList(), "", isCreateMode: true, availableProjects: _projectService.Projects.ToList())
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Saved)
        {
            _instanceService.AddInstance(newInst);
            RefreshInstancesUi();
            RefreshAccountsUi();
            UpdateSidebarStatus();
        }
    }

    private void OnConfigureInstanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string instId })
        {
            var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instId);
            if (inst == null) return;
            var sandboxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "instances", inst.Id);
            var dialog = new InstanceConfigDialog(inst, _accountPool.Accounts.ToList(), sandboxDir, isCreateMode: false, availableProjects: _projectService.Projects.ToList())
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true && dialog.Saved)
            {
                _instanceService.SaveInstances();
                RefreshInstancesUi();
                RefreshAccountsUi();
                UpdateSidebarStatus();
            }
        }
    }

    private void OnRefreshInstancesClicked(object sender, RoutedEventArgs e)
    {
        RefreshInstancesUi();
        RefreshAccountsUi();
        UpdateSidebarStatus();
    }

    private void OnStopAllInstancesClicked(object sender, RoutedEventArgs e)
    {
        foreach (var inst in _instanceService.Instances.Where(i => i.IsRunning))
        {
            _instanceService.StopInstance(inst.Id);
        }
    }

    private void StartInstanceById(string instId)
    {
        SaveCurrentProxyConfigFromUi();

        // Launching an account that differs from whoever holds the shared credential slot can
        // leave both instances refreshing against the wrong token. Confirm before rotating.
        var displaced = _instanceService.GetCredentialConflictEmail(instId);
        if (displaced != null)
        {
            var target = _instanceService.Instances.FirstOrDefault(i => i.Id == instId);
            var targetEmail = _accountPool.Accounts
                .FirstOrDefault(a => a.Id == target?.BoundAccountId)?.Email ?? string.Empty;

            var warnMsg = string.Format(
                LocalizationService.GetString("Instances_CredentialConflictMessageFormat",
                    "当前凭证槽位由 {0} 占用。\n启动本实例会写入 {1} 的凭证，可能扰乱正在运行实例的登录状态。\n\n确认启动即视为接受该风险。"),
                displaced, targetEmail);

            if (!ModernDialog.Confirm(
                    LocalizationService.GetString("Instances_CredentialConflictTitle", "凭证槽位冲突"),
                    warnMsg,
                    confirmText: LocalizationService.GetString("Instances_CredentialConflictBtn", "确认启动"),
                    cancelText: LocalizationService.GetString("Common_Cancel", "取消"),
                    isDestructive: true,
                    owner: this.IsVisible ? this : null))
            {
                return;
            }
        }

        // Projects held by another live instance cannot be mounted twice: the shared SQLite
        // conversation store would be written concurrently. Offer to skip them instead of
        // failing the launch outright.
        IReadOnlyCollection<string>? excludedProjects = null;
        var projectConflicts = _instanceService.FindProjectConflicts(instId);
        if (projectConflicts.Count > 0)
        {
            var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instId);
            var totalBound = inst?.BoundProjectIds.Count ?? 0;
            var allBusy = projectConflicts.Count >= totalBound;

            // One project per line: the list is the only variable-height part of the dialog.
            var lines = string.Join("\n", projectConflicts.Select(c =>
                string.Format(
                    LocalizationService.GetString("Instances_ProjectConflictLineFormat", "{0} — 被 {1} 使用中"),
                    c.ProjectName, c.InstanceName)));

            var titleKey = allBusy ? "Instances_AllProjectsBusyTitle" : "Instances_ProjectConflictTitle";
            var bodyKey = allBusy ? "Instances_AllProjectsBusyMessageFormat" : "Instances_ProjectConflictMessageFormat";
            var buttonKey = allBusy ? "Instances_AllProjectsBusyBtn" : "Instances_ProjectConflictBtn";

            var conflictMsg = string.Format(
                LocalizationService.GetString(bodyKey, string.Empty),
                lines);

            if (!ModernDialog.Confirm(
                    LocalizationService.GetString(titleKey, "项目被占用"),
                    conflictMsg,
                    confirmText: LocalizationService.GetString(buttonKey, "跳过并启动"),
                    cancelText: LocalizationService.GetString("Common_Cancel", "取消"),
                    isDestructive: false,
                    owner: this.IsVisible ? this : null))
            {
                return;
            }

            excludedProjects = projectConflicts.Select(c => c.ProjectId).ToList();
        }

        // A running instance behind the same slot reports conflict; force relaunch is confirmed with the user.
        if (!_instanceService.LaunchInstance(instId, _currentProxyConfig, out var err, force: false, excludedProjects, out var conflict, out var pid))
        {
            if (conflict)
            {
                var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instId);
                var instName = inst?.Name ?? instId;
                var riskMsg = string.Format(
                    LocalizationService.GetString("Instances_ForceRestartMessageFormat",
                        "「{0}」正在运行 (PID: {1})。\n强行重启将终止该进程，未保存的内容可能丢失。\n继续？"),
                    instName, pid);
                if (ModernDialog.Confirm(LocalizationService.GetString("Instances_ForceRestartTitle", "实例互斥"), riskMsg, confirmText: LocalizationService.GetString("Instances_ForceRestartBtn", "强行重启"), cancelText: LocalizationService.GetString("Common_Cancel", "取消"), isDestructive: true, owner: this.IsVisible ? this : null))
                {
                    if (!_instanceService.LaunchInstance(instId, _currentProxyConfig, out var forceErr, force: true, excludedProjects, out _, out _))
                    {
                        ModernDialog.ShowError(LocalizationService.GetString("Dialogs_StartErrorTitle", "启动失败"), string.Format(LocalizationService.GetString("Dialogs_ForceStartFailedFormat", "强行接管失败: {0}"), forceErr), owner: this.IsVisible ? this : null);
                    }
                    else
                    {
                        RefreshInstancesUi();
                        RefreshAccountsUi();
                        UpdateSidebarStatus();
                    }
                }
            }
            else
            {
                ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_StartInstanceFailedTitle", "启动实例失败"), err, owner: this.IsVisible ? this : null);
            }
        }
        else
        {
            RefreshInstancesUi();
            RefreshAccountsUi();
            UpdateSidebarStatus();
        }
    }

    private void OnStartInstanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string instId })
        {
            StartInstanceById(instId);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Close means hide to tray unless the user chose Exit.
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _daemon.Dispose();
        _trayService?.Dispose();
        _engineService.Dispose();
    }

    private void ShowFromTray()
    {
        // The activation message arrives on whichever thread posted it, so marshal to the UI thread.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ShowFromTray);
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();

        // Activate() alone is routinely refused when another app owns the foreground, so the
        // window is also raised directly through Win32.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeWindow.Restore(handle);
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static class NativeWindow
    {
        private const int SwRestore = 9;
        private const int SwShow = 5;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static void Restore(IntPtr hWnd)
        {
            ShowWindow(hWnd, SwRestore);
            ShowWindow(hWnd, SwShow);
            SetForegroundWindow(hWnd);
        }
    }

    private ContextMenu BuildTrayContextMenu()
    {
        var menu = new ContextMenu();
        var showItem = new MenuItem
        {
            Header = LocalizationService.GetString("Tray_ShowMainWindow", "展示主界面"),
            FontWeight = FontWeights.Bold,
            Icon = new SymbolIcon
            {
                Symbol = SymbolRegular.Window24,
                Foreground = Palette.Accent
            }
        };
        showItem.Click += (_, _) => ShowFromTray();
        menu.Items.Add(showItem);
        menu.Items.Add(new Separator());
        // Up to three instances, running ones first, then most recently started.
        var recentInstances = _instanceService.Instances
            .OrderByDescending(i => i.IsRunning)
            .ThenByDescending(i => i.StartedAt ?? DateTime.MinValue)
            .Take(3)
            .ToList();
        if (recentInstances.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = LocalizationService.GetString("Tray_NoInstances", "暂无可用实例"),
                IsEnabled = false
            };
            menu.Items.Add(emptyItem);
        }
        else
        {
            var recentItem = new MenuItem
            {
                Header = LocalizationService.GetString("Tray_RecentInstances", "最近实例"),
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold
            };
            menu.Items.Add(recentItem);
            foreach (var inst in recentInstances)
            {
                var isRunning = inst.IsRunning;
                var statusText = isRunning
                    ? (inst.ProcessId.HasValue
                        ? string.Format(LocalizationService.GetString("Tray_StatusRunningWithPid", "运行中 · PID {0}"), inst.ProcessId)
                        : LocalizationService.GetString("Tray_StatusRunning", "运行中"))
                    : LocalizationService.GetString("Tray_StatusIdle", "闲置");
                var instItem = new MenuItem
                {
                    Header = $"{inst.Name} ({statusText})",
                    Icon = new SymbolIcon
                    {
                        Symbol = isRunning ? SymbolRegular.PlayCircle24 : SymbolRegular.Circle24,
                        Foreground = isRunning 
                            ? Palette.Success 
                            : Palette.Neutral
                    }
                };
                if (isRunning)
                {
                    var stopSubItem = new MenuItem
                    {
                        Header = LocalizationService.GetString("Tray_StopThisInstance", "停止此实例"),
                        Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24, Foreground = Palette.Critical }
                    };
                    stopSubItem.Click += (_, _) =>
                    {
                        _instanceService.StopInstance(inst.Id);
                        RefreshInstancesUi();
                        UpdateSidebarStatus();
                    };
                    instItem.Items.Add(stopSubItem);
                }
                else
                {
                    var startSubItem = new MenuItem
                    {
                        Header = LocalizationService.GetString("Tray_StartThisInstance", "启动此实例"),
                        Icon = new SymbolIcon { Symbol = SymbolRegular.Play24, Foreground = Palette.Accent }
                    };
                    startSubItem.Click += (_, _) => StartInstanceById(inst.Id);
                    instItem.Items.Add(startSubItem);
                }
                var openSandboxSubItem = new MenuItem
                {
                    Header = LocalizationService.GetString("Tray_OpenSandboxFolder", "打开沙盒目录"),
                    Icon = new SymbolIcon { Symbol = SymbolRegular.FolderOpen24 }
                };
                openSandboxSubItem.Click += (_, _) =>
                {
                    var sDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "instances", inst.Id);
                    Directory.CreateDirectory(sDir);
                    try { Process.Start(new ProcessStartInfo("explorer.exe", sDir) { UseShellExecute = true }); } catch { }
                };
                instItem.Items.Add(openSandboxSubItem);
                menu.Items.Add(instItem);
            }
        }
        menu.Items.Add(new Separator());
        bool anyRunning = _instanceService.Instances.Any(i => i.IsRunning);
        var stopAllItem = new MenuItem
        {
            Header = LocalizationService.GetString("Tray_StopAllInstances", "停止所有实例"),
            IsEnabled = anyRunning,
            Icon = new SymbolIcon
            {
                Symbol = SymbolRegular.Stop24,
                Foreground = anyRunning 
                    ? Palette.Critical 
                    : Palette.Neutral
            }
        };
        stopAllItem.Click += (_, _) =>
        {
            OnStopAllInstancesClicked(this, new RoutedEventArgs());
        };
        menu.Items.Add(stopAllItem);
        menu.Items.Add(new Separator());
        var exitItem = new MenuItem
        {
            Header = LocalizationService.GetString("Tray_Exit", "退出 Gravityless"),
            Icon = new SymbolIcon
            {
                Symbol = SymbolRegular.Power24,
                Foreground = Palette.Critical
            }
        };
        exitItem.Click += (_, _) => PromptExitApplication();
        menu.Items.Add(exitItem);
        return menu;
    }

    private void PromptExitApplication()
    {
        int runningCount = _instanceService.Instances.Count(i => i.IsRunning);
        string confirmMsg = runningCount > 0
            ? string.Format(
                LocalizationService.GetString(
                    "Tray_ExitConfirmMessageWithCount",
                    "有 {0} 个实例正在运行。退出将一并停止。是否继续？"),
                runningCount)
            : LocalizationService.GetString(
                "Tray_ExitConfirmMessage",
                "退出将停止所有实例。是否继续？");
        bool confirmed = ModernDialog.Confirm(
            LocalizationService.GetString("Tray_ExitConfirmTitle", "确认退出 Gravityless"),
            confirmMsg,
            confirmText: LocalizationService.GetString("Common_Confirm", "彻底退出"),
            cancelText: LocalizationService.GetString("Common_Cancel", "取消"),
            isDestructive: true,
            owner: this.IsVisible ? this : null);
        if (confirmed)
        {
            // Flag first so OnWindowClosing tears down instead of hiding.
            _isExplicitExit = true;
            _trayService?.Dispose();
            foreach (var inst in _instanceService.Instances.Where(i => i.IsRunning))
            {
                _instanceService.StopInstance(inst.Id);
            }
            _daemon.Dispose();
            _engineService.Dispose();
            Application.Current.Shutdown();
        }
    }

    private void OnStopInstanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string instId })
        {
            _instanceService.StopInstance(instId);
        }
    }

    private void OnDeleteInstanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string instId })
        {
            if (ModernDialog.Confirm(
                    LocalizationService.GetString("Instances_DeleteConfirmTitle", "确认删除"),
                    LocalizationService.GetString("Instances_DeleteConfirmMessage", "确定要删除该实例配置及其独立沙盒目录吗？"),
                    isDestructive: true,
                    owner: this))
            {
                _instanceService.DeleteInstance(instId);
                RefreshInstancesUi();
                RefreshAccountsUi();
                UpdateSidebarStatus();
            }
        }
    }

    private async void OnCaptureAccountClicked(object sender, RoutedEventArgs e)
    {
        var exePath = _engineService.GetManagedExePath();
        if (!File.Exists(exePath))
        {
            ModernDialog.ShowWarning(
                LocalizationService.GetString("Accounts_MissingCoreTitle", "缺少核心"),
                LocalizationService.GetString("Accounts_MissingCoreMessage", "请先在「概览」安装核心。"),
                this);
            return;
        }
        SaveCurrentProxyConfigFromUi();
        AccountsStatusStrip.ShowStep(1, 3, LocalizationService.GetString("Accounts_CaptureStage1", "正在启动登录窗口..."));
        try
        {
            // Progress callback fires off the UI thread, so marshal each stage update.
            var newAccount = await _accountPool.CaptureNewAccountAsync(
                exePath, 
                _currentProxyConfig,
                stage => Dispatcher.Invoke(() => AccountsStatusStrip.ShowStep(stage, 3, LocalizationService.GetString($"Accounts_CaptureStage{stage}", ""))));
            if (newAccount != null)
            {
                ModernDialog.ShowSuccess(
                    LocalizationService.GetString("Accounts_CapturedTitle", "录入成功"),
                    string.Format(LocalizationService.GetString("Accounts_CapturedFormat", "已录入账号 [{0}]。"), newAccount.Email),
                    this);
                RefreshAccountsUi();
                RefreshProjectsUi();
                RefreshInstancesUi();
            }
            else
            {
                ModernDialog.ShowWarning(
                    LocalizationService.GetString("Accounts_CaptureCancelledTitle", "录入取消"),
                    LocalizationService.GetString("Accounts_CaptureCancelledMessage", "未能完成授权，可能登录未完成或窗口被中途关闭。"),
                    this);
            }
        }
        catch (Exception ex)
        {
            ModernDialog.ShowError(
                LocalizationService.GetString("Common_Error", "错误"),
                string.Format(LocalizationService.GetString("Accounts_CaptureFailedFormat", "录入异常: {0}"), ex.Message),
                this);
        }
        finally
        {
            RefreshEngineStatus();
            AccountsStatusStrip.Clear();
        }
    }

    private void OnAccountDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string accId })
        {
            var acc = _accountPool.Accounts.FirstOrDefault(a => a.Id == accId);
            if (acc == null) return;
            SaveCurrentProxyConfigFromUi();
            var dlg = new AccountDetailsDialog(acc, _accountPool, _instanceService, _currentProxyConfig)
            {
                Owner = this
            };
            dlg.ShowDialog();
            RefreshAccountsUi();
        }
    }

    private async void OnRefreshTokenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string accId })
        {
            SaveCurrentProxyConfigFromUi();
            AccountsStatusStrip.ShowBusy(LocalizationService.GetString("Accounts_RefreshStatus", "刷新 Token…"));
            try
            {
                var (success, msg) = await _accountPool.RefreshTokenAsync(accId, _currentProxyConfig);
                if (success)
                {
                    ModernDialog.ShowSuccess(LocalizationService.GetString("Accounts_RefreshDoneTitle", "已刷新"), msg, owner: this);
                }
                else
                {
                    ModernDialog.ShowWarning(LocalizationService.GetString("Accounts_RefreshFailedTitle", "刷新失败"), msg, owner: this);
                }
            }
            catch (Exception ex)
            {
                ModernDialog.ShowError(LocalizationService.GetString("Accounts_RefreshFailedTitle", "刷新失败"), ex.Message, owner: this);
            }
            finally
            {
                AccountsStatusStrip.Clear();
                RefreshAccountsUi();
            }
        }
    }

    private async void OnRefreshAllTokensClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentProxyConfigFromUi();
        AccountsStatusStrip.ShowBusy(LocalizationService.GetString("Accounts_RefreshAllStatus", "批量刷新 Token…"));
        try
        {
            var (successCount, failedCount) = await _accountPool.RefreshAllTokensAsync(_currentProxyConfig);
            ModernDialog.ShowInfo(LocalizationService.GetString("Accounts_RefreshAllDoneTitle", "已刷新"), string.Format(LocalizationService.GetString("Accounts_RefreshAllDoneFormat", "完成 {0} 个，失败 {1} 个。"), successCount, failedCount), owner: this);
        }
        catch (Exception ex)
        {
            ModernDialog.ShowError(LocalizationService.GetString("Accounts_RefreshFailedTitle", "刷新失败"), ex.Message, owner: this);
        }
        finally
        {
            AccountsStatusStrip.Clear();
            RefreshAccountsUi();
        }
    }

    private void OnDeleteAccountClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string accId })
        {
            var acc = _accountPool.Accounts.FirstOrDefault(a => a.Id == accId);
            if (acc != null && acc.IsRunning)
            {
                ModernDialog.ShowWarning(
                    LocalizationService.GetString("Accounts_DeleteBlockedTitle", "无法删除"),
                    LocalizationService.GetString("Accounts_DeleteBlockedMessage", "该账号正在被实例使用，请先停止。"),
                    this);
                return;
            }
            if (ModernDialog.Confirm(
                    LocalizationService.GetString("Accounts_RemoveConfirmTitle", "确认移除"),
                    LocalizationService.GetString("Accounts_RemoveConfirmMessage", "确定要将此账号从号池移除吗？"),
                    isDestructive: true,
                    owner: this))
            {
                _accountPool.RemoveAccount(accId);
                RefreshAccountsUi();
                RefreshProjectsUi();
                RefreshInstancesUi();
            }
        }
    }

    private void OnUnlockAccountClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string accId })
        {
            var acc = _accountPool.Accounts.FirstOrDefault(a => a.Id == accId);
            if (acc == null) return;
            var msg = string.Format(
                LocalizationService.GetString(
                    "Accounts_UnlockConfirmFormat",
                    "强制解锁账号【{0}】？\n当前占用: {1}\n若该账号仍在运行中的实例里使用，可能导致登录失效。"),
                acc.Email,
                acc.BoundInstanceId);
            if (ModernDialog.Confirm(
                    LocalizationService.GetString("Accounts_UnlockConfirmTitle", "解除账号互斥锁确认"),
                    msg,
                    isDestructive: true,
                    owner: this))
            {
                _accountPool.ForceUnlockAccount(accId);
                RefreshAccountsUi();
                RefreshProjectsUi();
                RefreshInstancesUi();
                UpdateSidebarStatus();
            }
        }
    }

    private void OnUnlockAllAccountsClicked(object sender, RoutedEventArgs e)
    {
        var lockedCount = _accountPool.Accounts.Count(a => a.IsRunning);
        if (lockedCount == 0)
        {
            ModernDialog.ShowInfo(
                LocalizationService.GetString("Common_Notice", "提示"),
                LocalizationService.GetString("Accounts_NoLockMessage", "当前所有账号均处于闲置可用状态，无需重置互斥锁。"),
                this);
            return;
        }
        var msg = string.Format(
            LocalizationService.GetString(
                "Accounts_UnlockAllConfirmFormat",
                "{0} 个账号处于锁定状态。重置后全部变为闲置可用。是否继续？"),
            lockedCount);
        if (ModernDialog.Confirm(
                LocalizationService.GetString("Accounts_UnlockAllConfirmTitle", "重置账号互斥锁"),
                msg,
                isDestructive: true,
                owner: this))
        {
            _accountPool.ForceUnlockAllAccounts();
            RefreshAccountsUi();
            RefreshProjectsUi();
            RefreshInstancesUi();
            UpdateSidebarStatus();
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("Accounts_UnlockDoneTitle", "完成"),
                LocalizationService.GetString("Accounts_UnlockDoneMessage", "已重置全部锁定。"),
                this);
        }
    }

    private async void OnTestProxyClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentProxyConfigFromUi();
        ProxyTestResultText.Text = LocalizationService.GetString("Proxy_TestingStatus", "正在测试连接...");
        ProxyTestResultText.Foreground = Brushes.LightGray;
        var result = await _proxyService.TestConnectionAsync(_currentProxyConfig, _accountPool.Accounts, _accountPool);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format(LocalizationService.GetString("Proxy_RegionFormat", "出口地区: {0}"), result.FormattedLocation));
        var geminiBadge = result.GeminiStatus switch
        {
            ConnectivityStatus.Success => $"✓ {result.GeminiLatencyMs}ms",
            ConnectivityStatus.Failed => $"✗ {(result.GeminiLatencyMs > 0 ? $"{result.GeminiLatencyMs}ms " : "")}",
            _ => "-"
        };
        sb.AppendLine($"Gemini: {geminiBadge} ({result.GeminiMessage})");
        var agBadge = result.AntigravityStatus switch
        {
            ConnectivityStatus.Success => $"✓ {result.AntigravityLatencyMs}ms",
            ConnectivityStatus.Failed => $"✗ {(result.AntigravityLatencyMs > 0 ? $"{result.AntigravityLatencyMs}ms " : "")}",
            _ => "-"
        };
        sb.Append($"Antigravity: {agBadge} ({result.AntigravityMessage})");
        ProxyTestResultText.Text = sb.ToString();
        ProxyTestResultText.Foreground = result.Success ? Brushes.LightGreen : Brushes.OrangeRed;
    }

    private void OnSaveProxyClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentProxyConfigFromUi();
        ModernDialog.ShowSuccess(
            LocalizationService.GetString("Proxy_SavedTitle", "保存成功"),
            LocalizationService.GetString("Proxy_SavedMessage", "已保存。新启动的实例将使用该代理。"),
            this);
    }

    // Overview update operations share one TTL-guarded exclusive lock: a concurrent request is
    // rejected while the holder is within its idle TTL, and after that the holder counts as stalled
    // and is cancelled. Check and install also run a watchdog that offers a retry; uninstall is local.

    private enum UpdateOpKind
    {
        None,
        CoreCheck,
        CoreInstall,
        CoreUninstall
    }

    /// <summary>Idle tolerance while waiting on the network.</summary>
    private static readonly TimeSpan UpdateOpIdleTtlNetwork = TimeSpan.FromSeconds(10);

    /// <summary>Idle tolerance for the local install phase, which reports no progress.</summary>
    private static readonly TimeSpan UpdateOpIdleTtlLocal = TimeSpan.FromMinutes(2);

    /// <summary>Watchdog poll interval.</summary>
    private static readonly TimeSpan UpdateOpWatchdogPoll = TimeSpan.FromSeconds(1);

    private UpdateOpKind _updateOpKind = UpdateOpKind.None;
    private DateTime _updateOpLastActivityUtc;
    private TimeSpan _updateOpIdleTtl = UpdateOpIdleTtlNetwork;
    private int _updateOpGeneration;
    private CancellationTokenSource? _updateOpCts;
    private bool _startupUpdateAutoChecked;

    /// <summary>Takes the exclusive slot; a holder past its idle TTL is cancelled and replaced.</summary>
    private bool TryEnterUpdateOp(UpdateOpKind kind, out int generation, out CancellationToken token)
    {
        generation = 0;
        token = CancellationToken.None;

        if (_updateOpKind != UpdateOpKind.None)
        {
            if (DateTime.UtcNow - _updateOpLastActivityUtc < _updateOpIdleTtl)
            {
                return false;
            }

            // Stalled holder: cancel but do not Dispose, in-flight callbacks may still reference the source.
            CancelCurrentUpdateOp();
            _updateOpCts = null;
        }

        _updateOpCts = new CancellationTokenSource();
        _updateOpGeneration++;
        generation = _updateOpGeneration;
        _updateOpKind = kind;
        _updateOpLastActivityUtc = DateTime.UtcNow;
        _updateOpIdleTtl = UpdateOpIdleTtlNetwork;
        token = _updateOpCts.Token;
        return true;
    }

    /// <summary>Releases the slot only when <paramref name="generation"/> is still current.</summary>
    private void ExitUpdateOp(int generation)
    {
        if (generation != _updateOpGeneration)
        {
            return;
        }

        _updateOpKind = UpdateOpKind.None;
        var cts = _updateOpCts;
        _updateOpCts = null;
        cts?.Dispose();
    }

    private void CancelCurrentUpdateOp()
    {
        try
        {
            _updateOpCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Operation already finished.
        }
    }

    /// <summary>Stamps progress so the watchdog can tell a slow operation from a stalled one.</summary>
    private void TouchUpdateOp()
    {
        if (_updateOpKind != UpdateOpKind.None)
        {
            _updateOpLastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Returns false when another operation still holds the slot.</summary>
    private async Task<bool> StartUpdateOpAsync(UpdateOpKind kind)
    {
        if (!TryEnterUpdateOp(kind, out var generation, out var token))
        {
            return false;
        }

        _ = RunUpdateStallWatchdogAsync(kind, generation);

        try
        {
            switch (kind)
            {
                case UpdateOpKind.CoreCheck:
                    await RunCoreCheckAsync(token);
                    break;
                case UpdateOpKind.CoreInstall:
                    await RunCoreInstallAsync(token);
                    break;
                case UpdateOpKind.CoreUninstall:
                    await RunCoreUninstallAsync(token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Whoever cancelled (watchdog or user) resets the UI.
        }
        finally
        {
            ExitUpdateOp(generation);
        }

        return true;
    }

    /// <summary>Offers a retry once no progress is seen for the idle TTL. Check and install only.</summary>
    private async Task RunUpdateStallWatchdogAsync(UpdateOpKind kind, int generation)
    {
        if (kind is not (UpdateOpKind.CoreCheck or UpdateOpKind.CoreInstall))
        {
            return;
        }

        while (true)
        {
            await Task.Delay(UpdateOpWatchdogPoll);

            // Finished, or the slot was taken over by another operation.
            if (generation != _updateOpGeneration || _updateOpKind != kind)
            {
                return;
            }

            if (DateTime.UtcNow - _updateOpLastActivityUtc < _updateOpIdleTtl)
            {
                continue;
            }

            break;
        }

        var isCheck = kind == UpdateOpKind.CoreCheck;
        var retry = ModernDialog.Confirm(
            LocalizationService.GetString("CloudUpdate_StallTitle", "更新操作超时"),
            isCheck
                ? LocalizationService.GetString("CloudUpdate_StallCheckMessage", "检查更新已超过 10 秒没有任何响应，是否重试？")
                : LocalizationService.GetString("CloudUpdate_StallInstallMessage", "下载并安装长时间没有任何进展，是否重试？"),
            LocalizationService.GetString("CloudUpdate_StallRetryBtn", "重试"),
            LocalizationService.GetString("Common_Cancel", "取消"),
            isDestructive: false,
            owner: this);

        // The modal dialog keeps pumping messages, so the operation may have ended meanwhile.
        if (generation != _updateOpGeneration || _updateOpKind != kind)
        {
            return;
        }

        CancelCurrentUpdateOp();

        if (retry)
        {
            await StartUpdateOpAsync(kind);
            return;
        }

        ResetUpdateOpUi(kind);
    }

    /// <summary>Restores the overview controls after an abandoned operation.</summary>
    private void ResetUpdateOpUi(UpdateOpKind kind)
    {
        var aborted = LocalizationService.GetString("CloudUpdate_Aborted", "已取消本次更新操作。");

        switch (kind)
        {
            case UpdateOpKind.CoreCheck:
                OnlineVersionText.Text = aborted;
                CheckUpdateButton.IsEnabled = true;
                break;

            case UpdateOpKind.CoreInstall:
            case UpdateOpKind.CoreUninstall:
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.Visibility = Visibility.Collapsed;
                InstallProgressLabel.Visibility = Visibility.Visible;
                InstallProgressLabel.Text = aborted;
                CheckUpdateButton.IsEnabled = true;
                DownloadInstallButton.IsEnabled = _currentManifest != null;
                if (UninstallCoreButton != null)
                {
                    UninstallCoreButton.IsEnabled = _engineService.DetectManagedEngine().IsAvailable;
                }
                break;
        }
    }

    /// <summary>Runs one update check per session, deferred past the first frame.</summary>
    private void TriggerStartupUpdateCheck()
    {
        if (_startupUpdateAutoChecked)
        {
            return;
        }

        _startupUpdateAutoChecked = true;

        Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                try
                {
                    // Silent: if the user already started a check, this call is simply refused.
                    await StartUpdateOpAsync(UpdateOpKind.CoreCheck);
                }
                catch (Exception ex)
                {
                    // Nobody is waiting here, so report on the card instead of throwing from async void.
                    OnlineVersionText.Text = string.Format(
                        LocalizationService.GetString("CloudUpdate_CheckFailedFormat", "检查更新失败: {0}"),
                        ex.Message);
                }
            }),
            DispatcherPriority.Background);
    }

    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        await StartUpdateOpAsync(UpdateOpKind.CoreCheck);
    }

    private async Task RunCoreCheckAsync(CancellationToken cancellationToken)
    {
        OnlineVersionText.Text = LocalizationService.GetString("CloudUpdate_Fetching", "⏳ 正在请求官方 Cloud Run Manifest 清单...");
        try
        {
            _currentManifest = await _engineService.FetchLatestManifestAsync(cancellationToken);
            if (_currentManifest != null)
            {
                var sizeMb = _currentManifest.Size / (1024.0 * 1024.0);
                OnlineVersionText.Text = string.Format(
                    LocalizationService.GetString("CloudUpdate_FoundFormat", "发现最新版本: v{0} | 包体大小: {1} MB\n下载直链: {2}"),
                    _currentManifest.Version,
                    sizeMb.ToString("F1"),
                    _currentManifest.DownloadUrl);
                DownloadInstallButton.IsEnabled = true;
                UpdateSidebarStatus();
            }
            else
            {
                OnlineVersionText.Text = LocalizationService.GetString("CloudUpdate_ParseFailed", "未能解析到版本清单信息。");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the watchdog; it resets the UI.
        }
        catch (Exception ex)
        {
            OnlineVersionText.Text = string.Format(LocalizationService.GetString("CloudUpdate_CheckFailedFormat", "检查更新失败: {0}"), ex.Message);
        }
    }

    private async void OnDownloadAndInstallClicked(object sender, RoutedEventArgs e)
    {
        await StartUpdateOpAsync(UpdateOpKind.CoreInstall);
    }

    private async Task RunCoreInstallAsync(CancellationToken cancellationToken)
    {
        if (_currentManifest == null) return;
        // Running instances hold file locks on the core directory.
        if (_instanceService.Instances.Any(i => i.IsRunning))
        {
            var confirm = ModernDialog.Confirm(
                LocalizationService.GetString("CloudUpdate_RunningConfirmTitle", "升级前确认"),
                LocalizationService.GetString("CloudUpdate_RunningConfirmMessage", "检测到当前有正在运行的 Antigravity 实例。\n进行核心全新部署需要先关闭所有运行实例以释放文件占用，是否立即停止所有实例并继续？"),
                isDestructive: true,
                owner: this);
            if (!confirm) return;
            foreach (var inst in _instanceService.Instances.Where(i => i.IsRunning))
            {
                _instanceService.StopInstance(inst.Id);
            }
        }
        DownloadInstallButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        if (UninstallCoreButton != null) UninstallCoreButton.IsEnabled = false;
        InstallProgressBar.Visibility = Visibility.Visible;
        InstallProgressLabel.Visibility = Visibility.Visible;
        string? installerPath = null;
        try
        {
            // Stage 1: download to the system temp directory.
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressLabel.Text = LocalizationService.GetString("CloudUpdate_Stage1Download", "阶段 1/4: 正在下载安装包至系统临时目录 (tmp)...");
            TouchUpdateOp();
            var progress = new Progress<double>(p =>
            {
                TouchUpdateOp();
                InstallProgressBar.Value = p * 100;
                InstallProgressLabel.Text = string.Format(LocalizationService.GetString("CloudUpdate_Stage1PercentFormat", "阶段 1/4: 正在下载安装程序... {0}%"), (p * 100).ToString("F1"));
            });
            installerPath = await _engineService.DownloadInstallerAsync(_currentManifest, progress, cancellationToken);
            // Stage 2: SHA512 verification happens inside the download call.
            InstallProgressBar.IsIndeterminate = true;
            InstallProgressLabel.Text = LocalizationService.GetString("CloudUpdate_Stage2Verified", "阶段 2/4: 安装包下载完成，SHA512 完整性校验通过。");
            TouchUpdateOp();
            await Task.Delay(200, cancellationToken);
            // Stage 3: silent uninstall and cleanup of the core directory.
            InstallProgressLabel.Text = LocalizationService.GetString("CloudUpdate_Stage3Uninstall", "阶段 3/4: 正在卸载旧版核心并彻底清理运行目录残留...");
            // Local phase from here: no progress callbacks, so widen the idle tolerance.
            _updateOpIdleTtl = UpdateOpIdleTtlLocal;
            TouchUpdateOp();
            await _engineService.UninstallExistingCoreAsync(cancellationToken);
            // Stage 4: fresh silent install into the core directory.
            InstallProgressLabel.Text = LocalizationService.GetString("CloudUpdate_Stage4Install", "阶段 4/4: 正在全新部署 Managed 便携核心...");
            TouchUpdateOp();
            await _engineService.InstallToManagedCoreAsync(installerPath, cancellationToken);
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            InstallProgressLabel.Text = LocalizationService.GetString("CloudUpdate_InstallSuccess", "安装完成。");
            TouchUpdateOp();
            RefreshEngineStatus();
            UpdateSidebarStatus();
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("CloudUpdate_InstallDoneTitle", "安装完成"),
                LocalizationService.GetString("CloudUpdate_InstallDoneMessage", "已部署 Managed 核心，临时安装包已清理。"),
                this);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the watchdog; it resets the UI.
        }
        catch (Exception ex)
        {
            InstallProgressLabel.Text = string.Format(LocalizationService.GetString("CloudUpdate_InstallFailedFormat", "安装失败: {0}"), ex.Message);
            ModernDialog.ShowError(
                LocalizationService.GetString("Common_Error", "错误"),
                string.Format(LocalizationService.GetString("CloudUpdate_InstallFailedFormat", "安装失败: {0}"), ex.Message),
                this);
        }
        finally
        {
            if (!string.IsNullOrEmpty(installerPath))
            {
                await EngineService.TryDeleteFileAsync(installerPath);
            }
            await _engineService.CleanTempInstallersAsync();
            DownloadInstallButton.IsEnabled = _currentManifest != null;
            CheckUpdateButton.IsEnabled = true;
            if (UninstallCoreButton != null) UninstallCoreButton.IsEnabled = _engineService.DetectManagedEngine().IsAvailable;
        }
    }

    private async void OnUninstallCoreClicked(object sender, RoutedEventArgs e)
    {
        await StartUpdateOpAsync(UpdateOpKind.CoreUninstall);
    }

    private async Task RunCoreUninstallAsync(CancellationToken cancellationToken)
    {
        var managed = _engineService.DetectManagedEngine();
        if (!managed.IsAvailable) return;
        var confirm = ModernDialog.Confirm(
            LocalizationService.GetString("CoreUninstall_ConfirmTitle", "确认卸载核心"),
            LocalizationService.GetString("CoreUninstall_ConfirmMessage", "确定要卸载并彻底移除当前的 Antigravity Managed 便携核心吗？\n所有核心二进制与补丁文件将被清空（实例数据、项目记忆与账号配置完全独立保存，绝不会丢失）。"),
            isDestructive: true,
            owner: this);
        if (!confirm) return;
        if (_instanceService.Instances.Any(i => i.IsRunning))
        {
            foreach (var inst in _instanceService.Instances.Where(i => i.IsRunning))
            {
                _instanceService.StopInstance(inst.Id);
            }
        }
        try
        {
            if (UninstallCoreButton != null) UninstallCoreButton.IsEnabled = false;
            DownloadInstallButton.IsEnabled = false;
            CheckUpdateButton.IsEnabled = false;
            InstallProgressLabel.Visibility = Visibility.Visible;
            InstallProgressLabel.Text = LocalizationService.GetString("CoreUninstall_Progress", "正在静默卸载并清理核心目录...");
            InstallProgressBar.Visibility = Visibility.Visible;
            InstallProgressBar.IsIndeterminate = true;
            await _engineService.UninstallExistingCoreAsync(cancellationToken);
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            InstallProgressLabel.Text = LocalizationService.GetString("CoreUninstall_Done", "核心已彻底卸载清空。");
            RefreshEngineStatus();
            UpdateSidebarStatus();
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("CoreUninstall_SuccessTitle", "卸载完成"),
                LocalizationService.GetString("CoreUninstall_SuccessMessage", "已卸载。"),
                this);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the watchdog; it resets the UI.
        }
        catch (Exception ex)
        {
            InstallProgressLabel.Text = string.Format(LocalizationService.GetString("CoreUninstall_FailedFormat", "卸载失败: {0}"), ex.Message);
            ModernDialog.ShowError(
                LocalizationService.GetString("Common_Error", "错误"),
                string.Format(LocalizationService.GetString("CoreUninstall_FailedFormat", "卸载失败: {0}"), ex.Message),
                this);
        }
        finally
        {
            if (UninstallCoreButton != null) UninstallCoreButton.IsEnabled = _engineService.DetectManagedEngine().IsAvailable;
            DownloadInstallButton.IsEnabled = _currentManifest != null;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>Paints the surface overlay above the Mica/Tabbed backdrop; alpha follows the opacity slider.</summary>
    private void ApplyHostWindowOpacity(double opacity)
    {
        this.Background = Brushes.Transparent;
        if (RootWindowGrid == null)
        {
            return;
        }

        var alpha = ThemeService.SurfaceAlpha(opacity);

        var baseColor = TryFindResource("WindowOverlayBaseColor") is Color c
            ? c
            : Color.FromRgb(0x20, 0x20, 0x20);

        RootWindowGrid.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
    }

    /// <summary>Repaints the surface overlay after a theme color change.</summary>
    internal void RefreshWindowSurface() => ApplyHostWindowOpacity(ThemeService.CurrentOpacity);

    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        // Skip the Checked that XAML raises during InitializeComponent; App already applied the saved mode.
        if (_isInitializingTheme || _daemon is null) return;

        var mode = ThemeDarkRadio?.IsChecked == true ? "dark"
                 : ThemeLightRadio?.IsChecked == true ? "light"
                 : "auto";

        App.SetThemeMode(mode);
        _daemon.UpdateThemeMode(mode);
    }

    private string GetSelectedMaterial()
    {
        if (NoneRadioBtn?.IsChecked == true) return "none";
        if (AcrylicRadioBtn?.IsChecked == true) return "acrylic";
        if (TabbedRadioBtn?.IsChecked == true) return "tabbed";
        return "mica";
    }

    /// <summary>Returns "" when the skin is explicitly off, null when the combo is not initialized yet.</summary>
    private string? GetSelectedNewUx()
    {
        if (NewUxThemeCombo?.SelectedItem is ComboBoxItem item)
        {
            return item.Tag as string ?? string.Empty;
        }
        return null;
    }

    /// <summary>Switches the Antigravity skin (body[data-agy-theme][data-newux-theme]).</summary>
    private void OnNewUxThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingTheme || _daemon == null) return;

        var mat = GetSelectedMaterial();
        var micaTransparency = MicaOpacitySlider?.Value ?? (1.0 - _daemon.MicaOpacity);
        var opacity = Math.Clamp(1.0 - micaTransparency, 0.00, 1.00);
        var popupOpacity = FixedPopupOpacity;

        _daemon.UpdateTheme(mat, opacity, popupOpacity, null, GetSelectedNewUx());
    }

    private void OnBackdropMaterialChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializingTheme) return;
        if (MicaRadioBtn == null || TabbedRadioBtn == null) return;
        var mat = GetSelectedMaterial();
        // Sliders carry transparency; alpha is its complement.
        var micaTransparency = MicaOpacitySlider?.Value ?? (1.0 - (_daemon?.MicaOpacity ?? 0.72));
        var opacity = Math.Clamp(1.0 - micaTransparency, 0.00, 1.00);
        var popupOpacity = FixedPopupOpacity;
        // Apply locally to the window and every open dialog.
        ThemeService.Update(mat, opacity, popupOpacity);
        ApplyHostWindowOpacity(opacity);
        // Then let the daemon persist it and push CSS to running instances.
        _daemon?.UpdateTheme(mat, opacity, popupOpacity, null, GetSelectedNewUx());
    }

    private void OnMicaOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var transparency = Math.Clamp(e.NewValue, 0.00, 1.00);
        if (MicaOpacityLabel != null)
        {
            MicaOpacityLabel.Text = $"{(int)Math.Round(transparency * 100)}%";
        }
        var opacity = Math.Clamp(1.0 - transparency, 0.00, 1.00);
        ApplyHostWindowOpacity(opacity);
        if (!_isInitializingTheme && _daemon != null && TabbedRadioBtn != null)
        {
            var mat = GetSelectedMaterial();
            var popupOpacity = FixedPopupOpacity;
            ThemeService.Update(mat, opacity, popupOpacity);
            _daemon.UpdateTheme(mat, opacity, popupOpacity, null, GetSelectedNewUx());
        }
    }

    private void OnAccentInteractionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializingTheme || _daemon == null) return;
        _daemon.UpdateAccentInteraction(AccentInteractionCheck?.IsChecked == true);
    }

    /// <summary>Popup opacity is fixed; the slider was removed.</summary>
    private const double FixedPopupOpacity = 0.60;

    private void OnNoiseAlphaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = Math.Clamp(e.NewValue, 0.0, 1.0);
        if (NoiseAlphaLabel != null) NoiseAlphaLabel.Text = $"{(int)Math.Round(v * 100)}%";
        if (_isInitializingTheme || _daemon == null) return;
        _daemon.UpdateNoise(alpha: v);
    }

    private void OnNoiseFreqChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = Math.Clamp(e.NewValue, 0.5, 2.0);
        if (NoiseFreqLabel != null) NoiseFreqLabel.Text = v.ToString("F2");
        if (_isInitializingTheme || _daemon == null) return;
        _daemon.UpdateNoise(frequency: v);
    }

    private void OnNoiseTintChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = Math.Clamp(e.NewValue, 0.0, 1.0);
        if (NoiseTintLabel != null) NoiseTintLabel.Text = $"{(int)Math.Round(v * 100)}%";
        if (_isInitializingTheme || _daemon == null) return;
        _daemon.UpdateNoise(tintRatio: v);
    }

    private void OnNoiseBaseChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = Math.Clamp(e.NewValue, 0.0, 1.0);
        if (NoiseBaseLabel != null) NoiseBaseLabel.Text = $"{(int)Math.Round(v * 100)}%";
        if (_isInitializingTheme || _daemon == null) return;
        _daemon.UpdateNoise(baseAlpha: v);
    }

    private void OnLanguageComboDropDownOpened(object? sender, EventArgs e)
    {
        LocalizationService.RefreshLanguages();
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Switching the language also pushes it to Antigravity and refreshes localized chrome.
        if (LanguageCombo?.SelectedValue is string langId && !string.IsNullOrEmpty(langId))
        {
            if (langId != LocalizationService.CurrentLanguage)
            {
                LocalizationService.SetLanguage(langId);
                _daemon?.UpdateLanguage(langId);
                ApplyLocalizedChrome();
                RefreshProjectsUi();
                RefreshInstancesUi();
                RefreshAccountsUi();
                UpdateSidebarStatus();
                RefreshEngineStatus();
                _trayService?.UpdateTooltip(
                    LocalizationService.GetString("Tray_Tooltip", "Gravityless - Antigravity 编排与运行守护伴侣"));
            }
        }
    }

    private async void RefreshToolsUi()
    {
        try
        {
            // Three directory scans over the shared pool; run them together off the dispatcher.
            var (skills, plugins, mcps) = await Task.Run(() =>
                (_toolService.ListSkills(), _toolService.ListPlugins(), _toolService.ListMcpServers()));

            _skillsDisplayList.Clear();
            foreach (var s in skills) _skillsDisplayList.Add(s);
            _pluginsDisplayList.Clear();
            foreach (var p in plugins) _pluginsDisplayList.Add(p);
            _mcpDisplayList.Clear();
            foreach (var m in mcps) _mcpDisplayList.Add(m);

            // Tab headers double as counters.
            SkillsTab.Header = string.Format(LocalizationService.GetString("Tools_TabSkillsFormat", "技能 ({0})"), skills.Count);
            PluginsTab.Header = string.Format(LocalizationService.GetString("Tools_TabPluginsFormat", "插件 ({0})"), plugins.Count);
            McpTab.Header = string.Format(LocalizationService.GetString("Tools_TabMcpFormat", "MCP ({0})"), mcps.Count);
        }
        catch { }
    }

    private async void OnImportStockToolsClicked(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("Tools_SelectGeminiFolderTitle", "选择 .gemini 文件夹或工具目录"),
            Multiselect = false
        };
        var hostGemini = ProjectService.GetRealHostGeminiDir();
        if (Directory.Exists(hostGemini))
        {
            ofd.InitialDirectory = hostGemini;
        }
        if (ofd.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(ofd.FolderName))
        {
            // Copies skills, plugins and MCP config; keep it off the dispatcher.
            var (importOk, importMsg) = await Task.Run(() =>
            {
                var ok = _toolService.ImportFromGeminiDir(ofd.FolderName, out _, out _, out _, out var message);
                return (ok, message);
            });

            if (importOk)
            {
                RefreshToolsUi();
                ModernDialog.ShowSuccess(LocalizationService.GetString("Tools_ImportSuccessTitle", "已导入"), importMsg, this);
            }
            else
            {
                ModernDialog.ShowWarning(LocalizationService.GetString("Tools_ImportNoticeTitle", "导入提示"), importMsg, this);
            }
        }
    }

    private void OnOpenSharedFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _toolService.SharedDir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnRefreshToolsClicked(object sender, RoutedEventArgs e)
    {
        RefreshToolsUi();
    }

    private void OnNewSkillClicked(object sender, RoutedEventArgs e)
    {
        var name = ModernDialog.Prompt(
            LocalizationService.GetString("Dialogs_NewSkillTitle", "新建技能"),
            LocalizationService.GetString("Dialogs_NewSkillPrompt", "输入技能名称（小写短横线，如 my-tool-skill）:"),
            "new-skill",
            owner: this);
        if (string.IsNullOrWhiteSpace(name)) return;
        var desc = ModernDialog.Prompt(
            LocalizationService.GetString("Dialogs_SkillDescTitle", "技能描述"),
            LocalizationService.GetString("Dialogs_SkillDescPrompt", "输入技能的用途和触发条件:"),
            string.Format(LocalizationService.GetString("Dialogs_SkillDescDefaultFormat", "处理 {0} 相关任务"), name),
            owner: this);
        if (_toolService.CreateSkill(name.Trim(), desc?.Trim(), out var err))
        {
            RefreshToolsUi();
            var skillDir = Path.Combine(_toolService.SkillsDir, name.Trim());
            try { Process.Start(new ProcessStartInfo { FileName = skillDir, UseShellExecute = true }); } catch { }
            ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_CreateDoneTitle", "创建完成"), string.Format(LocalizationService.GetString("Dialogs_CreateSkillDoneFormat", "已创建:\n{0}"), skillDir), this);
        }
        else
        {
            ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_CreateFailedTitle", "创建失败"), err ?? LocalizationService.GetString("Common_UnknownError", "未知错误"), this);
        }
    }

    private void OnNewPluginClicked(object sender, RoutedEventArgs e)
    {
        var name = ModernDialog.Prompt(
            LocalizationService.GetString("Dialogs_NewPluginTitle", "新建插件"),
            LocalizationService.GetString("Dialogs_NewPluginPrompt", "输入插件名称（小写短横线，如 my-plugin）:"),
            "my-plugin",
            owner: this);
        if (string.IsNullOrWhiteSpace(name)) return;
        var desc = ModernDialog.Prompt(
            LocalizationService.GetString("Dialogs_PluginDescTitle", "插件描述"),
            LocalizationService.GetString("Dialogs_PluginDescPrompt", "输入插件的功能描述:"),
            $"Plugin package for {name}",
            owner: this);
        if (_toolService.CreatePlugin(name.Trim(), desc?.Trim(), out var err))
        {
            RefreshToolsUi();
            var plugDir = Path.Combine(_toolService.PluginsDir, name.Trim());
            try { Process.Start(new ProcessStartInfo { FileName = plugDir, UseShellExecute = true }); } catch { }
            ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_CreateDoneTitle", "创建完成"), string.Format(LocalizationService.GetString("Dialogs_CreatePluginDoneFormat", "已创建:\n{0}"), plugDir), this);
        }
        else
        {
            ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_CreateFailedTitle", "创建失败"), err ?? LocalizationService.GetString("Common_UnknownError", "未知错误"), this);
        }
    }

    private void OnAddMcpServerClicked(object sender, RoutedEventArgs e)
    {
        var name = ModernDialog.Prompt(
            LocalizationService.GetString("Dialogs_AddMcpTitle", "添加 MCP 服务"),
            LocalizationService.GetString("Dialogs_AddMcpPrompt", "输入服务名称:"),
            "my-mcp-server",
            owner: this);
        if (string.IsNullOrWhiteSpace(name)) return;
        var cmd = ModernDialog.Prompt(
            LocalizationService.GetString("Dialogs_McpCommandTitle", "命令或 URL"),
            LocalizationService.GetString("Dialogs_McpCommandPrompt", "输入启动命令（如 npx）或远程 URL:"),
            "npx -y @modelcontextprotocol/server-filesystem",
            owner: this);
        if (string.IsNullOrWhiteSpace(cmd)) return;
        // http(s) input is stored as a remote URL; anything else splits into command + args.
        bool isHttp = cmd.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        string args = "";
        if (!isHttp)
        {
            var parts = cmd.Split(' ', 2);
            var command = parts[0];
            args = parts.Length > 1 ? parts[1] : "";
            if (_toolService.AddOrUpdateMcpServer(name.Trim(), command, args, null, false, out var err))
            {
                RefreshToolsUi();
                ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_McpAddedTitle", "已添加"), string.Format(LocalizationService.GetString("Dialogs_McpAddedFormat", "已写入 mcp_config.json: {0}"), name), this);
            }
            else
            {
                ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_AddFailedTitle", "添加失败"), err ?? LocalizationService.GetString("Common_UnknownError", "未知错误"), this);
            }
        }
        else
        {
            if (_toolService.AddOrUpdateMcpServer(name.Trim(), cmd.Trim(), "", null, true, out var err))
            {
                RefreshToolsUi();
                ModernDialog.ShowSuccess(LocalizationService.GetString("Dialogs_McpAddedTitle", "已添加"), string.Format(LocalizationService.GetString("Dialogs_McpAddedFormat", "已写入 mcp_config.json: {0}"), name), this);
            }
            else
            {
                ModernDialog.ShowWarning(LocalizationService.GetString("Dialogs_AddFailedTitle", "添加失败"), err ?? LocalizationService.GetString("Common_UnknownError", "未知错误"), this);
            }
        }
    }

    private void OnEditMcpConfigFileClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = _toolService.McpConfigFile,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>Dispatches by Tag type to a details dialog; edit/open/delete actions live in that dialog.</summary>
    private void OnToolDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: not null } element)
        {
            return;
        }

        ToolDetailsDialog? dialog = element.Tag switch
        {
            SkillItem skill => new ToolDetailsDialog(
                skill,
                onEditSkillMd: () => EditSkillMdFile(skill.Path),
                onOpenFolder: () => OpenToolFolder(skill.Path),
                onDelete: () => ConfirmDeleteSkill(skill.DirName)),

            PluginItem plugin => new ToolDetailsDialog(
                plugin,
                onOpenFolder: () => OpenToolFolder(plugin.Path),
                onDelete: () => ConfirmDeletePlugin(plugin.DirName)),

            McpServerItem mcp => new ToolDetailsDialog(
                mcp,
                onDelete: () => ConfirmDeleteMcpServer(mcp.Name)),

            _ => null
        };

        if (dialog is null)
        {
            return;
        }

        dialog.Owner = this;
        dialog.ShowDialog();

        // The dialog may have created SKILL.md or deleted entries.
        RefreshToolsUi();
    }

    private static void EditSkillMdFile(string dirPath)
    {
        var mdPath = Path.Combine(dirPath, "SKILL.md");
        if (!File.Exists(mdPath))
        {
            File.WriteAllText(mdPath, $"---\nname: {Path.GetFileName(dirPath)}\ndescription: \n---\n\n# {Path.GetFileName(dirPath)}\n");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = mdPath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void OpenToolFolder(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dirPath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>Confirms and deletes; true lets the details dialog close.</summary>
    private bool ConfirmDeleteSkill(string dirName)
    {
        if (!ModernDialog.Confirm(
                LocalizationService.GetString("Tools_ConfirmDeleteTitle", "确认删除"),
                string.Format(LocalizationService.GetString("Tools_DeleteSkillConfirmFormat", "确定要删除公共技能【{0}】吗？删除后所有挂载此技能的实例将无法再调用。"), dirName),
                isDestructive: true,
                owner: this))
        {
            return false;
        }

        _toolService.DeleteSkill(dirName, out _);
        return true;
    }

    private bool ConfirmDeletePlugin(string dirName)
    {
        if (!ModernDialog.Confirm(
                LocalizationService.GetString("Tools_ConfirmDeleteTitle", "确认删除"),
                string.Format(LocalizationService.GetString("Tools_DeletePluginConfirmFormat", "确定要删除公共插件包【{0}】吗？"), dirName),
                isDestructive: true,
                owner: this))
        {
            return false;
        }

        _toolService.DeletePlugin(dirName, out _);
        return true;
    }

    private bool ConfirmDeleteMcpServer(string name)
    {
        if (!ModernDialog.Confirm(
                LocalizationService.GetString("Tools_ConfirmDeleteTitle", "确认删除"),
                string.Format(LocalizationService.GetString("Tools_DeleteMcpConfirmFormat", "确定要从 mcp_config.json 中移除 MCP 服务【{0}】吗？"), name),
                isDestructive: true,
                owner: this))
        {
            return false;
        }

        _toolService.DeleteMcpServer(name, out _);
        return true;
    }
}
public class ProjectDisplayItem
{
    private readonly ProjectProfile _project;
    public string Id => _project.Id;
    public string Name => _project.Name;
    public string DisplayNameWithShortId => _project.DisplayNameWithShortId;
    public string Path => _project.Path;
    public bool IsPathValid => ProjectService.IsLooseChats(_project.Id)
        ? Directory.Exists(_project.Path)
        : !string.IsNullOrEmpty(_project.Path) && Directory.Exists(_project.Path);
    public bool IsPathInvalid => !IsPathValid;

    public string BoundInstancesText { get; set; } = string.Empty;
    public bool HasBoundInstances => !string.IsNullOrEmpty(BoundInstancesText);

    public string StatusText => _project.IsRunning
        ? string.Format(LocalizationService.GetString("Projects_StatusRunningFormat", "运行中 (PID: {0})"), _project.ProcessId)
        : LocalizationService.GetString("Projects_StatusIdle", "未运行");
    public bool IsRunning => _project.IsRunning;

    public string ConvoCountText => _project.ConversationCount > 0
        ? string.Format(LocalizationService.GetString("Projects_ConvoCountFormat", "会话: {0}"), _project.ConversationCount)
        : LocalizationService.GetString("Projects_NoConvo", "无会话");

    public string LastActiveText => _project.LastActiveAt.HasValue
        ? string.Format(LocalizationService.GetString("Projects_LastActiveFormat", "最近活动: {0}"), _project.LastActiveAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
        : "";
    public bool ShowStart => !_project.IsRunning;
    public bool ShowStop => _project.IsRunning;

    public ProjectDisplayItem(ProjectProfile project, List<InstanceProfile>? allInstances = null)
    {
        _project = project;
        if (allInstances != null)
        {
            var boundNames = allInstances
                .Where(i => i.BoundProjectIds.Contains(project.Id))
                .Select(i => i.Name)
                .ToList();
            if (boundNames.Count > 0)
            {
                BoundInstancesText = string.Format(LocalizationService.GetString("Projects_BoundInstancesFormat", "实例: {0}"), string.Join(", ", boundNames));
            }
        }
    }
}
public class AccountDisplayItem
{
    public string Id { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PrimaryName { get; set; } = string.Empty;

    public string InstallationId { get; set; } = string.Empty;

    public string StatusText { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public string BoundInstanceSubtitle { get; set; } = string.Empty;

    public bool ShowBoundInstanceSubtitle { get; set; }

    public string QuotaSummaryText { get; set; } = string.Empty;

    public bool ShowQuotaSummary { get; set; }

    public bool ShowUnlock => IsRunning;
}
public class InstanceDisplayItem
{
    private readonly InstanceProfile _profile;

    public List<AccountProfile> AvailableAccounts { get; }
    public string Id => _profile.Id;
    public string Name => _profile.Name;
    public string DisplayNameWithShortId => _profile.DisplayNameWithShortId;

    public string? BoundAccountId
    {
        get => _profile.BoundAccountId;
        set => _profile.BoundAccountId = value;
    }

    public string BoundProjectText { get; }

    public bool ShareSkills
    {
        get => _profile.ShareSkills;
        set => _profile.ShareSkills = value;
    }

    public bool SharePlugins
    {
        get => _profile.SharePlugins;
        set => _profile.SharePlugins = value;
    }

    public bool ShareMcp
    {
        get => _profile.ShareMcp;
        set => _profile.ShareMcp = value;
    }

    public string StatusText
    {
        get
        {
            if (_profile.IsRunning)
            {
                var run = LocalizationService.GetString("Instances_StatusRunning", "运行中");
                return _profile.ProcessId.HasValue ? $"{run} (PID: {_profile.ProcessId})" : run;
            }
            return LocalizationService.GetString("Instances_StatusStopped", "已停止");
        }
    }

    public bool IsRunning => _profile.IsRunning;

    public string PortText
    {
        get
        {
            if (_profile.IsRunning)
            {
                var label = LocalizationService.GetString("Instances_PortLabel", "端口:");
                return $"{label} {_profile.ListeningPort ?? 0}";
            }
            return LocalizationService.GetString("Instances_NoPort", "未分配端口");
        }
    }
    public bool ShowStart => !_profile.IsRunning;
    public bool ShowStop => _profile.IsRunning;

    public InstanceDisplayItem(InstanceProfile profile, List<AccountProfile> availableAccounts, IReadOnlyList<ProjectProfile>? allProjects = null)
    {
        _profile = profile;
        AvailableAccounts = availableAccounts;
        // Bound projects resolve to names; unresolved ids fall back to a count.
        if (allProjects != null && _profile.BoundProjectIds.Count > 0)
        {
            var names = allProjects
                .Where(p => _profile.BoundProjectIds.Contains(p.Id))
                .Select(p => p.Name)
                .ToList();
            BoundProjectText = names.Count > 0 
                ? string.Format(LocalizationService.GetString("Instances_BoundProjectFormat", "项目: {0}"), string.Join(", ", names)) 
                : string.Format(LocalizationService.GetString("Instances_BoundProjectCountFormat", "项目: {0} 个"), _profile.BoundProjectIds.Count);
        }
        else
        {
            BoundProjectText = LocalizationService.GetString("Instances_NoBoundProject", "项目: 无");
        }
    }
}
