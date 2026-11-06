namespace OrderIntakeApi.Resilience;

public class PaymentServiceException : Exception
{
    public int? StatusCode { get; }

    public PaymentServiceException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

public class PaymentTimeoutException : PaymentServiceException
{
    public TimeSpan Timeout { get; }

    public PaymentTimeoutException(TimeSpan timeout, Exception? innerException = null)
        : base($"Payment service call timed out after {timeout.TotalMilliseconds:F0}ms.", 504, innerException)
    {
        Timeout = timeout;
    }
}

public class CircuitBrokenException : PaymentServiceException
{
    public DateTime? RetryAfterUtc { get; }

    public CircuitBrokenException(DateTime? retryAfterUtc)
        : base($"Payment circuit breaker is OPEN until {retryAfterUtc:O}. Failing fast to protect downstream service.", 503)
    {
        RetryAfterUtc = retryAfterUtc;
    }
}
