using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL;
using Platform.DAL.Contexts;
using Reconstruction.Application;
using Reconstruction.Application.Contracts;
using Reconstruction.Application.Ports;
using Reconstruction.Application.Services;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Xunit;

namespace Reconstruction.Tests;

public sealed class ProjectClosureDomainTests
{
    internal static ReconstructionProject Project(ActivityStatus first = ActivityStatus.Pending, ActivityStatus second = ActivityStatus.Pending)
    {
        var proposal = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 200m, ProposalPriority.Unassigned);
        var project = new ReconstructionProject(proposal);
        var start = DateTimeOffset.UtcNow;
        project.SaveFormalization(200m, "Aportes", "Responsable", "Contratista",
            [new("Reparación", "Equipo", start, start.AddDays(5), 100m),
             new("Inspección", "Especialista", start.AddDays(1), start.AddDays(6), 100m)]);
        project.RegisterSignedContract("contrato://firmado", start.AddHours(-1), "responsable");
        ProjectMonitoringDomainTests.RestoreHistoricalStatus(project.Activities.First(), first);
        ProjectMonitoringDomainTests.RestoreHistoricalStatus(project.Activities.ElementAt(1), second);
        return project;
    }

    [Theory]
    [InlineData(ActivityStatus.Pending)]
    [InlineData(ActivityStatus.InProgress)]
    public void AnyUnfinishedActivityPreventsFinalization(ActivityStatus unfinished)
    {
        var project = Project(ActivityStatus.Completed, unfinished);
        Assert.False(project.CanFinalize());
        Assert.Throws<DomainException>(() => project.Close());
        Assert.Equal(ProjectStatus.Monitoring, project.Status);
        Assert.Null(project.ClosedAt);
        Assert.Equal(3, project.Version);
    }

    [Fact]
    public void FinalizationRequiresACompleteFinalReportForEveryActivity()
    {
        var project = Project(ActivityStatus.Completed, ActivityStatus.Completed);
        Assert.All(project.Activities, a => Assert.Empty(a.Reports));
        Assert.False(project.CanFinalize());
        Assert.Throws<DomainException>(() => project.Close());
        project.RegisterProgress(project.Activities.First().Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador");
        Assert.False(project.CanFinalize());
        Assert.Throws<DomainException>(() => project.Close());
        project.RegisterProgress(project.Activities.Last().Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador");
        Assert.True(project.CanFinalize());
        project.Close();
        Assert.Equal(ProjectStatus.Closed, project.Status);
        Assert.NotNull(project.ClosedAt);
        Assert.Equal(200m, project.FinalBudget);
        Assert.False(project.CanFinalize());
    }

    [Fact]
    public void EmptyScheduleCannotFinalizeAndClosedProjectsCannotReceiveReports()
    {
        var empty = new ReconstructionProject(new ReconstructionProposal(1, "Zona", "Puente",
            DamageLevel.Severe, 1, 200m, ProposalPriority.Unassigned));
        Assert.False(empty.CanFinalize());
        Assert.Throws<DomainException>(() => empty.Close());
        var project = Project(ActivityStatus.Completed, ActivityStatus.Completed);
        foreach (var activity in project.Activities)
            project.RegisterProgress(activity.Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador");
        project.Close();
        Assert.Throws<DomainException>(() => project.RegisterProgress(project.Activities.First().Id,
            ActivityStatus.Completed, 1m, ["foto"], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.SaveFormalization(300m, "Otra", "Otra", "Otro", []));
    }
}

public sealed class ProjectClosurePersistenceTests : IDisposable
{
    private readonly PostgresDatabase _database = new();

    private async Task<(ServiceProvider Provider, Guid Id)> SeedAsync(bool completed = false, bool withReports = false)
    {
        var services = new ServiceCollection().AddPlatformDal(_database.ConnectionString);
        services.AddSingleton<IReportAttachmentVerifier, TestAttachments>();
        services.AddSingleton<IReportAttachmentVerifier, TestAttachments>().AddScoped<ProjectMonitoringService>(); services.AddScoped<ProjectClosureService>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.InitializePlatformDalAsync();
        var project = completed ? ProjectClosureDomainTests.Project(ActivityStatus.Completed, ActivityStatus.Completed) : ProjectClosureDomainTests.Project();
        if (withReports)
            foreach (var activity in project.Activities)
                project.RegisterProgress(activity.Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador");
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProjectRepository>().Add(project);
        await scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>().SaveChangesAsync();
        return (provider, project.Id);
    }

    [Fact]
    public async Task ConsolidatesEveryReportAndPreservesTheResultAcrossReloadsAndRetries()
    {
        var seed = await SeedAsync(); await using var provider = seed.Provider;
        string originalReport; DateTimeOffset? closedAt;
        await using (var scope = provider.CreateAsyncScope())
        {
            var monitoring = scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>();
            var closure = scope.ServiceProvider.GetRequiredService<ProjectClosureService>();
            var current = await monitoring.FindAsync(seed.Id, default);
            var first = current.Activities[0].Id; var second = current.Activities[1].Id;
            foreach (var (state, cost) in new[] { (ActivityStatus.Pending, 0m), (ActivityStatus.InProgress, 20m),
                (ActivityStatus.InProgress, 15m), (ActivityStatus.Completed, 25m) })
                current = await monitoring.ReportAsync(seed.Id, first,
                    new RegisterProgressRequest(state, cost, [$"foto://{state}-{cost}"], [$"acta://{state}-{cost}"], current.Version), "operador", default);
            current = await monitoring.ReportAsync(seed.Id, second,
                new RegisterProgressRequest(ActivityStatus.Completed, 40m, ["foto://inspección"], ["acta://inspección"], current.Version), "inspector", default);
            Assert.True((await monitoring.GetMonitoringAsync(seed.Id, default)).Summary.CanFinalize);
            await Assert.ThrowsAsync<BusinessConflictException>(() => closure.GetFinalReportAsync(seed.Id, default));
            var closed = await closure.CloseAsync(seed.Id, current.Version, "responsable", default); closedAt = closed.ClosedAt;
            var report = await closure.GetFinalReportAsync(seed.Id, default);
            Assert.Equal(2, report.TotalActivities);
            Assert.Equal(5, report.TotalReports);
            Assert.Equal(100m, report.TotalExecuted);
            Assert.Equal(200m, report.FinalBudget);
            Assert.Equal(100m, report.RemainingBudget);
            Assert.Equal([0m, 20m, 15m, 25m], report.Activities[0].Reports.Select(r => r.ActualCost));
            Assert.Equal(ActivityStatus.Pending, report.Activities[0].Reports[0].Status);
            Assert.Equal(["foto://inspección"], report.Activities[1].Reports[0].Evidence);
            Assert.Equal(["acta://inspección"], report.Activities[1].Reports[0].Documents);
            Assert.Equal("inspector", report.Activities[1].Reports[0].Actor);
            originalReport = JsonSerializer.Serialize(report);
            var retry = await closure.CloseAsync(seed.Id, closed.Version, "responsable", default);
            Assert.Equal(closed.Version, retry.Version);
            Assert.Equal(closed.ClosedAt, retry.ClosedAt);
        }
        await using var reload = provider.CreateAsyncScope();
        var reloadedReport = await reload.ServiceProvider.GetRequiredService<ProjectClosureService>().GetFinalReportAsync(seed.Id, default);
        Assert.Equal(originalReport, JsonSerializer.Serialize(reloadedReport));
        Assert.Equal(closedAt, reloadedReport.ClosedAt);
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(6, await db.AuditEntries.CountAsync());
        Assert.Equal(6, await db.OutboxMessages.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync(e => e.Action == "reconstruction.project.closed"));
    }

    [Fact]
    public async Task CompletedScheduleWithoutReportsCannotCloseOrGenerateAFinalReport()
    {
        var seed = await SeedAsync(completed: true); await using var provider = seed.Provider;
        await using var scope = provider.CreateAsyncScope();
        var closure = scope.ServiceProvider.GetRequiredService<ProjectClosureService>();
        Assert.False((await scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>().GetMonitoringAsync(seed.Id, default)).Summary.CanFinalize);
        await Assert.ThrowsAsync<DomainException>(() => closure.CloseAsync(seed.Id, 3, "responsable", default));
        await Assert.ThrowsAsync<BusinessConflictException>(() => closure.GetFinalReportAsync(seed.Id, default));
        await using var reload = provider.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var unchanged = await db.Projects.Include(p => p.Activities).SingleAsync();
        Assert.Equal(ProjectStatus.Monitoring, unchanged.Status);
        Assert.Null(unchanged.ClosedAt);
        Assert.Equal(3, unchanged.Version);
        Assert.All(unchanged.Activities, activity => Assert.Equal(ActivityStatus.Completed, activity.Status));
        Assert.Empty(await db.Reports.ToListAsync());
        Assert.Empty(await db.AuditEntries.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task UnfinishedProjectStaleVersionAndMissingProjectAreRejectedWithoutAudit()
    {
        var seed = await SeedAsync(); await using var provider = seed.Provider;
        await using var scope = provider.CreateAsyncScope();
        var closure = scope.ServiceProvider.GetRequiredService<ProjectClosureService>();
        await Assert.ThrowsAsync<DomainException>(() => closure.CloseAsync(seed.Id, 3, "responsable", default));
        await Assert.ThrowsAsync<BusinessConflictException>(() => closure.CloseAsync(seed.Id, 2, "responsable", default));
        await Assert.ThrowsAsync<MissingResourceException>(() => closure.CloseAsync(Guid.NewGuid(), 3, "responsable", default));
        await Assert.ThrowsAsync<MissingResourceException>(() => closure.GetFinalReportAsync(Guid.NewGuid(), default));
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(ProjectStatus.Monitoring, (await db.Projects.SingleAsync()).Status);
        Assert.Equal(0, await db.AuditEntries.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData(nameof(ProgressReport.Evidence))]
    [InlineData(nameof(ProgressReport.Documents))]
    public async Task AFinalReportMissingEvidenceOrDocumentationPreventsClosure(string field)
    {
        var seed = await SeedAsync(completed: true, withReports: true); await using var provider = seed.Provider;
        await using (var historical = provider.CreateAsyncScope())
        {
            var db = historical.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            var report = await db.Reports.FirstAsync();
            // Simula un reporte histórico incompleto sin abrir setters del dominio.
            db.Entry(report).Property<IReadOnlyList<string>>(field).CurrentValue = Array.Empty<string>();
            await db.SaveChangesAsync();
        }
        await using var scope = provider.CreateAsyncScope();
        var monitoring = await scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>().GetMonitoringAsync(seed.Id, default);
        Assert.False(monitoring.Summary.CanFinalize);
        await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<ProjectClosureService>()
            .CloseAsync(seed.Id, monitoring.Project.Version, "responsable", default));
        var context = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Null((await context.Projects.SingleAsync()).ClosedAt);
        Assert.Empty(await context.AuditEntries.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentFinalizationsCreateOnlyOneClosureAndOneAuditEvent()
    {
        var seed = await SeedAsync(completed: true, withReports: true); await using var provider = seed.Provider;
        await using var first = provider.CreateAsyncScope(); await using var second = provider.CreateAsyncScope();
        await first.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(seed.Id, default);
        await second.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(seed.Id, default);
        var winner = await first.ServiceProvider.GetRequiredService<ProjectClosureService>().CloseAsync(seed.Id, 5, "responsable-1", default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.ServiceProvider.GetRequiredService<ProjectClosureService>()
            .CloseAsync(seed.Id, 5, "responsable-2", default));
        await using var reload = provider.CreateAsyncScope();
        var report = await reload.ServiceProvider.GetRequiredService<ProjectClosureService>().GetFinalReportAsync(seed.Id, default);
        Assert.Equal(winner.ClosedAt, report.ClosedAt);
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task AFinalizedProjectCannotReceiveAnotherReportEvenFromAStaleRead()
    {
        var seed = await SeedAsync(completed: true, withReports: true); await using var provider = seed.Provider;
        await using var closing = provider.CreateAsyncScope(); await using var reporting = provider.CreateAsyncScope();
        await closing.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(seed.Id, default);
        var before = await reporting.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(seed.Id, default);
        await closing.ServiceProvider.GetRequiredService<ProjectClosureService>().CloseAsync(seed.Id, 5, "responsable", default);
        await Assert.ThrowsAsync<DomainException>(() => reporting.ServiceProvider.GetRequiredService<ProjectMonitoringService>()
            .ReportAsync(seed.Id, before!.Activities.First().Id,
                new RegisterProgressRequest(ActivityStatus.Completed, 10m, ["foto"], ["acta"], 5), "operador", default));
        await using var check = provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(2, await db.Reports.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(0m, (await check.ServiceProvider.GetRequiredService<ProjectClosureService>().GetFinalReportAsync(seed.Id, default)).TotalExecuted);
    }

    public void Dispose() => _database.Dispose();
}
