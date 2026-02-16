namespace unicoreprovider.Services;

public interface IAuthStateService
{
    string? FirebaseUid { get; }
    string? Email { get; }
    void SetAuthState(string firebaseUid, string email);
    void ClearAuthState();
}

public class AuthStateService : IAuthStateService
{
    public string? FirebaseUid { get; private set; }
    public string? Email { get; private set; }

    public void SetAuthState(string firebaseUid, string email)
    {
        FirebaseUid = firebaseUid;
        Email = email;
    }

    public void ClearAuthState()
    {
        FirebaseUid = null;
        Email = null;
    }
}
