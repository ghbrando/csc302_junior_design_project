using FirebaseAdmin.Auth;

//Used For: Firebase Authentication Service Interface
public interface IFirebaseAuthService
{
    Task<FirebaseToken> VerifyIDTokenAsync(string idToken);
    Task<string> SignInAsync(string email, string password);
    Task<string> SignUpAsync(string email, string password);

    // send a password-reset email to the specified address using the
    // Firebase REST API. Throws on error message from Firebase.
    Task SendPasswordResetEmailAsync(string email);
}