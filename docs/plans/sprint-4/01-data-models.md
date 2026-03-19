# Workstream 1: Data Models — Adding Backup & Migration Fields

**Type:** Backend
**Depends On:** Nothing (first workstream)
**Blocks:** Everything else
**Estimated Scope:** 100 lines, 2 hours
**Owner:** Backend developer

---

## 🎯 Overview

Add 12 new fields to the `VirtualMachine` model to track backup status, snapshots, and migration state. Create a new `VmMigrationRequest` model to orchestrate VM failover between providers.

**Why:** Every other workstream depends on these fields existing. Without them, there's nowhere to store backup timestamps, snapshot image references, migration status, etc.

---

## 🏗️ Architecture

### Current State

`VirtualMachine.cs` in `unicore.shared/Models/`:
- Has fields like `VmId`, `ProviderId`, `Status`, `ContainerId`
- Tracks uptime, resource usage, paused state
- **Missing:** Anything related to backups, snapshots, or migration

### New Fields (on VirtualMachine)

| Firestore Field | C# Property | Type | Purpose |
|---|---|---|---|
| `volume_name` | VolumeName | string? | Docker volume identifier (e.g., "unicore-vol-{vmId}") |
| `gcs_bucket` | GcsBucket | string? | GCS bucket name (e.g., "unicore-vm-volumes") |
| `gcs_path` | GcsPath | string? | GCS path prefix (e.g., "consumers/{uid}/{vmId}/") |
| `last_volume_sync_at` | LastVolumeSyncAt | DateTime? | Timestamp of last successful volume backup |
| `volume_sync_status` | VolumeSyncStatus | string? | "Idle" \| "Syncing" \| "Error" |
| `snapshot_image` | SnapshotImage | string? | Full registry path to latest snapshot image |
| `last_snapshot_at` | LastSnapshotAt | DateTime? | Timestamp of last successful snapshot |
| `snapshot_status` | SnapshotStatus | string? | "Idle" \| "Committing" \| "Pushing" \| "Error" |
| `migration_status` | MigrationStatus | string? | "Requested" \| "Restoring" \| "Migrated" \| "Failed" |
| `migration_requested_at` | MigrationRequestedAt | DateTime? | When migration was initiated |
| `migration_error` | MigrationError | string? | Error message if migration failed |
| `original_vm_id` | OriginalVmId | string? | Points to previous VM if this is a migrated instance |

### New Model: VmMigrationRequest

**File:** `unicore.shared/Models/VmMigrationRequest.cs`

**Purpose:** Tracks the state machine of migrating a VM from one provider to another

```csharp
[FirestoreData]
public class VmMigrationRequest
{
    [FirestoreProperty("migration_request_id")]
    public string MigrationRequestId { get; set; }

    [FirestoreProperty("vm_id")]
    public string VmId { get; set; }

    [FirestoreProperty("consumer_uid")]
    public string ConsumerUid { get; set; }

    [FirestoreProperty("source_provider_uid")]
    public string SourceProviderUid { get; set; }

    [FirestoreProperty("target_provider_uid")]
    public string TargetProviderUid { get; set; }

    [FirestoreProperty("status")]
    public string Status { get; set; } // "pending" → "restoring" → "completed" or "failed"

    [FirestoreProperty("requested_at")]
    public DateTime RequestedAt { get; set; }

    [FirestoreProperty("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [FirestoreProperty("error")]
    public string? Error { get; set; }

    [FirestoreProperty("new_vm_id")]
    public string? NewVmId { get; set; } // The VM ID on the new provider after successful migration
}
```

**Firestore Collection:** `vm_migration_requests`
**Document ID:** Auto-generated or use `MigrationRequestId` (Guid)

---

## 🔌 Integration Points

### Files to Modify

1. **`unicore.shared/Models/VirtualMachine.cs`**
   - Add 12 new properties with `[FirestoreProperty]` attributes
   - All nullable (consumers with old VMs won't have these fields initially)

2. **`unicore.shared/Models/VmMigrationRequest.cs`** (NEW FILE)
   - Create new model with FirestoreData attributes
   - Must follow same pattern as VirtualMachine.cs

3. **`providerunicore/Program.cs`**
   - Register `IFirestoreRepository<VmMigrationRequest>` in DI
   - Use collection name: `"vm_migration_requests"`

4. **`consumerunicore/Program.cs`**
   - Register `IFirestoreRepository<VmMigrationRequest>` in DI
   - Same collection name

### Firestore Pattern to Follow

Use the exact pattern from existing models:

```csharp
[FirestoreData]
public class ExistingModel
{
    [FirestoreProperty("firestore_field_name")]
    public string CsharpPropertyName { get; set; }
}
```

**Example from VirtualMachine.cs:**
```csharp
[FirestoreProperty("vm_id")]
public string VmId { get; set; }

[FirestoreProperty("status")]
public string Status { get; set; }

[FirestoreProperty("consecutive_misses")]
public int ConsecutiveMisses { get; set; }
```

**Your task:** Add 12 new properties following this exact pattern.

---

## ✅ What Needs to Be Done

### Task 1: Extend VirtualMachine Model

**File:** `unicore.shared/Models/VirtualMachine.cs`

Add these 12 properties (at the end of the class):

```csharp
[FirestoreProperty("volume_name")]
public string? VolumeName { get; set; }

[FirestoreProperty("gcs_bucket")]
public string? GcsBucket { get; set; }

[FirestoreProperty("gcs_path")]
public string? GcsPath { get; set; }

[FirestoreProperty("last_volume_sync_at")]
public DateTime? LastVolumeSyncAt { get; set; }

[FirestoreProperty("volume_sync_status")]
public string? VolumeSyncStatus { get; set; }

[FirestoreProperty("snapshot_image")]
public string? SnapshotImage { get; set; }

[FirestoreProperty("last_snapshot_at")]
public DateTime? LastSnapshotAt { get; set; }

[FirestoreProperty("snapshot_status")]
public string? SnapshotStatus { get; set; }

[FirestoreProperty("migration_status")]
public string? MigrationStatus { get; set; }

[FirestoreProperty("migration_requested_at")]
public DateTime? MigrationRequestedAt { get; set; }

[FirestoreProperty("migration_error")]
public string? MigrationError { get; set; }

[FirestoreProperty("original_vm_id")]
public string? OriginalVmId { get; set; }
```

### Task 2: Create VmMigrationRequest Model

**File:** `unicore.shared/Models/VmMigrationRequest.cs` (NEW)

Copy the structure above. Follow the FirestoreData pattern.

### Task 3: Register Repository in Provider App

**File:** `providerunicore/Program.cs`

Find the section where repositories are registered (look for `AddFirestoreRepository<VirtualMachine>`).

Add:
```csharp
builder.Services.AddFirestoreRepository<VmMigrationRequest>(
    collectionName: "vm_migration_requests",
    documentIdSelector: r => r.MigrationRequestId);
```

### Task 4: Register Repository in Consumer App

**File:** `consumerunicore/Program.cs`

Same as Task 3:
```csharp
builder.Services.AddFirestoreRepository<VmMigrationRequest>(
    collectionName: "vm_migration_requests",
    documentIdSelector: r => r.MigrationRequestId);
```

---

## 🧪 Acceptance Criteria

- [ ] All 12 fields added to VirtualMachine.cs with correct Firestore property names
- [ ] VmMigrationRequest.cs created with all required fields
- [ ] Both models compile without errors
- [ ] Provider app: `IFirestoreRepository<VmMigrationRequest>` registered in DI
- [ ] Consumer app: `IFirestoreRepository<VmMigrationRequest>` registered in DI
- [ ] Firestore can be queried for `VmMigrationRequest` documents
- [ ] Existing VMs still work (fields are nullable)
- [ ] New VMs can have these fields populated

---

## 🧠 Why This Approach?

**Why nullable fields?**
- Existing VMs in Firestore won't have these fields
- Firestore is schema-less; missing fields are OK
- New VMs will have all fields set when created

**Why VmMigrationRequest is separate?**
- Cleaner separation of concerns
- Migration requests have different lifecycle than VMs
- Easier to query "pending migrations" or "failed migrations"

**Why Firestore field names are snake_case?**
- Firestore convention (matches existing fields like `vm_id`, `status`)
- C# properties use PascalCase (matches existing `VmId`, `Status`)

---

## 📋 Code Review Checklist

Before marking as complete, verify:

- [ ] Firestore field names match the design doc exactly (snake_case)
- [ ] C# property names match C# conventions (PascalCase)
- [ ] All nullable types use `?` (e.g., `string?`, `DateTime?`)
- [ ] Both models have `[FirestoreData]` attribute
- [ ] All properties have `[FirestoreProperty("...")]` attributes
- [ ] No typos in Firestore field names
- [ ] DI registration uses correct collection names
- [ ] Models follow existing code style (spacing, naming, order)

---

## 🔗 Related Code

**Existing Models to Reference:**
- `unicore.shared/Models/VirtualMachine.cs` — Main model (add fields here)
- `unicore.shared/Models/Provider.cs` — Similar structure
- `unicore.shared/Models/Consumer.cs` — Similar structure

**DI Registration Pattern:**
- `providerunicore/Program.cs` (lines ~50–70) — See existing `AddFirestoreRepository<T>` calls

**Firestore Property Attributes:**
- See any existing model (VirtualMachine, Provider, Consumer, etc.)

---

## ⏱️ Time Estimate

- Reading this document: 10 min
- Adding properties to VirtualMachine.cs: 10 min
- Creating VmMigrationRequest.cs: 10 min
- Registering repositories in DI: 10 min
- Testing that models compile: 10 min
- Code review + refinement: 10 min

**Total: 60 minutes**

---

## ❓ Questions to Answer Before Starting

- [ ] Are there any other backup-related fields we should add?
- [ ] Should VMs have a `backup_enabled` flag? (Or assume all new VMs have backups?)
- [ ] Should there be a `backup_retention_days` field per VM?

(These can be added later if needed; not in MVP design.)

---

**Status:** Ready to implement
**Owner:** Backend developer
**Next Workstream:** #2 (Docker Volumes & Snapshots) — after this completes
