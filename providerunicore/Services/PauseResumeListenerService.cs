using Google.Cloud.Firestore;
using System.Collections.Concurrent;
using UniCore.Shared.Models;

namespace unicoreprovider.Services;

/// <summary>
/// Listens for pause/resume state changes on VMs and reacts by calling Docker pause/unpause commands.
/// </summary>
public class PauseResumeListenerService : IDisposable
{
    private readonly FirestoreDb _firestoreDb;
    private readonly IDockerService _dockerService;
    private readonly ILogger<PauseResumeListenerService> _logger;

    // vmId → (containerId, isPaused)
    private readonly ConcurrentDictionary<string, (string ContainerId, bool IsPaused)> _vmState = new();

    private FirestoreChangeListener? _listener;

    public PauseResumeListenerService(
        FirestoreDb firestoreDb,
        IDockerService dockerService,
        ILogger<PauseResumeListenerService> logger)
    {
        _firestoreDb = firestoreDb ?? throw new ArgumentNullException(nameof(firestoreDb));
        _dockerService = dockerService ?? throw new ArgumentNullException(nameof(dockerService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts listening to VMs belonging to a specific provider for pause/resume changes.
    /// </summary>
    public void StartListening(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID cannot be empty", nameof(providerId));

        if (_listener != null)
        {
            _logger.LogWarning("Listener already started. Stopping old listener first.");
            _ = StopListening();
        }

        var query = _firestoreDb
            .Collection("virtual_machines")
            .WhereEqualTo("providerId", providerId);

        _listener = query.Listen(OnVmSnapshot);
        _logger.LogInformation("Started listening for pause/resume changes on provider {ProviderId}", providerId);
    }

    /// <summary>
    /// Stops listening for changes.
    /// </summary>
    public async Task StopListening()
    {
        if (_listener != null)
        {
            await _listener.StopAsync();
            _listener = null;
            _vmState.Clear();
            _logger.LogInformation("Stopped listening for pause/resume changes");
        }
    }

    private void OnVmSnapshot(QuerySnapshot snapshot)
    {
        if (snapshot == null) return;

        foreach (var doc in snapshot.Documents)
        {
            try
            {
                var vm = doc.ConvertTo<VirtualMachine>();

                if (string.IsNullOrWhiteSpace(vm.ContainerId))
                {
                    _logger.LogDebug("VM {VmId} has no container ID, skipping", vm.VmId);
                    continue;
                }

                // Track state and detect changes
                var newState = (vm.ContainerId, vm.IsPaused);

                if (_vmState.TryGetValue(vm.VmId, out var oldState))
                {
                    // State change detected
                    if (oldState.IsPaused != newState.IsPaused)
                    {
                        _logger.LogInformation(
                            "Pause state changed for VM {VmId}: {OldState} -> {NewState}",
                            vm.VmId,
                            oldState.IsPaused ? "PAUSED" : "RUNNING",
                            newState.IsPaused ? "PAUSED" : "RUNNING");

                        _ = HandlePauseStateChangeAsync(vm.VmId, vm.ContainerId, newState.IsPaused);
                    }

                    _vmState[vm.VmId] = newState;
                }
                else
                {
                    // First time seeing this VM
                    _vmState[vm.VmId] = newState;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error processing VM snapshot: {Message}", ex.Message);
            }
        }
    }

    private async Task HandlePauseStateChangeAsync(string vmId, string containerId, bool isPaused)
    {
        try
        {
            if (isPaused)
            {
                _logger.LogInformation("Pausing Docker container {ContainerId} for VM {VmId}", containerId, vmId);
                await _dockerService.PauseContainerAsync(containerId);
            }
            else
            {
                _logger.LogInformation("Unpausing Docker container {ContainerId} for VM {VmId}", containerId, vmId);
                await _dockerService.UnpauseContainerAsync(containerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to {Action} Docker container {ContainerId} for VM {VmId}: {Message}",
                isPaused ? "pause" : "unpause",
                containerId,
                vmId,
                ex.Message);
        }
    }

    public void Dispose()
    {
        _ = StopListening();
    }
}
