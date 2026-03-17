using System.Net.Sockets;
using System.Text;
using Google.Cloud.Firestore;

namespace heartbeatservice.Workers;

public class HeartbeatWorker : BackgroundService
{
    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly IFirestoreRepository<VirtualMachine> _vmRepo;
    private readonly IFirestoreRepository<Provider> _providerRepo;
    private readonly FirestoreDb _firestoreDb;
    private readonly string _relayAddr;
    private readonly int _intervalSeconds;
    private readonly int _sshTimeoutSeconds;
    private readonly int _startupGraceMinutes;
    private readonly int _resumeGraceSeconds;
    private readonly Dictionary<string, bool> _lastPausedStateByVmId = new();
    private readonly Dictionary<string, DateTime> _resumeGraceUntilByVmId = new();

    public HeartbeatWorker(
        ILogger<HeartbeatWorker> logger,
        IFirestoreRepository<VirtualMachine> vmRepo,
        IFirestoreRepository<Provider> providerRepo,
        FirestoreDb firestoreDb,
        IConfiguration configuration)
    {
        _logger = logger;
        _vmRepo = vmRepo;
        _providerRepo = providerRepo;
        _firestoreDb = firestoreDb;
        _relayAddr = configuration["FrpRelay:ServerAddr"] ?? "136.116.172.0";
        _intervalSeconds = configuration.GetValue<int>("Heartbeat:IntervalSeconds", 10);
        _sshTimeoutSeconds = configuration.GetValue<int>("Heartbeat:SshBannerTimeoutSeconds", 3);
        _startupGraceMinutes = configuration.GetValue<int>("Heartbeat:StartupGraceMinutes", 5);
        _resumeGraceSeconds = configuration.GetValue<int>("Heartbeat:ResumeGraceSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[HeartbeatWorker] Starting. Relay: {Relay}, Interval: {Interval}s", _relayAddr, _intervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_intervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HeartbeatWorker] Unhandled error in heartbeat cycle.");
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var runningVmCandidates = (await _vmRepo.WhereAsync("status", "Running")).ToList();

        var activeVmIds = runningVmCandidates.Select(vm => vm.VmId).ToHashSet();
        var staleStateKeys = _lastPausedStateByVmId.Keys.Where(id => !activeVmIds.Contains(id)).ToList();
        foreach (var staleId in staleStateKeys)
        {
            _lastPausedStateByVmId.Remove(staleId);
            _resumeGraceUntilByVmId.Remove(staleId);
        }

        foreach (var vm in runningVmCandidates)
        {
            if (_lastPausedStateByVmId.TryGetValue(vm.VmId, out var wasPaused) && wasPaused && !vm.IsPaused)
            {
                _resumeGraceUntilByVmId[vm.VmId] = DateTime.UtcNow.AddSeconds(_resumeGraceSeconds);
                _logger.LogDebug("[HeartbeatWorker] VM {VmId} resumed; applying {GraceSeconds}s grace before probing.", vm.VmId, _resumeGraceSeconds);
            }

            _lastPausedStateByVmId[vm.VmId] = vm.IsPaused;
        }

        var runningVms = runningVmCandidates.Where(vm => !vm.IsPaused).ToList();

        if (runningVms.Count == 0)
        {
            _logger.LogDebug("[HeartbeatWorker] No running VMs found.");
            return;
        }

        // Group by provider so we make one provider read/write per provider
        var vmsByProvider = runningVms.GroupBy(vm => vm.ProviderId);

        int totalProbed = 0;
        int totalResponded = 0;

        foreach (var group in vmsByProvider)
        {
            ct.ThrowIfCancellationRequested();

            var provider = await _providerRepo.GetByIdAsync(group.Key);
            if (provider == null)
            {
                _logger.LogWarning("[HeartbeatWorker] Provider {ProviderId} not found, skipping.", group.Key);
                continue;
            }

            // Offline providers are excluded from scoring — their VMs may be in a transient state
            if (provider.NodeStatus == "Offline")
            {
                _logger.LogDebug("[HeartbeatWorker] Provider {Name} is Offline, skipping.", provider.Name);
                continue;
            }

            double scoreDelta = 0;
            var vmFieldUpdates = new List<(string vmId, int consecutiveMisses)>();

            foreach (var vm in group)
            {
                if (_resumeGraceUntilByVmId.TryGetValue(vm.VmId, out var resumeGraceUntilUtc))
                {
                    if (DateTime.UtcNow < resumeGraceUntilUtc)
                    {
                        _logger.LogDebug("[HeartbeatWorker] VM {VmId} is within resume grace period, skipping.", vm.VmId);
                        continue;
                    }

                    _resumeGraceUntilByVmId.Remove(vm.VmId);
                }

                if (vm.RelayPort == null)
                {
                    _logger.LogDebug("[HeartbeatWorker] VM {VmId} has no relay port, skipping.", vm.VmId);
                    continue;
                }

                // Skip VMs still within the startup grace period — apt-get, FRP download,
                // and sshd startup can take several minutes after Docker reports the container
                // as running. Probing during this window would unfairly penalize the provider.
                if (vm.StartedAt.HasValue &&
                    (DateTime.UtcNow - vm.StartedAt.Value).TotalMinutes < _startupGraceMinutes)
                {
                    _logger.LogDebug("[HeartbeatWorker] VM {VmId} is within startup grace period, skipping.", vm.VmId);
                    continue;
                }

                totalProbed++;
                bool responded = await CheckSshBannerAsync(_relayAddr, vm.RelayPort.Value, _sshTimeoutSeconds);

                if (responded)
                {
                    totalResponded++;
                    scoreDelta += 0.01;
                    vmFieldUpdates.Add((vm.VmId, 0));
                    _logger.LogDebug("[HeartbeatWorker] VM {VmId} responded (+0.01).", vm.VmId);
                }
                else
                {
                    // Penalty scales with consecutive misses: miss #1 = -0.01, miss #2 = -0.02, ...
                    int streak = vm.ConsecutiveMisses + 1;
                    double penalty = 0.01 * streak;
                    scoreDelta -= penalty;
                    vmFieldUpdates.Add((vm.VmId, streak));
                    _logger.LogDebug("[HeartbeatWorker] VM {VmId} did not respond (streak {Streak}, penalty -{Penalty:F2}).", vm.VmId, streak, penalty);
                }
            }


            // Targeted field update on Provider — only touches consistency_score
            double newScore = provider.ConsistencyScore + scoreDelta;
            // Ensure score stays within [0, infinity) — it can grow without bound but should never drop below 0]
            if (newScore < 0) newScore = 0;
            await _firestoreDb.Collection("providers").Document(provider.FirebaseUid)
                .UpdateAsync("consistency_score", newScore);

            // Targeted field update on each VM — only touches consecutive_misses
            var vmCollection = _firestoreDb.Collection("virtual_machines");
            var updateTasks = vmFieldUpdates.Select(u =>
                vmCollection.Document(u.vmId).UpdateAsync("consecutive_misses", u.consecutiveMisses));
            await Task.WhenAll(updateTasks);
        }

        _logger.LogInformation("[HeartbeatWorker] Cycle complete: {Responded}/{Probed} VMs responded.", totalResponded, totalProbed);
    }

    /// <summary>
    /// Attempts a TCP connection to the relay port and reads the SSH version banner.
    /// Returns true if the banner starts with "SSH-", confirming the FRP tunnel and SSH daemon are alive.
    /// </summary>
    private async Task<bool> CheckSshBannerAsync(string host, int port, int timeoutSeconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var client = new TcpClient();

            await client.ConnectAsync(host, port, cts.Token);

            using var stream = client.GetStream();
            stream.ReadTimeout = timeoutSeconds * 1000;

            var buffer = new byte[64];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

            if (bytesRead == 0)
                return false;

            var banner = Encoding.ASCII.GetString(buffer, 0, bytesRead);
            return banner.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
