using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Firestore;
using unicoreprovider.Services;

namespace unicoreprovider.Services;

public class ContainerMonitorService : IHostedService, IDisposable
{
    private const int PollIntervalSeconds = 5;

    private readonly IDockerService _dockerService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContainerMonitorService> _logger;

    // vmId → (containerId, startedAt)
    private readonly ConcurrentDictionary<string, (string ContainerId, DateTime StartedAt, TimeSpan AccumulatedUptime, bool WasPaused)> _monitored = new();
    private readonly ConcurrentDictionary<string, string> _lastSecurityLogLineByVm = new();

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
        if (_monitored.TryAdd(vmId, (containerId, startedAt, TimeSpan.Zero, false)))
            _logger.LogInformation("Started monitoring VM {VmId} (container {ContainerId})", vmId, containerId);
    }

    // Called when a container is stopped
    public void StopMonitoring(string vmId)
    {
        if (_monitored.TryRemove(vmId, out _))
            _logger.LogInformation("Stopped monitoring VM {VmId}", vmId);

        _lastSecurityLogLineByVm.TryRemove(vmId, out _);
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
        foreach (var (vmId, (containerId, startedAt, accumulatedUptime, wasPaused)) in _monitored.ToList())
        {
            try
            {
                // Create a short-lived scope to resolve the scoped IVmService
                await using var scope = _scopeFactory.CreateAsyncScope();
                var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();

                // Check if VM is paused; skip metrics update if it is
                var vm = await vmService.GetByIdAsync(vmId);

                if (vm is not null)
                {
                    await IngestSecurityEventsAsync(scope.ServiceProvider, vm, containerId);
                }

                if (vm?.IsPaused == true)
                {
                    if (!wasPaused && _monitored.TryGetValue(vmId, out var entry))
                    {
                        var accumulated = entry.AccumulatedUptime + (DateTime.UtcNow - entry.StartedAt);
                        _monitored[vmId] = (containerId, DateTime.UtcNow, accumulated, true);
                    }
                    _logger.LogDebug("VM {VmId} is paused; skipping metrics update", vmId);
                    continue;
                }

                if (wasPaused && _monitored.TryGetValue(vmId, out var resumedEntry))
                {
                    _monitored[vmId] = resumedEntry with { StartedAt = DateTime.UtcNow, AccumulatedUptime = resumedEntry.AccumulatedUptime, WasPaused = false };
                }


                var (cpu, ram) = await _dockerService.GetContainerStatsAsync(containerId);
                var currVM = _monitored[vmId];
                var uptime = currVM.AccumulatedUptime + (DateTime.UtcNow - currVM.StartedAt);
                var uptimeStr = uptime.ToString(@"hh\:mm\:ss");

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

    private async Task IngestSecurityEventsAsync(IServiceProvider serviceProvider, VirtualMachine vm, string containerId)
    {
        IReadOnlyList<string> allLines;
        try
        {
            allLines = await _dockerService.GetContainerSecurityLogLinesAsync(containerId, 200);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read security log for VM {VmId} container {ContainerId}", vm.VmId, containerId);
            return;
        }

        if (allLines.Count == 0)
            return;

        var logLines = allLines.Where(IsSecurityLogLine).ToList();
        if (logLines.Count == 0)
            return;

        if (_lastSecurityLogLineByVm.TryGetValue(vm.VmId, out var lastLine))
        {
            var lastIndex = logLines.FindLastIndex(line => line == lastLine);
            if (lastIndex >= 0 && lastIndex < logLines.Count - 1)
            {
                logLines = logLines.Skip(lastIndex + 1).ToList();
            }
            else if (lastIndex == logLines.Count - 1)
            {
                return;
            }
        }
        else if (logLines.Count > 20)
        {
            // Avoid a large backfill on first attach; keep only most recent events.
            logLines = logLines.Skip(logLines.Count - 20).ToList();
        }

        if (logLines.Count == 0)
            return;

        var firestore = serviceProvider.GetRequiredService<FirestoreDb>();
        var eventsCollection = firestore.Collection("security_events");

        foreach (var line in logLines)
        {
            var parsed = ParseSecurityLogLine(line);
            var eventId = ComputeEventId(vm.VmId, line);

            var doc = new Dictionary<string, object>
            {
                ["event_id"] = eventId,
                ["timestamp"] = Timestamp.FromDateTime(parsed.TimestampUtc),
                ["vm_id"] = vm.VmId,
                ["container_id"] = containerId,
                ["provider_id"] = vm.ProviderId,
                ["client"] = vm.Client,
                ["user"] = parsed.User,
                ["strikes"] = parsed.Strikes,
                ["severity"] = parsed.Strikes >= 2 ? "critical" : "warning",
                ["action"] = parsed.Strikes >= 2 ? "session_terminated" : "warning_issued",
                ["command"] = parsed.Command,
                ["raw_log"] = line,
                ["uploaded_at"] = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            await eventsCollection.Document(eventId).SetAsync(doc);
        }

        _lastSecurityLogLineByVm[vm.VmId] = logLines[^1];
    }

    private static bool IsSecurityLogLine(string line)
        => !string.IsNullOrWhiteSpace(line) && line.StartsWith("[") && line.Contains(" user=") && line.Contains(" strikes=") && line.Contains(" cmd=");

    private static (DateTime TimestampUtc, string User, int Strikes, string Command) ParseSecurityLogLine(string line)
    {
        // Expected format:
        // [2026-04-20T12:34:56Z] user=consumer strikes=2 cmd=<command>
        var timestampUtc = DateTime.UtcNow;
        var user = "unknown";
        var strikes = 1;
        var command = line;

        var close = line.IndexOf(']');
        if (line.StartsWith("[") && close > 1)
        {
            var tsText = line.Substring(1, close - 1);
            if (DateTime.TryParse(tsText, out var parsedTs))
                timestampUtc = parsedTs.ToUniversalTime();
        }

        user = ReadField(line, "user=") ?? user;

        var strikesText = ReadField(line, "strikes=");
        if (!string.IsNullOrWhiteSpace(strikesText) && int.TryParse(strikesText, out var parsedStrikes))
            strikes = parsedStrikes;

        var cmdIndex = line.IndexOf("cmd=", StringComparison.Ordinal);
        if (cmdIndex >= 0)
            command = line[(cmdIndex + 4)..].Trim();

        return (timestampUtc, user, strikes, command);
    }

    private static string? ReadField(string line, string token)
    {
        var start = line.IndexOf(token, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += token.Length;
        var end = line.IndexOf(' ', start);
        if (end < 0)
            end = line.Length;

        return line[start..end].Trim();
    }

    private static string ComputeEventId(string vmId, string line)
    {
        var payload = Encoding.UTF8.GetBytes($"{vmId}|{line}");
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => _timer?.Dispose();
}
