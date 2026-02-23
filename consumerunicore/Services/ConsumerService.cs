using Google.Cloud.Firestore;
using consumerunicore.Repositories;

//Used For: Consumer Service Implementation
public class ConsumerService : IConsumerService
{
    private readonly IFirestoreRepository<Consumer> _repository;

    public ConsumerService(IFirestoreRepository<Consumer> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    // Get consumer by Firebase UID (document ID)
    public async Task<Consumer?> GetByFirebaseUidAsync(string firebaseUid)
    {
        return await _repository.GetByIdAsync(firebaseUid);
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