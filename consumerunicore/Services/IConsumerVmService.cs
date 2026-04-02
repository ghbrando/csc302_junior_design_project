using UniCore.Shared.Models;

namespace unicoreconsumer.Services;

public interface IConsumerVmService
{
    /// <summary>
    /// Pauses a running VM identified by vmId.
    /// </summary>
    Task<VirtualMachine> PauseVmAsync(string vmId);

    /// <summary>
    /// Resumes a paused VM identified by vmId.
    /// </summary>
    Task<VirtualMachine> ResumeVmAsync(string vmId);

    /// <summary>
    /// Stops a VM identified by vmId.
    /// </summary>
    Task<VirtualMachine> StopVmAsync(string vmId);

    /// <summary>
    /// Deletes a VM identified by vmId from Firestore.
    /// </summary>
    Task DeleteVmAsync(string vmId);
}
