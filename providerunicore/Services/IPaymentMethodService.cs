using unicore.shared.Models;

namespace unicoreprovider.Services
{
    public interface IPaymentMethodService
    {
        Task<List<PaymentMethod>> GetAllAsync(string firebaseUid);
        Task<PaymentMethod?> GetPrimaryAsync(string firebaseUid);
        Task AddAsync(string firebaseUid, PaymentMethod method);
        Task SetPrimaryAsync(string firebaseUid, string methodId);
        Task DeleteAsync(string firebaseUid, string methodId);
    }
}