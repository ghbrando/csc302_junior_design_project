using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using FirebaseAdmin.Auth;
using consumerunicore.Services;

//Used For: Authentication Controller
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IFirebaseAuthService _firebaseAuthService;
    private readonly IConsumerService _consumerService;
    private readonly IProviderService _providerService;

    public AuthController(IFirebaseAuthService firebaseAuthService, IConsumerService consumerService, IProviderService providerService)
    {
        _firebaseAuthService = firebaseAuthService;
        _consumerService = consumerService;
        _providerService = providerService;
    }

    // Register a new consumer with email + password + name
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });

        try
        {
            var idToken = await _firebaseAuthService.SignUpAsync(request.Email, request.Password);
            var decodedToken = await _firebaseAuthService.VerifyIDTokenAsync(idToken);
            var uid = decodedToken.Uid;

            var existing = await _consumerService.GetByFirebaseUidAsync(uid);
            if (existing != null)
                return Conflict(new { error = "A consumer with this account already exists." });

            var consumer = await _consumerService.CreateConsumerAsync(request.Name, request.Email, uid);

            return CreatedAtAction(nameof(GetCurrentConsumer), new AuthResponse
            {
                FirebaseUid = uid,
                Email = consumer.Email,
                Name = consumer.Name,
                LastLogin = consumer.LastLogin
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // Log in an existing consumer with email + password
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var idToken = await _firebaseAuthService.SignInAsync(request.Email, request.Password);
            var decodedToken = await _firebaseAuthService.VerifyIDTokenAsync(idToken);
            var uid = decodedToken.Uid;

            // GetByFirebaseUidAsync now handles provider fallback and will create
            // a consumer record if one doesn't exist but a provider does.
            var consumer = await _consumerService.GetByFirebaseUidAsync(uid);
            if (consumer == null)
                return NotFound(new { error = "Consumer not found. Please register first." });

            consumer = await _consumerService.UpdateLastLoginAsync(uid);

            return Ok(new AuthResponse
            {
                FirebaseUid = uid,
                Email = consumer.Email,
                Name = consumer.Name,
                LastLogin = consumer.LastLogin
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
        var uid = User.FindFirst("sub")?.Value;
        if (uid == null) return Unauthorized();

        await FirebaseAuth.DefaultInstance.RevokeRefreshTokensAsync(uid);

        return Ok(new { message = "Logged out successfully." });
    }

    // Protected endpoint to get current consumer info
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentConsumer()
    {
        var uid = User.FindFirst("sub")?.Value;
        if (uid == null) return Unauthorized();

        var consumer = await _consumerService.GetByFirebaseUidAsync(uid);
        return consumer == null ? NotFound() : Ok(consumer);
    }
}
