using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Google.Cloud.Firestore;
using UniCore.Shared.Models;

namespace consumerunicore.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VmsController : ControllerBase
{
    private readonly FirestoreDb _firestoreDb;

    public VmsController(FirestoreDb firestoreDb)
    {
        _firestoreDb = firestoreDb ?? throw new ArgumentNullException(nameof(firestoreDb));
    }

    /// <summary>
    /// Pauses a running VM.
    /// </summary>
    [HttpPost("{vmId}/pause")]
    public async Task<IActionResult> PauseVm(string vmId)
    {
        try
        {
            var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
            var doc = await vmRef.GetSnapshotAsync();

            if (!doc.Exists)
                return NotFound(new { error = $"VM {vmId} not found" });

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                { "is_paused", true }
            });

            var updatedDoc = await vmRef.GetSnapshotAsync();
            var vm = updatedDoc.ConvertTo<VirtualMachine>();

            return Ok(vm);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resumes a paused VM.
    /// </summary>
    [HttpPost("{vmId}/resume")]
    public async Task<IActionResult> ResumeVm(string vmId)
    {
        try
        {
            var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
            var doc = await vmRef.GetSnapshotAsync();

            if (!doc.Exists)
                return NotFound(new { error = $"VM {vmId} not found" });

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                { "is_paused", false }
            });

            var updatedDoc = await vmRef.GetSnapshotAsync();
            var vm = updatedDoc.ConvertTo<VirtualMachine>();

            return Ok(vm);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Stops a VM completely.
    /// </summary>
    [HttpPost("{vmId}/stop")]
    public async Task<IActionResult> StopVm(string vmId)
    {
        try
        {
            var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
            var doc = await vmRef.GetSnapshotAsync();

            if (!doc.Exists)
                return NotFound(new { error = $"VM {vmId} not found" });

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                { "status", "Stopped" },
                { "is_paused", false }
            });

            var updatedDoc = await vmRef.GetSnapshotAsync();
            var vm = updatedDoc.ConvertTo<VirtualMachine>();

            return Ok(vm);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
