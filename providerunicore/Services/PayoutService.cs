using unicoreprovider.Models;
using providerunicore.Repositories;

namespace unicoreprovider.Services;

public class PayoutService : IPayoutService
{
    private readonly IFirestoreRepository<Payout> _repository;

    public PayoutService(IFirestoreRepository<Payout> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<Payout?> GetByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Payout>> GetAllPayoutsAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<IEnumerable<Payout>> GetPayoutsByStatusAsync(string status)
    {
        return await _repository.WhereAsync("status", status);
    }

    public async Task<Payout> CreatePayoutAsync(decimal amount, string method)
    {
        var payout = new Payout
        {
            Date = DateTime.UtcNow,
            Amount = amount,
            Method = method,
            Status = "Pending"
        };

        // CreateAsync returns the generated document ID
        string generatedId = await _repository.CreateAsync(payout);
        payout.Id = generatedId;
        return payout;
    }

    public async Task<Payout> UpdatePayoutStatusAsync(string id, string status)
    {
        var payout = await _repository.GetByIdAsync(id);

        if (payout == null)
            throw new Exception($"Payout {id} not found");

        payout.Status = status;
        await _repository.UpdateAsync(id, payout);
        return payout;
    }
}
