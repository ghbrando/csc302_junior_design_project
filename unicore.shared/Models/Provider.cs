using Google.Cloud.Firestore;

namespace UniCore.Shared.Models;

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
    [FirestoreProperty("node_status")]
    public string NodeStatus { get; set; } = "Offline";
    [FirestoreProperty("cpu_limit_percent")]
    public double CpuLimitPercent { get; set; } = 55;
    [FirestoreProperty("ram_limit_gb")]
    public double RamLimitGB { get; set; } = 4;
    [FirestoreProperty("region")]
    public string Region { get; set; } = string.Empty;
    [FirestoreProperty("consistency_score")]
    public double ConsistencyScore { get; set; } = 100.0;
}
