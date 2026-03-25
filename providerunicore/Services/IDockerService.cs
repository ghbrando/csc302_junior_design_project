using Docker.DotNet.Models;

namespace unicoreprovider.Services;

public interface IDockerService
{
    /// <summary>
    /// Returns true if a Docker daemon is reachable on any known endpoint
    /// (Windows named pipe or TCP localhost:2375).
    /// </summary>
    Task<bool> IsReachableAsync();

    /// <summary>
    /// Pulls <paramref name="image"/> if not present locally, creates a named volume, creates a container with SSH and
    /// the FRP client configured to tunnel SSH through the GCP relay on <paramref name="relayPort"/>,
    /// starts it, and returns a tuple of (container ID, volume name).
    /// Hard Docker limits are applied: <paramref name="cpuCores"/> logical cores and
    /// <paramref name="ramGB"/> GB of RAM.
    /// The volume is mounted to /home/consumer inside the container for persistent storage and GCS sync.
    /// </summary>
    Task<(string ContainerId, string VolumeName)> StartContainerAsync(
        string vmId, string name, string image, int relayPort, int cpuCores, int ramGB,
        string? existingVolumeName = null, string? consumerUid = null,
        int? volumeGb = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the host port mapped to SSH (port 22) inside the container.
    /// Returns null if no SSH port mapping is found.
    /// </summary>
    Task<int?> GetContainerSshPortAsync(string containerId);

    /// <summary>
    /// Stops and removes a container by its Docker container ID, and optionally removes its associated volume.
    /// </summary>
    Task StopContainerAsync(string containerId, string vmName, string? volumeName = null);

    /// <summary>
    /// Returns a one-shot CPU% and RAM% snapshot for a running container.
    /// </summary>
    Task<(double CpuPercent, double RamPercent)> GetContainerStatsAsync(string containerId);

    /// <summary>
    /// Pauses a running container without stopping it.
    /// </summary>
    Task PauseContainerAsync(string containerId);

    /// <summary>
    /// Unpauses a paused container.
    /// </summary>
    Task UnpauseContainerAsync(string containerId, string vmId);

    /// <summary>
    /// Creates a named Docker volume for persistent storage.
    /// </summary>
    Task<string> CreateVolumeAsync(string volumeName, int? sizeGb = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a named Docker volume.
    /// </summary>
    Task RemoveVolumeAsync(string volumeName, CancellationToken ct = default);

    /// <summary>
    /// Inspects a named Docker volume and returns its metadata.
    /// </summary>
    Task<VolumeResponse> InspectVolumeAsync(string volumeName, CancellationToken ct = default);

    /// <summary>
    /// Commits container changes as a new image and returns the image ID.
    /// Used for creating snapshots of container state.
    /// </summary>
    Task<string> CommitContainerAsync(string containerId, string repository, string tag, CancellationToken ct = default);

    /// <summary>
    /// Pushes an image to a registry (e.g., GCP Artifact Registry).
    /// Uses the GCP service account credentials loaded at startup.
    /// </summary>
    Task PushImageAsync(string imageTag, CancellationToken ct = default);

    /// <summary>
    /// Pulls an image from a registry (e.g., GCP Artifact Registry).
    /// Uses the GCP service account credentials if available.
    /// </summary>
    Task PullImageAsync(string imageTag, CancellationToken ct = default);
}