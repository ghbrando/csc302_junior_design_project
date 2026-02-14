using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

namespace providerunicore.Repositories
{
    public class FirestoreRepository<T> : IFirestoreRepository<T> where T : class
    {
        private readonly FirestoreDb _firestoreDb;
        private CollectionReference _collection;
        private readonly Func<T, string>? _idSelector;

        // ==========================================
        // Constructor & Setup
        // ==========================================
    
        public FirestoreRepository(
            FirestoreDb firestoreDb,
            string? collectionName = null,
            Func<T, string>? idSelector = null)
        {
            _firestoreDb = firestoreDb;
            _idSelector = idSelector;

            // Validates Collection Name
            string nameToUse = string.IsNullOrWhiteSpace(collectionName) 
                ? GetPluralizedName(typeof(T).Name) 
                : collectionName;

            _collection = _firestoreDb.Collection(nameToUse);
        }

        // Simple helper to pluralize a collection name
        private string GetPluralizedName(string className)
        {
            string lowerName = className.ToLowerInvariant();
            return lowerName.EndsWith("s") ? lowerName : lowerName + "s";
        }

        // ==========================================
        // 1. CRUD Operations
        // ==========================================

        public async Task<T?> GetByIdAsync(string id)
        {
            DocumentSnapshot snapshot = await _collection.Document(id).GetSnapshotAsync();
            return snapshot.Exists ? snapshot.ConvertTo<T>() : null;
        }

        public async Task<IEnumerable<T>> GetAllAsync()
        {
            QuerySnapshot snapshot = await _collection.GetSnapshotAsync();
            return snapshot.Documents.Select(doc => doc.ConvertTo<T>());
        }

        public async Task<string> CreateAsync(T entity)
        {
            // If there is a custom id rule, use it
            if (_idSelector != null)
            {
                string customId = _idSelector(entity);
                await _collection.Document(customId).SetAsync(entity);
                return customId;
            }

            DocumentReference addedDocRef = await _collection.AddAsync(entity);
            return addedDocRef.Id;
        }

        public async Task UpdateAsync(string id, T entity)
        {
            // SetOptions.MergeAll ensures we update existing fields without overwriting the whole document with nulls
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
            QuerySnapshot snapshot = await _collection.WhereEqualTo(fieldPath, value).GetSnapshotAsync();
            return snapshot.Documents.Select(doc => doc.ConvertTo<T>());
        }

        public async Task<T?> FirstOrDefaultAsync(Func<Query, Query> queryModifier)
        {
            Query query = queryModifier(_collection);
            QuerySnapshot snapshot = await query.Limit(1).GetSnapshotAsync();
            
            DocumentSnapshot? firstDoc = snapshot.Documents.FirstOrDefault();
            return firstDoc?.ConvertTo<T>();
        }

        public async Task<(IEnumerable<T> Items, DocumentSnapshot? LastDocument)> GetPagedAsync(int pageSize, DocumentSnapshot? startAfter = null)
        {
            Query query = _collection.Limit(pageSize);
            
            if (startAfter != null)
            {
                query = query.StartAfter(startAfter);
            }

            QuerySnapshot snapshot = await query.GetSnapshotAsync();
            var items = snapshot.Documents.Select(doc => doc.ConvertTo<T>());
            
            // Get the very last document to act as the cursor for the next page
            DocumentSnapshot? lastDoc = snapshot.Documents.LastOrDefault();

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

        public Query CreateQuery()
        {
            // A CollectionReference inherits from Query, so we can just return it directly.
            return _collection;
        }
    }
}