namespace OrderIntakeApi.Resilience;

public class ResilienceOptions
{
    public const string SectionName = "PaymentResilience";

    public int MaxRetries { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 200;
    public int MaxJitterMs { get; set; } = 100;
    public int AttemptTimeoutMs { get; set; } = 1500;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public int CircuitBreakerDurationMs { get; set; } = 15000;
}
