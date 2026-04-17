using System.Collections.Concurrent;
using System.Net.Sockets;

namespace unicoreprovider.Services;

public class VmProvisioningService : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VmProvisioningService> _logger;
    private readonly ContainerMonitorService _monitorService;
    private readonly IDockerService _dockerService;
    private readonly int _timeoutSeconds;
    private readonly int _pollIntervalSeconds;
    private readonly int _connectTimeoutSeconds;

    // vmId → (ContainerId, RelayPort, SshPort, StartedAt)
    private readonly ConcurrentDictionary<string, (string ContainerId, int RelayPort, int? SshPort, DateTime StartedAt)> _pending = new();

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
        _timeoutSeconds = configuration.GetValue<int>("Provisioning:TimeoutSeconds", 120);
        _pollIntervalSeconds = configuration.GetValue<int>("Provisioning:PollIntervalSeconds", 5);
        _connectTimeoutSeconds = configuration.GetValue<int>("Provisioning:ConnectTimeoutSeconds", 3);
    }

    public void StartProvisioning(string vmId, string containerId, int relayPort, DateTime startedAt, int? sshPort = null)
    {
        if (_pending.TryAdd(vmId, (containerId, relayPort, sshPort, startedAt)))
            _logger.LogInformation("Provisioning started for VM {VmId} on relay port {Port} (local SSH port {SshPort})",
                vmId, relayPort, sshPort?.ToString() ?? "unknown");
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
        foreach (var (vmId, (containerId, relayPort, sshPort, startedAt)) in _pending.ToList())
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
                        await _dockerService.StopContainerAsync(containerId, vmId, vmId: vmId);
                        _monitorService.StopMonitoring(vmId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Error stopping container {ContainerId} for VM {VmId}: {Message}", containerId, vmId, ex.Message);
                    }

                    await UpdateStatusAsync(vmId, "Failed");
                    continue;
                }

                // Probe the local Docker-mapped SSH port to determine if the container's
                // SSH daemon is up. This is more reliable than checking the relay tunnel
                // because it only depends on Docker, not the external FRP relay. The relay
                // client (frpc) retries in the background and will establish the tunnel
                // once the relay server is reachable.
                var probePort = sshPort ?? await LookupSshPortAsync(containerId);
                if (probePort == null)
                {
                    _logger.LogDebug("VM {VmId} SSH port not yet mapped (elapsed {Elapsed:F0}s)", vmId, elapsed.TotalSeconds);
                    continue;
                }

                var sshReady = await TcpProbeAsync("localhost", probePort.Value);

                if (sshReady)
                {
                    _pending.TryRemove(vmId, out _);
                    _logger.LogInformation("VM {VmId} is ready (local SSH port {Port}); promoting to Running", vmId, probePort.Value);
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

    /// <summary>
    /// Falls back to querying Docker for the container's mapped SSH port when
    /// the caller didn't supply one (e.g. older code paths).
    /// </summary>
    private async Task<int?> LookupSshPortAsync(string containerId)
    {
        try
        {
            return await _dockerService.GetContainerSshPortAsync(containerId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Connects to the given host:port and waits for an SSH banner (a line starting
    /// with "SSH-"). A bare TCP accept (Docker port mapping) is NOT enough — sshd
    /// must be fully started and sending its identification string.
    /// </summary>
    private async Task<bool> TcpProbeAsync(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_connectTimeoutSeconds));
            await client.ConnectAsync(host, port, cts.Token);

            var stream = client.GetStream();
            var buffer = new byte[256];
            var totalRead = 0;

            // Read until we see a newline (end of banner) or fill the buffer.
            while (totalRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead), cts.Token);

                if (bytesRead == 0)
                    return false; // Connection closed before sending banner

                totalRead += bytesRead;

                // Check if we've received a complete line
                var text = System.Text.Encoding.ASCII.GetString(buffer, 0, totalRead);
                if (text.Contains('\n'))
                    return text.StartsWith("SSH-", StringComparison.Ordinal);
            }

            // Buffer full without newline — check what we got
            var partial = System.Text.Encoding.ASCII.GetString(buffer, 0, totalRead);
            return partial.StartsWith("SSH-", StringComparison.Ordinal);
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
