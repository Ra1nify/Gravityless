using Gravityless.App.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Gravityless.Core.Models;
using Gravityless.Core.Services;
using Wpf.Ui.Controls;

namespace Gravityless.App.Dialogs;

public partial class ProjectActionsDialog : FluentWindow
{
    private readonly ProjectService _projectService;
    private readonly InstanceService _instanceService;
    private readonly ProjectProfile _project;
    public bool HasModified { get; private set; }

    public ProjectActionsDialog(
        ProjectService projectService, 
        InstanceService instanceService, 
        ProjectProfile project, 
        Window owner)
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);
        Owner = owner;

        _projectService = projectService;
        _instanceService = instanceService;
        _project = project;

        DialogTitleBar.Title = string.Format(
            LocalizationService.GetString("ProjectActions_DialogTitleFormat", "项目操作与管理 - {0}"),
            _project.Name);

        LoadProjectData();
    }

    private void LoadProjectData()
    {
        ProjectNameText.Text = _project.Name;
        ProjectShortIdText.Text = _project.Id.Length >= 8 ? _project.Id[..8] : _project.Id;
        ProjectPathText.Text = _project.Path;

        StatusDot.Fill = _project.IsRunning ? Brushes.Orange : Brushes.LightGreen;
        StatusText.Text = _project.IsRunning
            ? string.Format(LocalizationService.GetString("ProjectActions_StatusRunning", "运行中 (PID: {0})"), _project.ProcessId)
            : LocalizationService.GetString("ProjectActions_StatusIdle", "闲置可用");
        StatusText.Foreground = _project.IsRunning ? Brushes.Orange : Brushes.LightGreen;

        var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == _project.RunningInstanceId);
        BoundInstanceText.Text = inst != null
            ? string.Format(LocalizationService.GetString("ProjectActions_BoundInstanceFormat", "绑定实例: {0}"), inst.Name)
            : LocalizationService.GetString("ProjectActions_NoBoundInstance", "未绑定运行实例");

        ConvoStatsText.Text = string.Format(
            LocalizationService.GetString("ProjectActions_ConversationCountFormat", "{0} 个历史会话"),
            _project.ConversationCount);
    }

    private void OnCopyPathClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_project.Path);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("ProjectActions_CopiedTitle", "复制成功"),
                string.Format(LocalizationService.GetString("ProjectActions_CopiedMessageFormat", "项目根目录路径已复制:\n{0}"), _project.Path),
                this);
        }
        catch (Exception ex)
        {
            ModernDialog.ShowError(LocalizationService.GetString("ProjectActions_CopyFailedTitle", "复制失败"), ex.Message, this);
        }
    }

    private void OnOpenConversationsClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new ProjectConversationsDialog(_projectService, _project, this);
        dlg.ShowDialog();
        _projectService.RefreshProjectMemoryStats(_project);
        LoadProjectData();
        HasModified = true;
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_project.Path))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", _project.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ModernDialog.ShowError(LocalizationService.GetString("ProjectActions_OpenFolderFailedTitle", "打开目录失败"), ex.Message, this);
            }
        }
        else
        {
            ModernDialog.ShowWarning(
                LocalizationService.GetString("Common_Notice", "提示"),
                string.Format(LocalizationService.GetString("ProjectActions_PathMissingMessageFormat", "本地项目路径不存在或已失效:\n{0}"), _project.Path),
                this);
        }
    }

    private void OnOpenMemoryClicked(object sender, RoutedEventArgs e)
    {
        var memDir = _projectService.GetProjectMemoryDir(_project.Id);
        Directory.CreateDirectory(memDir);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", memDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ModernDialog.ShowError(LocalizationService.GetString("ProjectActions_OpenMemoryFailedTitle", "打开记忆目录失败"), ex.Message, this);
        }
    }

    private void OnDuplicateClicked(object sender, RoutedEventArgs e)
    {
        var newName = ModernDialog.Prompt(
            LocalizationService.GetString("ProjectActions_ClonePromptTitle", "克隆项目分支"), 
            string.Format(
                LocalizationService.GetString(
                    "ProjectActions_ClonePromptMessageFormat",
                    "即将为项目【{0}】创建独立的分支副本（完整深拷贝其专属记忆库与项目映射）。\n请输入新分支项目的名称："),
                _project.Name), 
            string.Format(LocalizationService.GetString("ProjectActions_CloneDefaultNameFormat", "{0} (副本)"), _project.Name), 
            owner: this);

        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == _project.Name) return;

        var clone = _projectService.DuplicateProject(_project.Id, newName.Trim(), out var err);
        if (clone != null)
        {
            HasModified = true;
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("ProjectActions_CloneSuccessTitle", "分支克隆成功"),
                string.Format(LocalizationService.GetString("ProjectActions_CloneSuccessFormat", "项目分支【{0}】已就绪，拥有完全隔离的独立记忆库副本！"), clone.Name),
                owner: this);
        }
        else
        {
            ModernDialog.ShowError(LocalizationService.GetString("ProjectActions_CloneFailedTitle", "克隆失败"), err, owner: this);
        }
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        var newName = ModernDialog.Prompt(
            LocalizationService.GetString("ProjectActions_RenamePromptTitle", "重命名项目"), 
            string.Format(LocalizationService.GetString("ProjectActions_RenamePromptMessageFormat", "请输入项目【{0}】的新名称："), _project.Name), 
            _project.Name, 
            owner: this);

        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == _project.Name) return;

        if (_projectService.RenameProject(_project.Id, newName.Trim(), out var err))
        {
            HasModified = true;
            LoadProjectData();
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("ProjectActions_RenameSuccessTitle", "修改成功"),
                LocalizationService.GetString("ProjectActions_RenameSuccessMessage", "项目名称已成功更新！"),
                this);
        }
        else
        {
            ModernDialog.ShowError(LocalizationService.GetString("ProjectActions_RenameFailedTitle", "重命名失败"), err, owner: this);
        }
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_project.IsRunning)
        {
            ModernDialog.ShowWarning(
                LocalizationService.GetString("ProjectActions_CannotDeleteTitle", "无法删除"),
                string.Format(
                    LocalizationService.GetString("ProjectActions_CannotDeleteFormat", "项目【{0}】当前正在运行中 (PID: {1})，请先停止运行后再执行删除。"),
                    _project.Name,
                    _project.ProcessId),
                owner: this);
            return;
        }

        var confirmMessage = string.Format(
            LocalizationService.GetString(
                "ProjectActions_ConfirmDeleteFormat",
                "确定要从名录中移除项目【{0}】吗？\n\n其专属记忆沙盒与历史会话将被清理，本地源码文件保留。"),
            _project.Name);

        if (ModernDialog.Confirm(
                LocalizationService.GetString("ProjectActions_ConfirmDeleteTitle", "确认移除项目"),
                confirmMessage,
                isDestructive: true,
                owner: this))
        {
            if (_projectService.DeleteProject(_project.Id, out var err))
            {
                HasModified = true;
                Close();
            }
            else
            {
                ModernDialog.ShowError(LocalizationService.GetString("ProjectActions_DeleteFailedTitle", "删除失败"), err, owner: this);
            }
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
