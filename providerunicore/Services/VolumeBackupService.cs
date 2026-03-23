using Docker.DotNet;
using Docker.DotNet.Models;
using Google.Cloud.Firestore;
using Google.Cloud.Storage.V1;
using System.Formats.Tar;

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
            catch { }
        }

        throw new InvalidOperationException("Docker daemon is not reachable.");
    }
}
