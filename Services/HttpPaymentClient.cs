using System.Net.Http.Json;
using OrderIntakeApi.Models;
using OrderIntakeApi.Resilience;

namespace OrderIntakeApi.Services;

public class HttpPaymentClient : IPaymentClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpPaymentClient> _logger;

    public HttpPaymentClient(HttpClient httpClient, ILogger<HttpPaymentClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(
        PaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);

        _logger.LogInformation(
            "Calling external payment service POST /payments for Order {OrderId} (Amount={Amount:F2}, IdempotencyKey={IdempotencyKey})",
            request.OrderId,
            request.Amount,
            request.IdempotencyKey);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = (int)response.StatusCode;
            throw new PaymentServiceException(
                $"External payment service returned HTTP {statusCode} ({response.StatusCode}). Body: {errorBody}",
                statusCode);
        }

        var paymentResponse = await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken: cancellationToken);
        if (paymentResponse is null || string.IsNullOrWhiteSpace(paymentResponse.PaymentId))
        {
            throw new PaymentServiceException("External payment service returned an empty or invalid payment payload.", 502);
        }

        _logger.LogInformation(
            "Payment succeeded for Order {OrderId} with PaymentId {PaymentId}",
            request.OrderId,
            paymentResponse.PaymentId);

        return paymentResponse;
    }
}
