# Workstream 6: Migration Orchestration

**Type:** Backend (complex state machine)
**Depends On:** Workstreams 1–5 (everything must be complete)
**Blocks:** Workstream 7 (UI depends on backend completing migrations)
**Estimated Scope:** 600 lines, 12 hours
**Owner:** Senior backend developer (state machine complexity)

---

## 🎯 Overview

The provider app listens for migration requests (VmMigrationRequest documents in Firestore). When a request arrives targeting this provider, the provider:

1. Pulls the snapshot image from Artifact Registry
2. Creates a new volume
3. Restores user data from GCS into the volume
4. Starts a new container from the snapshot
5. Updates Firestore to mark old VM as "Migrated" and new VM as "Running"

This is the most complex workstream because it orchestrates multiple services in a specific sequence.

---

## 🏗️ Architecture

### Migration State Machine

```
Consumer clicks "Migrate VM"
  ↓
VmMigrationRequest created in Firestore: status = "pending"
  ↓
Provider Dashboard listener detects request
  (target_provider_uid == this provider AND status == "pending")
  ↓
ProcessMigrationRequestAsync invoked
  ├─ Update VmMigrationRequest.status = "restoring"
  ├─ Update old VM.migration_status = "Restoring"
  │
  ├─ Pull snapshot image from Artifact Registry
  │  (SnapshotService.PullSnapshotAsync)
  │
  ├─ Create new volume
  │  (DockerService.CreateVolumeAsync)
  │
  ├─ Restore user data from GCS to volume
  │  (VolumeBackupService.RestoreFromGcsAsync)
  │
  ├─ Start container from snapshot image with new volume
  │  (DockerService.StartContainerAsync)
  │
  ├─ Create new VirtualMachine document in Firestore
  │  (with OriginalVmId = old VM ID)
  │
  ├─ Update old VirtualMachine: status = "Stopped", migration_status = "Migrated"
  │
  ├─ Start monitoring new VM
  │  (ContainerMonitorService, VolumeBackupService, SnapshotSchedulerService)
  │
  └─ Update VmMigrationRequest.status = "completed", new_vm_id = newVmId
       ↓
Consumer sees new VM in dashboard, can SSH in
```

### Firestore Listener Setup

```csharp
// In Dashboard.razor, after provider authenticates
var migrationListener = _vmMigrationRequestRepository
    .CreateQuery()
    .WhereEqualTo("target_provider_uid", providerId)
    .WhereEqualTo("status", "pending")
    .Listen(snapshot =>
    {
        foreach (var change in snapshot.Changes)
        {
            if (change.Type == DocumentChangeType.Added)
            {
                var request = change.Document.ConvertTo<VmMigrationRequest>();
                _ = ProcessMigrationRequestAsync(request);
            }
        }
    });
```

---

## 🔌 Integration Points

### Files to Create

1. **`providerunicore/Services/IMigrationService.cs`** (NEW)
2. **`providerunicore/Services/MigrationService.cs`** (NEW)

### Files to Modify

1. **`providerunicore/Components/Pages/Dashboard.razor`**
   - Add Firestore listener for migration requests
   - Implement `ProcessMigrationRequestAsync(VmMigrationRequest request)`

2. **`providerunicore/Services/IVolumeBackupService.cs`**
   - Add method: `RestoreFromGcsAsync(vmId, gcsPath, containerId)`

3. **`providerunicore/Services/VolumeBackupService.cs`**
   - Implement restore method (pull from GCS into volume)

4. **`providerunicore/Program.cs`**
   - Register IMigrationService in DI

---

## ✅ What Needs to Be Done

### Task 1: Create IMigrationService Interface

**File:** `providerunicore/Services/IMigrationService.cs`

```csharp
public interface IMigrationService
{
    /// <summary>
    /// Process an incoming migration request and restore VM on this provider.
    /// </summary>
    Task ProcessMigrationRequestAsync(VmMigrationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Check if a migration is in progress.
    /// </summary>
    Task<bool> IsMigrationInProgressAsync(string vmId);
}
```

### Task 2: Implement MigrationService

**File:** `providerunicore/Services/MigrationService.cs`

```csharp
public class MigrationService : IMigrationService
{
    private readonly IDockerService _dockerService;
    private readonly ISnapshotService _snapshotService;
    private readonly IVolumeBackupService _volumeBackupService;
    private readonly IVmService _vmService;
    private readonly IFirestoreRepository<VirtualMachine> _vmRepository;
    private readonly IFirestoreRepository<VmMigrationRequest> _requestRepository;
    private readonly ConcurrentDictionary<string, bool> _migrationsInProgress = new();

    public MigrationService(
        IDockerService dockerService,
        ISnapshotService snapshotService,
        IVolumeBackupService volumeBackupService,
        IVmService vmService,
        IFirestoreRepository<VirtualMachine> vmRepository,
        IFirestoreRepository<VmMigrationRequest> requestRepository)
    {
        _dockerService = dockerService;
        _snapshotService = snapshotService;
        _volumeBackupService = volumeBackupService;
        _vmService = vmService;
        _vmRepository = vmRepository;
        _requestRepository = requestRepository;
    }

    public async Task ProcessMigrationRequestAsync(VmMigrationRequest request, CancellationToken ct = default)
    {
        string requestId = request.MigrationRequestId;
        string vmId = request.VmId;
        string oldVmId = vmId;
        string newVmId = Guid.NewGuid().ToString().Substring(0, 8);
        string consumerId = request.ConsumerUid;
        string oldProviderId = request.SourceProviderUid;
        string newProviderId = request.TargetProviderUid;

        if (_migrationsInProgress.ContainsKey(vmId))
        {
            throw new InvalidOperationException($"Migration already in progress for VM {vmId}");
        }

        _migrationsInProgress.TryAdd(vmId, true);

        try
        {
            // 1. Update request: restoring
            request.Status = "restoring";
            await _requestRepository.UpdateAsync(requestId, request);

            // 2. Update old VM: migration_status = "Restoring"
            var oldVm = await _vmRepository.GetByIdAsync(oldVmId);
            oldVm.MigrationStatus = "Restoring";
            oldVm.MigrationRequestedAt = DateTime.UtcNow;
            await _vmRepository.UpdateAsync(oldVmId, oldVm);

            // 3. Pull snapshot image
            Console.WriteLine($"Pulling snapshot image: {oldVm.SnapshotImage}");
            if (string.IsNullOrEmpty(oldVm.SnapshotImage))
                throw new InvalidOperationException($"VM {oldVmId} has no snapshot image");

            await _snapshotService.PullSnapshotAsync(oldVm.SnapshotImage, ct);

            // 4. Create new volume
            string newVolumeName = $"unicore-vol-{newVmId}";
            Console.WriteLine($"Creating volume: {newVolumeName}");
            await _dockerService.CreateVolumeAsync(newVolumeName, ct);

            // 5. Restore user data from GCS
            string gcsPath = oldVm.GcsPath ?? $"consumers/{consumerId}/{oldVmId}/";
            Console.WriteLine($"Restoring data from GCS: {gcsPath}");
            await _volumeBackupService.RestoreFromGcsAsync(newVmId, gcsPath, ct);

            // 6. Allocate relay port (find available)
            int relayPort = await AllocateRelayPortAsync();
            Console.WriteLine($"Allocated relay port: {relayPort}");

            // 7. Start container from snapshot image
            string containerName = $"vm-{newVmId}";
            var (containerId, volumeName) = await _dockerService.StartContainerAsync(
                vmId: newVmId,
                name: containerName,
                image: oldVm.SnapshotImage,
                relayPort: relayPort,
                cpuCores: oldVm.CpuCores,
                ramGB: oldVm.RamGb,
                existingVolumeName: newVolumeName,
                ct: ct);

            Console.WriteLine($"Container started: {containerId}");

            // 8. Create new VirtualMachine document
            var newVm = new VirtualMachine
            {
                VmId = newVmId,
                Name = $"{oldVm.Name} (migrated)",
                Status = "Running",
                ContainerId = containerId,
                ProviderId = newProviderId,
                Image = oldVm.Image,
                RelayPort = relayPort,
                CpuCores = oldVm.CpuCores,
                RamGb = oldVm.RamGb,
                Client = consumerId,
                StartedAt = DateTime.UtcNow,
                VolumeName = volumeName,
                GcsBucket = oldVm.GcsBucket,
                GcsPath = gcsPath,
                VolumeSyncStatus = "Idle",
                SnapshotStatus = "Idle",
                OriginalVmId = oldVmId,
                ConsecutiveMisses = 0
            };

            await _vmService.CreateVmAsync(newVm);
            Console.WriteLine($"New VM created: {newVmId}");

            // 9. Update old VM: migration_status = "Migrated", status = "Stopped"
            oldVm.Status = "Stopped";
            oldVm.MigrationStatus = "Migrated";
            await _vmRepository.UpdateAsync(oldVmId, oldVm);

            // 10. Start monitoring new VM (wiring in Dashboard is preferred)
            // (This would normally be done by Dashboard.razor)

            // 11. Update request: completed
            request.Status = "completed";
            request.NewVmId = newVmId;
            request.CompletedAt = DateTime.UtcNow;
            await _requestRepository.UpdateAsync(requestId, request);

            Console.WriteLine($"✓ Migration completed: {oldVmId} → {newVmId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Migration failed for {vmId}: {ex.Message}");

            // Update request: failed
            request.Status = "failed";
            request.Error = ex.Message;
            await _requestRepository.UpdateAsync(requestId, request);

            // Update old VM: failed
            oldVm.MigrationStatus = "Failed";
            oldVm.MigrationError = ex.Message;
            await _vmRepository.UpdateAsync(oldVmId, oldVm);

            throw;
        }
        finally
        {
            _migrationsInProgress.TryRemove(vmId, out _);
        }
    }

    public async Task<bool> IsMigrationInProgressAsync(string vmId)
    {
        return _migrationsInProgress.ContainsKey(vmId);
    }

    private async Task<int> AllocateRelayPortAsync()
    {
        // Find available port in range 2222–2300
        // TODO: Implement proper port allocation (check which are already in use)
        return new Random().Next(2222, 2301);
    }
}
```

### Task 3: Add RestoreFromGcsAsync to VolumeBackupService

**File:** `providerunicore/Services/IVolumeBackupService.cs`

Add method:

```csharp
/// <summary>
/// Restore volume data from GCS into a container's volume (for migration).
/// </summary>
Task RestoreFromGcsAsync(string vmId, string gcsPath, CancellationToken ct = default);
```

**File:** `providerunicore/Services/VolumeBackupService.cs`

Implement:

```csharp
public async Task RestoreFromGcsAsync(string vmId, string gcsPath, CancellationToken ct = default)
{
    // This runs a one-time restore when container starts
    // The container's startup script will handle it via:
    // gsutil rsync gs://{GCS_BUCKET}/{GCS_PATH}home/ /home/consumer/
    // So this service just logs/tracks the restore

    Console.WriteLine($"Volume restore initiated for VM {vmId} from {gcsPath}");
    await Task.CompletedTask;
}
```

### Task 4: Add Migration Listener to Provider Dashboard

**File:** `providerunicore/Components/Pages/Dashboard.razor`

In `OnInitializedAsync`, set up the listener:

```csharp
private ListenerRegistration? _migrationListener;

protected override async Task OnInitializedAsync()
{
    // ... existing initialization ...

    // Set up migration request listener
    if (!string.IsNullOrEmpty(providerId))
    {
        _migrationListener = _vmMigrationRequestRepository
            .CreateQuery()
            .WhereEqualTo("target_provider_uid", providerId)
            .WhereEqualTo("status", "pending")
            .Listen(snapshot =>
            {
                foreach (var change in snapshot.Changes)
                {
                    if (change.Type == DocumentChangeType.Added)
                    {
                        var request = change.Document.ConvertTo<VmMigrationRequest>();
                        _ = ProcessMigrationRequestAsync(request);
                    }
                }
            });
    }
}

private async Task ProcessMigrationRequestAsync(VmMigrationRequest request)
{
    try
    {
        var migrationService = ServiceProvider.GetRequiredService<IMigrationService>();
        await migrationService.ProcessMigrationRequestAsync(request);

        // Start monitoring the new VM (from the request result)
        // TODO: Fetch the newly created VM and wire services
    }
    catch (Exception ex)
    {
        errorMessage = $"Migration failed: {ex.Message}";
        Console.WriteLine($"Migration error: {ex}");
    }
}

public async ValueTask DisposeAsync()
{
    _migrationListener?.Stop();
    // ... other cleanup ...
}
```

### Task 5: Register MigrationService in DI

**File:** `providerunicore/Program.cs`

```csharp
builder.Services.AddScoped<IMigrationService, MigrationService>();
```

---

## 🧪 Acceptance Criteria

- [ ] IMigrationService interface created
- [ ] MigrationService implemented with full state machine logic
- [ ] RestoreFromGcsAsync added to IVolumeBackupService
- [ ] Provider Dashboard has migration request listener
- [ ] ProcessMigrationRequestAsync wired in Dashboard
- [ ] Service registered in DI
- [ ] Error handling for all failure scenarios
- [ ] Old VM properly marked as "Migrated"
- [ ] New VM created with all correct metadata
- [ ] New VM starts monitoring (volume backup + snapshots)
- [ ] VmMigrationRequest updated throughout process
- [ ] No compilation errors
- [ ] Integration with all prior workstreams verified

---

## 🧠 Key State Transitions

```
VmMigrationRequest: pending → restoring → completed
VirtualMachine (old): Running → (other) → Stopped (migration_status="Migrated")
VirtualMachine (new): [created] → Running
```

### Error Handling

If any step fails:
1. Request status = "failed"
2. Old VM migration_status = "Failed"
3. Old VM migration_error = error message
4. New VM may be partially created (cleanup later if needed)

---

## 🔗 Related Code

**Existing patterns:**
- `ProcessRequestAsync` in Dashboard — template for ProcessMigrationRequestAsync
- `ContainerMonitorService` — pattern for monitoring
- `PauseResumeListenerService` — Firestore listener pattern

**Services this depends on:**
- `IDockerService` (Workstream 2)
- `ISnapshotService` (Workstream 5)
- `IVolumeBackupService` (Workstream 5)
- `IVmService` (existing, extended in Workstream 3)

---

## ⏱️ Time Estimate

- Reading this document: 20 min
- Implementing MigrationService: 60 min
- Adding RestoreFromGcsAsync: 10 min
- Wiring Dashboard listener: 30 min
- DI registration: 5 min
- Testing & refinement: 45 min

**Total: ~3 hours** (estimate 4–5 hours with edge cases)

---

## 🚀 Next Steps

Once complete:
- Workstream 7 (consumer UI) can be implemented
- Provider can autonomously migrate VMs when requests arrive

---

**Status:** Ready to implement (after Workstreams 1–5 complete)
**Owner:** Senior backend developer
**Next Workstream:** 7 (consumer migration UI)
