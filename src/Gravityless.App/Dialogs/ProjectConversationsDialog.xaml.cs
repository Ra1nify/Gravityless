using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Gravityless.App.Services;
using Gravityless.Core.Models;
using Gravityless.Core.Services;
using Wpf.Ui.Controls;

namespace Gravityless.App.Dialogs;

public partial class ProjectConversationsDialog : FluentWindow
{
    private readonly ProjectService _projectService;
    private readonly ProjectProfile _project;
    private List<ConversationSummaryItem> _allConversations = new();
    private readonly ObservableCollection<ConversationSummaryItem> _displayConversations = new();

    public ProjectConversationsDialog(ProjectService projectService, ProjectProfile project, Window? owner = null)
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);
        if (owner != null) Owner = owner;

        _projectService = projectService;
        _project = project;

        ConversationsListView.ItemsSource = _displayConversations;

        ProjectTitleText.Text = _project.Name;
        ProjectPathText.Text = _project.Path;
        DialogTitleBar.Title = string.Format(
            LocalizationService.GetString("ProjectConversations_DialogTitleFormat", "历史会话 - {0}"),
            _project.Name);

        LoadConversations();
    }

    private void LoadConversations()
    {
        _allConversations = _projectService.GetConversations(_project.Id);
        ProjectConvoCountBadge.Text = string.Format(
            LocalizationService.GetString("ProjectConversations_CountFormat", "共 {0} 个历史会话"),
            _allConversations.Count);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _displayConversations.Clear();
        var q = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";

        foreach (var c in _allConversations)
        {
            if (string.IsNullOrEmpty(q)
                || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.ConversationId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Preview.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                _displayConversations.Add(c);
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        LoadConversations();
    }

    private void OnPreviewItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string convoId })
        {
            var dlg = new ConversationPreviewDialog(_projectService, _project, convoId, this);
            dlg.ShowDialog();
            LoadConversations();
        }
    }

    private void OnRenameItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string convoId })
        {
            var item = _allConversations.FirstOrDefault(c => c.ConversationId == convoId);
            if (item == null) return;

            var newTitle = ModernDialog.Prompt(
                LocalizationService.GetString("ProjectConversations_RenamePromptTitle", "修改会话标题"), 
                LocalizationService.GetString("ProjectConversations_RenamePromptMessage", "请输入新的会话标题："), 
                item.Title, 
                owner: this);

            if (string.IsNullOrWhiteSpace(newTitle) || newTitle.Trim() == item.Title) return;

            if (_projectService.UpdateConversationTitle(_project.Id, convoId, newTitle.Trim(), out var err))
            {
                LoadConversations();
            }
            else
            {
                ModernDialog.ShowError(
                    LocalizationService.GetString("ProjectConversations_RenameFailedTitle", "修改失败"),
                    err ?? LocalizationService.GetString("ProjectConversations_UnknownError", "未知错误"),
                    this);
            }
        }
    }

    private void OnCopyIdClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string convoId })
        {
            try
            {
                Clipboard.SetText(convoId);
                ModernDialog.ShowSuccess(
                    LocalizationService.GetString("ProjectConversations_CopiedTitle", "复制成功"),
                    string.Format(LocalizationService.GetString("ProjectConversations_CopiedMessageFormat", "会话 ID 已复制到剪贴板:\n{0}"), convoId),
                    this);
            }
            catch (Exception ex)
            {
                ModernDialog.ShowError(LocalizationService.GetString("ProjectConversations_CopyFailedTitle", "复制失败"), ex.Message, this);
            }
        }
    }

    private void OnOpenBrainFolderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string convoId })
        {
            var dir = _projectService.GetConversationBrainDir(_project.Id, convoId);
            if (Directory.Exists(dir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    ModernDialog.ShowError(
                        LocalizationService.GetString("ProjectConversations_OpenFolderFailedTitle", "打开目录失败"),
                        ex.Message,
                        this);
                }
            }
            else
            {
                ModernDialog.ShowWarning(
                    LocalizationService.GetString("Common_Notice", "提示"),
                    string.Format(LocalizationService.GetString("ProjectConversations_BrainMissingFormat", "未检测到对应的脑库子目录:\n{0}"), dir),
                    this);
            }
        }
    }

    private void OnDeleteItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string convoId })
        {
            var item = _allConversations.FirstOrDefault(c => c.ConversationId == convoId);
            var title = item != null ? item.Title : convoId;

            var confirmMessage = string.Format(
                LocalizationService.GetString(
                    "ProjectConversations_ConfirmDeleteFormat",
                    "确定要从记忆库中删除该会话记录吗？\n\n标题: {0}\nID: {1}\n\n注意：该会话的全部记录与脑库资产将被永久清理。"),
                title,
                convoId);

            if (ModernDialog.Confirm(
                    LocalizationService.GetString("ProjectConversations_ConfirmDeleteTitle", "确认删除会话"),
                    confirmMessage,
                    isDestructive: true,
                    owner: this))
            {
                if (_projectService.DeleteConversation(_project.Id, convoId, out var err))
                {
                    LoadConversations();
                    ModernDialog.ShowSuccess(
                        LocalizationService.GetString("ProjectConversations_DeletedTitle", "删除成功"),
                        LocalizationService.GetString("ProjectConversations_DeletedMessage", "已成功移除该会话记录及关联资产。"),
                        this);
                }
                else
                {
                    ModernDialog.ShowError(
                        LocalizationService.GetString("ProjectConversations_DeleteFailedTitle", "删除失败"),
                        err ?? LocalizationService.GetString("ProjectConversations_UnknownError", "未知错误"),
                        this);
                }
            }
        }
    }

    private void OnExportAllClicked(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.GetString("ProjectConversations_ExportFolderTitle", "选择全部会话 Markdown 导出目录")
        };

        if (ofd.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(ofd.FolderName))
        {
            if (_projectService.ExportAllConversationsToMarkdown(_project.Id, ofd.FolderName, out var count, out var err))
            {
                ModernDialog.ShowSuccess(
                    LocalizationService.GetString("ProjectConversations_ExportSuccessTitle", "导出成功"),
                    string.Format(
                        LocalizationService.GetString("ProjectConversations_ExportSuccessFormat", "成功导出 {0} 个历史会话为 Markdown 文档至:\n{1}"),
                        count,
                        ofd.FolderName),
                    this);
            }
            else
            {
                ModernDialog.ShowError(LocalizationService.GetString("ProjectConversations_ExportFailedTitle", "导出失败"), err, this);
            }
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
