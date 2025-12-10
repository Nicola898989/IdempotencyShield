# Redis Store Examples

This directory contains example configurations for using IdempotencyShield with Redis.

## Running Locally with Docker

The easiest way to test Redis locally is using Docker:

```bash
# Start Redis
docker run -d --name redis-idempotency -p 6379:6379 redis:latest

# Or with password
docker run -d --name redis-idempotency -p 6379:6379 redis:latest --requirepass mypassword

# Stop Redis
docker stop redis-idempotency
docker rm redis-idempotency
```

## Testing Redis Connection

```bash
# Connect to Redis CLI
docker exec -it redis-idempotency redis-cli

# Test connection
127.0.0.1:6379> PING
PONG

# View idempotency keys
127.0.0.1:6379> KEYS idempotency:*

# Get a cached record
127.0.0.1:6379> GET idempotency:cache:your-key-here

# View lock keys (should be minimal/empty)
127.0.0.1:6379> KEYS idempotency:lock:*

# Monitor Redis operations in real-time
127.0.0.1:6379> MONITOR
```

## Production Setup

### Using Redis Cloud (AWS, Azure, GCP)

Most cloud providers offer managed Redis services:

- **AWS**: ElastiCache for Redis
- **Azure**: Azure Cache for Redis
- **GCP**: Memorystore for Redis

Configuration example:
```csharp
var redisOptions = new ConfigurationOptions
{
    EndPoints = { "your-instance.cache.azure.net:6380" },
    Password = "your-access-key",
    Ssl = true,
    AbortOnConnectFail = false
};

builder.Services.AddIdempotencyShieldWithRedis(redisOptions);
```

### Using Redis Sentinel (High Availability)

```csharp
var redisOptions = new ConfigurationOptions
{
    EndPoints = 
    { 
        "sentinel1:26379",
        "sentinel2:26379",
        "sentinel3:26379"
    },
    ServiceName = "mymaster", // Redis service name
    Password = "password",
    TieBreaker = "",
    CommandMap = CommandMap.Sentinel
};
```

## Monitoring

### Key Metrics to Monitor

1. **Total idempotency keys**: `DBSIZE` or count of `idempotency:*`
2. **Lock keys**: Should be minimal (< 10 typically)
3. **Memory usage**: Monitor Redis memory consumption
4. **Hit rate**: Track cache hits vs misses in your application
5. **Lock conflicts**: Count 409 responses

### Redis Monitoring Commands

```bash
# Get database statistics
redis-cli INFO stats

# Monitor memory
redis-cli INFO memory

# Count keys by pattern
redis-cli --scan --pattern "idempotency:cache:*" | wc -l
redis-cli --scan --pattern "idempotency:lock:*" | wc -l

# Check key expiry
redis-cli TTL "idempotency:cache:some-key"
```

## Performance Tuning

### Redis Configuration

```conf
# redis.conf optimizations for idempotency use case

# Memory
maxmemory 2gb
maxmemory-policy allkeys-lru  # Evict least recently used keys

# Persistence (optional - can disable for pure cache)
save ""  # Disable RDB snapshots for better performance
appendonly no  # Disable AOF for better performance

# Network
tcp-backlog 511
timeout 300
tcp-keepalive 60

# Max connections
maxclients 10000
```

### Connection Pooling

StackExchange.Redis automatically pools connections. Configure appropriately:

```csharp
var redisOptions = new ConfigurationOptions
{
    EndPoints = { "localhost:6379" },
    DefaultDatabase = 0,
    ConnectRetry = 3,
    ConnectTimeout = 5000,
    SyncTimeout = 1000,
    AsyncTimeout = 1000
};
```

## Troubleshooting

### Issue: "Connection timeout"

**Solution**: Check network connectivity, firewall rules, and Redis server status.

```bash
# Test connectivity
telnet redis-server 6379

# Check Redis logs
docker logs redis-idempotency
```

### Issue: "NOAUTH Authentication required"

**Solution**: Provide password in connection string:

```csharp
"localhost:6379,password=yourpassword"
```

### Issue: "Lock keys accumulating"

**Solution**: Lock keys should auto-expire after 30 seconds. If accumulating, processes may be crashing. Check application logs.

```bash
# Cleanup (use with caution in production)
redis-cli --scan --pattern "idempotency:lock:*" | xargs redis-cli DEL
```

### Issue: "High memory usage"

**Solution**: 
1. Reduce `ExpiryInMinutes` on `[Idempotent]` attribute
2. Set `maxmemory-policy` to `allkeys-lru`
3. Monitor and clean up old keys

```bash
# Check memory
redis-cli INFO memory | grep used_memory_human

# Flush specific pattern (DANGEROUS in production)
redis-cli --scan --pattern "idempotency:cache:old-*" | xargs redis-cli DEL
```
