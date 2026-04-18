# VM Deletion Cleanup — Design

**Date:** 2026-04-18
**Status:** Draft — pending review

## Problem

The consumer "Delete VM" button in `StoppedVmTable` currently calls `ConsumerVmService.DeleteVmAsync`, which only removes the Firestore `virtual_machines` document. Two resources leak:

1. **GCS volume backups** under `gs://unicore-vm-volumes/consumers/{client}/{vmId}/home/`
2. **Artifact Registry Docker snapshots** under `us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots/{vmId}:*`

Users also delete without confirmation, risking accidental data loss.

## Goals

- Full cleanup of GCS backup objects and Artifact Registry snapshots when a consumer deletes a VM.
- Clear warning before deletion that data loss is permanent.
- Deletion is resilient to browser-close and individual-step failure.

## Non-goals

- Undo / soft-delete / recycle bin.
- Retention policies or delayed cleanup.
- Cleaning orphaned resources not linked to a VM doc.
- Unit tests for the cleanup service (manual verification only — see Testing).

## Architecture

```
Consumer clicks Delete
    → Confirmation modal
    → Consumer writes Firestore flag (deletion_requested=true, status="Deleting")
    → VM hidden from UI (list filter)

Provider-side VmCleanupService (BackgroundService, 30s poll):
    → Query virtual_machines WHERE deletion_requested=true
    → For each VM:
        1. Firestore transaction: Requested → CleaningGcs (skip if already claimed)
        2. Delete all GCS objects under consumers/{client}/{vmId}/home/
        3. Set deletion_status=CleaningSnapshots
        4. Delete all Artifact Registry versions of package {vmId}
        5. Delete the Firestore document
    → On failure: deletion_status=Error (next cycle retries)
```

**Why async signal-based rather than direct deletion:** GCS + Artifact Registry delete credentials already live on the provider side. Adding them to the consumer app would broaden its IAM surface. The signal pattern also matches the existing backup-request flow (consumer writes Firestore flag, provider service acts on it) and is resilient to the browser closing mid-delete.

## Data Model

Add two fields to `unicore.shared/Models/VirtualMachine.cs`:

```csharp
[FirestoreProperty("deletion_requested")]
public bool DeletionRequested { get; set; } = false;

[FirestoreProperty("deletion_status")]
public string? DeletionStatus { get; set; }
```

**Status transitions:** `null → "Requested" → "CleaningGcs" → "CleaningSnapshots" → (doc deleted)`. `"Error"` on any failure.

No Firestore migration needed — existing docs read `false` / `null`.

## Components

### UI — `consumerunicore/Components/Dashboard/StoppedVmTable.razor`

- Add inline Bootstrap modal (hidden by default), matching the pattern in `CreateVmModal.razor` and `MigrationDialog.razor`.
- Delete button sets `_vmPendingDelete = vmId` and opens the modal instead of invoking `OnDelete` directly.
- Modal body text: "Delete VM '{name}'? This action permanently erases all files, GCS backups, and snapshots. **This cannot be undone.**"
- Buttons: `Cancel` (secondary) / `Delete VM` (danger red). Only the Confirm button calls `OnDelete.InvokeAsync(vmId)`.

### Dashboard filter — `consumerunicore/Components/Pages/Dashboard.razor`

Extend the `_stoppedVms` filter at lines 298–301 so deletion-marked VMs vanish from the UI immediately:

```csharp
_stoppedVms = _allVms
    .Where(vm => vm.Status is not ("Running" or "Provisioning")
               && vm.MigrationStatus != "Migrated"
               && !vm.DeletionRequested)
    .Select(MapStopped)
    .ToList();
```

### Consumer service — `consumerunicore/Services/ConsumerVmService.cs`

Replace the raw Firestore delete in `DeleteVmAsync`:

```csharp
public async Task DeleteVmAsync(string vmId)
{
    var vm = await _vmRepository.GetByIdAsync(vmId)
        ?? throw new Exception($"VM {vmId} not found");

    vm.DeletionRequested = true;
    vm.DeletionStatus = "Requested";
    vm.Status = "Deleting";

    await _vmRepository.UpdateAsync(vmId, vm);
}
```

### Provider cleanup service — `providerunicore/Services/VmCleanupService.cs` (new)

`BackgroundService` that polls every 30s. Mirrors the structure of `VolumeBackupService` and reuses its `CreateStorageClientAsync` pattern for GCS credentials.

**Sketch:**
```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    while (await timer.WaitForNextTickAsync(ct))
    {
        try
        {
            var snapshot = await _firestoreDb.Collection("virtual_machines")
                .WhereEqualTo("deletion_requested", true)
                .GetSnapshotAsync(ct);

            foreach (var doc in snapshot.Documents)
                await CleanupVmAsync(doc.ConvertTo<VirtualMachine>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (Exception ex) { _logger.LogWarning(ex, "VmCleanupService loop failed."); }
    }
}
```

**`CleanupVmAsync` steps (each idempotent):**
1. Firestore transaction: read current `deletion_status`; only transition `"Requested"` → `"CleaningGcs"`. If another instance already claimed it, skip.
2. Resolve bucket name from `_configuration["Backups:VolumeBucket"] ?? "unicore-vm-volumes"` (same fallback as `VolumeBackupService`). List objects under `consumers/{vm.Client}/{vm.VmId}/home/`; delete each via `StorageClient.DeleteObjectAsync`. 404 on bucket/prefix is treated as success.
3. Update `deletion_status = "CleaningSnapshots"`.
4. Parse the Artifact Registry repository from `_configuration["ArtifactRegistry:Repository"] ?? "us-central1-docker.pkg.dev/unicore-junior-design/unicore-vm-snapshots"` (same fallback as `SnapshotService`) into `{location, project, repository}`. Using `Google.Cloud.ArtifactRegistry.V1`, list all versions of package `{vmId}` in that repository and delete each version. 404 is treated as success.
5. Delete the Firestore doc.
6. Any unhandled exception → update `deletion_status = "Error"` and log. Next cycle re-queries and retries.

**Registration:** `builder.Services.AddHostedService<VmCleanupService>()` in `providerunicore/Program.cs`.

**Package:** add `Google.Cloud.ArtifactRegistry.V1` to `providerunicore/providerunicore.csproj`.

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Partial cleanup (GCS succeeded, AR failed) | Status stays `"CleaningSnapshots"`; next poll re-runs. Both steps are idempotent. |
| Bucket or package missing (404) | Treated as success — nothing to clean. |
| VM never had a backup | GCS listing returns 0 objects → skip to snapshot step. |
| VM never had a snapshot | AR version list returns 0 → skip to Firestore delete. |
| Persistent `"Error"` state | Surfaced in Firestore; manual admin intervention. Not auto-retried forever to keep blast radius contained. |
| Two provider instances pick up same VM | Firestore transaction at step 1 ensures only one instance claims; the other skips. |
| User double-clicks Delete | Button disables during modal close; second `DeleteVmAsync` is a no-op since `deletion_requested` is already `true`. |

## Testing

**Manual golden path:** create test VM → request backup → trigger snapshot → click Delete → confirm modal → verify:
- VM disappears from stopped list immediately
- Within ~30s: `gs://unicore-vm-volumes/consumers/{uid}/{vmId}/home/` is empty
- Artifact Registry package `{vmId}` has no versions
- Firestore doc is gone

**Negative cases:**
- Delete a VM that never had backups or snapshots → cleans up gracefully, doc deleted.
- Close browser between confirm and next poll → cleanup still completes on next poll cycle.

**Failure injection:**
- Temporarily revoke AR delete IAM perms → verify `deletion_status = "Error"` appears in Firestore → restore perms → next poll completes cleanup.

No unit tests. The cleanup service is thin glue over GCS + Artifact Registry SDKs; meaningful behavior only emerges at the integration boundary, which isn't cheap to mock here. Manual verification is the pragmatic level.

## Out of scope / future work

- Surfacing `deletion_status = "Error"` in the consumer UI (currently only visible in Firestore console).
- Bulk delete or multi-select.
- Admin tooling to force-retry a stuck cleanup.
