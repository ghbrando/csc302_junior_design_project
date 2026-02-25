using Google.Cloud.Firestore;

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

    /// <summary>
    /// Listens for real-time changes to a provider's machine specs document.
    /// </summary>
    FirestoreChangeListener ListenSpecs(string providerId, Action<MachineSpecs?> onChanged);
}
