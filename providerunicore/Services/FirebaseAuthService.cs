using System.Security.Cryptography.X509Certificates;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;

//Used For: Firebase Authentication Service Interface
public interface IFirebaseAuthService
{
    Task<FirebaseToken> VerifyIDTokenAsync(string idToken);
}

//Used For: Firebase Authentication Service Implementation
public class FirebaseAuthService : IFirebaseAuthService
{
    public FirebaseAuthService()
    {
        if (FirebaseApp.DefaultInstance == null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile("unicore-junior-design-firebase-admin.json"),
            });
        }

    }
    public async Task<FirebaseToken> VerifyIDTokenAsync(string idToken) => await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(idToken);
}