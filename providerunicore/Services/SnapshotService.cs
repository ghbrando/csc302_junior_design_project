using Docker.DotNet;
using Google.Cloud.Firestore;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

namespace unicoreprovider.Services;

public class SnapshotService : BackgroundService, ISnapshotService
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(5);

    private readonly FirestoreDb _firestoreDb;
    private readonly IDockerService _dockerService;
    private readonly IConfiguration _configuration;
    private readonly IAuditService _audit;
    private readonly ILogger<SnapshotService> _logger;

    private readonly Channel<string> _onDemandQueue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, bool> _queuedVmIds = new();

    public SnapshotService(
        FirestoreDb firestoreDb,
        IDockerService dockerService,
        IConfiguration configuration,
        IAuditService audit,
        ILogger<SnapshotService> logger)
    {
        _firestoreDb = firestoreDb;
        _dockerService = dockerService;
        _configuration = configuration;
        _audit = audit;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SnapshotService started.");

        var nextScheduledRunAt = DateTime.UtcNow.Add(SnapshotInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_onDemandQueue.Reader.TryRead(out var vmId))
                {
                    try
                    {
                        await SnapshotVmByIdAsync(vmId, stoppingToken);
                    }
                    finally
                    {
                        _queuedVmIds.TryRemove(vmId, out _);
                    }
                }

                if (DateTime.UtcNow >= nextScheduledRunAt)
                {
                    await RunScheduledSnapshotsAsync(stoppingToken);
                    nextScheduledRunAt = DateTime.UtcNow.Add(SnapshotInterval);
                }

                await Task.Delay(LoopInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot service loop failed.");
            }
        }

        _logger.LogInformation("SnapshotService stopped.");
    }

    public async Task TriggerSnapshotAsync(string vmId)
    {
        if (string.IsNullOrWhiteSpace(vmId))
            throw new ArgumentException("VM ID is required.", nameof(vmId));

        if (_queuedVmIds.TryAdd(vmId, true))
        {
            await _onDemandQueue.Writer.WriteAsync(vmId);
        }
    }

    public async Task SnapshotNowAsync(string vmId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(vmId))
            throw new ArgumentException("VM ID is required.", nameof(vmId));

        await SnapshotVmByIdAsync(vmId, ct);
    }

    public async Task PullSnapshotAsync(string? imageTag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
            return;

        await ExecuteWithRetryAsync(
            () => _dockerService.PullImageAsync(imageTag, ct),
            operationName: $"pull snapshot {imageTag}");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _onDemandQueue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private async Task RunScheduledSnapshotsAsync(CancellationToken ct)
    {
        var runningVms = await _firestoreDb
            .Collection("virtual_machines")
            .WhereEqualTo("status", "Running")
            .GetSnapshotAsync();

        foreach (var vmDoc in runningVms.Documents)
        {
            var vm = vmDoc.ConvertTo<VirtualMachine>();
            if (string.IsNullOrWhiteSpace(vm.ContainerId))
                continue;

            await SnapshotVmAsync(vm, ct);
        }
    }

    private async Task SnapshotVmByIdAsync(string vmId, CancellationToken ct)
    {
        var vmDoc = await _firestoreDb.Collection("virtual_machines").Document(vmId).GetSnapshotAsync();
        if (!vmDoc.Exists)
            return;

        var vm = vmDoc.ConvertTo<VirtualMachine>();
        if (!string.Equals(vm.Status, "Running", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(vm.ContainerId))
            return;

        await SnapshotVmAsync(vm, ct);
    }

    private async Task SnapshotVmAsync(VirtualMachine vm, CancellationToken ct)
    {
        var vmRef = _firestoreDb.Collection("virtual_machines").Document(vm.VmId);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var imageTag = BuildImageTag(vm.VmId, timestamp);

        try
        {
            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["snapshot_status"] = "Committing"
            });

            var (repository, tag) = SplitImageTag(imageTag);

            await ExecuteWithRetryAsync(
                () => _dockerService.CommitContainerAsync(vm.ContainerId, repository, tag, ct),
                operationName: $"commit snapshot for VM {vm.VmId}");

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["snapshot_status"] = "Pushing"
            });

            await ExecuteWithRetryAsync(
                () => _dockerService.PushImageAsync(imageTag, ct),
                operationName: $"push snapshot for VM {vm.VmId}");

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["snapshot_status"] = "Idle",
                ["last_snapshot_at"] = DateTime.UtcNow,
                ["snapshot_image"] = imageTag
            });

            // Audit: provider took a snapshot of a consumer VM.
            _audit.Log(vm.ProviderId, "snapshot_taken",
                vmId: vm.VmId, consumerUid: vm.Client,
                detail: $"image={imageTag}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Snapshot failed for VM {VmId}", vm.VmId);

            var updates = new Dictionary<string, object>
            {
                ["snapshot_status"] = "Error"
            };

            // If the container no longer exists the VM is orphaned — mark it Failed so it
            // is removed from the "Running" list and the scheduler stops attempting it.
            if (IsContainerNotFound(ex))
            {
                _logger.LogWarning("Container for VM {VmId} no longer exists; marking VM as Failed.", vm.VmId);
                updates["status"] = "Failed";
            }

            await vmRef.UpdateAsync(updates);
        }
    }

    private string BuildImageTag(string vmId, string timestamp)
    {
        var registry = _configuration["ArtifactRegistry:Repository"]
            ?? "us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots";

        return $"{registry.TrimEnd('/')}/{vmId}:{timestamp}";
    }

    private static (string Repository, string Tag) SplitImageTag(string imageTag)
    {
        var lastSlash = imageTag.LastIndexOf('/');
        var lastColon = imageTag.LastIndexOf(':');

        if (lastColon <= lastSlash)
            throw new InvalidOperationException($"Invalid image tag: {imageTag}");

        return (imageTag[..lastColon], imageTag[(lastColon + 1)..]);
    }

    /// <summary>
    /// Returns true when <paramref name="ex"/> (possibly wrapped by ExecuteWithRetryAsync)
    /// originates from a Docker 404 — i.e. the target container no longer exists.
    /// </summary>
    private static bool IsContainerNotFound(Exception ex)
    {
        var inner = ex.InnerException ?? ex;
        return inner is DockerApiException dockerEx &&
               dockerEx.StatusCode == HttpStatusCode.NotFound;
    }

    private static async Task ExecuteWithRetryAsync(Func<Task> operation, string operationName, int maxAttempts = 3)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await operation();
                return;
            }
            catch when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{operationName} failed after {attempt} attempt(s).", ex);
            }
        }
    }
}
