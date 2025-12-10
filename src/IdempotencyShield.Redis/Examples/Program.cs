using IdempotencyShield.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ===== OPTION 1: Simple Redis Configuration =====
// Use this for basic setups with a single Redis instance
builder.Services.AddIdempotencyShieldWithRedis(
    redisConfiguration: "localhost:6379",
    configureOptions: options =>
    {
        options.HeaderName = "Idempotency-Key";
        options.DefaultExpiryMinutes = 60;
    });

/* ===== OPTION 2: Production Redis Configuration =====
// Use this for production with authentication, SSL, and resilience
using StackExchange.Redis;

var redisOptions = new ConfigurationOptions
{
    EndPoints = { "your-redis-server.com:6379" },
    Password = "your-redis-password",
    Ssl = true,
    AbortOnConnectFail = false, // Don't crash if Redis is temporarily unavailable
    ConnectRetry = 3,
    ConnectTimeout = 5000,
    SyncTimeout = 3000,
    AsyncTimeout = 3000,
    KeepAlive = 60,
    ReconnectRetryPolicy = new ExponentialRetry(5000)
};

builder.Services.AddIdempotencyShieldWithRedis(redisOptions, options =>
{
    options.HeaderName = "Idempotency-Key";
    options.DefaultExpiryMinutes = 120;
});
*/

/* ===== OPTION 3: Redis Cluster Configuration =====
// Use this for high availability with Redis Cluster or Sentinel
using StackExchange.Redis;

var redisOptions = new ConfigurationOptions
{
    EndPoints = 
    { 
        "redis-node1.example.com:6379",
        "redis-node2.example.com:6379", 
        "redis-node3.example.com:6379"
    },
    Password = "cluster-password",
    Ssl = true,
    AbortOnConnectFail = false
};

builder.Services.AddIdempotencyShieldWithRedis(redisOptions);
*/

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// IMPORTANT: UseIdempotencyShield MUST be called before UseRouting
app.UseIdempotencyShield();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
