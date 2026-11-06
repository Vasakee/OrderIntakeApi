using OrderIntakeApi.Models;

namespace OrderIntakeApi.Services;

public enum OrderOperationOutcome
{
    Created,
    IdempotentReplay,
    ValidationFailed,
    ConflictInFlight,
    IdempotencyPayloadMismatch,
    PaymentFailed,
    NotFound
}

public record OrderOperationResult
{
    public OrderOperationOutcome Outcome { get; init; }
    public OrderResponse? Order { get; init; }
    public Dictionary<string, string[]>? ValidationErrors { get; init; }
    public string? ErrorMessage { get; init; }
    public int? UpstreamStatusCode { get; init; }
}

public interface IOrderService
{
    Task<OrderOperationResult> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderOperationResult> RetryOrderPaymentAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<OrderResponse?> GetOrderByIdAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<List<OrderResponse>> GetAllOrdersAsync(CancellationToken cancellationToken = default);
    Task<PagedOrdersResponse> GetOrdersAsync(string? customerId, int page = 1, int pageSize = 10, CancellationToken cancellationToken = default);
}
