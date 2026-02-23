using Google.Cloud.Firestore;

//Used For: Consumer Service Interface
public interface IConsumerService
{
    Task<Consumer?> GetByFirebaseUidAsync(string firebaseUid);
    Task<Consumer> CreateConsumerAsync(string name, string email, string firebaseUid);
    Task<Consumer> UpdateLastLoginAsync(string firebaseUid);
    FirestoreChangeListener ListenByFirebaseUid(string firebaseUid, Action<Consumer?> onChanged);
}