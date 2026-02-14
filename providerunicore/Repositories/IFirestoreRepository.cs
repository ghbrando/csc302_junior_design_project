using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

namespace providerunicore.Repositories
{
    // A Generic repository interface for Firestore Database Operations
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
        ///  Simple Query match function, on specific field;
        /// </summary>
        Task<IEnumerable<T>> WhereAsync(string fieldPath, object value);

        /// <summary>
        /// Applies a modifier to the base query and returns the first result.
        /// </summary>
        Task<T?> FirstOrDefaultAsync(Func<Query, Query> queryModifer);

        /// <summary>
        /// Fetches a paginated list of documents using Firestore cursors.
        /// </summary>
        /// <param name="pageSize">Number of records to fetch.</param>
        /// <param name="startAfter">The last document snapshot from the previous page.</param>
        /// <returns>A tuple containing the items and the snapshot of the last document for the next page.</returns>
        Task<(IEnumerable<T> Items, DocumentSnapshot? LastDocument)> GetPagedAsync(int pageSize, DocumentSnapshot? startAfter = null);

        // ==========================================
        // 3. Transaction Support
        // ==========================================

        /// <summary>
        /// Executes a delegate within a Firestore transaction, allowing for safe atomic operations.
        /// </summary>
        Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Transaction, Task<TResult>> transactionDelegate);
        
        Task ExecuteInTransactionAsync(Func<Transaction, Task> transactionDelegate);

        // ==========================================
        // 4. Query Builder
        // ==========================================
        
        /// <summary>
        /// Exposes the base Firestore CollectionReference as a Query to allow for 
        /// advanced chaining (e.g., multiple Where, OrderBy, Limit) at the service level.
        /// </summary>
        Query CreateQuery();
    }
}