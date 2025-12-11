using IdempotencyShield.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add IdempotencyShield with custom configuration
builder.Services.AddIdempotencyShield(options =>
{
    options.HeaderName = "Idempotency-Key";
    options.DefaultExpiryMinutes = 60;
    options.LockWaitTimeoutMilliseconds = 0;
});

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
