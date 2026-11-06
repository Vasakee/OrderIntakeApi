using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderIntakeApi.Models;
using OrderIntakeApi.Resilience;

namespace OrderIntakeApi.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task ResilienceHandler_Transient503Then200_RetriesWithBackoffAndSucceeds()
    {
        var attempts = 0;
        var innerHandler = new StubHttpMessageHandler((req, ct) =>
        {
            attempts++;
            if (attempts <= 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse { PaymentId = "pay_123" })
            });
        });

        using var client = CreateResilientClient(innerHandler, new ResilienceOptions
        {
            MaxRetries = 3,
            BaseDelayMs = 10,
            MaxJitterMs = 5,
            AttemptTimeoutMs = 500,
            CircuitBreakerFailureThreshold = 5
        }, out var breaker);

        var response = await client.PostAsJsonAsync("http://localhost/payments", new PaymentRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.Equal("Closed", breaker.GetSnapshot().State);
        Assert.Equal(0, breaker.GetSnapshot().ConsecutiveFailures);
    }

    [Fact]
    public async Task ResilienceHandler_DownstreamHangs5Seconds_TimesOutAttemptAndRetriesToSuccess()
    {
        var attempts = 0;
        var innerHandler = new StubHttpMessageHandler(async (req, ct) =>
        {
            attempts++;
            if (attempts == 1)
            {
                await Task.Delay(5000, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PaymentResponse { PaymentId = "pay_after_timeout" })
            };
        });

        using var client = CreateResilientClient(innerHandler, new ResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMs = 10,
            MaxJitterMs = 5,
            AttemptTimeoutMs = 80,
            CircuitBreakerFailureThreshold = 5
        }, out _);

        var response = await client.PostAsJsonAsync("http://localhost/payments", new PaymentRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CircuitBreaker_TripsAfterThreshold_FailsFastThenRecoversViaHalfOpen()
    {
        var downstreamCalls = 0;
        var shouldFail = true;

        var innerHandler = new StubHttpMessageHandler((req, ct) =>
        {
            downstreamCalls++;
            if (shouldFail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var options = new ResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMs = 5,
            MaxJitterMs = 0,
            AttemptTimeoutMs = 200,
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerDurationMs = 120
        };

        using var client = CreateResilientClient(innerHandler, options, out var breaker);

        var firstResponse = await client.PostAsJsonAsync("http://localhost/payments", new PaymentRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, firstResponse.StatusCode);
        Assert.Equal(3, downstreamCalls);
        Assert.Equal("Open", breaker.GetSnapshot().State);

        await Assert.ThrowsAsync<CircuitBrokenException>(() =>
            client.PostAsJsonAsync("http://localhost/payments", new PaymentRequest()));
        Assert.Equal(3, downstreamCalls);

        await Task.Delay(150);
        shouldFail = false;

        var recoveryResponse = await client.PostAsJsonAsync("http://localhost/payments", new PaymentRequest());
        Assert.Equal(HttpStatusCode.OK, recoveryResponse.StatusCode);
        Assert.Equal(4, downstreamCalls);
        Assert.Equal("Closed", breaker.GetSnapshot().State);
    }

    private static HttpClient CreateResilientClient(
        HttpMessageHandler innerHandler,
        ResilienceOptions options,
        out CircuitBreakerStateMachine circuitBreaker)
    {
        var optionsWrapper = Options.Create(options);
        circuitBreaker = new CircuitBreakerStateMachine(
            optionsWrapper,
            NullLogger<CircuitBreakerStateMachine>.Instance);

        var resilienceHandler = new PaymentResilienceHandler(
            optionsWrapper,
            circuitBreaker,
            NullLogger<PaymentResilienceHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        return new HttpClient(resilienceHandler);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }
}
