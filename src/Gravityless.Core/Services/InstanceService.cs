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
    /// Tolerance when matching a process start time against the recorded one. The record is
    /// written immediately after the process starts, so the two differ by milliseconds; anything
    /// beyond this is a different process wearing a recycled PID.
    /// </summary>
    private const int ProcessIdentityToleranceSeconds = 10;

    /// <summary>
    /// True when <paramref name="processId"/> still refers to the process recorded at
    /// <paramref name="startedAt"/>.
    ///
    /// A PID is not an identity: Windows recycles numbers, so once a recorded process exits its
    /// number can be handed to an unrelated one — including another instance's Antigravity.
    /// Treating that as "still running" reports a phantom conflict and, on force restart,
    /// terminates the wrong instance. The recorded start time pins the identity.
    /// </summary>
    private static bool IsRecordedProcessAlive(int? processId, DateTime? startedAt)
    {
        if (processId is not { } pid || pid <= 0) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;

            if (startedAt is { } recorded)
            {
                var started = proc.StartTime.ToUniversalTime();
                return Math.Abs((started - recorded).TotalSeconds) <= ProcessIdentityToleranceSeconds;
            }

            // Records written before the timestamp existed: the executable name at least rejects
            // a recycled PID that landed on an unrelated program.
            return proc.ProcessName.Equals("Antigravity", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Gone, or not ours to inspect. Never claim a process we cannot identify.
            return false;
        }
    }

    /// <summary>
    /// Kills the recorded process only when it is still the one we started. Killing a recycled
    /// PID would take down whatever inherited the number.
    /// </summary>
    private static void TerminateRecordedProcess(int? processId, DateTime? startedAt)
    {
        if (!IsRecordedProcessAlive(processId, startedAt)) return;

        try
        {
            using var proc = Process.GetProcessById(processId!.Value);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
        }
        catch
        {
            // Already gone or protected; the caller clears the record either way.
        }
    }

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
        if (Directory.Exists(instDir) || JunctionService.IsJunction(instDir))
        {
            try
            {
                JunctionService.DeleteDirectorySafe(instDir);
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
            var isProcessAlive = IsRecordedProcessAlive(inst.ProcessId, inst.StartedAt);

            if (!isProcessAlive)
            {
                // Either it exited or the number now belongs to something else. Clearing the
                // record is safe: a process we cannot identify is not one we may manage.
                inst.IsRunning = false;
                inst.ProcessId = null;
                inst.StartedAt = null;
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
                TerminateRecordedProcess(inst.ProcessId, inst.StartedAt);

                inst.IsRunning = false;
                inst.ProcessId = null;
                inst.StartedAt = null;
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
                // Same PID-reuse guard as the instance check: a recycled number must not make a
                // free project look busy, nor let a force restart kill an unrelated process.
                var isProjProcAlive = IsRecordedProcessAlive(proj.ProcessId, proj.StartedAt);

                if (!isProjProcAlive)
                {
                    proj.IsRunning = false;
                    proj.RunningInstanceId = null;
                    proj.ProcessId = null;
                    proj.StartedAt = null;
                    _projectService.SaveProjects();
                }
                else if (proj.RunningInstanceId != inst.Id)
                {
                    // A project belongs to the instance that opened it. Taking it over by killing
                    // that process would let a second launch silently terminate a first one, so the
                    // conflict is reported regardless of `force`: the caller's remedy is to exclude
                    // the project for this launch, not to stop the holder.
                    _accountPool.CheckInAccount(inst.BoundAccountId, Path.Combine(_instancesStorageDir, inst.Id, ".gemini"));
                    conflictDetected = true;
                    conflictingPid = proj.ProcessId;
                    errorMessage = $"目标项目【{proj.Name}】当前已被另一实例占用运行中 (PID: {proj.ProcessId})！为防止 SQLite 记忆库损坏，严禁并发多写。";
                    return false;
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

        // Sync shared skills / plugins / MCP as real directories (Windows RedirectionTrust safe).
        SetupStorageMatrix(inst, instAntigravity, instConfig);

        // Materialize bound project memory stores (one or many) as real directories without junctions.
        SetupProjectMemoryMount(inst, boundProjects, instGemini, instAntigravity, instUserData);

        // Inject credentials. The slot is global, so take the instance lock (TTL-bounded) so two
        // launches cannot interleave writes. The blob is rendered from the profile, whose identity
        // fields are immutable, so it cannot describe any account but this one.
        if (!string.IsNullOrEmpty(account.RefreshToken))
        {
            var acquired = CredentialSlotLock.Wait(CredentialSlotTtl);
            if (!acquired)
            {
                errorMessage = "凭证槽位被其他实例占用超过 10 秒，请稍后重试";
                return false;
            }

            try
            {
                // A rejected blob means the rendered credential is unusable; the launch must fail
                // rather than run with whatever account the slot currently holds.
                if (!CredentialManager.WriteCredential(CredentialCodec.RenderBlob(account)))
                {
                    errorMessage = "账号凭证格式无效，已取消启动（请重新捕获该账号）";
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
                || !PatchService.IsPatchCurrent(managedExe);

            if (needsMicaPatch && (File.Exists(asar) || File.Exists(asarStock) || Directory.Exists(appDist)))
            {
                PatchService.ApplyPatch(managedExe, _daemon.MicaMaterial, _daemon.MicaOpacity, out _);
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

        ProxyService.ApplyProxyToProcess(psi, proxy);


        try
        {
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("启动 Antigravity 实例失败");
            inst.IsRunning = true;
            inst.ProcessId = proc.Id;
            inst.StartedAt = DateTime.UtcNow;
            _runningProcesses[inst.Id] = proc;

            // Record that the leased access token is actively in use by this launched engine.
            account.AccessTokenLastUsedAt = DateTime.UtcNow;
            _accountPool.TrySaveAccounts();

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

            // The slot is deliberately left populated until this instance exits.
            //
            // 2.29 removed the per-instance credential file, so the slot is the only channel the
            // language server can read, and it reads it during its own startup — which happens
            // seconds after Process.Start returns, with no way to observe when. A timed clear
            // raced that read and produced an unauthenticated window. Overwriting on the next
            // launch is enough for concurrency: each language server keeps its token in memory
            // afterwards and refreshes independently.
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
            // The handle came from Process.Start, so it still refers to our process even if the
            // PID has since been reused.
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(true);
                }
            }
            catch { }
            return;
        }

        // No handle in this session — the app was restarted while the instance kept running.
        // Fall back to the recorded PID, which is only acted on when the identity still matches.
        TerminateRecordedProcess(inst.ProcessId, inst.StartedAt);
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

                    // The instance is gone, so its token no longer needs to sit in the slot.
                    // This only removes a credential that still belongs to this account; when a
                    // second instance has since overwritten it, that account keeps its slot.
                    var account = _accountPool.Accounts.FirstOrDefault(a => a.Id == inst.BoundAccountId);
                    if (account != null && CredentialManager.ClearSlotIfOwnedBy(account.Email))
                    {
                        inst.CredentialSlotReleased = true;
                        CredentialSlotReleased?.Invoke(inst);
                    }
                }
                finally
                {
                    CredentialSlotLock.Release();
                }
            }
        }

        // Sync any in-instance additions to shared skills / plugins / MCP back to the shared pool.
        SyncStorageMatrixOutput(inst, Path.Combine(instanceGeminiDir, "antigravity"), Path.Combine(instanceGeminiDir, "config"));

        // Write back conversations created or updated during the session (single or multi project).
        if (boundProjects.Count == 1)
        {
            SyncSingleProjectOutput(inst, boundProjects[0], instanceGeminiDir);
        }
        else if (boundProjects.Count > 1)
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

            root["agy.onboarding.lastLoginUsername"] = email;
            root["agy.onboarding.lastLoginIsGcpTos"] = "false";
            root["jetski.onboarding.lastLoginUsername"] = email;
            root["jetski.onboarding.lastLoginIsGcpTos"] = "false";
            root["ide-install-wizard-shown"] = "true";

            File.WriteAllText(storagePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private sealed class SharedSyncManifest
    {
        public List<string> Skills { get; set; } = [];
        public List<string> Plugins { get; set; } = [];
        public long McpConfigUtcTicks { get; set; }
    }

    private static string GetSharedManifestPath(string instConfig)
    {
        var instGemini = Path.GetDirectoryName(instConfig) ?? instConfig;
        return Path.Combine(instGemini, ".gravityless_shared_manifest.json");
    }

    private static SharedSyncManifest LoadSharedManifest(string manifestPath)
    {
        try
        {
            if (File.Exists(manifestPath))
            {
                var parsed = JsonSerializer.Deserialize<SharedSyncManifest>(File.ReadAllText(manifestPath));
                if (parsed != null) return parsed;
            }
        }
        catch { }
        return new SharedSyncManifest();
    }

    private static void SaveSharedManifest(string manifestPath, SharedSyncManifest manifest)
    {
        try
        {
            var dir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        }
        catch (Exception ex) { DiagLog.Swallowed("instance.shared-manifest-save", ex); }
    }

    private static List<string> SyncSharedToolCategoryToInstance(string sharedCategoryDir, IReadOnlyList<string> instCategoryDirs, IReadOnlyList<string> previouslySynced)
    {
        Directory.CreateDirectory(sharedCategoryDir);
        var currentShared = Directory.GetDirectories(sharedCategoryDir)
            .Where(d => !JunctionService.IsJunction(d))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deletedFromShared = previouslySynced
            .Where(n => !currentShared.Contains(n))
            .ToList();

        foreach (var instDir in instCategoryDirs)
        {
            JunctionService.UnwrapJunction(instDir);
            JunctionService.UnwrapAllJunctions(instDir);
            Directory.CreateDirectory(instDir);

            // Propagate explicit deletions from ToolService without blindly removing local custom items.
            foreach (var deletedName in deletedFromShared)
            {
                var staleDir = Path.Combine(instDir, deletedName);
                if (Directory.Exists(staleDir))
                {
                    try { JunctionService.DeleteDirectorySafe(staleDir); }
                    catch (Exception ex) { DiagLog.Swallowed("instance.shared-prune", ex); }
                }
            }

            SyncDirectory(sharedCategoryDir, instDir, preferNewer: true);
        }

        return currentShared.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SyncInstanceToolCategoryBackToShared(
        string sharedCategoryDir,
        IEnumerable<string> instCategoryDirs,
        IReadOnlyList<string> previouslySynced)
    {
        Directory.CreateDirectory(sharedCategoryDir);
        var prevSet = previouslySynced.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentShared = Directory.GetDirectories(sharedCategoryDir)
            .Where(d => !JunctionService.IsJunction(d))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var instDir in instCategoryDirs)
        {
            if (!Directory.Exists(instDir) || JunctionService.IsJunction(instDir))
                continue;

            foreach (var subDir in Directory.GetDirectories(instDir))
            {
                if (JunctionService.IsJunction(subDir)) continue;
                var name = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(name)) continue;

                // Never resurrect an item that was explicitly removed from the shared pool while running.
                if (prevSet.Contains(name) && !currentShared.Contains(name))
                {
                    try { JunctionService.DeleteDirectorySafe(subDir); }
                    catch (Exception ex) { DiagLog.Swallowed("instance.shared-prune-exit", ex); }
                    continue;
                }

                SyncDirectory(subDir, Path.Combine(sharedCategoryDir, name), preferNewer: true);
            }
        }

        return Directory.GetDirectories(sharedCategoryDir)
            .Where(d => !JunctionService.IsJunction(d))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SetupStorageMatrix(InstanceProfile inst, string instAntigravity, string instConfig)
    {
        var manifestPath = GetSharedManifestPath(instConfig);
        var manifest = LoadSharedManifest(manifestPath);

        var sharedSkills = Path.Combine(_sharedDir, "skills");
        var instAntigravitySkills = Path.Combine(instAntigravity, "skills");
        var instConfigSkills = Path.Combine(instConfig, "skills");

        if (inst.ShareSkills)
        {
            manifest.Skills = SyncSharedToolCategoryToInstance(sharedSkills, [instAntigravitySkills, instConfigSkills], manifest.Skills);
        }
        else
        {
            JunctionService.UnwrapJunction(instAntigravitySkills);
            JunctionService.UnwrapJunction(instConfigSkills);
        }

        var sharedPlugins = Path.Combine(_sharedDir, "plugins");
        var instPlugins = Path.Combine(instConfig, "plugins");
        if (inst.SharePlugins)
        {
            manifest.Plugins = SyncSharedToolCategoryToInstance(sharedPlugins, [instPlugins], manifest.Plugins);
        }
        else
        {
            JunctionService.UnwrapJunction(instPlugins);
        }

        var sharedMcp = Path.Combine(_sharedDir, "mcp");
        Directory.CreateDirectory(sharedMcp);
        var instMcp = Path.Combine(instAntigravity, "mcp");
        var sharedMcpJson = Path.Combine(sharedMcp, "mcp_config.json");
        var instConfigMcpJson = Path.Combine(instConfig, "mcp_config.json");

        if (inst.ShareMcp)
        {
            JunctionService.UnwrapJunction(instMcp);
            JunctionService.UnwrapAllJunctions(instMcp);
            Directory.CreateDirectory(instMcp);
            SyncDirectory(sharedMcp, instMcp, preferNewer: true);

            if (File.Exists(sharedMcpJson))
            {
                try
                {
                    Directory.CreateDirectory(instConfig);
                    var sharedWriteUtc = File.GetLastWriteTimeUtc(sharedMcpJson);
                    var copyToConfig = !File.Exists(instConfigMcpJson)
                        || sharedWriteUtc >= File.GetLastWriteTimeUtc(instConfigMcpJson)
                        || sharedWriteUtc.Ticks != manifest.McpConfigUtcTicks;
                    if (copyToConfig)
                    {
                        File.Copy(sharedMcpJson, instConfigMcpJson, true);
                    }
                    manifest.McpConfigUtcTicks = sharedWriteUtc.Ticks;
                }
                catch (Exception ex) { DiagLog.Swallowed("instance.shared-mcp-sync", ex); }
            }
        }
        else
        {
            JunctionService.UnwrapJunction(instMcp);
        }

        SaveSharedManifest(manifestPath, manifest);
    }

    private void SyncStorageMatrixOutput(InstanceProfile inst, string instAntigravity, string instConfig)
    {
        try
        {
            var manifestPath = GetSharedManifestPath(instConfig);
            var manifest = LoadSharedManifest(manifestPath);

            if (inst.ShareSkills)
            {
                var sharedSkills = Path.Combine(_sharedDir, "skills");
                manifest.Skills = SyncInstanceToolCategoryBackToShared(
                    sharedSkills,
                    [Path.Combine(instAntigravity, "skills"), Path.Combine(instConfig, "skills")],
                    manifest.Skills);
            }

            if (inst.SharePlugins)
            {
                var sharedPlugins = Path.Combine(_sharedDir, "plugins");
                manifest.Plugins = SyncInstanceToolCategoryBackToShared(
                    sharedPlugins,
                    [Path.Combine(instConfig, "plugins")],
                    manifest.Plugins);
            }

            if (inst.ShareMcp)
            {
                var sharedMcp = Path.Combine(_sharedDir, "mcp");
                var instMcp = Path.Combine(instAntigravity, "mcp");
                var sharedMcpJson = Path.Combine(sharedMcp, "mcp_config.json");
                var instConfigMcpJson = Path.Combine(instConfig, "mcp_config.json");
                var instMcpJson = Path.Combine(instMcp, "mcp_config.json");

                if (Directory.Exists(instMcp) && !JunctionService.IsJunction(instMcp))
                {
                    SyncDirectory(instMcp, sharedMcp, preferNewer: true);
                }

                // Prefer whichever mcp_config.json was modified most recently during the session.
                var sharedWrite = File.Exists(sharedMcpJson) ? File.GetLastWriteTimeUtc(sharedMcpJson) : DateTime.MinValue;
                string? newestInstMcpJson = null;
                var newestInstWrite = sharedWrite;
                foreach (var candidate in new[] { instConfigMcpJson, instMcpJson })
                {
                    if (!File.Exists(candidate)) continue;
                    var w = File.GetLastWriteTimeUtc(candidate);
                    if (w > newestInstWrite)
                    {
                        newestInstWrite = w;
                        newestInstMcpJson = candidate;
                    }
                }

                if (newestInstMcpJson != null)
                {
                    Directory.CreateDirectory(sharedMcp);
                    File.Copy(newestInstMcpJson, sharedMcpJson, true);
                    manifest.McpConfigUtcTicks = File.GetLastWriteTimeUtc(sharedMcpJson).Ticks;
                }
            }

            SaveSharedManifest(manifestPath, manifest);
        }
        catch (Exception ex) { DiagLog.Swallowed("instance.sync-shared-output", ex); }
    }

    private void SetupProjectMemoryMount(InstanceProfile inst, List<ProjectProfile> boundProjects, string instGemini, string instAntigravity, string instUserData)
    {
        var instBrain = Path.Combine(instAntigravity, "brain");
        var instConvos = Path.Combine(instAntigravity, "conversations");
        var instSummaryDb = Path.Combine(instAntigravity, "conversation_summaries.db");

        // Unwrap any legacy NTFS junctions without deleting target files so Antigravity sees real paths.
        JunctionService.UnwrapJunction(instBrain);
        JunctionService.UnwrapJunction(instConvos);
        JunctionService.UnwrapAllJunctions(instBrain);
        JunctionService.UnwrapAllJunctions(instConvos);
        Directory.CreateDirectory(instBrain);
        Directory.CreateDirectory(instConvos);

        if (boundProjects.Count == 0)
        {
            MountLooseChats(instAntigravity);
            InitInstanceSummaryDb(instSummaryDb);
            SetupProjectMetadata(boundProjects, instGemini, instUserData);
            return;
        }

        // Preserve any existing project-less rows before rebuilding the summary index from bound projects.
        string? projectlessSnapshotDir = null;
        string? projectlessSnapshotDb = null;
        if (File.Exists(instSummaryDb) && IsUsableSqlite(instSummaryDb))
        {
            try
            {
                projectlessSnapshotDir = Path.Combine(Path.GetTempPath(), $"gl_mount_{Guid.NewGuid():N}");
                Directory.CreateDirectory(projectlessSnapshotDir);
                projectlessSnapshotDb = Path.Combine(projectlessSnapshotDir, "prev_summary.db");
                if (!ProjectService.CopySqliteSnapshot(instSummaryDb, projectlessSnapshotDb))
                {
                    projectlessSnapshotDb = null;
                }
            }
            catch
            {
                projectlessSnapshotDb = null;
            }
        }

        try
        {
            // Rebuild instance summary DB cleanly so stale summaries from previously unbound projects do not linger.
            ProjectService.CreateBlankSummaryDb(instSummaryDb);

            if (projectlessSnapshotDb != null)
            {
                MergeProjectlessSummaryRows(projectlessSnapshotDb, instSummaryDb);
            }

            foreach (var proj in boundProjects)
            {
                var pBrain = _projectService.GetProjectBrainDir(proj.Id);
                var pConvos = _projectService.GetProjectConversationsDir(proj.Id);
                var pDb = _projectService.GetProjectSummaryDbPath(proj.Id);

                Directory.CreateDirectory(pBrain);
                Directory.CreateDirectory(pConvos);
                _projectService.InitSummaryDb(proj.Id);

                SyncDirectory(pBrain, instBrain, preferNewer: true);
                SyncSqliteDirectory(pConvos, instConvos, preferNewer: true);

                if (File.Exists(pDb))
                {
                    MergeSummariesDb(pDb, instSummaryDb);
                }
            }

            if (boundProjects.Count == 1)
            {
                var projPb = _projectService.GetProjectSummariesPbPath(boundProjects[0].Id);
                var instPb = Path.Combine(instAntigravity, "agyhub_summaries_proto.pb");
                if (File.Exists(projPb))
                {
                    try { File.Copy(projPb, instPb, true); } catch { }
                }
            }

            AlignSummaryProjectIds(instSummaryDb, boundProjects);
            SetupProjectMetadata(boundProjects, instGemini, instUserData);
        }
        finally
        {
            if (projectlessSnapshotDir != null && Directory.Exists(projectlessSnapshotDir))
            {
                try { Directory.Delete(projectlessSnapshotDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Syncs project-less chats from the host store into an unbound instance as real files/directories.
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

            JunctionService.UnwrapJunction(instConvos);
            JunctionService.UnwrapJunction(instBrain);

            if (Directory.Exists(hostBrain) && !JunctionService.IsJunction(hostBrain))
            {
                SyncDirectory(hostBrain, instBrain, preferNewer: true);
            }

            if (Directory.Exists(hostConvos) && !JunctionService.IsJunction(hostConvos))
            {
                SyncSqliteDirectory(hostConvos, instConvos, preferNewer: true);
            }

            if (File.Exists(hostDb) && IsUsableSqlite(hostDb))
            {
                InitInstanceSummaryDb(instDb);
                MergeSummariesDb(hostDb, instDb);
            }
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("instance.sync-loose-chats", ex);
        }
    }

    private static void InitInstanceSummaryDb(string destDb)
    {
        if (File.Exists(destDb) && ProjectService.IsUsableSqlite(destDb))
        {
            return;
        }

        var hostGemini = ProjectService.GetRealHostGeminiDir();
        var stockDb = Path.Combine(hostGemini, "antigravity", "conversation_summaries.db");
        if (File.Exists(stockDb) && ProjectService.IsUsableSqlite(stockDb))
        {
            try
            {
                if (ProjectService.CopySqliteSnapshot(stockDb, destDb))
                {
                    using var conn = new SqliteConnection($"Data Source={destDb}");
                    conn.Open();
                    using (var tx = conn.BeginTransaction())
                    {
                        ProjectService.ClearSummaryTables(conn, tx);
                        tx.Commit();
                    }

                    using var vacuum = conn.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                    return;
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("instance.vacuum", ex); }
        }

        try
        {
            ProjectService.CreateBlankSummaryDb(destDb);
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("instance.create-blank-summary", ex);
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

    private static readonly string[] SummaryColumns =
    [
        "conversation_id",
        "title",
        "preview",
        "step_count",
        "last_modified_time",
        "workspace_uris",
        "status",
        "source",
        "project_id",
        "agent_name",
        "parent_conversation_id",
        "nesting_depth",
        "battle_id",
        "winning_conversation_id",
        "not_fully_idle",
        "killed",
        "last_user_input_time",
        "last_user_input_step_index",
        "app_data_dir",
        "raw_summary",
        "group_id"
    ];

    private static HashSet<string> ReadTableColumns(SqliteConnection conn, string tableName)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(`{tableName}`);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                cols.Add(reader.GetString(1));
            }
        }
        return cols;
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>Merges summary rows (all available schema columns) and conversation_parents links into destDb, preserving newer destination rows.</summary>
    private static void MergeSummaryRows(string srcDb, string destDb, string? filterConversationId = null, bool onlyProjectless = false)
    {
        if (!IsUsableSqlite(srcDb)) return;

        string[]? colsToCopy = null;
        var srcRows = new List<(string Cid, DateTime Mod, int Steps, string Title, object[] Values)>();
        var srcParents = new List<(object Cid, object Pid)>();

        ProjectService.TryQuerySqliteSnapshot(srcDb, srcConn =>
        {
            if (!TableExists(srcConn, "conversation_summaries")) return false;

            var srcCols = ReadTableColumns(srcConn, "conversation_summaries");
            colsToCopy = SummaryColumns.Where(c => srcCols.Contains(c)).ToArray();
            if (colsToCopy.Length == 0) return false;

            var cidColIdx = Array.IndexOf(colsToCopy, "conversation_id");
            var modColIdx = Array.IndexOf(colsToCopy, "last_modified_time");
            var stepsColIdx = Array.IndexOf(colsToCopy, "step_count");
            var titleColIdx = Array.IndexOf(colsToCopy, "title");
            var projColIdx = Array.IndexOf(colsToCopy, "project_id");
            var urisColIdx = Array.IndexOf(colsToCopy, "workspace_uris");
            var colListSql = string.Join(", ", colsToCopy.Select(c => $"`{c}`"));

            using (var selectCmd = srcConn.CreateCommand())
            {
                selectCmd.CommandText = filterConversationId == null
                    ? $"SELECT {colListSql} FROM `conversation_summaries`;"
                    : $"SELECT {colListSql} FROM `conversation_summaries` WHERE `conversation_id` = @cid;";
                if (filterConversationId != null)
                {
                    selectCmd.Parameters.AddWithValue("@cid", filterConversationId);
                }

                using var reader = selectCmd.ExecuteReader();
                while (reader.Read())
                {
                    var cid = cidColIdx >= 0 && !reader.IsDBNull(cidColIdx) ? reader.GetString(cidColIdx) : string.Empty;
                    if (string.IsNullOrEmpty(cid)) continue;

                    if (onlyProjectless)
                    {
                        var rowProjId = projColIdx >= 0 && !reader.IsDBNull(projColIdx) ? reader.GetString(projColIdx) : null;
                        var rowUris = urisColIdx >= 0 && !reader.IsDBNull(urisColIdx) ? reader.GetString(urisColIdx) : null;
                        if (!ProjectService.IsProjectlessConversation(rowProjId, rowUris)) continue;
                    }

                    var srcMod = modColIdx >= 0 ? ParseReaderDateTimeUtc(reader, modColIdx) : DateTime.MinValue;
                    var srcSteps = stepsColIdx >= 0 && !reader.IsDBNull(stepsColIdx) ? reader.GetInt32(stepsColIdx) : 0;
                    var srcTitle = titleColIdx >= 0 && !reader.IsDBNull(titleColIdx) ? reader.GetString(titleColIdx) : string.Empty;
                    var vals = new object[colsToCopy.Length];
                    for (var i = 0; i < colsToCopy.Length; i++)
                    {
                        vals[i] = GetDbValue(reader, i);
                    }
                    srcRows.Add((cid, srcMod, srcSteps, srcTitle, vals));
                }
            }

            if (TableExists(srcConn, "conversation_parents") && (!onlyProjectless || srcRows.Count > 0))
            {
                var mergedCids = srcRows.Select(r => r.Cid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                using var pSelect = srcConn.CreateCommand();
                pSelect.CommandText = filterConversationId == null
                    ? "SELECT `conversation_id`, `parent_id` FROM `conversation_parents`;"
                    : "SELECT `conversation_id`, `parent_id` FROM `conversation_parents` WHERE `conversation_id` = @cid OR `parent_id` = @cid;";
                if (filterConversationId != null)
                {
                    pSelect.Parameters.AddWithValue("@cid", filterConversationId);
                }

                using var pReader = pSelect.ExecuteReader();
                while (pReader.Read())
                {
                    if (onlyProjectless)
                    {
                        var childId = pReader.IsDBNull(0) ? string.Empty : pReader.GetString(0);
                        if (!mergedCids.Contains(childId)) continue;
                    }
                    srcParents.Add((GetDbValue(pReader, 0), GetDbValue(pReader, 1)));
                }
            }

            return true;
        }, out _);

        if (colsToCopy == null || colsToCopy.Length == 0 || (srcRows.Count == 0 && srcParents.Count == 0))
            return;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!File.Exists(destDb) || !IsUsableSqlite(destDb))
                {
                    if (File.Exists(destDb))
                    {
                        DiagLog.Swallowed("instance.repair-corrupt-dest-summary", new InvalidDataException($"Recreating unusable summary DB: {destDb}"));
                    }
                    ProjectService.CreateBlankSummaryDb(destDb);
                }

                using var destConn = new SqliteConnection($"Data Source={destDb}");
                destConn.Open();

                using var tx = destConn.BeginTransaction();
                ProjectService.EnsureSummarySchema(destConn, tx);

                var existingMeta = new Dictionary<string, (DateTime mod, int steps, string title)>(StringComparer.OrdinalIgnoreCase);
                using (var metaCmd = destConn.CreateCommand())
                {
                    metaCmd.Transaction = tx;
                    metaCmd.CommandText = filterConversationId == null
                        ? "SELECT `conversation_id`, `last_modified_time`, `step_count`, `title` FROM `conversation_summaries`;"
                        : "SELECT `conversation_id`, `last_modified_time`, `step_count`, `title` FROM `conversation_summaries` WHERE `conversation_id` = @cid;";
                    if (filterConversationId != null) metaCmd.Parameters.AddWithValue("@cid", filterConversationId);
                    using var mr = metaCmd.ExecuteReader();
                    while (mr.Read())
                    {
                        if (mr.IsDBNull(0)) continue;
                        var id = mr.GetString(0);
                        var mod = ParseReaderDateTimeUtc(mr, 1);
                        var steps = mr.IsDBNull(2) ? 0 : mr.GetInt32(2);
                        var title = mr.IsDBNull(3) ? string.Empty : mr.GetString(3);
                        existingMeta[id] = (mod, steps, title);
                    }
                }

                var rowsToWrite = srcRows.Where(row =>
                {
                    if (!existingMeta.TryGetValue(row.Cid, out var cur)) return true;
                    if (row.Mod > cur.mod) return true;
                    if (row.Mod < cur.mod) return false;
                    if (row.Steps > cur.steps) return true;
                    if (row.Steps < cur.steps) return false;
                    return !string.Equals(row.Title, cur.title, StringComparison.Ordinal);
                }).ToList();

                if (rowsToWrite.Count > 0)
                {
                    var colListSql = string.Join(", ", colsToCopy.Select(c => $"`{c}`"));
                    var paramListSql = string.Join(", ", colsToCopy.Select((_, i) => $"@p{i}"));

                    foreach (var row in rowsToWrite)
                    {
                        using var insertCmd = destConn.CreateCommand();
                        insertCmd.Transaction = tx;
                        insertCmd.CommandText = $"INSERT OR REPLACE INTO `conversation_summaries` ({colListSql}) VALUES ({paramListSql});";
                        for (var i = 0; i < colsToCopy.Length; i++)
                        {
                            insertCmd.Parameters.AddWithValue($"@p{i}", row.Values[i]);
                        }
                        insertCmd.ExecuteNonQuery();
                    }
                }

                foreach (var (cidVal, pidVal) in srcParents)
                {
                    using var pInsert = destConn.CreateCommand();
                    pInsert.Transaction = tx;
                    pInsert.CommandText = "INSERT OR IGNORE INTO `conversation_parents` (`conversation_id`, `parent_id`) VALUES (@cid, @pid);";
                    pInsert.Parameters.AddWithValue("@cid", cidVal);
                    pInsert.Parameters.AddWithValue("@pid", pidVal);
                    pInsert.ExecuteNonQuery();
                }

                tx.Commit();
                return;
            }
            catch (SqliteException ex) when (attempt == 0 && File.Exists(destDb))
            {
                DiagLog.Swallowed("instance.merge-summaries-recover", ex);
                try { ProjectService.CreateBlankSummaryDb(destDb); } catch { return; }
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("instance.merge-summaries", ex);
                return;
            }
        }
    }

    private static DateTime ParseReaderDateTimeUtc(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return DateTime.MinValue;
        try { return reader.GetDateTime(index).ToUniversalTime(); }
        catch
        {
            try
            {
                var raw = reader.GetString(index);
                if (DateTimeOffset.TryParse(raw, out var dto)) return dto.UtcDateTime;
            }
            catch { }
            return DateTime.MinValue;
        }
    }

    private static void MergeSummariesDb(string srcDb, string destDb) =>
        MergeSummaryRows(srcDb, destDb);

    private static void MergeProjectlessSummaryRows(string srcDb, string destDb) =>
        MergeSummaryRows(srcDb, destDb, onlyProjectless: true);

    private void SyncSingleProjectOutput(InstanceProfile inst, ProjectProfile proj, string instGeminiDir)
    {
        var instAntigravity = Path.Combine(instGeminiDir, "antigravity");
        var instBrain = Path.Combine(instAntigravity, "brain");
        var instConvos = Path.Combine(instAntigravity, "conversations");
        var instSummaryDb = Path.Combine(instAntigravity, "conversation_summaries.db");
        var instPb = Path.Combine(instAntigravity, "agyhub_summaries_proto.pb");

        var projBrain = _projectService.GetProjectBrainDir(proj.Id);
        var projConvos = _projectService.GetProjectConversationsDir(proj.Id);
        var projSummaryDb = _projectService.GetProjectSummaryDbPath(proj.Id);
        var projPb = _projectService.GetProjectSummariesPbPath(proj.Id);

        var projectlessIds = ReadProjectlessConversationIds(instSummaryDb);

        if (Directory.Exists(instBrain) && !JunctionService.IsJunction(instBrain))
        {
            Directory.CreateDirectory(projBrain);
            foreach (var subDir in Directory.GetDirectories(instBrain))
            {
                var cid = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(cid)
                    || cid.Equals("tempmediaStorage", StringComparison.OrdinalIgnoreCase)
                    || JunctionService.IsJunction(subDir)
                    || projectlessIds.Contains(cid))
                {
                    continue;
                }

                SyncDirectory(subDir, Path.Combine(projBrain, cid), preferNewer: true);
            }
        }

        if (Directory.Exists(instConvos) && !JunctionService.IsJunction(instConvos))
        {
            Directory.CreateDirectory(projConvos);
            foreach (var srcDb in Directory.GetFiles(instConvos, "*.db"))
            {
                var cid = Path.GetFileNameWithoutExtension(srcDb);
                if (string.IsNullOrEmpty(cid) || projectlessIds.Contains(cid)) continue;

                SyncSqliteFile(srcDb, Path.Combine(projConvos, Path.GetFileName(srcDb)), preferNewer: true);
            }
        }

        if (File.Exists(instSummaryDb) && IsUsableSqlite(instSummaryDb))
        {
            if (ProjectService.CopySqliteSnapshot(instSummaryDb, projSummaryDb))
            {
                AlignSummaryProjectIds(projSummaryDb, [proj]);
            }
        }

        if (File.Exists(instPb))
        {
            try
            {
                var dir = Path.GetDirectoryName(projPb);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(instPb, projPb, true);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("instance.sync-single-pb", ex);
            }
        }
    }

    private void SyncMultiProjectOutput(InstanceProfile inst, List<ProjectProfile> boundProjects, string instGeminiDir)
    {
        var instAntigravity = Path.Combine(instGeminiDir, "antigravity");
        var instBrain = Path.Combine(instAntigravity, "brain");
        var instConvos = Path.Combine(instAntigravity, "conversations");
        var instSummaryDb = Path.Combine(instAntigravity, "conversation_summaries.db");

        if (!File.Exists(instSummaryDb) || !IsUsableSqlite(instSummaryDb)) return;

        try
        {
            var rows = new List<(string cid, string? urisJson, string? pId)>();
            ProjectService.TryQuerySqliteSnapshot(instSummaryDb, conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id, workspace_uris, project_id FROM conversation_summaries";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cid = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrEmpty(cid)) continue;
                    rows.Add((cid, reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
                return true;
            }, out _);

            foreach (var (cid, urisJson, pId) in rows)
            {
                // Project-less chats belong to no workspace; copying them into one would relocate
                // their brain and make them appear under an unrelated project.
                if (ProjectService.IsProjectlessConversation(pId, urisJson)) continue;

                ProjectProfile? matchedProj = null;
                if (!string.IsNullOrEmpty(pId))
                {
                    matchedProj = boundProjects.FirstOrDefault(p => p.Id.Equals(pId, StringComparison.OrdinalIgnoreCase));
                }
                matchedProj ??= MatchProjectByWorkspaceUris(boundProjects, urisJson);

                if (matchedProj != null)
                {
                    var targetBrain = Path.Combine(_projectService.GetProjectBrainDir(matchedProj.Id), cid);
                    var srcBrain = Path.Combine(instBrain, cid);
                    if (Directory.Exists(srcBrain) && !JunctionService.IsJunction(srcBrain))
                    {
                        SyncDirectory(srcBrain, targetBrain, preferNewer: true);
                    }

                    var targetConvo = Path.Combine(_projectService.GetProjectConversationsDir(matchedProj.Id), $"{cid}.db");
                    var srcConvo = Path.Combine(instConvos, $"{cid}.db");
                    SyncSqliteFile(srcConvo, targetConvo, preferNewer: true);

                    var projDb = _projectService.GetProjectSummaryDbPath(matchedProj.Id);
                    if (!File.Exists(projDb) || !IsUsableSqlite(projDb))
                    {
                        _projectService.InitSummaryDb(matchedProj.Id);
                    }
                    MergeSingleSummary(instSummaryDb, projDb, cid);
                }
            }

            var instPb = Path.Combine(instAntigravity, "agyhub_summaries_proto.pb");
            if (File.Exists(instPb))
            {
                foreach (var proj in boundProjects)
                {
                    var pPb = _projectService.GetProjectSummariesPbPath(proj.Id);
                    try
                    {
                        var pDir = Path.GetDirectoryName(pPb);
                        if (!string.IsNullOrEmpty(pDir)) Directory.CreateDirectory(pDir);
                        File.Copy(instPb, pPb, true);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Swallowed("instance.sync-multi", ex);
        }
    }

    private static void MergeSingleSummary(string srcDb, string destDb, string convoId) =>
        MergeSummaryRows(srcDb, destDb, convoId);

    private static bool IsUsableSqlite(string path) => ProjectService.IsUsableSqlite(path);

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

        // Sync real (non-junction) memory entries from the instance sandbox into canonical project storage without deleting local copies or creating junctions.
        if (Directory.Exists(instBrain) && !JunctionService.IsJunction(instBrain))
        {
            Directory.CreateDirectory(instConvos);

            foreach (var subDir in Directory.GetDirectories(instBrain))
            {
                var cid = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(cid) || cid.Equals("tempmediaStorage", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (JunctionService.IsJunction(subDir))
                    continue;

                ProjectProfile? matchedProj = null;
                var isProjectless = false;

                // A. Resolve project_id / workspace_uris from conversation_summaries.db.
                if (File.Exists(instSummaryDb) && IsUsableSqlite(instSummaryDb))
                {
                    ProjectService.TryQuerySqliteSnapshot(instSummaryDb, conn =>
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT project_id, workspace_uris FROM conversation_summaries WHERE conversation_id=@cid";
                        cmd.Parameters.AddWithValue("@cid", cid);
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            var pId = reader.IsDBNull(0) ? null : reader.GetString(0);
                            var urisJson = reader.IsDBNull(1) ? null : reader.GetString(1);

                            isProjectless = ProjectService.IsProjectlessConversation(pId, urisJson);

                            if (!isProjectless && !string.IsNullOrEmpty(pId))
                            {
                                matchedProj = _projectService.Projects.FirstOrDefault(p => p.Id.Equals(pId, StringComparison.OrdinalIgnoreCase));
                            }

                            if (!isProjectless && matchedProj == null && !string.IsNullOrEmpty(urisJson))
                            {
                                matchedProj = MatchProjectByWorkspaceUris(_projectService.Projects, urisJson);
                            }
                        }
                        return true;
                    }, out _);
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

                // Sync brain/{cid} incrementally into project storage while leaving instance copy as a real directory.
                var targetPubBrain = Path.Combine(_projectService.GetProjectBrainDir(matchedProj.Id), cid);
                bool harvestedChanges = false;
                try
                {
                    harvestedChanges |= SyncDirectory(subDir, targetPubBrain, preferNewer: true);
                }
                catch (Exception ex) { DiagLog.Swallowed("instance.brain-sync", ex); }

                // Sync conversations/{cid}.db cleanly (snapshot newer/missing source -> canonical project store).
                var srcConvoDb = Path.Combine(instConvos, $"{cid}.db");
                var targetPubConvoDb = Path.Combine(_projectService.GetProjectConversationsDir(matchedProj.Id), $"{cid}.db");
                if (File.Exists(srcConvoDb))
                {
                    try
                    {
                        harvestedChanges |= SyncSqliteFile(srcConvoDb, targetPubConvoDb, preferNewer: true);
                    }
                    catch (Exception ex) { DiagLog.Swallowed("instance.convo-sync", ex); }
                }

                // Merge the summary row into the shared project DB.
                if (File.Exists(instSummaryDb) && IsUsableSqlite(instSummaryDb))
                {
                    var pubDb = _projectService.GetProjectSummaryDbPath(matchedProj.Id);
                    if (!File.Exists(pubDb) || !IsUsableSqlite(pubDb))
                    {
                        _projectService.InitSummaryDb(matchedProj.Id);
                    }
                    MergeSingleSummary(instSummaryDb, pubDb, cid);
                }

                if (harvestedChanges)
                {
                    _projectService.RefreshProjectMemoryStats(matchedProj);
                    projectsChanged = true;
                }
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

    private static ProjectProfile? MatchProjectByWorkspaceUris(IEnumerable<ProjectProfile> candidates, string? workspaceUrisJson)
    {
        if (string.IsNullOrWhiteSpace(workspaceUrisJson)) return null;

        var candidateList = candidates
            .Select(p => (Project: p, NormalizedPath: NormalizeCandidateProjectPath(p.Path)))
            .Where(x => !string.IsNullOrEmpty(x.NormalizedPath))
            .ToList();

        if (candidateList.Count == 0) return null;

        var normalizedUris = new List<string>();
        try
        {
            if (JsonNode.Parse(workspaceUrisJson) is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var norm = ProjectService.NormalizeWorkspaceUri(item?.ToString());
                    if (!string.IsNullOrEmpty(norm))
                    {
                        normalizedUris.Add(norm.TrimEnd('\\', '/'));
                    }
                }
            }
        }
        catch { }

        foreach (var uriPath in normalizedUris)
        {
            var match = candidateList.FirstOrDefault(c => string.Equals(c.NormalizedPath, uriPath, StringComparison.OrdinalIgnoreCase)).Project;
            if (match != null) return match;
        }

        // Fallback if workspace_uris wasn't well-formed JSON: check both plain and %3A-encoded forms explicitly.
        if (normalizedUris.Count == 0)
        {
            foreach (var (proj, normPath) in candidateList)
            {
                var fwd = normPath.Replace('\\', '/');
                var encoded = fwd.Replace(":", "%3A");
                if (workspaceUrisJson.Contains(fwd, StringComparison.OrdinalIgnoreCase)
                    || workspaceUrisJson.Contains(encoded, StringComparison.OrdinalIgnoreCase))
                {
                    return proj;
                }
            }
        }

        return null;
    }

    private static string NormalizeCandidateProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');
        }
        catch
        {
            return path.Trim().Replace('/', '\\').TrimEnd('\\');
        }
    }

    private static HashSet<string> ReadProjectlessConversationIds(string summaryDbPath)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(summaryDbPath) || !IsUsableSqlite(summaryDbPath)) return ids;

        ProjectService.TryQuerySqliteSnapshot(summaryDbPath, conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, project_id, workspace_uris FROM conversation_summaries;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var cid = reader.GetString(0);
                var pId = reader.IsDBNull(1) ? null : reader.GetString(1);
                var uris = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (ProjectService.IsProjectlessConversation(pId, uris))
                {
                    ids.Add(cid);
                }
            }
            return true;
        }, out _);

        return ids;
    }

    private static DateTime GetEffectiveSqliteWriteTimeUtc(string dbPath)
    {
        var best = File.Exists(dbPath) ? File.GetLastWriteTimeUtc(dbPath) : DateTime.MinValue;
        var wal = dbPath + "-wal";
        if (File.Exists(wal))
        {
            var walTime = File.GetLastWriteTimeUtc(wal);
            if (walTime > best) best = walTime;
        }
        return best;
    }

    private static long GetEffectiveSqliteLength(string dbPath)
    {
        long total = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
        var wal = dbPath + "-wal";
        if (File.Exists(wal)) total += new FileInfo(wal).Length;
        return total;
    }

    private static bool SyncSqliteFile(string sourceDb, string destDb, bool preferNewer = true)
    {
        if (!File.Exists(sourceDb) || !IsUsableSqlite(sourceDb)) return false;

        var destExists = File.Exists(destDb);
        var destUsable = destExists && IsUsableSqlite(destDb);

        if (destUsable && preferNewer)
        {
            var srcTime = GetEffectiveSqliteWriteTimeUtc(sourceDb);
            var dstTime = GetEffectiveSqliteWriteTimeUtc(destDb);
            if (srcTime < dstTime) return false;
            if (srcTime == dstTime && GetEffectiveSqliteLength(sourceDb) <= GetEffectiveSqliteLength(destDb)) return false;
        }

        return ProjectService.CopySqliteSnapshot(sourceDb, destDb);
    }

    private static void SyncSqliteDirectory(string sourceDir, string destDir, bool preferNewer = true)
    {
        if (!Directory.Exists(sourceDir) || JunctionService.IsJunction(sourceDir)) return;
        JunctionService.UnwrapJunction(destDir);
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.db"))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            SyncSqliteFile(file, dest, preferNewer);
        }
    }

    private static bool SyncDirectory(string sourceDir, string destinationDir, bool preferNewer = true)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;

        JunctionService.UnwrapJunction(destinationDir);
        Directory.CreateDirectory(destinationDir);

        var anyCopied = false;
        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            var destInfo = new FileInfo(targetFilePath);
            if (destInfo.Exists && preferNewer)
            {
                if (file.LastWriteTimeUtc < destInfo.LastWriteTimeUtc) continue;
                if (file.LastWriteTimeUtc == destInfo.LastWriteTimeUtc && file.Length <= destInfo.Length) continue;
            }

            try
            {
                file.CopyTo(targetFilePath, true);
                anyCopied = true;
            }
            catch (Exception ex) { DiagLog.Swallowed("instance.sync-file", ex); }
        }

        foreach (var subDir in dir.GetDirectories())
        {
            if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            if (SyncDirectory(subDir.FullName, newDestinationDir, preferNewer))
            {
                anyCopied = true;
            }
        }

        return anyCopied;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;

        Directory.CreateDirectory(destinationDir);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (var subDir in dir.GetDirectories())
        {
            if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }
}
