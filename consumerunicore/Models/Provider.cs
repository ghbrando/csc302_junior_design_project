using Google.Cloud.Firestore;

namespace consumerunicore.Models
{
    [FirestoreData]
    public class Provider
    {
        [FirestoreProperty("id")]
        public string Id { get; set; } = string.Empty;

        [FirestoreProperty("name")]
        public string Name { get; set; } = string.Empty;

        [FirestoreProperty("email")]
        public string Email { get; set; } = string.Empty;

        [FirestoreProperty("firebase_uid")]
        public string FirebaseUid { get; set; } = string.Empty;

        [FirestoreProperty("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [FirestoreProperty("last_login")]
        public DateTime LastLogin { get; set; } = DateTime.UtcNow;

        // other provider-specific fields are ignored in the consumer app
    }
}
