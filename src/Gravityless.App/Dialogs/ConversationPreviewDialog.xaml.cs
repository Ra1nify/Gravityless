using Gravityless.App.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Gravityless.Core.Models;
using Gravityless.Core.Services;
using Wpf.Ui.Controls;

namespace Gravityless.App.Dialogs;

public partial class ConversationPreviewDialog : FluentWindow
{
    private readonly ProjectService _projectService;
    private readonly ProjectProfile _project;
    private string _conversationId;
    private ConversationSummaryItem? _currentSummary;
    private List<TranscriptDisplayItem> _allSteps = new();
    private bool _isClosed;

    public ConversationPreviewDialog(
        ProjectService projectService, 
        ProjectProfile project, 
        string? conversationId = null, 
        Window? owner = null)
    {
        InitializeComponent();
        ThemeService.ApplyTheme(this);
        if (owner != null) Owner = owner;

        _projectService = projectService;
        _project = project;
        _conversationId = conversationId ?? string.Empty;

        TranscriptListBox.ItemsSource = Array.Empty<TranscriptDisplayItem>();

        Loaded += async (_, _) =>
        {
            try
            {
                await LoadConversationAsync();
            }
            catch (Exception ex)
            {
                // Async lambda has no caller to observe the exception.
                LoadingPanel.Visibility = Visibility.Collapsed;
                EmptyNoticeText.Text = string.Format(
                    LocalizationService.GetString("ConversationPreview_LoadFailedFormat", "加载会话失败: {0}"),
                    ex.Message);
                EmptyNoticeText.Visibility = Visibility.Visible;
            }
        };

        Closed += (_, _) => _isClosed = true;
    }

    private async Task LoadConversationAsync()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        TranscriptListBox.Visibility = Visibility.Collapsed;
        EmptyNoticeText.Visibility = Visibility.Collapsed;
        ExportSingleBtn.IsEnabled = false;

        var convos = await Task.Run(() => _projectService.GetConversations(_project.Id));
        if (string.IsNullOrEmpty(_conversationId))
        {
            var first = convos.FirstOrDefault();
            if (first != null)
            {
                _conversationId = first.ConversationId;
            }
        }

        _currentSummary = convos.FirstOrDefault(c => c.ConversationId == _conversationId);

        if (_currentSummary != null)
        {
            ConversationTitleText.Text = string.IsNullOrWhiteSpace(_currentSummary.Title)
                ? LocalizationService.GetString("ConversationPreview_UntitledConversation", "会话")
                : _currentSummary.Title;

            ConversationMetaText.Text = string.Format(
                LocalizationService.GetString("ConversationPreview_MetaFormat", "会话 ID: {0}  ·  更新: {1}  ·  共 {2} 步"),
                _currentSummary.ConversationId,
                _currentSummary.FormattedTime,
                _currentSummary.StepCount);

            DialogTitleBar.Title = string.Format(
                LocalizationService.GetString("ConversationPreview_DialogTitleFormat", "会话思维流预览 - {0}"),
                ConversationTitleText.Text);

            ExportSingleBtn.IsEnabled = true;
        }
        else
        {
            ConversationTitleText.Text = string.IsNullOrEmpty(_conversationId)
                ? LocalizationService.GetString("ConversationPreview_NoSelection", "未选择会话")
                : string.Format(
                    LocalizationService.GetString("ConversationPreview_SelectedFormat", "会话 ({0})"),
                    _conversationId[..Math.Min(8, _conversationId.Length)]);

            ConversationMetaText.Text = string.Format(
                LocalizationService.GetString("ConversationPreview_ProjectMetaFormat", "项目: {0} ({1})"),
                _project.Name,
                _project.Path);

            DialogTitleBar.Title = string.Format(
                LocalizationService.GetString("ConversationPreview_DialogTitleFormat", "会话思维流预览 - {0}"),
                _project.Name);
        }

        if (string.IsNullOrEmpty(_conversationId))
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyNoticeText.Text = LocalizationService.GetString("ConversationPreview_ProjectEmpty", "该项目暂无历史会话记录。");
            EmptyNoticeText.Visibility = Visibility.Visible;
            return;
        }

        var rawSteps = await Task.Run(() => _projectService.GetConversationTranscript(_project.Id, _conversationId));

        if (_isClosed) return;

        _allSteps = rawSteps.Select(s => new TranscriptDisplayItem(s)).ToList();

        LoadingPanel.Visibility = Visibility.Collapsed;

        if (_allSteps.Count == 0)
        {
            EmptyNoticeText.Text = LocalizationService.GetString("ConversationPreview_NoSteps", "该会话暂无记录的步骤内容。");
            EmptyNoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            TranscriptListBox.Visibility = Visibility.Visible;
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";

        // Build the filtered list first, then replace ItemsSource once. Adding items to a
        // bound ObservableCollection raises one change notification per item.
        List<TranscriptDisplayItem> filtered;
        if (string.IsNullOrEmpty(q))
        {
            filtered = _allSteps;
        }
        else
        {
            filtered = _allSteps.Where(s => s.Matches(q)).ToList();
        }

        TranscriptListBox.ItemsSource = filtered;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnForkFromStepClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int stepIndex })
        {
            var baseTitle = _currentSummary?.Title
                ?? LocalizationService.GetString("ConversationPreview_UntitledConversation", "会话");

            var defaultTitle = string.Format(
                LocalizationService.GetString("ConversationPreview_ForkDefaultTitleFormat", "{0} (Fork 步#{1})"),
                baseTitle,
                stepIndex);

            var newTitle = ModernDialog.Prompt(
                LocalizationService.GetString("ConversationPreview_ForkDialogTitle", "分叉新会话 (Fork)"), 
                string.Format(
                    LocalizationService.GetString(
                        "ConversationPreview_ForkPromptFormat",
                        "即将从步骤 #{0} 创建一个全新的分支会话。\n该分支将完整继承该步之前的所有上下文与脑库资产，原会话不受任何影响。\n\n请输入新会话标题："),
                    stepIndex), 
                defaultTitle, 
                owner: this);

            if (string.IsNullOrWhiteSpace(newTitle)) return;

            if (_projectService.ForkConversation(_project.Id, _conversationId, stepIndex, newTitle.Trim(), out var newConvoId, out var err))
            {
                ModernDialog.ShowSuccess(
                    LocalizationService.GetString("ConversationPreview_ForkSuccessTitle", "分叉成功"), 
                    string.Format(
                        LocalizationService.GetString(
                            "ConversationPreview_ForkSuccessFormat",
                            "新会话分支已成功创建并就绪！\n\n标题: {0}\n新会话 ID: {1}\n\n已可在历史会话列表中直接查看并继续调用。"),
                        newTitle.Trim(),
                        newConvoId), 
                    this);
            }
            else
            {
                ModernDialog.ShowError(
                    LocalizationService.GetString("ConversationPreview_ForkFailedTitle", "分叉失败"),
                    err ?? LocalizationService.GetString("ConversationPreview_UnknownError", "未知错误"),
                    this);
            }
        }
    }

    private void OnCopyStepContentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int stepIndex })
        {
            var step = _allSteps.FirstOrDefault(s => s.StepIndex == stepIndex);
            if (step != null && !string.IsNullOrEmpty(step.Content))
            {
                try
                {
                    Clipboard.SetText(step.Content);
                    ModernDialog.ShowSuccess(
                        LocalizationService.GetString("ConversationPreview_CopiedTitle", "复制成功"),
                        LocalizationService.GetString("ConversationPreview_CopiedMessage", "该步骤的文本内容已成功复制到剪贴板！"),
                        this);
                }
                catch (Exception ex)
                {
                    ModernDialog.ShowError(LocalizationService.GetString("ConversationPreview_CopyFailedTitle", "复制失败"), ex.Message, this);
                }
            }
        }
    }

    private void OnExportSingleClicked(object sender, RoutedEventArgs e)
    {
        if (_currentSummary == null && string.IsNullOrEmpty(_conversationId)) return;

        var title = _currentSummary?.Title ?? "Conversation";
        var safeTitle = string.Concat(title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "Conversation";
        var shortId = _conversationId.Length >= 8 ? _conversationId[..8] : _conversationId;

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.GetString("ConversationPreview_ExportDialogTitle", "导出当前会话为 Markdown"),
            Filter = LocalizationService.GetString(
                "ConversationPreview_ExportFilter",
                "Markdown 文档 (*.md)|*.md|所有文件 (*.*)|*.*"),
            FileName = $"{safeTitle}_{shortId}.md"
        };

        if (sfd.ShowDialog(this) == true)
        {
            if (_projectService.ExportConversationToMarkdown(_project.Id, _conversationId, sfd.FileName, out var err))
            {
                ModernDialog.ShowSuccess(
                    LocalizationService.GetString("ConversationPreview_ExportSuccessTitle", "导出成功"),
                    string.Format(LocalizationService.GetString("ConversationPreview_ExportSuccessFormat", "会话记录已成功保存至:\n{0}"), sfd.FileName),
                    this);
            }
            else
            {
                ModernDialog.ShowError(LocalizationService.GetString("ConversationPreview_ExportFailedTitle", "导出失败"), err, this);
            }
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class TranscriptDisplayItem
{
    private readonly TranscriptStepItem _item;

    public int StepIndex => _item.StepIndex;
    public string FormattedTime => _item.FormattedTime;
    public string Content => _item.Content;
    public string? Thinking => _item.Thinking;
    public string? ToolName => _item.ToolName;
    public string? ToolArgs => _item.ToolArgs;
    public string? ToolSummary => _item.ToolSummary;

    public string ForkMenuHeader => string.Format(
        LocalizationService.GetString("ConversationPreview_ForkMenuHeaderFormat", "🌿 从此步分叉新对话 (第 #{0} 步)"),
        StepIndex);

    public string StepBadgeText => string.Format(
        LocalizationService.GetString("ConversationPreview_StepFormat", "步骤 #{0}"),
        StepIndex);

    public string ToolOutputHeader => string.Format(
        LocalizationService.GetString("ConversationPreview_ToolOutputFormat", "💻 命令输出 (步骤 #{0})"),
        StepIndex);

    public Visibility UserBubbleVisibility => _item.IsUser ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModelBubbleVisibility => _item.IsModel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolOutputVisibility => _item.IsToolOutput ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ThinkingVisibility => _item.HasThinking ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolCallVisibility => _item.HasToolCall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ToolArgsVisibility => _item.HasToolArgs ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => _item.HasContent ? Visibility.Visible : Visibility.Collapsed;

    public TranscriptDisplayItem(TranscriptStepItem item)
    {
        _item = item;
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (!string.IsNullOrEmpty(Content) && Content.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(Thinking) && Thinking.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(ToolName) && ToolName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(ToolSummary) && ToolSummary.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(ToolArgs) && ToolArgs.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
