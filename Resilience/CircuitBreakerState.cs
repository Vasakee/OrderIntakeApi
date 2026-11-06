using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderIntakeApi.Resilience;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public record CircuitBreakerSnapshot(
    string State,
    int ConsecutiveFailures,
    int FailureThreshold,
    DateTime? OpenedUntilUtc);

public class CircuitBreakerStateMachine
{
    private readonly object _sync = new();
    private readonly ResilienceOptions _options;
    private readonly ILogger<CircuitBreakerStateMachine> _logger;
    private readonly TimeProvider _timeProvider;

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTime? _openedUntilUtc;
    private bool _halfOpenProbeInFlight;

    public CircuitBreakerStateMachine(
        IOptions<ResilienceOptions> options,
        ILogger<CircuitBreakerStateMachine> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CircuitBreakerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            EvaluateTimerTransitionUnlocked();
            return new CircuitBreakerSnapshot(
                _state.ToString(),
                _consecutiveFailures,
                _options.CircuitBreakerFailureThreshold,
                _openedUntilUtc);
        }
    }

    public void BeforeExecution()
    {
        lock (_sync)
        {
            EvaluateTimerTransitionUnlocked();

            if (_state == CircuitState.Open)
            {
                throw new CircuitBrokenException(_openedUntilUtc);
            }

            if (_state == CircuitState.HalfOpen)
            {
                if (_halfOpenProbeInFlight)
                {
                    throw new CircuitBrokenException(_openedUntilUtc);
                }

                _halfOpenProbeInFlight = true;
                _logger.LogInformation("Circuit breaker is HalfOpen; allowing probe request.");
            }
        }
    }

    public void OnSuccess()
    {
        lock (_sync)
        {
            var previousState = _state;
            _consecutiveFailures = 0;
            _openedUntilUtc = null;
            _halfOpenProbeInFlight = false;
            _state = CircuitState.Closed;

            if (previousState != CircuitState.Closed)
            {
                _logger.LogInformation(
                    "Circuit breaker moved from {PreviousState} to Closed.",
                    previousState);
            }
        }
    }

    public void OnFailure(string reason)
    {
        lock (_sync)
        {
            _consecutiveFailures++;
            _halfOpenProbeInFlight = false;

            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= _options.CircuitBreakerFailureThreshold)
            {
                var breakDuration = TimeSpan.FromMilliseconds(_options.CircuitBreakerDurationMs);
                _openedUntilUtc = _timeProvider.GetUtcNow().UtcDateTime.Add(breakDuration);
                var previousState = _state;
                _state = CircuitState.Open;

                _logger.LogWarning(
                    "Circuit breaker tripped from {PreviousState} to OPEN for {BreakDurationMs}ms ({ConsecutiveFailures} failures). Reason: {Reason}",
                    previousState,
                    _options.CircuitBreakerDurationMs,
                    _consecutiveFailures,
                    reason);
            }
        }
    }

    private void EvaluateTimerTransitionUnlocked()
    {
        if (_state == CircuitState.Open && _openedUntilUtc.HasValue)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (now >= _openedUntilUtc.Value)
            {
                _state = CircuitState.HalfOpen;
                _halfOpenProbeInFlight = false;
                _logger.LogInformation("Circuit breaker break duration elapsed; moving to HalfOpen.");
            }
        }
    }
}
