using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gravityless.Core.Services;

public class SkillItem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Real on-disk directory name; use it for delete/locate because Name may come from SKILL.md frontmatter.</summary>
    public string DirName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool HasSkillMd { get; set; }
    public DateTime LastModified { get; set; }
    public string LastModifiedText => LastModified == default ? "--" : LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public class PluginItem
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Real on-disk directory name; Name may be overridden by plugin.json.</summary>
    public string DirName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool HasPluginJson { get; set; }
    public int SkillCount { get; set; }
    public bool HasMcp { get; set; }
    public bool HasRules { get; set; }
    public bool HasHooks { get; set; }
    public string SummaryTag { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public string LastModifiedText => LastModified == default ? "--" : LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public class McpServerItem
{
    public string Name { get; set; } = string.Empty;
    public string TransportType { get; set; } = "Stdio";
    public string CommandOrUrl { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public string EnvSummary { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;
}

public class ToolService
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions IndentedJsonUnsafeRelaxed = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _baseDir;

    public string SharedDir => Path.Combine(_baseDir, "shared");
    public string SkillsDir => Path.Combine(SharedDir, "skills");
    public string PluginsDir => Path.Combine(SharedDir, "plugins");
    public string McpDir => Path.Combine(SharedDir, "mcp");
    public string McpConfigFile => Path.Combine(McpDir, "mcp_config.json");

    public ToolService(string? baseDir = null)
    {
        // baseDir is the install root; instances junction to <root>\shared
        _baseDir = baseDir ?? AppContext.BaseDirectory;
        MigrateLegacySharedDir();
        EnsureDirectories();
    }

    /// <summary>Merges the legacy data\shared pool into <see cref="SharedDir"/> and archives the remainder.</summary>
    private void MigrateLegacySharedDir()
    {
        try
        {
            var legacy = Path.Combine(_baseDir, "data", "shared");
            if (!Directory.Exists(legacy))
            {
                return;
            }

            var target = SharedDir;
            if (string.Equals(
                    Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(target);

            foreach (var sub in new[] { "skills", "plugins", "mcp" })
            {
                var src = Path.Combine(legacy, sub);
                if (!Directory.Exists(src))
                {
                    continue;
                }

                var dst = Path.Combine(target, sub);
                Directory.CreateDirectory(dst);

                foreach (var dir in Directory.EnumerateDirectories(src))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var targetDir = Path.Combine(dst, name);
                    if (!Directory.Exists(targetDir))
                    {
                        try { Directory.Move(dir, targetDir); }
                        catch { TryMergeDirectory(dir, targetDir); }
                    }
                    else
                    {
                        TryMergeDirectory(dir, targetDir);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(src))
                {
                    var targetFile = Path.Combine(dst, Path.GetFileName(file));
                    if (!File.Exists(targetFile))
                    {
                        try { File.Move(file, targetFile); } catch { }
                    }
                }
            }

            // Keep user data: delete the empty legacy directory, otherwise rename it as a backup
            try
            {
                if (Directory.Exists(legacy)
                    && !Directory.EnumerateFileSystemEntries(legacy).Any())
                {
                    Directory.Delete(legacy, true);
                }
                else if (Directory.Exists(legacy))
                {
                    var backup = legacy + ".merged-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    Directory.Move(legacy, backup);
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("tools.delete", ex); }
        }
        catch { }
    }

    /// <summary>Copies only files missing from <paramref name="dst"/>; never overwrites.</summary>
    private static void TryMergeDirectory(string src, string dst)
    {
        try
        {
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.EnumerateFiles(src))
            {
                var targetFile = Path.Combine(dst, Path.GetFileName(file));
                if (!File.Exists(targetFile))
                {
                    try { File.Copy(file, targetFile, false); } catch { }
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(src))
            {
                TryMergeDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
            }
        }
        catch { }
    }

    /// <summary>True only for a single path segment: no separators, drive colon, "." or "..".</summary>
    private static bool IsSafeChildName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name == "." || name == "..")
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return name.IndexOf('/') < 0
            && name.IndexOf('\\') < 0
            && name.IndexOf(':') < 0;
    }

    /// <summary>Resolves <paramref name="name"/> under <paramref name="root"/> and re-checks the normalized path stays inside it.</summary>
    private static bool TryResolveChild(string root, string? name, out string resolved)
    {
        resolved = string.Empty;

        if (!IsSafeChildName(name))
        {
            return false;
        }

        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, name!));
            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolved = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SharedDir);
        Directory.CreateDirectory(SkillsDir);
        Directory.CreateDirectory(PluginsDir);
        Directory.CreateDirectory(McpDir);

        if (!File.Exists(McpConfigFile))
        {
            try
            {
                var initialJson = new JsonObject
                {
                    ["mcpServers"] = new JsonObject()
                };
                File.WriteAllText(McpConfigFile, initialJson.ToJsonString(IndentedJson));
            }
            catch (Exception ex) { DiagLog.Swallowed("tools.mcp-save", ex); }
        }

        EnsureBuiltinSkills();
    }

    private void EnsureBuiltinSkills()
    {
        try
        {
            var guideDir = Path.Combine(SkillsDir, "gravityless-customizations");
            var guideFile = Path.Combine(guideDir, "SKILL.md");
            if (!File.Exists(guideFile))
            {
                Directory.CreateDirectory(guideDir);
                File.WriteAllText(guideFile, BuiltinGuideSkillMd, Encoding.UTF8);
            }
        }
        catch { }
    }

    private const string BuiltinGuideSkillMd = """
---
name: gravityless-customizations
description: Comprehensive guide and storage path reference for Skills, Plugins, and MCP servers in the Gravityless multi-instance environment. Activate whenever creating, installing, modifying, or configuring agent skills, plugins, or MCP servers.
---

# Gravityless Extension & Customization Storage Guide

This skill guides the agent on where and how to store, install, and configure customizations (Skills, Plugins, Rules, and MCP Servers) within the **Gravityless** multi-instance environment.

## 1. Multi-Instance Storage Architecture

Gravityless manages multiple isolated Antigravity instances. Each instance runs in an isolated sandbox where `%USERPROFILE%` and `~` point to `data/instances/<instance-id>/`.

To avoid losing extensions when switching instances or projects, Gravityless provides a **Global Shared Pool** (`<GravitylessInstallDir>\shared\`), which is automatically mounted into each instance via NTFS Junctions:
- **Shared Skills**: `~/.gemini/config/skills/` -> `<GravitylessInstallDir>\shared\skills\`
- **Shared Plugins**: `~/.gemini/config/plugins/` -> `<GravitylessInstallDir>\shared\plugins\`
- **Shared MCP Servers**: `~/.gemini/config/mcp_config.json` <-> `<GravitylessInstallDir>\shared\mcp\mcp_config.json`

---

## 2. Path Reference (Where to Store)

### A. Skills (Agent Capabilities & Procedures)
- **Target Location (Global Shared)**:
  `%GRAVITYLESS_SHARED_SKILLS%\<skill-name>\` or `~/.gemini/config/skills/<skill-name>/`
- **File Structure**:
  ```
  <skill-name>/
  ├── SKILL.md                 # Required: YAML frontmatter + Markdown instructions
  ├── scripts/                 # Optional: scripts or utilities
  └── resources/               # Optional: templates or assets
  ```
- **Required Frontmatter Format**:
  ```yaml
  ---
  name: <skill-name>
  description: >-
    Clear 1-2 sentence description explaining what this skill does and when the agent should activate it.
  ---
  ```

### B. Plugins (Packaged Bundles of Skills, Rules, and MCPs)
- **Target Location (Global Shared)**:
  `%GRAVITYLESS_SHARED_PLUGINS%\<plugin-name>\` or `~/.gemini/config/plugins/<plugin-name>/`
- **File Structure**:
  ```
  <plugin-name>/
  ├── plugin.json              # Required: Plugin manifest
  ├── skills/                  # Optional: bundled skills
  └── rules/                   # Optional: bundled rules
  ```
- **Manifest Format (`plugin.json`)**:
  ```json
  {
    "name": "<plugin-name>",
    "version": "1.0.0",
    "description": "Brief description of plugin capabilities",
    "skills": ["skills/<skill-name>"]
  }
  ```

### C. MCP Servers (Model Context Protocol Services)
- **Target Location (Global Shared)**:
  `%GRAVITYLESS_SHARED_MCP_JSON%` (also synced at `~/.gemini/config/mcp_config.json`)
- **Configuration Format**:
  ```json
  {
    "mcpServers": {
      "<server-name>": {
        "command": "node",
        "args": ["path/to/server/index.js"],
        "env": {}
      }
    }
  }
  ```
- **Installation Rule**:
  When configuring an MCP server, edit the shared config at `%GRAVITYLESS_SHARED_MCP_JSON%` (or `<GravitylessInstallDir>\shared\mcp\mcp_config.json`). It is shared across all instances.

### D. Workspace Tier (Project-Specific Customizations)
*Only use this tier if the user explicitly requests project-private customizations*:
- Workspace Skills: `<workspace-root>/.agents/skills/<skill-name>/SKILL.md`
- Workspace Rules: `<workspace-root>/GEMINI.md` or `<workspace-root>/.agents/rules/`
- Workspace MCP: `<workspace-root>/.agents/mcp_config.json`

---

## 3. Environment Variables Reference

When Gravityless launches Antigravity, the following environment variables are available:

| Environment Variable | Description |
| :--- | :--- |
| `%GRAVITYLESS_SHARED_DIR%` | Root directory of Gravityless shared extensions (`<GravitylessInstallDir>\shared`) |
| `%GRAVITYLESS_SHARED_SKILLS%` | Central directory for all shared skills (`<GravitylessInstallDir>\shared\skills`) |
| `%GRAVITYLESS_SHARED_PLUGINS%` | Central directory for all shared plugins (`<GravitylessInstallDir>\shared\plugins`) |
| `%GRAVITYLESS_SHARED_MCP_JSON%` | Central configuration file for shared MCP servers |
| `%GRAVITYLESS_INSTANCE_ID%` | Unique identifier for current Antigravity instance |
| `%USERPROFILE%` / `%HOME%` | Sandboxed instance user root (`data/instances/<id>`) |

---

## 4. Operational Best Practices for Agents

1. **Default to Global Shared**: Unless the user explicitly says *"只在当前项目生效"* (project-only), always install skills, plugins, and MCP configurations into the **Global Shared Pool** so they persist and are available across all instances.
2. **Use Relative Path `~/.gemini/config/` When Resolving**: Since `~/.gemini/config/skills` and `plugins` are already junction-linked by Gravityless, writing to `~/.gemini/config/skills/<skill-name>/` directly writes to the shared pool. Alternatively, resolve via `%GRAVITYLESS_SHARED_SKILLS%`.
3. **Always Include Valid Frontmatter**: Every `SKILL.md` must have valid YAML frontmatter containing `name` and `description` so Antigravity can index it for progressive disclosure.
4. **Notify the User**: Once installed, inform the user that the extension is placed in the Gravityless shared pool and can be viewed or toggled in Gravityless's **"工具与扩展管理 (Tools & Extensions)"** UI tab.
""";

    public List<SkillItem> ListSkills()
    {
        var list = new List<SkillItem>();
        if (!Directory.Exists(SkillsDir)) return list;

        foreach (var dir in Directory.EnumerateDirectories(SkillsDir))
        {
            var dirName = Path.GetFileName(dir);
            var skillMd = Path.Combine(dir, "SKILL.md");
            bool hasMd = File.Exists(skillMd);

            var (name, desc) = hasMd 
                ? ParseSkillMd(skillMd, dirName) 
                : (dirName, "普通工具目录 (未包含 SKILL.md 规范文件)");

            var lastMod = Directory.GetLastWriteTimeUtc(dir);

            list.Add(new SkillItem
            {
                Name = name,
                DirName = dirName,
                Description = desc,
                Path = dir,
                HasSkillMd = hasMd,
                LastModified = lastMod
            });
        }

        return list.OrderBy(s => s.Name).ToList();
    }

    public List<PluginItem> ListPlugins()
    {
        var list = new List<PluginItem>();
        if (!Directory.Exists(PluginsDir)) return list;

        foreach (var dir in Directory.EnumerateDirectories(PluginsDir))
        {
            var manifestName = Path.GetFileName(dir);
            var desc = "Antigravity 扩展插件包";
            bool hasPluginJson = false;

            var manifestFile = Path.Combine(dir, "plugin.json");
            if (File.Exists(manifestFile))
            {
                hasPluginJson = true;
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(manifestFile));
                    var d = node?["description"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(d)) desc = d;
                    var n = node?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(n)) manifestName = n;
                }
                catch { }
            }
            else
            {
                // Fallback to package.json
                var pkgFile = Path.Combine(dir, "package.json");
                if (File.Exists(pkgFile))
                {
                    try
                    {
                        var node = JsonNode.Parse(File.ReadAllText(pkgFile));
                        var d = node?["description"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(d)) desc = d;
                    }
                    catch { }
                }
            }

            var pluginSkillsDir = Path.Combine(dir, "skills");
            int skillCount = Directory.Exists(pluginSkillsDir) 
                ? Directory.EnumerateDirectories(pluginSkillsDir).Count() 
                : 0;

            bool hasMcp = File.Exists(Path.Combine(dir, "mcp_config.json"));
            bool hasRules = Directory.Exists(Path.Combine(dir, "rules")) || File.Exists(Path.Combine(dir, "AGENTS.md"));
            bool hasHooks = File.Exists(Path.Combine(dir, "hooks.json"));

            var tags = new List<string>();
            if (skillCount > 0) tags.Add($"Skills: {skillCount}");
            if (hasMcp) tags.Add("MCP");
            if (hasRules) tags.Add("Rules");
            if (hasHooks) tags.Add("Hooks");

            var summaryTag = tags.Count > 0 ? string.Join(" | ", tags) : "基础插件";

            var lastMod = Directory.GetLastWriteTimeUtc(dir);
            list.Add(new PluginItem
            {
                Name = manifestName,
                DirName = Path.GetFileName(dir),
                Description = desc,
                Path = dir,
                HasPluginJson = hasPluginJson,
                SkillCount = skillCount,
                HasMcp = hasMcp,
                HasRules = hasRules,
                HasHooks = hasHooks,
                SummaryTag = summaryTag,
                LastModified = lastMod
            });
        }

        return list.OrderBy(p => p.Name).ToList();
    }

    public List<McpServerItem> ListMcpServers()
    {
        var list = new List<McpServerItem>();
        if (!File.Exists(McpConfigFile)) return list;

        try
        {
            var text = File.ReadAllText(McpConfigFile);
            var root = JsonNode.Parse(text);
            var serversObj = root?["mcpServers"] as JsonObject;
            if (serversObj != null)
            {
                foreach (var kvp in serversObj)
                {
                    var name = kvp.Key;
                    var valObj = kvp.Value as JsonObject;
                    if (valObj == null) continue;

                    string transport = "Stdio";
                    string commandOrUrl = "";
                    string argsStr = "";

                    // A "url" entry means HTTP/SSE, otherwise a stdio command
                    if (valObj.ContainsKey("url"))
                    {
                        transport = "HTTP/SSE";
                        commandOrUrl = valObj["url"]?.ToString() ?? "";
                    }
                    else
                    {
                        commandOrUrl = valObj["command"]?.ToString() ?? "";
                        var argsArr = valObj["args"] as JsonArray;
                        argsStr = argsArr != null 
                            ? string.Join(" ", argsArr.Select(a => a?.ToString() ?? "")) 
                            : "";
                    }

                    var envObj = valObj["env"] as JsonObject;
                    var envStr = envObj != null && envObj.Count > 0
                        ? string.Join(", ", envObj.Select(e => $"{e.Key}={e.Value}"))
                        : "无环境变量";

                    list.Add(new McpServerItem
                    {
                        Name = name,
                        TransportType = transport,
                        CommandOrUrl = commandOrUrl,
                        Args = argsStr,
                        EnvSummary = envStr,
                        RawJson = valObj.ToJsonString(IndentedJson)
                    });
                }
            }
        }
        catch { }

        return list.OrderBy(m => m.Name).ToList();
    }

    public string GetMcpConfigRawJson()
    {
        if (File.Exists(McpConfigFile))
        {
            try
            {
                return File.ReadAllText(McpConfigFile);
            }
            catch { }
        }
        return "{\n  \"mcpServers\": {}\n}";
    }

    public bool SaveMcpConfigRawJson(string rawJson, out string? error)
    {
        error = null;
        try
        {
            var node = JsonNode.Parse(rawJson);
            if (node == null || node["mcpServers"] == null)
            {
                error = "JSON 必须包含根属性 \"mcpServers\" 对象。";
                return false;
            }

            File.WriteAllText(McpConfigFile, rawJson);
            return true;
        }
        catch (Exception ex)
        {
            error = $"JSON 格式错误: {ex.Message}";
            return false;
        }
    }

    public bool CreateSkill(string name, string? description, out string? error)
    {
        error = null;
        try
        {
            if (!TryResolveChild(SkillsDir, name, out var targetDir))
            {
                error = "技能名称不合法（不允许路径分隔符或 . / ..）。";
                return false;
            }
            if (Directory.Exists(targetDir))
            {
                error = "已存在同名技能目录。";
                return false;
            }

            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(Path.Combine(targetDir, "scripts"));
            Directory.CreateDirectory(Path.Combine(targetDir, "references"));

            var mdPath = Path.Combine(targetDir, "SKILL.md");
            var desc = string.IsNullOrWhiteSpace(description) ? $"Skill: {name}" : description.Trim();

            var content = $"---\nname: {name}\ndescription: >-\n  {desc}\n---\n\n# {name}\n\n{desc}\n\n## Instructions\n- Step 1: Initialize required context.\n- Step 2: Perform tool actions.\n";
            File.WriteAllText(mdPath, content);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool CreatePlugin(string name, string? description, out string? error)
    {
        error = null;
        try
        {
            if (!TryResolveChild(PluginsDir, name, out var targetDir))
            {
                error = "插件名称不合法（不允许路径分隔符或 . / ..）。";
                return false;
            }
            if (Directory.Exists(targetDir))
            {
                error = "已存在同名插件目录。";
                return false;
            }

            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(Path.Combine(targetDir, "skills"));
            Directory.CreateDirectory(Path.Combine(targetDir, "rules"));

            var desc = string.IsNullOrWhiteSpace(description) ? $"Plugin: {name}" : description.Trim();

            var manifestObj = new JsonObject
            {
                ["name"] = name,
                ["description"] = desc
            };
            File.WriteAllText(Path.Combine(targetDir, "plugin.json"), manifestObj.ToJsonString(IndentedJson));

            var mcpObj = new JsonObject
            {
                ["mcpServers"] = new JsonObject()
            };
            File.WriteAllText(Path.Combine(targetDir, "mcp_config.json"), mcpObj.ToJsonString(IndentedJson));

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool DeleteSkill(string name, out string? error)
    {
        error = null;
        try
        {
            if (!TryResolveChild(SkillsDir, name, out var targetDir))
            {
                error = "技能名称不合法（不允许路径分隔符或 . / ..）。";
                return false;
            }

            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
                return true;
            }
            error = "未找到目标技能目录。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool DeletePlugin(string name, out string? error)
    {
        error = null;
        try
        {
            if (!TryResolveChild(PluginsDir, name, out var targetDir))
            {
                error = "插件名称不合法（不允许路径分隔符或 . / ..）。";
                return false;
            }

            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
                return true;
            }
            error = "未找到目标插件目录。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool AddOrUpdateMcpServer(string name, string commandOrUrl, string args, string? envJson, bool isHttp, out string? error)
    {
        error = null;
        try
        {
            var raw = GetMcpConfigRawJson();
            var node = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            if (node["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                node["mcpServers"] = servers;
            }

            var serverObj = new JsonObject();

            if (isHttp)
            {
                serverObj["url"] = commandOrUrl.Trim();
            }
            else
            {
                serverObj["command"] = commandOrUrl.Trim();
                var argsList = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var argsArr = new JsonArray();
                foreach (var a in argsList) argsArr.Add(a);
                serverObj["args"] = argsArr;

                if (!string.IsNullOrWhiteSpace(envJson))
                {
                    try
                    {
                        var envNode = JsonNode.Parse(envJson);
                        if (envNode is JsonObject eObj) serverObj["env"] = eObj;
                    }
                    catch { }
                }
            }

            servers[name] = serverObj;

            File.WriteAllText(McpConfigFile, node.ToJsonString(IndentedJsonUnsafeRelaxed));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool DeleteMcpServer(string name, out string? error)
    {
        error = null;
        try
        {
            var raw = GetMcpConfigRawJson();
            var node = JsonNode.Parse(raw) as JsonObject;
            if (node?["mcpServers"] is JsonObject servers && servers.ContainsKey(name))
            {
                servers.Remove(name);
                File.WriteAllText(McpConfigFile, node.ToJsonString(IndentedJsonUnsafeRelaxed));
                return true;
            }
            error = "未找到指定的 MCP 服务。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ImportFromStock(out int skillsImported, out int pluginsImported, out bool mcpImported, out string message)
    {
        var hostGemini = ProjectService.GetRealHostGeminiDir();
        if (!Directory.Exists(hostGemini))
        {
            skillsImported = 0;
            pluginsImported = 0;
            mcpImported = false;
            message = $"未检测到宿主机官方 .gemini 目录 ({hostGemini})。";
            return false;
        }
        return ImportFromGeminiDir(hostGemini, out skillsImported, out pluginsImported, out mcpImported, out message);
    }

    /// <summary>Copies skill/plugin directories and merges MCP servers from every known location under <paramref name="sourcePath"/>.</summary>
    public bool ImportFromGeminiDir(string sourcePath, out int skillsImported, out int pluginsImported, out bool mcpImported, out string message)
    {
        skillsImported = 0;
        pluginsImported = 0;
        mcpImported = false;

        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            message = $"所选路径不存在或无法访问:\n{sourcePath}";
            return false;
        }

        EnsureDirectories();

        var geminiRoot = ProjectService.DetectGeminiDir(sourcePath) ?? sourcePath;

        var importedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Skills: a directory holding SKILL.md is itself a skill, otherwise its children are
        string[] sourceSkillDirs =
        [
            Path.Combine(geminiRoot, "config", "skills"),
            Path.Combine(geminiRoot, "antigravity", "skills"),
            Path.Combine(geminiRoot, "antigravity", "builtin", "skills"),
            Path.Combine(geminiRoot, "skills"),
            Path.Combine(geminiRoot, "shared", "skills"),
            sourcePath
        ];

        foreach (var srcDir in sourceSkillDirs)
        {
            if (Directory.Exists(srcDir))
            {
                if (File.Exists(Path.Combine(srcDir, "SKILL.md")))
                {
                    var skillName = Path.GetFileName(srcDir);
                    if (!string.IsNullOrEmpty(skillName) && importedSkills.Add(skillName))
                    {
                        var destDir = Path.Combine(SkillsDir, skillName);
                        try
                        {
                            CopyDirectory(srcDir, destDir);
                            skillsImported++;
                        }
                        catch { }
                    }
                    continue;
                }

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(srcDir))
                    {
                        var skillName = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(skillName) && importedSkills.Add(skillName))
                        {
                            var destDir = Path.Combine(SkillsDir, skillName);
                            try
                            {
                                CopyDirectory(dir, destDir);
                                skillsImported++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // Plugins: identified by plugin.json or package.json, own directory or children
        string[] sourcePluginDirs =
        [
            Path.Combine(geminiRoot, "config", "plugins"),
            Path.Combine(geminiRoot, "antigravity", "plugins"),
            Path.Combine(geminiRoot, "plugins"),
            Path.Combine(geminiRoot, "shared", "plugins"),
            sourcePath
        ];

        foreach (var srcDir in sourcePluginDirs)
        {
            if (Directory.Exists(srcDir))
            {
                if (File.Exists(Path.Combine(srcDir, "plugin.json")) || File.Exists(Path.Combine(srcDir, "package.json")))
                {
                    var pName = Path.GetFileName(srcDir);
                    if (!string.IsNullOrEmpty(pName) && importedPlugins.Add(pName))
                    {
                        var destDir = Path.Combine(PluginsDir, pName);
                        try
                        {
                            CopyDirectory(srcDir, destDir);
                            pluginsImported++;
                        }
                        catch { }
                    }
                    continue;
                }

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(srcDir))
                    {
                        var pName = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(pName) && importedPlugins.Add(pName))
                        {
                            var destDir = Path.Combine(PluginsDir, pName);
                            try
                            {
                                CopyDirectory(dir, destDir);
                                pluginsImported++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // MCP: merge host servers into the shared config without overwriting existing names
        string[] candidateMcpFiles =
        [
            Path.Combine(geminiRoot, "config", "mcp_config.json"),
            Path.Combine(geminiRoot, "antigravity", "mcp", "mcp_config.json"),
            Path.Combine(geminiRoot, "mcp_config.json"),
            Path.Combine(geminiRoot, "mcp", "mcp_config.json"),
            Path.Combine(geminiRoot, "shared", "mcp", "mcp_config.json"),
            Path.Combine(sourcePath, "mcp_config.json")
        ];

        var processedMcpFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mcpFile in candidateMcpFiles)
        {
            if (File.Exists(mcpFile) && processedMcpFiles.Add(mcpFile))
            {
                try
                {
                    var mcpText = File.ReadAllText(mcpFile);
                    var hostNode = JsonNode.Parse(mcpText);
                    if (hostNode?["mcpServers"] is JsonObject hostServers && hostServers.Count > 0)
                    {
                        var localRaw = GetMcpConfigRawJson();
                        var localNode = JsonNode.Parse(localRaw) as JsonObject ?? new JsonObject();
                        if (localNode["mcpServers"] is not JsonObject localServers)
                        {
                            localServers = new JsonObject();
                            localNode["mcpServers"] = localServers;
                        }

                        foreach (var kvp in hostServers)
                        {
                            if (!localServers.ContainsKey(kvp.Key) && kvp.Value != null)
                            {
                                localServers[kvp.Key] = kvp.Value.DeepClone();
                                mcpImported = true;
                            }
                        }

                        File.WriteAllText(McpConfigFile, localNode.ToJsonString(IndentedJsonUnsafeRelaxed));
                    }
                }
                catch { }
            }
        }

        if (skillsImported == 0 && pluginsImported == 0 && !mcpImported)
        {
            message = $"未在所选目录中发现有效的技能集 (skills)、插件包 (plugins) 或 MCP 配置文件。\n路径: {sourcePath}";
            return false;
        }

        message = $"已成功从所选目录导入/同步: {skillsImported} 个技能集, {pluginsImported} 个插件包" + (mcpImported ? "，并合并了 MCP 协议服务配置！" : "。");
        return true;
    }

    /// <summary>Reads name/description from the YAML frontmatter between the first two "---" lines.</summary>
    private static (string name, string desc) ParseSkillMd(string mdPath, string defaultName)
    {
        try
        {
            var lines = File.ReadAllLines(mdPath);
            string name = defaultName;
            string desc = "";
            bool inFrontMatter = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line == "---")
                {
                    if (!inFrontMatter) { inFrontMatter = true; continue; }
                    else { break; }
                }

                if (inFrontMatter)
                {
                    if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        name = line.Substring(5).Trim().Trim('"', '\'');
                    }
                    else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    {
                        desc = line.Substring(12).Trim().Trim('"', '\'');
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(desc))
            {
                var nonHeader = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && !l.StartsWith("---"));
                desc = nonHeader?.Trim() ?? "无详细描述";
            }

            return (string.IsNullOrWhiteSpace(name) ? defaultName : name, desc);
        }
        catch
        {
            return (defaultName, "无法解析 SKILL.md");
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSub);
        }
    }
}
