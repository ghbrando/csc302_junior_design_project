using UniCore.Shared.Models;

namespace consumerunicore.Services;

public interface IMigrationRequestService
{
    /// <summary>
    /// Finds a healthy target provider and creates a VmMigrationRequest in Firestore.
    /// If targetProviderUid is null, auto-selects the healthiest available provider.
    /// Returns the created migration request.
    /// </summary>
    Task<VmMigrationRequest> RequestMigrationAsync(string vmId, string consumerUid, string? targetProviderUid = null, int? requestedCpuCores = null, int? requestedRamGb = null);

    /// <summary>
    /// Returns the current migration request for a VM, or null if none exists.
    /// Looks for the most recent non-failed request.
    /// </summary>
    Task<VmMigrationRequest?> GetActiveMigrationAsync(string vmId);

    /// <summary>
    /// Returns all available target providers for migration (Online, excluding the source provider).
    /// </summary>
    Task<IEnumerable<Provider>> GetAvailableTargetProvidersAsync(string sourceProviderUid);
}
