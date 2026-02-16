using unicoreprovider.Models;

namespace unicoreprovider.Services;

public interface IPayoutService
{
    Task<Payout?> GetByIdAsync(string id);
    Task<IEnumerable<Payout>> GetAllPayoutsAsync();
    Task<IEnumerable<Payout>> GetPayoutsByStatusAsync(string status);
    Task<Payout> CreatePayoutAsync(double amount, string method);
    Task<Payout> UpdatePayoutStatusAsync(string id, string status);
}
