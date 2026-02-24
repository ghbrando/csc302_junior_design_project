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

    // Prevents overlapping poll cycles when a tick fires before the previous one finishes
    private readonly SemaphoreSlim _pollGate = new(1, 1);

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
        _monitored.TryAdd(vmId, (containerId, startedAt));
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
        // Skip this tick entirely if the previous poll cycle hasn't finished yet
        if (!await _pollGate.WaitAsync(0))
            return;

        try
        {
            foreach (var (vmId, (containerId, startedAt)) in _monitored.ToList())
            {
                try
                {
                    var (cpu, ram) = await _dockerService.GetContainerStatsAsync(containerId);
                    var uptime = DateTime.UtcNow - startedAt;
                    var uptimeStr = uptime.ToString(@"hh\:mm\:ss");

                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
                    await vmService.UpdateVmMetricsAsync(vmId, cpu, 0, ram, uptimeStr);
                }
                catch (Docker.DotNet.DockerContainerNotFoundException)
                {
                    _logger.LogInformation(
                        "Container {ContainerId} for VM {VmId} no longer exists; sending zero metrics and stopping monitor",
                        containerId, vmId);

                    StopMonitoring(vmId);

                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
                    await vmService.UpdateVmMetricsAsync(vmId, 0, 0, 0, "00:00:00");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error polling container {ContainerId} for VM {VmId}: {Message}",
                        containerId, vmId, ex.Message);
                }
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public void Dispose() => _timer?.Dispose();
}
