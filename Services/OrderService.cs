using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderIntakeApi.Data;
using OrderIntakeApi.Models;
using OrderIntakeApi.Resilience;

namespace OrderIntakeApi.Services;

public class OrderService : IOrderService
{
    private static readonly TimeSpan StaleProcessingTimeout = TimeSpan.FromSeconds(60);

    private readonly OrderDbContext _db;
    private readonly IPaymentClient _paymentClient;
    private readonly KeyedIdempotencyLock _idempotencyLock;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        OrderDbContext db,
        IPaymentClient paymentClient,
        KeyedIdempotencyLock idempotencyLock,
        ILogger<OrderService> logger)
    {
        _db = db;
        _paymentClient = paymentClient;
        _idempotencyLock = idempotencyLock;
        _logger = logger;
    }

    public async Task<OrderOperationResult> CreateOrderAsync(
        CreateOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return new OrderOperationResult
            {
                Outcome = OrderOperationOutcome.ValidationFailed,
                ValidationErrors = validationErrors
            };
        }

        var normalizedKey = request.IdempotencyKey.Trim();
        var requestHash = ComputeRequestHash(request);

        if (!_idempotencyLock.TryAcquire(normalizedKey, out var lease))
        {
            _logger.LogWarning(
                "Concurrent request rejected for in-flight IdempotencyKey {IdempotencyKey}",
                normalizedKey);

            return new OrderOperationResult
            {
                Outcome = OrderOperationOutcome.ConflictInFlight,
                ErrorMessage = $"A request with idempotencyKey '{normalizedKey}' is currently being processed."
            };
        }

        using (lease)
        {
            var existingOrder = await _db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.IdempotencyKey == normalizedKey, cancellationToken);

            Order orderToPay;

            if (existingOrder is not null)
            {
                if (!string.Equals(existingOrder.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "IdempotencyKey {IdempotencyKey} reused with different payload for Order {OrderId}",
                        normalizedKey,
                        existingOrder.Id);

                    return new OrderOperationResult
                    {
                        Outcome = OrderOperationOutcome.IdempotencyPayloadMismatch,
                        ErrorMessage = $"IdempotencyKey '{normalizedKey}' was already used with a different order payload."
                    };
                }

                if (existingOrder.PaymentStatus == PaymentStatus.Paid)
                {
                    _logger.LogInformation(
                        "Idempotent replay for Order {OrderId} (IdempotencyKey={IdempotencyKey})",
                        existingOrder.Id,
                        normalizedKey);

                    return new OrderOperationResult
                    {
                        Outcome = OrderOperationOutcome.IdempotentReplay,
                        Order = OrderResponse.FromEntity(existingOrder)
                    };
                }

                if (existingOrder.PaymentStatus == PaymentStatus.Processing &&
                    DateTime.UtcNow - existingOrder.UpdatedAtUtc < StaleProcessingTimeout)
                {
                    return new OrderOperationResult
                    {
                        Outcome = OrderOperationOutcome.ConflictInFlight,
                        ErrorMessage = $"Order '{existingOrder.Id}' with idempotencyKey '{normalizedKey}' is currently being processed."
                    };
                }

                _logger.LogInformation(
                    "Retrying Order {OrderId} from status {Status} (Attempt #{NextAttempt})",
                    existingOrder.Id,
                    existingOrder.PaymentStatus,
                    existingOrder.PaymentAttempts + 1);

                existingOrder.PaymentStatus = PaymentStatus.Processing;
                existingOrder.PaymentAttempts += 1;
                existingOrder.Version += 1;
                existingOrder.UpdatedAtUtc = DateTime.UtcNow;

                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return new OrderOperationResult
                    {
                        Outcome = OrderOperationOutcome.ConflictInFlight,
                        ErrorMessage = $"Concurrent state transition detected for idempotencyKey '{normalizedKey}'."
                    };
                }

                orderToPay = existingOrder;
            }
            else
            {
                var items = request.Items.Select(i => new OrderItem
                {
                    Sku = i.Sku.Trim(),
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice
                }).ToList();

                var totalAmount = items.Sum(i => i.Quantity * i.UnitPrice);

                var newOrder = new Order
                {
                    Id = Guid.NewGuid(),
                    CustomerId = request.CustomerId.Trim(),
                    IdempotencyKey = normalizedKey,
                    RequestHash = requestHash,
                    PaymentStatus = PaymentStatus.Processing,
                    TotalAmount = totalAmount,
                    PaymentAttempts = 1,
                    Version = 1,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Items = items
                };

                _db.Orders.Add(newOrder);

                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Unique constraint prevented duplicate order for IdempotencyKey {IdempotencyKey}",
                        normalizedKey);

                    return new OrderOperationResult
                    {
                        Outcome = OrderOperationOutcome.ConflictInFlight,
                        ErrorMessage = $"A concurrent order with idempotencyKey '{normalizedKey}' was already persisted."
                    };
                }

                _logger.LogInformation(
                    "Saved Order {OrderId} in Processing state for Customer {CustomerId} (Total={TotalAmount:F2})",
                    newOrder.Id,
                    newOrder.CustomerId,
                    newOrder.TotalAmount);

                orderToPay = newOrder;
            }

            return await ExecutePaymentTransitionAsync(orderToPay, cancellationToken);
        }
    }

    public async Task<OrderOperationResult> RetryOrderPaymentAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null)
        {
            return new OrderOperationResult
            {
                Outcome = OrderOperationOutcome.NotFound,
                ErrorMessage = $"Order '{orderId}' was not found."
            };
        }

        if (!_idempotencyLock.TryAcquire(order.IdempotencyKey, out var lease))
        {
            return new OrderOperationResult
            {
                Outcome = OrderOperationOutcome.ConflictInFlight,
                ErrorMessage = $"Order '{orderId}' is currently being processed."
            };
        }

        using (lease)
        {
            if (order.PaymentStatus == PaymentStatus.Paid)
            {
                return new OrderOperationResult
                {
                    Outcome = OrderOperationOutcome.IdempotentReplay,
                    Order = OrderResponse.FromEntity(order)
                };
            }

            order.PaymentStatus = PaymentStatus.Processing;
            order.PaymentAttempts += 1;
            order.Version += 1;
            order.UpdatedAtUtc = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new OrderOperationResult
                {
                    Outcome = OrderOperationOutcome.ConflictInFlight,
                    ErrorMessage = $"Concurrent retry detected for Order '{orderId}'."
                };
            }

            return await ExecutePaymentTransitionAsync(order, cancellationToken);
        }
    }

    public async Task<OrderResponse?> GetOrderByIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        return order is null ? null : OrderResponse.FromEntity(order);
    }

    public async Task<List<OrderResponse>> GetAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return orders.Select(OrderResponse.FromEntity).ToList();
    }

    public async Task<PagedOrdersResponse> GetOrdersAsync(
        string? customerId,
        int page = 1,
        int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var trimmedCustomer = customerId.Trim();
            query = query.Where(o => o.CustomerId == trimmedCustomer);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);

        var orders = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedOrdersResponse
        {
            CustomerId = customerId,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Items = orders.Select(OrderResponse.FromEntity).ToList()
        };
    }

    private async Task<OrderOperationResult> ExecutePaymentTransitionAsync(
        Order order,
        CancellationToken cancellationToken)
    {
        try
        {
            var paymentResult = await _paymentClient.ProcessPaymentAsync(
                new PaymentRequest
                {
                    OrderId = order.Id,
                    CustomerId = order.CustomerId,
                    Amount = order.TotalAmount,
                    Currency = "USD",
                    IdempotencyKey = order.IdempotencyKey
                },
                cancellationToken);

            order.PaymentStatus = PaymentStatus.Paid;
            order.PaymentReference = paymentResult.PaymentId;
            order.LastPaymentError = null;
            order.PaidAtUtc = paymentResult.ProcessedAtUtc;
            order.UpdatedAtUtc = DateTime.UtcNow;
            order.Version += 1;

            await _db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation(
                "Order {OrderId} marked as Paid (PaymentReference={PaymentReference})",
                order.Id,
                order.PaymentReference);

            return new OrderOperationResult
            {
                Outcome = OrderOperationOutcome.Created,
                Order = OrderResponse.FromEntity(order)
            };
        }
        catch (Exception ex)
        {
            var errorMessage = ex.Message;
            order.PaymentStatus = PaymentStatus.PaymentFailed;
            order.LastPaymentError = errorMessage.Length > 500 ? errorMessage[..500] : errorMessage;
            order.UpdatedAtUtc = DateTime.UtcNow;
            order.Version += 1;

            await _db.SaveChangesAsync(CancellationToken.None);

            var upstreamStatus = ex is PaymentServiceException pse ? pse.StatusCode ?? 502 : 502;

            _logger.LogError(
                ex,
                "Payment failed for Order {OrderId} on attempt #{Attempts}. Status set to PaymentFailed.",
                order.Id,
                order.PaymentAttempts);

            return new OrderOperationResult
            {
                Outcome = OrderOperationOutcome.PaymentFailed,
                Order = OrderResponse.FromEntity(order),
                ErrorMessage = ex.Message,
                UpstreamStatusCode = upstreamStatus
            };
        }
    }

    public static string ComputeRequestHash(CreateOrderRequest request)
    {
        var sb = new StringBuilder();
        sb.Append(request.CustomerId.Trim()).Append('|');

        foreach (var item in request.Items
                     .OrderBy(i => i.Sku.Trim(), StringComparer.Ordinal)
                     .ThenBy(i => i.UnitPrice)
                     .ThenBy(i => i.Quantity))
        {
            sb.Append(item.Sku.Trim())
              .Append(':')
              .Append(item.Quantity)
              .Append(':')
              .Append(item.UnitPrice.ToString("F4"))
              .Append(';');
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hashBytes);
    }
}
