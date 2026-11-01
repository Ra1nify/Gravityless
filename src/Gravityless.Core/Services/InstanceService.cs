using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Gravityless.Core.Models;
using Gravityless.Core.Native;

namespace Gravityless.Core.Services;

public class InstanceService
{
    private readonly string _baseDir;
    private readonly string _instancesJsonPath;
    private readonly string _instancesStorageDir;
    private readonly string _sharedDir;
    private readonly AccountPoolService _accountPool;
    private readonly EngineService _engineService;
    private readonly GravitylessDaemon _daemon;
    private readonly ProjectService _projectService;
    private readonly List<InstanceProfile> _instances = [];
    private readonly Dictionary<string, Process> _runningProcesses = [];

    /// <summary>Shared with the capture flow; both touch the same global credential slot.</summary>
    private static SemaphoreSlim CredentialSlotLock => CredentialManager.SlotLock;
    private static TimeSpan CredentialSlotTtl => CredentialManager.SlotLockTtl;

    /// <summary>
    /// How long to let the language server finish adopting the credential before the slot is
    /// cleared. The read happens during LS startup, well inside this window.
    /// </summary>
    private const int CredentialReadWindowMs = 8000;

    public IReadOnlyList<InstanceProfile> Instances => _instances.AsReadOnly();

    public event Action<InstanceProfile>? InstanceStateChanged;

    public InstanceService(
        AccountPoolService accountPool, 
        EngineService engineService, 
        GravitylessDaemon daemon, 
        ProjectService projectService,
        string? baseDir = null)
    {
        _accountPool = accountPool;
        _engineService = engineService;
        _daemon = daemon;
        _projectService = projectService;
        _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
        
        var dataDir = Path.Combine(_baseDir, "data");
        Directory.CreateDirectory(dataDir);

        _instancesJsonPath = Path.Combine(dataDir, "instances.json");
        _instancesStorageDir = Path.Combine(dataDir, "instances");
        Directory.CreateDirectory(_instancesStorageDir);

        _sharedDir = Path.Combine(_baseDir, "shared");
        Directory.CreateDirectory(_sharedDir);
        Directory.CreateDirectory(Path.Combine(_sharedDir, "skills"));
        Directory.CreateDirectory(Path.Combine(_sharedDir, "plugins"));
        Directory.CreateDirectory(Path.Combine(_sharedDir, "mcp"));

        LoadInstances();
    }

    private void LoadInstances()
    {
        _instances.Clear();
        if (File.Exists(_instancesJsonPath))
        {
            try
            {
                var json = File.ReadAllText(_instancesJsonPath);
                var list = JsonSerializer.Deserialize<List<InstanceProfile>>(json);
                if (list != null)
                {
                    foreach (var inst in list)
                    {
                        inst.IsRunning = false;
                        inst.ProcessId = null;
                        inst.ListeningPort = null;
                        ReconcileAndHarvestProjects(inst);
                    }
                    _instances.AddRange(list);
                }
            }
            catch { }
        }
    }

    /// <summary>Persists instance state atomically. Throws on failure so callers can roll back.</summary>
    public void SaveInstances() => JsonStateStore.Write(_instancesJsonPath, _instances);

    /// <summary>Save that reports failure instead of throwing.</summary>
    public bool TrySaveInstances() => JsonStateStore.TryWrite(_instancesJsonPath, _instances);

    public void AddInstance(InstanceProfile profile)
    {
        _instances.Add(profile);
        SaveInstances();
    }

    public InstanceProfile CreateInstance(string name, string? accountId = null, List<string>? projectIds = null)
    {
        var inst = new InstanceProfile
        {
            Name = name,
            BoundAccountId = accountId,
            BoundProjectIds = projectIds ?? [],
            PreferredEngine = EngineType.Managed
        };
        _instances.Add(inst);
        SaveInstances();
        return inst;
    }

    public void DeleteInstance(string instanceId)
    {
        var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return;

        var instGemini = Path.Combine(_instancesStorageDir, inst.Id, ".gemini");

        if (inst.IsRunning)
        {
            StopInstance(instanceId);
        }

        // Release bindings before removing the instance; the Exited callback can no longer find it.
        if (!string.IsNullOrEmpty(inst.BoundAccountId))
        {
            try { _accountPool.CheckInAccount(inst.BoundAccountId, instGemini); } catch { }
        }

        var boundProjects = inst.BoundProjectIds
            .Select(id => _projectService.Projects.FirstOrDefault(p => p.Id == id))
            .OfType<ProjectProfile>()
            .ToList();

        foreach (var proj in boundProjects)
        {
            if (proj.RunningInstanceId == inst.Id
                || (inst.ProcessId.HasValue && proj.ProcessId == inst.ProcessId))
            {
                proj.IsRunning = false;
                proj.RunningInstanceId = null;
                proj.ProcessId = null;
                _projectService.NotifyProjectStateChanged(proj);
            }
        }

        if (boundProjects.Count > 0)
        {
            try { _projectService.SaveProjects(); } catch { }
        }

        _runningProcesses.Remove(instanceId);
        _instances.Remove(inst);
        SaveInstances();

        var instDir = Path.Combine(_instancesStorageDir, instanceId);
        if (Directory.Exists(instDir))
        {
            try
            {
                Directory.Delete(instDir, true);
            }
            catch (Exception ex) { DiagLog.Swallowed("instance.delete", ex); }
        }
    }

    /// <summary>
    /// Lists the projects bound to <paramref name="instanceId"/> that another live instance is
    /// currently holding. Uses in-memory state (<see cref="_runningProcesses"/>), which is
    /// authoritative for this process, rather than the persisted RunningInstanceId that can
    /// survive a crash.
    /// </summary>
    public List<ProjectConflict> FindProjectConflicts(string instanceId)
    {
        var result = new List<ProjectConflict>();

        var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return result;

        foreach (var projectId in inst.BoundProjectIds)
        {
            var holder = FindProjectHolder(projectId, exceptInstanceId: instanceId);
            if (holder == null) continue;

            var project = _projectService.Projects.FirstOrDefault(p => p.Id == projectId);
            result.Add(new ProjectConflict
            {
                ProjectId = projectId,
                ProjectName = project?.Name ?? projectId,
                InstanceId = holder.Id,
                InstanceName = holder.Name,
                StartedAt = holder.StartedAt
            });
        }

        return result;
    }

    /// <summary>Returns the live instance mounting <paramref name="projectId"/>, if any.</summary>
    private InstanceProfile? FindProjectHolder(string projectId, string exceptInstanceId)
    {
        foreach (var runningId in _runningProcesses.Keys)
        {
            if (runningId == exceptInstanceId) continue;

            var running = _instances.FirstOrDefault(i => i.Id == runningId);
            if (running?.BoundProjectIds.Contains(projectId) == true)
            {
                return running;
            }
        }

        return null;
    }

    public bool LaunchInstance(
        string instanceId, 
        ProxyConfig globalProxy, 
        out string errorMessage)
    {
        return LaunchInstance(instanceId, globalProxy, out errorMessage, force: false, out _, out _);
    }

    /// <summary>
    /// Launches the instance while skipping projects held by another live instance.
    /// <paramref name="excludedProjectIds"/> is transient: it only affects this launch and
    /// leaves <see cref="InstanceProfile.BoundProjectIds"/> untouched.
    /// </summary>
    public bool LaunchInstance(
        string instanceId,
        ProxyConfig globalProxy,
        IReadOnlyCollection<string>? excludedProjectIds,
        out string errorMessage)
    {
        return LaunchInstance(instanceId, globalProxy, out errorMessage, force: false,
            excludedProjectIds, out _, out _);
    }

    public bool LaunchInstance(
        string instanceId, 
        ProxyConfig globalProxy, 
        out string errorMessage,
        bool force,
        out bool conflictDetected,
        out int? conflictingPid)
    {
        return LaunchInstance(instanceId, globalProxy, out errorMessage, force,
            excludedProjectIds: null, out conflictDetected, out conflictingPid);
    }

    public bool LaunchInstance(
        string instanceId,
        ProxyConfig globalProxy,
        out string errorMessage,
        bool force,
        IReadOnlyCollection<string>? excludedProjectIds,
        out bool conflictDetected,
        out int? conflictingPid)
    {
        conflictDetected = false;
        conflictingPid = null;
        errorMessage = string.Empty;
        var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
        {
            errorMessage = "找不到指定的实例";
            return false;
        }

        // Harvest any non-junction project assets into shared storage before launch.
        ReconcileAndHarvestProjects(inst);

        // Instance's own process still alive?
        if (inst.IsRunning)
        {
            bool isProcessAlive = false;
            if (inst.ProcessId.HasValue)
            {
                try
                {
                    var proc = Process.GetProcessById(inst.ProcessId.Value);
                    if (!proc.HasExited) isProcessAlive = true;
                }
                catch { }
            }

            if (!isProcessAlive)
            {
                inst.IsRunning = false;
                inst.ProcessId = null;
                SaveInstances();
            }
            else if (!force)
            {
                conflictDetected = true;
                conflictingPid = inst.ProcessId;
                errorMessage = $"该实例已在运行中 (PID: {inst.ProcessId})";
                return false;
            }
            else
            {
                try
                {
                    if (inst.ProcessId.HasValue)
                    {
                        var proc = Process.GetProcessById(inst.ProcessId.Value);
                        if (!proc.HasExited)
                        {
                            proc.Kill(true);
                            proc.WaitForExit(1000);
                        }
                    }
                }
                catch { }

                inst.IsRunning = false;
                inst.ProcessId = null;
                SaveInstances();
            }
        }

        var managedExe = _engineService.GetManagedExePath();
        if (!File.Exists(managedExe))
        {
            errorMessage = "未找到 Gravityless 托管核心！请先前往【核心管理】执行“云端拉取”。";
            return false;
        }

        if (string.IsNullOrEmpty(inst.BoundAccountId))
        {
            errorMessage = "尚未为此实例分配号池账号，请在卡片下拉菜单中选择一个账号！";
            return false;
        }

        // Exclusive 1-to-1 account lease.
        if (!_accountPool.TryCheckOutAccount(inst.BoundAccountId, inst.Id, out var lockErr, force))
        {
            conflictDetected = true;
            conflictingPid = null;
            errorMessage = lockErr;
            return false;
        }

        var account = _accountPool.Accounts.First(a => a.Id == inst.BoundAccountId);

        // Project locks: one instance may own several projects.
        // Excluded ids are skipped for this launch only; the instance keeps its binding.
        // An empty result is valid: the instance starts with no workspace, where the user can
        // create a project or open a scratch chat.
        var boundProjects = new List<ProjectProfile>();
        foreach (var projId in inst.BoundProjectIds)
        {
            if (excludedProjectIds?.Contains(projId) == true) continue;

            var p = _projectService.Projects.FirstOrDefault(x => x.Id == projId);
            if (p != null) boundProjects.Add(p);
        }

        foreach (var proj in boundProjects)
        {
            if (proj.IsRunning)
            {
                bool isProjProcAlive = false;
                if (proj.ProcessId.HasValue)
                {
                    try
                    {
                        var proc = Process.GetProcessById(proj.ProcessId.Value);
                        if (!proc.HasExited) isProjProcAlive = true;
                    }
                    catch { }
                }

                if (!isProjProcAlive)
                {
                    proj.IsRunning = false;
                    proj.RunningInstanceId = null;
                    proj.ProcessId = null;
                    _projectService.SaveProjects();
                }
                else if (proj.RunningInstanceId != inst.Id)
                {
                    if (!force)
                    {
                        // Report the conflict and stop. The caller may relaunch with the
                        // conflicting projects excluded instead of killing the holder.
                        _accountPool.CheckInAccount(inst.BoundAccountId, Path.Combine(_instancesStorageDir, inst.Id, ".gemini"));
                        conflictDetected = true;
                        conflictingPid = proj.ProcessId;
                        errorMessage = $"目标项目【{proj.Name}】当前已被另一实例占用运行中 (PID: {proj.ProcessId})！为防止 SQLite 记忆库损坏，严禁并发多写。";
                        return false;
                    }
                    else
                    {
                        try
                        {
                            if (proj.ProcessId.HasValue)
                            {
                                var proc = Process.GetProcessById(proj.ProcessId.Value);
                                if (!proc.HasExited)
                                {
                                    proc.Kill(true);
                                    proc.WaitForExit(1000);
                                }
                            }
                        }
                        catch { }

                        proj.IsRunning = false;
                        proj.RunningInstanceId = null;
                        proj.ProcessId = null;
                        _projectService.SaveProjects();
                    }
                }
            }
        }

        // Prepare the sandbox directories.
        var instDir = Path.Combine(_instancesStorageDir, inst.Id);
        var instUserData = Path.Combine(instDir, "UserData");
        var instGemini = Path.Combine(instDir, ".gemini");
        var instAntigravity = Path.Combine(instGemini, "antigravity");
        var instConfig = Path.Combine(instGemini, "config");

        Directory.CreateDirectory(instUserData);
        Directory.CreateDirectory(instGemini);
        Directory.CreateDirectory(instAntigravity);
        Directory.CreateDirectory(instConfig);

        // Mount shared skills / plugins / MCP via NTFS junctions.
        SetupStorageMatrix(inst, instAntigravity, instConfig);

        // Mount the memory stores of bound projects (one or many).
        SetupProjectMemoryMount(inst, boundProjects, instGemini, instAntigravity, instUserData);

        // Inject credentials. The slot is global, so:
        //   - take the instance lock (TTL-bounded) so two launches cannot interleave writes;
        //   - refuse a blob whose email belongs to another account.
        if (!string.IsNullOrEmpty(account.CredentialBlob))
        {
            var acquired = CredentialSlotLock.Wait(CredentialSlotTtl);
            if (!acquired)
            {
                errorMessage = "凭证槽位被其他实例占用超过 10 秒，请稍后重试";
                return false;
            }

            try
            {
                var blobEmail = AccountPoolService.ExtractEmailFromCredBlob(account.CredentialBlob);
                if (string.IsNullOrEmpty(blobEmail)
                    || blobEmail.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
                {
                    // A rejected blob means the stored credential is corrupt; the launch must fail
                    // rather than run with whatever account the slot currently holds.
                    if (!CredentialManager.WriteCredential(account.CredentialBlob))
                    {
                        errorMessage = "账号凭证格式无效，已取消启动（请重新捕获该账号）";
                        return false;
                    }
                }
                else
                {
                    errorMessage = $"账号凭证归属不匹配（凭证属于 {blobEmail}，实例绑定 {account.Email}）";
                    return false;
                }
            }
            finally
            {
                CredentialSlotLock.Release();
            }
        }
        AccountPoolService.InjectAccountToGeminiDir(instGemini, account);
        InjectAppStorageUser(instUserData, account.Email);

        // Write the proxy config.
        var proxy = inst.UseCustomProxy ? inst.CustomProxy : globalProxy;
        ProxyService.WriteProxyConfigFile(instDir, proxy);

        // Build process arguments; the first bound project becomes the primary workspace.
        var primaryProj = boundProjects.FirstOrDefault(p => !string.IsNullOrEmpty(p.Path));
        var langArg = string.Equals(_daemon.Language, "zh-CN", StringComparison.OrdinalIgnoreCase) ? " --lang=zh-CN" : " --lang=en-US";
        var psi = new ProcessStartInfo
        {
            FileName = managedExe,
            Arguments = (primaryProj != null)
                ? $"--user-data-dir=\"{instUserData}\"{langArg} \"{primaryProj.Path}\""
                : $"--user-data-dir=\"{instUserData}\"{langArg}",
            UseShellExecute = false
        };
        var drive = Path.GetPathRoot(instDir) ?? "C:\\";
        var pathWithoutDrive = instDir.Substring(drive.Length - 1);

        psi.EnvironmentVariables["USERPROFILE"] = instDir;
        psi.EnvironmentVariables["HOME"] = instDir;
        psi.EnvironmentVariables["HOMEDRIVE"] = drive.TrimEnd('\\');
        psi.EnvironmentVariables["HOMEPATH"] = pathWithoutDrive;
        psi.EnvironmentVariables["APPDATA"] = Path.Combine(instDir, "AppData", "Roaming");
        psi.EnvironmentVariables["LOCALAPPDATA"] = Path.Combine(instDir, "AppData", "Local");

        psi.EnvironmentVariables["GRAVITYLESS_DAEMON_PORT"] = _daemon.Port.ToString();
        psi.EnvironmentVariables["GRAVITYLESS_INSTANCE_ID"] = inst.Id;
        psi.EnvironmentVariables["GRAVITYLESS_MICA_MATERIAL"] = _daemon.MicaMaterial;
        psi.EnvironmentVariables["GRAVITYLESS_MICA_OPACITY"] = _daemon.MicaOpacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        psi.EnvironmentVariables["GRAVITYLESS_LOCALE"] = _daemon.Language;
        var localesDir = Path.Combine(_baseDir, "locales");
        if (!Directory.Exists(localesDir)) { localesDir = Path.Combine(_baseDir, "data", "locales"); }
        psi.EnvironmentVariables["GRAVITYLESS_LOCALES_DIR"] = localesDir;

        // Shared extension paths, so agent skills and tools resolve them deterministically.
        psi.EnvironmentVariables["GRAVITYLESS_SHARED_DIR"] = _sharedDir;
        psi.EnvironmentVariables["GRAVITYLESS_SHARED_SKILLS"] = Path.Combine(_sharedDir, "skills");
        psi.EnvironmentVariables["GRAVITYLESS_SHARED_PLUGINS"] = Path.Combine(_sharedDir, "plugins");
        psi.EnvironmentVariables["GRAVITYLESS_SHARED_MCP_JSON"] = Path.Combine(_sharedDir, "mcp", "mcp_config.json");

        // Prefer resources/app: swap a free app.asar to app.asar.stock so the Mica patch is used.
        try
        {
            var resDir = Path.Combine(Path.GetDirectoryName(managedExe)!, "resources");
            var asar = Path.Combine(resDir, "app.asar");
            var asarStock = Path.Combine(resDir, "app.asar.stock");
            var appDist = Path.Combine(resDir, "app", "dist");

            // Re-inject when not unpacked, or when the injected stamp is stale and a .stock exists.
            var stockJs = Path.Combine(appDist, "utils.js.stock");
            var needsMicaPatch = !Directory.Exists(appDist)
                || (!MicaPatchService.IsMicaPatchCurrent(managedExe) && File.Exists(stockJs));

            if (needsMicaPatch && (File.Exists(asar) || File.Exists(asarStock) || Directory.Exists(appDist)))
            {
                MicaPatchService.ApplyMicaPatch(managedExe, _daemon.MicaMaterial, _daemon.MicaOpacity, out _);
            }

            // Swap app.asar to app.asar.stock so Electron loads resources/app.
            if (File.Exists(asar) && Directory.Exists(appDist))
            {
                if (File.Exists(asarStock))
                {
                    File.Delete(asarStock);
                }
                File.Move(asar, asarStock);
            }
        }
        catch { }

        if (proxy.Mode == ProxyMode.Manual)
        {
            var proxyUrl = $"{proxy.Protocol.ToString().ToLower()}://{proxy.Host}:{proxy.Port}";
            psi.EnvironmentVariables["ALL_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["HTTP_PROXY"] = proxyUrl;
            psi.EnvironmentVariables["HTTPS_PROXY"] = proxyUrl;
            if (!string.IsNullOrEmpty(proxy.BypassList))
            {
                psi.EnvironmentVariables["NO_PROXY"] = proxy.BypassList;
            }
        }

        try
        {
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("启动 Antigravity 实例失败");
            inst.IsRunning = true;
            inst.ProcessId = proc.Id;
            inst.StartedAt = DateTime.UtcNow;
            _runningProcesses[inst.Id] = proc;

            foreach (var proj in boundProjects)
            {
                proj.IsRunning = true;
                proj.RunningInstanceId = inst.Id;
                proj.ProcessId = proc.Id;
                proj.StartedAt = DateTime.UtcNow;
                _projectService.NotifyProjectStateChanged(proj);
            }
            if (boundProjects.Count > 0)
            {
                _projectService.SaveProjects();
            }

            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnInstanceProcessExited(inst.Id, instGemini, boundProjects);

            // The language server reads the credential slot during its own startup and then
            // keeps the token in memory. Clearing the slot afterwards frees it for the next
            // account, which is the only way to run several accounts on one machine.
            _ = ReleaseCredentialSlotAsync(inst);

            SaveInstances();
            InstanceStateChanged?.Invoke(inst);
            return true;
        }
        catch (Exception ex)
        {
            _accountPool.CheckInAccount(inst.BoundAccountId, instGemini);
            errorMessage = $"启动异常: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Returns the email that currently owns the global credential slot when it differs from
    /// <paramref name="instanceId"/>'s bound account. Null means no risk to report.
    /// </summary>
    public string? GetCredentialConflictEmail(string instanceId)
    {
        var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null || string.IsNullOrEmpty(inst.BoundAccountId)) return null;

        var account = _accountPool.Accounts.FirstOrDefault(a => a.Id == inst.BoundAccountId);
        if (account == null) return null;

        var slotOwner = _accountPool.GetSlotOwnerEmail();
        if (string.IsNullOrEmpty(slotOwner)) return null;

        return slotOwner.Equals(account.Email, StringComparison.OrdinalIgnoreCase)
            ? null
            : slotOwner;
    }

    public void StopInstance(string instanceId)
    {
        var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null || !inst.IsRunning) return;

        if (_runningProcesses.TryGetValue(instanceId, out var proc))
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Waits for the language server to adopt the credential, then clears the global slot.
    /// The read happens within a few seconds of process start; the delay only has to cover it.
    /// </summary>
    private async Task ReleaseCredentialSlotAsync(InstanceProfile inst)
    {
        try
        {
            await Task.Delay(CredentialReadWindowMs).ConfigureAwait(false);

            // A process that died early never adopted the token; keep the slot for a retry.
            if (!_runningProcesses.TryGetValue(inst.Id, out var proc) || proc.HasExited) return;

            var account = _accountPool.Accounts.FirstOrDefault(a => a.Id == inst.BoundAccountId);
            if (account == null) return;

            if (!CredentialSlotLock.Wait(0)) return;
            try
            {
                if (CredentialManager.ClearSlotIfOwnedBy(account.Email))
                {
                    inst.CredentialSlotReleased = true;
                    SaveInstances();
                    CredentialSlotReleased?.Invoke(inst);
                }
            }
            finally
            {
                CredentialSlotLock.Release();
            }
        }
        catch
        {
            // Releasing the slot is best-effort; a failure leaves the previous behaviour.
        }
    }

    /// <summary>Raised after an instance has consumed and released the global credential slot.</summary>
    public event Action<InstanceProfile>? CredentialSlotReleased;

    /// <summary>
    /// Flushes every WAL in the instance sandbox into its main database. Runs after the
    /// process tree is gone, so no writer can interleave. A database whose -wal is not merged
    /// reads as empty even though all its pages are on disk, which is how conversations were
    /// being harvested as zero-length files.
    /// </summary>
    private static void CheckpointInstanceDatabases(string instanceGeminiDir)
    {
        var root = Path.Combine(instanceGeminiDir, "antigravity");
        if (!Directory.Exists(root)) return;

        foreach (var pattern in new[] { "conversation_summaries.db", "conversations/*.db" })
        {
            IEnumerable<string> files;
            try
            {
                var dir = Path.GetDirectoryName(Path.Combine(root, pattern));
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir, Path.GetFileName(pattern), SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("instance.checkpoint-enumerate", ex);
                continue;
            }

            foreach (var db in files)
            {
                // No WAL means nothing to merge; opening would only create and drop one.
                if (!File.Exists(db + "-wal")) continue;

                try
                {
                    using var conn = new SqliteConnection($"Data Source={db};Default Timeout=5");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("instance.checkpoint", ex);
                }
            }
        }
    }

    private void OnInstanceProcessExited(string instanceId, string instanceGeminiDir, List<ProjectProfile> boundProjects)
    {
        var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return;

        _runningProcesses.Remove(instanceId);

        // Force every WAL into its main file before harvesting. A process killed mid-write
        // leaves committed pages in -wal while the .db itself is still empty; harvest would
        // then see a valid-looking but data-less file.
        CheckpointInstanceDatabases(instanceGeminiDir);

        // Harvest projects and memory on process exit.
        ReconcileAndHarvestProjects(inst);

        if (!string.IsNullOrEmpty(inst.BoundAccountId))
        {
            // Read-back touches the same global slot; hold the lock so a concurrent launch
            // cannot swap the token between our read and our write.
            if (CredentialSlotLock.Wait(CredentialSlotTtl))
            {
                try
                {
                    _accountPool.CheckInAccount(inst.BoundAccountId, instanceGeminiDir);
                }
                finally
                {
                    CredentialSlotLock.Release();
                }
            }
        }

        // Multi-project mode: write back conversations created during the session.
        if (boundProjects.Count > 1)
        {
            SyncMultiProjectOutput(inst, boundProjects, instanceGeminiDir);
        }

        foreach (var proj in boundProjects)
        {
            proj.IsRunning = false;
            proj.RunningInstanceId = null;
            proj.ProcessId = null;
            proj.LastActiveAt = DateTime.UtcNow;
            _projectService.RefreshProjectMemoryStats(proj);
            _projectService.NotifyProjectStateChanged(proj);
        }
        if (boundProjects.Count > 0)
        {
            _projectService.SaveProjects();
        }

        inst.IsRunning = false;
        inst.ProcessId = null;
        inst.ListeningPort = null;
        SaveInstances();
        InstanceStateChanged?.Invoke(inst);
    }

    private static void InjectAppStorageUser(string userDataDir, string email)
    {
        try
        {
            var storagePath = Path.Combine(userDataDir, "app_storage.json");
            var root = File.Exists(storagePath) 
                ? (JsonNode.Parse(File.ReadAllText(storagePath)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            root["jetski.onboarding.lastLoginUsername"] = email;
            root["jetski.onboarding.lastLoginIsGcpTos"] = "false";
            root["ide-install-wizard-shown"] = "true";

            File.WriteAllText(storagePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void SetupStorageMatrix(InstanceProfile inst, string instAntigravity, string instConfig)
    {
        var sharedSkills = Path.Combine(_sharedDir, "skills");
        Directory.CreateDirectory(sharedSkills);
        var instAntigravitySkills = Path.Combine(instAntigravity, "skills");
        var instConfigSkills = Path.Combine(instConfig, "skills");

        if (inst.ShareSkills)
        {
            JunctionService.CreateJunction(instAntigravitySkills, sharedSkills);
            JunctionService.CreateJunction(instConfigSkills, sharedSkills);
        }
        else
        {
            if (JunctionService.IsJunction(instAntigravitySkills)) JunctionService.RemoveJunction(instAntigravitySkills);
            if (JunctionService.IsJunction(instConfigSkills)) JunctionService.RemoveJunction(instConfigSkills);
        }

        var sharedPlugins = Path.Combine(_sharedDir, "plugins");
        Directory.CreateDirectory(sharedPlugins);
        var instPlugins = Path.Combine(instConfig, "plugins");
        if (inst.SharePlugins)
        {
            JunctionService.CreateJunction(instPlugins, sharedPlugins);
        }
        else if (JunctionService.IsJunction(instPlugins))
        {
            JunctionService.RemoveJunction(instPlugins);
        }

        var sharedMcp = Path.Combine(_sharedDir, "mcp");
        Directory.CreateDirectory(sharedMcp);
        var instMcp = Path.Combine(instAntigravity, "mcp");
        var sharedMcpJson = Path.Combine(sharedMcp, "mcp_config.json");
        var instConfigMcpJson = Path.Combine(instConfig, "mcp_config.json");

        if (inst.ShareMcp)
        {
            JunctionService.CreateJunction(instMcp, sharedMcp);
            if (File.Exists(sharedMcpJson))
            {
                try
                {
                    Directory.CreateDirectory(instConfig);
                    File.Copy(sharedMcpJson, instConfigMcpJson, true);
                }
                catch (Exception ex) { DiagLog.Swallowed("instance.junction", ex); }
            }
        }
        else
        {
            if (JunctionService.IsJunction(instMcp)) JunctionService.RemoveJunction(instMcp);
        }
    }

    private void SetupProjectMemoryMount(InstanceProfile inst, List<ProjectProfile> boundProjects, string instGemini, string instAntigravity, string instUserData)
    {
        // Project-less chats live outside any project and are mounted separately.
        MountLooseChats(instAntigravity);

        var instBrain = Path.Combine(instAntigravity, "brain");
        var instConvos = Path.Combine(instAntigravity, "conversations");
        var instSummaryDb = Path.Combine(instAntigravity, "conversation_summaries.db");

        if (boundProjects.Count == 0)
        {
            if (JunctionService.IsJunction(instBrain)) JunctionService.RemoveJunction(instBrain);
            if (JunctionService.IsJunction(instConvos)) JunctionService.RemoveJunction(instConvos);
            Directory.CreateDirectory(instBrain);
            Directory.CreateDirectory(instConvos);
            SetupProjectMetadata(boundProjects, instGemini, instUserData);
            return;
        }

        if (boundProjects.Count == 1)
        {
            var proj = boundProjects[0];
            var projBrain = _projectService.GetProjectBrainDir(proj.Id);
            var projConvos = _projectService.GetProjectConversationsDir(proj.Id);
            var projSummaryDb = _projectService.GetProjectSummaryDbPath(proj.Id);

            Directory.CreateDirectory(projBrain);
            Directory.CreateDirectory(projConvos);

            JunctionService.CreateJunction(instBrain, projBrain);
            JunctionService.CreateJunction(instConvos, projConvos);

            if (File.Exists(projSummaryDb))
            {
                JunctionService.CreateFileHardLink(instSummaryDb, projSummaryDb);
                var shm = projSummaryDb + "-shm";
                var wal = projSummaryDb + "-wal";
                if (File.Exists(shm)) JunctionService.CreateFileHardLink(instSummaryDb + "-shm", shm);
                if (File.Exists(wal)) JunctionService.CreateFileHardLink(instSummaryDb + "-wal", wal);
            }

            // Single-project binding: keep project_id and metadata aligned as well.
            AlignSummaryProjectIds(instSummaryDb, boundProjects);
            if (File.Exists(projSummaryDb))
            {
                AlignSummaryProjectIds(projSummaryDb, boundProjects);
            }

            SetupProjectMetadata(boundProjects, instGemini, instUserData);
        }
        else
        {
            // Multi-project binding: merge each project's brain and conversations into the instance.
            if (JunctionService.IsJunction(instBrain)) JunctionService.RemoveJunction(instBrain);
            if (JunctionService.IsJunction(instConvos)) JunctionService.RemoveJunction(instConvos);
            Directory.CreateDirectory(instBrain);
            Directory.CreateDirectory(instConvos);

            InitInstanceSummaryDb(instSummaryDb);

            foreach (var proj in boundProjects)
            {
                var pBrain = _projectService.GetProjectBrainDir(proj.Id);
                var pConvos = _projectService.GetProjectConversationsDir(proj.Id);
                var pDb = _projectService.GetProjectSummaryDbPath(proj.Id);

                if (Directory.Exists(pBrain))
                {
                    foreach (var cDir in Directory.GetDirectories(pBrain))
                    {
                        var cName = Path.GetFileName(cDir);
                        var target = Path.Combine(instBrain, cName);
                        JunctionService.CreateJunction(target, cDir);
                    }
                }

                if (Directory.Exists(pConvos))
                {
                    foreach (var f in Directory.GetFiles(pConvos, "*.db"))
                    {
                        var fName = Path.GetFileName(f);
                        var target = Path.Combine(instConvos, fName);

                        // A stale -wal from an earlier run would be paired with the freshly linked
                        // database and make SQLite read pages that no longer belong to it.
                        DeleteSqliteSiblings(target);
                        JunctionService.CreateFileHardLink(target, f);
                    }
                }

                if (File.Exists(pDb))
                {
                    MergeSummariesDb(pDb, instSummaryDb);
                }
            }

            AlignSummaryProjectIds(instSummaryDb, boundProjects);
            SetupProjectMetadata(boundProjects, instGemini, instUserData);
        }
    }

    /// <summary>
    /// Links the instance's project-less chat store to the host copy.
    /// The three paths are independent, so each is handled separately: a missing host entry
    /// leaves the instance's own directory in place rather than breaking the whole mount.
    /// </summary>
    private static void MountLooseChats(string instAntigravity)
    {
        try
        {
            var hostDir = ProjectService.GetLooseChatsDir();
            if (!Directory.Exists(hostDir)) return;

            var hostConvos = Path.Combine(hostDir, "conversations");
            var hostBrain = Path.Combine(hostDir, "brain");
            var hostDb = Path.Combine(hostDir, "conversation_summaries.db");

            var instConvos = Path.Combine(instAntigravity, "conversations");
            var instBrain = Path.Combine(instAntigravity, "brain");
            var instDb = Path.Combine(instAntigravity, "conversation_summaries.db");

            // Directories: replace an existing plain directory with a junction. A directory
            // that already holds data is left alone so nothing is silently discarded.
            MountDir(instConvos, hostConvos);
            MountDir(instBrain, hostBrain);

            // The summary database is written by both sides, so it stays a local copy and is
            // refreshed from the host at launch instead of being linked.
            if (File.Exists(hostDb) && !File.Exists(instDb))
            {
                try { File.Copy(hostDb, instDb, overwrite: false); } catch { }
            }
        }
        catch
        {
            // Mounting is best-effort; a failure leaves the instance with its local store.
        }
    }

    /// <summary>Junctions <paramref name="linkPath"/> to <paramref name="targetPath"/> when safe.</summary>
    private static void MountDir(string linkPath, string targetPath)
    {
        if (!Directory.Exists(targetPath)) return;

        if (JunctionService.IsJunction(linkPath))
        {
            return;
        }

        if (Directory.Exists(linkPath))
        {
            // Keep directories that already contain files.
            try
            {
                if (Directory.EnumerateFileSystemEntries(linkPath).Any()) return;
            }
            catch { return; }

            try { Directory.Delete(linkPath, recursive: true); } catch { return; }
        }

        JunctionService.CreateJunction(linkPath, targetPath);
    }

    private static void InitInstanceSummaryDb(string destDb)
    {
        if (File.Exists(destDb))
        {
            return;
        }

        var hostGemini = ProjectService.GetRealHostGeminiDir();
        var stockDb = Path.Combine(hostGemini, "antigravity", "conversation_summaries.db");
        if (File.Exists(stockDb))
        {
            try
            {
                if (!ProjectService.CopySqliteSnapshot(stockDb, destDb))
                {
                    return;
                }

                using var conn = new SqliteConnection($"Data Source={destDb}");
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM conversation_summaries; DELETE FROM conversations_fts;";
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                }

                using var vacuum = conn.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }
            catch (Exception ex) { DiagLog.Swallowed("instance.vacuum", ex); }
        }
    }

    private static void AlignSummaryProjectIds(string destDb, List<ProjectProfile> boundProjects)
    {
        if (!File.Exists(destDb) || boundProjects.Count == 0) return;
        try
        {
            using var conn = new SqliteConnection($"Data Source={destDb}");
            conn.Open();
            foreach (var proj in boundProjects)
            {
                if (string.IsNullOrEmpty(proj.Path)) continue;
                var normPath = proj.Path.Replace("\\", "/").ToLowerInvariant();
                var encodedPath = normPath.Replace(":", "%3a");
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE conversation_summaries 
                    SET project_id = @pid 
                    WHERE (LOWER(workspace_uris) LIKE @pat1 OR LOWER(workspace_uris) LIKE @pat2) 
                      AND project_id != @pid";
                cmd.Parameters.AddWithValue("@pid", proj.Id);
                cmd.Parameters.AddWithValue("@pat1", $"%{normPath}%");
                cmd.Parameters.AddWithValue("@pat2", $"%{encodedPath}%");
                cmd.ExecuteNonQuery();
            }
        }
        catch { }
    }

    /// <summary>Reads a column, mapping NULL to DBNull.Value.</summary>
    private static object GetDbValue(SqliteDataReader reader, int index)
    {
        return reader.IsDBNull(index) ? DBNull.Value : reader.GetValue(index);
    }

    private static void MergeSummariesDb(string srcDb, string destDb)
    {
        try
        {
            using var srcConn = new SqliteConnection($"Data Source={srcDb};Mode=ReadOnly");
            srcConn.Open();
            using var destConn = new SqliteConnection($"Data Source={destDb}");
            destConn.Open();
            using var tx = destConn.BeginTransaction();

            using var cmd = srcConn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, title, preview, step_count, last_modified_time, workspace_uris, status, source, project_id, agent_name, last_user_input_time FROM conversation_summaries";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                using var insertCmd = destConn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"INSERT OR REPLACE INTO conversation_summaries 
                    (conversation_id, title, preview, step_count, last_modified_time, workspace_uris, status, source, project_id, agent_name, last_user_input_time)
                    VALUES (@cid, @title, @prev, @step, @time, @uris, @status, @source, @proj, @agent, @inputTime)";
                insertCmd.Parameters.AddWithValue("@cid", GetDbValue(reader, 0));
                insertCmd.Parameters.AddWithValue("@title", GetDbValue(reader, 1));
                insertCmd.Parameters.AddWithValue("@prev", GetDbValue(reader, 2));
                insertCmd.Parameters.AddWithValue("@step", GetDbValue(reader, 3));
                insertCmd.Parameters.AddWithValue("@time", GetDbValue(reader, 4));
                insertCmd.Parameters.AddWithValue("@uris", GetDbValue(reader, 5));
                insertCmd.Parameters.AddWithValue("@status", GetDbValue(reader, 6));
                insertCmd.Parameters.AddWithValue("@source", GetDbValue(reader, 7));
                insertCmd.Parameters.AddWithValue("@proj", GetDbValue(reader, 8));
                insertCmd.Parameters.AddWithValue("@agent", GetDbValue(reader, 9));
                insertCmd.Parameters.AddWithValue("@inputTime", GetDbValue(reader, 10));
                insertCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex) { DiagLog.Swallowed("instance.merge-summaries", ex); }
    }

    private void SyncMultiProjectOutput(InstanceProfile inst, List<ProjectProfile> boundProjects, string instGeminiDir)
    {
        var instAntigravity = Path.Combine(instGeminiDir, "antigravity");
        var instBrain = Path.Combine(instAntigravity, "brain");
        var instConvos = Path.Combine(instAntigravity, "conversations");
        var instSummaryDb = Path.Combine(instAntigravity, "conversation_summaries.db");

        if (!File.Exists(instSummaryDb)) return;

        try
        {
            using var conn = new SqliteConnection($"Data Source={instSummaryDb};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, workspace_uris, project_id FROM conversation_summaries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cid = reader.GetString(0);
                var urisJson = reader.GetString(1);
                var pId = reader.IsDBNull(2) ? null : reader.GetString(2);

                // Project-less chats belong to no workspace; copying them into one would relocate
                // their brain and make them appear under an unrelated project.
                if (ProjectService.IsProjectlessConversation(pId, urisJson)) continue;

                var matchedProj = boundProjects.FirstOrDefault(p => !string.IsNullOrEmpty(p.Path) && urisJson.Contains(p.Path.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase));
                if (matchedProj != null)
                {
                    var targetBrain = Path.Combine(_projectService.GetProjectBrainDir(matchedProj.Id), cid);
                    var srcBrain = Path.Combine(instBrain, cid);
                    if (Directory.Exists(srcBrain) && !Directory.Exists(targetBrain) && !JunctionService.IsJunction(srcBrain))
                    {
                        try { CopyDirectory(srcBrain, targetBrain); } catch { }
                    }

                    var targetConvo = Path.Combine(_projectService.GetProjectConversationsDir(matchedProj.Id), $"{cid}.db");
                    var srcConvo = Path.Combine(instConvos, $"{cid}.db");
                    if (File.Exists(srcConvo) && !File.Exists(targetConvo))
                    {
                        try { File.Copy(srcConvo, targetConvo, true); } catch { }
                    }

                    // Mirror the summary row.
                    var projDb = _projectService.GetProjectSummaryDbPath(matchedProj.Id);
                    if (File.Exists(projDb))
                    {
                        MergeSingleSummary(instSummaryDb, projDb, cid);
                    }
                }
            }
        }
        catch { }
    }

    private static void MergeSingleSummary(string srcDb, string destDb, string convoId)
    {
        try
        {
            using var srcConn = new SqliteConnection($"Data Source={srcDb};Mode=ReadOnly");
            srcConn.Open();
            using var destConn = new SqliteConnection($"Data Source={destDb}");
            destConn.Open();

            using var cmd = srcConn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, title, preview, step_count, last_modified_time, workspace_uris, status, source, project_id, agent_name, last_user_input_time FROM conversation_summaries WHERE conversation_id = @cid";
            cmd.Parameters.AddWithValue("@cid", convoId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                using var insertCmd = destConn.CreateCommand();
                insertCmd.CommandText = @"INSERT OR REPLACE INTO conversation_summaries 
                    (conversation_id, title, preview, step_count, last_modified_time, workspace_uris, status, source, project_id, agent_name, last_user_input_time)
                    VALUES (@cid, @title, @prev, @step, @time, @uris, @status, @source, @proj, @agent, @inputTime)";
                insertCmd.Parameters.AddWithValue("@cid", GetDbValue(reader, 0));
                insertCmd.Parameters.AddWithValue("@title", GetDbValue(reader, 1));
                insertCmd.Parameters.AddWithValue("@prev", GetDbValue(reader, 2));
                insertCmd.Parameters.AddWithValue("@step", GetDbValue(reader, 3));
                insertCmd.Parameters.AddWithValue("@time", GetDbValue(reader, 4));
                insertCmd.Parameters.AddWithValue("@uris", GetDbValue(reader, 5));
                insertCmd.Parameters.AddWithValue("@status", GetDbValue(reader, 6));
                insertCmd.Parameters.AddWithValue("@source", GetDbValue(reader, 7));
                insertCmd.Parameters.AddWithValue("@proj", GetDbValue(reader, 8));
                insertCmd.Parameters.AddWithValue("@agent", GetDbValue(reader, 9));
                insertCmd.Parameters.AddWithValue("@inputTime", GetDbValue(reader, 10));
                insertCmd.ExecuteNonQuery();
            }
        }
        catch { }
    }

    /// <summary>
    /// SQLite keeps committed-but-unmerged pages in companion files next to the database.
    /// Moving the .db alone would silently drop them, so the whole family travels together.
    /// </summary>
    private static readonly string[] SqliteSiblingSuffixes = ["-wal", "-shm"];

    /// <summary>Header every SQLite 3 database starts with.</summary>
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// True when the path holds a database that is safe to keep and to link against.
    /// File.Exists alone is not enough: a process killed before checkpointing leaves a
    /// zero-length file that still reports as present, and treating it as real is what
    /// destroys the only good copy.
    /// </summary>
    private static bool IsUsableSqlite(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            // A valid database needs at least one page plus the header.
            if (info.Length <= 512) return false;

            using var stream = File.OpenRead(path);
            var header = new byte[SqliteHeader.Length];
            var read = stream.Read(header, 0, header.Length);
            if (read != SqliteHeader.Length) return false;

            return header.AsSpan().SequenceEqual(SqliteHeader);
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("instance.sqlite-probe", ex);
            return false;
        }
    }

    private static void MoveSqliteWithSiblings(string sourceDb, string destDb)
    {
        // Overwrite semantics: the caller already decided this copy should become the shared one,
        // and leaving a stale target in place would mix rows from two different databases.
        if (File.Exists(destDb))
        {
            try { File.Delete(destDb); } catch { }
        }

        // Clear every destination companion first. A leftover -shm or -wal from the previous
        // occupant would otherwise be paired with the incoming database.
        DeleteSqliteSiblings(destDb);

        File.Move(sourceDb, destDb);

        foreach (var suffix in SqliteSiblingSuffixes)
        {
            var from = sourceDb + suffix;
            if (!File.Exists(from)) continue;

            try
            {
                File.Move(from, destDb + suffix);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("instance.sqlite-sibling-move", ex);
            }
        }
    }

    private static void DeleteSqliteSiblings(string dbPath)
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
                DiagLog.Swallowed("instance.sqlite-sibling-delete", ex);
            }
        }
    }

    public void ReconcileAndHarvestProjects(InstanceProfile inst)
    {
        var instGemini = Path.Combine(_instancesStorageDir, inst.Id, ".gemini");
        if (!Directory.Exists(instGemini)) return;

        var instAntigravity = Path.Combine(instGemini, "antigravity");
        var instBrain = Path.Combine(instAntigravity, "brain");
        var instConvos = Path.Combine(instAntigravity, "conversations");
        var instSummaryDb = Path.Combine(instAntigravity, "conversation_summaries.db");
        var instConfigProjects = Path.Combine(instGemini, "config", "projects");

        bool instancesChanged = false;
        bool projectsChanged = false;

        // Scan config/projects/*.json for newly created workspaces.
        if (Directory.Exists(instConfigProjects))
        {
            foreach (var projFile in Directory.EnumerateFiles(instConfigProjects, "*.json"))
            {
                try
                {
                    var fileGuid = Path.GetFileNameWithoutExtension(projFile);
                    var text = File.ReadAllText(projFile);
                    var node = JsonNode.Parse(text);
                    if (node == null) continue;

                    var projId = node["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(projId)) projId = fileGuid;

                    // The official id becomes a directory name, so sanitize it first.
                    if (!ProjectService.IsSafeIdSegment(projId)) projId = Guid.NewGuid().ToString("D");

                    var projName = node["name"]?.ToString();
                    // Skip Sidecar virtual workspaces (e.g. android-cli-plugin/compose-preview).
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
                        var localPath = ProjectService.NormalizeWorkspaceUri(folderUri);
                        if (!string.IsNullOrEmpty(localPath))
                        {
                            if (string.IsNullOrWhiteSpace(projName))
                            {
                                projName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            }
                            if (string.IsNullOrWhiteSpace(projName)) projName = "Project";

                            var existingProj = _projectService.Projects.FirstOrDefault(p => 
                                p.Id.Equals(projId, StringComparison.OrdinalIgnoreCase) ||
                                p.Id.Equals(fileGuid, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(p.Path) && p.Path.Equals(localPath, StringComparison.OrdinalIgnoreCase)));

                            if (existingProj == null)
                            {
                                existingProj = new ProjectProfile
                                {
                                    Id = projId,
                                    Name = projName,
                                    Path = localPath,
                                    CreatedAt = DateTime.UtcNow,
                                    LastActiveAt = DateTime.UtcNow
                                };
                                _projectService.RegisterProject(existingProj);
                                projectsChanged = true;
                            }
                            else if (!existingProj.Id.Equals(projId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(projId))
                            {
                                // Align to the canonical GUID generated by the official app.
                                existingProj.Id = projId;
                                projectsChanged = true;
                            }

                            if (!inst.BoundProjectIds.Contains(existingProj.Id))
                            {
                                inst.BoundProjectIds.Add(existingProj.Id);
                                instancesChanged = true;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // Move every real (non-junction) memory entry to shared storage and replace it with a junction.
        if (Directory.Exists(instBrain) && !JunctionService.IsJunction(instBrain))
        {
            Directory.CreateDirectory(instConvos);

            foreach (var subDir in Directory.GetDirectories(instBrain))
            {
                var cid = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(cid) || cid.Equals("tempmediaStorage", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Already a junction: nothing to migrate.
                if (JunctionService.IsJunction(subDir))
                    continue;

                ProjectProfile? matchedProj = null;
                var isProjectless = false;

                // A. Resolve project_id / workspace_uris from conversation_summaries.db.
                if (File.Exists(instSummaryDb))
                {
                    try
                    {
                        using var conn = new SqliteConnection($"Data Source={instSummaryDb};Mode=ReadOnly");
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT project_id, workspace_uris FROM conversation_summaries WHERE conversation_id=@cid";
                        cmd.Parameters.AddWithValue("@cid", cid);
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            var pId = reader.IsDBNull(0) ? null : reader.GetString(0);
                            var urisJson = reader.IsDBNull(1) ? null : reader.GetString(1);

                            // A chat started outside any workspace is not a mismatched project:
                            // adopting it into one would move its brain into that project and make
                            // the conversation appear under an unrelated name.
                            isProjectless = ProjectService.IsProjectlessConversation(pId, urisJson);

                            if (!isProjectless && !string.IsNullOrEmpty(pId))
                            {
                                matchedProj = _projectService.Projects.FirstOrDefault(p => p.Id.Equals(pId, StringComparison.OrdinalIgnoreCase));
                            }

                            if (!isProjectless && matchedProj == null && !string.IsNullOrEmpty(urisJson))
                            {
                                foreach (var p in _projectService.Projects)
                                {
                                    if (!string.IsNullOrEmpty(p.Path) && urisJson.Contains(p.Path.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase))
                                    {
                                        matchedProj = p;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Project-less chats keep their own store; leave them where Antigravity put them.
                if (isProjectless)
                {
                    continue;
                }

                // B. Fall back to the instance's first bound project.
                if (matchedProj == null)
                {
                    var firstId = inst.BoundProjectIds.FirstOrDefault();
                    if (firstId != null)
                    {
                        matchedProj = _projectService.Projects.FirstOrDefault(p => p.Id == firstId);
                    }
                }

                // C. Still unmatched: bind an existing/default workspace or create one.
                if (matchedProj == null)
                {
                    // Only the instance's own workspace is a safe default. Picking an arbitrary
                    // project here is what made unrelated chats show up under the wrong name.
                    matchedProj = _projectService.Projects.FirstOrDefault(p => p.Name == $"{inst.Name}-工作区");

                    if (matchedProj == null)
                    {
                        matchedProj = new ProjectProfile
                        {
                            Id = Guid.NewGuid().ToString("D"),
                            Name = $"{inst.Name}-工作区",
                            Path = instGemini,
                            CreatedAt = DateTime.UtcNow
                        };
                        _projectService.RegisterProject(matchedProj);
                        projectsChanged = true;
                    }
                }

                if (!inst.BoundProjectIds.Contains(matchedProj.Id))
                {
                    inst.BoundProjectIds.Add(matchedProj.Id);
                    instancesChanged = true;
                }

                // Move brain/{cid} into shared storage.
                var targetPubBrain = Path.Combine(_projectService.GetProjectBrainDir(matchedProj.Id), cid);
                try
                {
                    if (Directory.Exists(targetPubBrain))
                    {
                        CopyDirectory(subDir, targetPubBrain);
                        Directory.Delete(subDir, true);
                    }
                    else
                    {
                        var parent = Path.GetDirectoryName(targetPubBrain);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        Directory.Move(subDir, targetPubBrain);
                    }

                    // Recreate the junction link.
                    JunctionService.CreateJunction(subDir, targetPubBrain);
                }
                catch (Exception ex) { DiagLog.Swallowed("instance.brain-junction", ex); }

                // Move conversations/{cid}.db and hard-link it back.
                var srcConvoDb = Path.Combine(instConvos, $"{cid}.db");
                var targetPubConvoDb = Path.Combine(_projectService.GetProjectConversationsDir(matchedProj.Id), $"{cid}.db");
                if (File.Exists(srcConvoDb))
                {
                    try
                    {
                        var parent = Path.GetDirectoryName(targetPubConvoDb);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                        if (!IsUsableSqlite(targetPubConvoDb))
                        {
                            // Target is absent or an empty shell left by an uncheckpointed WAL.
                            // The instance copy is the only real data, so it becomes the shared one.
                            MoveSqliteWithSiblings(srcConvoDb, targetPubConvoDb);
                        }
                        else
                        {
                            // Proven good copy exists; the instance-side duplicate can go.
                            File.Delete(srcConvoDb);
                            DeleteSqliteSiblings(srcConvoDb);
                        }

                        JunctionService.CreateFileHardLink(srcConvoDb, targetPubConvoDb);
                    }
                    catch (Exception ex) { DiagLog.Swallowed("instance.convo-link", ex); }
                }

                // Merge the summary rows into the shared project DB.
                if (File.Exists(instSummaryDb))
                {
                    var pubDb = _projectService.GetProjectSummaryDbPath(matchedProj.Id);
                    MergeSingleSummary(instSummaryDb, pubDb, cid);
                }

                _projectService.RefreshProjectMemoryStats(matchedProj);
                projectsChanged = true;
            }
        }

        if (projectsChanged)
        {
            _projectService.SaveProjects();
        }
        if (instancesChanged)
        {
            SaveInstances();
        }
    }

    private void SetupProjectMetadata(List<ProjectProfile> projects, string instGemini, string instUserData)
    {
        try
        {
            var configProjectsDir = Path.Combine(instGemini, "config", "projects");
            Directory.CreateDirectory(configProjectsDir);

            var boundProjectGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keep sidecar project entries (compose-preview, device-hub, ...).
            var configJsonPath = Path.Combine(instGemini, "config", "config.json");
            if (File.Exists(configJsonPath))
            {
                try
                {
                    var cfgNode = JsonNode.Parse(File.ReadAllText(configJsonPath));
                    if (cfgNode?["sidecars"] is JsonObject sidecarsObj)
                    {
                        foreach (var kvp in sidecarsObj)
                        {
                            var sidecarProjId = kvp.Value?["projectId"]?.ToString();
                            if (!string.IsNullOrEmpty(sidecarProjId))
                            {
                                boundProjectGuids.Add(sidecarProjId);
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var p in projects)
            {
                if (string.IsNullOrEmpty(p.Path)) continue;

                boundProjectGuids.Add(p.Id);
                var projJsonPath = Path.Combine(configProjectsDir, $"{p.Id}.json");

                var folderUri = ProjectService.ToWorkspaceUri(p.Path);
                var gitDir = Path.Combine(p.Path, ".git");
                bool hasGit = Directory.Exists(gitDir);

                JsonObject projDoc;
                if (File.Exists(projJsonPath))
                {
                    try
                    {
                        projDoc = JsonNode.Parse(File.ReadAllText(projJsonPath)) as JsonObject ?? new JsonObject();
                    }
                    catch
                    {
                        projDoc = new JsonObject();
                    }
                }
                else
                {
                    projDoc = new JsonObject();
                }

                // Root fields follow the exa.project_pb.Project proto message.
                projDoc["id"] = p.Id;
                projDoc["name"] = p.Name;
                projDoc["isWorkspaceOnly"] = false;
                if (projDoc["settings"] == null)
                {
                    projDoc["settings"] = new JsonObject();
                }
                if (projDoc["permissionGrants"] == null)
                {
                    projDoc["permissionGrants"] = new JsonObject
                    {
                        ["v2Migrated"] = true
                    };
                }

                // folderUri is not a root field in the proto; language_server.exe rejects it there.
                projDoc.Remove("folderUri");

                var resourcesArr = new JsonArray();
                var resItem = new JsonObject();
                if (hasGit)
                {
                    var gitFolderObj = new JsonObject
                    {
                        ["defaultBranch"] = "main",
                        ["folderUri"] = folderUri
                    };
                    resItem["gitFolder"] = gitFolderObj;
                }
                else
                {
                    resItem["folderUri"] = folderUri;
                }
                resourcesArr.Add(resItem);

                var projectResources = new JsonObject
                {
                    ["resources"] = resourcesArr
                };
                projDoc["projectResources"] = projectResources;

                // Write to the canonical shared path, then hard-link the instance copy to it.
                // One project therefore has one definition; every instance sees the same file
                // and Antigravity writes made inside an instance land in the shared copy.
                var sharedJsonPath = _projectService.GetProjectConfigJsonPath(p.Id);
                try
                {
                    var json = projDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    Directory.CreateDirectory(Path.GetDirectoryName(sharedJsonPath)!);

                    // Refresh the shared copy only when it differs, so the link is not broken
                    // on every launch just to rewrite identical content.
                    if (!File.Exists(sharedJsonPath) || File.ReadAllText(sharedJsonPath) != json)
                    {
                        File.WriteAllText(sharedJsonPath, json);
                    }

                    if (!JunctionService.EnsureFileHardLink(projJsonPath, sharedJsonPath))
                    {
                        // Link refused (different volume, unsupported file system): keep a plain copy.
                        File.WriteAllText(projJsonPath, json);
                    }
                }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("instance.project-meta-link", ex);
                    try
                    {
                        File.WriteAllText(projJsonPath,
                            projDoc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                }
            }

            // Drop project JSON files that are no longer bound (sidecar entries excluded).
            // Deleting a hard link removes only this directory entry; the shared copy survives.
            foreach (var file in Directory.EnumerateFiles(configProjectsDir, "*.json"))
            {
                var guid = Path.GetFileNameWithoutExtension(file);
                if (!boundProjectGuids.Contains(guid))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            // UserData/app_storage.json holds the workspace order and last selection.
            if (!string.IsNullOrEmpty(instUserData))
            {
                Directory.CreateDirectory(instUserData);
                var storagePath = Path.Combine(instUserData, "app_storage.json");
                var storageObj = File.Exists(storagePath)
                    ? (JsonNode.Parse(File.ReadAllText(storagePath)) as JsonObject ?? new JsonObject())
                    : new JsonObject();

                var orderList = projects.Select(p => p.Id).ToList();
                storageObj["projectsOrder"] = JsonSerializer.Serialize(orderList);

                if (projects.Count > 0)
                {
                    storageObj["new-convo-last-selected-project"] = projects[0].Id;
                    storageObj["lastCreatedProjectId"] = projects[0].Id;
                }

                File.WriteAllText(storagePath, storageObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            // Maintain trustedFolders.json and projects.json.
            var trustedPath = Path.Combine(instGemini, "trustedFolders.json");
            var trustedObj = File.Exists(trustedPath)
                ? (JsonNode.Parse(File.ReadAllText(trustedPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            var projectsPath = Path.Combine(instGemini, "projects.json");
            var projRoot = File.Exists(projectsPath)
                ? (JsonNode.Parse(File.ReadAllText(projectsPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();
            var subObj = projRoot["projects"] as JsonObject ?? new JsonObject();

            foreach (var p in projects)
            {
                if (!string.IsNullOrEmpty(p.Path))
                {
                    trustedObj[p.Path.Replace("\\", "/").ToLowerInvariant()] = "TRUST_PARENT";
                    subObj[p.Path.ToLowerInvariant()] = p.Name.ToLowerInvariant().Replace(" ", "-");
                }
            }

            File.WriteAllText(trustedPath, trustedObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            projRoot["projects"] = subObj;
            File.WriteAllText(projectsPath, projRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
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
