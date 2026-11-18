using DisasterLocation.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL;
using Platform.DAL.Contexts;
using Reconstruction.Application;
using Reconstruction.Application.Contracts;
using Reconstruction.Application.Messaging;
using System.Text.Json;
using Reconstruction.Application.Ports;
using Reconstruction.Application.Services;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Domain.Policies;
using Reconstruction.Infrastructure.ExternalAdapters;
using Xunit;

namespace Reconstruction.Tests;

public sealed class ProposalPolicyTests
{
    private readonly WeightedProposalPolicy _policy = new();

    internal static DamageAssessment Evaluation(DamageLevel level = DamageLevel.Severe, decimal budget = 10m) => new(
        Guid.NewGuid(), 1, DateTimeOffset.Parse("2026-11-13T12:00:00Z"), "Evaluador", "Organización",
        "Zona Norte", "Vivienda", level, "Daños en muros", ["foto://1", "documento://1"], EvaluationStatus.Validated, budget);

    [Fact]
    public void LatestAssetRevisionControlsEligibilityWithoutFallingBackToAnOlderValidation()
    {
        var older = Evaluation() with { AssetId = "bridge-1", Revision = 1 };
        var latest = Evaluation() with { AssetId = "bridge-1", Revision = 2, Status = EvaluationStatus.Draft };
        Assert.Empty(_policy.Generate(1, [older, latest]));
        Assert.Empty(_policy.Generate(1, [older, latest with { Status = EvaluationStatus.Rejected }]));
        var proposal = Assert.Single(_policy.Generate(1, [older, latest with { Status = EvaluationStatus.Validated }]));
        Assert.Equal(2, Assert.Single(proposal.GetEvaluations()).Revision);
    }

    [Fact]
    public void GroupsDifferentSeveritiesByZoneAndAssetAndUsesTheirAverage()
    {
        var result = _policy.Generate(1, [Evaluation(DamageLevel.Minor), Evaluation(DamageLevel.Destroyed),
            Evaluation(DamageLevel.Moderate) with { Location = "Zona Sur" }], 90m);
        Assert.Equal(2, result.Count);
        var mixed = result.Single(p => p.AssessmentCount == 2);
        Assert.Equal(2.5m, mixed.AverageSeverity);
        Assert.Equal(50m, mixed.ProposedBudget);
        Assert.Equal(90m, result.Sum(p => p.ProposedBudget));
    }

    [Fact]
    public void AllocatesTenTwentyThirtyFortyAndDoesNotUseExternalCostEstimates()
    {
        var result = _policy.Generate(1, Enumerable.Range(1, 4).Select(level =>
            Evaluation((DamageLevel)level, 999999m) with { Location = $"Zona {level}" }).ToArray(), 100m);
        Assert.Equal(new[] { 40m, 30m, 20m, 10m }, result.Select(p => p.ProposedBudget));
        Assert.Equal(100m, result.Sum(p => p.ProposedBudget));
    }

    [Fact]
    public void EqualProjectsShareTheBudgetAndRoundingConservesEveryCent()
    {
        var evaluations = Enumerable.Range(1, 3).Select(i => Evaluation() with { Location = $"Zona {i}" }).ToArray();
        var result = _policy.Generate(1, evaluations, 1m);
        Assert.Equal(1m, result.Sum(p => p.ProposedBudget));
        Assert.Equal(new[] { .34m, .33m, .33m }, result.Select(p => p.ProposedBudget));
        Assert.Equal(result.Select(p => (p.Zone, p.ProposedBudget)),
            _policy.Generate(1, evaluations.Reverse().ToArray(), 1m).Select(p => (p.Zone, p.ProposedBudget)));
    }

    [Fact]
    public void ExcludesEvaluationsWithNoDamage()
    {
        Assert.Empty(_policy.Generate(1, [Evaluation(DamageLevel.None)]));
        var proposals = _policy.Generate(1, [Evaluation(DamageLevel.None, 1000m), Evaluation(DamageLevel.Minor)]);
        Assert.Equal(DamageLevel.Minor, Assert.Single(proposals).Damage);
    }

    [Theory]
    [InlineData(5)]
    public void RejectsLevelsOutsideTheFourDefinedLevels(int level) =>
        Assert.Throws<DomainException>(() => _policy.Generate(1, [Evaluation((DamageLevel)level)]));

    [Fact]
    public void RejectsNegativeBudgetAndCrossDisasterData()
    {
        Assert.Throws<DomainException>(() => _policy.Generate(1, [Evaluation(budget: -1m)]));
        Assert.Throws<DomainException>(() => _policy.Generate(1, [Evaluation() with { DisasterId = 2 }]));
    }

    [Fact]
    public void RejectsDuplicateEvaluationIdsAndIncompleteValidatedData()
    {
        var evaluation = Evaluation();
        Assert.Throws<DomainException>(() => _policy.Generate(1, [evaluation, evaluation]));
        Assert.Throws<DomainException>(() => _policy.Generate(1, [evaluation with { Evaluator = " " }]));
        Assert.Throws<DomainException>(() => _policy.Generate(1, [evaluation with { Evidence = [" "] }]));
        Assert.Throws<DomainException>(() => _policy.Generate(1, [evaluation with { Status = (EvaluationStatus)99 }]));
    }
}

public sealed class ProposalDefinitionTests : IDisposable
{
    private readonly PostgresDatabase _database = new();

    private async Task<ServiceProvider> ServicesAsync(bool disasterExists = true, int severity = 4,
        DisasterPhase phase = DisasterPhase.Recovery,
        IDamageAssessmentReader? assessments = null)
    {
        var services = new ServiceCollection().AddPlatformDal(_database.ConnectionString);
        services.AddSingleton<IDisasterReader>(new StubDisasterReader(disasterExists, severity, phase));
        services.AddSingleton<IDamageAssessmentReader>(assessments ?? new DemoDamageAssessmentReader());
        services.AddSingleton<IProposalPolicy, WeightedProposalPolicy>();
        services.AddScoped<ProposalDefinitionService>();
        services.AddScoped<ProposalReconciliationService>();
        services.AddScoped<ProjectFormalizationService>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<FormalizationEventHandler>();
        // La financiación se consulta en formalización; las propuestas usan el presupuesto de evaluación.
        services.AddSingleton<IFundingReader, DemoFundingReader>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await provider.InitializePlatformDalAsync();
        return provider;
    }

    [Fact]
    public async Task GeneratesAndListsPersistedProposalsWithEvaluationBudgetsAndNoDuplicates()
    {
        await using var services = await ServicesAsync();
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var generated = await definitions.GenerateAsync(1, "operador", default);
        var repeated = await definitions.GenerateAsync(1, "operador", default);
        Assert.Equal(4, generated.Count);
        Assert.Equal(generated.Select(p => p.Id), repeated.Select(p => p.Id));
        Assert.Equal([DamageLevel.Destroyed, DamageLevel.Severe, DamageLevel.Moderate, DamageLevel.Minor], generated.Select(p => p.Damage));
        Assert.Equal(45_000_000m, generated.Single(p => p.AssetType == "VIVIENDA").ProposedBudget);
        Assert.Equal(5, generated.Sum(p => p.Evaluations.Count));
        await using var reloaded = services.CreateAsyncScope();
        var listed = await reloaded.ServiceProvider.GetRequiredService<ProposalDefinitionService>().ListAsync(1, default);
        Assert.Equal(generated.Select(p => p.Id), listed.Select(p => p.Id));
        Assert.Equal(2, listed.Single(p => p.AssetType == "VIVIENDA").Evaluations.Count);
        Assert.Equal(1, await reloaded.ServiceProvider.GetRequiredService<ReconstructionDbContext>().OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData(DisasterPhase.Detected)]
    [InlineData(DisasterPhase.Registered)]
    [InlineData(DisasterPhase.Assessment)]
    [InlineData(DisasterPhase.Response)]
    [InlineData(DisasterPhase.Closed)]
    public async Task UnsupportedPhasesDoNotReadEvaluationsOrGenerateProposals(DisasterPhase phase)
    {
        var reader = new MutableAssessmentReader { Evaluations = [ProposalPolicyTests.Evaluation()] };
        await using var services = await ServicesAsync(phase: phase, assessments: reader);
        await using var scope = services.CreateAsyncScope();
        await Assert.ThrowsAsync<BusinessConflictException>(() => scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>().GenerateAsync(1, "operador", default));
        Assert.Equal(0, reader.Reads);
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Empty(await db.Proposals.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ReconstructionAllowsProjectsButClosedOnlyPreservesExistingOnes()
    {
        await using var services = await ServicesAsync(phase: DisasterPhase.Reconstruction);
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var proposals = await definitions.GenerateAsync(1, "operador", default);
        var formalization = scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        var project = await formalization.CreateFromProposalAsync(proposals[0].Id, proposals[0].Version, "operador", default);
        ((StubDisasterReader)services.GetRequiredService<IDisasterReader>()).Phase = DisasterPhase.Closed;
        Assert.Equal(project.Id, (await formalization.CreateFromProposalAsync(proposals[0].Id, proposals[0].Version, "operador", default)).Id);
        await Assert.ThrowsAsync<BusinessConflictException>(() => formalization.CreateFromProposalAsync(proposals[1].Id, proposals[1].Version, "operador", default));
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>().Projects.ToListAsync());
        Assert.NotEmpty(await definitions.ListAsync(1, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoEvaluationsOrOnlyNoDamageDoesNotRequireReconstruction(bool hasEvaluation)
    {
        var reader = new MutableAssessmentReader
        {
            Evaluations = hasEvaluation ? [ProposalPolicyTests.Evaluation(DamageLevel.None)] : []
        };
        await using var services = await ServicesAsync(assessments: reader);
        await using var scope = services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>().GenerateAsync(1, "operador", default));
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Empty(await db.Proposals.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task RefreshesAvailableProposalsAndCreatesANewProjectForNewDamageInAnAlreadyUsedGroup()
    {
        var critical = ProposalPolicyTests.Evaluation(DamageLevel.Destroyed, 100m);
        var medium = ProposalPolicyTests.Evaluation(DamageLevel.Moderate, 20m) with { AssetType = "Escuela" };
        var reader = new MutableAssessmentReader { Evaluations = [critical, medium] };
        await using var services = await ServicesAsync(assessments: reader);
        Guid originalProjectId, criticalProposalId, mediumProposalId;
        await using (var scope = services.CreateAsyncScope())
        {
            var generated = await scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>().GenerateAsync(1, "operador", default);
            criticalProposalId = generated[0].Id;
            mediumProposalId = generated[1].Id;
            var project = await scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>().CreateFromProposalAsync(criticalProposalId, 1, "operador", default);
            originalProjectId = project.Id;
        }
        var newCritical = ProposalPolicyTests.Evaluation(DamageLevel.Destroyed, 200m);
        var newMedium = ProposalPolicyTests.Evaluation(DamageLevel.Moderate, 50m) with { AssetType = "Escuela" };
        var otherCritical = ProposalPolicyTests.Evaluation(DamageLevel.Destroyed, 300m) with { Location = "Zona Z" };
        reader.Evaluations = [critical, medium, newCritical, newMedium, otherCritical];
        await using (var scope = services.CreateAsyncScope())
        {
            var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
            var refreshed = await definitions.GenerateAsync(1, "operador", default);
            Assert.Equal(4, refreshed.Count);
            Assert.Equal(150_000_000m, refreshed.Sum(p => p.ProposedBudget));
            Assert.Equal(2, refreshed.Single(p => p.Id == mediumProposalId).AssessmentCount);
            var original = refreshed.Single(p => p.Id == criticalProposalId);
            Assert.Equal(100_000_000m, original.ProposedBudget);
            Assert.Equal(critical.EvaluationId, Assert.Single(original.Evaluations).EvaluationId);
            var newProposal = refreshed.Single(p => p.Evaluations.Any(e => e.EvaluationId == newCritical.EvaluationId));
            Assert.NotEqual(criticalProposalId, newProposal.Id);
            Assert.Equal(newCritical.EvaluationId, Assert.Single(newProposal.Evaluations).EvaluationId);
            var repeated = await definitions.GenerateAsync(1, "operador", default);
            var listed = await definitions.ListAsync(1, default);
            Assert.Equal(refreshed.Select(p => p.Id), repeated.Select(p => p.Id));
            Assert.Equal(refreshed.Select(p => p.Id), listed.Select(p => p.Id));
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            Assert.Equal(3, await db.OutboxMessages.CountAsync());
            var secondProject = await scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>().CreateFromProposalAsync(newProposal.Id, newProposal.Version, "operador", default);
            Assert.NotEqual(originalProjectId, secondProject.Id);
            Assert.Equal(2, await db.Projects.CountAsync());
            Assert.Equal(4, await db.Proposals.CountAsync());
        }
        // Initialization must recognize the new partial index on a populated DB.
        await services.InitializePlatformDalAsync();
        await using var reload = services.CreateAsyncScope();
        var originalProject = await reload.ServiceProvider.GetRequiredService<IProjectRepository>().FindAsync(originalProjectId, default);
        Assert.Equal(ProjectStatus.Draft, originalProject!.Status);
        Assert.Equal(1, originalProject.Version);
    }

    [Fact]
    public async Task RefreshDoesNotAlterProposalsWithProjects()
    {
        var evaluation = ProposalPolicyTests.Evaluation();
        var reader = new MutableAssessmentReader { Evaluations = [evaluation] };
        await using var services = await ServicesAsync(assessments: reader);
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var original = Assert.Single(await definitions.GenerateAsync(1, "operador", default));
        await scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>().CreateFromProposalAsync(original.Id, 1, "operador", default);
        var newEvaluation = ProposalPolicyTests.Evaluation(budget: 30m);
        reader.Evaluations = [evaluation with { Budget = 999m }, newEvaluation];
        var refreshed = await definitions.GenerateAsync(1, "operador", default);
        var selected = refreshed.Single(p => p.Id == original.Id);
        Assert.Equal(ProposalStatus.Selected, selected.Status);
        Assert.Equal(original.ProposedBudget, selected.ProposedBudget);
        var available = Assert.Single(refreshed, p => p.Status == ProposalStatus.Proposed);
        Assert.Equal(0m, available.ProposedBudget);
        Assert.Equal(newEvaluation.EvaluationId, Assert.Single(available.Evaluations).EvaluationId);
    }

    private static IReadOnlyList<DamageAssessment> Changed(DamageAssessment evaluation, string change) => change switch
    {
        "removed" => [],
        "severity" => [evaluation with { DamageLevel = DamageLevel.Destroyed }],
        "zone" => [evaluation with { Location = "Zona Sur" }],
        "revoked" => [evaluation with { Status = EvaluationStatus.Rejected }],
        _ => throw new ArgumentException(change)
    };

    [Theory]
    [InlineData("removed")]
    [InlineData("severity")]
    [InlineData("zone")]
    [InlineData("revoked")]
    public async Task ReconciliationInvalidatesDisappearedGroupsAndRemovesTheirReviewTask(string change)
    {
        var evaluation = ProposalPolicyTests.Evaluation();
        var reader = new MutableAssessmentReader { Evaluations = [evaluation] };
        await using var services = await ServicesAsync(assessments: reader);
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var original = Assert.Single(await definitions.GenerateAsync(1, "operador", default));
        var handler = scope.ServiceProvider.GetRequiredService<FormalizationEventHandler>();
        var generatedEvent = Envelope((await db.OutboxMessages.SingleAsync()).PayloadJson);
        await handler.HandleAsync(generatedEvent, default);
        reader.Evaluations = Changed(evaluation, change);
        var refreshed = await definitions.GenerateAsync(1, "operador", default);
        var invalidation = Envelope((await db.OutboxMessages.SingleAsync(x => x.Id != generatedEvent.Id)).PayloadJson);
        await handler.HandleAsync(invalidation, default);
        await definitions.GenerateAsync(1, "operador", default);

        await using var reload = services.CreateAsyncScope();
        var persisted = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var obsolete = await persisted.Proposals.SingleAsync(p => p.Id == original.Id);
        if (change == "severity")
        {
            Assert.Equal(ProposalStatus.Proposed, obsolete.Status);
            Assert.Equal(reader.Evaluations[0].DamageLevel, obsolete.Damage);
            Assert.True((await persisted.WorkflowItems.SingleAsync(x => x.SubjectId == original.Id)).IsActive);
            return;
        }
        Assert.Equal(ProposalStatus.Obsolete, obsolete.Status);
        Assert.Equal(evaluation.EvaluationId, Assert.Single(obsolete.GetEvaluations()).EvaluationId);
        var listed = await reload.ServiceProvider.GetRequiredService<ProposalDefinitionService>().ListAsync(1, default);
        Assert.DoesNotContain(listed, p => p.Id == original.Id);
        Assert.Equal(refreshed.Select(p => p.Id), listed.Select(p => p.Id));
        if (change is "severity" or "zone")
        {
            var replacement = Assert.Single(listed);
            Assert.Equal(reader.Evaluations[0].DamageLevel, replacement.Damage);
            Assert.Equal(reader.Evaluations[0].Location.ToUpperInvariant(), replacement.Zone);
        }
        else Assert.Empty(listed);
        Assert.False((await persisted.WorkflowItems.SingleAsync(x => x.SubjectId == original.Id)).IsActive);
        Assert.Equal(2, await persisted.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("severity")]
    [InlineData("zone")]
    [InlineData("revoked")]
    public async Task CreationChecksFreshAssessmentsWithoutRequiringAPreviousRefresh(string change)
    {
        var evaluation = ProposalPolicyTests.Evaluation();
        var reader = new MutableAssessmentReader { Evaluations = [evaluation] };
        await using var services = await ServicesAsync(assessments: reader);
        Guid proposalId;
        await using (var scope = services.CreateAsyncScope())
            proposalId = Assert.Single(await scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>()
                .GenerateAsync(1, "operador", default)).Id;
        reader.Evaluations = Changed(evaluation, change);
        await using (var scope = services.CreateAsyncScope())
            await Assert.ThrowsAsync<BusinessConflictException>(() => scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>()
                .CreateFromProposalAsync(proposalId, 1, "operador", default));
        await using var reload = services.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(change == "severity" ? ProposalStatus.Proposed : ProposalStatus.Obsolete, (await db.Proposals.SingleAsync(p => p.Id == proposalId)).Status);
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.DoesNotContain(await db.OutboxMessages.ToListAsync(), e => e.EventType == "reconstruction.proposals.selected");
        Assert.Equal(2, reader.Reads);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("severity")]
    [InlineData("zone")]
    [InlineData("revoked")]
    public async Task ChangedProjectEvaluationsKeepHistoryAndProduceAnIdempotentReviewRequest(string change)
    {
        var evaluation = ProposalPolicyTests.Evaluation();
        var reader = new MutableAssessmentReader { Evaluations = [evaluation] };
        await using var services = await ServicesAsync(assessments: reader);
        Guid projectId, proposalId;
        IReadOnlyList<DamageAssessment> originalSnapshot;
        await using (var scope = services.CreateAsyncScope())
        {
            var proposal = Assert.Single(await scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>()
                .GenerateAsync(1, "operador", default));
            proposalId = proposal.Id;
            projectId = (await scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>()
                .CreateFromProposalAsync(proposalId, 1, "operador", default)).Id;
            originalSnapshot = (await scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>().Proposals.SingleAsync()).Evaluations;
        }
        reader.Evaluations = Changed(evaluation, change);
        EventEnvelope reviewEvent;
        await using (var scope = services.CreateAsyncScope())
        {
            var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
            var generated = await definitions.GenerateAsync(1, "operador", default);
            Assert.Equal(ProposalStatus.Selected, Assert.Single(generated).Status);
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            reviewEvent = (await db.OutboxMessages.Where(x => x.EventType == "reconstruction.proposals.generated").ToListAsync())
                .Select(x => Envelope(x.PayloadJson)).Single(x => x.Read<ProposalsGeneratedEvent>()!.ProjectReviews?.Any(r => r.RequiresReview) == true);
            var review = Assert.Single(reviewEvent.Read<ProposalsGeneratedEvent>()!.ProjectReviews!);
            Assert.Equal(projectId, review.ProjectId);
            Assert.Equal(evaluation.EvaluationId, Assert.Single(review.OriginalEvaluations).EvaluationId);
            Assert.Equal(reader.Evaluations.Select(e => (e.EvaluationId, e.Location, e.DamageLevel, e.Status)),
                review.CurrentEvaluations.Select(e => (e.EvaluationId, e.Location, e.DamageLevel, e.Status)));
            var handler = scope.ServiceProvider.GetRequiredService<FormalizationEventHandler>();
            await handler.HandleAsync(reviewEvent, default);
            await handler.HandleAsync(reviewEvent, default);
            await definitions.GenerateAsync(1, "operador", default);
            Assert.Equal(3, await db.OutboxMessages.CountAsync());
        }
        await using (var reload = services.CreateAsyncScope())
        {
            var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            var historical = await db.Proposals.SingleAsync();
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(originalSnapshot), System.Text.Json.JsonSerializer.Serialize(historical.Evaluations));
            Assert.Equal((ProposalStatus.Selected, evaluation.Location.ToUpperInvariant(), evaluation.DamageLevel, 150_000_000m, 1),
                (historical.Status, historical.Zone, historical.Damage, historical.ProposedBudget, historical.AssessmentCount));
            var project = await db.Projects.SingleAsync();
            Assert.Equal((projectId, proposalId, ProjectStatus.Draft, 1L), (project.Id, project.ProposalId, project.Status, project.Version));
            var notice = Assert.Single(await reload.ServiceProvider.GetRequiredService<WorkflowService>().ListAsync(1, projectId, default));
            Assert.Equal(WorkflowKind.ProjectReviewRequested, notice.Kind);
            Assert.True(notice.Data.GetProperty("requiresReview").GetBoolean());
        }
        // Restaurar la evaluación original resuelve la solicitud; un evento atrasado no la reabre.
        reader.Evaluations = [evaluation];
        await using var restored = services.CreateAsyncScope();
        await restored.ServiceProvider.GetRequiredService<ProposalDefinitionService>().GenerateAsync(1, "operador", default);
        var context = restored.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var resolved = (await context.OutboxMessages.Where(x => x.EventType == "reconstruction.proposals.generated").ToListAsync())
            .Select(x => Envelope(x.PayloadJson)).Single(x => x.Read<ProposalsGeneratedEvent>()!.ProjectReviews?.Any(r => !r.RequiresReview) == true);
        var consumer = restored.ServiceProvider.GetRequiredService<FormalizationEventHandler>();
        await consumer.HandleAsync(resolved, default);
        await consumer.HandleAsync(reviewEvent with { Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow }, default);
        Assert.Empty(await restored.ServiceProvider.GetRequiredService<WorkflowService>().ListAsync(1, projectId, default));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(originalSnapshot), System.Text.Json.JsonSerializer.Serialize((await context.Proposals.SingleAsync()).Evaluations));
    }

    [Fact]
    public async Task RemovingOneEvaluationRefreshesTheRemainingGroupAndBudget()
    {
        var first = ProposalPolicyTests.Evaluation(budget: 10m);
        var second = ProposalPolicyTests.Evaluation(budget: 20m);
        var reader = new MutableAssessmentReader { Evaluations = [first, second] };
        await using var services = await ServicesAsync(assessments: reader);
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var original = Assert.Single(await definitions.GenerateAsync(1, "operador", default));
        reader.Evaluations = [second];
        var updated = Assert.Single(await definitions.GenerateAsync(1, "operador", default));
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal((ProposalStatus.Proposed, 1, 150_000_000m), (updated.Status, updated.AssessmentCount, updated.ProposedBudget));
        Assert.Equal(second.EvaluationId, Assert.Single(updated.Evaluations).EvaluationId);
    }

    private static EventEnvelope Envelope(string json) => JsonSerializer.Deserialize<EventEnvelope>(json)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationRejectsAChangedProposalVersionIncludingChangesDuringReconciliation(bool refreshFirst)
    {
        var evaluation = ProposalPolicyTests.Evaluation();
        var reader = new MutableAssessmentReader { Evaluations = [evaluation] };
        await using var services = await ServicesAsync(assessments: reader);
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var original = Assert.Single(await definitions.GenerateAsync(1, "operador", default));
        reader.Evaluations = [evaluation with { Budget = 50m }];
        if (refreshFirst) await definitions.GenerateAsync(1, "operador", default);
        var formalization = scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        await Assert.ThrowsAsync<BusinessConflictException>(() => formalization.CreateFromProposalAsync(
            original.Id, original.Version, "operador", default));
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Empty(await db.Projects.ToListAsync());
        var latest = Assert.Single(await definitions.ListAsync(1, default));
        Assert.Equal(2, latest.Version);
        Assert.Equal(150_000_000m, latest.ProposedBudget);
        var project = await formalization.CreateFromProposalAsync(latest.Id, latest.Version, "operador", default);
        Assert.Equal(3, (await db.Proposals.SingleAsync()).Version);
        Assert.Equal(project.Id, (await formalization.CreateFromProposalAsync(original.Id, original.Version, "operador", default)).Id);
    }

    [Fact]
    public async Task EachProposalCreatesItsOwnProjectWithIndependentFormalization()
    {
        await using var services = await ServicesAsync();
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var proposals = await definitions.GenerateAsync(1, "operador", default);
        var formalization = scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        var first = await formalization.CreateFromProposalAsync(proposals[0].Id, 1, "operador", default);
        var second = await formalization.CreateFromProposalAsync(proposals[1].Id, 1, "operador", default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, (await formalization.CreateFromProposalAsync(proposals[0].Id, 1, "operador", default)).Id);
        await formalization.FormalizeAsync(first.Id, new FormalizeProjectRequest(200m, "DEMO: financiación simulada, no asignada", "Responsable A", "Contratista A",
            [ProjectFormalizationDomainTests.Activity()], first.Version), "operador", default);
        await formalization.FormalizeAsync(second.Id, new FormalizeProjectRequest(300m, "DEMO: financiación simulada, no asignada", "Responsable B", "Contratista B",
            [ProjectFormalizationDomainTests.Activity()], second.Version), "operador", default);
        await using var reload = services.CreateAsyncScope();
        var db = reload.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(2, await db.Proposals.CountAsync(p => p.Status == ProposalStatus.Selected));
        Assert.Equal(2, await db.Proposals.CountAsync(p => p.Status == ProposalStatus.Proposed));
        var saved = await reload.ServiceProvider.GetRequiredService<IProjectRepository>().ListAsync(1, default);
        Assert.Equal(2, saved.Count);
        var savedFirst = saved.Single(p => p.Id == first.Id);
        var savedSecond = saved.Single(p => p.Id == second.Id);
        Assert.Equal((proposals[0].Id, 200m, "DEMO: financiación simulada, no asignada", "Responsable A", "Contratista A"),
            (savedFirst.ProposalId, savedFirst.FinalBudget, savedFirst.FundingSource, savedFirst.Responsible, savedFirst.Contractor));
        Assert.Equal((proposals[1].Id, 300m, "DEMO: financiación simulada, no asignada", "Responsable B", "Contratista B"),
            (savedSecond.ProposalId, savedSecond.FinalBudget, savedSecond.FundingSource, savedSecond.Responsible, savedSecond.Contractor));
        Assert.Equal(5, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task HistoricalSelectionFlagsDoNotAttachOrBlockAnIndependentProject()
    {
        await using var services = await ServicesAsync();
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        var proposals = await definitions.GenerateAsync(1, "operador", default);
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var formalization = scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        var first = await formalization.CreateFromProposalAsync(proposals[0].Id, 1, "operador", default);
        var other = await db.Proposals.SingleAsync(p => p.Id == proposals[1].Id);
        db.Entry(other).Property<bool>("IsSelectedForReconstruction").CurrentValue = true;
        db.Entry(other).Property<Guid?>("SelectedForProjectId").CurrentValue = first.Id;
        await db.SaveChangesAsync();
        var second = await formalization.CreateFromProposalAsync(other.Id, 1, "operador", default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(other.Id, second.ProposalId);
        Assert.Equal(ProposalStatus.Selected, other.Status);
        Assert.Equal(1, (await db.Projects.SingleAsync(p => p.Id == first.Id)).Version);
        Assert.Equal(2, await db.Projects.CountAsync());
    }

    [Fact]
    public async Task MissingDisasterAndInvalidOrMissingProposalAreRejected()
    {
        await using var services = await ServicesAsync(disasterExists: false);
        await using var scope = services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<ProposalDefinitionService>();
        await Assert.ThrowsAsync<MissingResourceException>(() => definitions.GenerateAsync(1, "operador", default));
        var formalization = scope.ServiceProvider.GetRequiredService<ProjectFormalizationService>();
        await Assert.ThrowsAsync<DomainException>(() => formalization.CreateFromProposalAsync(Guid.Empty, 1, "operador", default));
        await Assert.ThrowsAsync<MissingResourceException>(() => formalization.CreateFromProposalAsync(Guid.NewGuid(), 1, "operador", default));
    }

    public void Dispose() => _database.Dispose();

    private sealed class StubDisasterReader(bool exists, int severity, DisasterPhase phase) : IDisasterReader
    {
        public DisasterPhase Phase { get; set; } = phase;
        public Task<DisasterSnapshot?> GetAsync(int disasterId, CancellationToken ct) =>
            Task.FromResult(exists ? new DisasterSnapshot(disasterId, severity, Phase) : null);
    }

    private sealed class MutableAssessmentReader : IDamageAssessmentReader
    {
        public IReadOnlyList<DamageAssessment> Evaluations { get; set; } = [];
        public int Reads { get; private set; }
        public Task<IReadOnlyList<DamageAssessment>> GetForDisasterAsync(int disasterId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Evaluations);
        }
    }
}
