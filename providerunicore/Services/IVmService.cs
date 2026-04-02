using Google.Cloud.Firestore;

namespace unicoreprovider.Services;

public interface IVmService
{
    Task<VirtualMachine?> GetByIdAsync(string vmId);
    Task<IEnumerable<VirtualMachine>> GetAllVmsAsync();
    Task<IEnumerable<VirtualMachine>> GetVmsByProviderIdAsync(string providerId);
    Task<IEnumerable<VirtualMachine>> GetVmsByStatusAsync(string status);
    Task<VirtualMachine> CreateVmAsync(VirtualMachine vm);
    Task UpdateResumedFlag(string vmID);
    Task DecrementVmConsecutiveFailedConnectionsAsync(string vmId, int decrementBy);
    Task<VirtualMachine> UpdateVmMetricsAsync(string vmId, double cpu, double gpu, double ram, string? uptimeString = null);
    Task DeleteVmAsync(string vmId);
    Task UpdateVmStatusAsync(string vmId, string status);
    FirestoreChangeListener ListenAllVms(Action<IEnumerable<VirtualMachine>> onChanged);
}
