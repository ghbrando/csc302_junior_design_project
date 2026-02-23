using FirebaseAdmin.Auth;

//Used For: Firebase Authentication Service Interface
public interface IFirebaseAuthService
{
    Task<FirebaseToken> VerifyIDTokenAsync(string idToken);
    Task<string> SignInAsync(string email, string password);
    Task<string> SignUpAsync(string email, string password);
}