using Google.Cloud.Firestore;

namespace unicoreprovider.Services;

public class VirtualMachineService : IVmService
{
    private readonly IFirestoreRepository<VirtualMachine> _repository;

    public VirtualMachineService(IFirestoreRepository<VirtualMachine> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<VirtualMachine?> GetByIdAsync(string vmId)
    {
        return await _repository.GetByIdAsync(vmId);
    }

    public async Task<IEnumerable<VirtualMachine>> GetAllVmsAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<IEnumerable<VirtualMachine>> GetVmsByProviderIdAsync(string providerId)
    {
        return await _repository.WhereAsync("providerId", providerId);
    }

    public async Task<IEnumerable<VirtualMachine>> GetVmsByStatusAsync(string status)
    {
        return await _repository.WhereAsync("status", status);
    }

    public async Task<VirtualMachine> CreateVmAsync(VirtualMachine vm)
    {
        if (string.IsNullOrWhiteSpace(vm.VmId))
            vm.VmId = Guid.NewGuid().ToString();

        // CreateAsync uses documentIdSelector (VmId) automatically
        await _repository.CreateAsync(vm);
        return vm;
    }

    public async Task<VirtualMachine> UpdateVmMetricsAsync(string vmId, double cpu, double gpu, double ram, string? uptimeString = null)
    {
        var vm = await _repository.GetByIdAsync(vmId);

        if (vm == null)
            throw new Exception($"VM {vmId} not found");

        // Update current metrics
        vm.CurrentCpuUsage = cpu;
        vm.CurrentGpuUsage = gpu;
        vm.CurrentRamUsage = ram;

        if (uptimeString != null)
            vm.UptimeString = uptimeString;

        // Update history (keep last 20 data points)
        const int maxHistory = 20;
        if (vm.CpuHistory.Count >= maxHistory) vm.CpuHistory.RemoveAt(0);
        vm.CpuHistory.Add(cpu);

        if (vm.GpuHistory.Count >= maxHistory) vm.GpuHistory.RemoveAt(0);
        vm.GpuHistory.Add(gpu);

        if (vm.RamHistory.Count >= maxHistory) vm.RamHistory.RemoveAt(0);
        vm.RamHistory.Add(ram);

        await _repository.UpdateAsync(vmId, vm);
        return vm;
    }

    public async Task DeleteVmAsync(string vmId)
    {
        await _repository.DeleteAsync(vmId);
    }

    public async Task DecrementVmConsecutiveFailedConnectionsAsync(string vmId, int decrementBy)
    {
        var vm = await _repository.GetByIdAsync(vmId);

        if (vm == null)
            throw new Exception($"VM {vmId} not found");

        if (vm.ConsecutiveMisses >= decrementBy)
        {
            vm.ConsecutiveMisses -= decrementBy;
            await _repository.UpdateAsync(vmId, vm);
        }
    }

    public async Task UpdateResumedFlag(string vmID)
    {
        var vm = await _repository.GetByIdAsync(vmID);

        if (vm == null)
            throw new Exception($"VM {vmID} not found");

        vm.ResumeSuccess = true;
        await _repository.UpdateAsync(vmID, vm);
    }

    public async Task UpdateVmStatusAsync(string vmId, string status)
    {
        var vm = await _repository.GetByIdAsync(vmId);
        if (vm == null) return;
        vm.Status = status;
        await _repository.UpdateAsync(vmId, vm);
    }

    // Listen for real-time changes to all VMs
    public FirestoreChangeListener ListenAllVms(Action<IEnumerable<VirtualMachine>> onChanged)
    {
        return _repository.ListenAll(onChanged);
    }

}
