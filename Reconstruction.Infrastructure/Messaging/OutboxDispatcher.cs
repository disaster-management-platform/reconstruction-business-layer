using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.DAL.Contexts;

namespace Reconstruction.Infrastructure.Messaging;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IEventTransport transport,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
                var pending = await db.OutboxMessages.Where(x => x.SentAt == null)
                    .OrderBy(x => x.CreatedUnixMs).Take(30).ToListAsync(stoppingToken);
                foreach (var item in pending)
                {
                    item.Attempts++;
                    try { await transport.PublishAsync(item, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        await db.SaveChangesAsync(stoppingToken);
                        logger.LogWarning(ex, "Publicación no confirmada para {EventId}; queda pendiente en Outbox", item.Id);
                        break;
                    }
                    item.SentAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "MOM no disponible; Outbox conserva eventos pendientes"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
