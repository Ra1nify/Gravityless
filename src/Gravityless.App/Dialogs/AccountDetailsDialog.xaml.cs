using System.Windows;
using System.Windows.Media;
using Gravityless.App.Services;
using Gravityless.Core.Models;
using Gravityless.Core.Services;
using Wpf.Ui.Controls;

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

        LoadAccountData();
    }

    private void LoadAccountData()
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

        var (accessToken, refreshToken, expiry) = ProxyService.ExtractTokens(_account.CredentialBlob);
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

        // Any unset 5h block falls back to the 7d value, then to "-".
        if (_account.Gemini5hFraction.HasValue)
        {
            var g5h = (int)(_account.Gemini5hFraction.Value * 100);
            Gemini5hProgressBar.Value = g5h;
            Gemini5hPercentText.Text = $"{g5h}%";
            Gemini5hResetText.Text = _account.Gemini5hResetTime.HasValue
                ? FormatResetLine(_account.Gemini5hResetTime.Value, "HH:mm")
                : LocalizationService.GetString("AccountDetails_Rolling5h", "5 小时滚动窗口");
        }
        else if (_account.Gemini7dFraction.HasValue)
        {
            var g7dVal = (int)(_account.Gemini7dFraction.Value * 100);
            Gemini5hProgressBar.Value = g7dVal;
            Gemini5hPercentText.Text = $"{g7dVal}%";
            Gemini5hResetText.Text = LocalizationService.GetString("AccountDetails_Depleted", "已耗尽");
        }
        else
        {
            Gemini5hProgressBar.Value = 0;
            Gemini5hPercentText.Text = "-";
            Gemini5hResetText.Text = LocalizationService.GetString("AccountDetails_NoQuota5h", "暂无 5h 配额缓存");
        }

        if (_account.Gemini7dFraction.HasValue)
        {
            var g7d = (int)(_account.Gemini7dFraction.Value * 100);
            Gemini7dProgressBar.Value = g7d;
            Gemini7dPercentText.Text = $"{g7d}%";
            Gemini7dResetText.Text = _account.Gemini7dResetTime.HasValue
                ? FormatResetLine(_account.Gemini7dResetTime.Value, "MM-dd HH:mm")
                : LocalizationService.GetString("AccountDetails_WeeklyCeiling7d", "7 天周期上限");
        }
        else if (_account.Gemini5hFraction.HasValue)
        {
            Gemini7dProgressBar.Value = 100;
            Gemini7dPercentText.Text = "100%";
            Gemini7dResetText.Text = LocalizationService.GetString("AccountDetails_Normal", "正常");
        }
        else
        {
            Gemini7dProgressBar.Value = 0;
            Gemini7dPercentText.Text = "-";
            Gemini7dResetText.Text = LocalizationService.GetString("AccountDetails_NoQuota7d", "暂无 7d 配额缓存");
        }

        // Any unset 5h block is treated as counted against the weekly quota.
        if (_account.Claude5hFraction.HasValue)
        {
            var c5h = (int)(_account.Claude5hFraction.Value * 100);
            Claude5hProgressBar.Value = c5h;
            Claude5hPercentText.Text = $"{c5h}%";
            Claude5hResetText.Text = _account.Claude5hResetTime.HasValue
                ? FormatResetLine(_account.Claude5hResetTime.Value, "HH:mm")
                : LocalizationService.GetString("AccountDetails_Rolling5h", "5 小时滚动窗口");
        }
        else if (_account.Claude7dFraction.HasValue)
        {
            Claude5hProgressBar.Value = (int)(_account.Claude7dFraction.Value * 100);
            Claude5hPercentText.Text = "✓";
            Claude5hResetText.Text = LocalizationService.GetString("AccountDetails_CountedWeekly", "按周配额计算");
        }
        else
        {
            Claude5hProgressBar.Value = 0;
            Claude5hPercentText.Text = "-";
            Claude5hResetText.Text = LocalizationService.GetString("AccountDetails_NoQuota5h", "暂无 5h 配额缓存");
        }

        if (_account.Claude7dFraction.HasValue)
        {
            var c7d = (int)(_account.Claude7dFraction.Value * 100);
            Claude7dProgressBar.Value = c7d;
            Claude7dPercentText.Text = $"{c7d}%";
            Claude7dResetText.Text = _account.Claude7dResetTime.HasValue
                ? FormatResetLine(_account.Claude7dResetTime.Value, "MM-dd HH:mm")
                : LocalizationService.GetString("AccountDetails_WeeklyCeiling7d", "7 天周期上限");
        }
        else
        {
            Claude7dProgressBar.Value = 0;
            Claude7dPercentText.Text = "-";
            Claude7dResetText.Text = LocalizationService.GetString("AccountDetails_NoQuota7d", "暂无 7d 配额缓存");
        }
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
                SetRefreshStatus(msg, InfoBarSeverity.Success, closable: true);
                LoadAccountData();
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
