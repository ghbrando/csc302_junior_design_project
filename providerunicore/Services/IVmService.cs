using unicoreprovider.Models;

namespace unicoreprovider.Services;

public interface IVmService
{
    Task<VirtualMachine?> GetByIdAsync(string vmId);
    Task<IEnumerable<VirtualMachine>> GetAllVmsAsync();
    Task<IEnumerable<VirtualMachine>> GetVmsByStatusAsync(string status);
    Task<VirtualMachine> CreateVmAsync(VirtualMachine vm);
    Task<VirtualMachine> UpdateVmMetricsAsync(string vmId, decimal cpu, decimal gpu, decimal ram);
    Task DeleteVmAsync(string vmId);
}
