namespace unicoreprovider.Services;

public interface IAuthStateService
{
    string? FirebaseUid { get; }
    string? Email { get; }
    string? Name { get; }
    void SetAuthState(string firebaseUid, string email, string name);
    void ClearAuthState();
}

public class AuthStateService : IAuthStateService
{
    public string? FirebaseUid { get; private set; }
    public string? Email { get; private set; }
    public string? Name { get; private set; }

    public void SetAuthState(string firebaseUid, string email, string name)
    {
        FirebaseUid = firebaseUid;
        Email = email;
        Name = name;
    }

    public void ClearAuthState()
    {
        FirebaseUid = null;
        Email = null;
        Name = null;
    }
}
