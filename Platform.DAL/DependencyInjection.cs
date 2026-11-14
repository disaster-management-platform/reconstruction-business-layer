using DisasterLocation.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL.Contexts;
using Platform.DAL.Messaging;
using Platform.DAL.Repositories.Disasters;
using Platform.DAL.Repositories.Reconstruction;
using Platform.DAL.Transactions;
using Reconstruction.Application.Ports;

namespace Platform.DAL;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformDal(this IServiceCollection services, string connectionString)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IDisasterAccess, UnrestrictedDisasterAccess>(services);
        services.AddDbContext<DisasterDbContext>(options => options.UseSqlite(connectionString,
            sqlite => sqlite.MigrationsHistoryTable(PlatformDatabase.DisasterHistory)));
        services.AddDbContext<ReconstructionDbContext>(options => options.UseSqlite(connectionString,
            sqlite => sqlite.MigrationsHistoryTable(PlatformDatabase.ReconstructionHistory)));
        services.AddScoped<IDisasterRepository, DisasterRepository>();
        services.AddScoped<IProposalRepository, ProposalRepository>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IFundingReservationRepository, FundingReservationRepository>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IWorkflowReplaySource, WorkflowReplaySource>();
        services.AddScoped<IReconstructionHistoryReader, HistoryReader>();
        services.AddScoped<IReconstructionUnitOfWork, ReconstructionUnitOfWork>();
        services.AddScoped<IConsumerInbox, ConsumerInbox>();
        services.AddScoped<ITimelineWriter, TimelineWriter>();
        return services;
    }

    public static async Task InitializePlatformDalAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var disasters = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        var reconstruction = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        // Both APIs can start together. Serialize baseline + migrations across local processes.
        await using var migrationLock = await PlatformDatabase.AcquireMigrationLockAsync(disasters, ct);
        await LegacySchemaBaseline.ApplyAsync(disasters, ct);
        await LegacySchemaBaseline.ApplyAsync(reconstruction, ct);
        await disasters.Database.MigrateAsync(ct);
        await reconstruction.Database.MigrateAsync(ct);
    }
}
