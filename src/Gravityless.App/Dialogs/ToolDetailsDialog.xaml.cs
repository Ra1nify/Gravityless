using System.Windows;
using System.Windows.Controls;
using Gravityless.App.Services;
using Gravityless.Core.Services;

namespace Gravityless.App.Dialogs;

/// <summary>Detail window shared by Skills, Plugins, and MCP servers.</summary>
public partial class ToolDetailsDialog
{
    private readonly object _item;
    private readonly Action? _onEditSkillMd;
    private readonly Action? _onOpenFolder;
    private readonly Func<bool>? _onDelete;

    public ToolDetailsDialog(
        object item,
        Action? onEditSkillMd = null,
        Action? onOpenFolder = null,
        Func<bool>? onDelete = null)
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);

        _item = item;
        _onEditSkillMd = onEditSkillMd;
        _onOpenFolder = onOpenFolder;
        _onDelete = onDelete;

        Configure();
    }

    private void Configure()
    {
        BtnClose.Content = LocalizationService.GetString("Tools_DetailClose", "关闭");
        BtnOpenFolder.Content = LocalizationService.GetString("Tools_DetailOpenFolder", "打开目录");

        switch (_item)
        {
            case SkillItem skill:
                ConfigureSkill(skill);
                break;
            case PluginItem plugin:
                ConfigurePlugin(plugin);
                break;
            case McpServerItem mcp:
                ConfigureMcp(mcp);
                break;
        }
    }

    private void ConfigureSkill(SkillItem skill)
    {
        HeaderIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Sparkle24;
        HeaderIcon.Foreground = Palette.Accent;
        SetHeader(skill.Name, skill.Path);
        SetDescription(skill.Description);

        AddMeta(LocalizationService.GetString("Tools_DetailDirName", "目录名"), skill.DirName);
        AddMeta(LocalizationService.GetString("Tools_DetailUpdated", "更新时间"), skill.LastModifiedText);
        AddMeta(
            LocalizationService.GetString("Tools_DetailSkillMd", "规范文件"),
            skill.HasSkillMd
                ? LocalizationService.GetString("Tools_DetailSkillMdYes", "包含 SKILL.md")
                : LocalizationService.GetString("Tools_DetailSkillMdNo", "不含 SKILL.md（保存时自动创建）"));

        BtnEditSkillMd.Content = skill.HasSkillMd
            ? LocalizationService.GetString("Tools_DetailEditSkillMd", "编辑 SKILL.md")
            : LocalizationService.GetString("Tools_DetailCreateSkillMd", "创建并编辑 SKILL.md");
        BtnEditSkillMd.Visibility = _onEditSkillMd != null ? Visibility.Visible : Visibility.Collapsed;
        BtnOpenFolder.Visibility = _onOpenFolder != null ? Visibility.Visible : Visibility.Collapsed;
        BtnDelete.Content = LocalizationService.GetString("Tools_DetailDelete", "删除");
        BtnDelete.Visibility = _onDelete != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigurePlugin(PluginItem plugin)
    {
        HeaderIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PuzzlePiece24;
        HeaderIcon.Foreground = Palette.Caution;
        SetHeader(plugin.Name, plugin.Path);
        SetDescription(plugin.Description);

        AddMeta(LocalizationService.GetString("Tools_DetailDirName", "目录名"), plugin.DirName);
        AddMeta(LocalizationService.GetString("Tools_DetailUpdated", "更新时间"), plugin.LastModifiedText);
        AddMeta(LocalizationService.GetString("Tools_DetailSkillCount", "包含技能"), plugin.SkillCount.ToString());
        AddMeta(LocalizationService.GetString("Tools_DetailFeatures", "附加内容"), plugin.SummaryTag);

        BtnOpenFolder.Visibility = _onOpenFolder != null ? Visibility.Visible : Visibility.Collapsed;
        BtnDelete.Content = LocalizationService.GetString("Tools_DetailDelete", "删除");
        BtnDelete.Visibility = _onDelete != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureMcp(McpServerItem mcp)
    {
        HeaderIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.PlugConnected24;
        HeaderIcon.Foreground = Palette.Success;
        SetHeader(mcp.Name, mcp.CommandOrUrl);
        SetDescription(mcp.CommandOrUrl);

        AddMeta(LocalizationService.GetString("Tools_DetailTransport", "传输类型"), mcp.TransportType);
        AddMeta(LocalizationService.GetString("Tools_DetailCommand", "命令 / 地址"), mcp.CommandOrUrl);
        AddMeta(LocalizationService.GetString("Tools_DetailArgs", "参数"), mcp.Args);
        AddMeta(LocalizationService.GetString("Tools_DetailEnv", "环境变量"), mcp.EnvSummary);

        if (!string.IsNullOrWhiteSpace(mcp.RawJson))
        {
            RawJsonSection.Visibility = Visibility.Visible;
            RawJsonText.Text = mcp.RawJson;
        }

        BtnDelete.Content = LocalizationService.GetString("Tools_DetailDelete", "删除");
        BtnDelete.Visibility = _onDelete != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetHeader(string name, string? subtitle)
    {
        NameText.Text = name;
        Title = name;
        DialogTitleBar.Title = name;
        SubtitleText.Text = subtitle ?? string.Empty;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetDescription(string? description)
    {
        DescriptionText.Text = string.IsNullOrWhiteSpace(description)
            ? LocalizationService.GetString("Tools_DetailEmpty", "—")
            : description;
    }

    private void AddMeta(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Top
        };

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueText, 1);

        row.Children.Add(labelText);
        row.Children.Add(valueText);
        MetaPanel.Children.Add(row);
    }

    private void OnEditSkillMdClicked(object sender, RoutedEventArgs e)
    {
        _onEditSkillMd?.Invoke();
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        _onOpenFolder?.Invoke();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        // The callback confirms and performs the delete; a true result closes this stale view.
        if (_onDelete != null && _onDelete())
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
