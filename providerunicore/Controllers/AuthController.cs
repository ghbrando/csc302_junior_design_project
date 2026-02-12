using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using FirebaseAdmin.Auth;

//Used For: Authentication Controller
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IFirebaseAuthService _firebaseAuthService;
    private readonly IProviderService _providerService;

    public AuthController(IFirebaseAuthService firebaseAuthService, IProviderService providerService)
    {
        _firebaseAuthService = firebaseAuthService;
        _providerService = providerService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] SignInRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        try
        {
            var decodedToken = await _firebaseAuthService.VerifyIDTokenAsync(request.IdToken);
            var uid = decodedToken.Uid;
            var email = decodedToken.Claims["email"]?.ToString() ?? string.Empty;

            var existing = await _providerService.GetByFirebaseUidAsync(uid);
            if (existing != null)
                return Conflict(new { error = "A provider with this account already exists." });

            // Use the name from the request body instead of Firebase
            var provider = await _providerService.CreateProviderAsync(uid, email, request.Name);

            return CreatedAtAction(nameof(GetCurrentProvider), new AuthResponse
            {
                FirebaseUid = uid,
                Email = provider.Email,
                Name = provider.Name,
                LastLogin = provider.LastLogin
            });
        }
        catch (FirebaseAuthException ex)
        {
            return Unauthorized(new { error = "Invalid or expired token.", detail = ex.Message });
        }
    }

    // Login endpoint - just verifies the token and updates last login time
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var decodedToken = await _firebaseAuthService.VerifyIDTokenAsync(request.IdToken);
            var uid = decodedToken.Uid;

            var provider = await _providerService.GetByFirebaseUidAsync(uid);
            if (provider == null)
                return NotFound(new { error = "Provider not found. Please register first." });

            provider = await _providerService.UpdateLastLoginAsync(uid);

            return Ok(new AuthResponse
            {
                FirebaseUid = uid,
                Email = provider.Email,
                Name = provider.Name,
                LastLogin = provider.LastLogin
            });
        }
        catch (FirebaseAuthException ex)
        {
            return Unauthorized(new { error = "Invalid or expired token.", detail = ex.Message });
        }
    }

    //Logout endpoint
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var uid = User.FindFirst("user_id")?.Value;
        if (uid == null) return Unauthorized();

        await FirebaseAuth.DefaultInstance.RevokeRefreshTokensAsync(uid);

        return Ok(new { message = "Logged out successfully." });
    }

    // Protected endpoint to get current provider info
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentProvider()
    {
        var uid = User.FindFirst("user_id")?.Value;
        if (uid == null) return Unauthorized();

        var provider = await _providerService.GetByFirebaseUidAsync(uid);
        return provider == null ? NotFound() : Ok(provider);
    }

}