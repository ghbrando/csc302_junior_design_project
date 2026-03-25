using Google.Cloud.Firestore;
using System.Collections.Concurrent;

namespace unicoreprovider.Services;

public class MigrationService : IMigrationService
{
    private readonly IDockerService _dockerService;
    private readonly ISnapshotService _snapshotService;
    private readonly IVolumeBackupService _volumeBackupService;
    private readonly IVmService _vmService;
    private readonly IFirestoreRepository<VirtualMachine> _vmRepo;
    private readonly ContainerMonitorService _monitorService;
    private readonly FirestoreDb _firestoreDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MigrationService> _logger;

    // Tracks migration request IDs currently being processed to prevent duplicate work.
    private readonly ConcurrentDictionary<string, bool> _inProgress = new();

    public MigrationService(
        IDockerService dockerService,
        ISnapshotService snapshotService,
        IVolumeBackupService volumeBackupService,
        IVmService vmService,
        IFirestoreRepository<VirtualMachine> vmRepo,
        ContainerMonitorService monitorService,
        FirestoreDb firestoreDb,
        IConfiguration configuration,
        ILogger<MigrationService> logger)
    {
        _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _volumeBackupService = volumeBackupService ?? throw new ArgumentNullException(nameof(volumeBackupService));
        _vmService = vmService ?? throw new ArgumentNullException(nameof(vmService));
        _vmRepo = vmRepo ?? throw new ArgumentNullException(nameof(vmRepo));
        _monitorService = monitorService ?? throw new ArgumentNullException(nameof(monitorService));
        _firestoreDb = firestoreDb ?? throw new ArgumentNullException(nameof(firestoreDb));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsMigrationInProgress(string migrationRequestId) =>
        _inProgress.ContainsKey(migrationRequestId);

    public async Task ProcessMigrationRequestAsync(VmMigrationRequest request)
    {
        if (!_inProgress.TryAdd(request.MigrationRequestId, true))
        {
            _logger.LogWarning("[Migration] Request {Id} is already in progress, skipping.", request.MigrationRequestId);
            return;
        }

        var migrationRef = _firestoreDb
            .Collection("vm_migration_requests")
            .Document(request.MigrationRequestId);

        var oldVmRef = _firestoreDb
            .Collection("virtual_machines")
            .Document(request.VmId);

        string? newVmId = null;
        string? newContainerId = null;
        string? newVolumeName = null;

        try
        {
            _logger.LogInformation("[Migration] Starting migration {Id}: VM {VmId} → provider {Target}",
                request.MigrationRequestId, request.VmId, request.TargetProviderUid);

            // ── Step 1: Mark request as in-progress ───────────────────────────
            await migrationRef.UpdateAsync("status", "restoring");

            // ── Step 2: Mark old VM as restoring ──────────────────────────────
            await oldVmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["migration_status"] = "Restoring",
                ["migration_requested_at"] = DateTime.UtcNow,
            });

            // Fetch old VM for its settings
            var oldVm = await _vmRepo.GetByIdAsync(request.VmId)
                ?? throw new InvalidOperationException($"Source VM {request.VmId} not found.");

            // ── Step 3: Pull snapshot image from Artifact Registry ─────────────
            _logger.LogInformation("[Migration] Step 3 – pulling snapshot image: {Image}", oldVm.SnapshotImage ?? "(none)");
            await _snapshotService.PullSnapshotAsync(oldVm.SnapshotImage);

            // ── Step 4: Create new volume ──────────────────────────────────────
            newVmId = Guid.NewGuid().ToString();
            newVolumeName = $"unicore-vol-{newVmId}";
            _logger.LogInformation("[Migration] Step 4 – creating volume {Volume}", newVolumeName);
            await _dockerService.CreateVolumeAsync(newVolumeName, oldVm.VolumeRequestedGb);

            // ── Step 5: Restore user data from GCS ────────────────────────────
            if (!string.IsNullOrEmpty(oldVm.Client))
            {
                _logger.LogInformation("[Migration] Step 5 – restoring GCS data for consumer {Uid}", oldVm.Client);
                await _volumeBackupService.RestoreFromGcsAsync(request.VmId, oldVm.Client, newVolumeName);
            }
            else
            {
                _logger.LogInformation("[Migration] Step 5 – skipping GCS restore (no consumer UID on VM).");
            }

            // ── Step 6: Allocate relay port ────────────────────────────────────
            _logger.LogInformation("[Migration] Step 6 – allocating relay port.");
            var relayPort = await AllocateRelayPortAsync();

            // ── Step 7: Start container from snapshot (or base image) ──────────
            var imageToUse = !string.IsNullOrWhiteSpace(oldVm.SnapshotImage)
                ? oldVm.SnapshotImage
                : oldVm.Image;

            var newVmName = $"vm-{newVmId[..8]}";
            var startedAt = DateTime.UtcNow;

            _logger.LogInformation("[Migration] Step 7 – starting container from {Image}", imageToUse);
            var (containerId, _) = await _dockerService.StartContainerAsync(
                newVmId, newVmName, imageToUse, relayPort,
                oldVm.CpuCores, oldVm.RamGB,
                existingVolumeName: newVolumeName,
                consumerUid: oldVm.Client,
                volumeGb: oldVm.VolumeRequestedGb);

            newContainerId = containerId;
            var sshPort = await _dockerService.GetContainerSshPortAsync(containerId);

            // ── Step 8: Create new VirtualMachine document ────────────────────
            _logger.LogInformation("[Migration] Step 8 – creating new VM document {VmId}", newVmId);
            var newVm = new VirtualMachine
            {
                VmId = newVmId,
                Name = newVmName,
                ContainerId = containerId,
                VolumeName = newVolumeName,
                StartedAt = startedAt,
                Status = "Running",
                Image = oldVm.Image,
                SnapshotImage = oldVm.SnapshotImage,
                ProviderId = request.TargetProviderUid,
                Client = oldVm.Client,
                RelayPort = relayPort,
                SshPort = sshPort,
                CpuCores = oldVm.CpuCores,
                RamGB = oldVm.RamGB,
                VolumeRequestedGb = oldVm.VolumeRequestedGb,
                VolumeSyncStatus = "Idle",
                OriginalVmId = request.VmId,
            };
            await _vmService.CreateVmAsync(newVm);

            // ── Step 9: Stop old VM and mark as Migrated ──────────────────────
            _logger.LogInformation("[Migration] Step 9 – stopping old VM {VmId}", request.VmId);
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                try
                {
                    await _dockerService.StopContainerAsync(oldVm.ContainerId, oldVm.Name, oldVm.VolumeName);
                }
                catch (Exception ex)
                {
                    // Best-effort: the old container may already be gone
                    _logger.LogWarning("[Migration] Could not stop old container {Id}: {Msg}", oldVm.ContainerId, ex.Message);
                }
            }

            _monitorService.StopMonitoring(request.VmId);

            await oldVmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "Stopped",
                ["migration_status"] = "Migrated",
            });

            // ── Step 10: Wire new VM into monitoring ───────────────────────────
            _logger.LogInformation("[Migration] Step 10 – starting monitor for new VM {VmId}", newVmId);
            _monitorService.StartMonitoring(newVmId, containerId, startedAt);

            // ── Step 11: Mark request as completed ────────────────────────────
            _logger.LogInformation("[Migration] Step 11 – migration {Id} completed. New VM: {NewVmId}",
                request.MigrationRequestId, newVmId);

            await migrationRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "Completed",
                ["completed_at"] = DateTime.UtcNow,
                ["new_vm_id"] = newVmId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Migration] Migration {Id} failed: {Message}",
                request.MigrationRequestId, ex.Message);

            await CleanupPartialMigrationAsync(newContainerId, newVolumeName, newVmId);

            try
            {
                await migrationRef.UpdateAsync(new Dictionary<string, object>
                {
                    ["status"] = "Failed",
                    ["error"] = ex.Message,
                });
            }
            catch (Exception writeEx)
            {
                _logger.LogError(writeEx, "[Migration] Could not update migration request to Failed.");
            }

            try
            {
                await oldVmRef.UpdateAsync(new Dictionary<string, object>
                {
                    ["migration_status"] = "Failed",
                    ["migration_error"] = ex.Message,
                });
            }
            catch (Exception writeEx)
            {
                _logger.LogError(writeEx, "[Migration] Could not update old VM migration_status to Failed.");
            }
        }
        finally
        {
            _inProgress.TryRemove(request.MigrationRequestId, out _);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Queries all VMs to find a relay port in the configured range that is not yet in use.
    /// </summary>
    private async Task<int> AllocateRelayPortAsync()
    {
        var portStart = _configuration.GetValue<int>("FrpRelay:PortRangeStart", 2222);
        var portEnd   = _configuration.GetValue<int>("FrpRelay:PortRangeEnd",   2300);

        var allVms    = await _vmRepo.GetAllAsync();
        var usedPorts = allVms
            .Where(v => v.RelayPort.HasValue)
            .Select(v => v.RelayPort!.Value)
            .ToHashSet();

        var available = Enumerable
            .Range(portStart, portEnd - portStart + 1)
            .FirstOrDefault(p => !usedPorts.Contains(p));

        if (available == 0)
            throw new InvalidOperationException(
                $"No relay ports available in range {portStart}–{portEnd}.");

        return available;
    }

    /// <summary>
    /// Best-effort cleanup of a partially-created migration.
    /// Never throws — logs errors but does not re-surface them.
    /// </summary>
    private async Task CleanupPartialMigrationAsync(string? containerId, string? volumeName, string? vmId)
    {
        if (!string.IsNullOrEmpty(containerId))
        {
            try { await _dockerService.StopContainerAsync(containerId, vmId ?? "partial-migration"); }
            catch (Exception ex) { _logger.LogWarning("[Migration] Cleanup: failed to stop container {Id}: {Msg}", containerId, ex.Message); }
        }

        if (!string.IsNullOrEmpty(volumeName))
        {
            try { await _dockerService.RemoveVolumeAsync(volumeName); }
            catch (Exception ex) { _logger.LogWarning("[Migration] Cleanup: failed to remove volume {Name}: {Msg}", volumeName, ex.Message); }
        }

        if (!string.IsNullOrEmpty(vmId))
        {
            try { await _vmService.DeleteVmAsync(vmId); }
            catch (Exception ex) { _logger.LogWarning("[Migration] Cleanup: failed to delete VM doc {Id}: {Msg}", vmId, ex.Message); }
        }
    }
}
