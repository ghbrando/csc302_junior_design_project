using Google.Cloud.Firestore;
using unicoreprovider.Services; // IConsumerService is in same namespace

//Used For: Provider Service Interface
public interface IProviderService
{
    Task<Provider?> GetByFirebaseUidAsync(string firebaseUid);
    Task<Provider> CreateProviderAsync(string name, string email, string firebaseUid);
    Task<Provider> UpdateLastLoginAsync(string firebaseUid);
    Task<Provider> UpdateNodeStatusAsync(string status, string firebaseUid);
    Task<Provider> UpdateResourceLimitsAsync(double cpuLimitPercent, double ramLimitGB, string firebaseUid);
    Task IncrementConsistencyScoreAsync(string firebaseUid, double incrementBy);
    FirestoreChangeListener ListenByFirebaseUid(string firebaseUid, Action<Provider?> onChanged);
}

//Used For: Provider Service Implementation
public class ProviderService : IProviderService
{
    private readonly IFirestoreRepository<Provider> _repository;
    private readonly IConsumerService _consumerService;

    public ProviderService(IFirestoreRepository<Provider> repository,
                           IConsumerService consumerService)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _consumerService = consumerService ?? throw new ArgumentNullException(nameof(consumerService));
    }

    // Get provider by Firebase UID (document ID). If not present but a consumer record
    // exists with the same UID, automatically create a provider from consumer data.
    public async Task<Provider?> GetByFirebaseUidAsync(string firebaseUid)
    {
        var provider = await _repository.GetByIdAsync(firebaseUid);
        if (provider == null)
        {
            var consumer = await _consumerService.GetByFirebaseUidAsync(firebaseUid);
            if (consumer != null)
            {
                provider = await CreateProviderAsync(consumer.Name, consumer.Email, firebaseUid);
            }
        }
        return provider;
    }

    // Create a new provider
    public async Task<Provider> CreateProviderAsync(string name, string email, string firebaseUid)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty.", nameof(email));
        if (string.IsNullOrWhiteSpace(firebaseUid))
            throw new ArgumentException("Firebase UID cannot be empty.", nameof(firebaseUid));

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
            throw new InvalidOperationException($"Provider {firebaseUid} not found");

        provider.LastLogin = DateTime.UtcNow;
        await _repository.UpdateAsync(firebaseUid, provider);
        return provider;
    }

    // Update Provider Node Status
    public async Task<Provider> UpdateNodeStatusAsync(string status, string firebaseUid)
    {
        var provider = await _repository.GetByIdAsync(firebaseUid);

        if (provider == null)
            throw new InvalidOperationException($"Provider {firebaseUid} not found");

        if (status != "Offline" && status != "Online")
            throw new ArgumentException($"Invalid Status {status}. Must be Online or Offline", nameof(status));

        provider.NodeStatus = status;
        await _repository.UpdateAsync(firebaseUid, provider);
        return provider;
    }

    // Update CPU and RAM resource limits
    public async Task<Provider> UpdateResourceLimitsAsync(double cpuLimitPercent, double ramLimitGB, string firebaseUid)
    {
        var provider = await _repository.GetByIdAsync(firebaseUid);

        if (provider == null)
            throw new InvalidOperationException($"Provider {firebaseUid} not found");

        provider.CpuLimitPercent = cpuLimitPercent;
        provider.RamLimitGB = ramLimitGB;
        await _repository.UpdateAsync(firebaseUid, provider);
        return provider;
    }

    public async Task IncrementConsistencyScoreAsync(string firebaseUid, double incrementBy)
    {
        var provider = await _repository.GetByIdAsync(firebaseUid);

        if (provider == null)
            throw new InvalidOperationException($"Provider {firebaseUid} not found");

        provider.ConsistencyScore += incrementBy;
        await _repository.UpdateAsync(firebaseUid, provider);
    }

    // Listen for real-time changes to a provider document
    public FirestoreChangeListener ListenByFirebaseUid(string firebaseUid, Action<Provider?> onChanged)
    {
        return _repository.Listen(firebaseUid, onChanged);
    }
}