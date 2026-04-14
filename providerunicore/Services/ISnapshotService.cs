namespace unicoreprovider.Services;

public interface ISnapshotService
{
    /// <summary>
    /// Queues an on-demand snapshot for a VM.
    /// </summary>
    Task TriggerSnapshotAsync(string vmId);

    /// <summary>
    /// Immediately takes a snapshot of a running VM and waits for it to complete.
    /// Use this when the caller needs the snapshot_image to be up-to-date before proceeding
    /// (e.g. migration), as opposed to TriggerSnapshotAsync which only queues the work.
    /// </summary>
    Task SnapshotNowAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Pulls a snapshot image from Artifact Registry into the local Docker daemon.
    /// Uses the GCP service account key stored in the GCP_SERVICE_ACCOUNT_KEY environment variable.
    /// No-ops gracefully if imageTag is null or empty.
    /// </summary>
    Task PullSnapshotAsync(string? imageTag, CancellationToken ct = default);
}
