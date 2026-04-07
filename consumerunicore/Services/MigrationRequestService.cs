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

    public async Task<VmMigrationRequest> RequestMigrationAsync(string vmId, string consumerUid, string? targetProviderUid = null, int? requestedCpuCores = null, int? requestedRamGb = null)
    {
        var vm = await _vmRepo.GetByIdAsync(vmId)
            ?? throw new InvalidOperationException($"VM {vmId} not found.");

        if (vm.MigrationStatus is "Restoring" or "Requested")
            throw new InvalidOperationException("A migration is already in progress for this VM.");

        int effectiveCPU = requestedCpuCores ?? vm.CpuCores;
        int effectiveRAM = requestedRamGb ?? vm.RamGB;

        if (effectiveCPU <= 0 || effectiveRAM <= 0)
            throw new InvalidOperationException("Requested CPU cores and RAM must be greater than zero.");

        if (effectiveCPU < vm.CpuCores || effectiveRAM < vm.RamGB)
            throw new InvalidOperationException("Requested resources cannot be less than the current VM specs.");

        // Find target provider
        Provider targetProvider;
        if (string.IsNullOrEmpty(targetProviderUid))
        {
            targetProvider = await AutoSelectTargetAsync(vm.ProviderId, effectiveCPU, effectiveRAM);
            targetProviderUid = targetProvider.FirebaseUid;
        }
        else
        {
            targetProvider = await GetProviderByUidAsync(targetProviderUid)
                ?? throw new InvalidOperationException($"Target provider '{targetProviderUid}' was not found.");

            if (!string.Equals(targetProvider.NodeStatus, "Online", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Target provider is not online.");

            if (!await HasRequiredCapacityAsync(targetProvider, effectiveCPU, effectiveRAM))
            {
                throw new InvalidOperationException(
                    $"Target provider does not have enough available resources for {effectiveCPU} vCPU and {effectiveRAM} GB RAM.");
            }
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
            RequestedCpuCores = requestedCpuCores,
            RequestedRamGb = requestedRamGb,
            EffectiveCpuCores = effectiveCPU,
            EffectiveRamGb = effectiveRAM
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
    private async Task<Provider> AutoSelectTargetAsync(string sourceProviderUid, int requiredCpu, int requiredRam)
    {
        var candidates = await GetCapacityQualifiedTargetProvidersAsync(sourceProviderUid, requiredCpu, requiredRam);
        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("No available target providers found.");
    }

    private async Task<IEnumerable<Provider>> GetCapacityQualifiedTargetProvidersAsync(
        string sourceProviderUid,
        int requiredCpu,
        int requiredRam)
    {
        var online = await GetAvailableTargetProvidersAsync(sourceProviderUid);
        var providers = online.ToList();

        var checks = await Task.WhenAll(providers.Select(async provider => new
        {
            Provider = provider,
            HasCapacity = await HasRequiredCapacityAsync(provider, requiredCpu, requiredRam)
        }));

        return checks
            .Where(c => c.HasCapacity)
            .Select(c => c.Provider)
            .OrderByDescending(p => p.ConsistencyScore);
    }

    private async Task<bool> HasRequiredCapacityAsync(Provider provider, int requiredCpu, int requiredRam)
    {
        var specsSnapshot = await _firestoreDb
            .Collection("machine_specs")
            .Document(provider.FirebaseUid)
            .GetSnapshotAsync();

        if (!specsSnapshot.Exists)
            return false;

        var specs = specsSnapshot.ConvertTo<MachineSpecs>();
        var providerVms = await _vmRepo.WhereAsync("providerId", provider.FirebaseUid);

        var runningVms = providerVms
            .Where(vm => string.Equals(vm.Status, "Running", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var cpuBudget = (int)Math.Floor(specs.CpuCores * (provider.CpuLimitPercent / 100.0));
        var availableCpu = cpuBudget - runningVms.Sum(vm => vm.CpuCores);
        var availableRam = provider.RamLimitGB - runningVms.Sum(vm => (double)vm.RamGB);

        return availableCpu >= requiredCpu && availableRam >= requiredRam;
    }

    private async Task<Provider?> GetProviderByUidAsync(string providerUid)
    {
        var byId = await _providerRepo.GetByIdAsync(providerUid);
        if (byId != null)
            return byId;

        var query = await _providerRepo.CreateQuery()
            .WhereEqualTo("firebase_uid", providerUid)
            .Limit(1)
            .GetSnapshotAsync();

        return query.Documents.Count == 0
            ? null
            : query.Documents[0].ConvertTo<Provider>();
    }
}
