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
        _providerService     = providerService;
    }

    // Register a new provider with email + password + name
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        try
        {
            var idToken      = await _firebaseAuthService.SignUpAsync(request.Email, request.Password);
            var decodedToken = await _firebaseAuthService.VerifyIDTokenAsync(idToken);
            var uid          = decodedToken.Uid;

            var existing = await _providerService.GetByFirebaseUidAsync(uid);
            if (existing != null)
                return Conflict(new { error = "A provider with this account already exists." });

            var provider = await _providerService.CreateProviderAsync(request.Name, request.Email, uid);

            return CreatedAtAction(nameof(GetCurrentProvider), new AuthResponse
            {
                FirebaseUid = uid,
                Email       = provider.Email,
                Name        = provider.Name,
                LastLogin   = provider.LastLogin
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // Log in an existing provider with email + password
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var idToken      = await _firebaseAuthService.SignInAsync(request.Email, request.Password);
            var decodedToken = await _firebaseAuthService.VerifyIDTokenAsync(idToken);
            var uid          = decodedToken.Uid;

            var provider = await _providerService.GetByFirebaseUidAsync(uid);
            if (provider == null)
                return NotFound(new { error = "Provider not found. Please register first." });

            provider = await _providerService.UpdateLastLoginAsync(uid);

            return Ok(new AuthResponse
            {
                FirebaseUid = uid,
                Email       = provider.Email,
                Name        = provider.Name,
                LastLogin   = provider.LastLogin
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // Logout — revokes Firebase refresh tokens
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
