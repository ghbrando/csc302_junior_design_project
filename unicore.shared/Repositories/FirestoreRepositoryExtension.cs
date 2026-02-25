using Google.Cloud.Firestore;
using Microsoft.Extensions.DependencyInjection;

namespace UniCore.Shared.Repositories;

public static class FirestoreRepositoryExtensions
{
    /// <summary>
    /// Registers a generic Firestore repository for a specific entity type.
    /// </summary>
    public static IServiceCollection AddFirestoreRepository<T>(
        this IServiceCollection services,
        string? collectionName = null,
        Func<T, string>? documentIdSelector = null,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where T : class
    {
        services.Add(new ServiceDescriptor(
            typeof(IFirestoreRepository<T>),
            sp =>
            {
                var db = sp.GetRequiredService<FirestoreDb>();
                return new FirestoreRepository<T>(db, collectionName, documentIdSelector);
            },
            lifetime));

        return services;
    }
}
