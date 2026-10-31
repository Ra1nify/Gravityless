using Gravityless.App.Services;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Gravityless.App.Dialogs;

public enum ModernDialogType
{
    Info,
    Success,
    Warning,
    Error,
    Question
}

public partial class ModernDialogWindow : FluentWindow
{
    public bool Confirmed { get; private set; }
    public string InputText => DialogInputBox.Text;

    public ModernDialogWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);
    }

    public void Configure(
        string title, 
        string heading, 
        string message, 
        ModernDialogType type, 
        string primaryText = "确定", 
        string? secondaryText = null,
        bool isDestructive = false,
        bool showInput = false,
        string? defaultInput = null)
    {
        DialogTitleBar.Title = title;
        DialogHeadingText.Text = heading;
        DialogMessageText.Text = message;
        PrimaryBtn.Content = primaryText;

        if (isDestructive)
        {
            PrimaryBtn.Appearance = ControlAppearance.Danger;
        }

        if (!string.IsNullOrEmpty(secondaryText))
        {
            SecondaryBtn.Content = secondaryText;
            SecondaryBtn.Visibility = Visibility.Visible;
        }
        else
        {
            SecondaryBtn.Visibility = Visibility.Collapsed;
        }

        if (showInput)
        {
            DialogInputBox.Visibility = Visibility.Visible;
            DialogInputBox.Text = defaultInput ?? string.Empty;
            DialogInputBox.Focus();
            DialogInputBox.SelectAll();
        }

        switch (type)
        {
            case ModernDialogType.Success:
                DialogIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                DialogIcon.Foreground = Palette.Success;
                break;
            case ModernDialogType.Warning:
                DialogIcon.Symbol = SymbolRegular.Warning24;
                DialogIcon.Foreground = Palette.Caution;
                break;
            case ModernDialogType.Error:
                DialogIcon.Symbol = SymbolRegular.DismissCircle24;
                DialogIcon.Foreground = Palette.Critical;
                break;
            case ModernDialogType.Question:
                DialogIcon.Symbol = SymbolRegular.QuestionCircle24;
                DialogIcon.Foreground = Palette.Accent;
                break;
            case ModernDialogType.Info:
            default:
                DialogIcon.Symbol = SymbolRegular.Info24;
                DialogIcon.Foreground = Palette.Accent;
                break;
        }
    }

    private void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        TryClose(true);
    }

    private void OnSecondaryClicked(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        TryClose(false);
    }

    // DialogResult closes the window but throws when shown non-modally; closing is then done explicitly.
    private void TryClose(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Non-modal window has no DialogResult.
        }

        if (IsVisible)
        {
            Close();
        }
    }
}
