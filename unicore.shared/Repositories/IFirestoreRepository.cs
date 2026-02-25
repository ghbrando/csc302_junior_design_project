using Google.Cloud.Firestore;

namespace UniCore.Shared.Repositories;

/// <summary>
/// Generic repository interface for Firestore database operations.
/// </summary>
public interface IFirestoreRepository<T> where T : class
{
    // ==========================================
    // 1. CRUD Operations
    // ==========================================

    Task<T?> GetByIdAsync(string id);

    Task<IEnumerable<T>> GetAllAsync();

    Task<string> CreateAsync(T entity);

    Task UpdateAsync(string id, T entity);

    Task DeleteAsync(string id);

    // ==========================================
    // 2. Query Support
    // ==========================================

    /// <summary>
    /// Simple equality query on a specific field.
    /// </summary>
    Task<IEnumerable<T>> WhereAsync(string fieldPath, object value);

    /// <summary>
    /// Applies a modifier to the base query and returns the first result.
    /// </summary>
    Task<T?> FirstOrDefaultAsync(Func<Query, Query> queryModifier);

    /// <summary>
    /// Fetches a paginated list of documents using Firestore cursors.
    /// </summary>
    Task<(IEnumerable<T> Items, DocumentSnapshot? LastDocument)> GetPagedAsync(int pageSize, DocumentSnapshot? startAfter = null);

    // ==========================================
    // 3. Transaction Support
    // ==========================================

    /// <summary>
    /// Executes a delegate within a Firestore transaction for safe atomic operations.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Transaction, Task<TResult>> transactionDelegate);

    Task ExecuteInTransactionAsync(Func<Transaction, Task> transactionDelegate);

    // ==========================================
    // 4. Query Builder
    // ==========================================

    /// <summary>
    /// Exposes the base Firestore CollectionReference as a Query for advanced chaining.
    /// </summary>
    Query CreateQuery();

    // ==========================================
    // 5. Real-Time Listeners
    // ==========================================

    /// <summary>
    /// Listens for real-time changes to a single document by ID.
    /// </summary>
    FirestoreChangeListener Listen(string id, Action<T?> onSnapshot);

    /// <summary>
    /// Listens for real-time changes to the entire collection.
    /// </summary>
    FirestoreChangeListener ListenAll(Action<IEnumerable<T>> onSnapshot);
}
