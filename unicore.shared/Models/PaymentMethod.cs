using Google.Cloud.Firestore;

namespace unicore.shared.Models
{
    [FirestoreData]
    public class PaymentMethod
    {
        [FirestoreDocumentId]
        public string Id { get; set; } = "";

        [FirestoreProperty("account_holder_name")]
        public string AccountHolderName { get; set; } = "";

        [FirestoreProperty("account_number")]
        public string AccountNumber { get; set; } = "";

        [FirestoreProperty("routing_number")]
        public string RoutingNumber { get; set; } = "";

        [FirestoreProperty("is_primary")]
        public bool IsPrimary { get; set; } = false;

        [FirestoreProperty("created_at")]
        public Timestamp CreatedAt { get; set; }

        public string DisplayLabel => string.IsNullOrEmpty(AccountNumber)
            ? "Bank Account"
            : $"Bank ****{AccountNumber[^Math.Min(4, AccountNumber.Length)..]}";
    }
}