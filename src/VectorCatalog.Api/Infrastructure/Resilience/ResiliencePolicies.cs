using Grpc.Core;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace VectorCatalog.Api.Infrastructure.Resilience;

/// <summary>
/// Pre-configured Polly resilience policies for gRPC sidecar calls.
///
/// Design decisions (explainable in system design interview):
/// - Retry on transient gRPC errors only (UNAVAILABLE, DEADLINE_EXCEEDED)
/// - Circuit breaker opens after 5 consecutive failures → 30s break
/// - Exponential backoff with jitter prevents thundering herd
/// - Timeout wraps retry+CB to cap total request time
/// </summary>
public static class ResiliencePolicies
{
    private static readonly IReadOnlyList<StatusCode> TransientStatusCodes =
    [
        StatusCode.Unavailable,
        StatusCode.DeadlineExceeded,
        StatusCode.ResourceExhausted,
        StatusCode.Internal
    ];

    public static IAsyncPolicy<T> GetGrpcRetryPolicy<T>(ILogger logger, string operationName)
    {
        return Policy<T>
            .Handle<RpcException>(ex => TransientStatusCodes.Contains(ex.StatusCode))
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt))   // 200ms, 400ms, 800ms
                    + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100)), // jitter
                onRetry: (outcome, timespan, attempt, ctx) =>
                {
                    var ex = outcome.Exception as RpcException;
                    logger.LogWarning(
                        "gRPC retry {Attempt}/3 for {Operation}: {StatusCode} — waiting {Delay}ms",
                        attempt, operationName, ex?.StatusCode, timespan.TotalMilliseconds);
                });
    }

    public static IAsyncPolicy<T> GetGrpcCircuitBreakerPolicy<T>(ILogger logger, string operationName)
    {
        return Policy<T>
            .Handle<RpcException>(ex => TransientStatusCodes.Contains(ex.StatusCode))
            .AdvancedCircuitBreakerAsync(
                failureThreshold: 0.5,              // Open when 50% of requests fail
                samplingDuration: TimeSpan.FromSeconds(10),
                minimumThroughput: 5,                // At least 5 requests in sampling window
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, duration) =>
                    logger.LogError("Circuit breaker OPEN for {Operation}: {Duration}s break. Reason: {Error}",
                        operationName, duration.TotalSeconds, ex.Exception?.Message),
                onReset: () =>
                    logger.LogInformation("Circuit breaker CLOSED for {Operation} — resuming", operationName),
                onHalfOpen: () =>
                    logger.LogInformation("Circuit breaker HALF-OPEN for {Operation} — probing", operationName));
    }

    /// <summary>
    /// Combined policy: Timeout → CircuitBreaker → Retry (outer to inner).
    /// Execution order: Timeout wraps (CircuitBreaker wraps (Retry wraps (operation))).
    /// </summary>
    public static IAsyncPolicy<T> GetCombinedGrpcPolicy<T>(ILogger logger, string operationName, int timeoutSeconds = 5)
    {
        var timeout = Policy.TimeoutAsync<T>(timeoutSeconds);
        var cb = GetGrpcCircuitBreakerPolicy<T>(logger, operationName);
        var retry = GetGrpcRetryPolicy<T>(logger, operationName);

        return Policy.WrapAsync(timeout, cb, retry);
    }
}
