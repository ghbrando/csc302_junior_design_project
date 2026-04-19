using Google.Cloud.Firestore;
using unicore.shared.Models;

namespace unicoreconsumer.Services
{
    public class PaymentMethodService : IPaymentMethodService
    {
        private readonly FirestoreDb _db;

        public PaymentMethodService(FirestoreDb db)
        {
            _db = db;
        }

        private CollectionReference Col(string uid) =>
            _db.Collection("consumers").Document(uid).Collection("payment_methods");

        public async Task<List<PaymentMethod>> GetAllAsync(string uid)
        {
            var snap = await Col(uid).GetSnapshotAsync();
            return snap.Documents.Select(d => d.ConvertTo<PaymentMethod>()).ToList();
        }

        public async Task<PaymentMethod?> GetPrimaryAsync(string uid)
        {
            var snap = await Col(uid).WhereEqualTo("is_primary", true).Limit(1).GetSnapshotAsync();
            return snap.Documents.FirstOrDefault()?.ConvertTo<PaymentMethod>();
        }

        public async Task AddAsync(string uid, PaymentMethod method)
        {
            var existing = await GetAllAsync(uid);
            method.IsPrimary = !existing.Any();
            method.CreatedAt = Timestamp.GetCurrentTimestamp();

            var dict = new Dictionary<string, object>
            {
                { "account_holder_name", method.AccountHolderName },
                { "account_number",      method.AccountNumber },
                { "routing_number",      method.RoutingNumber },
                { "is_primary",          method.IsPrimary },
                { "created_at",          method.CreatedAt }
            };

            await Col(uid).AddAsync(dict);
        }

        public async Task SetPrimaryAsync(string uid, string methodId)
        {
            var all = await GetAllAsync(uid);
            var batch = _db.StartBatch();
            foreach (var m in all)
            {
                batch.Update(Col(uid).Document(m.Id), new Dictionary<string, object>
                {
                    { "is_primary", m.Id == methodId }
                });
            }
            await batch.CommitAsync();
        }

        public async Task DeleteAsync(string uid, string methodId)
        {
            await Col(uid).Document(methodId).DeleteAsync();
            var remaining = await GetAllAsync(uid);
            if (remaining.Any() && !remaining.Any(m => m.IsPrimary))
                await SetPrimaryAsync(uid, remaining.First().Id);
        }
    }
}