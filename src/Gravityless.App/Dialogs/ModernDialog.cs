using System.Windows;
using Gravityless.App.Services;

namespace Gravityless.App.Dialogs;

public static class ModernDialog
{
    private static Window? GetActiveWindow()
    {
        return Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible) 
               ?? (Application.Current?.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null);
    }

    private static void AttachOwner(ModernDialogWindow dlg, Window? owner)
    {
        owner ??= GetActiveWindow();
        if (owner != null && owner != dlg && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // Reset Topmost on close so the instance does not keep the flag.
            dlg.Topmost = true;
            dlg.Closed += (_, _) => dlg.Topmost = false;
        }
    }

    public static void ShowInfo(string title, string message, Window? owner = null)
    {
        var dlg = new ModernDialogWindow();
        AttachOwner(dlg, owner);
        dlg.Configure(title, title, message, ModernDialogType.Info, LocalizationService.GetString("Common_Confirm", "确定"));
        dlg.ShowDialog();
    }

    public static void ShowSuccess(string title, string message, Window? owner = null)
    {
        var dlg = new ModernDialogWindow();
        AttachOwner(dlg, owner);
        dlg.Configure(title, title, message, ModernDialogType.Success, LocalizationService.GetString("Common_Confirm", "确定"));
        dlg.ShowDialog();
    }

    public static void ShowWarning(string title, string message, Window? owner = null)
    {
        var dlg = new ModernDialogWindow();
        AttachOwner(dlg, owner);
        dlg.Configure(title, title, message, ModernDialogType.Warning, LocalizationService.GetString("Common_Confirm", "确定"));
        dlg.ShowDialog();
    }

    public static void ShowError(string title, string message, Window? owner = null)
    {
        var dlg = new ModernDialogWindow();
        AttachOwner(dlg, owner);
        dlg.Configure(title, title, message, ModernDialogType.Error, LocalizationService.GetString("Common_Confirm", "确定"));
        dlg.ShowDialog();
    }

    public static bool Confirm(string title, string message, string? confirmText = null, string? cancelText = null, bool isDestructive = false, Window? owner = null)
    {
        confirmText ??= LocalizationService.GetString("Common_Confirm", "确定");
        cancelText ??= LocalizationService.GetString("Common_Cancel", "取消");
        var dlg = new ModernDialogWindow();
        AttachOwner(dlg, owner);
        dlg.Configure(title, title, message, isDestructive ? ModernDialogType.Warning : ModernDialogType.Question, confirmText, cancelText, isDestructive);
        return dlg.ShowDialog() == true && dlg.Confirmed;
    }

    public static string? Prompt(string title, string message, string defaultText = "", string? confirmText = null, string? cancelText = null, Window? owner = null)
    {
        confirmText ??= LocalizationService.GetString("Common_Confirm", "确定");
        cancelText ??= LocalizationService.GetString("Common_Cancel", "取消");
        var dlg = new ModernDialogWindow();
        AttachOwner(dlg, owner);
        dlg.Configure(title, title, message, ModernDialogType.Question, confirmText, cancelText, showInput: true, defaultInput: defaultText);
        return dlg.ShowDialog() == true && dlg.Confirmed ? dlg.InputText : null;
    }
}
