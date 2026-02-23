# Firestore Repository Pattern - Developer Guide

This guide explains how to add new data models to the application using our generic Firestore repository pattern.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Adding a New Model - Step by Step](#adding-a-new-model---step-by-step)
- [Examples](#examples)
- [Best Practices](#best-practices)
- [Troubleshooting](#troubleshooting)

---

## Overview

Our application uses a **generic repository pattern** for Firestore data access. This pattern provides:

✅ **Consistency** - All models follow the same CRUD pattern
✅ **Reusability** - One repository implementation serves all models
✅ **Type Safety** - Compile-time checking with C# generics
✅ **Testability** - Easy to mock repositories for unit tests
✅ **Separation of Concerns** - Business logic stays in services, data access in repositories

### Architecture Layers

```
Controllers/Pages → Services (Business Logic) → Repositories (Data Access) → Firestore
```

**Example Flow:**
```
Dashboard.razor → IVmService → IFirestoreRepository<VirtualMachine> → Firestore
```

---

## Architecture

### Core Components

1. **IFirestoreRepository&lt;T&gt;** - Generic interface for all CRUD operations
2. **FirestoreRepository&lt;T&gt;** - Generic implementation handling Firestore operations
3. **Service Layer** - Business logic specific to each model (e.g., `IVmService`, `VirtualMachineService`)
4. **Models** - Data classes with Firestore attributes

### Repository Methods Available

| Method | Purpose | Returns |
|--------|---------|---------|
| `GetByIdAsync(id)` | Fetch single document | `Task<T?>` |
| `GetAllAsync()` | Fetch all documents | `Task<IEnumerable<T>>` |
| `CreateAsync(entity)` | Create new document | `Task<string>` (document ID) |
| `UpdateAsync(id, entity)` | Update existing document | `Task` (void) |
| `DeleteAsync(id)` | Delete document | `Task` (void) |
| `WhereAsync(field, value)` | Query by field equality | `Task<IEnumerable<T>>` |
| `FirstOrDefaultAsync(query)` | Get first matching document | `Task<T?>` |
| `GetPagedAsync(pageSize, cursor)` | Paginated results | `Task<(IEnumerable<T>, DocumentSnapshot?)>` |
| `CreateQuery()` | Build advanced queries | `Query` |

---

## Adding a New Model - Step by Step

Follow these steps to add a new model to the Firestore repository pattern.

### Step 1: Create the Model Class

Create your model in the `/Models` folder with Firestore attributes.

**File:** `Models/YourModel.cs`

```csharp
using Google.Cloud.Firestore;

namespace unicoreprovider.Models;

[FirestoreData]
public class YourModel
{
    [FirestoreProperty("id")]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("name")]
    public string Name { get; set; } = string.Empty;

    [FirestoreProperty("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [FirestoreProperty("is_active")]
    public bool IsActive { get; set; } = true;

    // Add more properties as needed
}
```

**Key Points:**
- Add `[FirestoreData]` attribute to the class
- Add `[FirestoreProperty("field_name")]` to each property
- Use **snake_case** for Firestore field names (consistent with existing models)
- Initialize properties with sensible defaults

---

### Step 2: Create the Service Interface

Define the business operations for your model.

**File:** `Services/IYourModelService.cs`

```csharp
using unicoreprovider.Models;

namespace unicoreprovider.Services;

public interface IYourModelService
{
    // Basic CRUD
    Task<YourModel?> GetByIdAsync(string id);
    Task<IEnumerable<YourModel>> GetAllAsync();
    Task<YourModel> CreateAsync(YourModel model);
    Task<YourModel> UpdateAsync(string id, YourModel model);
    Task DeleteAsync(string id);

    // Business-specific operations
    Task<IEnumerable<YourModel>> GetActiveModelsAsync();
    Task<YourModel> DeactivateAsync(string id);
}
```

**Key Points:**
- Include standard CRUD operations
- Add business-specific methods that make sense for your model
- Return the model entity from create/update for convenience

---

### Step 3: Implement the Service

Implement the business logic using the repository.

**File:** `Services/YourModelService.cs`

```csharp
using unicoreprovider.Models;
using providerunicore.Repositories;

namespace unicoreprovider.Services;

public class YourModelService : IYourModelService
{
    private readonly IFirestoreRepository<YourModel> _repository;

    public YourModelService(IFirestoreRepository<YourModel> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    // Basic CRUD implementations
    public async Task<YourModel?> GetByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<YourModel>> GetAllAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<YourModel> CreateAsync(YourModel model)
    {
        // Set creation timestamp
        model.CreatedAt = DateTime.UtcNow;

        // Auto-generate ID if not provided
        if (string.IsNullOrWhiteSpace(model.Id))
            model.Id = Guid.NewGuid().ToString();

        // CreateAsync returns the document ID
        await _repository.CreateAsync(model);
        return model;
    }

    public async Task<YourModel> UpdateAsync(string id, YourModel model)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
            throw new Exception($"YourModel {id} not found");

        // UpdateAsync returns void, so call it then return the model
        await _repository.UpdateAsync(id, model);
        return model;
    }

    public async Task DeleteAsync(string id)
    {
        await _repository.DeleteAsync(id);
    }

    // Business-specific implementations
    public async Task<IEnumerable<YourModel>> GetActiveModelsAsync()
    {
        return await _repository.WhereAsync("is_active", true);
    }

    public async Task<YourModel> DeactivateAsync(string id)
    {
        var model = await _repository.GetByIdAsync(id);
        if (model == null)
            throw new Exception($"YourModel {id} not found");

        model.IsActive = false;
        await _repository.UpdateAsync(id, model);
        return model;
    }
}
```

**Key Points:**
- Inject `IFirestoreRepository<YourModel>` via constructor
- Add business logic in service methods (validation, calculations, etc.)
- `CreateAsync` returns document ID - use it or generate your own
- `UpdateAsync` returns void - call it then return the modified entity
- Always check if entity exists before updating/deleting

---

### Step 4: Register in Dependency Injection

Add repository and service registrations to `Program.cs`.

**File:** `Program.cs`

```csharp
// After existing repository registrations
builder.Services.AddFirestoreRepository<YourModel>(
    collectionName: "your_models",  // Firestore collection name
    documentIdSelector: m => m.Id); // How to extract document ID from entity

// After existing service registrations
builder.Services.AddScoped<IYourModelService, YourModelService>();
```

**Key Points:**
- `collectionName` - The Firestore collection (usually plural, snake_case)
- `documentIdSelector` - Lambda to extract document ID from entity
  - If omitted, Firestore will auto-generate document IDs
- Services are typically `Scoped` lifetime (new instance per HTTP request)

---

### Step 5: Use in Controllers/Pages

Inject and use the service in your Blazor pages or API controllers.

**Example - Blazor Page:**

```razor
@page "/your-models"
@using unicoreprovider.Models
@using unicoreprovider.Services
@inject IYourModelService YourModelService

<h3>Your Models</h3>

@if (_models == null)
{
    <p>Loading...</p>
}
else
{
    @foreach (var model in _models)
    {
        <div>@model.Name - @model.CreatedAt</div>
    }
}

@code {
    private List<YourModel> _models;

    protected override async Task OnInitializedAsync()
    {
        _models = (await YourModelService.GetAllAsync()).ToList();
    }
}
```

**Example - API Controller:**

```csharp
[ApiController]
[Route("api/[controller]")]
public class YourModelController : ControllerBase
{
    private readonly IYourModelService _service;

    public YourModelController(IYourModelService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var models = await _service.GetAllAsync();
        return Ok(models);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var model = await _service.GetByIdAsync(id);
        if (model == null)
            return NotFound();
        return Ok(model);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] YourModel model)
    {
        var created = await _service.CreateAsync(model);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }
}
```

---

## Examples

### Example 1: Simple Model (Auto-Generated IDs)

**Model:**
```csharp
[FirestoreData]
public class Notification
{
    [FirestoreProperty("id")]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("message")]
    public string Message { get; set; } = string.Empty;

    [FirestoreProperty("timestamp")]
    public DateTime Timestamp { get; set; }
}
```

**Registration (no documentIdSelector):**
```csharp
builder.Services.AddFirestoreRepository<Notification>(
    collectionName: "notifications");
```

**Service Create Method:**
```csharp
public async Task<Notification> CreateAsync(string message)
{
    var notification = new Notification
    {
        Message = message,
        Timestamp = DateTime.UtcNow
    };

    // Auto-generates ID and assigns it
    string generatedId = await _repository.CreateAsync(notification);
    notification.Id = generatedId;
    return notification;
}
```

---

### Example 2: Model with Custom Document ID

**Model:**
```csharp
[FirestoreData]
public class UserProfile
{
    [FirestoreProperty("user_id")]
    public string UserId { get; set; } = string.Empty;  // Firebase Auth UID

    [FirestoreProperty("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [FirestoreProperty("email")]
    public string Email { get; set; } = string.Empty;
}
```

**Registration (with documentIdSelector):**
```csharp
builder.Services.AddFirestoreRepository<UserProfile>(
    collectionName: "user_profiles",
    documentIdSelector: u => u.UserId);  // Use UserId as document ID
```

**Service Create Method:**
```csharp
public async Task<UserProfile> CreateAsync(string userId, string displayName, string email)
{
    var profile = new UserProfile
    {
        UserId = userId,
        DisplayName = displayName,
        Email = email
    };

    // Uses UserId as document ID automatically
    await _repository.CreateAsync(profile);
    return profile;
}
```

---

### Example 3: Model with Complex Properties

**Model with Lists:**
```csharp
[FirestoreData]
public class ShoppingCart
{
    [FirestoreProperty("cart_id")]
    public string CartId { get; set; } = string.Empty;

    [FirestoreProperty("user_id")]
    public string UserId { get; set; } = string.Empty;

    [FirestoreProperty("items")]
    public List<CartItem> Items { get; set; } = new();

    [FirestoreProperty("total")]
    public decimal Total { get; set; }
}

[FirestoreData]
public class CartItem
{
    [FirestoreProperty("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [FirestoreProperty("quantity")]
    public int Quantity { get; set; }

    [FirestoreProperty("price")]
    public decimal Price { get; set; }
}
```

**Service Business Logic:**
```csharp
public async Task<ShoppingCart> AddItemAsync(string cartId, CartItem item)
{
    var cart = await _repository.GetByIdAsync(cartId);
    if (cart == null)
        throw new Exception($"Cart {cartId} not found");

    // Business logic: Check if item already exists
    var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == item.ProductId);
    if (existingItem != null)
    {
        existingItem.Quantity += item.Quantity;
    }
    else
    {
        cart.Items.Add(item);
    }

    // Recalculate total
    cart.Total = cart.Items.Sum(i => i.Price * i.Quantity);

    await _repository.UpdateAsync(cartId, cart);
    return cart;
}
```

---

### Example 4: Advanced Queries

**Service with Complex Filtering:**
```csharp
public async Task<IEnumerable<Order>> GetRecentOrdersAsync(string userId, int limit)
{
    var query = _repository.CreateQuery()
        .WhereEqualTo("user_id", userId)
        .WhereEqualTo("status", "completed")
        .OrderByDescending("created_at")
        .Limit(limit);

    return await _repository.FirstOrDefaultAsync(q => query);
}

public async Task<IEnumerable<Product>> SearchProductsAsync(string category, decimal minPrice, decimal maxPrice)
{
    var query = _repository.CreateQuery()
        .WhereEqualTo("category", category)
        .WhereGreaterThanOrEqualTo("price", minPrice)
        .WhereLessThanOrEqualTo("price", maxPrice)
        .OrderBy("price");

    var snapshot = await query.GetSnapshotAsync();
    return snapshot.Documents.Select(doc => doc.ConvertTo<Product>());
}
```

---

## Best Practices

### 1. Naming Conventions

✅ **DO:**
- Use **snake_case** for Firestore property names (`"created_at"`, `"user_id"`)
- Use **PascalCase** for C# property names (`CreatedAt`, `UserId`)
- Use **plural, lowercase** collection names (`"users"`, `"virtual_machines"`, `"payouts"`)

❌ **DON'T:**
- Mix naming conventions (pick one and stick to it)
- Use spaces in property names
- Use reserved Firestore field names

### 2. Property Initialization

✅ **DO:**
```csharp
public string Name { get; set; } = string.Empty;  // Avoid null reference errors
public List<Item> Items { get; set; } = new();    // Avoid null lists
public DateTime CreatedAt { get; set; } = DateTime.UtcNow;  // Sensible default
```

❌ **DON'T:**
```csharp
public string Name { get; set; }  // Can be null - potential NRE
public List<Item> Items { get; set; }  // Can be null - crashes on .Add()
```

### 3. Document ID Strategy

**Option A: Auto-Generated IDs**
- Use when: No natural unique identifier
- Registration: `AddFirestoreRepository<T>("collection_name")` (no selector)
- Example: Notifications, Logs, Comments

**Option B: Custom Property IDs**
- Use when: Natural unique identifier exists
- Registration: `AddFirestoreRepository<T>("collection_name", entity => entity.Id)`
- Example: UserProfiles (use Firebase UID), Products (use SKU)

### 4. Error Handling

✅ **DO:**
```csharp
public async Task<Order> UpdateOrderStatusAsync(string orderId, string newStatus)
{
    var order = await _repository.GetByIdAsync(orderId);
    if (order == null)
        throw new Exception($"Order {orderId} not found");

    // Validate state transition
    if (!IsValidStatusTransition(order.Status, newStatus))
        throw new InvalidOperationException($"Cannot transition from {order.Status} to {newStatus}");

    order.Status = newStatus;
    order.UpdatedAt = DateTime.UtcNow;
    await _repository.UpdateAsync(orderId, order);
    return order;
}
```

### 5. Timestamps

✅ **DO:**
```csharp
[FirestoreData]
public class BaseModel
{
    [FirestoreProperty("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [FirestoreProperty("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
```

**Set timestamps in service layer:**
```csharp
public async Task<Model> CreateAsync(Model model)
{
    model.CreatedAt = DateTime.UtcNow;
    model.UpdatedAt = null;
    await _repository.CreateAsync(model);
    return model;
}

public async Task<Model> UpdateAsync(string id, Model model)
{
    model.UpdatedAt = DateTime.UtcNow;
    await _repository.UpdateAsync(id, model);
    return model;
}
```

### 6. Service vs Repository

**Repository** - Simple data access (CRUD only)
**Service** - Business logic, validation, calculations

❌ **DON'T put business logic in repositories:**
```csharp
// WRONG - business logic in repository
public async Task<Order> ApproveOrderAsync(string orderId)
{
    var order = await GetByIdAsync(orderId);
    order.Status = "approved";
    order.ApprovedBy = GetCurrentUser();  // ❌ Business logic
    await UpdateAsync(orderId, order);
    return order;
}
```

✅ **DO put business logic in services:**
```csharp
// CORRECT - business logic in service
public async Task<Order> ApproveOrderAsync(string orderId, string approvedBy)
{
    var order = await _repository.GetByIdAsync(orderId);
    if (order == null)
        throw new Exception("Order not found");

    // Business validation
    if (order.Status != "pending")
        throw new InvalidOperationException("Only pending orders can be approved");

    order.Status = "approved";
    order.ApprovedBy = approvedBy;
    order.ApprovedAt = DateTime.UtcNow;

    await _repository.UpdateAsync(orderId, order);
    return order;
}
```

---

## Troubleshooting

### Build Error: "No overload for method 'CreateAsync' takes 2 arguments"

**Problem:** You're calling `CreateAsync(entity, documentId)` but the method only accepts 1 parameter.

**Solution:** Use the `documentIdSelector` in registration, then call `CreateAsync(entity)`:
```csharp
// Registration
builder.Services.AddFirestoreRepository<Model>(
    collectionName: "models",
    documentIdSelector: m => m.Id);

// Service
await _repository.CreateAsync(model);  // ✅ Correct
```

### Build Error: "Cannot implicitly convert type 'void' to 'Model'"

**Problem:** `UpdateAsync` returns `Task` (void), not `Task<Model>`.

**Solution:**
```csharp
// ❌ WRONG
return await _repository.UpdateAsync(id, model);

// ✅ CORRECT
await _repository.UpdateAsync(id, model);
return model;
```

### Runtime Error: "Firestore field 'field_name' not found"

**Problem:** Property name doesn't match Firestore field name.

**Solution:** Ensure `[FirestoreProperty("field_name")]` matches the actual field in Firestore:
```csharp
[FirestoreProperty("user_id")]  // ✅ Must match Firestore field
public string UserId { get; set; }
```

### Data Not Persisting

**Problem:** Service not registered in DI or wrong lifetime.

**Solution:** Check `Program.cs`:
```csharp
// Repository registration
builder.Services.AddFirestoreRepository<YourModel>("collection_name");

// Service registration
builder.Services.AddScoped<IYourModelService, YourModelService>();
```

---

## Quick Reference Checklist

When adding a new model, ensure you've completed:

- [ ] Created model class in `/Models` folder
- [ ] Added `[FirestoreData]` attribute to class
- [ ] Added `[FirestoreProperty("field_name")]` to all properties
- [ ] Used snake_case for Firestore field names
- [ ] Created service interface in `/Services` folder (e.g., `IYourModelService`)
- [ ] Created service implementation in `/Services` folder (e.g., `YourModelService`)
- [ ] Injected `IFirestoreRepository<YourModel>` in service constructor
- [ ] Registered repository in `Program.cs` using `AddFirestoreRepository<T>()`
- [ ] Registered service in `Program.cs` using `AddScoped<IService, Service>()`
- [ ] Used service in controller/page by injecting `IYourModelService`
- [ ] Tested CRUD operations (Create, Read, Update, Delete)

---

## Need Help?

- **Firestore Documentation:** https://cloud.google.com/firestore/docs
- **Existing Examples:** See `Provider`, `VirtualMachine`, and `Payout` models
- **Repository Code:** `/Repositories/IFirestoreRepository.cs` and `/Repositories/FirestoreRepository.cs`

---

**Last Updated:** February 2026
**Maintained By:** Development Team
