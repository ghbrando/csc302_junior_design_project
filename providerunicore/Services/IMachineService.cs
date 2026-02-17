using unicoreprovider.Models;

namespace unicoreprovider.Services;

public interface IMachineService
{
    /// <summary>
    /// Detects the host machine's hardware specs, persists them to Firestore, and returns the result.
    /// </summary>
    Task<MachineSpecs> GetSpecsAsync();

    /// <summary>
    /// Returns the previously stored specs from Firestore without re-detecting hardware.
    /// Returns null if no specs have been detected yet.
    /// </summary>
    Task<MachineSpecs?> GetCachedSpecsAsync();
}
