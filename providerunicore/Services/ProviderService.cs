using Google.Cloud.Firestore;
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

    private readonly CollectionReference _providers;
    public ProviderService(FirestoreDb db)
    {
        _providers = db.Collection("providers");
    }

    // Get provider by Firebase UID
    public async Task<Provider?> GetByFirebaseUidAsync(string firebaseUid)
    {
        var doc = await _providers.Document(firebaseUid).GetSnapshotAsync();
        if (doc.Exists)
        {
            return doc.ConvertTo<Provider>();
        }
        return null;
    }

    // Create a new provider
    public async Task<Provider> CreateProviderAsync(string name, string email, string firebaseUid)
    {
        var provider = new Provider
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Email = email,
            FirebaseUid = firebaseUid,
            CreatedAt = DateTime.UtcNow
        };
        await _providers.Document(firebaseUid).SetAsync(provider);
        return provider;
    }

    // Update the last login time for a provider
    public async Task<Provider> UpdateLastLoginAsync(string firebaseUid)
    {
        var doc = await _providers.Document(firebaseUid).GetSnapshotAsync();
        if (doc.Exists)
        {
            var provider = doc.ConvertTo<Provider>();
            provider.LastLogin = DateTime.UtcNow;
            await _providers.Document(firebaseUid).SetAsync(provider);
            return provider;
        }
        throw new Exception($"Provider {firebaseUid} not found");
    }
}