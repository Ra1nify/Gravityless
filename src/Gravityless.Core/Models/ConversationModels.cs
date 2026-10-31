namespace Gravityless.Core.Models;

public class ConversationSummaryItem
{
    public string ConversationId { get; set; } = string.Empty;
    public string ShortId => ConversationId.Length >= 8 ? ConversationId[..8] : ConversationId;
    public string Title { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public int StepCount { get; set; }
    public DateTime LastModifiedTime { get; set; }
    public string FormattedTime => LastModifiedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public class TranscriptStepItem
{
    public int StepIndex { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Thinking { get; set; }
    public string? ToolSummary { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArgs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    public string FormattedTime => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public bool IsUser => Type == "USER_INPUT" || Source == "USER_EXPLICIT";
    public bool IsModel => Type == "PLANNER_RESPONSE" || Source == "MODEL";
    public bool IsToolOutput => Type == "GENERIC";

    public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);
    public bool HasToolCall => !string.IsNullOrEmpty(ToolName);
    public bool HasToolArgs => !string.IsNullOrEmpty(ToolArgs);
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
}
