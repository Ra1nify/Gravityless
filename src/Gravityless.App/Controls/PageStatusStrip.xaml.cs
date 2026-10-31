using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Gravityless.App.Services;

namespace Gravityless.App.Controls;

/// <summary>Inline status strip; collapses entirely when idle.</summary>
public partial class PageStatusStrip
{
    private CancellationTokenSource? _cancelSource;

    public PageStatusStrip()
    {
        InitializeComponent();
        CancelButton.Content = LocalizationService.GetString("Common_Cancel", "取消");
    }

    public void ShowBusy(string text)
    {
        SetText(null, text);
    }

    /// <summary>Renders a "[current/total]" prefix when total is positive.</summary>
    public void ShowStep(int current, int total, string text)
    {
        SetText(total > 0 ? $"[{current}/{total}]" : null, text);
    }

    /// <summary>Passing null hides the cancel button.</summary>
    public void AttachCancel(CancellationTokenSource? source)
    {
        _cancelSource = source;
        CancelButton.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Clear()
    {
        _cancelSource = null;
        CancelButton.Visibility = Visibility.Collapsed;
        MessageText.Text = string.Empty;
        Visibility = Visibility.Collapsed;
    }

    private void SetText(string? prefix, string text)
    {
        MessageText.Text = string.IsNullOrEmpty(prefix) ? text : $"{prefix} {text}";
        Visibility = Visibility.Visible;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _cancelSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Operation already finished.
        }

        CancelButton.IsEnabled = false;
    }
}
