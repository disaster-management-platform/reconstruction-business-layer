using System.Text;
using Microsoft.Extensions.Options;
using Platform.DAL.Models;
using RabbitMQ.Client;

namespace Reconstruction.Infrastructure.Messaging;

public sealed record DeadLetterMessage(byte[] Body, string RoutingKey, string? MessageId,
    string? ContentType, string? ContentEncoding, string? Type, IDictionary<string, object?> Headers);

public interface IEventTransport
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
    Task PublishDeadLetterAsync(string queue, DeadLetterMessage message, CancellationToken ct);
}

public sealed class RabbitMqEventTransport(IOptions<RabbitMqOptions> configuration) : IEventTransport, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public Task PublishAsync(OutboxMessage message, CancellationToken ct) => PublishConfirmedAsync(
        configuration.Value.Exchange, message.EventType, new BasicProperties
        {
            ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.Id.ToString("D"), Type = message.EventType
        }, Encoding.UTF8.GetBytes(message.PayloadJson), ct);

    public Task PublishDeadLetterAsync(string queue, DeadLetterMessage message, CancellationToken ct) => PublishConfirmedAsync(
        configuration.Value.DeadLetterExchange, queue, new BasicProperties
        {
            ContentType = message.ContentType ?? "application/octet-stream", ContentEncoding = message.ContentEncoding,
            DeliveryMode = DeliveryModes.Persistent, MessageId = message.MessageId,
            Type = message.Type, Headers = message.Headers
        }, message.Body, ct);

    private async Task PublishConfirmedAsync(string exchange, string routingKey, BasicProperties properties, byte[] body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Value.PublishTimeoutSeconds));
        await _gate.WaitAsync(timeout.Token);
        try
        {
            if (_connection is null || !_connection.IsOpen || _channel is null || !_channel.IsOpen)
            {
                if (_channel is not null) await _channel.DisposeAsync();
                if (_connection is not null) await _connection.DisposeAsync();
                _connection = await RabbitMqTopology.CreateFactory(configuration.Value).CreateConnectionAsync(timeout.Token);
                _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
                await RabbitMqTopology.DeclareAsync(_channel, configuration.Value, timeout.Token);
            }
            // Awaiting with tracking fails on NACK or mandatory return as well.
            await _channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, body, timeout.Token);
        }
        catch
        {
            if (_channel is not null) { await _channel.DisposeAsync(); _channel = null; }
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
