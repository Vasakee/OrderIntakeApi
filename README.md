# Order Intake API

Order Intake API built with ASP.NET Core (.NET 8) and EF Core (SQLite). It validates incoming orders, saves them to the database, and processes payments through an external payment endpoint (`POST /payments`) with retry, timeout, circuit breaker, and idempotency handling.

## Getting Started

### Run the API
```bash
dotnet run
```
The server starts on `http://localhost:5080` (Swagger UI at `http://localhost:5080/swagger`).

For convenience, a simulated flaky `/payments` endpoint is included in the project so everything works with a single `dotnet run` (configured to fail ~30% of requests with either a `503` or a `5s` delay). There is also a standalone `FakePaymentService` project if you prefer running it separately on port `5081`:
```bash
dotnet run --project FakePaymentService
```

### Run Tests
```bash
dotnet test
```

## Endpoints

- `POST /api/orders` — Create and pay for an order (`201 Created` with `Location` header)
- `GET /api/orders/{id}` — Get order details and current payment status
- `GET /api/orders?customerId=...&page=1&pageSize=10` — Get paginated orders for a customer
- `GET /api/orders/all` — List all orders in the database
- `POST /api/orders/{id}/retry` — Retry payment for a failed order
- `GET /health` — Check database connectivity and circuit breaker status

## Design Decisions

- **Idempotency**: `IdempotencyKey` has a unique index in SQLite. We also store a SHA-256 hash of the request payload (`RequestHash`). Repeating a request for an already paid order returns the existing record without calling the payment service again. Reusing the same key with different order items returns `422 Unprocessable Entity`.
- **Concurrency**: Handled using an in-memory keyed lock (`KeyedIdempotencyLock`) for in-flight requests along with the DB unique constraint and an optimistic concurrency token (`Version`) on the `Order` entity. Simultaneous requests with the same key return `409 Conflict` so only one request runs.
- **Consistency**: Orders are saved as `Processing` before calling `/payments`. They are only updated to `Paid` after a `2xx` response from the payment service. If payment fails after retries, the status is set to `PaymentFailed`, allowing a subsequent retry with the same `idempotencyKey` to recover and complete the payment.
- **Resilience**: Implemented using a custom `DelegatingHandler` (`PaymentResilienceHandler`) on `HttpClient`:
  - **Timeout**: 1.5s timeout per attempt via a linked `CancellationTokenSource` to cut off 5s hangs.
  - **Retry**: Retries up to 3 times on `5xx`, `408`, `429`, network errors, and timeouts using exponential backoff + random jitter.
  - **Circuit Breaker**: Trips to `Open` for 15s after 5 consecutive failures, failing fast with `503` before allowing a `HalfOpen` probe request.
- **Observability**: `CorrelationIdMiddleware` extracts or generates `X-Correlation-ID`, attaches it to the log scope, returns it on the response, and forwards it to `/payments` via `CorrelationIdDelegatingHandler`.
