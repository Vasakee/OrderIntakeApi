namespace OrderIntakeApi.Observability;

public class CorrelationIdDelegatingHandler : DelegatingHandler
{
    private readonly ICorrelationContext _correlationContext;

    public CorrelationIdDelegatingHandler(ICorrelationContext correlationContext)
    {
        _correlationContext = correlationContext;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(
                CorrelationIdMiddleware.HeaderName,
                _correlationContext.CorrelationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
