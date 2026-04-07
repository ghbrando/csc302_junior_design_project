using Google.Cloud.Firestore;

namespace UniCore.Shared.Models;

[FirestoreData]
public class Consumer
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

    [FirestoreProperty("notify_vm_started")]
    public bool NotifyVmStarted { get; set; } = true;

    [FirestoreProperty("notify_vm_completed")]
    public bool NotifyVmCompleted { get; set; } = true;

    [FirestoreProperty("notify_budget_alert")]
    public bool NotifyBudgetAlert { get; set; } = true;

    [FirestoreProperty("notify_payout_ready")]
    public bool NotifyPayoutReady { get; set; } = false;

    [FirestoreProperty("notify_system_updates")]
    public bool NotifySystemUpdates { get; set; } = false;

    [FirestoreProperty("time_zone")]
    public string TimeZone { get; set; } = "UTC-08:00";

    [FirestoreProperty("currency")]
    public string Currency { get; set; } = "USD";

    [FirestoreProperty("terms_accepted")]
    public bool TermsAccepted { get; set; } = false;

    [FirestoreProperty("onboarding_step")]
    public int OnboardingStep { get; set; } = 0;
}
