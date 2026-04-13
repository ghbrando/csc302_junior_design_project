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
    private readonly VmProvisioningService _provisioningService;
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
        VmProvisioningService provisioningService,
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
        _provisioningService = provisioningService ?? throw new ArgumentNullException(nameof(provisioningService));
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

            // ── Step 2.5: Force a fresh snapshot of the running container ────────
            // This captures the full OS state (installed packages, configs, etc.)
            // so the target provider gets the exact current state, not a stale
            // 2-hour-old snapshot.
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                _logger.LogInformation("[Migration] Step 2.5 – taking fresh snapshot of VM {VmId} before migration.", request.VmId);
                try
                {
                    await _snapshotService.TriggerSnapshotAsync(request.VmId);
                    // Re-fetch the VM to get the updated snapshot_image tag
                    oldVm = await _vmRepo.GetByIdAsync(request.VmId)
                        ?? throw new InvalidOperationException($"Source VM {request.VmId} not found after snapshot.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Migration] Fresh snapshot failed: {Msg}. Will use last known snapshot (if any).", ex.Message);
                }
            }

            // ── Step 2.6: Force a live backup of the old VM to GCS ───────────────
            // This ensures GCS has fresh user data (/home/consumer) before the
            // restore step, regardless of whether the consumer ever pressed "Backup Now".
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                _logger.LogInformation("[Migration] Step 2.6 – backing up old VM {VmId} to GCS before restore.", request.VmId);
                try
                {
                    await _volumeBackupService.ForceBackupToGcsAsync(oldVm);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Migration] GCS backup failed: {Msg}. Restore will use last known backup (if any).", ex.Message);
                }
            }

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

            // ── Step 6.5: Stop the old container ────────────────────────────────
            // Snapshot and backup are done — stop the old container to free
            // resources and release the container name for the new one.
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                _logger.LogInformation("[Migration] Step 6.5 – stopping old container {ContainerId}", oldVm.ContainerId);
                try
                {
                    _monitorService.StopMonitoring(request.VmId);
                    await _dockerService.StopContainerAsync(oldVm.ContainerId, oldVm.Name, oldVm.VolumeName,
                        vmId: oldVm.VmId,
                        providerUid: oldVm.ProviderId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Migration] Could not stop old container: {Msg}", ex.Message);
                }
            }

            // ── Step 7: Start container from snapshot (or base image) ──────────
            var imageToUse = !string.IsNullOrWhiteSpace(oldVm.SnapshotImage)
                ? oldVm.SnapshotImage
                : oldVm.Image;

            var newVmName = oldVm.Name;
            var startedAt = DateTime.UtcNow;
            var effectiveCPU = request.EffectiveCpuCores;
            var effectiveRAM = request.EffectiveRamGb;

            if (effectiveCPU <= 0 || effectiveRAM <= 0)
            {
                _logger.LogWarning("[Migration] Effective CPU or RAM is null, defaulting to old VM specs.");
                effectiveCPU = oldVm.CpuCores;
                effectiveRAM = oldVm.RamGB;
            }

            _logger.LogInformation("[Migration] Step 7 – starting container from {Image}", imageToUse);
            var (containerId, _) = await _dockerService.StartContainerAsync(
                newVmId, newVmName, imageToUse, relayPort,
                effectiveCPU, effectiveRAM,
                existingVolumeName: newVolumeName,
                consumerUid: oldVm.Client,
                volumeGb: oldVm.VolumeRequestedGb,
                providerUid: request.TargetProviderUid);

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
                Status = "Provisioning",
                Image = oldVm.Image,
                SnapshotImage = oldVm.SnapshotImage,
                ProviderId = request.TargetProviderUid,
                Client = oldVm.Client,
                RelayPort = relayPort,
                SshPort = sshPort,
                CpuCores = effectiveCPU,
                RamGB = effectiveRAM,
                VolumeRequestedGb = oldVm.VolumeRequestedGb,
                VolumeSyncStatus = "Idle",
                OriginalVmId = request.VmId,
            };
            await _vmService.CreateVmAsync(newVm);

            // ── Step 9: Mark old VM as Migrated via Firestore ────────────────
            _logger.LogInformation("[Migration] Step 9 – marking old VM {VmId} as Migrated in Firestore", request.VmId);

            await oldVmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "Stopped",
                ["migration_status"] = "Migrated",
            });

            // ── Step 10: Wire new VM into monitoring ───────────────────────────
            _logger.LogInformation("[Migration] Step 10 – starting monitor for new VM {VmId}", newVmId);
            _provisioningService.StartProvisioning(newVmId, containerId, relayPort, startedAt);

            // ── Step 11: Mark request as completed ────────────────────────────
            _logger.LogInformation("[Migration] Step 11 – migration {Id} completed. New VM: {NewVmId}",
                request.MigrationRequestId, newVmId);

            await migrationRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "Completed",
                ["completed_at"] = DateTime.UtcNow,
                ["new_vm_id"] = newVmId,
            });

            // ── Step 12: Delete old VM document ─────────────────────────────
            // The old VM is fully migrated and its container will be cleaned up
            // by the source provider's PauseResumeListenerService. Remove the
            // Firestore document so it doesn't linger in the consumer's stopped list.
            _logger.LogInformation("[Migration] Step 12 – deleting old VM document {VmId}", request.VmId);
            try
            {
                await oldVmRef.DeleteAsync();
            }
            catch (Exception delEx)
            {
                _logger.LogWarning(delEx, "[Migration] Could not delete old VM doc {VmId}: {Msg}",
                    request.VmId, delEx.Message);
            }
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
        var portEnd = _configuration.GetValue<int>("FrpRelay:PortRangeEnd", 2300);

        var allVms = await _vmRepo.GetAllAsync();
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
            try
            {
                await _dockerService.StopContainerAsync(containerId, vmId ?? "partial-migration", vmId: vmId);
            }
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
