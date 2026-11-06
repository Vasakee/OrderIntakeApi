using OrderIntakeApi.Models;
using OrderIntakeApi.Services;

namespace OrderIntakeApi.Tests;

public class OrderValidationTests
{
    [Fact]
    public void Validate_ValidRequest_ReturnsNoErrors()
    {
        var request = new CreateOrderRequest
        {
            CustomerId = "cust-123",
            IdempotencyKey = "idem-001",
            Items =
            [
                new OrderItemRequest { Sku = "SKU-1", Quantity = 2, UnitPrice = 9.99m }
            ]
        };

        var errors = request.Validate();

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("", "idem-1")]
    [InlineData("   ", "idem-1")]
    [InlineData("cust-1", "")]
    [InlineData("cust-1", "   ")]
    public void Validate_MissingCustomerOrIdempotencyKey_ReturnsErrors(string customerId, string idempotencyKey)
    {
        var request = new CreateOrderRequest
        {
            CustomerId = customerId,
            IdempotencyKey = idempotencyKey,
            Items = [new OrderItemRequest { Sku = "SKU-1", Quantity = 1, UnitPrice = 5.00m }]
        };

        var errors = request.Validate();

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_EmptyItems_ReturnsError()
    {
        var request = new CreateOrderRequest
        {
            CustomerId = "cust-123",
            IdempotencyKey = "idem-001",
            Items = []
        };

        var errors = request.Validate();

        Assert.True(errors.ContainsKey("items"));
    }

    [Theory]
    [InlineData("", 1, 10.0)]
    [InlineData("SKU-1", 0, 10.0)]
    [InlineData("SKU-1", -3, 10.0)]
    [InlineData("SKU-1", 1, 0.0)]
    [InlineData("SKU-1", 1, -5.99)]
    public void Validate_InvalidItemFields_ReturnsFieldErrors(string sku, int quantity, double unitPrice)
    {
        var request = new CreateOrderRequest
        {
            CustomerId = "cust-123",
            IdempotencyKey = "idem-001",
            Items =
            [
                new OrderItemRequest
                {
                    Sku = sku,
                    Quantity = quantity,
                    UnitPrice = (decimal)unitPrice
                }
            ]
        };

        var errors = request.Validate();

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ComputeRequestHash_SamePayloadDifferentItemOrder_ProducesSameHash()
    {
        var req1 = new CreateOrderRequest
        {
            CustomerId = "cust-1",
            IdempotencyKey = "key-1",
            Items =
            [
                new OrderItemRequest { Sku = "B", Quantity = 1, UnitPrice = 10m },
                new OrderItemRequest { Sku = "A", Quantity = 2, UnitPrice = 5m }
            ]
        };

        var req2 = new CreateOrderRequest
        {
            CustomerId = "cust-1",
            IdempotencyKey = "key-1",
            Items =
            [
                new OrderItemRequest { Sku = "A", Quantity = 2, UnitPrice = 5m },
                new OrderItemRequest { Sku = "B", Quantity = 1, UnitPrice = 10m }
            ]
        };

        var reqDifferent = new CreateOrderRequest
        {
            CustomerId = "cust-1",
            IdempotencyKey = "key-1",
            Items =
            [
                new OrderItemRequest { Sku = "A", Quantity = 3, UnitPrice = 5m },
                new OrderItemRequest { Sku = "B", Quantity = 1, UnitPrice = 10m }
            ]
        };

        Assert.Equal(OrderService.ComputeRequestHash(req1), OrderService.ComputeRequestHash(req2));
        Assert.NotEqual(OrderService.ComputeRequestHash(req1), OrderService.ComputeRequestHash(reqDifferent));
    }
}
