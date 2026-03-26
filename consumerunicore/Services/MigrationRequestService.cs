using Google.Cloud.Firestore;
using UniCore.Shared.Models;

namespace consumerunicore.Services;

public class MigrationRequestService : IMigrationRequestService
{
    private readonly IFirestoreRepository<VirtualMachine> _vmRepo;
    private readonly IFirestoreRepository<VmMigrationRequest> _migrationRepo;
    private readonly IFirestoreRepository<Provider> _providerRepo;
    private readonly FirestoreDb _firestoreDb;

    public MigrationRequestService(
        IFirestoreRepository<VirtualMachine> vmRepo,
        IFirestoreRepository<VmMigrationRequest> migrationRepo,
        IFirestoreRepository<Provider> providerRepo,
        FirestoreDb firestoreDb)
    {
        _vmRepo = vmRepo ?? throw new ArgumentNullException(nameof(vmRepo));
        _migrationRepo = migrationRepo ?? throw new ArgumentNullException(nameof(migrationRepo));
        _providerRepo = providerRepo ?? throw new ArgumentNullException(nameof(providerRepo));
        _firestoreDb = firestoreDb ?? throw new ArgumentNullException(nameof(firestoreDb));
    }

    public async Task<VmMigrationRequest> RequestMigrationAsync(string vmId, string consumerUid, string? targetProviderUid = null)
    {
        var vm = await _vmRepo.GetByIdAsync(vmId)
            ?? throw new InvalidOperationException($"VM {vmId} not found.");

        if (vm.MigrationStatus is "Restoring" or "Requested")
            throw new InvalidOperationException("A migration is already in progress for this VM.");

        // Find target provider
        if (string.IsNullOrEmpty(targetProviderUid))
        {
            var target = await AutoSelectTargetAsync(vm.ProviderId);
            targetProviderUid = target.FirebaseUid;
        }

        if (targetProviderUid == vm.ProviderId)
            throw new InvalidOperationException("Target provider cannot be the same as the source provider.");

        // Create migration request
        var requestId = Guid.NewGuid().ToString();
        var request = new VmMigrationRequest
        {
            MigrationRequestId = requestId,
            VmId = vmId,
            ConsumerUid = consumerUid,
            SourceProviderUid = vm.ProviderId,
            TargetProviderUid = targetProviderUid,
            Status = "pending",
            RequestedAt = DateTime.UtcNow,
        };

        await _firestoreDb
            .Collection("vm_migration_requests")
            .Document(requestId)
            .SetAsync(request);

        // Mark VM as migration requested
        await _firestoreDb
            .Collection("virtual_machines")
            .Document(vmId)
            .UpdateAsync("migration_status", "Requested");

        return request;
    }

    public async Task<VmMigrationRequest?> GetActiveMigrationAsync(string vmId)
    {
        var snapshot = await _firestoreDb
            .Collection("vm_migration_requests")
            .WhereEqualTo("vm_id", vmId)
            .OrderByDescending("requested_at")
            .Limit(1)
            .GetSnapshotAsync();

        if (snapshot.Documents.Count == 0)
            return null;

        return snapshot.Documents[0].ConvertTo<VmMigrationRequest>();
    }

    public async Task<IEnumerable<Provider>> GetAvailableTargetProvidersAsync(string sourceProviderUid)
    {
        var snapshot = await _providerRepo.CreateQuery()
            .WhereEqualTo("node_status", "Online")
            .GetSnapshotAsync();

        return snapshot.Documents
            .Select(doc => doc.ConvertTo<Provider>())
            .Where(p => p.FirebaseUid != sourceProviderUid)
            .OrderByDescending(p => p.ConsistencyScore);
    }

    /// <summary>
    /// Auto-selects the healthiest online provider, excluding the source.
    /// </summary>
    private async Task<Provider> AutoSelectTargetAsync(string sourceProviderUid)
    {
        var candidates = await GetAvailableTargetProvidersAsync(sourceProviderUid);
        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("No available target providers found.");
    }
}
