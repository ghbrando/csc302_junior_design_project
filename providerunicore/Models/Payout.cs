using Google.Cloud.Firestore;

namespace unicoreprovider.Models;

[FirestoreData]
public class Payout
{
    [FirestoreProperty("id")]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("date")]
    public DateTime Date { get; set; }

    [FirestoreProperty("amount")]
    public decimal Amount { get; set; }

    [FirestoreProperty("method")]
    public string Method { get; set; } = string.Empty;

    [FirestoreProperty("status")]
    public string Status { get; set; } = "Completed";
}