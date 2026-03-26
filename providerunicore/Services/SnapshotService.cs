using Google.Cloud.Firestore;

namespace unicoreprovider.Services;

public class SnapshotService : ISnapshotService
{
    private const string RegistryBase = "us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots";

    private readonly IDockerService _dockerService;
    private readonly FirestoreDb _firestoreDb;
    private readonly ILogger<SnapshotService> _logger;

    public SnapshotService(IDockerService dockerService, FirestoreDb firestoreDb, ILogger<SnapshotService> logger)
    {
        _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
        _firestoreDb = firestoreDb ?? throw new ArgumentNullException(nameof(firestoreDb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PullSnapshotAsync(string? imageTag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
            return;

        await _dockerService.PullImageAsync(imageTag, ct);
    }

    public async Task TakeSnapshotAsync(string vmId, string containerId, CancellationToken ct = default)
    {
        var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        // Commit locally first with a local tag
        var localRepo = $"unicore-snapshot-{vmId}";
        var tag = $"snap-{timestamp}";

        await vmRef.UpdateAsync("snapshot_status", "Committing");
        _logger.LogInformation("[Snapshot] Committing container {ContainerId} for VM {VmId}", containerId, vmId);

        await _dockerService.CommitContainerAsync(containerId, localRepo, tag, ct);

        // Tag for Artifact Registry and push so other providers can pull during migration
        var registryTag = $"{RegistryBase}/{localRepo}:{tag}";

        await vmRef.UpdateAsync("snapshot_status", "Pushing");
        _logger.LogInformation("[Snapshot] Pushing {RegistryTag} to Artifact Registry", registryTag);

        try
        {
            await _dockerService.TagImageAsync($"{localRepo}:{tag}", registryTag, ct);
            await _dockerService.PushImageAsync(registryTag, ct);
            _logger.LogInformation("[Snapshot] Push complete for VM {VmId}", vmId);
        }
        catch (Exception ex)
        {
            // Push failure is non-fatal — local snapshot still exists for same-machine migration.
            // Cross-network migration will fall back to the base image + GCS restore.
            _logger.LogWarning("[Snapshot] Push to registry failed for VM {VmId}: {Msg}. Local snapshot retained.", vmId, ex.Message);
        }

        await vmRef.UpdateAsync(new Dictionary<string, object>
        {
            ["snapshot_status"] = "Idle",
            ["snapshot_image"] = registryTag,
            ["last_snapshot_at"] = DateTime.UtcNow,
        });
    }
}
