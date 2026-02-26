using System.Collections.Concurrent;
using unicoreprovider.Services;

namespace unicoreprovider.Services;

public class ContainerMonitorService : IHostedService, IDisposable
{
    private const int PollIntervalSeconds = 5;

    private readonly IDockerService _dockerService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContainerMonitorService> _logger;

    // vmId → (containerId, startedAt)
    private readonly ConcurrentDictionary<string, (string ContainerId, DateTime StartedAt)> _monitored = new();

    private Timer? _timer;

    public ContainerMonitorService(
        IDockerService dockerService,
        IServiceScopeFactory scopeFactory,
        ILogger<ContainerMonitorService> logger)
    {
        _dockerService = dockerService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Called by Dashboard when a new container is launched
    public void StartMonitoring(string vmId, string containerId, DateTime startedAt)
    {
        if (_monitored.TryAdd(vmId, (containerId, startedAt)))
            _logger.LogInformation("Started monitoring VM {VmId} (container {ContainerId})", vmId, containerId);
    }

    // Called when a container is stopped
    public void StopMonitoring(string vmId)
    {
        if (_monitored.TryRemove(vmId, out _))
            _logger.LogInformation("Stopped monitoring VM {VmId}", vmId);
    }

    // IHostedService — starts the background timer
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(
            callback: _ => _ = PollAllAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(PollIntervalSeconds),
            period: TimeSpan.FromSeconds(PollIntervalSeconds));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async Task PollAllAsync()
    {
        foreach (var (vmId, (containerId, startedAt)) in _monitored.ToList())
        {
            try
            {
                var (cpu, ram) = await _dockerService.GetContainerStatsAsync(containerId);
                var uptime = DateTime.UtcNow - startedAt;
                var uptimeStr = uptime.ToString(@"hh\:mm\:ss");

                // Create a short-lived scope to resolve the scoped IVmService
                await using var scope = _scopeFactory.CreateAsyncScope();
                var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
                await vmService.UpdateVmMetricsAsync(vmId, cpu, 0, ram, uptimeStr);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("no such container", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("VM {VmId} no longer exists in Firestore; removing from monitor.", vmId);
                    StopMonitoring(vmId);
                }
                else
                {
                    _logger.LogWarning("Error polling container {ContainerId} for VM {VmId}: {Message}",
                        containerId, vmId, ex.Message);
                }
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
