namespace Reconstruction.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public bool Enabled { get; set; }
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string Exchange { get; set; } = "reconstruction.events";
    public string TimelineQueue { get; set; } = "reconstruction.timeline";
    public string FormalizationQueue { get; set; } = "reconstruction.formalization";
    public string MonitoringQueue { get; set; } = "reconstruction.monitoring";
    public string ClosureQueue { get; set; } = "reconstruction.closure";
    public string IntegrationsQueue { get; set; } = "reconstruction.integrations";
    public string DeadLetterExchange { get; set; } = "reconstruction.events.dlx";
    public int MaxDeliveryAttempts { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 1000;
    public int PublishTimeoutSeconds { get; set; } = 15;
}
