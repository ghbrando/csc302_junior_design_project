using Google.Cloud.Firestore;

//Used For: Consumer Service Interface
public interface IConsumerService
{
    Task<Consumer?> GetByFirebaseUidAsync(string firebaseUid);
    Task<Consumer> CreateConsumerAsync(string name, string email, string firebaseUid);
    Task<Consumer> UpdateProfileAsync(string firebaseUid, string name, string email);
    Task<Consumer> UpdateSettingsAsync(
        string firebaseUid,
        string name,
        string email,
        bool notifyVmStarted,
        bool notifyVmCompleted,
        bool notifyBudgetAlert,
        bool notifyPayoutReady,
        bool notifySystemUpdates,
        string timeZone,
        string currency);
    Task<Consumer> UpdateLastLoginAsync(string firebaseUid);
    Task<Consumer> UpdateTermsAcceptedAsync(string firebaseUid);
    Task<Consumer> UpdateOnboardingAsync(string firebaseUid, int step, string? timeZone = null, string? currency = null);
    FirestoreChangeListener ListenByFirebaseUid(string firebaseUid, Action<Consumer?> onChanged);
}