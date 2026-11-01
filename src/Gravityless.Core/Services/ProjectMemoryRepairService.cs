using System.Text;
using System.Text.Json;
using Gravityless.Core.Models;
using Microsoft.Data.Sqlite;

namespace Gravityless.Core.Services;

/// <summary>
/// Result of inspecting one project's memory directory.
/// </summary>
public class ProjectMemoryIssue
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Conversation listed in the summary index whose checkpoint database is gone.</summary>
    public string ConversationId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>True when brain/ still holds this conversation, so its transcript can be read.</summary>
    public bool HasBrainData { get; set; }

    public int StepCount { get; set; }

    /// <summary>True when the conversation is only in brain/ and has no summary row at all.</summary>
    public bool MissingFromIndex { get; set; }
}

/// <summary>
/// Repairs project memory that Antigravity can no longer load.
///
/// A conversation needs three things: a summary row, a checkpoint database under
/// conversations/, and its brain directory. When the checkpoint database is lost the
/// conversation cannot be opened again — LangGraph state cannot be rebuilt from the
/// transcript — so the repair keeps what survives: it re-registers orphaned brain
/// directories in the index and exports every transcript to Markdown.
/// </summary>
public class ProjectMemoryRepairService
{
    private readonly ProjectService _projectService;
    private readonly string _baseDir;

    public ProjectMemoryRepairService(ProjectService projectService, string? baseDir = null)
    {
        _projectService = projectService;
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>Reports every conversation whose checkpoint database is missing.</summary>
    public List<ProjectMemoryIssue> Scan()
    {
        var issues = new List<ProjectMemoryIssue>();

        foreach (var project in _projectService.Projects)
        {
            if (project.Id == ProjectService.LooseChatsProjectId) continue;

            var memoryDir = _projectService.GetProjectMemoryDir(project.Id);
            if (!Directory.Exists(memoryDir)) continue;

            var indexed = ReadIndex(memoryDir);
            var brainDir = Path.Combine(memoryDir, "brain");
            var convoDir = Path.Combine(memoryDir, "conversations");

            foreach (var row in indexed)
            {
                var checkpoint = Path.Combine(convoDir, $"{row.Id}.db");
                if (File.Exists(checkpoint) && new FileInfo(checkpoint).Length > 512) continue;

                var brainPath = Path.Combine(brainDir, row.Id);
                issues.Add(new ProjectMemoryIssue
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    ConversationId = row.Id,
                    Title = row.Title,
                    HasBrainData = Directory.Exists(brainPath),
                    StepCount = CountTranscriptSteps(brainPath)
                });
            }

            // brain/ directories with no summary row: the conversation vanished from the list
            // but its transcript is intact.
            if (Directory.Exists(brainDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(brainDir))
                {
                    var cid = Path.GetFileName(dir);
                    if (indexed.Any(r => r.Id.Equals(cid, StringComparison.OrdinalIgnoreCase))) continue;

                    issues.Add(new ProjectMemoryIssue
                    {
                        ProjectId = project.Id,
                        ProjectName = project.Name,
                        ConversationId = cid,
                        Title = string.Empty,
                        HasBrainData = true,
                        StepCount = CountTranscriptSteps(dir),
                        MissingFromIndex = true
                    });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Repairs the given issues: re-registers conversations missing from the index and
    /// exports every recoverable transcript to Markdown beside its brain directory.
    /// Returns a human-readable summary.
    /// </summary>
    public string Repair(IEnumerable<ProjectMemoryIssue> issues, Action<int, int, string>? onProgress = null)
    {
        var list = issues.ToList();
        int exported = 0, registered = 0, failed = 0;

        for (int i = 0; i < list.Count; i++)
        {
            var issue = list[i];
            onProgress?.Invoke(i + 1, list.Count, issue.Title.Length > 0 ? issue.Title : issue.ConversationId);

            try
            {
                var memoryDir = _projectService.GetProjectMemoryDir(issue.ProjectId);
                var brainPath = Path.Combine(memoryDir, "brain", issue.ConversationId);

                if (issue.MissingFromIndex && issue.HasBrainData)
                {
                    if (ReindexConversation(memoryDir, issue.ProjectId, issue.ConversationId, brainPath))
                        registered++;
                }

                if (issue.HasBrainData)
                {
                    if (ExportTranscript(brainPath)) exported++;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("memory-repair", ex);
                failed++;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"恢复报告：导出 {exported} 条，补登记 {registered} 条，失败 {failed} 条。");
        sb.AppendLine();
        sb.AppendLine("说明：对话本体（checkpoint 数据库）已丢失，无法重新打开。");
        sb.AppendLine("导出内容保存在各对话 brain 目录下的 conversation-export.md。");
        return sb.ToString().TrimEnd();
    }

    private sealed record IndexRow(string Id, string Title);

    private static List<IndexRow> ReadIndex(string memoryDir)
    {
        var rows = new List<IndexRow>();
        var db = Path.Combine(memoryDir, "conversation_summaries.db");
        if (!File.Exists(db)) return rows;

        try
        {
            // Not Mode=ReadOnly: a database whose pages still sit in -wal refuses read-only
            // opens, which would report an intact project as having no index at all.
            using var conn = new SqliteConnection($"Data Source={db}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, title FROM conversation_summaries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrEmpty(id)) continue;
                rows.Add(new IndexRow(id, reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("memory-repair.read-index", ex);
        }

        return rows;
    }

    private static string TranscriptPath(string brainPath) =>
        Path.Combine(brainPath, ".system_generated", "logs", "transcript.jsonl");

    private static int CountTranscriptSteps(string brainPath)
    {
        try
        {
            var path = TranscriptPath(brainPath);
            if (!File.Exists(path)) return 0;

            var count = 0;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() != null) count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Adds a summary row for a brain directory the index does not know about, so the
    /// conversation reappears in Antigravity's list.
    /// </summary>
    private bool ReindexConversation(string memoryDir, string projectId, string conversationId, string brainPath)
    {
        var db = Path.Combine(memoryDir, "conversation_summaries.db");
        if (!File.Exists(db)) return false;

        var transcript = ReadTranscript(brainPath);
        if (transcript.Count == 0) return false;

        var firstUser = transcript.FirstOrDefault(s => s.Type == "USER_INPUT");
        var title = ExtractTitle(firstUser?.Content) ?? "Recovered conversation";
        var lastTime = transcript[^1].CreatedAt;

        try
        {
            using var conn = new SqliteConnection($"Data Source={db}");
            conn.Open();
            using var cmd = conn.CreateCommand();

            // project_id and workspace_uris keep the row inside this project, which is where
            // the brain directory already lives.
            cmd.CommandText = @"
                INSERT OR IGNORE INTO conversation_summaries
                    (conversation_id, title, preview, step_count, last_modified_time,
                     workspace_uris, status, source, project_id, agent_name,
                     last_user_input_time)
                VALUES (@cid, @title, @title, @steps, @time,
                        @uris, 'RECOVERED', '', @proj, '',
                        @time)";
            cmd.Parameters.AddWithValue("@cid", conversationId);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@steps", transcript.Count);
            cmd.Parameters.AddWithValue("@time", lastTime);
            cmd.Parameters.AddWithValue("@uris", WorkspaceUrisFor(projectId));
            cmd.Parameters.AddWithValue("@proj", projectId);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("memory-repair.reindex", ex);
            return false;
        }
    }

    private string WorkspaceUrisFor(string projectId)
    {
        var project = _projectService.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null || string.IsNullOrEmpty(project.Path)) return "[]";

        var uri = "file:///" + project.Path.Replace("\\", "/").Replace(" ", "%20");
        return JsonSerializer.Serialize(new[] { uri });
    }

    /// <summary>Writes the transcript as readable Markdown next to the brain directory.</summary>
    private bool ExportTranscript(string brainPath)
    {
        var transcript = ReadTranscript(brainPath);
        if (transcript.Count == 0) return false;

        try
        {
            var sb = new StringBuilder();
            var title = ExtractTitle(transcript.FirstOrDefault(s => s.Type == "USER_INPUT")?.Content);

            sb.AppendLine($"# {title ?? "Recovered conversation"}");
            sb.AppendLine();
            sb.AppendLine($"> 从 local transcript 导出 · {transcript.Count} 步 · {transcript[0].CreatedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine();

            foreach (var step in transcript)
            {
                if (string.IsNullOrWhiteSpace(step.Content)) continue;

                sb.AppendLine($"## {step.Index} · {DescribeSource(step)}");
                sb.AppendLine();
                sb.AppendLine(step.Content.Trim());
                sb.AppendLine();
            }

            File.WriteAllText(
                Path.Combine(brainPath, "conversation-export.md"),
                sb.ToString(),
                new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("memory-repair.export", ex);
            return false;
        }
    }

    private static string DescribeSource(TranscriptStep step)
    {
        var label = step.Type switch
        {
            "USER_INPUT" => "用户",
            "PLANNER_RESPONSE" => "助手",
            _ => step.Source
        };

        return step.Source == "MODEL" ? $"{label} ({step.Type})" : label;
    }

    private sealed record TranscriptStep(int Index, string Source, string Type, string Content, DateTime CreatedAt);

    private static List<TranscriptStep> ReadTranscript(string brainPath)
    {
        var steps = new List<TranscriptStep>();
        var path = TranscriptPath(brainPath);
        if (!File.Exists(path)) return steps;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var index = root.TryGetProperty("step_index", out var i) && i.TryGetInt32(out var n) ? n : steps.Count;
                    var source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    var created = root.TryGetProperty("created_at", out var d) && DateTime.TryParse(d.GetString(), out var dt)
                        ? dt
                        : DateTime.MinValue;

                    steps.Add(new TranscriptStep(index, source, type, content, created));
                }
                catch
                {
                    // A truncated final line is expected when the process was killed mid-write.
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("memory-repair.read-transcript", ex);
        }

        return steps;
    }

    /// <summary>Pulls the first non-empty line out of the user request envelope.</summary>
    private static string? ExtractTitle(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = content;
        var start = text.IndexOf("<USER_REQUEST>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += "<USER_REQUEST>".Length;
            var end = text.IndexOf("</USER_REQUEST>", start, StringComparison.OrdinalIgnoreCase);
            if (end > start) text = text[start..end];
        }

        var line = text
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        if (string.IsNullOrEmpty(line)) return null;
        return line.Length > 60 ? line[..60] + "…" : line;
    }
}
