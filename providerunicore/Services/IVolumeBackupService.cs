namespace unicoreprovider.Services;

public interface IVolumeBackupService
{
    /// <summary>
    /// Triggers an immediate volume backup (gsutil sync) for the specified VM.
    /// Updates volume_sync_status in Firestore to track progress.
    /// </summary>
    Task BackupVolumeNowAsync(string vmId);
}
