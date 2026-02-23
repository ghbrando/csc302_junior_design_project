namespace unicoreprovider.Services;

public interface IAuthStateService
{
    string? FirebaseUid { get; }
    string? Email { get; }
    string? Name { get; }
    event Action? OnChange;
    void SetAuthState(string firebaseUid, string email, string name);
    void ClearAuthState();
}

public class AuthStateService : IAuthStateService
{
    public string? FirebaseUid { get; private set; }
    public string? Email { get; private set; }
    public string? Name { get; private set; }

    public event Action? OnChange;

    public void SetAuthState(string firebaseUid, string email, string name)
    {
        FirebaseUid = firebaseUid;
        Email = email;
        Name = name;
        OnChange?.Invoke();
    }

    public void ClearAuthState()
    {
        FirebaseUid = null;
        Email = null;
        Name = null;
        OnChange?.Invoke();
    }
}
