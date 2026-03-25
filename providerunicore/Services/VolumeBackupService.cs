using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using System.Formats.Tar;
using System.Runtime.InteropServices;

namespace unicoreprovider.Services;

public class VolumeBackupService : IVolumeBackupService
{
    private readonly FirestoreDb _firestoreDb;

    public VolumeBackupService(FirestoreDb firestoreDb)
    {
        _firestoreDb = firestoreDb;
    }

    public async Task BackupVolumeNowAsync(string vmId)
    {
        var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);

        // Use a transaction to atomically check status and claim the backup.
        // This prevents duplicate triggers when the listener fires multiple snapshots.
        VirtualMachine vm = null!;
        await _firestoreDb.RunTransactionAsync(async transaction =>
        {
            var doc = await transaction.GetSnapshotAsync(vmRef);

            if (!doc.Exists)
                throw new InvalidOperationException($"VM {vmId} not found.");

            vm = doc.ConvertTo<VirtualMachine>();

            if (string.IsNullOrEmpty(vm.ContainerId))
                throw new InvalidOperationException("VM has no running container.");

            // Only proceed if status is still "Requested" — reject duplicates
            if (vm.VolumeSyncStatus != "Requested")
                throw new InvalidOperationException("Backup not in Requested state, skipping.");

            transaction.Update(vmRef, new Dictionary<string, object>
            {
                ["volume_sync_status"] = "Syncing"
            });
        });

        try
        {
            var consumerContext = string.IsNullOrEmpty(vm.Client) ? "shared" : vm.Client;
            var gcsPrefix = $"consumers/{consumerContext}/{vmId}/home";
            const string bucketName = "unicore-vm-volumes";

            // Step 1: Pull a tar archive of /home/consumer from the container
            var dockerClient = await GetDockerClientAsync();
            var archiveResponse = await dockerClient.Containers.GetArchiveFromContainerAsync(
                vm.ContainerId,
                new GetArchiveFromContainerParameters { Path = "/home/consumer" },
                statOnly: false);

            // Step 2: Upload each file to GCS
            var storageClient = await StorageClient.CreateAsync();

            // Ensure bucket exists
            try
            {
                await storageClient.GetBucketAsync(bucketName);
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var projectId = _firestoreDb.ProjectId;
                await storageClient.CreateBucketAsync(projectId, bucketName);
            }

            int fileCount = 0;
            // Buffer the entire tar archive into memory first — Docker's chunked
            // HTTP stream causes EndOfStreamException when TarReader reads entry data.
            using var tarBuffer = new MemoryStream();
            await using (var rawStream = archiveResponse.Stream)
            {
                await rawStream.CopyToAsync(tarBuffer);
            }
            tarBuffer.Position = 0;
            var tarReader = new TarReader(tarBuffer);

            while (await tarReader.GetNextEntryAsync() is { } entry)
            {
                // Skip directories and non-file entries
                if (entry.EntryType != TarEntryType.RegularFile)
                    continue;

                // entry.Name is like "consumer/file.txt" (relative to parent of /home/consumer)
                // Map to GCS: consumers/{uid}/{vmId}/home/{path}
                var objectName = $"{gcsPrefix}/{entry.Name}";

                if (entry.DataStream != null)
                {
                    // Buffer into MemoryStream — GCS resumable upload needs a seekable stream
                    using var buffer = new MemoryStream();
                    await entry.DataStream.CopyToAsync(buffer);
                    buffer.Position = 0;

                    await storageClient.UploadObjectAsync(bucketName, objectName, null, buffer);
                    fileCount++;
                }
            }

            // Mark as complete with timestamp
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
            throw new InvalidOperationException($"Backup failed: {ex.Message}", ex);
        }
    }

    public async Task ForceBackupToGcsAsync(VirtualMachine vm, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(vm.ContainerId))
            throw new InvalidOperationException($"VM {vm.VmId} has no running container to back up.");

        var consumerContext = string.IsNullOrEmpty(vm.Client) ? "shared" : vm.Client;
        var gcsPrefix = $"consumers/{consumerContext}/{vm.VmId}/home";
        const string bucketName = "unicore-vm-volumes";

        var dockerClient = await GetDockerClientAsync();
        var archiveResponse = await dockerClient.Containers.GetArchiveFromContainerAsync(
            vm.ContainerId,
            new GetArchiveFromContainerParameters { Path = "/home/consumer" },
            statOnly: false);

        var storageClient = await StorageClient.CreateAsync();

        try { await storageClient.GetBucketAsync(bucketName, cancellationToken: ct); }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await storageClient.CreateBucketAsync(_firestoreDb.ProjectId, bucketName);
        }

        using var tarBuffer = new MemoryStream();
        await using (var rawStream = archiveResponse.Stream)
            await rawStream.CopyToAsync(tarBuffer, ct);
        tarBuffer.Position = 0;

        var tarReader = new TarReader(tarBuffer);
        while (await tarReader.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null)
                continue;

            using var buffer = new MemoryStream();
            await entry.DataStream.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            await storageClient.UploadObjectAsync(bucketName, $"{gcsPrefix}/{entry.Name}", null, buffer, cancellationToken: ct);
        }
    }

    public async Task RestoreFromGcsAsync(string sourceVmId, string consumerUid, string targetVolumeName, CancellationToken ct = default)
    {
        const string bucketName = "unicore-vm-volumes";
        var gcsPrefix = $"consumers/{consumerUid}/{sourceVmId}/home/";

        var storageClient = await StorageClient.CreateAsync();

        // Verify the backup prefix exists before starting Docker work
        bool hasFiles = false;
        await foreach (var obj in storageClient.ListObjectsAsync(bucketName, gcsPrefix).WithCancellation(ct))
        {
            if (!obj.Name.EndsWith("/"))
            {
                hasFiles = true;
                break;
            }
        }

        if (!hasFiles)
            return;

        // Build a tar archive in memory from all GCS objects under the prefix.
        // Entries retain their relative paths (e.g. "consumer/file.txt") so that
        // extracting to /restore in the temp container writes to
        // /restore/consumer/... which is inside the volume (mounted at /restore/consumer).
        using var tarBuffer = new MemoryStream();
        await using (var tarWriter = new TarWriter(tarBuffer, TarEntryFormat.Gnu, leaveOpen: true))
        {
            await foreach (var obj in storageClient.ListObjectsAsync(bucketName, gcsPrefix).WithCancellation(ct))
            {
                if (obj.Name.EndsWith("/")) continue;

                var entryName = obj.Name[gcsPrefix.Length..];
                if (string.IsNullOrEmpty(entryName)) continue;

                using var dataBuffer = new MemoryStream();
                await storageClient.DownloadObjectAsync(bucketName, obj.Name, dataBuffer, cancellationToken: ct);
                dataBuffer.Position = 0;

                var entry = new GnuTarEntry(TarEntryType.RegularFile, entryName)
                {
                    DataStream = dataBuffer
                };
                await tarWriter.WriteEntryAsync(entry, ct);
            }
        }

        if (tarBuffer.Length == 0)
            return;

        tarBuffer.Position = 0;

        // Start a minimal temp container with the target volume mounted at /restore/consumer.
        // Extracting the tar at /restore puts "consumer/file.txt" → /restore/consumer/file.txt
        // which is the volume root — matching the layout expected by the main container.
        var dockerClient = await GetDockerClientAsync();

        await EnsureImagePulledAsync(dockerClient, "alpine", "latest", ct);

        var createResponse = await dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = "alpine",
                Cmd = new List<string> { "sleep", "3600" },
                HostConfig = new HostConfig
                {
                    Binds = new[] { $"{targetVolumeName}:/restore/consumer" }
                }
            }, ct);

        var tempId = createResponse.ID;
        try
        {
            await dockerClient.Containers.StartContainerAsync(tempId, new ContainerStartParameters(), ct);

            await dockerClient.Containers.ExtractArchiveToContainerAsync(
                tempId,
                new ContainerPathStatParameters { Path = "/restore" },
                tarBuffer,
                ct);
        }
        finally
        {
            try { await dockerClient.Containers.StopContainerAsync(tempId, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }); } catch { }
            try { await dockerClient.Containers.RemoveContainerAsync(tempId, new ContainerRemoveParameters { Force = true }); } catch { }
        }
    }

    private static async Task EnsureImagePulledAsync(DockerClient client, string image, string tag, CancellationToken ct)
    {
        var existing = await client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [$"{image}:{tag}"] = true }
            }
        });

        if (existing.Count > 0) return;

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image, Tag = tag },
            null,
            new Progress<JSONMessage>(),
            ct);
    }

    private async Task<DockerClient> GetDockerClientAsync()
    {
        var endpoints = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
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
            catch { }
        }

        throw new InvalidOperationException("Docker daemon is not reachable.");
    }
}
