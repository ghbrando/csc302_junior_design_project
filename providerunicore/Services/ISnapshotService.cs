namespace unicoreprovider.Services;

public interface ISnapshotService
{
    /// <summary>
    /// Pulls a snapshot image from Artifact Registry into the local Docker daemon.
    /// Uses the GCP service account key stored in the GCP_SERVICE_ACCOUNT_KEY environment variable.
    /// No-ops gracefully if imageTag is null or empty.
    /// </summary>
    Task PullSnapshotAsync(string? imageTag, CancellationToken ct = default);

    /// <summary>
    /// Commits the running container as a local Docker image and updates the VM's
    /// SnapshotImage, LastSnapshotAt, and SnapshotStatus fields in Firestore.
    /// The image is tagged as {vmId}:snapshot-{timestamp} and kept locally
    /// (no registry push — useful for single-machine / dev testing).
    /// </summary>
    Task TakeSnapshotAsync(string vmId, string containerId, CancellationToken ct = default);
}
