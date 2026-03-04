using Google.Cloud.Firestore;
using consumerunicore.Services;    // IProviderService lives here

//Used For: Consumer Service Implementation
public class ConsumerService : IConsumerService
{
    private readonly IFirestoreRepository<Consumer> _repository;
    private readonly IProviderService _providerService;

    public ConsumerService(IFirestoreRepository<Consumer> repository,
                           IProviderService providerService)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
    }

    // Get consumer by Firebase UID (document ID). If the document doesn't exist
    // but a provider with that UID does, automatically create and return the
    // new consumer record.
    public async Task<Consumer?> GetByFirebaseUidAsync(string firebaseUid)
    {
        var consumer = await _repository.GetByIdAsync(firebaseUid);
        if (consumer == null)
        {
            var provider = await _providerService.GetByFirebaseUidAsync(firebaseUid);
            if (provider != null)
            {
                consumer = await CreateConsumerAsync(provider.Name, provider.Email, firebaseUid);
            }
        }
        return consumer;
    }

    // Create a new consumer
    public async Task<Consumer> CreateConsumerAsync(string name, string email, string firebaseUid)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty.", nameof(email));
        if (string.IsNullOrWhiteSpace(firebaseUid))
            throw new ArgumentException("Firebase UID cannot be empty.", nameof(firebaseUid));

        var consumer = new Consumer
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Email = email,
            FirebaseUid = firebaseUid,
            CreatedAt = DateTime.UtcNow
        };

        // CreateAsync uses documentIdSelector (FirebaseUid) automatically
        await _repository.CreateAsync(consumer);
        return consumer;
    }

    // Update the last login time for a consumer
    public async Task<Consumer> UpdateProfileAsync(string firebaseUid, string name, string email)
    {
        if (string.IsNullOrWhiteSpace(firebaseUid))
            throw new ArgumentException("Firebase UID cannot be empty.", nameof(firebaseUid));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty.", nameof(email));

        var consumer = await _repository.GetByIdAsync(firebaseUid);

        if (consumer == null)
            throw new InvalidOperationException($"Consumer {firebaseUid} not found");

        consumer.Name = name.Trim();
        consumer.Email = email.Trim();

        await _repository.UpdateAsync(firebaseUid, consumer);
        return consumer;
    }

    public async Task<Consumer> UpdateSettingsAsync(
        string firebaseUid,
        string name,
        string email,
        bool notifyVmStarted,
        bool notifyVmCompleted,
        bool notifyBudgetAlert,
        bool notifyPayoutReady,
        bool notifySystemUpdates,
        string timeZone,
        string currency)
    {
        if (string.IsNullOrWhiteSpace(firebaseUid))
            throw new ArgumentException("Firebase UID cannot be empty.", nameof(firebaseUid));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty.", nameof(email));

        var consumer = await _repository.GetByIdAsync(firebaseUid);

        if (consumer == null)
            throw new InvalidOperationException($"Consumer {firebaseUid} not found");

        consumer.Name = name.Trim();
        consumer.Email = email.Trim();
        consumer.NotifyVmStarted = notifyVmStarted;
        consumer.NotifyVmCompleted = notifyVmCompleted;
        consumer.NotifyBudgetAlert = notifyBudgetAlert;
        consumer.NotifyPayoutReady = notifyPayoutReady;
        consumer.NotifySystemUpdates = notifySystemUpdates;
        consumer.TimeZone = string.IsNullOrWhiteSpace(timeZone) ? "UTC-08:00" : timeZone.Trim();
        consumer.Currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim();

        await _repository.UpdateAsync(firebaseUid, consumer);
        return consumer;
    }

    // Update the last login time for a consumer
    public async Task<Consumer> UpdateLastLoginAsync(string firebaseUid)
    {
        var consumer = await _repository.GetByIdAsync(firebaseUid);

        if (consumer == null)
            throw new InvalidOperationException($"Consumer {firebaseUid} not found");

        consumer.LastLogin = DateTime.UtcNow;
        await _repository.UpdateAsync(firebaseUid, consumer);
        return consumer;
    }
    // Listen for real-time changes to a consumer document
    public FirestoreChangeListener ListenByFirebaseUid(string firebaseUid, Action<Consumer?> onChanged)
    {
        return _repository.Listen(firebaseUid, onChanged);
    }
}