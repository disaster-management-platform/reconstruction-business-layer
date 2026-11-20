using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reconstruction.Application.Facade;
using Reconstruction.Application.Ports;
using Reconstruction.Application.Services;
using Reconstruction.Domain.Policies;
using Reconstruction.Infrastructure.ExternalAdapters;
using Reconstruction.Infrastructure.Messaging;
using Reconstruction.Infrastructure.Messaging.Consumers;
using Reconstruction.Infrastructure.Messaging.Inbox;
using Reconstruction.Application.Messaging;
using Polly;

namespace Reconstruction.Infrastructure;

public static class DependencyInjection
{
    private static void ConfigureExternal(HttpClient client, IConfiguration configuration, string module)
    {
        var address = configuration[$"ExternalApis:{module}:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(address)) client.BaseAddress = new Uri(address);
        client.Timeout = TimeSpan.FromSeconds(4);
        var token = configuration[$"ExternalApis:{module}:BearerToken"];
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public static IServiceCollection AddReconstruction(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration.GetValue<bool>("Reconstruction:DemoExternalData") && !environment.IsDevelopment())
            throw new InvalidOperationException("Reconstruction:DemoExternalData solo está permitido en Development.");
        services.AddHttpClient("oauth", client => { client.Timeout = TimeSpan.FromSeconds(3); client.MaxResponseContentBufferSize = 65536; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ProposalDefinitionService>();
        services.AddScoped<FundingService>();
        services.AddScoped<ProposalReconciliationService>();
        services.AddScoped<ProjectFormalizationService>();
        services.AddScoped<ProjectMonitoringService>();
        if (environment.IsDevelopment() && configuration.GetValue<bool>("Attachments:LocalDevelopment"))
            services.AddSingleton<IAttachmentStore, FileAttachmentStore>();
        else
        {
            services.AddHttpClient("attachment-storage", client => client.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) });
            services.AddSingleton<IAttachmentStore, ObjectAttachmentStore>();
        }
        services.AddSingleton<IReportAttachmentVerifier>(p => p.GetRequiredService<IAttachmentStore>());
        services.AddScoped<ProjectClosureService>();
        services.AddScoped<FormalizationEventHandler>();
        services.AddScoped<MonitoringEventHandler>();
        services.AddScoped<ClosureEventHandler>();
        services.AddScoped<TimelineEventHandler>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<MessageDisasterScope>();
        services.AddScoped<ConsumerInboxDispatcher>();
        services.AddHostedService<WorkflowProjectionRecovery>();
        services.AddScoped<IReconstructionFacade, ReconstructionFacade>();
        services.AddSingleton<IProposalPolicy, WeightedProposalPolicy>();

        // Estado compartido entre scopes/HttpClients del mismo servicio remoto.
        services.AddSingleton<ResiliencePipeline<DisasterSnapshot?>>(_ => DisasterHttpResilience.Create());
        services.AddHttpClient<IDisasterReader, DisasterHttpReader>(client =>
        {
            client.BaseAddress = new Uri(configuration["ExternalApis:Disasters"] ?? "http://localhost:5156/");
            client.Timeout = TimeSpan.FromSeconds(4);
        }).AddHttpMessageHandler(p => new ClientCredentialsHandler(p.GetRequiredService<IHttpClientFactory>(), configuration, "Disasters"));

        if (configuration.GetValue<bool>("Reconstruction:DemoExternalData")
            && string.IsNullOrWhiteSpace(configuration["ExternalApis:DamageAssessments:BaseUrl"]))
            services.AddScoped<IDamageAssessmentReader, DemoDamageAssessmentReader>();
        else
            services.AddHttpClient<IDamageAssessmentReader, DamageAssessmentHttpReader>(c => ConfigureExternal(c, configuration, "DamageAssessments")).AddHttpMessageHandler(p => new ClientCredentialsHandler(p.GetRequiredService<IHttpClientFactory>(), configuration, "DamageAssessments"));
        if (configuration.GetValue<bool>("Reconstruction:DemoExternalData"))
            services.AddScoped<IFundingReader, DemoFundingReader>();
        else
            services.AddHttpClient<IFundingReader, FundingHttpReader>(c => ConfigureExternal(c, configuration, "Funding")).AddHttpMessageHandler(p => new ClientCredentialsHandler(p.GetRequiredService<IHttpClientFactory>(), configuration, "Funding"));

        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection("Messaging"))
            .Validate(o => o.MaxDeliveryAttempts is >= 1 and <= 10
                && o.RetryDelayMilliseconds is >= 1 and <= 10000 && o.PublishTimeoutSeconds is >= 1 and <= 120
                && o.Port is >= 1 and <= 65535, "Configuración de reintentos/puerto RabbitMQ inválida.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.HostName) && !string.IsNullOrWhiteSpace(o.VirtualHost)
                && !string.IsNullOrWhiteSpace(o.Exchange) && !string.IsNullOrWhiteSpace(o.DeadLetterExchange)
                && o.Exchange != o.DeadLetterExchange
                && RabbitMqTopology.Subscriptions(o).All(s => !string.IsNullOrWhiteSpace(s.Queue))
                && RabbitMqTopology.Subscriptions(o).SelectMany(s => new[] { s.Queue, s.Queue + ".dlq" }).Distinct().Count() == 10,
                "Los exchanges y colas de RabbitMQ deben ser válidos y distintos.")
            .ValidateOnStart();
        if (configuration.GetValue<bool>("Messaging:Enabled"))
        {
            services.AddSingleton<IEventTransport, RabbitMqEventTransport>();
            services.AddHostedService<OutboxDispatcher>();
            services.AddHostedService<RabbitMqTimelineConsumer>();
            services.AddHostedService<FormalizationConsumer>();
            services.AddHostedService<MonitoringConsumer>();
            services.AddHostedService<ClosureConsumer>();
        }
        return services;
    }
}
