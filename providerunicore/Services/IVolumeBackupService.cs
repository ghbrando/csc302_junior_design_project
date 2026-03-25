namespace unicoreprovider.Services;

public interface IVolumeBackupService
{
    /// <summary>
    /// Triggers an immediate volume backup (gsutil sync) for the specified VM.
    /// Updates volume_sync_status in Firestore to track progress.
    /// </summary>
    Task BackupVolumeNowAsync(string vmId);

    /// <summary>
    /// Restores the consumer's home directory from GCS into the specified Docker volume.
    /// Used during VM migration to recreate data on the new provider.
    /// No-ops gracefully if no backup exists for the source VM.
    /// </summary>
    Task RestoreFromGcsAsync(string sourceVmId, string consumerUid, string targetVolumeName, CancellationToken ct = default);
}
