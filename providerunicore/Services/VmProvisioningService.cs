using System.Collections.Concurrent;
using System.Net.Sockets;

namespace unicoreprovider.Services;

public class VmProvisioningService : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VmProvisioningService> _logger;
    private readonly ContainerMonitorService _monitorService;
    private readonly IDockerService _dockerService;
    private readonly string _relayIp;
    private readonly int _timeoutSeconds;
    private readonly int _pollIntervalSeconds;
    private readonly int _connectTimeoutSeconds;

    // vmId → (ContainerId, RelayPort, StartedAt)
    private readonly ConcurrentDictionary<string, (string ContainerId, int RelayPort, DateTime StartedAt)> _pending = new();

    private Timer? _timer;

    public VmProvisioningService(
        IServiceScopeFactory scopeFactory,
        ILogger<VmProvisioningService> logger,
        ContainerMonitorService monitorService,
        IDockerService dockerService,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _monitorService = monitorService;
        _dockerService = dockerService;
        _relayIp = configuration["FrpRelay:ServerAddr"] ?? "localhost";
        _timeoutSeconds = configuration.GetValue<int>("Provisioning:TimeoutSeconds", 120);
        _pollIntervalSeconds = configuration.GetValue<int>("Provisioning:PollIntervalSeconds", 5);
        _connectTimeoutSeconds = configuration.GetValue<int>("Provisioning:ConnectTimeoutSeconds", 3);
    }

    public void StartProvisioning(string vmId, string containerId, int relayPort, DateTime startedAt)
    {
        if (_pending.TryAdd(vmId, (containerId, relayPort, startedAt)))
            _logger.LogInformation("Provisioning started for VM {VmId} on relay port {Port}", vmId, relayPort);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(
            callback: _ => _ = PollAllAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(_pollIntervalSeconds),
            period: TimeSpan.FromSeconds(_pollIntervalSeconds));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async Task PollAllAsync()
    {
        foreach (var (vmId, (containerId, relayPort, startedAt)) in _pending.ToList())
        {
            try
            {
                var elapsed = DateTime.UtcNow - startedAt;

                if (elapsed.TotalSeconds >= _timeoutSeconds)
                {
                    _pending.TryRemove(vmId, out _);
                    _logger.LogWarning("VM {VmId} provisioning timed out after {Seconds}s; stopping container {ContainerId}", vmId, _timeoutSeconds, containerId);

                    // Stop and remove the container from Docker
                    try
                    {
                        await _dockerService.StopContainerAsync(containerId, vmId);
                        _monitorService.StopMonitoring(vmId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Error stopping container {ContainerId} for VM {VmId}: {Message}", containerId, vmId, ex.Message);
                    }

                    await UpdateStatusAsync(vmId, "Failed");
                    continue;
                }

                var sshReady = await TcpProbeAsync(_relayIp, relayPort);

                if (sshReady)
                {
                    _pending.TryRemove(vmId, out _);
                    _logger.LogInformation("VM {VmId} is ready; promoting to Running", vmId);
                    await UpdateStatusAsync(vmId, "Running");
                    _monitorService.StartMonitoring(vmId, containerId, startedAt);
                }
                else
                {
                    _logger.LogDebug("VM {VmId} not yet ready (elapsed {Elapsed:F0}s)", vmId, elapsed.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error during provisioning poll for VM {VmId}: {Message}", vmId, ex.Message);
            }
        }
    }

    private async Task<bool> TcpProbeAsync(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_connectTimeoutSeconds));
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task UpdateStatusAsync(string vmId, string status)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
        await vmService.UpdateVmStatusAsync(vmId, status);
    }

    public void Dispose() => _timer?.Dispose();
}
