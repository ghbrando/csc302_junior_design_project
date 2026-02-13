using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

//Used For: Firebase Authentication Service Interface
public interface IFirebaseAuthService
{
    Task<FirebaseToken> VerifyIDTokenAsync(string idToken);
    Task<string> SignInAsync(string email, string password);
    Task<string> SignUpAsync(string email, string password);
}

//Used For: Firebase Authentication Service Implementation
public class FirebaseAuthService : IFirebaseAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _webApiKey;

    private const string SignInUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=";
    private const string SignUpUrl  = "https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=";

    public FirebaseAuthService(IHttpClientFactory httpFactory, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _webApiKey   = config["Firebase:WebApiKey"] ?? throw new InvalidOperationException("Firebase:WebApiKey is not configured.");

        if (FirebaseApp.DefaultInstance == null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = CredentialFactory.FromFile<ServiceAccountCredential>("firebase-service-account.json").ToGoogleCredential()
            });
        }
    }

    // Verify a Firebase ID token (used by AuthController after sign-in/sign-up)
    public async Task<FirebaseToken> VerifyIDTokenAsync(string idToken)
        => await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(idToken);

    // Exchange email+password for a Firebase ID token (sign in existing user)
    public async Task<string> SignInAsync(string email, string password)
    {
        var http     = _httpFactory.CreateClient();
        var payload  = new { email, password, returnSecureToken = true };
        var response = await http.PostAsJsonAsync(SignInUrl + _webApiKey, payload);
        var result   = await response.Content.ReadFromJsonAsync<FirebaseRestResponse>()
                       ?? throw new Exception("Empty response from Firebase.");

        if (!string.IsNullOrEmpty(result.Error?.Message))
            throw new Exception(result.Error.Message);

        return result.IdToken;
    }

    // Exchange email+password for a Firebase ID token (create new user)
    public async Task<string> SignUpAsync(string email, string password)
    {
        var http     = _httpFactory.CreateClient();
        var payload  = new { email, password, returnSecureToken = true };
        var response = await http.PostAsJsonAsync(SignUpUrl + _webApiKey, payload);
        var result   = await response.Content.ReadFromJsonAsync<FirebaseRestResponse>()
                       ?? throw new Exception("Empty response from Firebase.");

        if (!string.IsNullOrEmpty(result.Error?.Message))
            throw new Exception(result.Error.Message);

        return result.IdToken;
    }

    // Internal response model for Firebase REST API
    private class FirebaseRestResponse
    {
        [JsonPropertyName("idToken")]
        public string IdToken { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public FirebaseRestError? Error { get; set; }
    }

    private class FirebaseRestError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
