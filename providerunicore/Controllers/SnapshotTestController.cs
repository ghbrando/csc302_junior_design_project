using Microsoft.AspNetCore.Mvc;
using unicoreprovider.Services;

namespace providerunicore.Controllers;

/// <summary>
/// Development-only endpoint for taking a snapshot of a running VM.
/// Remove or gate behind an environment check before shipping.
/// </summary>
[ApiController]
[Route("api/dev/snapshot")]
public class SnapshotTestController : ControllerBase
{
    private readonly IFirestoreRepository<VirtualMachine> _vmRepo;
    private readonly ISnapshotService _snapshotService;

    public SnapshotTestController(
        IFirestoreRepository<VirtualMachine> vmRepo,
        ISnapshotService snapshotService)
    {
        _vmRepo = vmRepo;
        _snapshotService = snapshotService;
    }

    /// <summary>
    /// POST /api/dev/snapshot/trigger/{vmId}
    ///
    /// Commits the running container as a local Docker image and updates Firestore.
    /// Call this before triggering a migration so the snapshot image is available.
    /// </summary>
    [HttpPost("trigger/{vmId}")]
    public async Task<IActionResult> TriggerSnapshot(string vmId)
    {
        var vm = await _vmRepo.GetByIdAsync(vmId);
        if (vm == null)
            return NotFound(new { error = $"VM {vmId} not found." });

        if (string.IsNullOrEmpty(vm.ContainerId))
            return BadRequest(new { error = "VM has no running container." });

        await _snapshotService.TakeSnapshotAsync(vmId, vm.ContainerId);

        // Re-read to get updated snapshot fields
        vm = await _vmRepo.GetByIdAsync(vmId);

        return Ok(new
        {
            message = "Snapshot taken successfully.",
            vm_id = vmId,
            snapshot_image = vm?.SnapshotImage,
            last_snapshot_at = vm?.LastSnapshotAt,
        });
    }
}
