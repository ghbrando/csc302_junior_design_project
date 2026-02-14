using providerunicore.Repositories;

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
    private readonly IFirestoreRepository<Provider> _repository;

    public ProviderService(IFirestoreRepository<Provider> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    // Get provider by Firebase UID (document ID)
    public async Task<Provider?> GetByFirebaseUidAsync(string firebaseUid)
    {
        return await _repository.GetByIdAsync(firebaseUid);
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

        // CreateAsync uses documentIdSelector (FirebaseUid) automatically
        await _repository.CreateAsync(provider);
        return provider;
    }

    // Update the last login time for a provider
    public async Task<Provider> UpdateLastLoginAsync(string firebaseUid)
    {
        var provider = await _repository.GetByIdAsync(firebaseUid);

        if (provider == null)
            throw new Exception($"Provider {firebaseUid} not found");

        provider.LastLogin = DateTime.UtcNow;
        await _repository.UpdateAsync(firebaseUid, provider);
        return provider;
    }
}