# Workstream 7: Consumer Migration UI

**Type:** Frontend
**Depends On:** Workstreams 1–6 (all backend must be complete)
**Blocks:** Nothing (final workstream)
**Estimated Scope:** 300 lines, 6 hours
**Owner:** Frontend developer

---

## 🎯 Overview

Add UI elements and interactions for consumers to:
1. See when their VM's last backup was taken
2. Click "Backup Now" to trigger an on-demand snapshot
3. Click "Migrate VM" when their provider is unhealthy
4. See migration status as it progresses

All backend pieces exist by this point; this workstream is pure UI + service integration.

---

## 🏗️ Architecture

### Consumer Dashboard with Backup/Migration Features

```
┌─ VM Card ─────────────────────────────┐
│                                        │
│  VM Name                               │
│  Status: RUNNING                       │
│  Last backup: 3 min ago     [Backup ↻] │
│                                        │
│  [Open Terminal]  [Migrate VM]         │
│  (always visible) (when unhealthy)     │
│                                        │
└────────────────────────────────────────┘
```

### Workflow: Consumer Requests Migration

```
1. Consumer sees VM with unhealthy provider (ConsecutiveMisses > 5)
   ↓
2. Consumer clicks "Migrate VM" button
   ↓
3. Modal appears: "Migrate this VM?"
   - Warning: "Your home directory will be preserved."
   - "Installed packages will be restored from the last snapshot."
   - [Cancel] [Confirm]
   ↓
4. Consumer clicks Confirm
   ↓
5. Service calls: ConsumerVmService.RequestMigrationAsync(vmId)
   - Finds healthy replacement provider (matchmaking)
   - Creates VmMigrationRequest in Firestore
   - Sets VM.migration_status = "Requested"
   ↓
6. UI shows badge: "MIGRATING" (spinner)
   ↓
7. Provider app (Workstream 6) picks up request
   - Restores VM on new provider
   - Updates VmMigrationRequest.status = "completed"
   - Creates new VM
   ↓
8. Consumer dashboard updates
   - Old VM disappears (or marked as "Migrated")
   - New VM appears in list
   - Consumer can click "Open Terminal" on new VM
```

---

## 🔌 Integration Points

### Files to Create

None (all reuse existing Blazor components)

### Files to Modify

1. **`consumerunicore/Services/IConsumerVmService.cs`**
   - Add: `RequestMigrationAsync(vmId)`
   - Add: `RequestBackupAsync(vmId)`

2. **`consumerunicore/Services/ConsumerVmService.cs`**
   - Implement both methods

3. **`consumerunicore/Components/Dashboard/VmCard.razor`**
   - Add "Backup Now" button (always visible)
   - Add "Migrate VM" button (visible when unhealthy or on request)
   - Add "Last backup: X min ago" subtitle
   - Add migration status badge

4. **`consumerunicore/Components/Pages/Dashboard.razor`**
   - Add migration confirmation modal
   - Add handler: `HandleMigrateVm(vmId)`
   - Add Firestore listener watching migration status on old VM
   - Map new VM fields into display

5. **`consumerunicore/Program.cs`**
   - Register `IFirestoreRepository<VmMigrationRequest>`

---

## ✅ What Needs to Be Done

### Task 1: Add Migration Methods to IConsumerVmService

**File:** `consumerunicore/Services/IConsumerVmService.cs`

Add:

```csharp
/// <summary>
/// Request migration of a VM to a healthy provider.
/// </summary>
Task RequestMigrationAsync(string vmId);

/// <summary>
/// Request an on-demand backup/snapshot of a VM.
/// </summary>
Task RequestBackupAsync(string vmId);
```

### Task 2: Implement Migration Methods

**File:** `consumerunicore/Services/ConsumerVmService.cs`

```csharp
public async Task RequestMigrationAsync(string vmId)
{
    var vm = await _vmRepository.GetByIdAsync(vmId);
    if (vm == null)
        throw new InvalidOperationException($"VM {vmId} not found");

    if (vm.MigrationStatus != null && vm.MigrationStatus != "Migrated")
        throw new InvalidOperationException("Migration already in progress");

    // Run matchmaking to find replacement provider
    var replacementProvider = await MatchmakeAsync(
        cpuCores: vm.CpuCores,
        ramGb: vm.RamGb,
        volumeGb: int.Parse(vm.VolumeName?.Split('-').Last() ?? "50"),
        excludeProviderId: vm.ProviderId); // Exclude source provider

    if (replacementProvider == null)
        throw new InvalidOperationException("No healthy providers available");

    // Create migration request
    var request = new VmMigrationRequest
    {
        MigrationRequestId = Guid.NewGuid().ToString().Substring(0, 8),
        VmId = vmId,
        ConsumerUid = currentConsumerId, // From Auth context
        SourceProviderUid = vm.ProviderId,
        TargetProviderUid = replacementProvider.FirebaseUid,
        Status = "pending",
        RequestedAt = DateTime.UtcNow
    };

    await _migrationRequestRepository.CreateAsync(request);

    // Mark old VM as requested
    vm.MigrationStatus = "Requested";
    vm.MigrationRequestedAt = DateTime.UtcNow;
    await _vmRepository.UpdateAsync(vmId, vm);
}

public async Task RequestBackupAsync(string vmId)
{
    var vm = await _vmRepository.GetByIdAsync(vmId);
    if (vm == null)
        throw new InvalidOperationException($"VM {vmId} not found");

    // Set flag in Firestore for provider to notice
    vm.VolumeSyncStatus = "Syncing"; // Could use a dedicated flag like "force_sync_requested"
    await _vmRepository.UpdateAsync(vmId, vm);

    // Note: Container's cron job will handle the actual sync
    // This just marks it as "user requested now, don't wait 5 min"
}
```

### Task 3: Update VmCard Component

**File:** `consumerunicore/Components/Dashboard/VmCard.razor`

Add buttons and status display:

```razor
@* At the top of the card, show last backup time *@
<div class="vm-status">
    <span class="badge bg-info">@vm.Status?.ToUpper()</span>

    @if (vm.MigrationStatus != null)
    {
        <span class="badge bg-warning">
            @if (vm.MigrationStatus == "Requested")
            {
                <span class="spinner-border spinner-border-sm me-2"></span>Migration Pending
            }
            else if (vm.MigrationStatus == "Restoring")
            {
                <span class="spinner-border spinner-border-sm me-2"></span>Restoring
            }
            else if (vm.MigrationStatus == "Migrated")
            {
                <span>✓ Migrated</span>
            }
        </span>
    }
</div>

@* Show backup info *@
<small class="text-muted">
    Last backup: @(vm.LastVolumeSyncAt?.ToString("MMM dd, h:mm tt") ?? "Never")
    (@GetTimeSinceSync(vm.LastVolumeSyncAt))
</small>

@* Action buttons *@
<div class="card-footer mt-3">
    <button class="btn btn-sm btn-outline-secondary"
            @onclick="() => OnBackupClick(vm.VmId)"
            disabled="@(vm.VolumeSyncStatus == "Syncing")">
        📦 Backup Now
    </button>

    @* Show migrate button if provider is unhealthy or always *@
    @if (vm.ConsecutiveMisses > 5 || true) @* adjust condition as needed *@
    {
        <button class="btn btn-sm btn-outline-danger"
                @onclick="() => OnMigrateClick(vm.VmId)"
                disabled="@(vm.MigrationStatus != null)">
            🚀 Migrate VM
        </button>
    }
</div>

@code {
    private string GetTimeSinceSync(DateTime? lastSync)
    {
        if (lastSync == null) return "";
        var elapsed = DateTime.UtcNow - lastSync.Value;
        if (elapsed.TotalMinutes < 1) return "moments ago";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours} hours ago";
        return $"{(int)elapsed.TotalDays} days ago";
    }

    private async Task OnBackupClick(string vmId)
    {
        try
        {
            await ConsumerVmService.RequestBackupAsync(vmId);
            successMessage = "Backup requested. Check status in a moment.";
        }
        catch (Exception ex)
        {
            errorMessage = $"Backup failed: {ex.Message}";
        }
    }

    private async Task OnMigrateClick(string vmId)
    {
        showMigrationModal = true;
        selectedVmForMigration = vmId;
    }
}
```

### Task 4: Add Migration Modal and Handler to Dashboard

**File:** `consumerunicore/Components/Pages/Dashboard.razor`

Add the modal:

```razor
@* Migration confirmation modal *@
@if (showMigrationModal && !string.IsNullOrEmpty(selectedVmForMigration))
{
    <div class="modal-overlay" @onclick="() => showMigrationModal = false">
        <div class="modal-content" @onclick:stopPropagation>
            <div class="modal-header">
                <h5 class="modal-title">Migrate VM</h5>
                <button type="button" class="btn-close" @onclick="() => showMigrationModal = false"></button>
            </div>
            <div class="modal-body">
                <p>Are you sure you want to migrate this VM?</p>
                <div class="alert alert-info">
                    <strong>What happens:</strong>
                    <ul>
                        <li>Your home directory will be fully preserved</li>
                        <li>Installed packages and configs will be restored from the last snapshot</li>
                        <li>The migration may take a few minutes</li>
                        <li>You'll receive a new SSH connection string</li>
                    </ul>
                </div>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn btn-secondary" @onclick="() => showMigrationModal = false">
                    Cancel
                </button>
                <button type="button" class="btn btn-danger" @onclick="() => HandleMigrateVm(selectedVmForMigration)">
                    Migrate VM
                </button>
            </div>
        </div>
    </div>
}

@code {
    private bool showMigrationModal = false;
    private string? selectedVmForMigration = null;

    private async Task HandleMigrateVm(string vmId)
    {
        try
        {
            await _consumerVmService.RequestMigrationAsync(vmId);
            showMigrationModal = false;
            successMessage = "Migration requested. Waiting for assignment...";

            // Optionally: set up listener to watch migration status
            var vm = await _vmRepository.GetByIdAsync(vmId);
            ListenToMigrationStatus(vm);
        }
        catch (Exception ex)
        {
            errorMessage = $"Migration request failed: {ex.Message}";
        }
    }

    private void ListenToMigrationStatus(VirtualMachine vm)
    {
        // Watch the old VM's migration_status for completion
        var listener = _vmRepository.CreateQuery()
            .WhereEqualTo("vm_id", vm.VmId)
            .Listen(snapshot =>
            {
                var updated = snapshot.Documents.First().ConvertTo<VirtualMachine>();
                if (updated.MigrationStatus == "Migrated" || updated.MigrationStatus == "Failed")
                {
                    // Trigger refresh of VM list
                    StateHasChanged();
                }
            });

        // TODO: Store listener reference for cleanup
    }
}
```

### Task 5: Add Migration Fields to VM Display

**File:** `consumerunicore/Components/Pages/Dashboard.razor`

Update the VM display record to include new fields:

```csharp
public record RunningVm(
    string VmId,
    string Name,
    string Status,
    string Image,
    int CpuCores,
    int RamGb,
    DateTime StartedAt,
    double CpuUsagePercent,
    double RamUsagePercent,
    int ConsecutiveMisses,
    // NEW FIELDS
    DateTime? LastVolumeSyncAt,
    string? VolumeSyncStatus,
    DateTime? LastSnapshotAt,
    string? SnapshotStatus,
    string? MigrationStatus,
    string? OriginalVmId);
```

Update the mapping from Firestore to RunningVm:

```csharp
var runningVms = vms
    .Where(vm => vm.Status == "Running")
    .Select(vm => new RunningVm(
        vm.VmId,
        vm.Name,
        vm.Status,
        vm.Image,
        vm.CpuCores,
        vm.RamGb,
        vm.StartedAt,
        vm.CurrentCpuUsage,
        vm.CurrentRamUsage,
        vm.ConsecutiveMisses,
        // NEW
        vm.LastVolumeSyncAt,
        vm.VolumeSyncStatus,
        vm.LastSnapshotAt,
        vm.SnapshotStatus,
        vm.MigrationStatus,
        vm.OriginalVmId))
    .ToList();
```

### Task 6: Register Repository in DI

**File:** `consumerunicore/Program.cs`

```csharp
builder.Services.AddFirestoreRepository<VmMigrationRequest>(
    collectionName: "vm_migration_requests",
    documentIdSelector: r => r.MigrationRequestId);
```

---

## 🧪 Acceptance Criteria

- [ ] IConsumerVmService has `RequestMigrationAsync` and `RequestBackupAsync`
- [ ] Both methods implemented with proper matchmaking and Firestore updates
- [ ] VmCard shows "Last backup: X min ago" timestamp
- [ ] VmCard has "Backup Now" button (always visible, disabled while syncing)
- [ ] VmCard has "Migrate VM" button (visible when unhealthy)
- [ ] VmCard shows migration status badge (Pending/Restoring/Migrated)
- [ ] Migration confirmation modal appears with helpful description
- [ ] Dashboard handler `HandleMigrateVm` calls service
- [ ] Dashboard listens to old VM's migration_status for updates
- [ ] UI refreshes when migration completes
- [ ] New VM appears in dashboard after migration done
- [ ] VmMigrationRequest registered in DI
- [ ] No compilation errors
- [ ] All new fields properly mapped from Firestore

---

## 🎨 UI Patterns to Reuse

**Existing modal pattern (PauseModal, etc.):**
- Uses `modal-overlay` + `modal-content` div structure
- `@onclick:stopPropagation` to prevent closing on inner click
- Button styling: `btn btn-primary`, `btn btn-danger`, etc.

**Existing status badges:**
- `<span class="badge bg-info">RUNNING</span>`
- `<span class="badge bg-warning">PAUSED</span>`
- Add: `<span class="badge bg-warning">MIGRATING</span>`

**Existing timestamp display:**
- Format: `vm.StartedAt?.ToString("MMM dd, h:mm tt")`
- Relative time: Calculate with `DateTime.UtcNow - timestamp`

---

## 🔗 Related Code

**Existing UI patterns:**
- `consumerunicore/Components/Dashboard/VmCard.razor` — Button layout
- `consumerunicore/Components/Pages/Dashboard.razor` — Modal pattern, handlers

**Existing service calls:**
- `@inject IConsumerVmService ConsumerVmService` in components
- `await ConsumerVmService.PauseVmAsync(vmId)` pattern

**Existing Firestore listeners:**
- `_vmRepository.CreateQuery().WhereEqualTo(...).Listen(...)`

---

## ⏱️ Time Estimate

- Reading this document: 15 min
- Adding migration methods to service: 20 min
- Updating VmCard component: 30 min
- Adding migration modal to Dashboard: 30 min
- Adding display fields: 15 min
- DI registration: 5 min
- Testing & refinement: 30 min

**Total: ~2.5 hours** (estimate 3–4 hours with CSS/styling refinements)

---

## 🚀 Deployment & Rollout

Once complete:
1. All workstreams finished
2. Feature complete: consumers can backup and migrate VMs
3. Providers autonomously handle migrations
4. System is production-ready for VM redundancy

---

**Status:** Ready to implement (after Workstreams 1–6 complete)
**Owner:** Frontend developer
**Next:** Deployment!
