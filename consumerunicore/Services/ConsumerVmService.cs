using UniCore.Shared.Models;
using UniCore.Shared.Repositories;

namespace unicoreconsumer.Services;

public class ConsumerVmService : IConsumerVmService
{
    private readonly IFirestoreRepository<VirtualMachine> _vmRepository;

    public ConsumerVmService(IFirestoreRepository<VirtualMachine> vmRepository)
    {
        _vmRepository = vmRepository ?? throw new ArgumentNullException(nameof(vmRepository));
    }

    public async Task<VirtualMachine> PauseVmAsync(string vmId)
    {
        var vm = await _vmRepository.GetByIdAsync(vmId)
            ?? throw new Exception($"VM {vmId} not found");

        vm.IsPaused = true;
        vm.ResumeSuccess = false; // Reset resume success flag on new pause
        await _vmRepository.UpdateAsync(vmId, vm);

        var updatedVm = await _vmRepository.GetByIdAsync(vmId)
            ?? throw new Exception("Failed to retrieve updated VM");

        return updatedVm;
    }

    public async Task<VirtualMachine> ResumeVmAsync(string vmId)
    {
        var vm = await _vmRepository.GetByIdAsync(vmId)
            ?? throw new Exception($"VM {vmId} not found");

        vm.IsPaused = false;
        await _vmRepository.UpdateAsync(vmId, vm);

        var updatedVm = await _vmRepository.GetByIdAsync(vmId)
            ?? throw new Exception("Failed to retrieve updated VM");

        return updatedVm;
    }

    public async Task<VirtualMachine> StopVmAsync(string vmId)
    {
        var vm = await _vmRepository.GetByIdAsync(vmId)
            ?? throw new Exception($"VM {vmId} not found");

        vm.Status = "Stopped";
        vm.IsPaused = false;
        await _vmRepository.UpdateAsync(vmId, vm);

        var updatedVm = await _vmRepository.GetByIdAsync(vmId)
            ?? throw new Exception("Failed to retrieve updated VM");

        return updatedVm;
    }
}
