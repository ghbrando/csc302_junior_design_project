using Microsoft.AspNetCore.Mvc;
using Google.Cloud.Firestore;
using unicoreprovider.Services;

namespace providerunicore.Controllers;

/// <summary>
/// Development-only endpoint for triggering a migration request without the consumer UI.
/// Remove or gate behind an environment check before shipping.
/// </summary>
[ApiController]
[Route("api/dev/migration")]
public class MigrationTestController : ControllerBase
{
    private readonly FirestoreDb _firestoreDb;
    private readonly IFirestoreRepository<VirtualMachine> _vmRepo;
    private readonly IAuthStateService _authState;

    public MigrationTestController(
        FirestoreDb firestoreDb,
        IFirestoreRepository<VirtualMachine> vmRepo,
        IAuthStateService authState)
    {
        _firestoreDb = firestoreDb;
        _vmRepo = vmRepo;
        _authState = authState;
    }

    /// <summary>
    /// POST /api/dev/migration/trigger/{vmId}
    ///
    /// Creates a vm_migration_requests document for the given VM, targeting the
    /// currently-logged-in provider as both source and target (self-migration).
    /// The Dashboard listener will pick it up and run the full state machine.
    /// </summary>
    [HttpPost("trigger/{vmId}")]
    public async Task<IActionResult> TriggerMigration(string vmId)
    {
        var providerUid = _authState.FirebaseUid;
        if (string.IsNullOrEmpty(providerUid))
            return Unauthorized(new { error = "No provider logged in." });

        var vm = await _vmRepo.GetByIdAsync(vmId);
        if (vm == null)
            return NotFound(new { error = $"VM {vmId} not found." });

        var requestId = Guid.NewGuid().ToString();

        await _firestoreDb.Collection("vm_migration_requests").Document(requestId).SetAsync(
            new Dictionary<string, object>
            {
                ["migration_request_id"] = requestId,
                ["vm_id"]                = vmId,
                ["consumer_uid"]         = vm.Client ?? "",
                ["source_provider_uid"]  = vm.ProviderId,
                ["target_provider_uid"]  = providerUid,   // self-migration for testing
                ["status"]               = "pending",
                ["requested_at"]         = DateTime.UtcNow,
                ["completed_at"]         = (object?)null,
                ["new_vm_id"]            = "",
                ["error"]                = (object?)null,
            });

        return Ok(new
        {
            message = "Migration request created. Watch provider app logs for [Migration] output.",
            migration_request_id = requestId,
            vm_id = vmId,
            target_provider = providerUid,
        });
    }
}
