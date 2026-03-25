namespace unicoreprovider.Services;

public interface ISnapshotService
{
    /// <summary>
    /// Queues an on-demand snapshot for a VM.
    /// </summary>
    Task TriggerSnapshotAsync(string vmId);

    /// <summary>
    /// Pulls a snapshot image from Artifact Registry.
    /// </summary>
    Task PullSnapshotAsync(string imageTag, CancellationToken ct = default);
}
