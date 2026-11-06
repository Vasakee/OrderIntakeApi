namespace OrderIntakeApi.Models;

public record CreateOrderRequest
{
    public string CustomerId { get; init; } = string.Empty;
    public List<OrderItemRequest> Items { get; init; } = new();
    public string IdempotencyKey { get; init; } = string.Empty;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, List<string>>();

        void AddError(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                list = new List<string>();
                errors[field] = list;
            }
            list.Add(message);
        }

        if (string.IsNullOrWhiteSpace(CustomerId))
        {
            AddError("customerId", "CustomerId is required and cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(IdempotencyKey))
        {
            AddError("idempotencyKey", "IdempotencyKey is required and cannot be empty.");
        }

        if (Items is null || Items.Count == 0)
        {
            AddError("items", "Order must contain at least one item.");
        }
        else
        {
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                if (item is null)
                {
                    AddError($"items[{i}]", "Item cannot be null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Sku))
                {
                    AddError($"items[{i}].sku", "Item SKU is required and cannot be empty.");
                }

                if (item.Quantity <= 0)
                {
                    AddError($"items[{i}].quantity", "Item quantity must be greater than zero.");
                }

                if (item.UnitPrice <= 0)
                {
                    AddError($"items[{i}].unitPrice", "Item unitPrice must be greater than zero.");
                }
            }
        }

        return errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }
}

public record OrderItemRequest
{
    public string Sku { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

public record OrderResponse
{
    public Guid Id { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public PaymentStatus PaymentStatus { get; init; }
    public decimal TotalAmount { get; init; }
    public string? PaymentReference { get; init; }
    public string? LastPaymentError { get; init; }
    public int PaymentAttempts { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public DateTime? PaidAtUtc { get; init; }
    public List<OrderItemResponse> Items { get; init; } = new();

    public static OrderResponse FromEntity(Order order) => new()
    {
        Id = order.Id,
        CustomerId = order.CustomerId,
        IdempotencyKey = order.IdempotencyKey,
        PaymentStatus = order.PaymentStatus,
        TotalAmount = order.TotalAmount,
        PaymentReference = order.PaymentReference,
        LastPaymentError = order.LastPaymentError,
        PaymentAttempts = order.PaymentAttempts,
        CreatedAtUtc = order.CreatedAtUtc,
        UpdatedAtUtc = order.UpdatedAtUtc,
        PaidAtUtc = order.PaidAtUtc,
        Items = order.Items.Select(i => new OrderItemResponse
        {
            Sku = i.Sku,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            LineTotal = i.Quantity * i.UnitPrice
        }).ToList()
    };
}

public record OrderItemResponse
{
    public string Sku { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineTotal { get; init; }
}

public record PagedOrdersResponse
{
    public string? CustomerId { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public List<OrderResponse> Items { get; init; } = new();
}

public record PaymentRequest
{
    public Guid OrderId { get; init; }
    public string CustomerId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";
    public string IdempotencyKey { get; init; } = string.Empty;
}

public record PaymentResponse
{
    public string PaymentId { get; init; } = string.Empty;
    public string Status { get; init; } = "Succeeded";
    public DateTime ProcessedAtUtc { get; init; } = DateTime.UtcNow;
}
