
//Used For: Authentication Request and Response Models
public class SignInRequest
{
    public string IdToken { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string IdToken { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string FirebaseUid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;
}

