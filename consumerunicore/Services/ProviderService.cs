using Google.Cloud.Firestore;
using consumerunicore.Models;
using consumerunicore.Repositories;

namespace consumerunicore.Services
{
    public interface IProviderService
    {
        Task<Provider?> GetByFirebaseUidAsync(string firebaseUid);
    }

    public class ProviderService : IProviderService
    {
        private readonly IFirestoreRepository<Provider> _repository;

        public ProviderService(IFirestoreRepository<Provider> repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public async Task<Provider?> GetByFirebaseUidAsync(string firebaseUid)
        {
            return await _repository.GetByIdAsync(firebaseUid);
        }
    }
}
