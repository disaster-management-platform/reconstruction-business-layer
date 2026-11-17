using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL;
using Platform.DAL.Contexts;
using Reconstruction.Application;
using Reconstruction.Application.Messaging;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Application.Services;
using Reconstruction.Application.Contracts;
using System.Text.Json;
using Xunit;

namespace Reconstruction.Tests;

public sealed class MessagingTests : IDisposable
{
    private readonly PostgresDatabase _database = new();
    private string ConnectionString => _database.ConnectionString;

    private ServiceProvider Services() => new ServiceCollection().AddPlatformDal(ConnectionString)
        .AddScoped<FormalizationEventHandler>().AddScoped<MonitoringEventHandler>()
        .AddScoped<ClosureEventHandler>().AddScoped<TimelineEventHandler>()
        .AddScoped<WorkflowService>()
        .AddSingleton<IReportAttachmentVerifier, TestAttachments>().AddScoped<ProjectMonitoringService>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static ReconstructionProposal Proposal() => new(1, "Zona", "Puente", DamageLevel.Severe, 1, 100m, ProposalPriority.Unassigned);
    private static EventEnvelope Event(string type, Guid? projectId = null) =>
        new(Guid.NewGuid(), type, 1, projectId, "operador", "Evento de prueba", DateTimeOffset.UtcNow);
    private static EventEnvelope Typed(IIntegrationEventData data, Guid? projectId = null) =>
        Event(data.EventType, projectId) with { Data = EventPayloadJson.Serialize(data), Detail = "Texto sin datos de negocio" };

    [Fact]
    public async Task TypedSelectionCreatesAnIndividualPendingFormalizationAndResolvesReview()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        var proposal = Proposal();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        db.Proposals.Add(proposal); await db.SaveChangesAsync();
        var generated = Typed(new ProposalsGeneratedEvent([new(proposal.Id, proposal.Zone, proposal.AssetType,
            proposal.Damage, proposal.ProposedBudget, proposal.AssessmentCount)]));
        var handler = scope.ServiceProvider.GetRequiredService<FormalizationEventHandler>();
        await handler.HandleAsync(generated, default); await handler.HandleAsync(generated, default);
        Assert.True((await db.WorkflowItems.SingleAsync()).IsActive);
        Assert.Empty(await db.Projects.ToListAsync());
        var project = new ReconstructionProject(proposal); proposal.Select(); db.Projects.Add(project); await db.SaveChangesAsync();
        var selected = Typed(new ProposalsSelectedEvent(project.Id, proposal.Id, project.Version), project.Id);
        await handler.HandleAsync(selected, default); await handler.HandleAsync(selected, default);
        Assert.False((await db.WorkflowItems.SingleAsync(x => x.Kind == WorkflowKind.ProposalReview)).IsActive);
        var pending = Assert.Single(await scope.ServiceProvider.GetRequiredService<WorkflowService>().ListAsync(1, project.Id, default));
        Assert.Equal(WorkflowKind.FormalizationRequest, pending.Kind);
        Assert.Equal(proposal.Id, pending.Data.GetProperty("proposalId").GetGuid());
        Assert.Equal(ProjectStatus.Draft, project.Status); Assert.Equal(1, project.Version); Assert.Empty(project.Activities);
        Assert.Equal(2, await db.InboxMessages.CountAsync());
    }

    [Fact]
    public async Task TypedEventsPrepareMonitoringAndNotifyClosureAndCompletionWithoutAdvancingStages()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var proposal = Proposal(); var project = new ReconstructionProject(proposal); proposal.Select();
        var start = DateTimeOffset.UtcNow;
        project.SaveFormalization(100m, "Aportes", "Responsable", "Contratista",
            [new("Reparar", "Equipo", start, start.AddDays(1), 50m), new("Inspeccionar", "Especialista", start, start.AddDays(2), 50m)]);
        project.RegisterSignedContract("contrato://firmado", start.AddHours(-1), "operador");
        db.AddRange(proposal, project); await db.SaveChangesAsync();
        var formalized = Typed(new ProjectFormalizedEvent(project.Id, project.Version, project.FinalBudget,
            project.SignedContractReference, project.ContractSignedAt!.Value,
            project.Activities.Select(a => new ScheduledActivityData(a.Id, a.Name, a.Responsible, a.StartsAt, a.DueAt, a.Budget)).ToArray()), project.Id);
        await scope.ServiceProvider.GetRequiredService<MonitoringEventHandler>().HandleAsync(formalized, default);
        var schedule = await db.WorkflowItems.SingleAsync(x => x.Kind == WorkflowKind.MonitoringPrepared);
        Assert.True(schedule.IsActive); Assert.Equal(2, JsonDocument.Parse(schedule.DataJson).RootElement.GetProperty("activities").GetArrayLength());
        var closure = scope.ServiceProvider.GetRequiredService<ClosureEventHandler>();
        foreach (var activity in project.Activities)
        {
            var reportId = project.RegisterProgress(activity.Id, ActivityStatus.Completed, 10.25m, ["foto"], ["acta"], "operador");
            await db.SaveChangesAsync();
            var completed = Typed(new ActivityCompletedEvent(project.Id, activity.Id, reportId, 10.25m, project.Version), project.Id);
            await closure.HandleAsync(completed, default); await closure.HandleAsync(completed, default);
            Assert.Equal(project.CanFinalize(), (await db.WorkflowItems.SingleAsync(x => x.Kind == WorkflowKind.ClosureAvailable)).IsActive);
            Assert.Equal(ProjectStatus.Monitoring, project.Status); Assert.Null(project.ClosedAt);
        }
        var eligible = await db.WorkflowItems.SingleAsync(x => x.Kind == WorkflowKind.ClosureAvailable);
        Assert.True(eligible.IsActive);
        project.Close(); await db.SaveChangesAsync();
        var closed = Typed(new ProjectClosedEvent(project.Id, project.Version, project.ClosedAt!.Value,
            project.FinalBudget, 20.50m, 2, 2), project.Id);
        await closure.HandleAsync(closed, default); await closure.HandleAsync(closed, default);
        // Un evento antiguo que llega después del cierre no vuelve a habilitarlo.
        await closure.HandleAsync(Typed(new ActivityCompletedEvent(project.Id, project.Activities.First().Id,
            project.Activities.First().Reports.Single().Id, 10.25m, 4), project.Id) with { OccurredAt = formalized.OccurredAt }, default);
        var notices = await scope.ServiceProvider.GetRequiredService<WorkflowService>().ListAsync(1, project.Id, default);
        Assert.Equal(WorkflowKind.ProjectClosed, Assert.Single(notices).Kind);
        Assert.False(eligible.IsActive); Assert.False(schedule.IsActive);
        Assert.Empty(await db.OutboxMessages.ToListAsync()); Assert.Empty(await db.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task InvalidTypedReportCannotCreateANotificationOrMarkTheInbox()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var scope = provider.CreateAsyncScope();
        var project = ProjectMonitoringDomainTests.Project(); var activity = project.Activities.First();
        var reportId = project.RegisterProgress(activity.Id, ActivityStatus.Completed, 10m, ["foto"], ["acta"], "operador");
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        db.Projects.Add(project); await db.SaveChangesAsync();
        var handler = scope.ServiceProvider.GetRequiredService<ClosureEventHandler>();
        await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(Typed(
            new ActivityCompletedEvent(project.Id, activity.Id, reportId, 999m, project.Version), project.Id), default));
        Assert.Empty(await db.WorkflowItems.ToListAsync()); Assert.Empty(await db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task AHistoricalFormalizedEventWithoutAContractKeepsFormalizationPending()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var scope = provider.CreateAsyncScope();
        var project = new ReconstructionProject(Proposal());
        project.SaveFormalization(100m, "Aportes", "Responsable", "Contratista", [ProjectFormalizationDomainTests.Activity()]);
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        db.Projects.Add(project); await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringEventHandler>().HandleAsync(Event("reconstruction.project.formalized", project.Id), default);
        var task = Assert.Single(await scope.ServiceProvider.GetRequiredService<WorkflowService>().ListAsync(1, project.Id, default));
        Assert.Equal(WorkflowKind.FormalizationRequest, task.Kind);
        Assert.Equal(ProjectStatus.Draft, project.Status); Assert.False(project.IsBudgetLocked);
        Assert.Equal(2, project.Version);
    }

    [Fact]
    public async Task BusinessReportsPublishTypedIdentifiersAndExactMoney()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var scope = provider.CreateAsyncScope();
        var project = ProjectMonitoringDomainTests.Project(); var activity = project.Activities.First();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        db.Projects.Add(project); await db.SaveChangesAsync();
        var saved = await scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>().ReportAsync(project.Id, activity.Id,
            new RegisterProgressRequest(ActivityStatus.Completed, 12.34m, ["foto"], ["acta"], project.Version), "operador", default);
        var envelope = JsonSerializer.Deserialize<EventEnvelope>((await db.OutboxMessages.SingleAsync()).PayloadJson)!;
        var payload = envelope.Read<ActivityCompletedEvent>()!;
        Assert.Equal(project.Id, payload.ProjectId); Assert.Equal(activity.Id, payload.ActivityId);
        Assert.Equal(saved.Activities.Single(a => a.Id == activity.Id).Reports.Single().Id, payload.ReportId);
        Assert.Equal(12.34m, payload.ActualCost); Assert.Equal(saved.Version, payload.ProjectVersion);
    }

    [Fact]
    public async Task TypedPayloadMustIncludeCostEvenWhenTheActualCostIsZero()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var scope = provider.CreateAsyncScope();
        var project = ProjectMonitoringDomainTests.Project(); var activity = project.Activities.First();
        var reportId = project.RegisterProgress(activity.Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador");
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        db.Projects.Add(project); await db.SaveChangesAsync();
        var envelope = Event("reconstruction.activity.completed", project.Id) with
        {
            Data = EventPayloadJson.Serialize(new { ProjectId = project.Id, ActivityId = activity.Id, ReportId = reportId, ProjectVersion = project.Version })
        };
        await Assert.ThrowsAsync<JsonException>(() => scope.ServiceProvider.GetRequiredService<ClosureEventHandler>().HandleAsync(envelope, default));
        Assert.Empty(await db.WorkflowItems.ToListAsync()); Assert.Empty(await db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DuplicateRaceRollsBackTheWorkflowAndInboxTogether()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var first = provider.CreateAsyncScope(); await using var second = provider.CreateAsyncScope();
        var evt = Event("reconstruction.proposals.generated"); var subject = Guid.NewGuid();
        foreach (var scope in new[] { first, second })
        {
            await scope.ServiceProvider.GetRequiredService<WorkflowService>().PutAsync(WorkflowKind.ProposalReview,
                subject, null, subject, evt, true, new { ProposalId = subject }, default);
            scope.ServiceProvider.GetRequiredService<IConsumerInbox>().Record(ConsumerNames.Formalization, evt.Id, "{}");
        }
        await first.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(null, default);
        await Assert.ThrowsAsync<DbUpdateException>(() => second.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(null, default));
        await using var check = provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Single(await db.WorkflowItems.ToListAsync()); Assert.Single(await db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task PublishedLegacyEventsRebuildMissingProjectionsWithoutRewritingInboxOrRepublishing()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        var evt = Event("reconstruction.proposals.generated");
        await using (var seed = provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.Proposals.Add(Proposal());
            db.OutboxMessages.Add(new Platform.DAL.Models.OutboxMessage
            {
                Id = evt.Id, EventType = evt.Type, PayloadJson = JsonSerializer.Serialize(evt),
                CreatedUnixMs = evt.OccurredAt.ToUnixTimeMilliseconds(), SentAt = DateTimeOffset.UtcNow
            });
            seed.ServiceProvider.GetRequiredService<IConsumerInbox>().Record(ConsumerNames.Formalization, evt.Id, "{\"Result\":\"Resultado histórico\"}");
            await db.SaveChangesAsync();
        }
        await using var scope = provider.CreateAsyncScope();
        var published = await scope.ServiceProvider.GetRequiredService<IWorkflowReplaySource>().ReadPublishedAsync(default);
        var handler = scope.ServiceProvider.GetRequiredService<FormalizationEventHandler>();
        await handler.RebuildProjectionAsync(Assert.Single(published), default);
        await handler.RebuildProjectionAsync(published[0], default);
        var context = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.True(Assert.Single(await context.WorkflowItems.ToListAsync()).IsActive);
        Assert.Equal("{\"Result\":\"Resultado histórico\"}", Assert.Single(await context.InboxMessages.ToListAsync()).OutcomeJson);
        Assert.Single(await context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task TheSameEventIsDeduplicatedIndependentlyForEachConsumer()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        var evt = Event("reconstruction.proposals.generated");
        await using (var seed = provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.Proposals.Add(Proposal()); await db.SaveChangesAsync();
        }
        for (var retry = 0; retry < 2; retry++)
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FormalizationEventHandler>().HandleAsync(evt, default);
            await scope.ServiceProvider.GetRequiredService<TimelineEventHandler>().HandleAsync(evt, default);
        }
        await using var check = provider.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(2, await context.InboxMessages.CountAsync());
        Assert.Equal(1, await context.TimelineEntries.CountAsync());
        Assert.All(await context.InboxMessages.ToListAsync(), row => Assert.Equal(evt.Id, row.EventId));
        Assert.Equal(0, await context.Projects.CountAsync());
        Assert.Equal(ProposalStatus.Proposed, (await context.Proposals.SingleAsync()).Status);
        Assert.Equal(0, await context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task WorkflowEventsPersistResultsWithoutAdvancingOrClosingTheProject()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        Guid id;
        await using (var seed = provider.CreateAsyncScope())
        {
            var proposal = Proposal(); var project = new ReconstructionProject(proposal);
            project.SaveFormalization(100m, "Aportes", "Responsable", "Contratista",
                [new("Reparar", "Equipo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 100m)]);
            project.RegisterSignedContract("contrato://firmado", DateTimeOffset.UtcNow.AddHours(-1), "operador");
            ProjectMonitoringDomainTests.RestoreHistoricalStatus(project.Activities.First(), ActivityStatus.Completed);
            var db = seed.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.AddRange(proposal, project); await db.SaveChangesAsync(); id = project.Id;
        }
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MonitoringEventHandler>().HandleAsync(Event("reconstruction.project.formalized", id), default);
        await scope.ServiceProvider.GetRequiredService<ClosureEventHandler>().HandleAsync(Event("reconstruction.activity.completed", id), default);
        var context = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var saved = await context.Projects.SingleAsync();
        Assert.Equal(ProjectStatus.Monitoring, saved.Status);
        Assert.Equal(3, saved.Version);
        Assert.Null(saved.ClosedAt);
        Assert.Equal(2, await context.InboxMessages.CountAsync());
        Assert.True((await context.WorkflowItems.SingleAsync(x => x.Kind == WorkflowKind.MonitoringPrepared)).IsActive);
        Assert.False((await context.WorkflowItems.SingleAsync(x => x.Kind == WorkflowKind.ClosureAvailable)).IsActive);
        Assert.Equal(0, await context.Reports.CountAsync());
        Assert.Equal(0, await context.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task InvalidEventsDoNotMarkInboxAndCanBeRetriedAfterCorrection()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ClosureEventHandler>();
        await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(Event("reconstruction.proposals.generated"), default));
        await Assert.ThrowsAsync<MissingResourceException>(() => handler.HandleAsync(Event("reconstruction.activity.completed", Guid.NewGuid()), default));
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(0, await db.InboxMessages.CountAsync());
        Assert.Equal(0, await db.TimelineEntries.CountAsync());
    }

    [Fact]
    public async Task DuplicateRaceRollsBackInboxAndTimelineTogether()
    {
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using var first = provider.CreateAsyncScope(); await using var second = provider.CreateAsyncScope();
        var evt = Event("reconstruction.project.closed", Guid.NewGuid());
        foreach (var scope in new[] { first, second })
        {
            scope.ServiceProvider.GetRequiredService<IConsumerInbox>().Record(ConsumerNames.Timeline, evt.Id, "{}");
            scope.ServiceProvider.GetRequiredService<ITimelineWriter>().Append(evt);
        }
        await first.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(null, default);
        await Assert.ThrowsAsync<DbUpdateException>(() => second.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(null, default));
        await using var check = provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1, await db.InboxMessages.CountAsync()); Assert.Equal(1, await db.TimelineEntries.CountAsync());
    }

    [Fact]
    public async Task MigrationRerunPreservesInboxAndAllowsOtherConsumers()
    {
        var evt = Event("reconstruction.proposals.generated"); var processedAt = DateTimeOffset.UtcNow.AddDays(-1);
        // PostgreSQL stores timestamps at microsecond precision.
        processedAt = new DateTimeOffset(processedAt.Ticks - processedAt.Ticks % 10, TimeSpan.Zero);
        await using var provider = Services(); await provider.InitializePlatformDalAsync();
        await using (var seed = provider.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            context.Proposals.Add(Proposal());
            context.InboxMessages.Add(new Platform.DAL.Models.InboxMessage { ConsumerName = ConsumerNames.Timeline,
                EventId = evt.Id, ProcessedAt = processedAt });
            await context.SaveChangesAsync();
        }
        await PlatformDatabase.MigrateAsync(ConnectionString);
        await PlatformDatabase.MigrateAsync(ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var preserved = await db.InboxMessages.SingleAsync();
        Assert.Equal(ConsumerNames.Timeline, preserved.ConsumerName); Assert.Equal(evt.Id, preserved.EventId);
        Assert.Equal(processedAt, preserved.ProcessedAt);
        await scope.ServiceProvider.GetRequiredService<TimelineEventHandler>().HandleAsync(evt, default);
        await scope.ServiceProvider.GetRequiredService<FormalizationEventHandler>().HandleAsync(evt, default);
        Assert.Equal(2, await db.InboxMessages.CountAsync()); Assert.Equal(0, await db.TimelineEntries.CountAsync());
    }

    public void Dispose() => _database.Dispose();
}
