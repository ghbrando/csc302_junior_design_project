using consumerunicore.Models;
using Google.Cloud.Firestore;

namespace consumerunicore.Services
{
    public interface IMatchmakingService
    {
        /// <summary>
        /// Finds the best available provider for the given resource request.
        /// Returns null if no qualifying provider exists.
        /// </summary>
        Task<MatchmakingResult?> FindBestMatchAsync(MatchmakingRequest request);
    }

    public class MatchmakingService : IMatchmakingService
    {
        private readonly IFirestoreRepository<Provider> _providerRepo;
        private readonly IFirestoreRepository<MachineSpecs> _machineSpecsRepo;
        private readonly IFirestoreRepository<VirtualMachine> _vmRepo;

        public MatchmakingService(
            IFirestoreRepository<Provider> providerRepo,
            IFirestoreRepository<MachineSpecs> machineSpecsRepo,
            IFirestoreRepository<VirtualMachine> vmRepo)
        {
            _providerRepo     = providerRepo     ?? throw new ArgumentNullException(nameof(providerRepo));
            _machineSpecsRepo = machineSpecsRepo ?? throw new ArgumentNullException(nameof(machineSpecsRepo));
            _vmRepo           = vmRepo           ?? throw new ArgumentNullException(nameof(vmRepo));
        }

        public async Task<MatchmakingResult?> FindBestMatchAsync(MatchmakingRequest request)
        {
            // ----------------------------------------------------------------
            // STEP 1 + 2: Single Firestore round-trip — filter Online + region.
            // CreateQuery() chains both WhereEqualTo filters before executing.
            // ----------------------------------------------------------------
            var onlineInRegion = await FetchOnlineProvidersInRegionAsync(request.Region);

            if (!onlineInRegion.Any())
                return null;

            // ----------------------------------------------------------------
            // STEP 3: Evaluate each candidate in parallel (fan-out).
            // Each evaluation fetches machine_specs + running VMs and computes
            // available resources. Returns null if provider doesn't qualify.
            // ----------------------------------------------------------------
            var evaluations = await Task.WhenAll(
                onlineInRegion.Select(p => EvaluateCandidateAsync(p, request)));

            // ----------------------------------------------------------------
            // STEP 4: Sort qualifying candidates by consistency_score DESC.
            // Absent scores are null-coalesced to 100.0.
            // STEP 5: Return the top result (greedy first-match by score).
            // ----------------------------------------------------------------
            return evaluations
                .Where(e => e != null)
                .OrderByDescending(e => e!.ConsistencyScore)
                .FirstOrDefault();
        }

        // ====================================================================
        // Private helpers
        // ====================================================================

        /// <summary>
        /// Chains two Firestore equality filters in a single network call.
        /// WhereAsync only supports one field at a time, so CreateQuery() is
        /// used to compose both filters before executing GetSnapshotAsync().
        /// </summary>
        private async Task<IEnumerable<Provider>> FetchOnlineProvidersInRegionAsync(string region)
        {
            QuerySnapshot snapshot = await _providerRepo.CreateQuery()
                .WhereEqualTo("node_status", "Online")
                .WhereEqualTo("region", region)
                .GetSnapshotAsync();

            return snapshot.Documents.Select(doc => doc.ConvertTo<Provider>());
        }

        /// <summary>
        /// Fetches machine_specs and running VMs for one provider in parallel,
        /// computes available resources, and returns a MatchmakingResult if the
        /// provider satisfies the request. Returns null if it doesn't qualify.
        /// </summary>
        private async Task<MatchmakingResult?> EvaluateCandidateAsync(
            Provider provider,
            MatchmakingRequest request)
        {
            // Fetch specs and VMs concurrently for this provider
            var specsTask = _machineSpecsRepo.GetByIdAsync(provider.FirebaseUid);
            var vmsTask   = _vmRepo.WhereAsync("providerId", provider.FirebaseUid);

            await Task.WhenAll(specsTask, vmsTask);

            MachineSpecs? specs          = specsTask.Result;
            IEnumerable<VirtualMachine> allVms = vmsTask.Result;

            // No machine_specs document means we can't compute capacity — skip
            if (specs == null)
                return null;

            // Only Running VMs consume resources
            var runningVms = allVms
                .Where(vm => string.Equals(vm.Status, "Running", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // ----------------------------------------------------------------
            // Available resource formula:
            //   available_cpu = floor(cpu_cores * (cpu_limit_percent / 100))
            //                   - sum(running_vm.cpu_cores)
            //   available_ram = ram_limit_gb - sum(running_vm.ram_gb)
            // ----------------------------------------------------------------
            int cpuBudget       = (int)Math.Floor(specs.CpuCores * (provider.CpuLimitPercent / 100.0));
            int availableCpu    = cpuBudget - runningVms.Sum(vm => vm.CpuCores);
            double availableRam = provider.RamLimitGB - runningVms.Sum(vm => (double)vm.RamGB);

            if (availableCpu < request.CpuCoresNeeded || availableRam < request.RamGbNeeded)
                return null;

            return new MatchmakingResult
            {
                Provider          = provider,
                MachineSpecs      = specs,
                AvailableCpuCores = availableCpu,
                AvailableRamGb    = availableRam,
                ConsistencyScore  = provider.ConsistencyScore
            };
        }
    }
}
