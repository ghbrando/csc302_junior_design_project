namespace unicoreprovider.Services;

public interface IMigrationService
{
    /// <summary>
    /// Executes the full 11-step migration state machine for the given request.
    /// Updates VmMigrationRequest and VirtualMachine documents throughout.
    /// On failure, cleans up any partial state and marks the request as "failed".
    /// </summary>
    Task ProcessMigrationRequestAsync(VmMigrationRequest request);

    /// <summary>
    /// Returns true if a migration for the given request ID is currently in progress.
    /// Used by the Dashboard listener to prevent duplicate processing.
    /// </summary>
    bool IsMigrationInProgress(string migrationRequestId);
}
