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
                BoundInstanceNameText.Text = inst.DisplayNameWithShortId;
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

        RenderTokenStatus();

        var tierLabel = _account.FormatTierSummary();
        if (!string.IsNullOrWhiteSpace(tierLabel))
        {
            AccountTierText.Text = tierLabel;
            AccountTierBadge.Visibility = Visibility.Visible;
        }
        else
        {
            AccountTierBadge.Visibility = Visibility.Collapsed;
        }

        var metaParts = new List<string>();
        var creditsSummary = _account.FormatCreditsSummary();
        if (!string.IsNullOrWhiteSpace(creditsSummary))
        {
            metaParts.Add(string.Format(
                LocalizationService.GetString("AccountDetails_CreditsFormat", "Credits 门槛: {0}"),
                creditsSummary));
        }
        if (_account.TelemetryEnabled.HasValue)
        {
            metaParts.Add($"Telemetry: {(_account.TelemetryEnabled.Value ? "On" : "Off")}");
        }
        if (metaParts.Count > 0)
        {
            AccountSubscriptionMetaText.Text = string.Join("  ·  ", metaParts);
            AccountSubscriptionMetaText.Visibility = Visibility.Visible;
        }
        else
        {
            AccountSubscriptionMetaText.Visibility = Visibility.Collapsed;
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
        RenderModelSpecs();

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

    private void RenderModelSpecs()
    {
        if (ModelSpecsPanel == null) return;
        ModelSpecsPanel.Children.Clear();

        var specs = _account.ModelSpecs;
        if (specs == null || specs.Count == 0)
        {
            ModelSpecsPanel.Children.Add(new WpfControls.TextBlock
            {
                Text = LocalizationService.GetString("AccountDetails_ModelSpecsEmpty", "点击下方「刷新 Token 与配额」后展示服务端下发的模型上限与压缩阈值"),
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (i > 0)
            {
                ModelSpecsPanel.Children.Add(new WpfControls.Separator
                {
                    Margin = new Thickness(0, 6, 0, 6),
                    Opacity = 0.12
                });
            }

            var row = new WpfControls.Grid
            {
                Opacity = spec.Disabled ? 0.58 : 1.0,
                ToolTip = string.IsNullOrWhiteSpace(spec.TagDescription) ? null : spec.TagDescription
            };
            row.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = GridLength.Auto });

            var left = new WpfControls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Horizontal };
            nameRow.Children.Add(new WpfControls.TextBlock
            {
                Text = spec.DisplayName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var badgeLabel = spec.Disabled
                ? (string.IsNullOrWhiteSpace(spec.TagTitle)
                    ? LocalizationService.GetString("AccountDetails_ModelDisabledBadge", "未解锁")
                    : $"{spec.TagTitle} · {LocalizationService.GetString("AccountDetails_ModelDisabledBadge", "未解锁")}")
                : spec.TagTitle;

            if (!string.IsNullOrWhiteSpace(badgeLabel))
            {
                var badgeForeground = spec.Disabled
                    ? ResolveBrush("TextFillColorSecondaryBrush", Colors.Silver)
                    : string.Equals(spec.TagTitle, "New", StringComparison.OrdinalIgnoreCase)
                        ? ResolveBrush("StatusSuccessBrush", Colors.LightGreen)
                        : ResolveBrush("StatusCautionBrush", Colors.Orange);

                nameRow.Children.Add(new WpfControls.Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ResolveBrush("BrandAccentFillStrongerBrush", Colors.DimGray),
                    Child = new WpfControls.TextBlock
                    {
                        Text = badgeLabel,
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = badgeForeground
                    }
                });
            }
            left.Children.Add(nameRow);

            var subParts = new List<string> { spec.ModelId };
            if (spec.CheckpointerThreshold > 0)
            {
                var overhead = spec.CheckpointerOverheadRatio.HasValue
                    ? $" / {spec.CheckpointerOverheadRatio.Value * 100:0}%"
                    : string.Empty;
                subParts.Add($"Threshold {ModelContextSpec.FormatK(spec.CheckpointerThreshold)}{overhead}");
            }
            if (spec.MinThinkingBudget.HasValue)
            {
                subParts.Add($"Think≥{spec.MinThinkingBudget.Value}");
            }
            left.Children.Add(new WpfControls.TextBlock
            {
                Text = string.Join(" · ", subParts),
                FontSize = 10.5,
                Opacity = 0.55,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            WpfControls.Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var rightParts = new List<string>();
            if (spec.MaxTokens > 0)
            {
                rightParts.Add(string.Format(
                    LocalizationService.GetString("AccountDetails_HardwareCapFormat", "硬件上限 {0}"),
                    ModelContextSpec.FormatK(spec.MaxTokens)));
            }
            if (spec.CheckpointerMaxTokens > 0)
            {
                rightParts.Add(string.Format(
                    LocalizationService.GetString("AccountDetails_CheckpointerCapFormat", "Checkpointer {0}"),
                    ModelContextSpec.FormatK(spec.CheckpointerMaxTokens)));
            }
            if (spec.MaxOutputTokens > 0)
            {
                rightParts.Add(string.Format(
                    LocalizationService.GetString("AccountDetails_OutputCapFormat", "输出 {0}"),
                    ModelContextSpec.FormatK(spec.MaxOutputTokens)));
            }
            var rightText = new WpfControls.TextBlock
            {
                Text = string.Join("  ·  ", rightParts),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Opacity = 0.85,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            WpfControls.Grid.SetColumn(rightText, 1);
            row.Children.Add(rightText);

            ModelSpecsPanel.Children.Add(row);
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
        var valueBrush = fraction.HasValue && pct == 0
            ? ResolveBrush("StatusCriticalBrush", Colors.IndianRed)
            : accentBrush;
        var pctText = new WpfControls.TextBlock
        {
            Text = fraction.HasValue ? $"{pct}%" : "-",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = valueBrush,
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
            Foreground = valueBrush
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

    private void RenderTokenStatus()
    {
        var successBrush = ResolveBrush("StatusSuccessBrush", Colors.LightGreen);
        var cautionBrush = ResolveBrush("StatusCautionBrush", Colors.Orange);
        var criticalBrush = ResolveBrush("StatusCriticalBrush", Colors.IndianRed);
        var neutralBrush = ResolveBrush("StatusNeutralBrush", Colors.Gray);

        // 1. Access Token (short-lived runtime credential)
        var accessToken = _account.AccessToken;
        var atExpiry = _account.AccessTokenExpiresAt;
        bool hasAccess = !string.IsNullOrWhiteSpace(accessToken);

        if (!hasAccess)
        {
            AccessTokenStatusText.Text = LocalizationService.GetString("AccountDetails_AccessTokenNotFetched", "未获取");
            AccessTokenStatusText.Foreground = neutralBrush;
            AccessTokenStatusDot.Fill = neutralBrush;
        }
        else if (_account.IsAccessTokenExpired)
        {
            AccessTokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenExpired", "已过期");
            AccessTokenStatusText.Foreground = cautionBrush;
            AccessTokenStatusDot.Fill = cautionBrush;
        }
        else if (_account.NeedsRefresh)
        {
            AccessTokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenExpiringSoon", "即将过期");
            AccessTokenStatusText.Foreground = cautionBrush;
            AccessTokenStatusDot.Fill = cautionBrush;
        }
        else
        {
            AccessTokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenValid", "有效");
            AccessTokenStatusText.Foreground = successBrush;
            AccessTokenStatusDot.Fill = successBrush;
        }

        AccessTokenPreviewText.Text = FormatMaskedToken(accessToken);
        AccessTokenLastUsedText.Text = _account.AccessTokenLastUsedAt.HasValue
            ? string.Format(
                LocalizationService.GetString("AccountDetails_TokenLastUsedFormat", "最近使用: {0}"),
                _account.AccessTokenLastUsedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
            : LocalizationService.GetString("AccountDetails_TokenNeverUsed", "最近使用: 未使用");

        if (atExpiry.HasValue)
        {
            var localTime = atExpiry.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            AccessTokenExpiryText.Text = atExpiry.Value <= DateTime.UtcNow
                ? string.Format(LocalizationService.GetString("AccountDetails_TokenExpiredAtFormat", "过期时间: {0} (已过期)"), localTime)
                : string.Format(LocalizationService.GetString("AccountDetails_TokenExpiryWithCountdownFormat", "过期时间: {0} ({1})"), localTime, FormatCountdown(atExpiry.Value));
        }
        else
        {
            AccessTokenExpiryText.Text = LocalizationService.GetString("AccountDetails_TokenExpiryUnknown", "过期时间: 未知");
        }

        // 2. Refresh Token (long-lived base credential)
        var refreshToken = _account.RefreshToken;
        var rtExpiry = _account.RefreshTokenExpiresAt;
        bool hasRefresh = !string.IsNullOrWhiteSpace(refreshToken);

        if (!hasRefresh)
        {
            RefreshTokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenMissing", "缺少 Refresh Token");
            RefreshTokenStatusText.Foreground = criticalBrush;
            RefreshTokenStatusDot.Fill = criticalBrush;
        }
        else if (_account.RefreshTokenRevoked)
        {
            RefreshTokenStatusText.Text = LocalizationService.GetString("AccountDetails_RefreshTokenRevoked", "已失效 (invalid_grant)");
            RefreshTokenStatusText.Foreground = criticalBrush;
            RefreshTokenStatusDot.Fill = criticalBrush;
        }
        else if (rtExpiry.HasValue && rtExpiry.Value <= DateTime.UtcNow)
        {
            RefreshTokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenExpired", "已过期");
            RefreshTokenStatusText.Foreground = criticalBrush;
            RefreshTokenStatusDot.Fill = criticalBrush;
        }
        else
        {
            RefreshTokenStatusText.Text = LocalizationService.GetString("AccountDetails_TokenValid", "有效");
            RefreshTokenStatusText.Foreground = successBrush;
            RefreshTokenStatusDot.Fill = successBrush;
        }

        RefreshTokenPreviewText.Text = FormatMaskedToken(refreshToken);
        RefreshTokenLastUsedText.Text = _account.RefreshTokenLastUsedAt.HasValue
            ? string.Format(
                LocalizationService.GetString("AccountDetails_TokenLastUsedFormat", "最近使用: {0}"),
                _account.RefreshTokenLastUsedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
            : LocalizationService.GetString("AccountDetails_TokenNeverUsed", "最近使用: 未使用");

        if (_account.RefreshTokenRevoked)
        {
            RefreshTokenExpiryText.Text = LocalizationService.GetString("AccountDetails_RefreshTokenRevokedExpiry", "过期时间: 已失效（需重新捕获登录）");
        }
        else if (rtExpiry.HasValue)
        {
            var localTime = rtExpiry.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            RefreshTokenExpiryText.Text = rtExpiry.Value <= DateTime.UtcNow
                ? string.Format(LocalizationService.GetString("AccountDetails_TokenExpiredAtFormat", "过期时间: {0} (已过期)"), localTime)
                : string.Format(LocalizationService.GetString("AccountDetails_TokenExpiryWithCountdownFormat", "过期时间: {0} ({1})"), localTime, FormatCountdown(rtExpiry.Value));
        }
        else if (hasRefresh)
        {
            RefreshTokenExpiryText.Text = LocalizationService.GetString("AccountDetails_RefreshTokenLongLived", "过期时间: 长期有效（未下发过期时间）");
        }
        else
        {
            RefreshTokenExpiryText.Text = LocalizationService.GetString("AccountDetails_TokenExpiryUnknown", "过期时间: 未知");
        }
    }

    private static string FormatMaskedToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "—";
        var trimmed = token.Trim();
        if (trimmed.Length <= 14) return trimmed;
        return $"{trimmed[..8]}…{trimmed[^4..]}";
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
            var (tokenSuccess, quotaSuccess, msg) = await _accountPool.RefreshTokenAndQuotaAsync(_account.Id, _proxyConfig, autoRetry: true);

            if (tokenSuccess)
            {
                var severity = quotaSuccess && string.IsNullOrEmpty(_account.QuotaLastError)
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning;
                SetRefreshStatus(msg, severity, closable: true);
            }
            else
            {
                SetRefreshStatus(msg, InfoBarSeverity.Error, closable: true);
            }

            LoadAccountData(showStoredError: false);
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
