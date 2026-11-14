using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reconstruction.Application.Ports;
using Reconstruction.Infrastructure.Messaging.Inbox;

namespace Reconstruction.Infrastructure.Messaging;

public sealed class WorkflowProjectionRecovery(IServiceScopeFactory scopes, ILogger<WorkflowProjectionRecovery> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        IReadOnlyList<EventEnvelope> events;
        await using (var scope = scopes.CreateAsyncScope())
            events = await scope.ServiceProvider.GetRequiredService<IWorkflowReplaySource>().ReadPublishedAsync(ct);
        foreach (var envelope in events)
        {
            var consumer = envelope.Type switch
            {
                "reconstruction.proposals.generated" or "reconstruction.proposals.selected" or "reconstruction.project.created" => ConsumerNames.Formalization,
                "reconstruction.project.formalized" or "reconstruction.project.budget-revised" => ConsumerNames.Monitoring,
                _ => ConsumerNames.Closure
            };
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ConsumerInboxDispatcher>().RebuildAsync(consumer, envelope, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo reconstruir la proyección del evento {EventId}", envelope.Id); }
        }
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
