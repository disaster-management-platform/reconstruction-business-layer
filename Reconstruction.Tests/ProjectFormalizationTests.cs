using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL;
using Platform.DAL.Contexts;
using Reconstruction.Application;
using Reconstruction.Application.Contracts;
using Reconstruction.Application.Ports;
using Reconstruction.Application.Services;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Domain.Policies;
using Xunit;

namespace Reconstruction.Tests;

public sealed class ProjectFormalizationDomainTests
{
    private static ReconstructionProject Project() => new(new ReconstructionProposal(
        1, "Zona", "Puente", DamageLevel.Severe, 1, 150m, ProposalPriority.Unassigned));

    internal static ActivityDefinition Activity(decimal budget = 100m) =>
        new("Reparar estructura", "Equipo especializado", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(5), budget);

    [Fact]
    public void SavesAllFiveComponentsAndAlwaysCreatesPendingActivitiesBeforeSignature()
    {
        var project = Project();
        project.SaveFormalization(300m, " Aportes ", " Autoridad ", " Contratista ",
            [Activity(), Activity(), Activity()]);
        Assert.Equal(300m, project.FinalBudget);
        Assert.Equal("Aportes", project.FundingSource);
        Assert.Equal("Autoridad", project.Responsible);
        Assert.Equal("Contratista", project.Contractor);
        Assert.All(project.Activities, activity => Assert.Equal(ActivityStatus.Pending, activity.Status));
        Assert.Equal(ProjectStatus.Draft, project.Status);
        Assert.NotNull(project.FormalizationSavedAt);
        Assert.False(project.IsBudgetLocked);
        project.SaveFormalization(400m, "Aportes", "Autoridad", "Contratista", [Activity()]);
        Assert.Equal(400m, project.FinalBudget);
        Assert.False(project.IsBudgetLocked);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("InProgress")]
    public void ClientSuppliedStateCannotOverrideTheInitialPendingState(string state)
    {
        var definition = JsonSerializer.Deserialize<ActivityDefinition>($$"""
            {"name":"Reparar","responsible":"Equipo","startsAt":"2026-11-14T00:00:00Z",
             "dueAt":"2026-11-15T00:00:00Z","budget":100,"status":"{{state}}"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var activity = new ProjectActivity(Guid.NewGuid(), definition);
        Assert.Equal(ActivityStatus.Pending, activity.Status);
        Assert.Empty(activity.Reports);
    }

    [Fact]
    public void SignatureRequiresSavedFormalizationAndValidContractData()
    {
        var project = Project();
        Assert.Throws<DomainException>(() => project.RegisterSignedContract("documento-1", DateTimeOffset.UtcNow.AddMinutes(-1), "operador"));
        project.SaveFormalization(200m, "Aportes", "Autoridad", "Contratista", [Activity()]);
        Assert.Throws<DomainException>(() => project.RegisterSignedContract(" ", DateTimeOffset.UtcNow, "operador"));
        Assert.Throws<DomainException>(() => project.RegisterSignedContract("documento-1", default, "operador"));
        Assert.Throws<DomainException>(() => project.RegisterSignedContract("documento-1", DateTimeOffset.UtcNow.AddDays(1), "operador"));
        Assert.Throws<DomainException>(() => project.RegisterProgress(project.Activities.First().Id,
            ActivityStatus.InProgress, 0m, ["foto"], ["acta"], "operador"));
        Assert.False(project.IsBudgetLocked);
        Assert.Equal(ProjectStatus.Draft, project.Status);
    }

    [Fact]
    public void SignatureLocksTheAmountAndMovesAutomaticallyToMonitoring()
    {
        var project = Project();
        project.SaveFormalization(200m, "Aportes", "Autoridad", "Contratista", [Activity()]);
        var signedAt = DateTimeOffset.UtcNow.AddHours(-1);
        Assert.True(project.RegisterSignedContract(" contrato-firmado-1 ", signedAt, "operador"));
        Assert.Equal(ProjectStatus.Monitoring, project.Status);
        Assert.True(project.IsBudgetLocked);
        Assert.Equal(200m, project.FinalBudget);
        Assert.Equal("contrato-firmado-1", project.SignedContractReference);
        Assert.Equal(signedAt, project.ContractSignedAt);
        Assert.Equal("operador", project.ContractRecordedBy);
        Assert.NotNull(project.BudgetLockedAt);
        Assert.False(project.RegisterSignedContract("contrato-firmado-1", signedAt, "operador"));
        Assert.Throws<DomainException>(() => project.SaveFormalization(500m, "Otra fuente", "Otra persona", "Otro contratista", [Activity()]));
        Assert.Throws<DomainException>(() => project.RegisterSignedContract("otro-contrato", signedAt, "operador"));
        Assert.Equal(200m, project.FinalBudget);
        Assert.Equal(3, project.Version);
    }

    [Fact]
    public void RequiresBudgetFundingResponsibleContractorAndSchedule()
    {
        var project = Project();
        Assert.Throws<DomainException>(() => project.SaveFormalization(0m, "Aportes", "Autoridad", "Contratista", [Activity()]));
        Assert.Throws<DomainException>(() => project.SaveFormalization(200m, " ", "Autoridad", "Contratista", [Activity()]));
        Assert.Throws<DomainException>(() => project.SaveFormalization(200m, "Aportes", " ", "Contratista", [Activity()]));
        Assert.Throws<DomainException>(() => project.SaveFormalization(200m, "Aportes", "Autoridad", " ", [Activity()]));
        Assert.Throws<DomainException>(() => project.SaveFormalization(200m, "Aportes", "Autoridad", "Contratista", []));
        Assert.Equal(0m, project.FinalBudget);
        Assert.Null(project.FormalizationSavedAt);
    }

    [Fact]
    public void RejectsInvalidScheduleWithoutChangingTheAgreement()
    {
        var project = Project();
        ActivityDefinition[] invalid = [Activity(budget: -1m),
            Activity() with { Name = " " }, Activity() with { Responsible = " " },
            Activity() with { StartsAt = default }, Activity() with { DueAt = DateTimeOffset.UtcNow.AddDays(-1) }];
        foreach (var activity in invalid)
            Assert.Throws<DomainException>(() => project.SaveFormalization(200m, "Aportes", "Autoridad", "Contratista", [activity]));
        Assert.Throws<DomainException>(() => project.SaveFormalization(50m, "Aportes", "Autoridad", "Contratista", [Activity()]));
        Assert.Empty(project.Activities);
        Assert.Equal(1, project.Version);
    }
}

public sealed class ProjectFormalizationPersistenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "formalization-" + Guid.NewGuid());
    public ProjectFormalizationPersistenceTests() => Directory.CreateDirectory(_folder);

    private async Task<ServiceProvider> ServicesAsync(decimal financing = 1000m)
    {
        var services = new ServiceCollection().AddPlatformDal($"Data Source={Path.Combine(_folder, "disasters.db")};Pooling=False");
        services.AddScoped<ProjectFormalizationService>();
        services.AddScoped<ProposalReconciliationService>();
        services.AddSingleton<IProposalPolicy, WeightedProposalPolicy>();
        services.AddSingleton<AssessmentSource>();
        services.AddSingleton<IDisasterReader>(sp => sp.GetRequiredService<AssessmentSource>());
        services.AddSingleton<IDamageAssessmentReader>(sp => sp.GetRequiredService<AssessmentSource>());
        services.AddSingleton<IFundingReader>(new FixedFundingReader(financing));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.InitializePlatformDalAsync();
        return provider;
    }

    private static async Task<ProjectDto> CreateAsync(IServiceProvider services)
    {
        var evaluation = ProposalPolicyTests.Evaluation(budget: 200m) with { Location = "Zona", AssetType = "Puente" };
        services.GetRequiredService<AssessmentSource>().Evaluations.Add(evaluation);
        var available = await services.GetRequiredService<ProposalReconciliationService>().ReconcileAsync(1, "responsable", default);
        var proposal = available.Single(p => p.Status == ProposalStatus.Proposed && p.Evaluations.Any(e => e.EvaluationId == evaluation.EvaluationId));
        return await services.GetRequiredService<ProjectFormalizationService>().CreateFromProposalAsync(proposal.Id, proposal.Version, "responsable", default);
    }

    private static FormalizeProjectRequest Request(long version, decimal budget = 200m) => new(budget,
        "Donaciones", "Responsable", "Entidad especializada", [ProjectFormalizationDomainTests.Activity(budget: 123.45m)], version);

    [Fact]
    public async Task ReservesAcrossProjectsAndReusesOnlyReleasedBudgetWithoutDoubleReserving()
    {
        await using var provider = await ServicesAsync();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var first = await CreateAsync(services);
        var second = await CreateAsync(services);
        var service = services.GetRequiredService<ProjectFormalizationService>();
        first = await service.FormalizeAsync(first.Id, Request(first.Version, 700m), "operador", default);
        await Assert.ThrowsAsync<DomainException>(() => service.FormalizeAsync(second.Id, Request(second.Version, 400m), "operador", default));
        first = await service.FormalizeAsync(first.Id, Request(first.Version, 700m), "operador", default);
        first = await service.FormalizeAsync(first.Id, Request(first.Version, 600m), "operador", default);
        await service.FormalizeAsync(second.Id, Request(second.Version, 400m), "operador", default);
        await Assert.ThrowsAsync<DomainException>(() => service.FormalizeAsync(first.Id, Request(first.Version, 601m), "operador", default));
        await using var reload = provider.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1000m, (await db.FundingReservations.SingleAsync()).ReservedAmount);
        Assert.Equal(600m, (await db.Projects.SingleAsync(p => p.Id == first.Id)).FinalBudget);
        Assert.Equal(400m, (await db.Projects.SingleAsync(p => p.Id == second.Id)).FinalBudget);
        Assert.Equal(6, await db.OutboxMessages.CountAsync(e => e.EventType != "reconstruction.proposals.generated"));
        Assert.Equal(6, await db.AuditEntries.CountAsync(e => e.Action != "reconstruction.proposals.generated"));
        await provider.InitializePlatformDalAsync();
    }

    [Fact]
    public async Task ConcurrentProjectsCannotReserveTheSameDisasterFunding()
    {
        await using var provider = await ServicesAsync();
        ProjectDto first, second;
        await using (var seed = provider.CreateAsyncScope())
        {
            first = await CreateAsync(seed.ServiceProvider);
            second = await CreateAsync(seed.ServiceProvider);
            var seedDb = seed.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            seedDb.FundingReservations.Add(new DisasterFundingReservation(1, 0m));
            await seedDb.SaveChangesAsync();
        }
        await using var winner = provider.CreateAsyncScope();
        await using var loser = provider.CreateAsyncScope();
        await winner.ServiceProvider.GetRequiredService<IFundingReservationRepository>().FindAsync(1, default);
        await loser.ServiceProvider.GetRequiredService<IFundingReservationRepository>().FindAsync(1, default);
        await winner.ServiceProvider.GetRequiredService<ProjectFormalizationService>()
            .FormalizeAsync(first.Id, Request(first.Version, 700m), "ganador", default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.ServiceProvider.GetRequiredService<ProjectFormalizationService>()
            .FormalizeAsync(second.Id, Request(second.Version, 700m), "otro", default));
        await using var reload = provider.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(700m, (await db.FundingReservations.SingleAsync()).ReservedAmount);
        var unchanged = await db.Projects.Include(p => p.Activities).SingleAsync(p => p.Id == second.Id);
        Assert.Equal(0m, unchanged.FinalBudget);
        Assert.Equal(1, unchanged.Version);
        Assert.Empty(unchanged.Activities);
        Assert.Equal(3, await db.OutboxMessages.CountAsync(e => e.EventType != "reconstruction.proposals.generated"));
        Assert.Equal(3, await db.AuditEntries.CountAsync(e => e.Action != "reconstruction.proposals.generated"));
    }

    [Fact]
    public async Task ExistingBudgetsAreReservedAndInvalidFormalizationDoesNotLeaveAHold()
    {
        await using var provider = await ServicesAsync();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var first = await CreateAsync(services);
        var second = await CreateAsync(services);
        var original = (await services.GetRequiredService<IProjectRepository>().FindAsync(first.Id, default))!;
        original.SaveFormalization(800m, "Histórica", "Responsable", "Contratista", [ProjectFormalizationDomainTests.Activity()]);
        var db = services.GetRequiredService<ReconstructionDbContext>();
        await db.SaveChangesAsync();
        var service = services.GetRequiredService<ProjectFormalizationService>();
        await Assert.ThrowsAsync<DomainException>(() => service.FormalizeAsync(second.Id, Request(second.Version, 300m), "operador", default));
        await Assert.ThrowsAsync<DomainException>(() => service.FormalizeAsync(second.Id,
            Request(second.Version) with { Activities = [] }, "operador", default));
        Assert.Empty(await db.FundingReservations.ToListAsync());
        await service.FormalizeAsync(second.Id, Request(second.Version), "operador", default);
        Assert.Equal(1000m, (await db.FundingReservations.SingleAsync()).ReservedAmount);
        Assert.Equal(800m, original.FinalBudget);
        Assert.Equal(3, await db.OutboxMessages.CountAsync(e => e.EventType != "reconstruction.proposals.generated"));
    }

    [Fact]
    public async Task PersistsScheduleAndContractAndRejectsBudgetChangesAfterReload()
    {
        await using var provider = await ServicesAsync();
        Guid projectId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var project = await CreateAsync(services); projectId = project.Id;
            var formalization = services.GetRequiredService<ProjectFormalizationService>();
            project = await formalization.FormalizeAsync(project.Id, Request(project.Version, 234.56m), "responsable", default);
            Assert.Equal(ProjectStatus.Draft, project.Status);
            project = await formalization.RegisterSignedContractAsync(project.Id,
                new SignedContractRequest("contrato://firmado-1", DateTimeOffset.UtcNow.AddMinutes(-5), project.Version), "responsable", default);
            Assert.Equal(ProjectStatus.Monitoring, project.Status);
        }
        await using var reload = provider.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var saved = await reload.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(projectId, default);
        Assert.True(saved!.IsBudgetLocked);
        Assert.Equal(234.56m, saved.FinalBudget);
        Assert.Equal(123.45m, saved.Activities.Single().Budget);
        Assert.Equal("contrato://firmado-1", saved.SignedContractReference);
        var service = reload.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        await Assert.ThrowsAsync<DomainException>(() => service.FormalizeAsync(projectId, Request(saved.Version, 999m), "responsable", default));
        Assert.Equal(3, await db.AuditEntries.CountAsync(e => e.Action != "reconstruction.proposals.generated"));
        Assert.Equal(3, await db.OutboxMessages.CountAsync(e => e.EventType != "reconstruction.proposals.generated"));
        Assert.Equal(234.56m, (await db.Projects.AsNoTracking().SingleAsync()).FinalBudget);
    }

    [Fact]
    public async Task ConcurrentBudgetEditCannotOverwriteALockedContractAndRollsBackItsAudit()
    {
        await using var provider = await ServicesAsync();
        ProjectDto project;
        await using (var seed = provider.CreateAsyncScope())
        {
            project = await CreateAsync(seed.ServiceProvider);
            project = await seed.ServiceProvider.GetRequiredService<ProjectFormalizationService>()
                .FormalizeAsync(project.Id, Request(project.Version), "responsable", default);
        }
        await using var signing = provider.CreateAsyncScope();
        await using var editing = provider.CreateAsyncScope();
        await signing.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(project.Id, default);
        await editing.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(project.Id, default);
        await signing.ServiceProvider.GetRequiredService<ProjectFormalizationService>().RegisterSignedContractAsync(project.Id,
            new SignedContractRequest("contrato://firmado", DateTimeOffset.UtcNow.AddMinutes(-1), project.Version), "responsable", default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => editing.ServiceProvider.GetRequiredService<ProjectFormalizationService>()
            .FormalizeAsync(project.Id, Request(project.Version, 900m), "otro-operador", default));
        await using var reload = provider.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var saved = await db.Projects.SingleAsync();
        Assert.Equal(200m, saved.FinalBudget);
        Assert.True(saved.IsBudgetLocked);
        Assert.Equal(ProjectStatus.Monitoring, saved.Status);
        Assert.Equal(3, await db.AuditEntries.CountAsync(e => e.Action != "reconstruction.proposals.generated"));
        Assert.Equal(3, await db.OutboxMessages.CountAsync(e => e.EventType != "reconstruction.proposals.generated"));
    }

    [Fact]
    public async Task StaleVersionOrMissingSavedBudgetDoesNotRegisterAContract()
    {
        await using var provider = await ServicesAsync();
        await using var scope = provider.CreateAsyncScope();
        var project = await CreateAsync(scope.ServiceProvider);
        var service = scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        await Assert.ThrowsAsync<DomainException>(() => service.RegisterSignedContractAsync(project.Id,
            new SignedContractRequest("contrato://1", DateTimeOffset.UtcNow.AddMinutes(-1), project.Version), "responsable", default));
        await service.FormalizeAsync(project.Id, Request(project.Version), "responsable", default);
        await Assert.ThrowsAsync<BusinessConflictException>(() => service.RegisterSignedContractAsync(project.Id,
            new SignedContractRequest("contrato://1", DateTimeOffset.UtcNow.AddMinutes(-1), project.Version), "responsable", default));
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(0, await db.Projects.CountAsync(p => p.BudgetLockedAt != null));
        Assert.Equal(2, await db.AuditEntries.CountAsync(e => e.Action != "reconstruction.proposals.generated"));
    }

    [Fact]
    public async Task AuditPreservesThePreviousScheduleAndBudgetCanBeRevisedWithoutAContract()
    {
        await using var provider = await ServicesAsync(); await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var project = await CreateAsync(services);
        var formalization = services.GetRequiredService<ProjectFormalizationService>();
        project = await formalization.FormalizeAsync(project.Id, Request(project.Version, 200m), "operator", default);
        var firstActivity = project.Activities.Single().Id;
        project = await formalization.FormalizeAsync(project.Id, Request(project.Version, 300m), "operator", default);
        var history = await services.GetRequiredService<IReconstructionHistoryReader>().GetAuditAsync(project.Id, default);
        var change = history.Last();
        Assert.Equal(200m, change.Before!.Value.GetProperty("finalBudget").GetDecimal());
        Assert.Equal(firstActivity, change.Before.Value.GetProperty("activities")[0].GetProperty("id").GetGuid());
        project = await formalization.ConfirmAsync(project.Id, project.Version, "operator", default);
        Assert.Equal(ProjectStatus.Monitoring, project.Status);
        Assert.Empty(project.SignedContractReference);
        var activityId = project.Activities.Single().Id;
        project = await formalization.ReviseBudgetAsync(project.Id, new ReviseBudgetRequest(400m, "Nueva asignación", project.Version)
            { ActivityBudgets = new Dictionary<Guid, decimal> { [activityId] = 350m } }, "operator", default);
        Assert.Equal(350m, project.Activities.Single().Budget);
        Assert.Equal(400m, project.FinalBudget);
        Assert.Equal(400m, (await services.GetRequiredService<ReconstructionDbContext>().FundingReservations.SingleAsync()).ReservedAmount);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FixedFundingReader(decimal amount) : IFundingReader
    {
        public Task<FundingAvailability> GetAvailableAsync(int disasterId, CancellationToken ct) =>
            Task.FromResult(new FundingAvailability(disasterId, amount, "Donaciones"));
    }

    private sealed class AssessmentSource : IDisasterReader, IDamageAssessmentReader
    {
        public List<DamageAssessment> Evaluations { get; } = [];
        public Task<DisasterSnapshot?> GetAsync(int disasterId, CancellationToken ct) => Task.FromResult<DisasterSnapshot?>(new(disasterId, 4, Reconstruction.Domain.DisasterPhase.Recovery));
        public Task<IReadOnlyList<DamageAssessment>> GetForDisasterAsync(int disasterId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DamageAssessment>>(Evaluations.Where(e => e.DisasterId == disasterId).ToArray());
    }
}
