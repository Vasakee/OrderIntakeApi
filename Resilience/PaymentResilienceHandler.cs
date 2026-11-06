using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderIntakeApi.Resilience;

public class PaymentResilienceHandler : DelegatingHandler
{
    private readonly ResilienceOptions _options;
    private readonly CircuitBreakerStateMachine _circuitBreaker;
    private readonly ILogger<PaymentResilienceHandler> _logger;

    public PaymentResilienceHandler(
        IOptions<ResilienceOptions> options,
        CircuitBreakerStateMachine circuitBreaker,
        ILogger<PaymentResilienceHandler> logger)
    {
        _options = options.Value;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, _options.MaxRetries + 1);
        var attemptTimeout = TimeSpan.FromMilliseconds(Math.Max(50, _options.AttemptTimeoutMs));
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _circuitBreaker.BeforeExecution();

            using var attemptRequest = await CloneHttpRequestMessageAsync(request, cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(attemptTimeout);

            try
            {
                var response = await base.SendAsync(attemptRequest, timeoutCts.Token);

                if (IsTransientStatusCode(response.StatusCode))
                {
                    var statusReason = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
                    _circuitBreaker.OnFailure(statusReason);

                    if (attempt < maxAttempts)
                    {
                        response.Dispose();
                        await WaitBackoffAsync(attempt, statusReason, cancellationToken);
                        continue;
                    }

                    return response;
                }

                _circuitBreaker.OnSuccess();
                return response;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                var timeoutEx = new PaymentTimeoutException(attemptTimeout, ex);
                lastException = timeoutEx;
                _circuitBreaker.OnFailure($"Timeout after {attemptTimeout.TotalMilliseconds:F0}ms");

                if (attempt < maxAttempts)
                {
                    await WaitBackoffAsync(attempt, timeoutEx.Message, cancellationToken);
                    continue;
                }

                throw timeoutEx;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _circuitBreaker.OnFailure($"HttpRequestException: {ex.Message}");

                if (attempt < maxAttempts)
                {
                    await WaitBackoffAsync(attempt, ex.Message, cancellationToken);
                    continue;
                }

                throw new PaymentServiceException($"Payment service network error after {attempt} attempts: {ex.Message}", 502, ex);
            }
        }

        throw lastException ?? new PaymentServiceException("Payment call failed after exhausting retries.", 502);
    }

    private async Task WaitBackoffAsync(int attempt, string reason, CancellationToken cancellationToken)
    {
        var exponentialDelay = _options.BaseDelayMs * Math.Pow(2, attempt - 1);
        var jitter = _options.MaxJitterMs > 0 ? Random.Shared.Next(0, _options.MaxJitterMs + 1) : 0;
        var totalDelayMs = (int)Math.Min(10_000, exponentialDelay + jitter);

        _logger.LogWarning(
            "Payment attempt {Attempt}/{MaxAttempts} failed ({Reason}). Retrying in {DelayMs}ms...",
            attempt,
            _options.MaxRetries + 1,
            reason,
            totalDelayMs);

        if (totalDelayMs > 0)
        {
            await Task.Delay(totalDelayMs, cancellationToken);
        }
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code >= 500
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests;
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(
        HttpRequestMessage original,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version
        };

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync(cancellationToken);
            var byteContent = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
            {
                byteContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = byteContent;
        }

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
