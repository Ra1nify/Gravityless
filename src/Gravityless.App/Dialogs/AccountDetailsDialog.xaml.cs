using System.Windows;
using System.Windows.Media;
using Gravityless.App.Services;
using Gravityless.Core.Models;
using Gravityless.Core.Services;

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
        bool isEn = LocalizationService.CurrentLanguage == "en-US";

        DisplayNameBox.Text = string.IsNullOrEmpty(_account.DisplayName) ? _account.Email : _account.DisplayName;
        EmailText.Text = _account.Email;

        InstallationIdText.Text = _account.InstallationId;

        if (!string.IsNullOrEmpty(_account.BoundInstanceId))
        {
            var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == _account.BoundInstanceId);
            if (inst != null)
            {
                BoundInstanceNameText.Text = $"🔵 {inst.DisplayNameWithShortId}";
                BoundInstanceIdText.Text = isEn ? $"Instance ID: {_account.BoundInstanceId}" : $"实例 ID: {_account.BoundInstanceId}";
            }
            else
            {
                BoundInstanceNameText.Text = isEn ? $"🔵 Bound (ID: {_account.BoundInstanceId})" : $"已绑定";
                BoundInstanceIdText.Text = $"ID: {_account.BoundInstanceId}";
            }
            CopyInstanceIdBtn.Visibility = Visibility.Visible;
        }
        else
        {
            BoundInstanceNameText.Text = isEn ? "Not bound" : "未绑定";
            BoundInstanceIdText.Text = isEn ? "Not assigned" : "未分配";
            CopyInstanceIdBtn.Visibility = Visibility.Collapsed;
        }

        var (accessToken, refreshToken, expiry) = ProxyService.ExtractTokens(_account.CredentialBlob);
        bool hasRefresh = !string.IsNullOrWhiteSpace(refreshToken);

        if (hasRefresh)
        {
            TokenStatusText.Text = isEn 
                ? "✓ Refresh Token valid (Auto-refresh & quiet renewal enabled)" 
                : "有效";
            TokenStatusDot.Fill = Brushes.LightGreen;
        }
        else
        {
            TokenStatusText.Text = isEn 
                ? "⚠️ Missing Refresh Token (Re-login required)" 
                : "缺少 Refresh Token";
            TokenStatusDot.Fill = Brushes.Orange;
        }

        TokenExpiryText.Text = expiry.HasValue
            ? (isEn ? $"Token Expiry: {expiry.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : $"过期时间: {expiry.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
            : (isEn ? "Token Expiry: Unknown" : "过期时间: 未知");

        TokenLastActiveText.Text = isEn
            ? $"Last Active / Refreshed: {_account.LastActiveAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : $"最近活动: {_account.LastActiveAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        if (_account.QuotaLastFetchedAt.HasValue)
        {
            QuotaLastFetchedText.Text = isEn 
                ? $"Synced: {_account.QuotaLastFetchedAt.Value.ToLocalTime():HH:mm:ss}" 
                : $"同步于: {_account.QuotaLastFetchedAt.Value.ToLocalTime():HH:mm:ss}";
        }
        else
        {
            QuotaLastFetchedText.Text = isEn ? "Not cached" : "未同步";
        }

        // Any unset 5h block falls back to the 7d value, then to "-".
        if (_account.Gemini5hFraction.HasValue)
        {
            var g5h = (int)(_account.Gemini5hFraction.Value * 100);
            Gemini5hProgressBar.Value = g5h;
            Gemini5hPercentText.Text = $"{g5h}%";
            Gemini5hResetText.Text = _account.Gemini5hResetTime.HasValue
                ? (isEn ? $"Reset: {_account.Gemini5hResetTime.Value.ToLocalTime():HH:mm} ({FormatCountdown(_account.Gemini5hResetTime.Value, isEn)})"
                        : $"重置: {_account.Gemini5hResetTime.Value.ToLocalTime():HH:mm} ({FormatCountdown(_account.Gemini5hResetTime.Value, isEn)})")
                : (isEn ? "Rolling window (5h)" : "5小时滚动窗口");
        }
        else if (_account.Gemini7dFraction.HasValue)
        {
            var g7dVal = (int)(_account.Gemini7dFraction.Value * 100);
            Gemini5hProgressBar.Value = g7dVal;
            Gemini5hPercentText.Text = $"{g7dVal}%";
            Gemini5hResetText.Text = isEn ? "Depleted" : "已耗尽";
        }
        else
        {
            Gemini5hProgressBar.Value = 0;
            Gemini5hPercentText.Text = "-";
            Gemini5hResetText.Text = isEn ? "No 5h quota cached" : "暂无 5h 配额缓存";
        }

        if (_account.Gemini7dFraction.HasValue)
        {
            var g7d = (int)(_account.Gemini7dFraction.Value * 100);
            Gemini7dProgressBar.Value = g7d;
            Gemini7dPercentText.Text = $"{g7d}%";
            Gemini7dResetText.Text = _account.Gemini7dResetTime.HasValue
                ? (isEn ? $"Reset: {_account.Gemini7dResetTime.Value.ToLocalTime():MM-dd HH:mm} ({FormatCountdown(_account.Gemini7dResetTime.Value, isEn)})"
                        : $"重置: {_account.Gemini7dResetTime.Value.ToLocalTime():MM-dd HH:mm} ({FormatCountdown(_account.Gemini7dResetTime.Value, isEn)})")
                : (isEn ? "Weekly ceiling (7d)" : "7天周期上限");
        }
        else if (_account.Gemini5hFraction.HasValue)
        {
            Gemini7dProgressBar.Value = 100;
            Gemini7dPercentText.Text = "100%";
            Gemini7dResetText.Text = isEn ? "Normal" : "正常";
        }
        else
        {
            Gemini7dProgressBar.Value = 0;
            Gemini7dPercentText.Text = "-";
            Gemini7dResetText.Text = isEn ? "No 7d quota cached" : "暂无 7d 配额缓存";
        }

        // Any unset 5h block is treated as counted against the weekly quota.
        if (_account.Claude5hFraction.HasValue)
        {
            var c5h = (int)(_account.Claude5hFraction.Value * 100);
            Claude5hProgressBar.Value = c5h;
            Claude5hPercentText.Text = $"{c5h}%";
            Claude5hResetText.Text = _account.Claude5hResetTime.HasValue
                ? (isEn ? $"Reset: {_account.Claude5hResetTime.Value.ToLocalTime():HH:mm} ({FormatCountdown(_account.Claude5hResetTime.Value, isEn)})"
                        : $"重置: {_account.Claude5hResetTime.Value.ToLocalTime():HH:mm} ({FormatCountdown(_account.Claude5hResetTime.Value, isEn)})")
                : (isEn ? "Rolling window (5h)" : "5小时滚动窗口");
        }
        else if (_account.Claude7dFraction.HasValue)
        {
            Claude5hProgressBar.Value = (int)(_account.Claude7dFraction.Value * 100);
            Claude5hPercentText.Text = "✓";
            Claude5hResetText.Text = isEn ? "Counted against weekly quota" : "按周配额计算";
        }
        else
        {
            Claude5hProgressBar.Value = 0;
            Claude5hPercentText.Text = "-";
            Claude5hResetText.Text = isEn ? "No 5h quota cached" : "暂无 5h 配额缓存";
        }

        if (_account.Claude7dFraction.HasValue)
        {
            var c7d = (int)(_account.Claude7dFraction.Value * 100);
            Claude7dProgressBar.Value = c7d;
            Claude7dPercentText.Text = $"{c7d}%";
            Claude7dResetText.Text = _account.Claude7dResetTime.HasValue
                ? (isEn ? $"Reset: {_account.Claude7dResetTime.Value.ToLocalTime():MM-dd HH:mm} ({FormatCountdown(_account.Claude7dResetTime.Value, isEn)})"
                        : $"重置: {_account.Claude7dResetTime.Value.ToLocalTime():MM-dd HH:mm} ({FormatCountdown(_account.Claude7dResetTime.Value, isEn)})")
                : (isEn ? "Weekly ceiling (7d)" : "7天周期上限");
        }
        else
        {
            Claude7dProgressBar.Value = 0;
            Claude7dPercentText.Text = "-";
            Claude7dResetText.Text = isEn ? "No 7d quota cached" : "暂无 7d 配额缓存";
        }
    }

    private static string FormatCountdown(DateTime targetUtc, bool isEn)
    {
        var remaining = targetUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return isEn ? "Resetting soon" : "即将重置";
        if (remaining.TotalHours >= 24)
        {
            return isEn 
                ? $"in {(int)remaining.TotalDays}d {remaining.Hours}h" 
                : $"{(int)remaining.TotalDays}天{remaining.Hours}小时后";
        }
        return isEn 
            ? $"in {(int)remaining.TotalHours}h {remaining.Minutes}m" 
            : $"{(int)remaining.TotalHours}小时{remaining.Minutes}分后";
    }

    private void OnSaveDisplayNameClicked(object sender, RoutedEventArgs e)
    {
        var newName = DisplayNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            _account.DisplayName = newName;
            _accountPool.UpdateAccountDisplayName(_account.Id, newName);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("Common_Success", "成功"),
                LocalizationService.GetString("AccountDetails_NameSaved", "账号名称已成功保存！"),
                this);
        }
    }

    private void OnCopyInstallationIdClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_account.InstallationId);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("Common_Success", "成功"),
                LocalizationService.GetString("AccountDetails_CopiedFingerprint", "设备指纹 ID 已成功复制到剪贴板！"),
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
                LocalizationService.GetString("Common_Success", "成功"),
                LocalizationService.GetString("AccountDetails_CopiedInstanceId", "完整实例 ID 已成功复制到剪贴板！"),
                this);
        }
        catch { }
    }

    private async void OnRefreshTokenClicked(object sender, RoutedEventArgs e)
    {
        bool isEn = LocalizationService.CurrentLanguage == "en-US";
        RefreshTokenBtn.IsEnabled = false;
        RefreshFeedbackText.Visibility = Visibility.Visible;
        RefreshFeedbackText.Foreground = Brushes.LightGray;
        RefreshFeedbackText.Text = isEn 
            ? "⏳ Renewing Access Token and synchronizing model quotas..." 
            : "正在刷新 Token...";

        try
        {
            var (success, msg) = await _accountPool.RefreshTokenAsync(_account.Id, _proxyConfig, autoRetry: true);

            if (success)
            {
                RefreshFeedbackText.Text = $"✓ {msg}";
                RefreshFeedbackText.Foreground = Brushes.LightGreen;
                LoadAccountData();
            }
            else
            {
                RefreshFeedbackText.Text = $"✗ {msg}";
                RefreshFeedbackText.Foreground = Brushes.OrangeRed;
            }
        }
        catch (Exception ex)
        {
            // async void handler: an unhandled exception would crash the UI thread.
            RefreshFeedbackText.Text = isEn
                ? $"✗ Refresh failed: {ex.Message}"
                : $"刷新失败: {ex.Message}";
            RefreshFeedbackText.Foreground = Brushes.OrangeRed;
        }
        finally
        {
            RefreshTokenBtn.IsEnabled = true;
        }
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
