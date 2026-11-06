using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OrderIntakeApi.Models;

namespace OrderIntakeApi.Controllers;

public class FakePaymentOptions
{
    public const string SectionName = "FakePaymentService";

    public double FailureRate { get; set; } = 0.30;
    public int HangDurationMs { get; set; } = 5000;
}

[ApiController]
[Route("payments")]
public class FakePaymentsController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, PaymentResponse> ProcessedPayments = new();

    private readonly FakePaymentOptions _options;
    private readonly ILogger<FakePaymentsController> _logger;

    public FakePaymentsController(
        IOptions<FakePaymentOptions> options,
        ILogger<FakePaymentsController> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> ProcessPayment(
        [FromBody] PaymentRequest request,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? request.IdempotencyKey;

        if (!string.IsNullOrWhiteSpace(idempotencyKey) &&
            ProcessedPayments.TryGetValue(idempotencyKey, out var existingReceipt))
        {
            _logger.LogInformation(
                "[FakePaymentService] Returning existing receipt {PaymentId} for Order {OrderId}",
                existingReceipt.PaymentId,
                request.OrderId);
            return Ok(existingReceipt);
        }

        var roll = Random.Shared.NextDouble();
        var halfFailureRate = _options.FailureRate / 2.0;

        if (roll < halfFailureRate)
        {
            _logger.LogWarning(
                "[FakePaymentService] Simulated 503 for Order {OrderId}",
                request.OrderId);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Simulated transient 503 Service Unavailable."
            });
        }

        if (roll < _options.FailureRate)
        {
            _logger.LogWarning(
                "[FakePaymentService] Simulated {HangMs}ms delay for Order {OrderId}",
                _options.HangDurationMs,
                request.OrderId);

            await Task.Delay(_options.HangDurationMs, cancellationToken);
        }

        var receipt = new PaymentResponse
        {
            PaymentId = $"pay_{Guid.NewGuid():N}",
            Status = "Succeeded",
            ProcessedAtUtc = DateTime.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            ProcessedPayments.TryAdd(idempotencyKey, receipt);
        }

        _logger.LogInformation(
            "[FakePaymentService] Charged {Amount:F2} {Currency} for Order {OrderId} -> {PaymentId}",
            request.Amount,
            request.Currency,
            request.OrderId,
            receipt.PaymentId);

        return Ok(receipt);
    }
}
