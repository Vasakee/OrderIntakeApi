using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderIntakeApi.Controllers;
using OrderIntakeApi.Data;
using OrderIntakeApi.Models;
using OrderIntakeApi.Observability;
using OrderIntakeApi.Resilience;
using OrderIntakeApi.Services;

namespace OrderIntakeApi.Tests;

public class OrderIntegrationTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly DbContextOptions<OrderDbContext> _dbOptions;
    private readonly KeyedIdempotencyLock _idempotencyLock = new();
    private readonly CorrelationContext _correlationContext = new();

    public OrderIntegrationTests()
    {
        var dbName = $"OrderIntakeTest_{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        _keepAliveConnection = new SqliteConnection(connectionString);
        _keepAliveConnection.Open();

        _dbOptions = new DbContextOptionsBuilder<OrderDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var db = new OrderDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task PostOrder_HappyPath_PersistsOrderCallsPaymentAndReturns201WithLocation()
    {
        var paymentCalls = 0;
        string? capturedCorrelationId = null;

        var controller = CreateController((req, ct) =>
        {
            paymentCalls++;
            capturedCorrelationId = req.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var vals)
                ? vals.FirstOrDefault()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse
                {
                    PaymentId = "pay_happy_001",
                    Status = "Succeeded"
                })
            });
        });

        _correlationContext.CorrelationId = "corr-happy-test-123";

        var createResult = await controller.CreateOrder(new CreateOrderRequest
        {
            CustomerId = "customer-42",
            IdempotencyKey = "idem-happy-1",
            Items =
            [
                new OrderItemRequest { Sku = "WIDGET-A", Quantity = 2, UnitPrice = 15.50m },
                new OrderItemRequest { Sku = "GADGET-B", Quantity = 1, UnitPrice = 9.00m }
            ]
        }, CancellationToken.None);

        var createdAt = Assert.IsType<CreatedAtActionResult>(createResult);
        Assert.Equal(StatusCodes.Status201Created, createdAt.StatusCode);
        Assert.Equal(nameof(OrdersController.GetOrderById), createdAt.ActionName);

        var orderResponse = Assert.IsType<OrderResponse>(createdAt.Value);
        Assert.Equal(PaymentStatus.Paid, orderResponse.PaymentStatus);
        Assert.Equal(40.00m, orderResponse.TotalAmount);
        Assert.Equal("pay_happy_001", orderResponse.PaymentReference);
        Assert.Equal(1, paymentCalls);
        Assert.Equal("corr-happy-test-123", capturedCorrelationId);

        var getResult = await controller.GetOrderById(orderResponse.Id, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(getResult);
        var fetchedOrder = Assert.IsType<OrderResponse>(okResult.Value);
        Assert.Equal(orderResponse.Id, fetchedOrder.Id);
        Assert.Equal(PaymentStatus.Paid, fetchedOrder.PaymentStatus);
    }

    [Fact]
    public async Task PostOrder_FailurePath_NeverMarksOrderPaid_AndAllowsRecoveryOnRetry()
    {
        var paymentServiceHealthy = false;
        var totalDownstreamAttempts = 0;

        var controller = CreateController((req, ct) =>
        {
            Interlocked.Increment(ref totalDownstreamAttempts);

            if (!paymentServiceHealthy)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Downstream payment gateway outage")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse
                {
                    PaymentId = "pay_recovered_999",
                    Status = "Succeeded"
                })
            });
        }, new ResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMs = 5,
            MaxJitterMs = 2,
            AttemptTimeoutMs = 200,
            CircuitBreakerFailureThreshold = 10
        });

        var request = new CreateOrderRequest
        {
            CustomerId = "customer-fail-recover",
            IdempotencyKey = "idem-recover-key-1",
            Items = [new OrderItemRequest { Sku = "ITEM-X", Quantity = 1, UnitPrice = 99.95m }]
        };

        var failedResult = await controller.CreateOrder(request, CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(failedResult);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        Assert.Equal(3, totalDownstreamAttempts);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        var orderId = Assert.IsType<Guid>(problem.Extensions["orderId"]);

        var getAfterFail = await controller.GetOrderById(orderId, CancellationToken.None);
        var failedOrder = Assert.IsType<OrderResponse>(Assert.IsType<OkObjectResult>(getAfterFail).Value);
        Assert.Equal(PaymentStatus.PaymentFailed, failedOrder.PaymentStatus);
        Assert.Null(failedOrder.PaymentReference);
        Assert.NotNull(failedOrder.LastPaymentError);

        paymentServiceHealthy = true;
        var retryResult = await controller.CreateOrder(request, CancellationToken.None);

        var recoveredCreated = Assert.IsType<CreatedAtActionResult>(retryResult);
        var recoveredOrder = Assert.IsType<OrderResponse>(recoveredCreated.Value);

        Assert.Equal(orderId, recoveredOrder.Id);
        Assert.Equal(PaymentStatus.Paid, recoveredOrder.PaymentStatus);
        Assert.Equal("pay_recovered_999", recoveredOrder.PaymentReference);
        Assert.Equal(2, recoveredOrder.PaymentAttempts);

        await using var verifyDb = new OrderDbContext(_dbOptions);
        Assert.Equal(1, await verifyDb.Orders.CountAsync());
    }

    [Fact]
    public async Task PostOrder_Idempotency_RepeatingRequestDoesNotDuplicateOrDoubleCharge()
    {
        var paymentCalls = 0;

        var controller = CreateController((req, ct) =>
        {
            Interlocked.Increment(ref paymentCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse { PaymentId = $"pay_{paymentCalls}" })
            });
        });

        var request = new CreateOrderRequest
        {
            CustomerId = "cust-idem",
            IdempotencyKey = "idem-duplicate-test",
            Items = [new OrderItemRequest { Sku = "BOOK-1", Quantity = 1, UnitPrice = 25.00m }]
        };

        var firstResult = Assert.IsType<CreatedAtActionResult>(
            await controller.CreateOrder(request, CancellationToken.None));
        var firstOrder = Assert.IsType<OrderResponse>(firstResult.Value);

        var secondResult = Assert.IsType<CreatedAtActionResult>(
            await controller.CreateOrder(request, CancellationToken.None));
        var secondOrder = Assert.IsType<OrderResponse>(secondResult.Value);

        Assert.Equal(firstOrder.Id, secondOrder.Id);
        Assert.Equal(firstOrder.PaymentReference, secondOrder.PaymentReference);
        Assert.Equal(1, paymentCalls);
        Assert.Equal("true", controller.Response.Headers["X-Idempotent-Replay"].ToString());

        var tamperedRequest = request with
        {
            Items = [new OrderItemRequest { Sku = "BOOK-1", Quantity = 99, UnitPrice = 25.00m }]
        };
        var mismatchResult = await controller.CreateOrder(tamperedRequest, CancellationToken.None);
        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(mismatchResult);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);

        await using var verifyDb = new OrderDbContext(_dbOptions);
        Assert.Equal(1, await verifyDb.Orders.CountAsync());
    }

    [Fact]
    public async Task PostOrder_ConcurrencySafety_SimultaneousRequestsWithSameKey_OnlyOneSucceeds()
    {
        var paymentCalls = 0;

        HttpMessageHandler HandlerFactory() => new DelegateHttpMessageHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref paymentCalls);
            await Task.Delay(80, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse { PaymentId = "pay_concurrent_winner" })
            };
        });

        var request = new CreateOrderRequest
        {
            CustomerId = "cust-race",
            IdempotencyKey = "idem-race-key-001",
            Items = [new OrderItemRequest { Sku = "LAPTOP-PRO", Quantity = 1, UnitPrice = 1299.00m }]
        };

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            await startGate.Task;
            var controller = CreateControllerWithHandler(HandlerFactory());
            return await controller.CreateOrder(request, CancellationToken.None);
        }).ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        var createdResults = results.OfType<CreatedAtActionResult>().ToList();
        var conflictResults = results.OfType<ConflictObjectResult>().ToList();

        Assert.Single(createdResults);
        Assert.Equal(4, conflictResults.Count);
        Assert.Equal(1, paymentCalls);

        await using var verifyDb = new OrderDbContext(_dbOptions);
        Assert.Equal(1, await verifyDb.Orders.CountAsync());
    }

    [Fact]
    public async Task GetOrders_FiltersByCustomerAndPaginatesCorrectly()
    {
        var controller = CreateController((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse { PaymentId = $"pay_{Guid.NewGuid():N}" })
            }));

        for (var i = 1; i <= 3; i++)
        {
            await controller.CreateOrder(new CreateOrderRequest
            {
                CustomerId = "cust-paginated",
                IdempotencyKey = $"idem-page-{i}",
                Items = [new OrderItemRequest { Sku = $"SKU-{i}", Quantity = i, UnitPrice = 10m }]
            }, CancellationToken.None);
        }

        await controller.CreateOrder(new CreateOrderRequest
        {
            CustomerId = "other-customer",
            IdempotencyKey = "idem-other-1",
            Items = [new OrderItemRequest { Sku = "SKU-OTHER", Quantity = 1, UnitPrice = 5m }]
        }, CancellationToken.None);

        var page1Result = Assert.IsType<OkObjectResult>(
            await controller.GetOrders("cust-paginated", page: 1, pageSize: 2, CancellationToken.None));
        var page1 = Assert.IsType<PagedOrdersResponse>(page1Result.Value);

        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);
        Assert.All(page1.Items, o => Assert.Equal("cust-paginated", o.CustomerId));

        var page2Result = Assert.IsType<OkObjectResult>(
            await controller.GetOrders("cust-paginated", page: 2, pageSize: 2, CancellationToken.None));
        var page2 = Assert.IsType<PagedOrdersResponse>(page2Result.Value);

        Assert.Single(page2.Items);
    }

    private OrdersController CreateController(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc,
        ResilienceOptions? resilienceOptions = null)
    {
        return CreateControllerWithHandler(
            new DelegateHttpMessageHandler(handlerFunc),
            resilienceOptions);
    }

    private OrdersController CreateControllerWithHandler(
        HttpMessageHandler innerHandler,
        ResilienceOptions? resilienceOptions = null)
    {
        var options = Options.Create(resilienceOptions ?? new ResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMs = 5,
            MaxJitterMs = 2,
            AttemptTimeoutMs = 200,
            CircuitBreakerFailureThreshold = 5
        });

        var circuitBreaker = new CircuitBreakerStateMachine(
            options,
            NullLogger<CircuitBreakerStateMachine>.Instance);

        var resilienceHandler = new PaymentResilienceHandler(
            options,
            circuitBreaker,
            NullLogger<PaymentResilienceHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var correlationHandler = new CorrelationIdDelegatingHandler(_correlationContext)
        {
            InnerHandler = resilienceHandler
        };

        var httpClient = new HttpClient(correlationHandler)
        {
            BaseAddress = new Uri("http://localhost:5080")
        };

        var paymentClient = new HttpPaymentClient(httpClient, NullLogger<HttpPaymentClient>.Instance);
        var db = new OrderDbContext(_dbOptions);
        var orderService = new OrderService(db, paymentClient, _idempotencyLock, NullLogger<OrderService>.Instance);

        return new OrdersController(orderService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    public void Dispose()
    {
        _keepAliveConnection.Dispose();
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _send(request, cancellationToken);
    }
}
