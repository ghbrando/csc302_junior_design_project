# Workstream 3: Consumer Volume Requests

**Type:** Full-stack (Frontend + Backend)
**Depends On:** Workstreams 1, 2 (data models + Docker volumes)
**Blocks:** Workstream 6 (migration needs volumes to exist)
**Estimated Scope:** 300 lines, 6 hours
**Owner:** Full-stack developer

---

## 🎯 Overview

Add the ability for consumers to request a specific amount of storage (in GB) when creating a VM. This volume is created on the provider's Docker daemon and is what gets backed up to GCS.

**Why:** Volumes don't exist yet in the current flow. Consumers need to be able to specify storage, and providers need to know how much to allocate.

---

## 🏗️ Architecture

### Current VM Request Flow

```
Consumer clicks "Launch VM"
  ↓
Consumer selects: image, CPU, RAM, (no storage yet!)
  ↓
Matchmaking finds provider with available resources
  ↓
VmRequest sent to provider
  ↓
Provider ProcessRequestAsync:
  1. Check resources
  2. Allocate relay port
  3. Call DockerService.StartContainerAsync(...)
  4. Create VirtualMachine doc in Firestore
  5. Start monitoring
```

### New Flow (With Storage)

```
Consumer clicks "Launch VM"
  ↓
Consumer selects: image, CPU, RAM, Storage (NEW: in GB)
  ↓
Matchmaking finds provider with available resources
  (Now includes volume capacity check)
  ↓
VmRequest sent to provider with requested_volume_gb
  ↓
Provider ProcessRequestAsync:
  1. Check resources (CPU, RAM, AND now VOLUME)
  2. Allocate relay port
  3. Get requested_volume_gb from VmRequest
  4. Call DockerService.StartContainerAsync(...) → creates volume automatically
  5. Create VirtualMachine doc with volume_name, gcs_bucket, gcs_path
  6. Start monitoring
```

---

## 🔌 Integration Points

### Files to Modify

1. **`unicore.shared/Models/VmRequest.cs`**
   - Add `RequestedVolumeGb` field

2. **`consumerunicore/Components/Pages/Dashboard.razor`**
   - Add volume size input to VM creation form

3. **`consumerunicore/Services/IConsumerVmService.cs`**
   - Add parameter to match the new VmRequest structure (or add validation method)

4. **`consumerunicore/Services/ConsumerVmService.cs`**
   - Validate volume size (min 1GB, max 1TB?)
   - Update matchmaking to include volume capacity

5. **`providerunicore/Services/IVmService.cs`**
   - Add `GetAvailableVolumeCapacityAsync(providerId)` method (query Docker volumes)

6. **`providerunicore/Services/VirtualMachineService.cs`**
   - Implement volume capacity check

7. **`providerunicore/Components/Pages/Dashboard.razor`**
   - Update `ProcessRequestAsync` to pass `requestedVolumeGb` to `StartContainerAsync`

---

## ✅ What Needs to Be Done

### Task 1: Add Volume Size to VmRequest Model

**File:** `unicore.shared/Models/VmRequest.cs`

Add this field:

```csharp
[FirestoreProperty("requested_volume_gb")]
public int RequestedVolumeGb { get; set; } = 50; // Default 50GB
```

### Task 2: Add Volume Input to Consumer UI

**File:** `consumerunicore/Components/Pages/Dashboard.razor`

Find the VM creation form (where CPU/RAM are selected). Add a storage input:

```html
<div class="form-group">
    <label for="volumeGb">Storage (GB)</label>
    <input type="number" id="volumeGb" @bind="@newVmRequest.RequestedVolumeGb"
           min="1" max="1000" class="form-control" />
    <small class="form-text text-muted">
        How much storage do you need? (1–1000 GB)
    </small>
</div>
```

Add validation:
```csharp
private async Task ValidateAndLaunchVm()
{
    if (newVmRequest.RequestedVolumeGb < 1 || newVmRequest.RequestedVolumeGb > 1000)
    {
        errorMessage = "Volume must be between 1 and 1000 GB";
        return;
    }

    await SubmitVmRequest();
}
```

### Task 3: Update Matchmaking to Check Volume Capacity

**File:** `consumerunicore/Services/ConsumerVmService.cs`

When running matchmaking, check that the provider has enough volume capacity:

```csharp
private async Task<Provider> MatchmakeAsync(VmRequest request)
{
    // Get list of providers
    var providers = await _providerService.GetAllAsync();

    foreach (var provider in providers)
    {
        // Check CPU/RAM (existing)
        bool hasCpuRam = await HasResourcesAsync(provider, request.CpuCores, request.RamGb);

        // Check Volume (NEW)
        int availableVolumeGb = await _vmService.GetAvailableVolumeCapacityAsync(provider.FirebaseUid);
        bool hasVolume = availableVolumeGb >= request.RequestedVolumeGb;

        if (hasCpuRam && hasVolume)
        {
            return provider; // Found a match
        }
    }

    throw new InvalidOperationException("No providers available with requested resources");
}
```

### Task 4: Implement Volume Capacity Check in Provider

**File:** `providerunicore/Services/IVmService.cs`

Add method signature:

```csharp
/// <summary>
/// Get total available volume capacity on this provider.
/// </summary>
Task<int> GetAvailableVolumeCapacityAsync();
```

**File:** `providerunicore/Services/VirtualMachineService.cs`

Implement it (for now, return a default value; in future, query actual Docker volumes):

```csharp
public async Task<int> GetAvailableVolumeCapacityAsync()
{
    // TODO: Query Docker volumes to get actual usage
    // For MVP: Return hardcoded limit (e.g., 500GB per provider)
    var runningVms = await _vmRepository.WhereAsync("status", "Running");
    int totalUsed = runningVms.Sum(vm => int.TryParse(
        vm.VolumeName?.Split('-').Last() ?? "0", out var gb) ? gb : 0);

    return 500 - totalUsed; // 500GB limit per provider
}
```

### Task 5: Update Provider ProcessRequestAsync

**File:** `providerunicore/Components/Pages/Dashboard.razor`

When processing a request, read the `RequestedVolumeGb` and pass to Docker:

```csharp
private async Task ProcessRequestAsync(VmRequest request)
{
    // ... existing validation ...

    // Get requested volume size (NEW)
    int requestedVolumeGb = request.RequestedVolumeGb ?? 50;

    // Call Docker with volume size
    var (containerId, volumeName) = await _dockerService.StartContainerAsync(
        vmId: vmId,
        name: containerName,
        image: baseImage,
        relayPort: allocatedPort,
        cpuCores: request.CpuCores,
        ramGB: request.RamGb,
        existingVolumeName: null);

    // Create VirtualMachine document with volume info (NEW)
    var vm = new VirtualMachine
    {
        VmId = vmId,
        Name = request.VmName,
        Status = "Running",
        ContainerId = containerId,
        ProviderId = providerId,
        Image = request.ImageName,
        RelayPort = allocatedPort,
        CpuCores = request.CpuCores,
        RamGb = request.RamGb,
        // NEW: Volume & backup fields
        VolumeName = volumeName,
        GcsBucket = "unicore-vm-volumes",
        GcsPath = $"consumers/{request.ConsumerUid}/{vmId}/",
        VolumeSyncStatus = "Idle",
        SnapshotStatus = "Idle"
    };

    await _vmService.CreateVmAsync(vm);

    // ... rest of existing logic ...
}
```

---

## 🧪 Acceptance Criteria

- [ ] VmRequest model has `RequestedVolumeGb` field
- [ ] Consumer UI has storage input (1–1000 GB range)
- [ ] Input validation prevents invalid volumes
- [ ] Matchmaking checks provider volume capacity
- [ ] Provider implements `GetAvailableVolumeCapacityAsync`
- [ ] ProcessRequestAsync passes volume size to Docker
- [ ] ProcessRequestAsync sets volume fields on created VirtualMachine
- [ ] UI shows selected volume size to consumer
- [ ] No compilation errors
- [ ] Existing VM requests (without volume size) still work (default to 50GB)

---

## 🧠 Integration Notes

### Why Volume Size Matters for Matchmaking

Different consumers might need different storage:
- Small project: 10 GB
- Data science: 500 GB
- ML model training: 1000+ GB

Matchmaking must ensure provider has capacity.

### Volume Capacity Tracking

For MVP: Hardcode a limit per provider (e.g., 500GB total).
For v2: Query actual Docker volumes + filesystem usage.

### Default Volume Size

If consumer doesn't specify, default to 50GB (reasonable for most use cases).

---

## 🔗 Related Code

**Existing VmRequest model:**
- `unicore.shared/Models/VmRequest.cs` — See existing fields (CpuCores, RamGb, ImageName, etc.)

**Existing matchmaking logic:**
- `consumerunicore/Services/ConsumerVmService.cs` — FindAvailableProviderAsync or similar

**Existing ProcessRequestAsync:**
- `providerunicore/Components/Pages/Dashboard.razor` — See current implementation

**Docker volume creation:**
- `DockerService.StartContainerAsync` — Already calls CreateVolumeAsync (from Workstream 2)

---

## ⏱️ Time Estimate

- Reading this document: 10 min
- Adding field to VmRequest: 5 min
- UI input + validation: 20 min
- Matchmaking update: 15 min
- IVmService method: 5 min
- Implementation: 20 min
- ProcessRequestAsync update: 15 min
- Testing & refinement: 20 min

**Total: ~2 hours**

---

## 🚀 Next Steps

Once complete:
- Workstream 4 (GCP setup) should also be done
- Then Workstream 5 (background services) can use volumes

---

**Status:** Ready to implement (after Workstreams 1 & 2)
**Owner:** Full-stack developer
**Blockers:** Workstreams 1 & 2 must be complete
