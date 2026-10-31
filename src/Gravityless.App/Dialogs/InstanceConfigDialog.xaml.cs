using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Gravityless.App.Services;
using Gravityless.Core.Models;
using Wpf.Ui.Controls;

namespace Gravityless.App.Dialogs;

public partial class InstanceConfigDialog : FluentWindow
{
    private readonly InstanceProfile _profile;
    private readonly string _sandboxDir;
    private readonly bool _isCreateMode;
    private readonly List<ProjectSelectableItem> _selectableProjects = [];
    private readonly ICollectionView _projectsView;
    public bool Saved { get; private set; }

    public InstanceConfigDialog(
        InstanceProfile profile, 
        List<AccountProfile> availableAccounts, 
        string sandboxDir, 
        bool isCreateMode = false,
        List<ProjectProfile>? availableProjects = null)
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);
        _profile = profile;
        _sandboxDir = sandboxDir;
        _isCreateMode = isCreateMode;

        if (_isCreateMode)
        {
            var title = LocalizationService.GetString("Instances_CreateDialogTitle", "新建实例");
            Title = title;
            DialogTitleBar.Title = title;
            SaveButton.Content = LocalizationService.GetString("Instances_CreateConfirmBtn", "创建实例");
            SandboxSeparator.Visibility = Visibility.Collapsed;
            OpenSandboxButton.Visibility = Visibility.Collapsed;
        }

        InstanceIdText.Text = profile.Id;
        InstanceNameBox.Text = profile.Name;

        var guestLabel = LocalizationService.GetString("Instances_NoAccountGuest", "(未绑定账号 / 游客沙盒模式)");
        var comboList = new List<AccountProfile>
        {
            new AccountProfile { Id = "", Email = guestLabel }
        };
        comboList.AddRange(availableAccounts);
        AccountCombo.ItemsSource = comboList;
        AccountCombo.SelectedValue = profile.BoundAccountId ?? "";

        _selectableProjects.Clear();
        if (availableProjects != null)
        {
            foreach (var p in availableProjects)
            {
                var isSelected = profile.BoundProjectIds.Contains(p.Id);
                _selectableProjects.Add(new ProjectSelectableItem(p, isSelected));
            }
        }

        _projectsView = CollectionViewSource.GetDefaultView(_selectableProjects);
        _projectsView.Filter = FilterProjectItem;
        ProjectsCheckList.ItemsSource = _projectsView;
        UpdateSelectedCountText();

        ShareSkillsSwitch.IsChecked = profile.ShareSkills;
        SharePluginsSwitch.IsChecked = profile.SharePlugins;
        ShareMcpSwitch.IsChecked = profile.ShareMcp;
    }

    private bool FilterProjectItem(object obj)
    {
        if (obj is not ProjectSelectableItem item) return false;
        var query = ProjectSearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.ShortId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(item.Path) && item.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnProjectSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _projectsView?.Refresh();
    }

    private void OnProjectCheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelectedCountText();
    }

    private void UpdateSelectedCountText()
    {
        var selectedCount = _selectableProjects.Count(p => p.IsSelected);
        var totalCount = _selectableProjects.Count;
        if (SelectedProjectsCountText != null)
        {
            var format = LocalizationService.GetString("Instances_ConfigSelectedCountFormat", "已选 {0} 项 (共 {1} 项)");
            SelectedProjectsCountText.Text = string.Format(format, selectedCount, totalCount);
        }
    }

    private void OnCopyInstanceIdClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_profile.Id);
            ModernDialog.ShowSuccess(
                LocalizationService.GetString("Common_Success", "成功"),
                LocalizationService.GetString("Instances_CopiedInstanceId", "完整实例 ID 已成功复制到剪贴板！"),
                this);
        }
        catch { }
    }

    private void OnOpenSandboxClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_sandboxDir);
        try
        {
            Process.Start("explorer.exe", _sandboxDir);
        }
        catch { }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Saved = false;
        DialogResult = false;
        Close();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        var newName = InstanceNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            _profile.Name = newName;
        }

        var selAcc = AccountCombo.SelectedValue as string;
        _profile.BoundAccountId = string.IsNullOrEmpty(selAcc) ? null : selAcc;

        _profile.BoundProjectIds = _selectableProjects
            .Where(p => p.IsSelected)
            .Select(p => p.Id)
            .ToList();

        _profile.ShareSkills = ShareSkillsSwitch.IsChecked == true;
        _profile.SharePlugins = SharePluginsSwitch.IsChecked == true;
        _profile.ShareMcp = ShareMcpSwitch.IsChecked == true;

        Saved = true;
        DialogResult = true;
        Close();
    }
}

public class ProjectSelectableItem : INotifyPropertyChanged
{
    public ProjectProfile Profile { get; }
    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public string Path => Profile.Path;
    public string ShortId => Profile.ShortId;
    public string DisplayNameWithShortId => Profile.DisplayNameWithShortId;
    public string DisplayText => string.IsNullOrEmpty(Profile.Path)
        ? Profile.DisplayNameWithShortId
        : $"{Profile.Name} ({Profile.ShortId}) - {Profile.Path}";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectSelectableItem(ProjectProfile profile, bool isSelected)
    {
        Profile = profile;
        _isSelected = isSelected;
    }
}
