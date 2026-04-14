using Google.Cloud.Firestore;

namespace UniCore.Shared.Repositories;

public class FirestoreRepository<T> : IFirestoreRepository<T> where T : class
{
    private readonly FirestoreDb _firestoreDb;
    private readonly CollectionReference _collection;
    private readonly Func<T, string>? _idSelector;

    public FirestoreRepository(
        FirestoreDb firestoreDb,
        string? collectionName = null,
        Func<T, string>? idSelector = null)
    {
        _firestoreDb = firestoreDb;
        _idSelector = idSelector;

        string nameToUse = string.IsNullOrWhiteSpace(collectionName)
            ? GetPluralizedName(typeof(T).Name)
            : collectionName;

        _collection = _firestoreDb.Collection(nameToUse);
    }

    private static string GetPluralizedName(string className)
    {
        string lowerName = className.ToLowerInvariant();
        return lowerName.EndsWith("s") ? lowerName : lowerName + "s";
    }

    // ==========================================
    // 1. CRUD Operations
    // ==========================================

    public async Task<T?> GetByIdAsync(string id)
    {
        var snapshot = await _collection.Document(id).GetSnapshotAsync();
        return snapshot.Exists ? snapshot.ConvertTo<T>() : null;
    }

    public async Task<IEnumerable<T>> GetAllAsync()
    {
        var snapshot = await _collection.GetSnapshotAsync();
        return snapshot.Documents.Select(doc => doc.ConvertTo<T>());
    }

    public async Task<string> CreateAsync(T entity)
    {
        if (_idSelector != null)
        {
            string customId = _idSelector(entity);
            await _collection.Document(customId).SetAsync(entity);
            return customId;
        }

        var addedDocRef = await _collection.AddAsync(entity);
        return addedDocRef.Id;
    }

    public async Task UpdateAsync(string id, T entity)
    {
        // MergeAll ensures existing fields are not overwritten with nulls
        await _collection.Document(id).SetAsync(entity, SetOptions.MergeAll);
    }

    public async Task DeleteAsync(string id)
    {
        await _collection.Document(id).DeleteAsync();
    }

    // ==========================================
    // 2. Query Support
    // ==========================================

    public async Task<IEnumerable<T>> WhereAsync(string fieldPath, object value)
    {
        var snapshot = await _collection.WhereEqualTo(fieldPath, value).GetSnapshotAsync();
        return snapshot.Documents.Select(doc => doc.ConvertTo<T>());
    }

    public async Task<T?> FirstOrDefaultAsync(Func<Query, Query> queryModifier)
    {
        var query = queryModifier(_collection);
        var snapshot = await query.Limit(1).GetSnapshotAsync();
        return snapshot.Documents.FirstOrDefault()?.ConvertTo<T>();
    }

    public async Task<(IEnumerable<T> Items, DocumentSnapshot? LastDocument)> GetPagedAsync(int pageSize, DocumentSnapshot? startAfter = null)
    {
        Query query = _collection.Limit(pageSize);

        if (startAfter != null)
            query = query.StartAfter(startAfter);

        var snapshot = await query.GetSnapshotAsync();
        var items = snapshot.Documents.Select(doc => doc.ConvertTo<T>());
        var lastDoc = snapshot.Documents.LastOrDefault();

        return (items, lastDoc);
    }

    // ==========================================
    // 3. Transaction Support
    // ==========================================

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Transaction, Task<TResult>> transactionDelegate)
    {
        return await _firestoreDb.RunTransactionAsync(transactionDelegate);
    }

    public async Task ExecuteInTransactionAsync(Func<Transaction, Task> transactionDelegate)
    {
        await _firestoreDb.RunTransactionAsync(transactionDelegate);
    }

    // ==========================================
    // 4. Query Builder
    // ==========================================

    public Query CreateQuery() => _collection;

    // ==========================================
    // 5. Real-Time Listeners
    // ==========================================

    public FirestoreChangeListener Listen(string id, Action<T?> onSnapshot)
    {
        return _collection.Document(id).Listen(snapshot =>
        {
            var entity = snapshot.Exists ? snapshot.ConvertTo<T>() : null;
            onSnapshot(entity);
        });
    }

    public FirestoreChangeListener ListenAll(Action<IEnumerable<T>> onSnapshot)
    {
        return _collection.Listen(snapshot =>
        {
            var entities = snapshot.Documents.Select(doc => doc.ConvertTo<T>());
            onSnapshot(entities);
        });
    }
}
