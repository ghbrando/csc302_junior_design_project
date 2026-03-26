using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using unicoreprovider.Services;

[ApiController]
[Route("api/[controller]")]
public class SnapshotController : ControllerBase
{
    private readonly ISnapshotService _snapshotService;

    public SnapshotController(ISnapshotService snapshotService)
    {
        _snapshotService = snapshotService;
    }

    [HttpPost("trigger/{vmId}")]
    [Authorize]
    public async Task<IActionResult> Trigger(string vmId)
    {
        await _snapshotService.TriggerSnapshotAsync(vmId);
        return Accepted(new { vmId, status = "Queued" });
    }
}
