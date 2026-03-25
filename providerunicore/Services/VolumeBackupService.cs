using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using System.Diagnostics;
using System.Formats.Tar;

namespace unicoreprovider.Services;

public class VolumeBackupService : BackgroundService, IVolumeBackupService
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromMinutes(10);

    private readonly FirestoreDb _firestoreDb;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VolumeBackupService> _logger;

    public VolumeBackupService(
        FirestoreDb firestoreDb,
        IConfiguration configuration,
        ILogger<VolumeBackupService> logger)
    {
        _firestoreDb = firestoreDb;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VolumeBackupService started.");

        using var timer = new PeriodicTimer(MonitorInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await MonitorVolumeSyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Volume sync monitor loop failed.");
            }
        }

        _logger.LogInformation("VolumeBackupService stopped.");
    }

    public async Task BackupVolumeNowAsync(string vmId)
    {
        var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);

        VirtualMachine vm = null!;
        await _firestoreDb.RunTransactionAsync(async transaction =>
        {
            var doc = await transaction.GetSnapshotAsync(vmRef);

            if (!doc.Exists)
                throw new InvalidOperationException($"VM {vmId} not found.");

            vm = doc.ConvertTo<VirtualMachine>();

            if (string.IsNullOrWhiteSpace(vm.ContainerId))
                throw new InvalidOperationException("VM has no running container.");

            if (string.Equals(vm.VolumeSyncStatus, "Syncing", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup is already in progress.");

            transaction.Update(vmRef, new Dictionary<string, object>
            {
                ["volume_sync_status"] = "Syncing"
            });
        });

        try
        {
            await ExecuteWithRetryAsync(
                () => BackupContainerToGcsAsync(vm),
                operationName: $"backup VM {vmId}");

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["volume_sync_status"] = "Idle",
                ["last_volume_sync_at"] = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["volume_sync_status"] = "Error"
            });

            throw new InvalidOperationException($"Backup failed for VM {vmId}: {ex.Message}", ex);
        }
    }

    public async Task RestoreFromGcsAsync(string vmId, string gcsPath)
    {
        var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
        var snapshot = await vmRef.GetSnapshotAsync();

        if (!snapshot.Exists)
            throw new InvalidOperationException($"VM {vmId} not found.");

        var vm = snapshot.ConvertTo<VirtualMachine>();
        if (string.IsNullOrWhiteSpace(vm.ContainerId))
            throw new InvalidOperationException("VM has no running container.");

        await vmRef.UpdateAsync(new Dictionary<string, object>
        {
            ["volume_sync_status"] = "Syncing"
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"unicore-restore-{vmId}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            var (bucket, prefix) = ParseGcsPath(gcsPath, vm);

            await ExecuteWithRetryAsync(async () =>
            {
                var storage = await StorageClient.CreateAsync();

                await foreach (var obj in storage.ListObjectsAsync(bucket, prefix))
                {
                    if (string.IsNullOrEmpty(obj.Name) || obj.Name.EndsWith('/'))
                        continue;

                    var relativePath = obj.Name.StartsWith(prefix)
                        ? obj.Name[prefix.Length..].TrimStart('/')
                        : obj.Name;

                    var filePath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    await using var output = File.Create(filePath);
                    await storage.DownloadObjectAsync(obj, output);
                }
            }, operationName: $"download restore data for VM {vmId}");

            await ExecuteWithRetryAsync(
                () => RunProcessAsync("docker", $"cp \"{tempDir}/.\" \"{vm.ContainerId}:/home/consumer\""),
                operationName: $"restore files into VM {vmId}");

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["volume_sync_status"] = "Idle",
                ["last_volume_sync_at"] = DateTime.UtcNow
            });
        }
        catch
        {
            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                ["volume_sync_status"] = "Error"
            });
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean temporary restore directory: {TempDir}", tempDir);
            }
        }
    }

    private async Task MonitorVolumeSyncAsync(CancellationToken ct)
    {
        var staleThresholdMinutes = _configuration.GetValue<int?>("Backups:VolumeSyncStaleMinutes") ?? 10;
        var staleThreshold = staleThresholdMinutes > 0
            ? TimeSpan.FromMinutes(staleThresholdMinutes)
            : DefaultStaleThreshold;

        var storageClient = await StorageClient.CreateAsync();

        var snapshot = await _firestoreDb
            .Collection("virtual_machines")
            .WhereEqualTo("status", "Running")
            .GetSnapshotAsync();

        var now = DateTime.UtcNow;

        foreach (var vmDoc in snapshot.Documents)
        {
            var vm = vmDoc.ConvertTo<VirtualMachine>();
            var updates = new Dictionary<string, object>();
            var currentLastSync = vm.LastVolumeSyncAt;

            var latestGcsSync = await TryGetLatestSyncTimestampAsync(storageClient, vm);
            if (latestGcsSync.HasValue && (!currentLastSync.HasValue || latestGcsSync > currentLastSync))
            {
                updates["last_volume_sync_at"] = latestGcsSync.Value;
                currentLastSync = latestGcsSync;

                if (!string.Equals(vm.VolumeSyncStatus, "Syncing", StringComparison.OrdinalIgnoreCase))
                    updates["volume_sync_status"] = "Idle";
            }

            if (string.IsNullOrWhiteSpace(vm.VolumeSyncStatus))
                updates["volume_sync_status"] = "Idle";

            if (currentLastSync.HasValue)
            {
                var age = now - currentLastSync.Value;
                var stale = age > staleThreshold;

                if (stale && !string.Equals(vm.VolumeSyncStatus, "Syncing", StringComparison.OrdinalIgnoreCase))
                {
                    updates["volume_sync_status"] = "Error";
                }
                else if (!stale && !string.Equals(vm.VolumeSyncStatus, "Syncing", StringComparison.OrdinalIgnoreCase))
                {
                    updates["volume_sync_status"] = "Idle";
                }
            }

            if (updates.Count > 0)
                await vmDoc.Reference.UpdateAsync(updates);
        }
    }

    private async Task<DateTime?> TryGetLatestSyncTimestampAsync(StorageClient storageClient, VirtualMachine vm)
    {
        var (bucket, prefix) = GetVmSyncLocation(vm);
        DateTime? latest = null;

        try
        {
            await foreach (var obj in storageClient.ListObjectsAsync(bucket, prefix))
            {
                if (string.IsNullOrEmpty(obj.Name) || obj.Name.EndsWith('/'))
                    continue;

                if (obj.UpdatedDateTimeOffset.HasValue)
                {
                    var updatedUtc = obj.UpdatedDateTimeOffset.Value.UtcDateTime;
                    if (!latest.HasValue || updatedUtc > latest.Value)
                        latest = updatedUtc;
                }
            }
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("GCS bucket or prefix not found for VM {VmId}: {Bucket}/{Prefix}", vm.VmId, bucket, prefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query GCS sync state for VM {VmId}", vm.VmId);
        }

        return latest;
    }

    private (string Bucket, string Prefix) GetVmSyncLocation(VirtualMachine vm)
    {
        if (!string.IsNullOrWhiteSpace(vm.GcsPath))
            return ParseGcsPath(vm.GcsPath, vm);

        var bucket = vm.GcsBucket
            ?? _configuration["Backups:VolumeBucket"]
            ?? "unicore-vm-volumes";

        var consumerContext = string.IsNullOrWhiteSpace(vm.Client) ? "shared" : vm.Client;
        var prefix = $"consumers/{consumerContext}/{vm.VmId}/home";
        return (bucket, prefix);
    }

    private async Task BackupContainerToGcsAsync(VirtualMachine vm)
    {
        var consumerContext = string.IsNullOrEmpty(vm.Client) ? "shared" : vm.Client;
        var gcsPrefix = $"consumers/{consumerContext}/{vm.VmId}/home";
        var bucketName = _configuration["Backups:VolumeBucket"] ?? "unicore-vm-volumes";

        using var dockerClient = await GetDockerClientAsync();
        var archiveResponse = await dockerClient.Containers.GetArchiveFromContainerAsync(
            vm.ContainerId,
            new GetArchiveFromContainerParameters { Path = "/home/consumer" },
            statOnly: false);

        var storageClient = await StorageClient.CreateAsync();

        try
        {
            await storageClient.GetBucketAsync(bucketName);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await storageClient.CreateBucketAsync(_firestoreDb.ProjectId, bucketName);
        }

        using var tarBuffer = new MemoryStream();
        await using (var rawStream = archiveResponse.Stream)
        {
            await rawStream.CopyToAsync(tarBuffer);
        }

        tarBuffer.Position = 0;
        var tarReader = new TarReader(tarBuffer);

        while (await tarReader.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null)
                continue;

            var objectName = $"{gcsPrefix}/{entry.Name}";
            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer);
            buffer.Position = 0;
            await storageClient.UploadObjectAsync(bucketName, objectName, null, buffer);
        }
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

    private (string Bucket, string Prefix) ParseGcsPath(string gcsPath, VirtualMachine vm)
    {
        if (gcsPath.StartsWith("gs://", StringComparison.OrdinalIgnoreCase))
        {
            var pathWithoutScheme = gcsPath[5..];
            var slashIndex = pathWithoutScheme.IndexOf('/');
            if (slashIndex < 0)
                return (pathWithoutScheme, string.Empty);

            var bucket = pathWithoutScheme[..slashIndex];
            var prefix = pathWithoutScheme[(slashIndex + 1)..].Trim('/');
            return (bucket, prefix);
        }

        var bucketName = vm.GcsBucket
            ?? _configuration["Backups:VolumeBucket"]
            ?? "unicore-vm-volumes";

        return (bucketName, gcsPath.Trim('/'));
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdOutTask;
        var stderr = await stdErrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Process '{fileName} {arguments}' failed ({process.ExitCode}): {stderr}{stdout}");
    }

    private async Task<DockerClient> GetDockerClientAsync()
    {
        var endpoints = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? new[] { new Uri("npipe://./pipe/docker_engine"), new Uri("tcp://127.0.0.1:2375") }
            : new[] { new Uri("unix:///var/run/docker.sock") };

        foreach (var uri in endpoints)
        {
            try
            {
                var client = new DockerClientConfiguration(uri).CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.System.PingAsync(cts.Token);
                return client;
            }
            catch
            {
            }
        }

        throw new InvalidOperationException("Docker daemon is not reachable.");
    }
}
