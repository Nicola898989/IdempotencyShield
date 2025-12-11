# IdempotencyShield.EntityFrameworkCore

Entity Framework Core storage implementation for the [IdempotencyShield](https://www.nuget.org/packages/IdempotencyShield) library. Allows you to store idempotency records and distributed locks in **SQL Server**, **PostgreSQL**, **MySQL**, or any other database supported by EF Core.

## Installation

```bash
dotnet add package IdempotencyShield.EntityFrameworkCore
```

## Quick Start

### 1. Create your DbContext

You don't need to inherit from a special base class. Just make sure your specific `DbContext` has the required tables. You can achieve this by using our entities or configuring them manually.

**Option A: Dedicated Context (Recommended)**

```csharp
using IdempotencyShield.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class MyIdempotencyContext : DbContext
{
    public MyIdempotencyContext(DbContextOptions<MyIdempotencyContext> options) 
        : base(options) { }

    // Define the datasets
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; set; }
    public DbSet<IdempotencyLockEntity> IdempotencyLocks { get; set; }
}
```

### 2. Register Services

In `Program.cs`:

```csharp
using IdempotencyShield.Extensions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Register EF Core Context
builder.Services.AddDbContext<MyIdempotencyContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Add IdempotencyShield with EF Core storage
builder.Services.AddIdempotencyShieldWithEfCore<MyIdempotencyContext>();

var app = builder.Build();

app.UseIdempotencyShield();
app.MapControllers();
app.Run();
```

### 3. Create Migrations

Run standard EF Core commands to create the tables:

```bash
dotnet ef migrations add InitialCreate --context MyIdempotencyContext
dotnet ef database update --context MyIdempotencyContext
```

This will create two tables:
- `IdempotencyRecords`: Stores cached responses.
- `IdempotencyLocks`: Handles distributed locking.

## Advanced Configuration

### Using an Existing DbContext

If you want to add idempotency tables to your existing application `DbContext`:

```csharp
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Your app entities...
    public DbSet<Product> Products { get; set; }

    // Idempotency entities
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; set; }
    public DbSet<IdempotencyLockEntity> IdempotencyLocks { get; set; }
}
```

Then register it normally:

```csharp
builder.Services.AddIdempotencyShieldWithEfCore<AppDbContext>();
```

### With Custom Options

```csharp
builder.Services.AddIdempotencyShieldWithEfCore<MyIdempotencyContext>(options =>
{
    options.FailureMode = IdempotencyFailureMode.FailSafe;
    options.LockExpirationMilliseconds = 30000;
    options.StorageRetryCount = 3;  // Retry on deadlock/transient failures
});
```

### Supported Databases

Since this depends only on `Microsoft.EntityFrameworkCore`, it works with:
- SQL Server
- PostgreSQL (Npgsql)
- MySQL / MariaDB (Pomelo)
- SQLite
- Oracle
- ...and more!

## Database Schema

The library uses two entities:

### 1. IdempotencyRecordEntity
Stores the cached response.
- `Key` (PK, String): The idempotency key.
- `StatusCode` (Int): HTTP status code.
- `HeaderJson` (String): Serialized headers.
- `Body` (Byte[]): Response body.
- `CreatedAt` (DateTime): Timestamp.
- `ExpiresAt` (DateTime): Expiration time.
- `BodyHash` (String, Nullable): SHA256 of the request payload.

### 2. IdempotencyLockEntity
Manages distributed locks.
- `Key` (PK, String): The key being processed.
- `OwnerId` (String): Unique ID of the process holding the lock.
- `ExpiresAt` (DateTime): Safety expiration to prevent deadlocks.

## Features

✅ **Deadlock Resilience** - Automatic retry on SQL Server/PostgreSQL deadlocks and transient failures  
✅ **Serializable Transactions** - Ensures strict isolation for lock operations  
✅ **Configurable Retry** - Built-in retry mechanism for database conflicts  
✅ **Lock Expiration** - Prevents stuck locks from crashed processes

## Background Cleanup Service

The database will grow over time as new keys are used. Enable the automatic cleanup service to remove expired records:

```csharp
using IdempotencyShield.EntityFrameworkCore.Extensions;

// In Program.cs
builder.Services.AddIdempotencyCleanupService<MyIdempotencyContext>(
    cleanupInterval: TimeSpan.FromHours(1));  // Run cleanup every hour
```

This hosted service:
- Automatically removes expired `IdempotencyRecords` and `IdempotencyLocks`
- Runs in the background without affecting API performance
- Prevents database bloat over time
- Uses efficient bulk delete operations (`ExecuteDeleteAsync` on .NET 7+)

### Manual Cleanup (Alternative)

```sql
-- Example SQL Cleanup
DELETE FROM IdempotencyRecords WHERE ExpiresAt < GETUTCDATE();
DELETE FROM IdempotencyLocks WHERE ExpiresAt < GETUTCDATE();
```

## Related Packages

- [**IdempotencyShield**](https://www.nuget.org/packages/IdempotencyShield): Core library.
- [**IdempotencyShield.Redis**](https://www.nuget.org/packages/IdempotencyShield.Redis): High-performance distributed storage using Redis.

## License

MIT License - See LICENSE file for details.
