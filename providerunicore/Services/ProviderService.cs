//Used For: Provider Service Interface
public interface IProviderService
{
    Task<Provider?> GetByFirebaseUidAsync(string firebaseUid);
    Task<Provider> CreateProviderAsync(string name, string email, string firebaseUid);
    Task<Provider> UpdateLastLoginAsync(string firebaseUid);
}
//Used For: Provider Service Implementation
public class ProviderService : IProviderService
{
    // In-memory store for providers (replace with database in production)
    private readonly Dictionary<string, Provider> _providers = new();

    // Get provider by Firebase UID
    public Task<Provider?> GetByFirebaseUidAsync(string firebaseUid)
    {
        _providers.TryGetValue(firebaseUid, out var provider);
        return Task.FromResult(provider);
    }

    // Create a new provider
    public Task<Provider> CreateProviderAsync(string name, string email, string firebaseUid)
    {
        var provider = new Provider
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Email = email,
            FirebaseUid = firebaseUid,
            CreatedAt = DateTime.UtcNow
        };
        _providers[firebaseUid] = provider;
        return Task.FromResult(provider);
    }

    // Update the last login time for a provider
    public Task<Provider> UpdateLastLoginAsync(string firebaseUid)
    {
        if (_providers.TryGetValue(firebaseUid, out var provider))
        {
            provider.LastLogin = DateTime.UtcNow;
            return Task.FromResult(provider);
        }
        throw new Exception("Provider not found");
    }
}