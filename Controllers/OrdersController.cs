using Microsoft.AspNetCore.Mvc;
using OrderIntakeApi.Models;
using OrderIntakeApi.Services;

namespace OrderIntakeApi.Controllers;

[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateOrderRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid Request Body",
                Detail = "Order payload is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Allow passing Idempotency-Key via HTTP header as well
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
            Request.Headers.TryGetValue("Idempotency-Key", out var headerKey) &&
            !string.IsNullOrWhiteSpace(headerKey))
        {
            request = request with { IdempotencyKey = headerKey.ToString() };
        }

        var result = await _orderService.CreateOrderAsync(request, cancellationToken);
        return MapOperationResult(result);
    }

    [HttpGet("{id:guid}", Name = nameof(GetOrderById))]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderByIdAsync(id, cancellationToken);
        if (order is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Order Not Found",
                Detail = $"Order with ID '{id}' was not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(order);
    }

    [HttpGet("all")]
    public async Task<IActionResult> GetAllOrders(CancellationToken cancellationToken)
    {
        var orders = await _orderService.GetAllOrdersAsync(cancellationToken);
        return Ok(orders);
    }

    [HttpGet]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string? customerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var pagedOrders = await _orderService.GetOrdersAsync(customerId, page, pageSize, cancellationToken);
        return Ok(pagedOrders);
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> RetryOrderPayment(Guid id, CancellationToken cancellationToken)
    {
        var result = await _orderService.RetryOrderPaymentAsync(id, cancellationToken);
        return MapOperationResult(result);
    }

    private IActionResult MapOperationResult(OrderOperationResult result)
    {
        switch (result.Outcome)
        {
            case OrderOperationOutcome.ValidationFailed:
                return BadRequest(new ValidationProblemDetails(result.ValidationErrors!)
                {
                    Title = "Order Validation Failed",
                    Status = StatusCodes.Status400BadRequest
                });

            case OrderOperationOutcome.Created:
                return CreatedAtAction(
                    nameof(GetOrderById),
                    new { id = result.Order!.Id },
                    result.Order);

            case OrderOperationOutcome.IdempotentReplay:
                Response.Headers["X-Idempotent-Replay"] = "true";
                return CreatedAtAction(
                    nameof(GetOrderById),
                    new { id = result.Order!.Id },
                    result.Order);

            case OrderOperationOutcome.ConflictInFlight:
                return Conflict(new ProblemDetails
                {
                    Title = "Concurrent Order Processing Conflict",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status409Conflict
                });

            case OrderOperationOutcome.IdempotencyPayloadMismatch:
                return UnprocessableEntity(new ProblemDetails
                {
                    Title = "Idempotency Key Payload Mismatch",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status422UnprocessableEntity
                });

            case OrderOperationOutcome.NotFound:
                return NotFound(new ProblemDetails
                {
                    Title = "Order Not Found",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status404NotFound
                });

            case OrderOperationOutcome.PaymentFailed:
            {
                var statusCode = result.UpstreamStatusCode == StatusCodes.Status503ServiceUnavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status502BadGateway;

                var problem = new ProblemDetails
                {
                    Title = "Payment Processing Failed",
                    Detail = result.ErrorMessage ?? "Payment failed. Order was saved as PaymentFailed and can be retried.",
                    Status = statusCode,
                    Instance = result.Order is not null ? $"/api/orders/{result.Order.Id}" : null
                };

                if (result.Order is not null)
                {
                    problem.Extensions["orderId"] = result.Order.Id;
                    problem.Extensions["paymentStatus"] = result.Order.PaymentStatus.ToString();
                    problem.Extensions["idempotencyKey"] = result.Order.IdempotencyKey;
                    problem.Extensions["paymentAttempts"] = result.Order.PaymentAttempts;
                    problem.Extensions["retryable"] = true;
                }

                return StatusCode(statusCode, problem);
            }

            default:
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
