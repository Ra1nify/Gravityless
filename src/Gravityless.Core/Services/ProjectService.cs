using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Gravityless.Core.Models;

namespace Gravityless.Core.Services;

public class ProjectService
{
    private readonly string _baseDir;
    private readonly string _projectsJsonPath;
    private readonly string _projectsStorageDir;
    private readonly List<ProjectProfile> _projects = [];

    public IReadOnlyList<ProjectProfile> Projects => _projects.AsReadOnly();
    public event Action<ProjectProfile>? ProjectStateChanged;

    public ProjectService(string? baseDir = null)
    {
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
        var dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(dataDir);

        _projectsJsonPath = Path.Combine(dataDir, "projects.json");
        _projectsStorageDir = Path.Combine(dataDir, "projects");
        Directory.CreateDirectory(_projectsStorageDir);

        LoadProjects();
    }

    private void LoadProjects()
    {
        _projects.Clear();
        if (File.Exists(_projectsJsonPath))
        {
            try
            {
                var json = File.ReadAllText(_projectsJsonPath);
                var list = JsonSerializer.Deserialize<List<ProjectProfile>>(json);
                if (list != null)
                {
                    foreach (var proj in list)
                    {
                        // Id doubles as a directory name, so sanitize ids loaded from disk.
                        proj.Id = EnsureSafeId(proj.Id);

                        // Reset state of processes that are no longer alive.
                        bool isProcessAlive = false;
                        if (proj.ProcessId.HasValue)
                        {
                            try
                            {
                                var proc = Process.GetProcessById(proj.ProcessId.Value);
                                if (!proc.HasExited) isProcessAlive = true;
                            }
                            catch { }
                        }

                        if (!isProcessAlive)
                        {
                            proj.IsRunning = false;
                            proj.RunningInstanceId = null;
                            proj.ProcessId = null;
                        }

                        RefreshProjectMemoryStats(proj);
                    }
                    _projects.AddRange(list);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Persists the project list. Writes to a sibling file first and then swaps, so a failed
    /// or interrupted write cannot leave a truncated projects.json behind. Throws on failure
    /// so callers can roll back their in-memory change.
    /// </summary>
    /// <summary>Persists project state atomically. Throws on failure so callers can roll back.</summary>
    public void SaveProjects() => JsonStateStore.Write(_projectsJsonPath, _projects);

    /// <summary>Save that reports failure instead of throwing.</summary>
    public bool TrySaveProjects() => JsonStateStore.TryWrite(_projectsJsonPath, _projects);

    /// <summary>Validates a project id as a single path segment: [A-Za-z0-9_-], 1..64 chars, no separators.</summary>
    public static bool IsSafeIdSegment(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        if (id is "." or "..")
        {
            return false;
        }

        foreach (var c in id)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the id when it is a safe segment, otherwise a new GUID.</summary>
    public static string EnsureSafeId(string? id)
    {
        return IsSafeIdSegment(id) ? id! : Guid.NewGuid().ToString("D");
    }

    /// <summary>Deterministic mapping to a single safe directory name; invalid characters become '_'.</summary>
    private static string ToSafePathSegment(string? id)
    {
        if (IsSafeIdSegment(id))
        {
            return id!;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var c in id ?? string.Empty)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            sb.Append(ok ? c : '_');
            if (sb.Length >= 64)
            {
                break;
            }
        }

        var segment = sb.ToString();
        if (string.IsNullOrEmpty(segment) || segment is "." or "..")
        {
            segment = "_invalid_";
        }

        return segment;
    }

    public string GetProjectDir(string projectId)
    {
        return Path.Combine(_projectsStorageDir, ToSafePathSegment(projectId));
    }

    /// <summary>
    /// Canonical copy of a project's Antigravity metadata file. Instances hard-link their
    /// config/projects/&lt;id&gt;.json to this path, so one project has exactly one definition.
    /// </summary>
    public string GetProjectConfigJsonPath(string projectId)
    {
        var dir = Path.Combine(_projectsStorageDir, ToSafePathSegment(projectId));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "project.json");
    }

    /// <summary>
    /// Synthetic project id for Antigravity's project-less chats. They live outside the
    /// Gravityless data root, so the path resolvers special-case this id.
    /// </summary>
    public const string LooseChatsProjectId = "__loose__";

    /// <summary>
    /// Value Antigravity itself writes into <c>conversation_summaries.project_id</c> for a
    /// chat started outside any workspace. Treated as equivalent to <see cref="LooseChatsProjectId"/>.
    /// </summary>
    public const string AntigravityLooseProjectId = "outside-of-project";

    /// <summary>Directory holding project-less conversations on the host.</summary>
    public static string GetLooseChatsDir() => Path.Combine(GetRealHostGeminiDir(), "antigravity");

    /// <summary>True when <paramref name="projectId"/> refers to the project-less chat store.</summary>
    public static bool IsLooseChats(string projectId) =>
        string.Equals(projectId, LooseChatsProjectId, StringComparison.Ordinal)
        || string.Equals(projectId, AntigravityLooseProjectId, StringComparison.Ordinal);

    /// <summary>
    /// True when a summary row describes a chat that belongs to no workspace. Antigravity leaves
    /// both the id and the workspace list empty in that case, so either signal is enough.
    /// </summary>
    public static bool IsProjectlessConversation(string? projectId, string? workspaceUris)
    {
        if (IsLooseChats(projectId ?? string.Empty)) return true;

        var uris = workspaceUris?.Trim();
        var hasNoUris = string.IsNullOrEmpty(uris) || uris == "[]";
        return string.IsNullOrWhiteSpace(projectId) && hasNoUris;
    }

    public string GetProjectMemoryDir(string projectId) =>
        IsLooseChats(projectId) ? GetLooseChatsDir() : Path.Combine(GetProjectDir(projectId), "memory");
    public string GetProjectBrainDir(string projectId) => Path.Combine(GetProjectMemoryDir(projectId), "brain");

    /// <summary>True when the project-less chat store exists and holds a summary database.</summary>
    public bool HasLooseChats() => File.Exists(Path.Combine(GetLooseChatsDir(), "conversation_summaries.db"));

    /// <summary>
    /// Converts a user-supplied workspace path into a plain filesystem path.
    /// Shell namespace forms (`shell:::{GUID}`, `::{GUID}`), environment variables and `~`
    /// are expanded, because the bundled language server only understands real paths.
    /// Returns an empty string when the result does not exist.
    /// </summary>
    public static string NormalizeWorkspacePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var raw = input.Trim().Trim('"');

        // Expand %VAR% and ~ first; shell forms can contain either.
        try { raw = Environment.ExpandEnvironmentVariables(raw); } catch { }

        if (raw == "~" || raw.StartsWith("~\\") || raw.StartsWith("~/"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            raw = Path.Combine(home, raw.Substring(1).TrimStart('\\', '/'));
        }

        // Map the well-known shell folders this app is likely to receive.
        var shellMap = new (string Token, Environment.SpecialFolder Folder)[]
        {
            ("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", Environment.SpecialFolder.DesktopDirectory),
            ("{374DE290-123F-4565-9164-39C4925E467B}", Environment.SpecialFolder.MyDocuments),
            ("{FDD39AD0-238F-46AF-ADB4-6C85480369C7}", Environment.SpecialFolder.MyDocuments),
            ("{5E6C858F-0E22-4760-9AFE-EA3317B67173}", Environment.SpecialFolder.UserProfile),
        };

        if (raw.Contains("::"))
        {
            foreach (var (token, folder) in shellMap)
            {
                if (raw.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    var mapped = Environment.GetFolderPath(folder);
                    // Keep any tail that follows the CLSID.
                    var idx = raw.IndexOf(token, StringComparison.OrdinalIgnoreCase) + token.Length;
                    var tail = raw[idx..].TrimStart('\\', '/');
                    raw = string.IsNullOrEmpty(tail) ? mapped : Path.Combine(mapped, tail);
                    break;
                }
            }
        }

        // A bare drive or an existing directory is already usable.
        try { raw = Path.GetFullPath(raw); } catch { return string.Empty; }

        return Directory.Exists(raw) ? raw : string.Empty;
    }

    /// <summary>
    /// Resolves a project id to its profile, including the synthetic project-less chat row.
    /// Every caller that acts on a row must use this instead of searching <see cref="Projects"/>.
    /// </summary>
    public ProjectProfile? FindProject(string projectId)
    {
        if (IsLooseChats(projectId))
        {
            return CreateLooseChatsProfile();
        }
        return _projects.FirstOrDefault(p => p.Id == projectId);
    }

    /// <summary>
    /// Builds the profile describing the project-less chat store. The path points at the
    /// store so path checks succeed and the row reads as a real location.
    /// </summary>
    public ProjectProfile CreateLooseChatsProfile()
    {
        var dir = GetLooseChatsDir();
        var profile = new ProjectProfile
        {
            Id = LooseChatsProjectId,
            Name = "无项目聊天",
            Path = dir
        };

        try
        {
            var looseConvos = GetConversations(LooseChatsProjectId);
            profile.ConversationCount = looseConvos.Count;
            if (looseConvos.Count > 0)
            {
                var maxMod = looseConvos.Max(c => c.LastModifiedTime);
                if (maxMod > DateTime.MinValue) profile.LastActiveAt = maxMod;
            }
        }
        catch { }

        return profile;
    }
    public string GetProjectConversationsDir(string projectId) => Path.Combine(GetProjectMemoryDir(projectId), "conversations");
    public string GetProjectSummaryDbPath(string projectId) => Path.Combine(GetProjectMemoryDir(projectId), "conversation_summaries.db");
    public string GetProjectSummariesPbPath(string projectId) => Path.Combine(GetProjectMemoryDir(projectId), "agyhub_summaries_proto.pb");

    public static readonly string[] SqliteSiblingSuffixes = [ "-wal", "-shm", "-journal" ];
    public static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public static void DeleteSqliteSiblings(string dbPath)
    {
        foreach (var suffix in SqliteSiblingSuffixes)
        {
            try
            {
                var path = dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("sqlite-sibling-delete", ex);
            }
        }
    }

    /// <summary>Checks whether a file exists and is a valid, readable SQLite database.</summary>
    public static bool IsUsableSqlite(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 512) return false;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Span<byte> header = stackalloc byte[16];
                int read = fs.Read(header);
                if (read < 16) return false;
                ReadOnlySpan<byte> expected = "SQLite format 3\0"u8;
                if (!header.SequenceEqual(expected)) return false;
            }

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master LIMIT 1;";
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates a clean Antigravity 2.35+ conversation_summaries.db schema with WAL, parent links, and FTS5 search index.</summary>
    public static void CreateBlankSummaryDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        SqliteConnection.ClearAllPools();
        DeleteSqliteSiblings(dbPath);
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { }
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS search_schema_version (
                version INTEGER PRIMARY KEY
            );
            INSERT OR REPLACE INTO search_schema_version (version) VALUES (1);

            CREATE TABLE IF NOT EXISTS `conversation_summaries` (
                `conversation_id` text,
                `title` text NOT NULL DEFAULT '',
                `preview` text NOT NULL DEFAULT '',
                `step_count` integer NOT NULL DEFAULT 0,
                `last_modified_time` datetime NOT NULL,
                `workspace_uris` text NOT NULL,
                `status` text NOT NULL DEFAULT '',
                `source` text NOT NULL DEFAULT '',
                `project_id` text NOT NULL DEFAULT '',
                `agent_name` text NOT NULL DEFAULT '',
                `parent_conversation_id` text NOT NULL DEFAULT '',
                `nesting_depth` integer NOT NULL DEFAULT 0,
                `battle_id` text NOT NULL DEFAULT '',
                `winning_conversation_id` text NOT NULL DEFAULT '',
                `not_fully_idle` numeric NOT NULL DEFAULT false,
                `killed` numeric NOT NULL DEFAULT false,
                `last_user_input_time` datetime NOT NULL,
                `last_user_input_step_index` integer NOT NULL DEFAULT -1,
                `app_data_dir` text NOT NULL DEFAULT '',
                `raw_summary` blob,
                `group_id` text NOT NULL DEFAULT '',
                PRIMARY KEY (`conversation_id`)
            );

            CREATE INDEX IF NOT EXISTS `idx_conversation_summaries_last_modified_time` ON `conversation_summaries`(`last_modified_time`);
            CREATE INDEX IF NOT EXISTS `idx_conversation_summaries_last_user_input_time` ON `conversation_summaries`(`last_user_input_time`);

            CREATE TABLE IF NOT EXISTS `conversation_parents` (
                `conversation_id` TEXT PRIMARY KEY,
                `parent_id` TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE VIRTUAL TABLE IF NOT EXISTS conversations_fts USING fts5(
                conversation_id UNINDEXED,
                chunk_index UNINDEXED,
                chunk_first_step UNINDEXED,
                chunk_last_step UNINDEXED,
                last_modified_time UNINDEXED,
                step_count UNINDEXED,
                last_step_created_at UNINDEXED,
                archived UNINDEXED,
                user_step_byte_offsets UNINDEXED,
                agent_step_byte_offsets UNINDEXED,
                title,
                workspace,
                project,
                user_body,
                agent_body,
                tokenize = 'unicode61 remove_diacritics 2',
                prefix = '1 2 3'
            );
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clears seeded conversation rows when creating an empty instance/project DB from a host stock snapshot.</summary>
    public static void ClearSummaryTables(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        foreach (var table in new[] { "conversation_summaries", "conversations_fts", "conversation_parents" })
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM `{table}`;";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Older stock databases may omit optional FTS or conversation_parents tables.
            }
        }
    }

    /// <summary>Ensures an existing summary DB has all 21 summary columns and the conversation_parents table.</summary>
    public static void EnsureSummarySchema(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = conn.CreateCommand())
        {
            pragma.Transaction = tx;
            pragma.CommandText = "PRAGMA table_info(`conversation_summaries`);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    existingCols.Add(reader.GetString(1));
                }
            }
        }

        if (existingCols.Count > 0)
        {
            (string Name, string Definition)[] optionalCols =
            [
                ("parent_conversation_id", "text NOT NULL DEFAULT ''"),
                ("nesting_depth", "integer NOT NULL DEFAULT 0"),
                ("battle_id", "text NOT NULL DEFAULT ''"),
                ("winning_conversation_id", "text NOT NULL DEFAULT ''"),
                ("not_fully_idle", "numeric NOT NULL DEFAULT false"),
                ("killed", "numeric NOT NULL DEFAULT false"),
                ("last_user_input_step_index", "integer NOT NULL DEFAULT -1"),
                ("app_data_dir", "text NOT NULL DEFAULT ''"),
                ("raw_summary", "blob"),
                ("group_id", "text NOT NULL DEFAULT ''")
            ];

            foreach (var (name, definition) in optionalCols)
            {
                if (existingCols.Contains(name)) continue;
                using var alter = conn.CreateCommand();
                alter.Transaction = tx;
                alter.CommandText = $"ALTER TABLE `conversation_summaries` ADD COLUMN `{name}` {definition};";
                alter.ExecuteNonQuery();
            }
        }

        using var parentsCmd = conn.CreateCommand();
        parentsCmd.Transaction = tx;
        parentsCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `conversation_parents` (
                `conversation_id` TEXT PRIMARY KEY,
                `parent_id` TEXT NOT NULL
            ) WITHOUT ROWID;";
        parentsCmd.ExecuteNonQuery();
    }

    /// <summary>Consistent snapshot copy through the SQLite online backup API (never a raw File.Copy of a live DB).</summary>
    /// <summary>
    /// Reads a SQLite database safely even when another running process holds WAL locks on it.
    /// When no <c>-wal</c> companion exists, queries in-place read-only; otherwise copies
    /// <c>.db</c> + <c>-wal</c> + <c>-shm</c> using <see cref="FileShare.ReadWrite"/> to an isolated
    /// temp directory so SQLite replays all committed WAL frames without disturbing the live DB.
    /// </summary>
    internal static bool TryQuerySqliteSnapshot<T>(string dbPath, Func<SqliteConnection, T> query, out T? result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return false;

        var hasWal = File.Exists(dbPath + "-wal") || File.Exists(dbPath + "-shm");
        if (!hasWal)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                result = query(conn);
                return true;
            }
            catch
            {
                // Fall through to temp snapshot recovery attempt
            }
        }

        string? tempDir = null;
        try
        {
            if (!CopySqliteFamilyToTemp(dbPath, out tempDir, out var tempDbPath))
            {
                return false;
            }

            using var conn = new SqliteConnection($"Data Source={tempDbPath};Pooling=False");
            conn.Open();
            result = query(conn);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static bool CopySqliteFamilyToTemp(string sourceDb, out string tempDir, out string tempDbPath)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"gl_snap_{Guid.NewGuid():N}");
        tempDbPath = Path.Combine(tempDir, "snapshot.db");
        try
        {
            Directory.CreateDirectory(tempDir);
            CopyFileShared(sourceDb, tempDbPath);
            foreach (var suffix in SqliteSiblingSuffixes)
            {
                var sidecar = sourceDb + suffix;
                if (File.Exists(sidecar))
                {
                    CopyFileShared(sidecar, tempDbPath + suffix);
                }
            }
            return true;
        }
        catch
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            return false;
        }
    }

    private static void CopyFileShared(string sourcePath, string destPath)
    {
        using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    public static bool CopySqliteSnapshot(string sourceDb, string destDb)
    {
        try
        {
            if (!File.Exists(sourceDb))
            {
                return false;
            }

            var dir = Path.GetDirectoryName(destDb);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Passive WAL checkpoint on the source to bring committed pages into main DB
            try
            {
                using var chkConn = new SqliteConnection($"Data Source={sourceDb}");
                chkConn.Open();
                using var chkCmd = chkConn.CreateCommand();
                chkCmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                chkCmd.ExecuteNonQuery();
            }
            catch { }

            SqliteConnection.ClearAllPools();
            DeleteSqliteSiblings(destDb);
            if (File.Exists(destDb))
            {
                try { File.Delete(destDb); } catch { }
            }

            try
            {
                using var src = new SqliteConnection($"Data Source={sourceDb};Mode=ReadOnly");
                using var dst = new SqliteConnection($"Data Source={destDb}");
                src.Open();
                dst.Open();
                src.BackupDatabase(dst);
                return true;
            }
            catch
            {
                // Live WAL or file lock fallback: copy db+wal+shm via FileShare.ReadWrite and back up from temp.
                if (!CopySqliteFamilyToTemp(sourceDb, out var tempDir, out var tempDb))
                {
                    return false;
                }
                try
                {
                    using var tempSrc = new SqliteConnection($"Data Source={tempDb};Pooling=False");
                    using var dst = new SqliteConnection($"Data Source={destDb};Pooling=False");
                    tempSrc.Open();
                    dst.Open();
                    tempSrc.BackupDatabase(dst);
                    return true;
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes rows whose id is not in keepIds, staging the ids in a temp table (avoids SQLite's parameter cap).</summary>
    private static void DeleteRowsNotIn(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string table,
        IReadOnlyList<string> keepIds)
    {
        using (var setup = conn.CreateCommand())
        {
            setup.Transaction = tx;
            setup.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _gl_keep (id TEXT PRIMARY KEY); DELETE FROM _gl_keep;";
            setup.ExecuteNonQuery();
        }

        const int chunkSize = 400;
        for (var offset = 0; offset < keepIds.Count; offset += chunkSize)
        {
            var chunk = keepIds.Skip(offset).Take(chunkSize).ToList();
            var names = chunk.Select((_, i) => "@p" + i).ToList();

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = $"INSERT OR IGNORE INTO _gl_keep (id) VALUES ({string.Join("),(", names)});";
            for (var i = 0; i < chunk.Count; i++)
            {
                insert.Parameters.AddWithValue("@p" + i, chunk[i]);
            }

            insert.ExecuteNonQuery();
        }

        using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = $"DELETE FROM {table} WHERE conversation_id NOT IN (SELECT id FROM _gl_keep);";
        delete.ExecuteNonQuery();
    }

    /// <summary>Resolves the real host .gemini root (%USERPROFILE%, via the Volatile Environment key).</summary>
    public static string GetRealHostGeminiDir()
    {
        var userName = Environment.UserName;
        var directPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users", userName, ".gemini");
        if (Directory.Exists(directPath)) return directPath;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Volatile Environment");
            var profile = key?.GetValue("USERPROFILE")?.ToString();
            if (!string.IsNullOrEmpty(profile) && Directory.Exists(Path.Combine(profile, ".gemini")))
                return Path.Combine(profile, ".gemini");
        }
        catch { }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
    }

    public ProjectProfile CreateProject(string name, string folderPath)
    {
        var proj = new ProjectProfile
        {
            Name = name,
            Path = folderPath
        };

        var memDir = GetProjectMemoryDir(proj.Id);
        Directory.CreateDirectory(GetProjectBrainDir(proj.Id));
        Directory.CreateDirectory(GetProjectConversationsDir(proj.Id));

        // Seed an empty-schema conversation_summaries.db from the host stock database.
        InitSummaryDb(proj.Id);

        _projects.Add(proj);
        SaveProjects();
        return proj;
    }

    public void RegisterProject(ProjectProfile profile)
    {
        if (profile == null) return;

        // Validate externally supplied ids before they are used as directory names.
        if (!IsSafeIdSegment(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("D");
        }

        if (_projects.Any(p => p.Id == profile.Id)) return;
        Directory.CreateDirectory(GetProjectBrainDir(profile.Id));
        Directory.CreateDirectory(GetProjectConversationsDir(profile.Id));
        InitSummaryDb(profile.Id);
        _projects.Add(profile);
        SaveProjects();
    }

    public void InitSummaryDb(string projectId)
    {
        var destDb = GetProjectSummaryDbPath(projectId);
        if (File.Exists(destDb) && IsUsableSqlite(destDb)) return;

        var hostGemini = GetRealHostGeminiDir();
        var stockDb = Path.Combine(hostGemini, "antigravity", "conversation_summaries.db");
        if (File.Exists(stockDb) && IsUsableSqlite(stockDb))
        {
            try
            {
                if (CopySqliteSnapshot(stockDb, destDb))
                {
                    using var conn = new SqliteConnection($"Data Source={destDb}");
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        ClearSummaryTables(conn, tx);
                        tx.Commit();
                    }

                    using var vacuum = conn.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                    return;
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("project.vacuum", ex); }
        }

        try
        {
            CreateBlankSummaryDb(destDb);
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("project.create-blank-summary", ex);
        }
    }

    public bool RenameProject(string projectId, string newName, out string errorMessage)
    {
        errorMessage = string.Empty;
        var proj = _projects.FirstOrDefault(p => p.Id == projectId);
        if (proj == null)
        {
            errorMessage = "未找到指定的项目！";
            return false;
        }

        proj.Name = newName.Trim();
        SaveProjects();
        ProjectStateChanged?.Invoke(proj);
        return true;
    }

    public ProjectProfile? DuplicateProject(string sourceProjectId, string newName, out string errorMessage)
    {
        errorMessage = string.Empty;
        var srcProj = _projects.FirstOrDefault(p => p.Id == sourceProjectId);
        if (srcProj == null)
        {
            errorMessage = "未找到原项目！";
            return null;
        }

        var newProj = new ProjectProfile
        {
            Name = string.IsNullOrWhiteSpace(newName) ? $"{srcProj.Name} (Copy)" : newName.Trim(),
            Path = srcProj.Path,
            ConversationCount = srcProj.ConversationCount,
            LastActiveAt = srcProj.LastActiveAt
        };

        var srcMem = GetProjectMemoryDir(srcProj.Id);
        var destMem = GetProjectMemoryDir(newProj.Id);

        try
        {
            if (Directory.Exists(srcMem))
            {
                CopyDirectory(srcMem, destMem);
            }
            else
            {
                Directory.CreateDirectory(GetProjectBrainDir(newProj.Id));
                Directory.CreateDirectory(GetProjectConversationsDir(newProj.Id));
                InitSummaryDb(newProj.Id);
            }

            _projects.Add(newProj);
            SaveProjects();
            ProjectStateChanged?.Invoke(newProj);
            return newProj;
        }
        catch (Exception ex)
        {
            errorMessage = $"复制项目失败: {ex.Message}";
            return null;
        }
    }

    public bool DeleteProject(string projectId, out string errorMessage, bool force = false)
    {
        errorMessage = string.Empty;
        var proj = _projects.FirstOrDefault(p => p.Id == projectId);
        if (proj == null) return true;

        if (proj.IsRunning && !force)
        {
            errorMessage = "该项目当前处于运行状态，请先停止挂载该项目的实例后再删除！";
            return false;
        }

        _projects.Remove(proj);

        // Roll back the in-memory change when the write fails, otherwise the UI would show the
        // project as gone while projects.json still lists it.
        try
        {
            SaveProjects();
        }
        catch (Exception ex)
        {
            _projects.Add(proj);
            errorMessage = $"保存项目列表失败，已撤销删除：{ex.Message}";
            return false;
        }

        // Instance sandboxes hold their own copy of the workspace record; leaving it behind
        // makes ReconcileAndHarvestProjects recreate the project on the next startup.
        RemoveInstanceWorkspaceRecords(projectId);

        // The record is gone; a leftover directory is reported but no longer resurrects the project.
        var dir = GetProjectDir(projectId);
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("project.orphan-delete", ex);
                errorMessage = $"项目已移除，但记忆库目录未能完全删除：{ex.Message}";
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes `<instance>/.gemini/config/projects/&lt;id&gt;.json` across every instance sandbox.
    /// These records make the reconciler re-register a deleted project on the next start, so
    /// they must go together with the project record. Returns how many files were removed.
    /// </summary>
    public int RemoveInstanceWorkspaceRecords(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return 0;

        var removed = 0;
        try
        {
            var instancesDir = Path.Combine(_baseDir, "data", "instances");
            if (!Directory.Exists(instancesDir)) return 0;

            foreach (var instDir in Directory.GetDirectories(instancesDir))
            {
                var configProjects = Path.Combine(instDir, ".gemini", "config", "projects");
                if (!Directory.Exists(configProjects)) continue;

                foreach (var file in Directory.EnumerateFiles(configProjects, "*.json"))
                {
                    try
                    {
                        // Match on the file name or the id field; the two normally agree but
                        // the official app can rename the file independently.
                        var nameMatches = string.Equals(
                            Path.GetFileNameWithoutExtension(file), projectId,
                            StringComparison.OrdinalIgnoreCase);

                        var idMatches = false;
                        if (!nameMatches)
                        {
                            var node = JsonNode.Parse(File.ReadAllText(file));
                            var id = node?["id"]?.ToString();
                            idMatches = !string.IsNullOrEmpty(id)
                                && id.Equals(projectId, StringComparison.OrdinalIgnoreCase);
                        }

                        if (nameMatches || idMatches)
                        {
                            File.Delete(file);
                            removed++;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return removed;
    }

    /// <summary>
    /// Lists memory-store directories that no project record refers to. These accumulate when
    /// a delete removed the record but failed to remove the files.
    /// </summary>
    public List<string> GetOrphanProjectDirs()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(_projectsStorageDir)) return result;

            var live = _projects.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(_projectsStorageDir))
            {
                var name = Path.GetFileName(dir);
                if (!live.Contains(name))
                {
                    result.Add(dir);
                }
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Removes orphan directories. Returns how many were removed and how many refused.
    /// A directory that is still mounted by a running instance fails and is reported.
    /// </summary>
    public (int Removed, int Failed) CleanOrphanProjectDirs()
    {
        int removed = 0, failed = 0;
        foreach (var dir in GetOrphanProjectDirs())
        {
            try
            {
                // Drop the sandbox record first, otherwise the next startup restores the project.
                RemoveInstanceWorkspaceRecords(Path.GetFileName(dir));
                Directory.Delete(dir, true);
                removed++;
            }
            catch
            {
                failed++;
            }
        }
        return (removed, failed);
    }

    public void RefreshProjectMemoryStats(ProjectProfile proj)
    {
        try
        {
            var conversations = GetConversations(proj.Id);
            proj.ConversationCount = conversations.Count;
            if (conversations.Count > 0)
            {
                var maxMod = conversations.Max(c => c.LastModifiedTime);
                if (maxMod > DateTime.MinValue)
                {
                    proj.LastActiveAt = maxMod;
                }
            }
        }
        catch { }
    }

    public static string ToWorkspaceUri(string localPath)
    {
        var full = Path.GetFullPath(localPath).Replace('\\', '/');
        if (full.Length >= 2 && char.IsLetter(full[0]) && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full.Substring(2).Replace(" ", "%20");
            return $"file:///{drive}%3A{rest}";
        }
        return $"file:///{full.Replace(" ", "%20")}";
    }

    public static string? DetectGeminiDir(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath)) return null;

        var dirName = Path.GetFileName(inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (dirName.Equals(".gemini", StringComparison.OrdinalIgnoreCase)) return inputPath;

        var subGemini = Path.Combine(inputPath, ".gemini");
        if (Directory.Exists(subGemini)) return subGemini;

        if (Directory.Exists(Path.Combine(inputPath, "config")) || 
            Directory.Exists(Path.Combine(inputPath, "antigravity")) || 
            Directory.Exists(Path.Combine(inputPath, "skills")))
        {
            return inputPath;
        }

        return null;
    }

    public static string? NormalizeWorkspaceUri(string? uriStr)
    {
        if (string.IsNullOrWhiteSpace(uriStr)) return null;

        try
        {
            var unescaped = Uri.UnescapeDataString(uriStr).Trim();
            if (unescaped.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var p = unescaped.Substring("file:///".Length).Replace('/', '\\');
                if (p.Length >= 3 && p[0] == '\\' && char.IsLetter(p[1]) && p[2] == ':')
                {
                    p = p.Substring(1);
                }
                return Path.GetFullPath(p);
            }

            if (Uri.TryCreate(unescaped, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                var local = uri.LocalPath;
                if (local.Length >= 3 && local[0] == '\\' && char.IsLetter(local[1]) && local[2] == ':')
                {
                    local = local.Substring(1);
                }
                return Path.GetFullPath(local);
            }

            if (Path.IsPathRooted(unescaped))
            {
                return Path.GetFullPath(unescaped);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool ImportStockProjectsAndMemories(out int importedCount, out string message)
    {
        var hostGemini = GetRealHostGeminiDir();
        return ImportFromGeminiDir(hostGemini, out importedCount, out _, out message);
    }

    public bool ImportFromGeminiDir(string sourcePath, out int importedCount, out int convosCount, out string message)
    {
        importedCount = 0;
        convosCount = 0;
        message = string.Empty;

        var geminiRoot = DetectGeminiDir(sourcePath);
        if (string.IsNullOrEmpty(geminiRoot) || !Directory.Exists(geminiRoot))
        {
            message = $"所选路径中未检测到有效的 .gemini 配置或记忆库目录结构！\n路径: {sourcePath}";
            return false;
        }

        var configProjectsDir = Path.Combine(geminiRoot, "config", "projects");
        var projectsJson = Path.Combine(geminiRoot, "projects.json");
        var dbPath = Path.Combine(geminiRoot, "antigravity", "conversation_summaries.db");
        var brainDir = Path.Combine(geminiRoot, "antigravity", "brain");
        var convosDir = Path.Combine(geminiRoot, "antigravity", "conversations");

        var discoveredPaths = new Dictionary<string, (string? Guid, string Name, List<string> ConvoIds, DateTime LastActive)>(StringComparer.OrdinalIgnoreCase);

        // Read projects from the official config/projects/*.json.
        if (Directory.Exists(configProjectsDir))
        {
            foreach (var projFile in Directory.EnumerateFiles(configProjectsDir, "*.json"))
            {
                try
                {
                    var fileGuid = Path.GetFileNameWithoutExtension(projFile);
                    var fText = File.ReadAllText(projFile);
                    var node = JsonNode.Parse(fText);
                    if (node == null) continue;

                    var projId = node["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(projId)) projId = fileGuid;

                    var projName = node["name"]?.ToString();
                    // Skip sidecars (e.g. android-cli-plugin/compose-preview).
                    if (projName != null && (projName.Contains('/') || projName.Contains('\\')))
                    {
                        continue;
                    }

                    string? folderUri = null;

                    if (node["projectResources"]?["resources"] is JsonArray resArr && resArr.Count > 0)
                    {
                        var first = resArr[0];
                        folderUri = first?["gitFolder"]?["folderUri"]?.ToString() 
                            ?? first?["folderUri"]?.ToString();
                    }

                    if (string.IsNullOrEmpty(folderUri))
                    {
                        folderUri = node["folderUri"]?.ToString();
                    }

                    if (!string.IsNullOrEmpty(folderUri))
                    {
                        var localPath = NormalizeWorkspaceUri(folderUri);
                        if (!string.IsNullOrEmpty(localPath))
                        {
                            if (string.IsNullOrWhiteSpace(projName))
                            {
                                projName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            }
                            if (string.IsNullOrWhiteSpace(projName)) projName = "Project";

                            if (!discoveredPaths.ContainsKey(localPath))
                            {
                                discoveredPaths[localPath] = (projId, projName, new List<string>(), DateTime.UtcNow);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // Discover workspaces from projects.json (legacy data).
        if (File.Exists(projectsJson))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(projectsJson));
                if (doc?["projects"] is JsonObject pObj)
                {
                    foreach (var kvp in pObj)
                    {
                        var folder = kvp.Key;
                        var localPath = NormalizeWorkspaceUri(folder);
                        if (!string.IsNullOrEmpty(localPath))
                        {
                            var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            if (string.IsNullOrEmpty(name)) name = kvp.Value?.ToString() ?? "Project";
                            if (!discoveredPaths.ContainsKey(localPath))
                            {
                                discoveredPaths[localPath] = (null, name, new List<string>(), DateTime.UtcNow);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // Attribute conversations from conversation_summaries.db.
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id, workspace_uris, last_modified_time, project_id FROM conversation_summaries";
                using var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    var convoId = reader.GetString(0);
                    var urisJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var modTime = reader.IsDBNull(2) ? DateTime.UtcNow : reader.GetDateTime(2);
                    var pId = reader.IsDBNull(3) ? null : reader.GetString(3);

                    if (!string.IsNullOrEmpty(urisJson))
                    {
                        try
                        {
                            var uris = JsonSerializer.Deserialize<List<string>>(urisJson);
                            if (uris != null && uris.Count > 0)
                            {
                                foreach (var uriStr in uris)
                                {
                                    var localPath = NormalizeWorkspaceUri(uriStr);
                                    if (!string.IsNullOrEmpty(localPath))
                                    {
                                        if (discoveredPaths.TryGetValue(localPath, out var info))
                                        {
                                            if (!info.ConvoIds.Contains(convoId))
                                            {
                                                info.ConvoIds.Add(convoId);
                                            }
                                            discoveredPaths[localPath] = (info.Guid ?? pId, info.Name, info.ConvoIds, modTime > info.LastActive ? modTime : info.LastActive);
                                        }
                                        else
                                        {
                                            var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                                            if (string.IsNullOrWhiteSpace(name)) name = "Project";
                                            discoveredPaths[localPath] = (pId, name, new List<string> { convoId }, modTime);
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        if (discoveredPaths.Count == 0)
        {
            message = "在指定的 .gemini 目录中未检测到任何有效的本地项目记录。";
            return false;
        }

        // Create a separate memory store per project.
        foreach (var (path, (guid, name, convoIds, lastActive)) in discoveredPaths)
        {
            var existing = _projects.FirstOrDefault(p => 
                (!string.IsNullOrEmpty(guid) && p.Id.Equals(guid, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(p.Path) && p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

            if (existing == null)
            {
                existing = new ProjectProfile
                {
                    Id = EnsureSafeId(guid),
                    Name = name,
                    Path = path,
                    CreatedAt = DateTime.UtcNow
                };
                _projects.Add(existing);
                importedCount++;
            }
            else if (!string.IsNullOrEmpty(guid)
                     && IsSafeIdSegment(guid)
                     && !existing.Id.Equals(guid, StringComparison.OrdinalIgnoreCase))
            {
                // Existing project carries a generated id: migrate it to the official GUID.
                var oldBrain = GetProjectBrainDir(existing.Id);
                var oldConvos = GetProjectConversationsDir(existing.Id);
                var oldDb = GetProjectSummaryDbPath(existing.Id);

                existing.Id = guid;

                var newBrain = GetProjectBrainDir(existing.Id);
                var newConvos = GetProjectConversationsDir(existing.Id);
                var newDb = GetProjectSummaryDbPath(existing.Id);

                Directory.CreateDirectory(newBrain);
                Directory.CreateDirectory(newConvos);

                if (Directory.Exists(oldBrain))
                {
                    try { CopyDirectory(oldBrain, newBrain); } catch { }
                }
                if (Directory.Exists(oldConvos))
                {
                    try { CopyDirectory(oldConvos, newConvos); } catch { }
                }
                if (File.Exists(oldDb) && !File.Exists(newDb))
                {
                    try { File.Copy(oldDb, newDb, true); } catch { }
                }
            }

            existing.ConversationCount = convoIds.Count;
            existing.LastActiveAt = lastActive;
            convosCount += convoIds.Count;

            var targetBrain = GetProjectBrainDir(existing.Id);
            var targetConvos = GetProjectConversationsDir(existing.Id);
            Directory.CreateDirectory(targetBrain);
            Directory.CreateDirectory(targetConvos);

            // Copy the brain folder and conversation db.
            foreach (var cid in convoIds)
            {
                var srcBrainCid = Path.Combine(brainDir, cid);
                var destBrainCid = Path.Combine(targetBrain, cid);
                if (Directory.Exists(srcBrainCid) && !Directory.Exists(destBrainCid))
                {
                    try { CopyDirectory(srcBrainCid, destBrainCid); } catch { }
                }

                var srcConvoFile = Path.Combine(convosDir, $"{cid}.db");
                var destConvoFile = Path.Combine(targetConvos, $"{cid}.db");
                if (File.Exists(srcConvoFile) && !File.Exists(destConvoFile))
                {
                    try { File.Copy(srcConvoFile, destConvoFile, true); } catch { }
                }
            }

            // Build the project's own conversation_summaries.db.
            var destDb = GetProjectSummaryDbPath(existing.Id);
            if (File.Exists(dbPath) && convoIds.Count > 0)
            {
                try
                {
                    if (!CopySqliteSnapshot(dbPath, destDb))
                    {
                        // Fall back to a direct copy when the snapshot fails.
                        File.Copy(dbPath, destDb, true);
                    }

                    using var conn = new SqliteConnection($"Data Source={destDb}");
                    conn.Open();

                    using (var tx = conn.BeginTransaction())
                    {
                        DeleteRowsNotIn(conn, tx, "conversation_summaries", convoIds);
                        // Optional tables may not exist in older stock databases.
                        try { DeleteRowsNotIn(conn, tx, "conversations_fts", convoIds); } catch (Exception ex) { DiagLog.Swallowed("project.fts", ex); }
                        try { DeleteRowsNotIn(conn, tx, "conversation_parents", convoIds); } catch (Exception ex) { DiagLog.Swallowed("project.parents", ex); }
                        tx.Commit();
                    }

                    using var vacuum = conn.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();

                }
                catch { }
            }
        }

        SaveProjects();
        message = $"成功导入/同步了 {discoveredPaths.Count} 个项目与 {convosCount} 条历史对话！所有历史记忆已建立独立专有库。";
        return true;
    }

    public string GetConversationBrainDir(string projectId, string conversationId) => 
        Path.Combine(GetProjectBrainDir(projectId), conversationId);

    public bool UpdateConversationTitle(string projectId, string conversationId, string newTitle, out string? error)
    {
        error = null;
        var dbPath = GetProjectSummaryDbPath(projectId);
        if (!File.Exists(dbPath))
        {
            error = "未找到项目的会话数据库。";
            return false;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE conversation_summaries SET title = @title WHERE conversation_id = @id";
            cmd.Parameters.AddWithValue("@title", newTitle);
            cmd.Parameters.AddWithValue("@id", conversationId);
            var rows = cmd.ExecuteNonQuery();
            if (rows > 0)
            {
                try
                {
                    using var ftsCmd = conn.CreateCommand();
                    ftsCmd.CommandText = "UPDATE conversations_fts SET title = @title WHERE conversation_id = @id";
                    ftsCmd.Parameters.AddWithValue("@title", newTitle);
                    ftsCmd.Parameters.AddWithValue("@id", conversationId);
                    ftsCmd.ExecuteNonQuery();
                }
                catch { }
                return true;
            }
            error = "未找到目标会话记录。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool DeleteConversation(string projectId, string conversationId, out string? error)
    {
        error = null;
        var dbPath = GetProjectSummaryDbPath(projectId);
        if (!File.Exists(dbPath))
        {
            error = "未找到项目的会话数据库。";
            return false;
        }

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM conversation_summaries WHERE conversation_id = @id";
                cmd.Parameters.AddWithValue("@id", conversationId);
                cmd.ExecuteNonQuery();

                try
                {
                    using var ftsCmd = conn.CreateCommand();
                    ftsCmd.CommandText = "DELETE FROM conversations_fts WHERE conversation_id = @id";
                    ftsCmd.Parameters.AddWithValue("@id", conversationId);
                    ftsCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var parentCmd = conn.CreateCommand();
                    parentCmd.CommandText = "DELETE FROM conversation_parents WHERE conversation_id = @id OR parent_id = @id";
                    parentCmd.Parameters.AddWithValue("@id", conversationId);
                    parentCmd.ExecuteNonQuery();
                }
                catch { }
            }

            var brainDir = GetConversationBrainDir(projectId, conversationId);
            if (Directory.Exists(brainDir) || JunctionService.IsJunction(brainDir))
            {
                try { JunctionService.DeleteDirectorySafe(brainDir); } catch { }
            }

            var convoFile = Path.Combine(GetProjectConversationsDir(projectId), $"{conversationId}.db");
            if (File.Exists(convoFile))
            {
                try { File.Delete(convoFile); } catch { }
                DeleteSqliteSiblings(convoFile);
            }

            // Remove stopped/unlocked instance copies as well so GetConversations / next harvest won't resurrect it.
            DeleteConversationFromStoppedInstances(conversationId);

            var proj = _projects.FirstOrDefault(p => p.Id == projectId);
            if (proj != null)
            {
                RefreshProjectMemoryStats(proj);
                SaveProjects();
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void DeleteConversationFromStoppedInstances(string conversationId)
    {
        try
        {
            var instancesDir = Path.Combine(_baseDir, "data", "instances");
            if (!Directory.Exists(instancesDir)) return;

            foreach (var instDir in Directory.GetDirectories(instancesDir))
            {
                var instAnti = Path.Combine(instDir, ".gemini", "antigravity");
                var instDb = Path.Combine(instAnti, "conversation_summaries.db");
                if (File.Exists(instDb) && IsUsableSqlite(instDb))
                {
                    try
                    {
                        using var conn = new SqliteConnection($"Data Source={instDb};Default Timeout=1;Pooling=False");
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "DELETE FROM conversation_summaries WHERE conversation_id = @id";
                        cmd.Parameters.AddWithValue("@id", conversationId);
                        cmd.ExecuteNonQuery();
                    }
                    catch { continue; }
                }

                var instBrain = Path.Combine(instAnti, "brain", conversationId);
                if (Directory.Exists(instBrain) || JunctionService.IsJunction(instBrain))
                {
                    try { JunctionService.DeleteDirectorySafe(instBrain); } catch { }
                }

                var instConvo = Path.Combine(instAnti, "conversations", $"{conversationId}.db");
                if (File.Exists(instConvo))
                {
                    try { File.Delete(instConvo); DeleteSqliteSiblings(instConvo); } catch { }
                }
            }
        }
        catch { }
    }

    public bool ForkConversation(
        string projectId, 
        string sourceConversationId, 
        int upToStepIndex, 
        string newTitle, 
        out string newConversationId, 
        out string? error)
    {
        newConversationId = Guid.NewGuid().ToString("D");
        error = null;

        var srcBrainDir = ResolveConversationBrainDir(projectId, sourceConversationId);
        if (!Directory.Exists(srcBrainDir))
        {
            error = "未找到源会话的脑库资产目录。";
            return false;
        }

        var destBrainDir = GetConversationBrainDir(projectId, newConversationId);
        Directory.CreateDirectory(destBrainDir);

        var srcLogsDir = Path.Combine(srcBrainDir, ".system_generated", "logs");
        var destLogsDir = Path.Combine(destBrainDir, ".system_generated", "logs");
        Directory.CreateDirectory(destLogsDir);

        int filteredCount = 0;
        int lastUserInputStepIndex = 0;
        string lastPreview = "";

        // Aggregate and copy compact transcript lines (chunks + main) up to upToStepIndex.
        var compactLines = ReadFilteredTranscriptJsonLines(
            srcLogsDir,
            chunkSubDir: Path.Combine("chunks", "transcript"),
            mainFileName: "transcript.jsonl",
            upToStepIndex);
        if (compactLines.Count > 0)
        {
            var destTranscript = Path.Combine(destLogsDir, "transcript.jsonl");
            using var writer = new StreamWriter(destTranscript, false, Encoding.UTF8);
            foreach (var (_, line, node) in compactLines)
            {
                writer.WriteLine(line);
                filteredCount++;
                var step = node["step_index"]?.GetValue<int>() ?? 0;
                var type = node["type"]?.ToString();
                var src = node["source"]?.ToString();
                if (type == "USER_INPUT" || src == "USER_EXPLICIT")
                {
                    lastUserInputStepIndex = step;
                }
                var content = node["content"]?.ToString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    lastPreview = content.Length > 120 ? content[..120] + "..." : content;
                }
            }
        }

        // Aggregate and copy full transcript lines (chunks + main) up to upToStepIndex.
        var fullLines = ReadFilteredTranscriptJsonLines(
            srcLogsDir,
            chunkSubDir: Path.Combine("chunks", "transcript_full"),
            mainFileName: "transcript_full.jsonl",
            upToStepIndex);
        if (fullLines.Count > 0)
        {
            var destTranscriptFull = Path.Combine(destLogsDir, "transcript_full.jsonl");
            using var writerFull = new StreamWriter(destTranscriptFull, false, Encoding.UTF8);
            foreach (var (_, line, _) in fullLines)
            {
                writerFull.WriteLine(line);
            }
        }

        // Copy artifacts and uploads (skip .system_generated, handled above).
        try
        {
            foreach (var file in Directory.EnumerateFiles(srcBrainDir))
            {
                var destFile = Path.Combine(destBrainDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }
            foreach (var dir in Directory.EnumerateDirectories(srcBrainDir))
            {
                var dirName = Path.GetFileName(dir);
                if (dirName.Equals(".system_generated", StringComparison.OrdinalIgnoreCase)) continue;
                var destSubDir = Path.Combine(destBrainDir, dirName);
                CopyDirectory(dir, destSubDir);
            }
        }
        catch { }

        // Create the summary DB from the official blank schema first; the row insert below needs it.
        var dbPath = GetProjectSummaryDbPath(projectId);
        InitSummaryDb(projectId);

        if (!File.Exists(dbPath))
        {
            error = "项目会话摘要库不存在，且无法从官方空白模板重建，已中止 Fork。";
            return false;
        }

        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var tx = conn.BeginTransaction();

                string? workspaceUris = null;
                string? status = "DONE";
                string? source = "USER";
                string? projIdVal = projectId;
                string? agentName = string.Empty;
                int nestingDepth = 0;
                string? appDataDir = string.Empty;
                var foundSourceMeta = false;

                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = "SELECT workspace_uris, status, source, project_id, agent_name, nesting_depth, app_data_dir FROM conversation_summaries WHERE conversation_id = @srcId";
                    selectCmd.Parameters.AddWithValue("@srcId", sourceConversationId);
                    using var reader = selectCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        foundSourceMeta = true;
                        workspaceUris = reader.IsDBNull(0) ? null : reader.GetString(0);
                        status = reader.IsDBNull(1) ? "DONE" : reader.GetString(1);
                        source = reader.IsDBNull(2) ? "USER" : reader.GetString(2);
                        projIdVal = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3)) ? projectId : reader.GetString(3);
                        agentName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                        nestingDepth = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                        appDataDir = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                    }
                }

                // Live/unharvested source chats sit inside an instance sandbox summary DB rather than proj memory yet.
                if (!foundSourceMeta)
                {
                    var instancesDir = Path.Combine(_baseDir, "data", "instances");
                    if (Directory.Exists(instancesDir))
                    {
                        foreach (var instDir in Directory.GetDirectories(instancesDir))
                        {
                            if (foundSourceMeta || JunctionService.IsJunction(instDir)) continue;
                            var instDb = Path.Combine(instDir, ".gemini", "antigravity", "conversation_summaries.db");
                            if (!File.Exists(instDb)) continue;
                            TryQuerySqliteSnapshot(instDb, instConn =>
                            {
                                using var cmd = instConn.CreateCommand();
                                cmd.CommandText = "SELECT workspace_uris, status, source, project_id, agent_name, nesting_depth, app_data_dir FROM conversation_summaries WHERE conversation_id = @srcId LIMIT 1";
                                cmd.Parameters.AddWithValue("@srcId", sourceConversationId);
                                using var r = cmd.ExecuteReader();
                                if (r.Read())
                                {
                                    foundSourceMeta = true;
                                    workspaceUris = r.IsDBNull(0) ? null : r.GetString(0);
                                    status = r.IsDBNull(1) ? "DONE" : r.GetString(1);
                                    source = r.IsDBNull(2) ? "USER" : r.GetString(2);
                                    projIdVal = r.IsDBNull(3) || string.IsNullOrWhiteSpace(r.GetString(3)) ? projectId : r.GetString(3);
                                    agentName = r.IsDBNull(4) ? string.Empty : r.GetString(4);
                                    nestingDepth = r.IsDBNull(5) ? 0 : r.GetInt32(5);
                                    appDataDir = r.IsDBNull(6) ? string.Empty : r.GetString(6);
                                }
                                return true;
                            }, out _);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(workspaceUris))
                {
                    var targetProj = _projects.FirstOrDefault(p => p.Id == projectId);
                    workspaceUris = targetProj != null && !string.IsNullOrWhiteSpace(targetProj.Path)
                        ? JsonSerializer.Serialize(new[] { ToWorkspaceUri(targetProj.Path) })
                        : "[]";
                }

                var nowUtc = DateTime.UtcNow;
                var inserted = 0;
                using (var insertCmd = conn.CreateCommand())
                {
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"
                        INSERT INTO conversation_summaries 
                        (conversation_id, title, preview, step_count, last_modified_time, workspace_uris, status, source, project_id, agent_name, parent_conversation_id, nesting_depth, last_user_input_time, last_user_input_step_index, app_data_dir)
                        VALUES 
                        (@id, @title, @preview, @steps, @modTime, @uris, @status, @source, @projId, @agent, @parentId, @depth, @lastUserTime, @lastUserStep, @appDataDir)";

                    insertCmd.Parameters.AddWithValue("@id", newConversationId);
                    insertCmd.Parameters.AddWithValue("@title", newTitle);
                    insertCmd.Parameters.AddWithValue("@preview", lastPreview);
                    insertCmd.Parameters.AddWithValue("@steps", filteredCount);
                    insertCmd.Parameters.AddWithValue("@modTime", nowUtc);
                    insertCmd.Parameters.AddWithValue("@uris", workspaceUris);
                    insertCmd.Parameters.AddWithValue("@status", status ?? "DONE");
                    insertCmd.Parameters.AddWithValue("@source", source ?? "USER");
                    insertCmd.Parameters.AddWithValue("@projId", projIdVal ?? projectId);
                    insertCmd.Parameters.AddWithValue("@agent", agentName ?? string.Empty);
                    insertCmd.Parameters.AddWithValue("@parentId", sourceConversationId);
                    insertCmd.Parameters.AddWithValue("@depth", nestingDepth);
                    insertCmd.Parameters.AddWithValue("@lastUserTime", nowUtc);
                    insertCmd.Parameters.AddWithValue("@lastUserStep", lastUserInputStepIndex);
                    insertCmd.Parameters.AddWithValue("@appDataDir", appDataDir ?? string.Empty);
                    inserted = insertCmd.ExecuteNonQuery();
                }

                if (inserted <= 0)
                {
                    // Disposing the uncommitted transaction rolls it back; no partial row remains.
                    error = "写入会话摘要记录失败，已回滚 Fork。";
                    return false;
                }

                try
                {
                    using var ftsCmd = conn.CreateCommand();
                    ftsCmd.Transaction = tx;
                    ftsCmd.CommandText = "INSERT INTO conversations_fts (conversation_id, title, preview) VALUES (@id, @title, @preview)";
                    ftsCmd.Parameters.AddWithValue("@id", newConversationId);
                    ftsCmd.Parameters.AddWithValue("@title", newTitle);
                    ftsCmd.Parameters.AddWithValue("@preview", lastPreview);
                    ftsCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var parentCmd = conn.CreateCommand();
                    parentCmd.Transaction = tx;
                    parentCmd.CommandText = "INSERT OR REPLACE INTO conversation_parents (conversation_id, parent_id) VALUES (@id, @pid)";
                    parentCmd.Parameters.AddWithValue("@id", newConversationId);
                    parentCmd.Parameters.AddWithValue("@pid", sourceConversationId);
                    parentCmd.ExecuteNonQuery();
                }
                catch { }

                tx.Commit();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        var srcConvoFile = ResolveConversationConvoDbPath(projectId, sourceConversationId);
        var destConvoFile = Path.Combine(GetProjectConversationsDir(projectId), $"{newConversationId}.db");
        if (File.Exists(srcConvoFile))
        {
            try { CopySqliteSnapshot(srcConvoFile, destConvoFile); } catch { }
        }

        var proj = _projects.FirstOrDefault(p => p.Id == projectId);
        if (proj != null)
        {
            RefreshProjectMemoryStats(proj);
            SaveProjects();
        }

        return true;
    }

    public List<ConversationSummaryItem> GetConversations(string projectId)
    {
        var byId = new Dictionary<string, ConversationSummaryItem>(StringComparer.OrdinalIgnoreCase);
        var dbPath = GetProjectSummaryDbPath(projectId);
        var isLoose = IsLooseChats(projectId);
        var proj = isLoose ? null : _projects.FirstOrDefault(p => p.Id == projectId);

        if (!isLoose)
        {
            var needsInit = !File.Exists(dbPath) || !IsUsableSqlite(dbPath);
            if (needsInit)
            {
                try
                {
                    if (File.Exists(dbPath))
                    {
                        DeleteSqliteSiblings(dbPath);
                        File.Delete(dbPath);
                    }
                    InitSummaryDb(projectId);
                }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("project.recover-summary-db", ex);
                }
            }

            if (File.Exists(dbPath))
            {
                if (!TryReadSummaryItems(dbPath, byId))
                {
                    // Corrupted B-tree pages (e.g. SQLite Error 26): rebuild cleanly and reindex from brain.
                    try
                    {
                        SqliteConnection.ClearAllPools();
                        DeleteSqliteSiblings(dbPath);
                        if (File.Exists(dbPath)) File.Delete(dbPath);
                        InitSummaryDb(projectId);
                        byId.Clear();
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Swallowed("project.reinit-corrupt-summary", ex);
                    }
                }
            }

            // Recover any brain/{cid} conversations missing from conversation_summaries (e.g. after Error 26 wipe).
            var brainDir = GetProjectBrainDir(projectId);
            if (Directory.Exists(brainDir) && HasUnindexedBrainConversations(brainDir, byId))
            {
                try
                {
                    var repair = new ProjectMemoryRepairService(this);
                    if (repair.ReindexMissingForProject(projectId) > 0 && File.Exists(dbPath))
                    {
                        TryReadSummaryItems(dbPath, byId);
                    }
                }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("project.auto-reindex", ex);
                }
            }
        }
        else if (File.Exists(dbPath))
        {
            TryReadSummaryItems(dbPath, byId);
        }

        // Surface conversations currently live in any instance sandbox (even before process exit / sync).
        MergeLiveInstanceConversations(projectId, proj, isLoose, byId);

        return byId.Values
            .OrderByDescending(c => c.LastModifiedTime)
            .ToList();
    }

    private static bool HasUnindexedBrainConversations(string brainDir, IReadOnlyDictionary<string, ConversationSummaryItem> indexed)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(brainDir))
            {
                if (JunctionService.IsJunction(dir)) continue;
                var cid = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(cid) || cid.Equals("tempmediaStorage", StringComparison.OrdinalIgnoreCase)) continue;
                if (!indexed.ContainsKey(cid)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryReadSummaryItems(string dbPath, Dictionary<string, ConversationSummaryItem> byId)
    {
        return TryQuerySqliteSnapshot(dbPath, conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, title, preview, step_count, last_modified_time FROM conversation_summaries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cid = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrEmpty(cid)) continue;

                var item = new ConversationSummaryItem
                {
                    ConversationId = cid,
                    Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Preview = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    StepCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    LastModifiedTime = reader.IsDBNull(4) ? DateTime.MinValue : SafeReadDateTime(reader, 4)
                };

                UpsertNewerSummary(byId, item);
            }
            return true;
        }, out _);
    }

    private void MergeLiveInstanceConversations(
        string projectId,
        ProjectProfile? proj,
        bool isLoose,
        Dictionary<string, ConversationSummaryItem> byId)
    {
        try
        {
            var instancesDir = Path.Combine(_baseDir, "data", "instances");
            if (!Directory.Exists(instancesDir)) return;

            foreach (var instDir in Directory.GetDirectories(instancesDir))
            {
                if (JunctionService.IsJunction(instDir)) continue;
                var instAnti = Path.Combine(instDir, ".gemini", "antigravity");
                var instDb = Path.Combine(instAnti, "conversation_summaries.db");
                if (!File.Exists(instDb)) continue;

                TryQuerySqliteSnapshot(instDb, conn =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT conversation_id, title, preview, step_count, last_modified_time, workspace_uris, project_id FROM conversation_summaries";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var cid = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        if (string.IsNullOrEmpty(cid)) continue;

                        var urisJson = reader.IsDBNull(5) ? null : reader.GetString(5);
                        var pId = reader.IsDBNull(6) ? null : reader.GetString(6);

                        bool belongs;
                        if (isLoose)
                        {
                            belongs = IsProjectlessConversation(pId, urisJson);
                        }
                        else
                        {
                            if (IsProjectlessConversation(pId, urisJson)) continue;
                            belongs = string.Equals(pId, projectId, StringComparison.OrdinalIgnoreCase)
                                || (proj != null && !string.IsNullOrEmpty(proj.Path) && MatchesWorkspaceUris(proj.Path, urisJson));
                        }
                        if (!belongs) continue;

                        var hasLocalArtifacts = Directory.Exists(Path.Combine(instAnti, "brain", cid))
                            || File.Exists(Path.Combine(instAnti, "conversations", $"{cid}.db"));
                        if (!hasLocalArtifacts) continue;

                        var item = new ConversationSummaryItem
                        {
                            ConversationId = cid,
                            Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            Preview = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            StepCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                            LastModifiedTime = reader.IsDBNull(4) ? DateTime.MinValue : SafeReadDateTime(reader, 4)
                        };
                        UpsertNewerSummary(byId, item);
                    }
                    return true;
                }, out _);
            }
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("project.merge-live-instances", ex);
        }
    }

    private static void UpsertNewerSummary(Dictionary<string, ConversationSummaryItem> byId, ConversationSummaryItem candidate)
    {
        if (!byId.TryGetValue(candidate.ConversationId, out var existing))
        {
            byId[candidate.ConversationId] = candidate;
            return;
        }

        if (candidate.LastModifiedTime > existing.LastModifiedTime
            || (candidate.LastModifiedTime == existing.LastModifiedTime && candidate.StepCount > existing.StepCount))
        {
            byId[candidate.ConversationId] = candidate;
        }
    }

    private static DateTime SafeReadDateTime(SqliteDataReader reader, int index)
    {
        try { return reader.GetDateTime(index); }
        catch
        {
            try
            {
                var raw = reader.GetString(index);
                if (DateTime.TryParse(raw, out var dt)) return dt;
            }
            catch { }
            return DateTime.MinValue;
        }
    }

    internal static bool MatchesWorkspaceUris(string projectPath, string? workspaceUrisJson)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(workspaceUrisJson)) return false;

        string normProj;
        try { normProj = Path.GetFullPath(projectPath.Trim()).TrimEnd('\\', '/'); }
        catch { normProj = projectPath.Trim().Replace('/', '\\').TrimEnd('\\'); }

        try
        {
            if (JsonNode.Parse(workspaceUrisJson) is JsonArray arr)
            {
                foreach (var entry in arr)
                {
                    var normUri = NormalizeWorkspaceUri(entry?.ToString())?.TrimEnd('\\', '/');
                    if (!string.IsNullOrEmpty(normUri) && string.Equals(normUri, normProj, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
        }
        catch { }

        var fwd = normProj.Replace('\\', '/');
        var encoded = fwd.Replace(":", "%3A");
        return workspaceUrisJson.Contains(fwd, StringComparison.OrdinalIgnoreCase)
            || workspaceUrisJson.Contains(encoded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the freshest brain directory for <paramref name="conversationId"/> across both
    /// canonical project memory and any live instance sandboxes.
    /// </summary>
    public string ResolveConversationBrainDir(string projectId, string conversationId)
    {
        var canonical = GetConversationBrainDir(projectId, conversationId);
        var bestDir = canonical;
        var bestScore = ScoreBrainDir(canonical);

        try
        {
            var instancesDir = Path.Combine(_baseDir, "data", "instances");
            if (Directory.Exists(instancesDir))
            {
                foreach (var instDir in Directory.GetDirectories(instancesDir))
                {
                    if (JunctionService.IsJunction(instDir)) continue;
                    var candidate = Path.Combine(instDir, ".gemini", "antigravity", "brain", conversationId);
                    if (!Directory.Exists(candidate) || JunctionService.IsJunction(candidate)) continue;

                    var score = ScoreBrainDir(candidate);
                    if (score.hasLogs && (!bestScore.hasLogs
                        || score.latestWriteUtc > bestScore.latestWriteUtc
                        || (score.latestWriteUtc == bestScore.latestWriteUtc && score.totalBytes > bestScore.totalBytes)))
                    {
                        bestScore = score;
                        bestDir = candidate;
                    }
                }
            }
        }
        catch { }

        return bestDir;
    }

    private string ResolveConversationConvoDbPath(string projectId, string conversationId)
    {
        var canonical = Path.Combine(GetProjectConversationsDir(projectId), $"{conversationId}.db");
        var bestPath = canonical;
        var bestTime = File.Exists(canonical) ? File.GetLastWriteTimeUtc(canonical) : DateTime.MinValue;

        try
        {
            var instancesDir = Path.Combine(_baseDir, "data", "instances");
            if (Directory.Exists(instancesDir))
            {
                foreach (var instDir in Directory.GetDirectories(instancesDir))
                {
                    if (JunctionService.IsJunction(instDir)) continue;
                    var candidate = Path.Combine(instDir, ".gemini", "antigravity", "conversations", $"{conversationId}.db");
                    if (!File.Exists(candidate)) continue;

                    var t = File.GetLastWriteTimeUtc(candidate);
                    var wal = candidate + "-wal";
                    if (File.Exists(wal))
                    {
                        var wt = File.GetLastWriteTimeUtc(wal);
                        if (wt > t) t = wt;
                    }
                    if (!File.Exists(bestPath) || t > bestTime)
                    {
                        bestTime = t;
                        bestPath = candidate;
                    }
                }
            }
        }
        catch { }

        return bestPath;
    }

    private static (bool hasLogs, DateTime latestWriteUtc, long totalBytes) ScoreBrainDir(string brainDir)
    {
        if (string.IsNullOrEmpty(brainDir) || !Directory.Exists(brainDir) || JunctionService.IsJunction(brainDir))
            return (false, DateTime.MinValue, 0);

        var logsDir = Path.Combine(brainDir, ".system_generated", "logs");
        if (!Directory.Exists(logsDir))
            return (false, Directory.GetLastWriteTimeUtc(brainDir), 0);

        var hasLogs = false;
        var latest = DateTime.MinValue;
        long bytes = 0;

        foreach (var file in EnumerateTranscriptLogFiles(logsDir, includeChunks: true))
        {
            hasLogs = true;
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > latest) latest = info.LastWriteTimeUtc;
                bytes += info.Length;
            }
            catch { }
        }

        return (hasLogs, latest, bytes);
    }

    private static IEnumerable<string> EnumerateTranscriptLogFiles(string logsDir, bool includeChunks)
    {
        foreach (var name in new[] { "transcript.jsonl", "transcript_full.jsonl" })
        {
            var p = Path.Combine(logsDir, name);
            if (File.Exists(p)) yield return p;
        }

        if (!includeChunks) yield break;

        foreach (var chunkSub in new[] { Path.Combine(logsDir, "chunks", "transcript"), Path.Combine(logsDir, "chunks", "transcript_full") })
        {
            if (!Directory.Exists(chunkSub)) continue;
            IEnumerable<string> chunkFiles;
            try
            {
                chunkFiles = Directory.EnumerateFiles(chunkSub, "*.jsonl")
                    .OrderBy(f => ExtractLeadingChunkOrder(Path.GetFileNameWithoutExtension(f)))
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                continue;
            }

            foreach (var cf in chunkFiles) yield return cf;
        }
    }

    private static List<(int StepIndex, string Line, JsonNode Node)> ReadFilteredTranscriptJsonLines(
        string srcLogsDir,
        string chunkSubDir,
        string mainFileName,
        int upToStepIndex)
    {
        var files = new List<string>();
        var chunkDir = Path.Combine(srcLogsDir, chunkSubDir);
        if (Directory.Exists(chunkDir))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(chunkDir, "*.jsonl")
                    .OrderBy(f => ExtractLeadingChunkOrder(Path.GetFileNameWithoutExtension(f)))
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch { }
        }

        var mainFile = Path.Combine(srcLogsDir, mainFileName);
        if (File.Exists(mainFile)) files.Add(mainFile);

        var byStep = new SortedDictionary<int, (int StepIndex, string Line, JsonNode Node)>();
        foreach (var file in files)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var node = JsonNode.Parse(line);
                        if (node == null) continue;
                        var step = node["step_index"]?.GetValue<int>() ?? 0;
                        if (step <= upToStepIndex)
                        {
                            byStep[step] = (step, line, node);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return byStep.Values.ToList();
    }

    private static int ExtractLeadingChunkOrder(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    public List<TranscriptStepItem> GetConversationTranscript(string projectId, string conversationId)
    {
        var brainDir = ResolveConversationBrainDir(projectId, conversationId);
        return ReadMergedTranscriptSteps(brainDir);
    }

    /// <summary>
    /// Reads and merges Antigravity 2.35+ transcript logs (<c>transcript.jsonl</c>, <c>transcript_full.jsonl</c>,
    /// and <c>chunks/transcript*/</c>) using <see cref="FileShare.ReadWrite"/> so live sessions stay readable
    /// and truncated compact entries are upgraded from full logs.
    /// </summary>
    public static List<TranscriptStepItem> ReadMergedTranscriptSteps(string brainDir)
    {
        if (string.IsNullOrWhiteSpace(brainDir) || !Directory.Exists(brainDir)) return [];

        var logsDir = Path.Combine(brainDir, ".system_generated", "logs");
        if (!Directory.Exists(logsDir)) return [];

        var byStep = new Dictionary<int, TranscriptStepItem>();
        var truncatedMap = new Dictionary<int, HashSet<string>>();

        void IngestFile(string filePath, bool isFullSource)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line;
                int lineOrdinal = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var node = JsonNode.Parse(line);
                        if (node == null) continue;

                        var stepIndex = node["step_index"]?.GetValue<int>() ?? lineOrdinal;
                        lineOrdinal++;

                        var item = new TranscriptStepItem
                        {
                            StepIndex = stepIndex,
                            Source = node["source"]?.ToString() ?? string.Empty,
                            Type = node["type"]?.ToString() ?? string.Empty,
                            Content = node["content"]?.ToString() ?? string.Empty,
                            Thinking = node["thinking"]?.ToString(),
                            InputTokens = node["input_tokens"]?.GetValue<int>(),
                            OutputTokens = node["output_tokens"]?.GetValue<int>()
                        };

                        if (DateTime.TryParse(node["created_at"]?.ToString(), out var dt))
                        {
                            item.CreatedAt = dt;
                        }

                        if (node["tool_calls"] is JsonArray tools && tools.Count > 0)
                        {
                            PopulateToolCallFields(item, tools);
                        }

                        HashSet<string>? truncFields = null;
                        if (node["truncated_fields"] is JsonArray tf && tf.Count > 0)
                        {
                            truncFields = tf
                                .Select(x => x?.ToString())
                                .Where(x => !string.IsNullOrEmpty(x))
                                .Select(x => x!)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        }

                        if (!byStep.TryGetValue(stepIndex, out var existing))
                        {
                            byStep[stepIndex] = item;
                            if (!isFullSource && truncFields != null)
                            {
                                truncatedMap[stepIndex] = truncFields;
                            }
                            continue;
                        }

                        truncatedMap.TryGetValue(stepIndex, out var prevTrunc);
                        var contentTruncated = prevTrunc?.Contains("content") == true;
                        var thinkingTruncated = prevTrunc?.Contains("thinking") == true;

                        if (!string.IsNullOrEmpty(item.Content) &&
                            (string.IsNullOrEmpty(existing.Content)
                             || (isFullSource && (contentTruncated || item.Content.Length >= existing.Content.Length))
                             || item.Content.Length > existing.Content.Length))
                        {
                            existing.Content = item.Content;
                            if (isFullSource) prevTrunc?.Remove("content");
                        }

                        if (!string.IsNullOrEmpty(item.Thinking) &&
                            (string.IsNullOrEmpty(existing.Thinking)
                             || (isFullSource && (thinkingTruncated || item.Thinking.Length >= existing.Thinking.Length))
                             || item.Thinking.Length > existing.Thinking.Length))
                        {
                            existing.Thinking = item.Thinking;
                            if (isFullSource) prevTrunc?.Remove("thinking");
                        }

                        if (!string.IsNullOrEmpty(item.ToolName) &&
                            (string.IsNullOrEmpty(existing.ToolName)
                             || (item.ToolArgs?.Length ?? 0) > (existing.ToolArgs?.Length ?? 0)))
                        {
                            existing.ToolName = item.ToolName;
                            existing.ToolSummary = item.ToolSummary;
                            existing.ToolArgs = item.ToolArgs;
                        }

                        if (string.IsNullOrEmpty(existing.Source)) existing.Source = item.Source;
                        if (string.IsNullOrEmpty(existing.Type)) existing.Type = item.Type;
                        if (existing.CreatedAt == DateTime.MinValue) existing.CreatedAt = item.CreatedAt;
                        existing.InputTokens ??= item.InputTokens;
                        existing.OutputTokens ??= item.OutputTokens;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // First read compact transcripts (chunks + main), then upgrade with untruncated full transcripts.
        var chunkCompactDir = Path.Combine(logsDir, "chunks", "transcript");
        if (Directory.Exists(chunkCompactDir))
        {
            foreach (var cf in Directory.EnumerateFiles(chunkCompactDir, "*.jsonl")
                .OrderBy(f => ExtractLeadingChunkOrder(Path.GetFileNameWithoutExtension(f)))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                IngestFile(cf, isFullSource: false);
            }
        }
        IngestFile(Path.Combine(logsDir, "transcript.jsonl"), isFullSource: false);

        var chunkFullDir = Path.Combine(logsDir, "chunks", "transcript_full");
        if (Directory.Exists(chunkFullDir))
        {
            foreach (var cf in Directory.EnumerateFiles(chunkFullDir, "*.jsonl")
                .OrderBy(f => ExtractLeadingChunkOrder(Path.GetFileNameWithoutExtension(f)))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                IngestFile(cf, isFullSource: true);
            }
        }
        IngestFile(Path.Combine(logsDir, "transcript_full.jsonl"), isFullSource: true);

        return byStep.Values.OrderBy(s => s.StepIndex).ToList();
    }

    private static void PopulateToolCallFields(TranscriptStepItem item, JsonArray tools)
    {
        if (tools.Count == 1)
        {
            var first = tools[0];
            item.ToolName = first?["name"]?.ToString();
            item.ToolArgs = first?["args"]?.ToJsonString();
            item.ToolSummary = first?["args"]?["toolSummary"]?.ToString();
            return;
        }

        var names = new List<string>(tools.Count);
        var summaries = new List<string>(tools.Count);
        var argsSb = new StringBuilder();

        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            var name = tool?["name"]?.ToString() ?? $"tool_{i + 1}";
            var summary = tool?["args"]?["toolSummary"]?.ToString();
            var argsJson = tool?["args"]?.ToJsonString();

            names.Add(name);
            if (!string.IsNullOrWhiteSpace(summary)) summaries.Add(summary!);

            if (argsSb.Length > 0) argsSb.AppendLine().AppendLine();
            argsSb.Append('[').Append(i + 1).Append("] ").Append(name);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                argsSb.Append(" (").Append(summary).Append(')');
            }
            if (!string.IsNullOrEmpty(argsJson))
            {
                argsSb.AppendLine().Append(argsJson);
            }
        }

        item.ToolName = names.Count <= 2
            ? string.Join(", ", names)
            : $"{names[0]} (+{tools.Count - 1})";
        item.ToolSummary = summaries.Count > 0
            ? string.Join(" | ", summaries)
            : $"{tools.Count} calls";
        item.ToolArgs = argsSb.ToString();
    }

    public bool ExportConversationToMarkdown(string projectId, string conversationId, string targetFilePath, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            var steps = GetConversationTranscript(projectId, conversationId);
            var summaries = GetConversations(projectId);
            var summary = summaries.FirstOrDefault(s => s.ConversationId == conversationId);
            var title = string.IsNullOrWhiteSpace(summary?.Title) ? "会话记录" : summary.Title;

            var sb = new StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine($"> **会话 ID**: `{conversationId}`  ");
            sb.AppendLine($"> **最后修改**: {summary?.FormattedTime ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}  ");
            sb.AppendLine($"> **总步数**: {steps.Count} 步  ");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var step in steps)
            {
                if (step.IsUser)
                {
                    sb.AppendLine($"### 👤 用户 ({step.FormattedTime})");
                    sb.AppendLine();
                    var cleanContent = step.Content;
                    if (cleanContent.Contains("<USER_REQUEST>") && cleanContent.Contains("</USER_REQUEST>"))
                    {
                        var start = cleanContent.IndexOf("<USER_REQUEST>") + 14;
                        var end = cleanContent.IndexOf("</USER_REQUEST>");
                        cleanContent = cleanContent.Substring(start, end - start).Trim();
                    }
                    sb.AppendLine(cleanContent);
                    sb.AppendLine();
                }
                else if (step.IsModel)
                {
                    sb.AppendLine($"### 🤖 Antigravity ({step.FormattedTime})");
                    sb.AppendLine();
                    if (!string.IsNullOrWhiteSpace(step.Thinking))
                    {
                        sb.AppendLine("> [!NOTE] 思考过程 (Thinking)");
                        sb.AppendLine("> " + step.Thinking.Replace("\n", "\n> "));
                        sb.AppendLine();
                    }
                    if (!string.IsNullOrEmpty(step.ToolName))
                    {
                        sb.AppendLine($"- **执行工具**: `{step.ToolName}` {(!string.IsNullOrEmpty(step.ToolSummary) ? $"({step.ToolSummary})" : "")}");
                        if (!string.IsNullOrEmpty(step.ToolArgs))
                        {
                            sb.AppendLine("```json");
                            sb.AppendLine(step.ToolArgs);
                            sb.AppendLine("```");
                        }
                        sb.AppendLine();
                    }
                    if (!string.IsNullOrWhiteSpace(step.Content))
                    {
                        sb.AppendLine(step.Content);
                        sb.AppendLine();
                    }
                }
                else if (step.IsToolOutput)
                {
                    sb.AppendLine($"<details><summary>🛠️ 工具输出 (步骤 {step.StepIndex})</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(step.Content);
                    sb.AppendLine("```");
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }
            }

            var dir = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(targetFilePath, sb.ToString(), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public bool ExportAllConversationsToMarkdown(string projectId, string targetDirectoryPath, out int exportedCount, out string errorMessage)
    {
        exportedCount = 0;
        errorMessage = string.Empty;
        try
        {
            Directory.CreateDirectory(targetDirectoryPath);
            var convos = GetConversations(projectId);
            foreach (var c in convos)
            {
                var safeTitle = string.Concat(c.Title.Split(Path.GetInvalidFileNameChars())).Trim();
                if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "Session";
                var fileName = $"{safeTitle}_{c.ShortId}.md";
                var filePath = Path.Combine(targetDirectoryPath, fileName);
                if (ExportConversationToMarkdown(projectId, c.ConversationId, filePath, out _))
                {
                    exportedCount++;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public void NotifyProjectStateChanged(ProjectProfile proj)
    {
        ProjectStateChanged?.Invoke(proj);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) return;

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }
}
