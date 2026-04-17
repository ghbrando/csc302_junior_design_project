namespace unicoreprovider.Services;

public interface IMigrationService
{
    /// <summary>
    /// Source-side preparation: snapshots the running container, backs up user data
    /// to GCS, stops the old container, then sets the request status to
    /// "ready_for_restore" so the target provider can pick it up.
    /// Must run on the source provider (which has local Docker access to the container).
    /// </summary>
    Task PrepareSourceForMigrationAsync(VmMigrationRequest request);

    /// <summary>
    /// Target-side restore: pulls the snapshot image, creates a new volume, restores
    /// user data from GCS, starts a new container, and wires up monitoring.
    /// Expects the request status to already be "ready_for_restore".
    /// </summary>
    Task ProcessMigrationRequestAsync(VmMigrationRequest request);

    /// <summary>
    /// Returns true if a migration for the given request ID is currently in progress.
    /// Used by the Dashboard listener to prevent duplicate processing.
    /// </summary>
    bool IsMigrationInProgress(string migrationRequestId);
}
