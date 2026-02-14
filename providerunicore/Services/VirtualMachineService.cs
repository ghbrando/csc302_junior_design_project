using unicoreprovider.Models;
using providerunicore.Repositories;

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

    public async Task<VirtualMachine> UpdateVmMetricsAsync(string vmId, decimal cpu, decimal gpu, decimal ram)
    {
        var vm = await _repository.GetByIdAsync(vmId);

        if (vm == null)
            throw new Exception($"VM {vmId} not found");

        // Update current metrics
        vm.CurrentCpuUsage = cpu;
        vm.CurrentGpuUsage = gpu;
        vm.CurrentRamUsage = ram;

        // Update history (keep last 20 data points)
        vm.CpuHistory.Add((double)cpu);
        if (vm.CpuHistory.Count > 20) vm.CpuHistory.RemoveAt(0);

        vm.GpuHistory.Add((double)gpu);
        if (vm.GpuHistory.Count > 20) vm.GpuHistory.RemoveAt(0);

        vm.RamHistory.Add((double)ram);
        if (vm.RamHistory.Count > 20) vm.RamHistory.RemoveAt(0);

        await _repository.UpdateAsync(vmId, vm);
        return vm;
    }

    public async Task DeleteVmAsync(string vmId)
    {
        await _repository.DeleteAsync(vmId);
    }
}
