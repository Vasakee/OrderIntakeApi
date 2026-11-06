using System.Diagnostics;

namespace OrderIntakeApi.Observability;

public interface ICorrelationContext
{
    string CorrelationId { get; set; }
}

public class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    public string CorrelationId
    {
        get => CurrentId.Value ??= Guid.NewGuid().ToString("N");
        set => CurrentId.Value = value;
    }
}

public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        correlationContext.CorrelationId = correlationId;
        context.TraceIdentifier = correlationId;
        context.Items[HeaderName] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path.ToString(),
            ["RequestMethod"] = context.Request.Method
        }))
        {
            var sw = Stopwatch.StartNew();
            _logger.LogInformation(
                "HTTP {Method} {Path} started [CorrelationId={CorrelationId}]",
                context.Request.Method,
                context.Request.Path,
                correlationId);

            try
            {
                await _next(context);
                sw.Stop();

                _logger.LogInformation(
                    "HTTP {Method} {Path} completed with {StatusCode} in {ElapsedMs}ms [CorrelationId={CorrelationId}]",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds,
                    correlationId);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(
                    ex,
                    "HTTP {Method} {Path} failed after {ElapsedMs}ms [CorrelationId={CorrelationId}]",
                    context.Request.Method,
                    context.Request.Path,
                    sw.ElapsedMilliseconds,
                    correlationId);
                throw;
            }
        }
    }
}
