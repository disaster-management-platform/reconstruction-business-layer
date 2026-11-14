using System.Net;
using System.Text.Json;
using Polly;
using Polly.CircuitBreaker;
using Reconstruction.Application.Ports;

namespace Reconstruction.Infrastructure.ExternalAdapters;

public static class DisasterHttpResilience
{
    public static ResiliencePipeline<DisasterSnapshot?> Create(TimeProvider? clock = null) =>
        new ResiliencePipelineBuilder<DisasterSnapshot?> { Name = "disasters", TimeProvider = clock ?? TimeProvider.System }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<DisasterSnapshot?>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = args => ValueTask.FromResult(!args.Context.CancellationToken.IsCancellationRequested
                    && (args.Outcome.Exception switch
                    {
                        HttpRequestException ex => ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                            || (int?)ex.StatusCode >= 500,
                        OperationCanceledException => true, // Timeout interno; el token del usuario sigue activo.
                        JsonException => true,
                        _ => false
                    }))
            }).Build();
}
