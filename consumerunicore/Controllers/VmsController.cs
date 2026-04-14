using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Google.Cloud.Firestore;
using UniCore.Shared.Models;
using consumerunicore.Services;

namespace consumerunicore.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VmsController : ControllerBase
{
    private readonly FirestoreDb _firestoreDb;
    private readonly IMigrationRequestService _migrationService;

    public VmsController(FirestoreDb firestoreDb, IMigrationRequestService migrationService)
    {
        _firestoreDb = firestoreDb ?? throw new ArgumentNullException(nameof(firestoreDb));
        _migrationService = migrationService ?? throw new ArgumentNullException(nameof(migrationService));
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

    /// <summary>
    /// Triggers an on-demand volume backup for a VM.
    /// Sets volume_sync_status to "Requested" so the provider picks it up.
    /// </summary>
    [HttpPost("{vmId}/backup-now")]
    public async Task<IActionResult> BackupNow(string vmId)
    {
        try
        {
            var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
            var doc = await vmRef.GetSnapshotAsync();

            if (!doc.Exists)
                return NotFound(new { error = $"VM {vmId} not found" });

            var vm = doc.ConvertTo<VirtualMachine>();

            if (vm.VolumeSyncStatus == "Syncing")
                return BadRequest(new { error = "A backup is already in progress." });

            await vmRef.UpdateAsync(new Dictionary<string, object>
            {
                { "volume_sync_status", "Requested" }
            });

            return Ok(new { message = "Backup requested." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Requests migration of a VM to a different provider.
    /// Auto-selects the healthiest provider if targetProviderUid is not specified.
    /// </summary>
    [HttpPost("{vmId}/migrate")]
    public async Task<IActionResult> MigrateVm(string vmId, [FromBody] MigrateRequest? body = null)
    {
        try
        {
            var consumerUid = User.FindFirst("user_id")?.Value
                ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(consumerUid))
                return Unauthorized(new { error = "Consumer not authenticated." });

            var request = await _migrationService.RequestMigrationAsync(
                vmId, consumerUid, body?.TargetProviderUid, body?.RequestedCpuCores, body?.RequestedRamGb);

            return Ok(new
            {
                message = "Migration requested.",
                migration_request_id = request.MigrationRequestId,
                source_provider = request.SourceProviderUid,
                target_provider = request.TargetProviderUid,
                status = request.Status,
                requested_cpu_cores = request.RequestedCpuCores,
                requested_ram_gb = request.RequestedRamGb,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the current/most recent migration status for a VM.
    /// </summary>
    [HttpGet("{vmId}/migration-status")]
    public async Task<IActionResult> GetMigrationStatus(string vmId)
    {
        try
        {
            var migration = await _migrationService.GetActiveMigrationAsync(vmId);
            if (migration == null)
                return Ok(new { status = "none" });

            return Ok(new
            {
                migration_request_id = migration.MigrationRequestId,
                status = migration.Status,
                source_provider = migration.SourceProviderUid,
                target_provider = migration.TargetProviderUid,
                new_vm_id = migration.NewVmId,
                error = migration.Error,
                requested_at = migration.RequestedAt,
                completed_at = migration.CompletedAt,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns available target providers for migration (excludes the source provider).
    /// </summary>
    [HttpGet("{vmId}/migration-targets")]
    public async Task<IActionResult> GetMigrationTargets(string vmId)
    {
        try
        {
            var vmRef = _firestoreDb.Collection("virtual_machines").Document(vmId);
            var doc = await vmRef.GetSnapshotAsync();

            if (!doc.Exists)
                return NotFound(new { error = $"VM {vmId} not found." });

            var vm = doc.ConvertTo<VirtualMachine>();
            var providers = await _migrationService.GetAvailableTargetProvidersAsync(vm.ProviderId);

            return Ok(providers.Select(p => new
            {
                provider_uid = p.FirebaseUid,
                name = p.Name,
                region = p.Region,
                consistency_score = p.ConsistencyScore,
                node_status = p.NodeStatus,
            }));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

/// <summary>
/// Request body for the migrate endpoint.
/// </summary>
public class MigrateRequest
{
    /// <summary>
    /// Optional target provider UID. If null, auto-selects healthiest provider.
    /// </summary>
    public string? TargetProviderUid { get; set; }
    public int? RequestedCpuCores { get; set; }
    public int? RequestedRamGb { get; set; }
}
