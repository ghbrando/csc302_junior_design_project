# UNI-60: Consumer Migration UI — Implementation Guide

## What's Already Done

All backend work is complete. You're building **pure UI** on top of existing services and API endpoints.

### Existing Services & Models

| Component | Location | Status |
|-----------|----------|--------|
| `IMigrationRequestService` | `consumerunicore/Services/IMigrationRequestService.cs` | Done |
| `MigrationRequestService` | `consumerunicore/Services/MigrationRequestService.cs` | Done |
| `VmMigrationRequest` model | `unicore.shared/Models/VmMigrationRequest.cs` | Done |
| `VirtualMachine` model | `unicore.shared/Models/VirtualMachine.cs` | Has `MigrationStatus`, `ProviderId`, `LastVolumeSyncAt`, `VolumeSyncStatus` |
| `Provider` model | `unicore.shared/Models/Provider.cs` | Has `NodeStatus`, `ConsistencyScore`, `Region`, `Name` |
| Service registration in `Program.cs` | `consumerunicore/Program.cs` | Done |

### Existing API Endpoints (`consumerunicore/Controllers/VmsController.cs`)

All endpoints require `[Authorize]`. Base route: `/api/vms`

| Method | Endpoint | What it does |
|--------|----------|--------------|
| `POST` | `/api/vms/{vmId}/migrate` | Creates a migration request. Body: `{ "targetProviderUid": "..." }` (optional — auto-selects if omitted) |
| `GET` | `/api/vms/{vmId}/migration-status` | Returns current migration status for a VM |
| `GET` | `/api/vms/{vmId}/migration-targets` | Returns available target providers (online, sorted by consistency score) |
| `POST` | `/api/vms/{vmId}/backup-now` | Triggers an on-demand backup |

### Existing UI (Dashboard + VmCard)

- **Dashboard** (`consumerunicore/Components/Pages/Dashboard.razor`) — has Firestore listener on `virtual_machines`, backup toast messages, backup button wiring
- **VmCard** (`consumerunicore/Components/Dashboard/VmCard.razor`) — shows backup status, "Backup Now" button, "Last backup: X min ago" timestamp

---

## What You Need To Build

### Task 1: Add "Migrate VM" Button to VmCard

**File:** `consumerunicore/Components/Dashboard/VmCard.razor`

Add new parameters to VmCard:

```csharp
[Parameter] public bool ShowMigrateButton { get; set; } = false;
[Parameter] public bool IsMigrating { get; set; } = false;
[Parameter] public string? MigrationStatus { get; set; }
[Parameter] public EventCallback OnMigrate { get; set; }
```

Add a migrate button in the template (after the backup row, before `vm-actions`):

```razor
@if (ShowMigrateButton || IsMigrating)
{
    <div class="vm-migrate-row">
        @if (IsMigrating)
        {
            <span class="migrate-status">
                <i class="bi bi-arrow-repeat spin"></i> MIGRATING — @MigrationStatus
            </span>
        }
        else
        {
            <button class="btn-migrate" @onclick="OnMigrate">
                <i class="bi bi-box-arrow-right"></i> MIGRATE VM
            </button>
        }
    </div>
}
```

**When to show it:** Only when the VM's provider `NodeStatus != "Online"`. The Dashboard already has the VM data — you need to pass the provider status down. The `VirtualMachine` model has `ProviderId`, and you can look up the provider's `NodeStatus` from Firestore.

**In Dashboard.razor**, wire the new parameters:

```razor
<VmCard ...existing params...
        ShowMigrateButton="@(vm.ProviderIsUnhealthy)"
        IsMigrating="@(vm.MigrationStatus is "Requested" or "Restoring")"
        MigrationStatus="@vm.MigrationStatus"
        OnMigrate="@(() => OpenMigrationDialog(vm.VmId, vm.ProviderId))" />
```

You'll need to update the `RunningVm` record to include `ProviderId`, `MigrationStatus`, and `ProviderIsUnhealthy`. To determine if a provider is unhealthy, you can either:
- Add a Firestore listener on the `providers` collection, or
- Fetch provider status when VMs load and store it in a dictionary

---

### Task 2: Create MigrationDialog Component

**New file:** `consumerunicore/Components/MigrationDialog.razor`

This is a modal dialog. Follow the same pattern as `CreateVmModal` in the project.

**What the dialog needs:**
1. Fetch available target providers from `GET /api/vms/{vmId}/migration-targets`
2. Display each provider with: name, region, consistency score, node status
3. Option to "Auto-select healthiest" (just omit `targetProviderUid` from the POST body)
4. Option to manually select a specific provider
5. Confirm/Cancel buttons
6. On confirm: `POST /api/vms/{vmId}/migrate` with optional `{ "targetProviderUid": "..." }`

**API response from `/migration-targets`:**
```json
[
  {
    "provider_uid": "abc123",
    "name": "Provider A",
    "region": "us-east1",
    "consistency_score": 98.5,
    "node_status": "Online"
  }
]
```

**API request body for `/migrate`:**
```json
{ "targetProviderUid": "abc123" }
```
Or send empty/null body to auto-select.

**API response from `/migrate`:**
```json
{
  "message": "Migration requested.",
  "migration_request_id": "guid-here",
  "source_provider": "old-uid",
  "target_provider": "new-uid",
  "status": "pending"
}
```

Wire the dialog into Dashboard.razor:

```razor
<MigrationDialog IsOpen="@_showMigrateDialog"
                 VmId="@_migrateVmId"
                 SourceProviderUid="@_migrateSourceProvider"
                 OnClose="@(() => _showMigrateDialog = false)"
                 OnMigrationRequested="@HandleMigrationRequested" />
```

---

### Task 3: Add Real-Time Migration Progress Listener

**File:** `consumerunicore/Components/Pages/Dashboard.razor`

The dashboard already has a Firestore listener for VMs (`_vmListener`). You need a second listener for `vm_migration_requests` to track migration progress in real time.

In `StartListenerAsync()`, add:

```csharp
var migrationQuery = FirestoreDb
    .Collection("vm_migration_requests")
    .WhereEqualTo("consumer_uid", uid);

_migrationListener = migrationQuery.Listen(OnMigrationSnapshot);
```

Add a new field:

```csharp
private FirestoreChangeListener? _migrationListener;
private Dictionary<string, VmMigrationRequest> _activeMigrations = new();
```

Handle the snapshot:

```csharp
private void OnMigrationSnapshot(QuerySnapshot snapshot)
{
    _ = InvokeAsync(() =>
    {
        _activeMigrations = snapshot.Documents
            .Select(d => d.ConvertTo<VmMigrationRequest>())
            .Where(m => m.Status is "pending" or "restoring")
            .ToDictionary(m => m.VmId);
        StateHasChanged();
    });
}
```

Don't forget to stop it in `DisposeAsync()`:

```csharp
if (_migrationListener != null)
    await _migrationListener.StopAsync();
```

**Migration status values** (from `VmMigrationRequest.Status`):
- `"pending"` — request created, waiting for orchestrator
- `"restoring"` — migration in progress
- `"Completed"` — done
- `"Failed"` — error (check `Error` field)

---

### Task 4: Success/Error Notifications

Reuse the existing toast pattern from backup messages in Dashboard.razor. When the migration listener detects a status change to `"Completed"` or `"Failed"`:

```csharp
// Inside OnMigrationSnapshot, detect completion:
foreach (var migration in allMigrations)
{
    if (migration.Status == "Completed" && _activeMigrations.ContainsKey(migration.VmId))
    {
        _backupMessage = $"Migration complete! Your VM is now on a new provider.";
        _backupMessageIsError = false;
    }
    else if (migration.Status == "Failed" && _activeMigrations.ContainsKey(migration.VmId))
    {
        _backupMessage = $"Migration failed: {migration.Error}";
        _backupMessageIsError = true;
    }
}
```

---

### Task 5 (Optional): ProviderHealthCard Component

**New file:** `consumerunicore/Components/ProviderHealthCard.razor`

Used inside the MigrationDialog to display each target provider option:

```
+-----------------------------------------+
| Provider Name          Region: us-east1 |
| Score: 98.5%           Status: ONLINE   |
|                     [Select This Provider]|
+-----------------------------------------+
```

---

### Task 6 (Optional): BackupHistoryPanel Component

**New file:** `consumerunicore/Components/BackupHistoryPanel.razor`

Query Firestore for historical backup/snapshot data. The `VirtualMachine` model has `LastVolumeSyncAt` and `LastSnapshotAt` but a full history would need a subcollection or separate query.

---

## Key Patterns to Follow

1. **Blazor Server rendering** — the app uses `InteractiveServerRenderMode` with `prerender: false`
2. **Firestore listeners** — use `query.Listen(callback)` pattern (see Dashboard.razor lines 142-158)
3. **State updates** — always wrap in `InvokeAsync(() => { ...; StateHasChanged(); })`
4. **API calls from Blazor** — use `HttpClient` with the auth token, or call services directly via DI
5. **CSS** — scoped `<style>` blocks at bottom of each `.razor` file, using CSS variables like `--uc-green`, `--uc-red`, `--uc-bg-card`, etc.
6. **Icons** — Bootstrap Icons (`bi-*` classes)

## File Checklist

- [ ] `consumerunicore/Components/Dashboard/VmCard.razor` — add migrate button + migration status display
- [ ] `consumerunicore/Components/Pages/Dashboard.razor` — add migration listener, provider health tracking, dialog wiring
- [ ] `consumerunicore/Components/MigrationDialog.razor` — **NEW** — confirmation dialog with provider selection
- [ ] `consumerunicore/Components/ProviderHealthCard.razor` — **NEW** (optional) — provider health display card
- [ ] `consumerunicore/Components/BackupHistoryPanel.razor` — **NEW** (optional) — backup history list
