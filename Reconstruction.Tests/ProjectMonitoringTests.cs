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
using Reconstruction.Application.Messaging;
using Xunit;

namespace Reconstruction.Tests;

public sealed class ProjectMonitoringDomainTests
{
    internal static ReconstructionProject Project(ActivityStatus state = ActivityStatus.Pending)
    {
        var proposal = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 200m, ProposalPriority.Unassigned);
        var project = new ReconstructionProject(proposal);
        project.SaveFormalization(200m, "Aportes", "Responsable", "Contratista",
            [new("Reparar estructura", "Equipo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(5), 100m),
             new("Inspección", "Especialista", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(6), 50m)]);
        project.RegisterSignedContract("contrato://firmado", DateTimeOffset.UtcNow.AddHours(-1), "responsable");
        RestoreHistoricalStatus(project.Activities.First(), state);
        RestoreHistoricalStatus(project.Activities.ElementAt(1), ActivityStatus.Completed);
        return project;
    }

    // Simula la materialización de registros históricos, no la creación de actividades.
    internal static void RestoreHistoricalStatus(ProjectActivity activity, ActivityStatus status) =>
        typeof(ProjectActivity).GetProperty(nameof(ProjectActivity.Status))!.SetValue(activity, status);

    [Theory]
    [InlineData(ActivityStatus.Pending)]
    [InlineData(ActivityStatus.InProgress)]
    [InlineData(ActivityStatus.Completed)]
    public void EveryReportRequiresBothEvidenceAndDocumentation(ActivityStatus status)
    {
        var project = Project(); var activity = project.Activities.First();
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, status, 0m, [], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, status, 0m, ["foto"], [], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, status, 0m, [" "], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, status, 0m, ["foto"], null!, "operador"));
        Assert.Empty(activity.Reports);
        Assert.Equal(ActivityStatus.Pending, activity.Status);
        Assert.Equal(3, project.Version);
    }

    [Fact]
    public void TracksPendingProgressAndFinalReportWithIncrementalCosts()
    {
        var project = Project(); var activity = project.Activities.First();
        project.RegisterProgress(activity.Id, ActivityStatus.Pending, 0m, ["visita"], ["acta-inicial"], "operador");
        project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 30m, ["avance-1"], ["factura-1"], "operador");
        project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 25m, ["avance-2"], ["factura-2"], "operador");
        project.RegisterProgress(activity.Id, ActivityStatus.Completed, 45m, ["final"], ["acta-final"], "operador");
        Assert.Equal([0m, 30m, 25m, 45m], activity.Reports.Select(r => r.ActualCost));
        Assert.Equal(100m, activity.GetActualCost());
        Assert.True(activity.IsCompleteWithEvidence);
        Assert.Equal(200m, project.FinalBudget);
        Assert.Equal(ProjectStatus.Monitoring, project.Status);
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 0m, ["foto"], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador"));
        Assert.Equal(4, activity.Reports.Count);
    }

    [Fact]
    public void ACompletedActivityFromTheScheduleStillNeedsItsFinalReport()
    {
        var project = Project(ActivityStatus.Completed); var activity = project.Activities.First();
        Assert.False(activity.IsCompleteWithEvidence);
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 0m, ["foto"], ["acta"], "operador"));
        project.RegisterProgress(activity.Id, ActivityStatus.Completed, 80m, ["final"], ["acta-final"], "operador");
        Assert.True(activity.IsCompleteWithEvidence);
        Assert.Equal(80m, activity.GetActualCost());
    }

    [Fact]
    public void RejectsInvalidStatesNegativeOrExcessiveCostsAndBackwardsProgress()
    {
        var project = Project(); var activity = project.Activities.First();
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, (ActivityStatus)99, 0m, ["foto"], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.InProgress, -1m, ["foto"], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 101m, ["foto"], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(Guid.NewGuid(), ActivityStatus.InProgress, 0m, ["foto"], ["acta"], "operador"));
        project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 70m, ["foto"], ["acta"], "operador");
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.Pending, 0m, ["foto"], ["acta"], "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(activity.Id, ActivityStatus.Completed, 31m, ["foto"], ["acta"], "operador"));
        Assert.Single(activity.Reports);
        Assert.Equal(70m, activity.GetActualCost());
    }

    [Fact]
    public void MoneyRemainsDecimalAndReferencesAreSavedAsSnapshots()
    {
        var project = Project(); var activity = project.Activities.First();
        string[] evidence = [" foto-1 "], documents = [" documento-1 "];
        project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 0.10m, evidence, documents, " operador ");
        evidence[0] = "cambiada"; documents[0] = "cambiado";
        project.RegisterProgress(activity.Id, ActivityStatus.Completed, 0.20m, ["foto-final"], ["acta-final"], "operador");
        Assert.Equal(0.30m, activity.GetActualCost());
        Assert.Equal(["foto-1"], activity.Reports.First().GetEvidence());
        Assert.Equal(["documento-1"], activity.Reports.First().GetDocuments());
        Assert.Equal("operador", activity.Reports.First().Actor);
    }

    [Fact]
    public void ReportsAreNotAllowedBeforeMonitoringOrAfterProjectClosure()
    {
        var proposal = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 100m, ProposalPriority.Unassigned);
        var draft = new ReconstructionProject(proposal);
        Assert.Throws<DomainException>(() => draft.RegisterProgress(Guid.NewGuid(), ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador"));
        var project = Project();
        foreach (var activity in project.Activities)
            project.RegisterProgress(activity.Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador");
        project.Close();
        Assert.Throws<DomainException>(() => project.RegisterProgress(project.Activities.First().Id, ActivityStatus.Completed, 0m, ["foto"], ["acta"], "operador"));
    }
}

public sealed class ProjectMonitoringPersistenceTests : IDisposable
{
    private readonly PostgresDatabase _database = new();

    private async Task<(ServiceProvider Provider, Guid Id)> SeedAsync()
    {
        var services = new ServiceCollection().AddPlatformDal(_database.ConnectionString);
        services.AddSingleton<IReportAttachmentVerifier, TestAttachments>();
        services.AddSingleton<IReportAttachmentVerifier, TestAttachments>().AddScoped<ProjectMonitoringService>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.InitializePlatformDalAsync();
        var project = ProjectMonitoringDomainTests.Project();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProjectRepository>().Add(project);
        await scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>().SaveChangesAsync();
        return (provider, project.Id);
    }

    private static RegisterProgressRequest Report(ActivityStatus status, decimal cost, long version) => new(
        status, cost, ["foto://avance"], ["documento://avance"], version);

    [Fact]
    public async Task PersistsChronologicalHistoryAndEvaluatesProgressFromFinalReports()
    {
        var seed = await SeedAsync(); await using var provider = seed.Provider;
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>();
        var initial = await service.GetMonitoringAsync(seed.Id, default);
        Assert.Equal(0m, initial.Summary.ProgressPercentage);
        Assert.Equal(1, initial.Summary.CompletedActivities);
        Assert.Equal(0, initial.Summary.ResolvedActivities);
        var first = initial.Project.Activities.Single(a => a.Name == "Reparar estructura");
        var updated = await service.ReportAsync(seed.Id, first.Id, Report(ActivityStatus.InProgress, 20m, initial.Project.Version), "operador", default);
        updated = await service.ReportAsync(seed.Id, first.Id, Report(ActivityStatus.Completed, 30m, updated.Version), "operador", default);
        var halfway = await service.GetMonitoringAsync(seed.Id, default);
        Assert.Equal(50m, halfway.Summary.ProgressPercentage);
        Assert.Equal(50m, halfway.Summary.TotalCost);
        Assert.Equal(150m, halfway.Summary.RemainingBudget);
        var other = updated.Activities.Single(a => a.Name == "Inspección");
        await service.ReportAsync(seed.Id, other.Id, Report(ActivityStatus.Completed, 0m, updated.Version), "inspector", default);
        await using var reload = provider.CreateAsyncScope();
        var monitoring = await reload.ServiceProvider.GetRequiredService<ProjectMonitoringService>().GetMonitoringAsync(seed.Id, default);
        Assert.Equal(100m, monitoring.Summary.ProgressPercentage);
        Assert.Equal(2, monitoring.Summary.ResolvedActivities);
        Assert.Equal(3, monitoring.Summary.ReportsCount);
        Assert.Equal(ProjectStatus.Monitoring, monitoring.Project.Status);
        Assert.Equal(200m, monitoring.Project.FinalBudget);
        var reports = monitoring.Project.Activities.Single(a => a.Id == first.Id).Reports;
        Assert.Equal([20m, 30m], reports.Select(r => r.ActualCost));
        Assert.Equal(["foto://avance"], reports[0].Evidence);
        Assert.Equal(["documento://avance"], reports[0].Documents);
        Assert.Equal("operador", reports[0].Actor);
        Assert.True(reports[0].RegisteredAt <= reports[1].RegisteredAt);
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(3, await db.AuditEntries.CountAsync());
        Assert.Equal(3, await db.OutboxMessages.CountAsync());
        Assert.True((await db.AuditEntries.ToListAsync()).All(a => a.Detail.Contains("reporte") && a.Detail.Contains("costo")));
    }

    [Fact]
    public async Task StaleVersionAndIncompleteReportsDoNotChangeCostsOrHistory()
    {
        var seed = await SeedAsync(); await using var provider = seed.Provider;
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>();
        var initial = await service.GetMonitoringAsync(seed.Id, default);
        var activity = initial.Project.Activities.Single(a => a.Status == ActivityStatus.Pending);
        await Assert.ThrowsAsync<DomainException>(() => service.ReportAsync(seed.Id, activity.Id,
            new RegisterProgressRequest(ActivityStatus.InProgress, 25m, [], ["acta"], initial.Project.Version), "operador", default));
        await service.ReportAsync(seed.Id, activity.Id, Report(ActivityStatus.InProgress, 25m, initial.Project.Version), "operador", default);
        await Assert.ThrowsAsync<BusinessConflictException>(() => service.ReportAsync(seed.Id, activity.Id,
            Report(ActivityStatus.Completed, 25m, initial.Project.Version), "operador", default));
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1, await db.Reports.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ConcurrentReportsCannotExceedBudgetAndLosingReportRollsBackCompletely()
    {
        var seed = await SeedAsync(); await using var provider = seed.Provider;
        await using var first = provider.CreateAsyncScope(); await using var second = provider.CreateAsyncScope();
        var a = await first.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(seed.Id, default);
        var b = await second.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(seed.Id, default);
        var activity = a!.Activities.Single(x => x.Status == ActivityStatus.Pending);
        // Both requests have read and changed their aggregate before either commits.
        a.RegisterProgress(activity.Id, ActivityStatus.InProgress, 70m, ["foto-1"], ["acta-1"], "operador-1");
        b!.RegisterProgress(activity.Id, ActivityStatus.InProgress, 70m, ["foto-2"], ["acta-2"], "operador-2");
        var evt = IntegrationEvent.Typed(1, seed.Id, "operador", new ActivityProgressEvent(seed.Id,
            activity.Id, activity.Reports.Last().Id, 70m, ActivityStatus.InProgress, a.Version));
        await first.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(evt, default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>()
            .CommitAsync(evt, default));
        await using var check = provider.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1, await db.Reports.CountAsync());
        Assert.Equal(70m, (await db.Reports.SingleAsync()).ActualCost);
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(200m, (await db.Projects.SingleAsync()).FinalBudget);
    }

    [Fact]
    public async Task RetryingTheSameRequestDoesNotDuplicateCostEvenAfterRefreshingVersion()
    {
        var seed = await SeedAsync(); await using var provider = seed.Provider;
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProjectMonitoringService>();
        var project = await service.FindAsync(seed.Id, default);
        var request = new RegisterProgressRequest(ActivityStatus.InProgress, 10m, ["foto"], ["acta"], project.Version);
        var first = await service.ReportAsync(seed.Id, project.Activities.First().Id, request, "operador", default);
        await service.ReportAsync(seed.Id, project.Activities.First().Id, request, "operador", default);
        await service.ReportAsync(seed.Id, project.Activities.First().Id, request with { ExpectedVersion = first.Version }, "operador", default);
        Assert.Equal(10m, (await service.GetMonitoringAsync(seed.Id, default)).Summary.TotalCost);
        Assert.Single((await service.FindAsync(seed.Id, default)).Activities.First().Reports);
        await Assert.ThrowsAsync<BusinessConflictException>(() => service.ReportAsync(seed.Id, project.Activities.First().Id,
            request with { ActualCost = 20m, ExpectedVersion = first.Version }, "operador", default));
    }

    public void Dispose() => _database.Dispose();
}
