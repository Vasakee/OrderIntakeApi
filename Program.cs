using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OrderIntakeApi;
using OrderIntakeApi.Controllers;
using OrderIntakeApi.Data;
using OrderIntakeApi.Observability;
using OrderIntakeApi.Resilience;
using OrderIntakeApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = true;
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});

builder.Services.Configure<ResilienceOptions>(
    builder.Configuration.GetSection(ResilienceOptions.SectionName));
builder.Services.Configure<FakePaymentOptions>(
    builder.Configuration.GetSection(FakePaymentOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=orders.db";
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddSingleton<KeyedIdempotencyLock>();
builder.Services.AddSingleton<CircuitBreakerStateMachine>();

builder.Services.AddTransient<CorrelationIdDelegatingHandler>();
builder.Services.AddTransient<PaymentResilienceHandler>();

builder.Services.AddHttpClient<IPaymentClient, HttpPaymentClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var configuredBaseUrl = config["PaymentService:BaseUrl"];

    if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
    {
        client.BaseAddress = new Uri(configuredBaseUrl);
        return;
    }

    var server = sp.GetService<IServer>();
    var addressFeature = server?.Features.Get<IServerAddressesFeature>();
    var listeningAddress = addressFeature?.Addresses.FirstOrDefault() ?? "http://localhost:5080";
    var normalizedAddress = listeningAddress
        .Replace("0.0.0.0", "127.0.0.1")
        .Replace("[::]", "localhost")
        .Replace("+", "localhost")
        .Replace("*", "localhost");

    client.BaseAddress = new Uri(normalizedAddress);
})
.AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
.AddHttpMessageHandler<PaymentResilienceHandler>();

builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Intake API",
        Version = "v1",
        Description = "Resilient, observable, and concurrency-safe Order Intake API (.NET 8)"
    });
    options.SchemaFilter<SwaggerExampleSchemaFilter>();
    options.OperationFilter<SwaggerExampleOperationFilter>();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.EnsureCreated();
}

app.UseMiddleware<CorrelationIdMiddleware>();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Intake API v1");
    c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
    c.DisplayRequestDuration();
});

app.MapGet("/health", async (
    OrderDbContext db,
    CircuitBreakerStateMachine circuitBreaker,
    ICorrelationContext correlationContext,
    CancellationToken ct) =>
{
    var dbHealthy = await db.Database.CanConnectAsync(ct);
    var breakerSnapshot = circuitBreaker.GetSnapshot();

    var overallStatus = !dbHealthy
        ? "Unhealthy"
        : breakerSnapshot.State == CircuitState.Open.ToString()
            ? "Degraded"
            : "Healthy";

    var statusCode = dbHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;

    return Results.Json(new
    {
        status = overallStatus,
        timestampUtc = DateTime.UtcNow,
        correlationId = correlationContext.CorrelationId,
        checks = new
        {
            database = dbHealthy ? "Healthy" : "Unhealthy",
            paymentCircuitBreaker = breakerSnapshot
        }
    }, statusCode: statusCode);
});

app.MapControllers();

app.Run();

public partial class Program { }
