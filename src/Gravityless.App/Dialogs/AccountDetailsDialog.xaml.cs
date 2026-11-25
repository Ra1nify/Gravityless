using System.Windows;
using System.Windows.Media;
using Gravityless.App.Services;
using Gravityless.Core.Models;
using Gravityless.Core.Services;
using Wpf.Ui.Controls;
using WpfControls = System.Windows.Controls;

namespace Gravityless.App.Dialogs;

public partial class AccountDetailsDialog
{
    private readonly AccountProfile _account;
    private readonly AccountPoolService _accountPool;
    private readonly InstanceService _instanceService;
    private readonly ProxyConfig? _proxyConfig;

    public AccountDetailsDialog(
        AccountProfile account,
        AccountPoolService accountPool,
        InstanceService instanceService,
        ProxyConfig? proxyConfig = null)
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);
        _account = account;
        _accountPool = accountPool;
        _instanceService = instanceService;
        _proxyConfig = proxyConfig;

        LoadAccountData(showStoredError: true);
    }

    private void LoadAccountData(bool showStoredError = false)
    {
        DisplayNameBox.Text = string.IsNullOrEmpty(_account.DisplayName) ? _account.Email : _account.DisplayName;
        EmailText.Text = _account.Email;

        InstallationIdText.Text = _account.InstallationId;

        if (!string.IsNullOrEmpty(_account.BoundInstanceId))
        {
            var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == _account.BoundInstanceId);
            if (inst != null)
            {
                BoundInstanceNameText.Text = $"🔵 {inst.DisplayNameWithShortId}";
                BoundInstanceIdText.Text = string.Format(
                    LocalizationService.GetString("AccountDetails_BoundInstanceIdFormat", "实例 ID: {0}"),
                    _account.BoundInstanceId);
            }
            else
            {
                BoundInstanceNameText.Text = string.Format(
                    LocalizationService.GetString("AccountDetails_BoundInstanceFormat", "已绑定 (ID: {0})"),
                    _account.BoundInstanceId);
                BoundInstanceIdText.Text = $"ID: {_account.BoundInstanceId}";
            }
            CopyInstanceIdBtn.Visibility = Visibility.Visible;
        }
        else
        {
            BoundInstanceNameText.Text = LocalizationService.GetString("AccountDetails_NotBound", "未绑定");
            BoundInstanceIdText.Text = LocalizationService.GetString("AccountDetails_NotAssigned", "未分配");
            CopyInstanceIdBtn.Visibility = Visibility.Collapsed;
        }

        var refreshToken = _account.RefreshToken;
        var expiry = _account.AccessTokenExpiresAt;
        bool hasRefresh = !string.IsNullOrWhiteSpace(refreshToken);

        if (hasRefresh)
        {
            TokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenValid", "有效");
            TokenStatusDot.Fill = ResolveBrush("SystemFillColorSuccessBrush", Colors.LightGreen);
        }
        else
        {
            TokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenMissing", "缺少 Refresh Token");
            TokenStatusDot.Fill = ResolveBrush("SystemFillColorCautionBrush", Colors.Orange);
        }

        TokenExpiryText.Text = expiry.HasValue
            ? string.Format(
                LocalizationService.GetString("AccountDetails_TokenExpiryFormat", "过期时间: {0}"),
                expiry.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
            : LocalizationService.GetString("AccountDetails_TokenExpiryUnknown", "过期时间: 未知");

        TokenLastActiveText.Text = string.Format(
            LocalizationService.GetString("AccountDetails_LastActiveFormat", "最近活动: {0}"),
            _account.LastActiveAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        var tierLabel = !string.IsNullOrWhiteSpace(_account.AccountTierName)
            ? _account.AccountTierName
            : _account.AccountTierId;
        if (!string.IsNullOrWhiteSpace(tierLabel))
        {
            AccountTierText.Text = tierLabel;
            AccountTierBadge.Visibility = Visibility.Visible;
        }
        else
        {
            AccountTierBadge.Visibility = Visibility.Collapsed;
        }

        if (_account.QuotaLastFetchedAt.HasValue)
        {
            QuotaLastFetchedText.Text = string.Format(
                LocalizationService.GetString("AccountDetails_QuotaSyncedFormat", "同步于: {0}"),
                _account.QuotaLastFetchedAt.Value.ToLocalTime().ToString("HH:mm:ss"));
        }
        else
        {
            QuotaLastFetchedText.Text = LocalizationService.GetString("AccountDetails_QuotaNotCached", "未同步");
        }

        RenderQuotaGroups();

        if (showStoredError && !string.IsNullOrWhiteSpace(_account.QuotaLastError))
        {
            SetRefreshStatus(
                string.Format(
                    LocalizationService.GetString("AccountDetails_QuotaSyncWarningPrefix", "配额同步异常: {0}"),
                    _account.QuotaLastError),
                InfoBarSeverity.Warning,
                closable: true);
        }
    }

    private void RenderQuotaGroups()
    {
        QuotaGroupsPanel.Children.Clear();

        var groups = _account.GetDisplayQuotaGroups().ToList();
        var ordered = new List<QuotaGroupInfo>();

        void Pull(string poolKey, QuotaGroupInfo fallbackPlaceholder)
        {
            var found = groups.FirstOrDefault(g => string.Equals(g.PoolKey, poolKey, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                ordered.Add(found);
                groups.Remove(found);
            }
            else
            {
                ordered.Add(fallbackPlaceholder);
            }
        }

        Pull("gemini", new QuotaGroupInfo
        {
            PoolKey = "gemini",
            Name = LocalizationService.GetString("AccountDetails_GeminiPool", "Gemini 算力池 (Pro & Flash)")
        });
        Pull("claude_gpt", new QuotaGroupInfo
        {
            PoolKey = "claude_gpt",
            Name = LocalizationService.GetString("AccountDetails_ClaudeGptPool", "Claude & GPT 算力池 (Sonnet / GPT)")
        });
        Pull("argon", new QuotaGroupInfo
        {
            PoolKey = "argon",
            Name = LocalizationService.GetString("AccountDetails_ArgonPool", "Gemini 4 Argon (实验性配额)")
        });

        // Append any extra / newly introduced pools (e.g. Neon, Carbon, Hydrogen).
        ordered.AddRange(groups);

        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                QuotaGroupsPanel.Children.Add(new WpfControls.Separator
                {
                    Margin = new Thickness(0, 6, 0, 10),
                    Opacity = 0.15
                });
            }

            AddQuotaGroupSection(ordered[i]);
        }
    }

    private void AddQuotaGroupSection(QuotaGroupInfo group)
    {
        bool isArgon = string.Equals(group.PoolKey, "argon", StringComparison.OrdinalIgnoreCase);
        bool isClaude = string.Equals(group.PoolKey, "claude_gpt", StringComparison.OrdinalIgnoreCase);

        var primaryBrush = isClaude
            ? ResolveBrush("BrandAccentTextBrush", Colors.MediumPurple)
            : isArgon
                ? ResolveBrush("StatusCautionBrush", Colors.Orange)
                : ResolveBrush("BrandAccentBrush", Colors.DeepSkyBlue);
        var secondaryBrush = isClaude
            ? ResolveBrush("StatusCautionBrush", Colors.Orange)
            : isArgon
                ? ResolveBrush("BrandAccentTextBrush", Colors.MediumPurple)
                : ResolveBrush("StatusSuccessBrush", Colors.LightGreen);

        // Section header
        var headerRow = new WpfControls.StackPanel
        {
            Orientation = WpfControls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };
        headerRow.Children.Add(new SymbolIcon
        {
            Symbol = isClaude ? SymbolRegular.Brain24 : SymbolRegular.Sparkle24,
            FontSize = 15,
            Foreground = primaryBrush,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        headerRow.Children.Add(new WpfControls.TextBlock
        {
            Text = ResolvePoolTitle(group),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (isArgon)
        {
            var badge = new WpfControls.Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ResolveBrush("BrandAccentFillStrongerBrush", Colors.DimGray),
                Child = new WpfControls.TextBlock
                {
                    Text = LocalizationService.GetString("AccountDetails_ExperimentalBadge", "实验性"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = primaryBrush
                }
            };
            headerRow.Children.Add(badge);
        }
        QuotaGroupsPanel.Children.Add(headerRow);

        if (!string.IsNullOrWhiteSpace(group.Description))
        {
            QuotaGroupsPanel.Children.Add(new WpfControls.TextBlock
            {
                Text = group.Description,
                FontSize = 11,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(21, 0, 0, 6)
            });
        }

        // Experimental Argon without entitlement: report null cleanly instead of fake values.
        if (isArgon && !group.HasAnyQuota)
        {
            QuotaGroupsPanel.Children.Add(new WpfControls.TextBlock
            {
                Text = LocalizationService.GetString("AccountDetails_ArgonNoEntitlement", "当前账号暂未开通 Argon 权能，接口返回 null"),
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(21, 2, 0, 4)
            });
            return;
        }

        bool hasSyncedAny = group.HasAnyQuota;
        var label5h = LocalizationService.GetString("AccountDetails_5hWindow", "5 小时滚动刷新窗口 (5h)");
        var fallback5h = hasSyncedAny
            ? LocalizationService.GetString("AccountDetails_NoWindowData", "接口未返回该窗口数据 (null)")
            : LocalizationService.GetString("AccountDetails_NoQuota5h", "暂无 5h 配额缓存");
        AddQuotaWindowRow(
            label5h,
            group.FiveHourFraction,
            group.FiveHourResetTime,
            "HH:mm",
            primaryBrush,
            LocalizationService.GetString("AccountDetails_Rolling5h", "5 小时滚动窗口"),
            fallback5h);

        if (group.DailyFraction.HasValue)
        {
            AddQuotaWindowRow(
                LocalizationService.GetString("AccountDetails_DailyWindow", "每日周期窗口 (Daily)"),
                group.DailyFraction,
                group.DailyResetTime,
                "MM-dd HH:mm",
                primaryBrush,
                LocalizationService.GetString("AccountDetails_DailyWindow", "每日周期窗口 (Daily)"),
                string.Empty);
        }

        var labelWeekly = LocalizationService.GetString("AccountDetails_7dWindow", "每周总量上限 (Weekly)");
        var fallbackWeekly = hasSyncedAny
            ? LocalizationService.GetString("AccountDetails_NoWindowData", "接口未返回该窗口数据 (null)")
            : LocalizationService.GetString("AccountDetails_NoQuota7d", "暂无 7d 配额缓存");
        AddQuotaWindowRow(
            labelWeekly,
            group.WeeklyFraction,
            group.WeeklyResetTime,
            "MM-dd HH:mm",
            secondaryBrush,
            LocalizationService.GetString("AccountDetails_WeeklyCeiling7d", "7 天周期上限"),
            fallbackWeekly);

        // Render any extra non-standard windows (e.g. "5d", "12h") returned by CloudCode.
        var renderedExtraWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in group.Buckets)
        {
            if (!bucket.RemainingFraction.HasValue) continue;
            var win = bucket.Window.Trim().ToLowerInvariant();
            if (win is "" or "5h" or "daily" or "1d" or "24h" or "weekly" or "7d") continue;
            if (!renderedExtraWindows.Add(win)) continue;

            var winTitle = !string.IsNullOrWhiteSpace(bucket.DisplayName)
                ? bucket.DisplayName
                : string.Format(LocalizationService.GetString("AccountDetails_WindowFormat", "{0} 周期窗口"), win);
            AddQuotaWindowRow(winTitle, bucket.RemainingFraction, bucket.ResetTime, "MM-dd HH:mm", secondaryBrush, winTitle, string.Empty);
        }
    }

    private string ResolvePoolTitle(QuotaGroupInfo group)
    {
        return group.PoolKey switch
        {
            "gemini" => string.IsNullOrWhiteSpace(group.Name) || group.Name == "Gemini Models"
                ? LocalizationService.GetString("AccountDetails_GeminiPool", "Gemini 算力池 (Pro & Flash)")
                : group.Name,
            "claude_gpt" => string.IsNullOrWhiteSpace(group.Name) || group.Name == "Claude and GPT models"
                ? LocalizationService.GetString("AccountDetails_ClaudeGptPool", "Claude & GPT 算力池 (Sonnet / GPT)")
                : group.Name,
            "argon" => string.IsNullOrWhiteSpace(group.Name)
                ? LocalizationService.GetString("AccountDetails_ArgonPool", "Gemini 4 Argon (实验性配额)")
                : group.Name,
            _ => group.Name
        };
    }

    private void AddQuotaWindowRow(
        string title,
        double? fraction,
        DateTime? resetTime,
        string resetFormat,
        Brush accentBrush,
        string defaultStatusText,
        string nullStatusText)
    {
        var labelRow = new WpfControls.Grid { Margin = new Thickness(4, 4, 0, 2) };
        labelRow.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = GridLength.Auto });

        var titleText = new WpfControls.TextBlock
        {
            Text = title,
            FontSize = 11,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };
        WpfControls.Grid.SetColumn(titleText, 0);

        int pct = fraction.HasValue ? (int)Math.Round(fraction.Value * 100) : 0;
        var pctText = new WpfControls.TextBlock
        {
            Text = fraction.HasValue ? $"{pct}%" : "-",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = accentBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        WpfControls.Grid.SetColumn(pctText, 1);
        labelRow.Children.Add(titleText);
        labelRow.Children.Add(pctText);
        QuotaGroupsPanel.Children.Add(labelRow);

        QuotaGroupsPanel.Children.Add(new WpfControls.ProgressBar
        {
            Height = 5,
            Value = pct,
            Maximum = 100,
            Margin = new Thickness(4, 0, 0, 2),
            Foreground = accentBrush
        });

        string subText = fraction.HasValue
            ? (resetTime.HasValue ? FormatResetLine(resetTime.Value, resetFormat) : defaultStatusText)
            : nullStatusText;

        QuotaGroupsPanel.Children.Add(new WpfControls.TextBlock
        {
            Text = subText,
            FontSize = 11,
            Opacity = 0.55,
            Margin = new Thickness(6, 0, 0, 6)
        });
    }

    /// <summary>"Reset: 14:30 (in 2h 5m)" — the absolute time plus a countdown.</summary>
    private static string FormatResetLine(DateTime targetUtc, string timeFormat)
    {
        return string.Format(
            LocalizationService.GetString("AccountDetails_ResetFormat", "重置: {0} ({1})"),
            targetUtc.ToLocalTime().ToString(timeFormat),
            FormatCountdown(targetUtc));
    }

    /// <summary>Relative time to a reset instant, in the shape "3天2小时后" / "2h 5m".</summary>
    private static string FormatCountdown(DateTime targetUtc)
    {
        var remaining = targetUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return LocalizationService.GetString("AccountDetails_ResetSoon", "即将重置");
        }

        if (remaining.TotalHours >= 24)
        {
            return string.Format(
                LocalizationService.GetString("AccountDetails_CountdownDaysFormat", "{0} 天 {1} 小时后"),
                (int)remaining.TotalDays, remaining.Hours);
        }

        return string.Format(
            LocalizationService.GetString("AccountDetails_CountdownHoursFormat", "{0} 小时 {1} 分后"),
            (int)remaining.TotalHours, remaining.Minutes);
    }

    private void OnSaveDisplayNameClicked(object sender, RoutedEventArgs e)
    {
        var newName = DisplayNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            _account.DisplayName = newName;
            _accountPool.UpdateAccountDisplayName(_account.Id, newName);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("AccountDetails_NameSavedTitle", "已保存"),
                LocalizationService.GetString("AccountDetails_NameSaved", "账号名称已更新。"),
                this);
        }
    }

    private void OnCopyInstallationIdClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_account.InstallationId);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("Common_CopiedTitle", "已复制"),
                LocalizationService.GetString("AccountDetails_CopiedFingerprint", "设备指纹已复制。"),
                this);
        }
        catch { }
    }

    private void OnCopyInstanceIdClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_account.BoundInstanceId)) return;
        try
        {
            Clipboard.SetText(_account.BoundInstanceId);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("Common_CopiedTitle", "已复制"),
                LocalizationService.GetString("AccountDetails_CopiedInstanceId", "实例 ID 已复制。"),
                this);
        }
        catch { }
    }

    private async void OnRefreshTokenClicked(object sender, RoutedEventArgs e)
    {
        RefreshTokenBtn.IsEnabled = false;
        SetRefreshStatus(
            LocalizationService.GetString("AccountDetails_RefreshRunning", "正在刷新 Token 与配额…"),
            InfoBarSeverity.Informational,
            closable: false);

        try
        {
            var (success, msg) = await _accountPool.RefreshTokenAsync(_account.Id, _proxyConfig, autoRetry: true);

            if (success)
            {
                var severity = string.IsNullOrEmpty(_account.QuotaLastError)
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning;
                SetRefreshStatus(msg, severity, closable: true);
                LoadAccountData(showStoredError: false);
            }
            else
            {
                SetRefreshStatus(msg, InfoBarSeverity.Error, closable: true);
            }
        }
        catch (Exception ex)
        {
            // async void handler: an unhandled exception would crash the UI thread.
            SetRefreshStatus(
                string.Format(LocalizationService.GetString("AccountDetails_RefreshFailedFormat", "刷新失败: {0}"), ex.Message),
                InfoBarSeverity.Error,
                closable: true);
        }
        finally
        {
            RefreshTokenBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Resolves a theme brush by key, falling back to a literal colour when the theme does not
    /// define it. Keeps status dots on the theme palette instead of fixed RGB values.
    /// </summary>
    private Brush ResolveBrush(string key, Color fallback)
    {
        return TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    }

    /// <summary>
    /// Updates the refresh InfoBar. Severity replaces the previous coloured text run, so the
    /// result reads the same in light, dark and contrast themes.
    /// </summary>
    private void SetRefreshStatus(string message, InfoBarSeverity severity, bool closable)
    {
        if (RefreshInfoBar == null) return;

        RefreshInfoBar.Severity = severity;
        RefreshInfoBar.Message = message;
        RefreshInfoBar.IsClosable = closable;
        RefreshInfoBar.IsOpen = true;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        // Persist an edited display name that was never saved explicitly.
        var newName = DisplayNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != _account.DisplayName)
        {
            _account.DisplayName = newName;
            _accountPool.UpdateAccountDisplayName(_account.Id, newName);
        }

        Close();
    }
}
