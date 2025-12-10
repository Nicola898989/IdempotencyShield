# IdempotencyShield.Redis

Redis-backed implementation of `IIdempotencyStore` for the [IdempotencyShield](../IdempotencyShield/README.md) library. Enables distributed caching and locking for multi-instance ASP.NET Core deployments.

## Installation

```bash
dotnet add package IdempotencyShield.Redis
```

## Quick Start

### Basic Setup

```csharp
using IdempotencyShield.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add IdempotencyShield with Redis backend
builder.Services.AddIdempotencyShieldWithRedis("localhost:6379");

var app = builder.Build();

app.UseIdempotencyShield();
app.UseRouting();
app.MapControllers();

app.Run();
```

### With Configuration

```csharp
builder.Services.AddIdempotencyShieldWithRedis(
    redisConfiguration: "localhost:6379,password=mypassword,ssl=true",
    configureOptions: options =>
    {
        options.HeaderName = "X-Idempotency-Key";
        options.DefaultExpiryMinutes = 120;
    });
```

### Using ConfigurationOptions

```csharp
using StackExchange.Redis;

var redisOptions = new ConfigurationOptions
{
    EndPoints = { "redis-master:6379", "redis-replica:6379" },
    Password = "your-password",
    Ssl = true,
    AbortOnConnectFail = false,
    ConnectRetry = 3,
    ConnectTimeout = 5000
};

builder.Services.AddIdempotencyShieldWithRedis(redisOptions);
```

### Using Existing IConnectionMultiplexer

If you already have Redis configured in your application:

```csharp
// Register Redis elsewhere
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
{
    var configuration = ConfigurationOptions.Parse("localhost:6379");
    return ConnectionMultiplexer.Connect(configuration);
});

// Use existing connection
builder.Services.AddIdempotencyShieldWithRedis();
```

## Redis Key Structure

The implementation uses the following key prefixes:

- **Cache Keys**: `idempotency:cache:{idempotency-key}`
  - Stores serialized `IdempotencyRecord` (status, headers, body, hash)
  - Expires based on `ExpiryInMinutes` attribute parameter

- **Lock Keys**: `idempotency:lock:{idempotency-key}`
  - Used for distributed locking via `SET NX`
  - Automatically expires after 30 seconds (prevents deadlocks)
  - Stores machine name for debugging

## Features

✅ **Distributed Locking** - Uses Redis `SET NX` for atomic lock acquisition  
✅ **Automatic Expiration** - TTL on both cache and locks  
✅ **JSON Serialization** - Efficient storage using System.Text.Json  
✅ **Error Handling** - Gracefully handles corrupted data  
✅ **Deadlock Prevention** - Locks auto-expire after 30 seconds  
✅ **Multi-Instance Safe** - Designed for load-balanced deployments

## Configuration Options

### Redis Connection String Format

```
server:port[,server:port][,options]
```

**Common Options**:
- `password=xxx` - Redis password
- `ssl=true` - Enable SSL/TLS
- `abortConnect=false` - Don't fail on initial connection error
- `connectRetry=3` - Number of retry attempts
- `connectTimeout=5000` - Connection timeout in milliseconds
- `syncTimeout=1000` - Operation timeout in milliseconds

**Example**:
```
redis.example.com:6379,password=secret,ssl=true,abortConnect=false
```

## Production Recommendations

### 1. Connection Resilience

```csharp
var redisOptions = new ConfigurationOptions
{
    EndPoints = { "redis-primary:6379" },
    AbortOnConnectFail = false, // Don't crash if Redis is down
    ConnectRetry = 3,
    ReconnectRetryPolicy = new ExponentialRetry(5000),
    KeepAlive = 60
};
```

### 2. Redis Cluster Setup

For high availability, use Redis Cluster or Sentinel:

```csharp
var redisOptions = new ConfigurationOptions
{
    EndPoints = 
    { 
        "redis-node1:6379",
        "redis-node2:6379", 
        "redis-node3:6379"
    },
    Password = "your-password",
    Ssl = true
};
```

### 3. Monitoring

Monitor Redis key usage:

```bash
# Count idempotency keys
redis-cli --scan --pattern "idempotency:*" | wc -l

# Check lock keys (should be minimal)
redis-cli --scan --pattern "idempotency:lock:*"

# View a cached record
redis-cli GET "idempotency:cache:your-key-here"
```

### 4. Memory Management

Set Redis `maxmemory-policy` to `allkeys-lru` or `volatile-ttl`:

```conf
# redis.conf
maxmemory 2gb
maxmemory-policy allkeys-lru
```

## Comparison with InMemoryStore

| Feature | InMemoryStore | RedisStore |
|---------|---------------|------------|
| **Use Case** | Single instance, dev/test | Multi-instance production |
| **Distributed** | ❌ No | ✅ Yes |
| **Persistence** | ❌ Lost on restart | ✅ Persisted (if configured) |
| **Scalability** | Single server | Horizontal scaling |
| **Lock Safety** | Process-local | Distributed |
| **Setup Complexity** | None | Requires Redis |

## Thread Safety

The `RedisIdempotencyStore` is fully thread-safe:
- Redis operations are atomic
- `SET NX` provides distributed lock guarantees
- JSON serialization is thread-safe
- No shared mutable state

## Performance Considerations

- **Network Latency**: ~1-2ms per Redis operation (local network)
- **Serialization Overhead**: ~0.5ms per JSON operation
- **Lock Contention**: Non-blocking, immediate 409 response
- **Cache Hits**: Near-instant (single Redis GET)

## Troubleshooting

### Connection Failures

```csharp
// Add logging to diagnose connection issues
var redisOptions = ConfigurationOptions.Parse("localhost:6379");
redisOptions.AbortOnConnectFail = false;

var redis = ConnectionMultiplexer.Connect(redisOptions);
redis.ConnectionFailed += (sender, args) =>
{
    Console.WriteLine($"Redis connection failed: {args.Exception}");
};

services.AddSingleton(redis);
```

### Lock Key Leaks

If lock keys accumulate, increase cleanup:

```bash
# Manual cleanup (use with caution)
redis-cli --scan --pattern "idempotency:lock:*" | xargs redis-cli DEL
```

Lock keys should auto-expire after 30 seconds. Persistent lock keys indicate crashed processes.

## Related Packages

- [**IdempotencyShield**](https://www.nuget.org/packages/IdempotencyShield): Core library.
- [**IdempotencyShield.EntityFrameworkCore**](https://www.nuget.org/packages/IdempotencyShield.EntityFrameworkCore): Alternative storage using Entity Framework Core.

## License

MIT License - See LICENSE file for details.
