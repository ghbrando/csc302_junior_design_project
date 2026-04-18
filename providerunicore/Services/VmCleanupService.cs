using Google.Apis.Auth.OAuth2;
using Google.Cloud.ArtifactRegistry.V1;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;

namespace unicoreprovider.Services;

public class VmCleanupService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly FirestoreDb _firestoreDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VmCleanupService> _logger;

    public VmCleanupService(
        FirestoreDb firestoreDb,
        IConfiguration configuration,
        ILogger<VmCleanupService> logger)
    {
        _firestoreDb = firestoreDb;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VmCleanupService started.");

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var snapshot = await _firestoreDb
                    .Collection("virtual_machines")
                    .WhereEqualTo("deletion_requested", true)
                    .GetSnapshotAsync(stoppingToken);

                foreach (var doc in snapshot.Documents)
                {
                    var vm = doc.ConvertTo<VirtualMachine>();
                    await CleanupVmAsync(vm, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VmCleanupService loop failed.");
            }
        }

        _logger.LogInformation("VmCleanupService stopped.");
    }

    private async Task CleanupVmAsync(VirtualMachine vm, CancellationToken ct)
    {
        var vmRef = _firestoreDb.Collection("virtual_machines").Document(vm.VmId);

        // Step 1: Claim the VM. Only transition Requested → CleaningGcs.
        var claimed = await _firestoreDb.RunTransactionAsync(async transaction =>
        {
            var snap = await transaction.GetSnapshotAsync(vmRef);
            if (!snap.Exists) return false;

            var current = snap.ConvertTo<VirtualMachine>();
            if (current.DeletionStatus != "Requested") return false;

            transaction.Update(vmRef, new Dictionary<string, object>
            {
                ["deletion_status"] = "CleaningGcs"
            });
            return true;
        }, cancellationToken: ct);

        if (!claimed) return;

        try
        {
            // Step 2: Delete GCS objects under consumers/{client}/{vmId}/home/
            await DeleteGcsBackupsAsync(vm, ct);

            // Step 3: Advance to CleaningSnapshots
            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["deletion_status"] = "CleaningSnapshots"
            }, cancellationToken: ct);

            // Step 4: Delete Artifact Registry versions of package {vmId}
            await DeleteArtifactRegistrySnapshotsAsync(vm, ct);

            // Step 5: Delete the Firestore document
            await vmRef.DeleteAsync(cancellationToken: ct);

            _logger.LogInformation("Cleanup completed for VM {VmId}.", vm.VmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup failed for VM {VmId}; marking Error.", vm.VmId);
            try
            {
                await vmRef.UpdateAsync(new Dictionary<string, object>
                {
                    ["deletion_status"] = "Error"
                }, cancellationToken: ct);
            }
            catch (Exception updateEx)
            {
                _logger.LogWarning(updateEx, "Failed to mark VM {VmId} deletion_status=Error.", vm.VmId);
            }
        }
    }

    private async Task DeleteGcsBackupsAsync(VirtualMachine vm, CancellationToken ct)
    {
        var bucketName = _configuration["Backups:VolumeBucket"] ?? "unicore-vm-volumes";
        var consumerContext = string.IsNullOrEmpty(vm.Client) ? "shared" : vm.Client;
        var prefix = $"consumers/{consumerContext}/{vm.VmId}/home/";

        var storageClient = await CreateStorageClientAsync();

        try
        {
            var objects = new List<string>();
            await foreach (var obj in storageClient.ListObjectsAsync(bucketName, prefix).WithCancellation(ct))
            {
                if (!string.IsNullOrEmpty(obj.Name))
                    objects.Add(obj.Name);
            }

            foreach (var name in objects)
            {
                try
                {
                    await storageClient.DeleteObjectAsync(bucketName, name, cancellationToken: ct);
                }
                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone — treat as success.
                }
            }

            _logger.LogInformation("Deleted {Count} GCS object(s) for VM {VmId}.", objects.Count, vm.VmId);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("GCS bucket or prefix missing for VM {VmId}; nothing to clean.", vm.VmId);
        }
    }

    private async Task DeleteArtifactRegistrySnapshotsAsync(VirtualMachine vm, CancellationToken ct)
    {
        var registry = _configuration["ArtifactRegistry:Repository"]
            ?? "us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots";

        if (!TryParseArtifactRegistry(registry, out var location, out var project, out var repository))
        {
            _logger.LogWarning("Could not parse ArtifactRegistry:Repository '{Registry}'. Skipping snapshot cleanup for VM {VmId}.",
                registry, vm.VmId);
            return;
        }

        var client = await CreateArtifactRegistryClientAsync(location);
        var packageName = PackageName.FromProjectLocationRepositoryPackage(project, location, repository, vm.VmId);

        try
        {
            var versions = new List<string>();
            var listRequest = new ListVersionsRequest { Parent = packageName.ToString() };
            await foreach (var version in client.ListVersionsAsync(listRequest).WithCancellation(ct))
            {
                versions.Add(version.Name);
            }

            foreach (var versionName in versions)
            {
                try
                {
                    var op = await client.DeleteVersionAsync(versionName, ct);
                    await op.PollUntilCompletedAsync();
                }
                catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
                {
                    // Already gone.
                }
            }

            _logger.LogInformation("Deleted {Count} Artifact Registry version(s) for VM {VmId}.", versions.Count, vm.VmId);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            _logger.LogInformation("Artifact Registry package missing for VM {VmId}; nothing to clean.", vm.VmId);
        }
    }

    private static bool TryParseArtifactRegistry(string registry, out string location, out string project, out string repository)
    {
        location = project = repository = string.Empty;

        // Expected: {location}-docker.pkg.dev/{project}/{repository}
        var parts = registry.TrimEnd('/').Split('/');
        if (parts.Length < 3) return false;

        var host = parts[0];
        const string suffix = "-docker.pkg.dev";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        location = host[..^suffix.Length];
        project = parts[1];
        repository = parts[2];
        return !string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(repository);
    }

    private static async Task<StorageClient> CreateStorageClientAsync()
    {
        var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY");
        if (!string.IsNullOrEmpty(gcpKeyJson))
        {
            var credential = GoogleCredential.FromJson(gcpKeyJson);
            return await StorageClient.CreateAsync(credential);
        }
        return await StorageClient.CreateAsync();
    }

    private static async Task<ArtifactRegistryClient> CreateArtifactRegistryClientAsync(string location)
    {
        var builder = new ArtifactRegistryClientBuilder
        {
            Endpoint = $"{location}-artifactregistry.googleapis.com:443"
        };

        var gcpKeyJson = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY");
        if (!string.IsNullOrEmpty(gcpKeyJson))
        {
            builder.Credential = GoogleCredential.FromJson(gcpKeyJson);
        }

        return await builder.BuildAsync();
    }
}
