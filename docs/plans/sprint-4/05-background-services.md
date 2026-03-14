# Workstream 5: Background Services (Monitoring & Scheduling)

**Type:** Backend
**Depends On:** Workstreams 1 (data models), 2 (Docker operations), 4 (GCP infrastructure)
**Blocks:** Workstream 6 (migration depends on healthy snapshots)
**Estimated Scope:** 600 lines, 10 hours
**Owner:** Backend developer (services/threading)

---

## 🎯 Overview

Create three background services that run on the provider app:

1. **VolumeBackupService** — Monitors when containers last synced volumes to GCS. Alerts if stale.
2. **SnapshotService** — Orchestrates committing containers to images and pushing to Artifact Registry.
3. **SnapshotSchedulerService** — Runs snapshots every 2 hours + on graceful shutdown.

These services are "helpers" that manage the backup/snapshot process. The actual syncing happens inside containers (via cron job), and Firestore is the source of truth.

---

## 🏗️ Architecture

### How Backups Work (Container-Centric)

```
Container (running)
  ├─ Every 5 minutes (via cron job, runs as root):
  │   └─ gsutil rsync /home/consumer → gs://unicore-vm-volumes/{...}
  │
  └─ Updates Firestore: VirtualMachine.last_volume_sync_at = now()
        ↓
Provider App (monitoring)
  └─ VolumeBackupService reads last_volume_sync_at from Firestore
      └─ Alerts if stale (e.g., > 10 min old)
```

### How Snapshots Work (Provider-Centric)

```
SnapshotSchedulerService (every 2 hours)
  └─ Calls SnapshotService.TakeSnapshotAsync(vmId, containerId)
      ├─ Updates Firestore: snapshot_status = "Committing"
      ├─ Calls DockerService.CommitContainerAsync(...)
      ├─ Updates Firestore: snapshot_status = "Pushing"
      ├─ Calls DockerService.PushImageAsync(...)
      ├─ Updates Firestore: snapshot_status = "Idle", last_snapshot_at = now()
      └─ Returns
```

### Service Lifecycle

```
Provider App Starts
  ├─ VolumeBackupService starts
  │   └─ In-memory dict of VM → metadata
  │   └─ Monitoring loop: read Firestore, check staleness
  │
  ├─ SnapshotService registered (no background loop)
  │   └─ Called on-demand by SnapshotScheduler or Dashboard
  │
  ├─ SnapshotSchedulerService starts
  │   └─ PeriodicTimer every 2 hours
  │   └─ Calls TakeSnapshotAsync for each registered VM
  │
  └─ Dashboard.razor wires them:
      └─ On VM launch: call service.StartMonitoring(vmId)
      └─ On VM stop: call service.StopMonitoring(vmId)

Provider App Stops (graceful shutdown)
  └─ SnapshotSchedulerService.StopAsync() called
      └─ Takes final snapshots before shutdown
      └─ Waits for snapshots to complete (timeout 10 min)
```

---

## 🔌 Integration Points

### Files to Create

1. **`providerunicore/Services/IVolumeBackupService.cs`** (NEW)
2. **`providerunicore/Services/VolumeBackupService.cs`** (NEW)
3. **`providerunicore/Services/ISnapshotService.cs`** (NEW)
4. **`providerunicore/Services/SnapshotService.cs`** (NEW)
5. **`providerunicore/Services/SnapshotSchedulerService.cs`** (NEW)

### Files to Modify

1. **`providerunicore/Program.cs`**
   - Register ISnapshotService, IVolumeBackupService
   - Register SnapshotSchedulerService as hosted service
   - Configure shutdown timeout (10 min for graceful snapshots)

2. **`providerunicore/Components/Pages/Dashboard.razor`**
   - Wire `StartMonitoring()` / `StopMonitoring()` calls alongside existing services

---

## ✅ What Needs to Be Done

### Task 1: Create VolumeBackupService Interface

**File:** `providerunicore/Services/IVolumeBackupService.cs`

```csharp
public interface IVolumeBackupService
{
    /// <summary>
    /// Start monitoring a VM's volume backup status.
    /// </summary>
    Task StartMonitoringAsync(string vmId);

    /// <summary>
    /// Stop monitoring a VM's volume backup status.
    /// </summary>
    Task StopMonitoringAsync(string vmId);

    /// <summary>
    /// Check if a VM's volume backup is healthy (synced recently).
    /// </summary>
    Task<bool> IsHealthyAsync(string vmId);

    /// <summary>
    /// Get time since last successful volume sync.
    /// </summary>
    Task<TimeSpan?> GetTimeSinceLastSyncAsync(string vmId);
}
```

### Task 2: Implement VolumeBackupService

**File:** `providerunicore/Services/VolumeBackupService.cs`

```csharp
public class VolumeBackupService : BackgroundService, IVolumeBackupService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, bool> _monitoredVms = new();
    private PeriodicTimer? _timer;
    private const int CheckIntervalSeconds = 30; // Check every 30 seconds
    private const int StaleThresholdMinutes = 10; // Alert if last sync > 10 min ago

    public VolumeBackupService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task StartMonitoringAsync(string vmId)
    {
        _monitoredVms.TryAdd(vmId, true);
        await Task.CompletedTask;
    }

    public async Task StopMonitoringAsync(string vmId)
    {
        _monitoredVms.TryRemove(vmId, out _);
        await Task.CompletedTask;
    }

    public async Task<bool> IsHealthyAsync(string vmId)
    {
        using var scope = _scopeFactory.CreateScope();
        var vmRepo = scope.ServiceProvider.GetRequiredService<IFirestoreRepository<VirtualMachine>>();

        var vm = await vmRepo.GetByIdAsync(vmId);
        if (vm?.LastVolumeSyncAt == null)
            return false;

        var timeSinceSync = DateTime.UtcNow - vm.LastVolumeSyncAt.Value;
        return timeSinceSync < TimeSpan.FromMinutes(StaleThresholdMinutes);
    }

    public async Task<TimeSpan?> GetTimeSinceLastSyncAsync(string vmId)
    {
        using var scope = _scopeFactory.CreateScope();
        var vmRepo = scope.ServiceProvider.GetRequiredService<IFirestoreRepository<VirtualMachine>>();

        var vm = await vmRepo.GetByIdAsync(vmId);
        return vm?.LastVolumeSyncAt == null ? null : DateTime.UtcNow - vm.LastVolumeSyncAt.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));

        using (_timer)
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                // Check each monitored VM for stale backup
                foreach (var vmId in _monitoredVms.Keys)
                {
                    try
                    {
                        var isHealthy = await IsHealthyAsync(vmId);
                        if (!isHealthy)
                        {
                            // Log alert or update VM status
                            // TODO: Set VM status to "UnhealthyBackup" or similar
                            Console.WriteLine($"[ALERT] VM {vmId} volume backup is stale");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error, continue monitoring
                        Console.WriteLine($"[ERROR] Checking volume backup for {vmId}: {ex.Message}");
                    }
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
```

### Task 3: Create SnapshotService Interface

**File:** `providerunicore/Services/ISnapshotService.cs`

```csharp
public interface ISnapshotService
{
    /// <summary>
    /// Take a snapshot of a running container and push to registry.
    /// </summary>
    Task TakeSnapshotAsync(string vmId, string containerId, CancellationToken ct = default);

    /// <summary>
    /// Pull a snapshot image from registry (ensures it's locally available).
    /// </summary>
    Task PullSnapshotAsync(string imageTag, CancellationToken ct = default);
}
```

### Task 4: Implement SnapshotService

**File:** `providerunicore/Services/SnapshotService.cs`

```csharp
public class SnapshotService : ISnapshotService
{
    private readonly IDockerService _dockerService;
    private readonly IFirestoreRepository<VirtualMachine> _vmRepository;
    private readonly string _registryHost; // e.g., "us-central1-docker.pkg.dev"
    private readonly string _projectId;

    public SnapshotService(
        IDockerService dockerService,
        IFirestoreRepository<VirtualMachine> vmRepository,
        IConfiguration config)
    {
        _dockerService = dockerService;
        _vmRepository = vmRepository;
        _registryHost = config["GCP:RegistryHost"] ?? "us-central1-docker.pkg.dev";
        _projectId = config["Firebase:ProjectId"];
    }

    public async Task TakeSnapshotAsync(string vmId, string containerId, CancellationToken ct = default)
    {
        var vm = await _vmRepository.GetByIdAsync(vmId);
        if (vm == null)
            throw new InvalidOperationException($"VM {vmId} not found");

        try
        {
            // 1. Update status → Committing
            vm.SnapshotStatus = "Committing";
            await _vmRepository.UpdateAsync(vmId, vm);

            // 2. Commit container to image
            string imageTag = $"{_registryHost}/{_projectId}/unicore-vm-snapshots/{vmId}:latest";
            string imageId = await _dockerService.CommitContainerAsync(
                containerId,
                $"{_registryHost}/{_projectId}/unicore-vm-snapshots/{vmId}",
                "latest",
                ct);

            // 3. Update status → Pushing
            vm.SnapshotStatus = "Pushing";
            await _vmRepository.UpdateAsync(vmId, vm);

            // 4. Push image to registry
            await _dockerService.PushImageAsync(imageTag, ct);

            // 5. Update status → Idle, record timestamp
            vm.SnapshotStatus = "Idle";
            vm.SnapshotImage = imageTag;
            vm.LastSnapshotAt = DateTime.UtcNow;
            await _vmRepository.UpdateAsync(vmId, vm);
        }
        catch (Exception ex)
        {
            // On error, mark as error state
            vm.SnapshotStatus = "Error";
            vm.MigrationError = ex.Message;
            await _vmRepository.UpdateAsync(vmId, vm);
            throw;
        }
    }

    public async Task PullSnapshotAsync(string imageTag, CancellationToken ct = default)
    {
        // Reuse existing Docker method (if available)
        // Otherwise: await _dockerService.PullImageAsync(imageTag);
        await Task.CompletedTask;
    }
}
```

### Task 5: Create SnapshotSchedulerService

**File:** `providerunicore/Services/SnapshotSchedulerService.cs`

```csharp
public class SnapshotSchedulerService : BackgroundService
{
    private readonly ISnapshotService _snapshotService;
    private readonly IFirestoreRepository<VirtualMachine> _vmRepository;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ConcurrentDictionary<string, string> _scheduledVms = new(); // vmId → containerId
    private PeriodicTimer? _timer;
    private const int SnapshotIntervalHours = 2;

    public SnapshotSchedulerService(
        ISnapshotService snapshotService,
        IFirestoreRepository<VirtualMachine> vmRepository,
        IHostApplicationLifetime appLifetime)
    {
        _snapshotService = snapshotService;
        _vmRepository = vmRepository;
        _appLifetime = appLifetime;
    }

    public void StartScheduling(string vmId, string containerId)
    {
        _scheduledVms.TryAdd(vmId, containerId);
    }

    public void StopScheduling(string vmId)
    {
        _scheduledVms.TryRemove(vmId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Register for graceful shutdown
        _appLifetime.ApplicationStopping.Register(async () =>
        {
            Console.WriteLine("Graceful shutdown: Taking final snapshots...");
            await TakeSnapshotsAsync(stoppingToken);
        });

        _timer = new PeriodicTimer(TimeSpan.FromHours(SnapshotIntervalHours));

        using (_timer)
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                Console.WriteLine("Scheduled snapshot cycle starting...");
                await TakeSnapshotsAsync(stoppingToken);
            }
        }
    }

    private async Task TakeSnapshotsAsync(CancellationToken stoppingToken)
    {
        var tasks = _scheduledVms.Select(async kvp =>
        {
            string vmId = kvp.Key;
            string containerId = kvp.Value;

            try
            {
                await _snapshotService.TakeSnapshotAsync(vmId, containerId, stoppingToken);
                Console.WriteLine($"✓ Snapshot completed: {vmId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Snapshot failed for {vmId}: {ex.Message}");
            }
        });

        await Task.WhenAll(tasks);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
```

### Task 6: Register Services in Program.cs

**File:** `providerunicore/Program.cs`

Add to the DI section:

```csharp
// Register snapshot services
builder.Services.AddScoped<ISnapshotService, SnapshotService>();
builder.Services.AddSingleton<IVolumeBackupService, VolumeBackupService>();
builder.Services.AddSingleton<SnapshotSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VolumeBackupService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SnapshotSchedulerService>());

// Extend shutdown timeout for graceful snapshots
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(10));
```

### Task 7: Wire Services in Dashboard

**File:** `providerunicore/Components/Pages/Dashboard.razor`

In `ProcessRequestAsync` (when VM is created), start monitoring:

```csharp
// After creating VirtualMachine and starting monitoring via ContainerMonitorService
var snapshotScheduler = ServiceProvider.GetRequiredService<SnapshotSchedulerService>();
snapshotScheduler.StartScheduling(vmId, containerId);

var volumeBackup = ServiceProvider.GetRequiredService<IVolumeBackupService>();
await volumeBackup.StartMonitoringAsync(vmId);
```

When VM is stopped, stop monitoring:

```csharp
snapshotScheduler.StopScheduling(vmId);
await volumeBackup.StopMonitoringAsync(vmId);
```

---

## 🧪 Acceptance Criteria

- [ ] IVolumeBackupService interface created with 3 methods
- [ ] VolumeBackupService implemented as IHostedService
- [ ] ISnapshotService interface created with 2 methods
- [ ] SnapshotService implemented with proper state management
- [ ] SnapshotSchedulerService implemented with 2-hour timer
- [ ] SnapshotSchedulerService registers graceful shutdown hook
- [ ] All services registered in Program.cs DI
- [ ] Shutdown timeout extended to 10 minutes
- [ ] Dashboard wires StartMonitoring/StopMonitoring calls
- [ ] Services use proper exception handling and logging
- [ ] No compilation errors
- [ ] Services use `IServiceScopeFactory` correctly (for scoped repos in singletons)

---

## 🧠 Key Implementation Patterns

### Hosted Services
```csharp
public class MyService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Do work
        }
    }
}
```

### PeriodicTimer
```csharp
_timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));
while (await _timer.WaitForNextTickAsync(stoppingToken))
{
    // Runs every interval
}
```

### Service Scope Factory (for scoped repos in singletons)
```csharp
public class SingletonService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public async Task DoWorkAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFirestoreRepository<T>>();
        // Use repo
    }
}
```

---

## 🔗 Related Code

**Existing pattern (ContainerMonitorService):**
- `providerunicore/Services/ContainerMonitorService.cs` — IHostedService + PeriodicTimer
- Shows: how to monitor VMs, update Firestore, handle cleanup

**Existing pattern (PauseResumeListenerService):**
- Shows: Firestore listener pattern, real-time state changes

**Docker operations (from Workstream 2):**
- `DockerService.CommitContainerAsync`
- `DockerService.PushImageAsync`

---

## ⏱️ Time Estimate

- Reading this document: 20 min
- Implementing VolumeBackupService: 45 min
- Implementing SnapshotService: 30 min
- Implementing SnapshotSchedulerService: 30 min
- DI registration + wiring: 15 min
- Testing & refinement: 30 min

**Total: ~3 hours** (estimate 4–5 hours with edge cases)

---

## 🚀 Next Steps

Once complete:
- Workstream 6 (migration orchestration) can begin
- Both depend on snapshots being reliably stored

---

**Status:** Ready to implement (after Workstreams 1, 2, 4)
**Owner:** Backend developer (services)
**Next Workstream:** 6 (migration orchestration)
