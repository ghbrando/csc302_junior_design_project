namespace unicoreprovider.Services;

public class SnapshotSchedulerService : IHostedService, IDisposable
{
    private const int IntervalHours = 2;

    private readonly ISnapshotService _snapshotService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SnapshotSchedulerService> _logger;

    private Timer? _timer;

    public SnapshotSchedulerService(
        ISnapshotService snapshotService,
        IServiceScopeFactory scopeFactory,
        ILogger<SnapshotSchedulerService> logger)
    {
        _snapshotService = snapshotService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SnapshotSchedulerService started. Interval: every {Hours} hours.", IntervalHours);

        _timer = new Timer(
            callback: _ => _ = SnapshotAllAsync(),
            state: null,
            dueTime: TimeSpan.FromHours(IntervalHours),
            period: TimeSpan.FromHours(IntervalHours));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SnapshotSchedulerService stopping.");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async Task SnapshotAllAsync()
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();

            var runningVms = await vmService.GetVmsByStatusAsync("Running");

            foreach (var vm in runningVms)
            {
                if (string.IsNullOrEmpty(vm.ContainerId))
                    continue;

                try
                {
                    _logger.LogInformation("Scheduling snapshot for VM {VmId} (container {ContainerId})", vm.VmId, vm.ContainerId);
                    await _snapshotService.TriggerSnapshotAsync(vm.VmId);
                    _logger.LogInformation("Snapshot completed for VM {VmId}", vm.VmId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Snapshot failed for VM {VmId}: {Message}", vm.VmId, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SnapshotSchedulerService cycle failed: {Message}", ex.Message);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
