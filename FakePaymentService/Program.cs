using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var processedPayments = new ConcurrentDictionary<string, object>();
var failureRate = builder.Configuration.GetValue<double?>("FailureRate") ?? 0.30;
var hangDurationMs = builder.Configuration.GetValue<int?>("HangDurationMs") ?? 5000;

app.MapPost("/payments", async (HttpContext httpContext, CancellationToken ct) =>
{
    var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

    var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
        processedPayments.TryGetValue(idempotencyKey, out var cachedReceipt))
    {
        return Results.Ok(cachedReceipt);
    }

    var roll = Random.Shared.NextDouble();

    if (roll < failureRate / 2.0)
    {
        return Results.Json(
            new { error = "Simulated transient 503 Service Unavailable." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (roll < failureRate)
    {
        await Task.Delay(hangDurationMs, ct);
    }

    var receipt = new
    {
        PaymentId = $"pay_{Guid.NewGuid():N}",
        Status = "Succeeded",
        ProcessedAtUtc = DateTime.UtcNow
    };

    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        processedPayments.TryAdd(idempotencyKey, receipt);
    }

    return Results.Ok(receipt);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "FakePaymentService" }));

app.Run("http://localhost:5081");
