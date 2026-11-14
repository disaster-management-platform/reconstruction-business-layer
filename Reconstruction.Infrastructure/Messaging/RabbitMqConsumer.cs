using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Reconstruction.Application.Ports;
using Reconstruction.Infrastructure.Messaging.Inbox;

namespace Reconstruction.Infrastructure.Messaging;

public abstract class RabbitMqConsumer(IOptions<RabbitMqOptions> settings, IServiceScopeFactory scopes,
    IEventTransport transport, ILogger logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    protected abstract string ConsumerName { get; }
    protected abstract string Queue(RabbitMqOptions options);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = settings.Value;
                await using var connection = await RabbitMqTopology.CreateFactory(options).CreateConnectionAsync(stoppingToken);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                await RabbitMqTopology.DeclareAsync(channel, options, stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
                consumer.ReceivedAsync += (_, delivery) => ProcessDeliveryAsync(channel, delivery, options, stoppingToken);
                await channel.BasicConsumeAsync(Queue(options), autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
                logger.LogInformation("Consumidor {Consumer} conectado a {Queue}", ConsumerName, Queue(options));
                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Consumidor {Consumer} desconectado; se reintentará", ConsumerName); }
            try { await Task.Delay(2000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery, RabbitMqOptions options, CancellationToken ct)
    {
        var body = delivery.Body.ToArray();
        Exception? failure = null;
        for (var attempt = 1; attempt <= options.MaxDeliveryAttempts; attempt++)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<EventEnvelope>(body, JsonOptions)
                    ?? throw new JsonException("Evento sin contenido.");
                if (envelope.Type != delivery.RoutingKey) throw new JsonException("El tipo de evento no coincide con su routing key.");
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ConsumerInboxDispatcher>().DispatchAsync(ConsumerName, envelope, ct);
                failure = null;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                failure = ex;
                logger.LogWarning(ex, "Fallo en {Consumer}; intento {Attempt}/{Maximum}", ConsumerName, attempt, options.MaxDeliveryAttempts);
                if (attempt < options.MaxDeliveryAttempts)
                {
                    try { await Task.Delay(options.RetryDelayMilliseconds * attempt, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        try
        {
            if (failure is not null)
            {
                var headers = delivery.BasicProperties.Headers is null ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(delivery.BasicProperties.Headers);
                headers["x-original-routing-key"] = delivery.RoutingKey;
                headers["x-consumer-name"] = ConsumerName;
                headers["x-processing-attempts"] = options.MaxDeliveryAttempts;
                headers["x-processing-error"] = failure.Message[..Math.Min(1000, failure.Message.Length)];
                await transport.PublishDeadLetterAsync(Queue(options), new DeadLetterMessage(body, delivery.RoutingKey,
                    delivery.BasicProperties.MessageId, delivery.BasicProperties.ContentType,
                    delivery.BasicProperties.ContentEncoding, delivery.BasicProperties.Type, headers), ct);
                logger.LogError("Mensaje enviado a {Queue}.dlq tras {Attempts} intentos", Queue(options), options.MaxDeliveryAttempts);
            }
            // Success: DB commit returned. Failure: persistent DLQ publication confirmed.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se confirmó la entrega de {Consumer}; se conservará para reentrega", ConsumerName);
            if (channel.IsOpen)
            {
                try { await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, ct); }
                catch (Exception nackError) { logger.LogWarning(nackError, "Canal desconectado antes de NACK"); }
            }
        }
    }
}
