namespace unicoreprovider.Services;

public interface ISnapshotService
{
    /// <summary>
    /// Queues an on-demand snapshot for a VM.
    /// </summary>
    Task TriggerSnapshotAsync(string vmId);

    /// <summary>
    /// Pulls a snapshot image from Artifact Registry into the local Docker daemon.
    /// Uses the GCP service account key stored in the GCP_SERVICE_ACCOUNT_KEY environment variable.
    /// No-ops gracefully if imageTag is null or empty.
    /// </summary>
    Task PullSnapshotAsync(string? imageTag, CancellationToken ct = default);
}
