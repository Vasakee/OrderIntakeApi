using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OrderIntakeApi.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentStatus
{
    Processing = 0,
    Paid = 1,
    PaymentFailed = 2
}

public class Order
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string CustomerId { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    // Hash of normalized request payload to detect key reuse with different items
    [Required]
    [MaxLength(64)]
    public string RequestHash { get; set; } = string.Empty;

    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Processing;

    public decimal TotalAmount { get; set; }

    [MaxLength(128)]
    public string? PaymentReference { get; set; }

    [MaxLength(512)]
    public string? LastPaymentError { get; set; }

    public int PaymentAttempts { get; set; }

    [ConcurrencyCheck]
    public int Version { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAtUtc { get; set; }

    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrderId { get; set; }

    [Required]
    [MaxLength(128)]
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    [JsonIgnore]
    public Order? Order { get; set; }
}
