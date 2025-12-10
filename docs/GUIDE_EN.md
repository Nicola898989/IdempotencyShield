# Comprehensive Guide to IdempotencyShield
This guide will walk you through integrating **IdempotencyShield** into your ASP.NET Core Web API step-by-step.

---

## 🧐 What is it and why do you need it?
Imagine a user clicks the "Pay" button on your app twice in quick succession. Without protection, you might charge their credit card twice!

**IdempotencyShield** solves this problem:
1.  Receives the first request and processes it.
2.  If an identical second request arrives (with the same key), it **stops execution** and immediately returns the result of the first one.
3.  Protects your database and external services from dangerous duplicates.

---

## 🚀 Step 1: Installation

Open your terminal in your project folder and install the base package:

```bash
dotnet add package IdempotencyShield
```

If you plan to use **Redis** (recommended for production) or **SQL Server/Postgres**:

```bash
# For Redis
dotnet add package IdempotencyShield.Redis

# For Entity Framework Core (SQL)
dotnet add package IdempotencyShield.EntityFrameworkCore
```

---

## 🛠️ Step 2: Configuration (Program.cs)

Configuration changes depending on where you want to save data (Memory, Redis, or Database). Choose the one that fits your needs.

### A. For Development / Testing (In-Memory)
The fastest way to start. Data is lost if you restart the application.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Register the service (Default: In-Memory)
builder.Services.AddIdempotencyShield(); 

var app = builder.Build();

// 2. Add the Middleware (IMPORTANT: after UseRouting, before MapControllers)
app.UseRouting();
app.UseIdempotencyShield(); // <--- Here!
app.MapControllers();

app.Run();
```

### B. For Production (Redis)
Ideal for microservices or distributed applications.

```csharp
using IdempotencyShield.Extensions;

// Configure Redis
builder.Services.AddIdempotencyShieldWithRedis(
    redisConfiguration: "localhost:6379,abortConnect=false", 
    configureOptions: options => 
    {
        options.HeaderName = "Idempotency-Key"; // HTTP Header name
        options.DefaultExpiryMinutes = 60;      // Default cache duration
    }
);
```

### C. For Production (SQL Server / Postgres with EF Core)
Use your existing database to store locks and responses.

```csharp
using IdempotencyShield.EntityFrameworkCore.Extensions;

// Ensure your DbContext implements IIdempotencyDbContext
builder.Services.AddDbContext<MyDbContext>(options => ...);

// Register IdempotencyShield using your DbContext
builder.Services.AddIdempotencyShieldWithEfCore<MyDbContext>();
```

> **Note for EF Core**: Remember to create tables in the database!
> If using Migrations: `dotnet ef migrations add AddIdempotencyTables`
> If you want to do it automatically at startup (dev only): `dbContext.Database.EnsureCreated()`

---

## 💻 Step 3: Protect an Endpoint

Now that it's configured, let's protect a sensitive route, for example, a payment.

Go to your `Controller` and add the `[Idempotent]` attribute.

```csharp
using IdempotencyShield.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    [HttpPost]
    // 🛡️ Protection Active!
    // ExpiryInMinutes: How long do we remember the response?
    // ValidatePayload: Do we check if JSON body is identical? (Recommended: true)
    [Idempotent(ExpiryInMinutes = 60, ValidatePayload = true)]
    public IActionResult CreatePayment([FromBody] PaymentRequest request)
    {
        // Payment logic...
        // This will run EXACTLY ONCE per unique key.
        
        return Ok(new { status = "Payment Successful", id = Guid.NewGuid() });
    }
}
```

---

## 📡 Step 4: How to Call the API (Client)

The client (Frontend, Mobile App, or Postman) **MUST** send a header with a unique key for each operation.

### Example with cURL
```bash
curl -X POST https://localhost:5001/api/payments \
   -H "Content-Type: application/json" \
   -H "Idempotency-Key: 12345-unique-transaction-key" \
   -d '{"amount": 100}'
```

### What happens?

1.  **First Call**:
    *   Server processes payment.
    *   Returns `200 OK`.
    *   Saves response to cache.

2.  **Second Call** (same `Idempotency-Key`):
    *   Server sees key exists.
    *   **Does NOT execute controller**.
    *   Immediately returns the saved `200 OK`.

3.  **Concurrent Call** (two requests starting together):
    *   First one wins and processes.
    *   Second one receives `409 Conflict` (or waits, if configured).

---

## ⚙️ Advanced Settings

You can customize everything in `Program.cs`:

```csharp
builder.Services.AddIdempotencyShield(options =>
{
    // Change header name (Standard is "Idempotency-Key")
    options.HeaderName = "X-Request-ID"; 

    // Default max cache time (if not specified in attribute)
    options.DefaultExpiryMinutes = 120; // 2 hours

    // Do you want waiting requests to wait a bit before failing?
    // 0 = Fail immediately (409 Conflict) if locked.
    // >0 = Wait N milliseconds hoping lock releases.
    options.LockTimeoutMilliseconds = 100; 

    // Validate Idempotency-Key format (Optional)
    // Return 400 Bad Request if validation fails
    options.KeyValidator = key => Guid.TryParse(key, out _);
});
```

---

## ❓ Common Troubleshooting

### 🔴 I always get 409 Conflict
*   Are you using the same key for different simultaneous requests?
*   Is your store (Redis/DB) reachable?
*   Check for "orphan locks" (should expire automatically after 30-60 seconds).

### 🔴 I get 422 Unprocessable Entity
*   Are you reusing an old `Idempotency-Key` but changed the JSON content (Body)?
*   The system protects against this error: a key is born for a specific payload. If you change data, change the key.

### 🔴 The response is not saved
*   Does the endpoint return an error (500, 400)? By default, we only save successes (2xx).
*   Did you forget `app.UseIdempotencyShield()` in `Program.cs`?

---

Thank you for choosing IdempotencyShield!
For support, open an issue on GitHub.
