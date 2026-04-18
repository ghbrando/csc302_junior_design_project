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
    private readonly IAuditService _audit;
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
        IAuditService audit,
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
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsMigrationInProgress(string migrationRequestId) =>
        _inProgress.ContainsKey(migrationRequestId);

    public async Task PrepareSourceForMigrationAsync(VmMigrationRequest request)
    {
        if (!_inProgress.TryAdd(request.MigrationRequestId, true))
        {
            _logger.LogWarning("[Migration/Source] Request {Id} is already in progress, skipping.", request.MigrationRequestId);
            return;
        }

        var migrationRef = _firestoreDb
            .Collection("vm_migration_requests")
            .Document(request.MigrationRequestId);

        var oldVmRef = _firestoreDb
            .Collection("virtual_machines")
            .Document(request.VmId);

        try
        {
            _logger.LogInformation(
                "[Migration/Source] Preparing source VM {VmId} for migration {Id} → target {Target}",
                request.VmId, request.MigrationRequestId, request.TargetProviderUid);

            _audit.Log(request.SourceProviderUid, "migration_started",
                vmId: request.VmId, consumerUid: request.ConsumerUid,
                detail: $"target={request.TargetProviderUid} requestId={request.MigrationRequestId}");

            // ── Mark request as snapshotting ──────────────────────────────────
            await migrationRef.UpdateAsync("status", "snapshotting");

            // ── Mark old VM as restoring ──────────────────────────────────────
            await oldVmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["migration_status"] = "Restoring",
                ["migration_requested_at"] = DateTime.UtcNow,
            });

            var oldVm = await _vmRepo.GetByIdAsync(request.VmId)
                ?? throw new InvalidOperationException($"Source VM {request.VmId} not found.");

            // ── Snapshot the running container ────────────────────────────────
            // Captures the full OS state (installed packages, configs, etc.)
            // and pushes the image to Artifact Registry so the target provider
            // can pull it.
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                _logger.LogInformation("[Migration/Source] Taking fresh snapshot of VM {VmId}.", request.VmId);
                await _snapshotService.SnapshotNowAsync(request.VmId);
            }

            // ── Force a live backup to GCS ────────────────────────────────────
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                _logger.LogInformation("[Migration/Source] Backing up VM {VmId} to GCS.", request.VmId);
                await _volumeBackupService.ForceBackupToGcsAsync(oldVm);
            }

            // ── Stop the old container ────────────────────────────────────────
            // Snapshot and backup are done — stop the container to free
            // resources and release the container name.
            if (!string.IsNullOrEmpty(oldVm.ContainerId))
            {
                _logger.LogInformation("[Migration/Source] Stopping old container {ContainerId}.", oldVm.ContainerId);
                try
                {
                    _monitorService.StopMonitoring(request.VmId);
                    await _dockerService.StopContainerAsync(oldVm.ContainerId, oldVm.Name, oldVm.VolumeName,
                        vmId: oldVm.VmId,
                        providerUid: oldVm.ProviderId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Migration/Source] Could not stop old container: {Msg}", ex.Message);
                }
            }

            // ── Hand off to target provider ───────────────────────────────────
            _logger.LogInformation(
                "[Migration/Source] Source preparation complete for {Id}. Setting status to ready_for_restore.",
                request.MigrationRequestId);

            await migrationRef.UpdateAsync("status", "ready_for_restore");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Migration/Source] Source preparation failed for {Id}: {Message}",
                request.MigrationRequestId, ex.Message);

            _audit.Log(request.SourceProviderUid, "migration_failed",
                vmId: request.VmId, consumerUid: request.ConsumerUid,
                detail: $"phase=source error={ex.Message} requestId={request.MigrationRequestId}");

            try
            {
                await migrationRef.UpdateAsync(new Dictionary<string, object>
                {
                    ["status"] = "Failed",
                    ["error"] = $"Source preparation failed: {ex.Message}",
                });
            }
            catch (Exception writeEx)
            {
                _logger.LogError(writeEx, "[Migration/Source] Could not update migration request to Failed.");
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
                _logger.LogError(writeEx, "[Migration/Source] Could not update old VM migration_status to Failed.");
            }
        }
        finally
        {
            _inProgress.TryRemove(request.MigrationRequestId, out _);
        }
    }

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
            _logger.LogInformation("[Migration/Target] Starting restore for migration {Id}: VM {VmId}",
                request.MigrationRequestId, request.VmId);

            // ── Step 1: Mark request as restoring ─────────────────────────────
            await migrationRef.UpdateAsync("status", "restoring");

            // Fetch old VM for its settings (snapshot_image, specs, etc.)
            var oldVm = await _vmRepo.GetByIdAsync(request.VmId)
                ?? throw new InvalidOperationException($"Source VM {request.VmId} not found.");

            // ── Step 2: Pull snapshot image from Artifact Registry ────────────
            _logger.LogInformation("[Migration/Target] Pulling snapshot image: {Image}", oldVm.SnapshotImage ?? "(none)");
            await _snapshotService.PullSnapshotAsync(oldVm.SnapshotImage);

            // ── Step 3: Create new volume ─────────────────────────────────────
            newVmId = Guid.NewGuid().ToString();
            newVolumeName = $"unicore-vol-{newVmId}";
            _logger.LogInformation("[Migration/Target] Creating volume {Volume}", newVolumeName);
            await _dockerService.CreateVolumeAsync(newVolumeName, oldVm.VolumeRequestedGb);

            // ── Step 4: Restore user data from GCS ────────────────────────────
            if (!string.IsNullOrEmpty(oldVm.Client))
            {
                _logger.LogInformation("[Migration/Target] Restoring GCS data for consumer {Uid}", oldVm.Client);
                await _volumeBackupService.RestoreFromGcsAsync(request.VmId, oldVm.Client, newVolumeName);
            }
            else
            {
                _logger.LogInformation("[Migration/Target] Skipping GCS restore (no consumer UID on VM).");
            }

            // ── Step 5: Allocate relay port ───────────────────────────────────
            _logger.LogInformation("[Migration/Target] Allocating relay port.");
            var relayPort = await AllocateRelayPortAsync();

            // ── Step 6: Start container from snapshot (or base image) ─────────
            var imageToUse = !string.IsNullOrWhiteSpace(oldVm.SnapshotImage)
                ? oldVm.SnapshotImage
                : oldVm.Image;

            var newVmName = oldVm.Name;
            var startedAt = DateTime.UtcNow;
            var effectiveCPU = request.EffectiveCpuCores;
            var effectiveRAM = request.EffectiveRamGb;

            if (effectiveCPU <= 0 || effectiveRAM <= 0)
            {
                _logger.LogWarning("[Migration/Target] Effective CPU or RAM is zero, defaulting to old VM specs.");
                effectiveCPU = oldVm.CpuCores;
                effectiveRAM = oldVm.RamGB;
            }

            var serviceRelayPort = oldVm.ServiceRelayPort;

            _logger.LogInformation("[Migration/Target] Starting container from {Image} (serviceRelayPort={SvcPort})",
                imageToUse, serviceRelayPort?.ToString() ?? "none");
            var (containerId, _) = await _dockerService.StartContainerAsync(
                newVmId, newVmName, imageToUse, relayPort,
                effectiveCPU, effectiveRAM,
                existingVolumeName: newVolumeName,
                consumerUid: oldVm.Client,
                volumeGb: oldVm.VolumeRequestedGb,
                serviceRelayPort: serviceRelayPort,
                providerUid: request.TargetProviderUid);

            newContainerId = containerId;
            var sshPort = await _dockerService.GetContainerSshPortAsync(containerId);

            // ── Step 7: Create new VirtualMachine document ────────────────────
            _logger.LogInformation("[Migration/Target] Creating new VM document {VmId}", newVmId);
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
                ServicePort = oldVm.ServicePort,
                ServiceRelayPort = serviceRelayPort,
                ServiceUrl = serviceRelayPort.HasValue
                    ? $"https://{newVmId}.services.cbu-unicore.com"
                    : null,
            };
            await _vmService.CreateVmAsync(newVm);

            // ── Step 8: Mark old VM as Migrated ───────────────────────────────
            _logger.LogInformation("[Migration/Target] Marking old VM {VmId} as Migrated", request.VmId);
            await oldVmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "Stopped",
                ["migration_status"] = "Migrated",
            });

            // ── Step 9: Wire new VM into monitoring ───────────────────────────
            _logger.LogInformation("[Migration/Target] Starting monitor for new VM {VmId}", newVmId);
            _provisioningService.StartProvisioning(newVmId, containerId, relayPort, startedAt, sshPort);

            // ── Step 10: Mark request as completed ────────────────────────────
            _logger.LogInformation("[Migration/Target] Migration {Id} completed. New VM: {NewVmId}",
                request.MigrationRequestId, newVmId);

            _audit.Log(request.TargetProviderUid, "migration_completed",
                vmId: newVmId, consumerUid: request.ConsumerUid,
                detail: $"oldVm={request.VmId} requestId={request.MigrationRequestId}");

            await migrationRef.UpdateAsync(new Dictionary<string, object>
            {
                ["status"] = "Completed",
                ["completed_at"] = DateTime.UtcNow,
                ["new_vm_id"] = newVmId,
            });

            // ── Step 11: Delete old VM document ───────────────────────────────
            _logger.LogInformation("[Migration/Target] Deleting old VM document {VmId}", request.VmId);
            try
            {
                await oldVmRef.DeleteAsync();
            }
            catch (Exception delEx)
            {
                _logger.LogWarning(delEx, "[Migration/Target] Could not delete old VM doc {VmId}: {Msg}",
                    request.VmId, delEx.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Migration/Target] Migration {Id} failed: {Message}",
                request.MigrationRequestId, ex.Message);

            _audit.Log(request.TargetProviderUid, "migration_failed",
                vmId: request.VmId, consumerUid: request.ConsumerUid,
                detail: $"phase=restore error={ex.Message} requestId={request.MigrationRequestId}");

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
                _logger.LogError(writeEx, "[Migration/Target] Could not update migration request to Failed.");
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
                _logger.LogError(writeEx, "[Migration/Target] Could not update old VM migration_status to Failed.");
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
