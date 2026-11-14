using DisasterLocation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL;
using Platform.DAL.Contexts;
using Reconstruction.Application.Messaging;
using Platform.DAL.Models;
using Platform.DAL.Transactions;
using Reconstruction.Application.Ports;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Xunit;

namespace Reconstruction.Tests;

public sealed class PlatformPersistenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "platform-dal-test-" + Guid.NewGuid());
    private string ConnectionString => $"Data Source={Path.Combine(_folder, "disasters.db")};Pooling=False";
    private static readonly string[] DisasterMigrations =
    ["20261106013444_InitialCreate", "20261106013504_AddOperationId", "20261106013525_UseClientRequestId"];

    public PlatformPersistenceTests() => Directory.CreateDirectory(_folder);

    private ServiceProvider Services() => new ServiceCollection().AddPlatformDal(ConnectionString).AddScoped<TimelineEventHandler>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    [Fact]
    public async Task FreshDatabaseHasBothModelsAndIndependentHistories()
    {
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        await services.InitializePlatformDalAsync();
        await using var scope = services.CreateAsyncScope();
        var disasters = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        var reconstruction = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(disasters.Database.GetConnectionString(), reconstruction.Database.GetConnectionString());
        Assert.Equal(disasters.Database.GetMigrations(), await disasters.Database.GetAppliedMigrationsAsync());
        Assert.Equal(reconstruction.Database.GetMigrations(), await reconstruction.Database.GetAppliedMigrationsAsync());
        Assert.False(disasters.Database.HasPendingModelChanges());
        Assert.False(reconstruction.Database.HasPendingModelChanges());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDisasterRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReconstructionHistoryReader>());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task LegacyDisastersKeepTheirDataAndReceiveOnlyPendingMigrations(int stage, bool retainHistory)
    {
        await using (var legacy = new DisasterDbContext(new DbContextOptionsBuilder<DisasterDbContext>()
            .UseSqlite(ConnectionString).Options))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(DisasterMigrations[stage - 1]);
            await legacy.Database.ExecuteSqlRawAsync("""
                INSERT INTO Desastres(Id, Nombre, Ubicacion, Severidad, Descripcion, Latitud, Longitud)
                VALUES(42, 'Reporte histórico', 'Bogotá', 4, 'Conservar', 4.71, -74.07);
                """);
            if (!retainHistory) await legacy.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory;");
        }
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        await services.InitializePlatformDalAsync();
        await using var scope = services.CreateAsyncScope();
        var disasters = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        var record = await disasters.Disasters.SingleAsync();
        Assert.Equal(42, record.Id);
        Assert.Equal("Reporte histórico", record.Nombre);
        Assert.Equal(disasters.Database.GetMigrations(), await disasters.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task ExistingReconstructionHistoryAndProjectAreAdoptedWithoutRecreation()
    {
        var proposal = new ReconstructionProposal(42, "Zona Norte", "Puente", DamageLevel.Severe, 1, 100m, ProposalPriority.High);
        var project = new ReconstructionProject(proposal);
        proposal.Select();
        await using (var legacy = new ReconstructionDbContext(new DbContextOptionsBuilder<ReconstructionDbContext>()
            .UseSqlite(ConnectionString).Options))
        {
            await legacy.Database.MigrateAsync();
            legacy.Proposals.Add(proposal);
            legacy.Projects.Add(project);
            await new ReconstructionUnitOfWork(legacy).CommitAsync(
                IntegrationEvent.Typed(42, project.Id, "histórico", new ProposalsSelectedEvent(project.Id, proposal.Id, project.Version)), default);
        }
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        await services.InitializePlatformDalAsync();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(project.Id, (await db.Projects.SingleAsync()).Id);
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task ProposalDefinitionMigrationPreservesOriginalProposalData()
    {
        var id = Guid.NewGuid();
        await using (var legacy = new ReconstructionDbContext(new DbContextOptionsBuilder<ReconstructionDbContext>().UseSqlite(ConnectionString).Options))
        {
            // The historical ID has 12 timestamp digits. Apply its unchanged Up
            // operations directly to reproduce that original database faithfully.
            var original = new Platform.DAL.Migrations.Reconstruction.InitialReconstruction();
            foreach (var command in legacy.GetService<IMigrationsSqlGenerator>().Generate(original.UpOperations))
                await legacy.Database.ExecuteSqlRawAsync(command.CommandText);
            await legacy.Database.ExecuteSqlRawAsync("""
                CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
                INSERT INTO __EFMigrationsHistory VALUES('202611130001_InitialReconstruction', '10.0.14');
                """);
            await legacy.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ReconstructionProposals(Id, DisasterId, Zone, AssetType, Damage, AssessmentCount, ProposedBudget, Priority, Status, CreatedAt)
                VALUES({id}, 42, 'Zona histórica', 'Puente', 3, 2, '1234.56', 2, 0, '2026-11-13 12:00:00+00:00');
                """);
        }
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        await services.InitializePlatformDalAsync();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var proposal = await db.Proposals.SingleAsync();
        Assert.Equal(id, proposal.Id);
        Assert.Equal(1234.56m, proposal.ProposedBudget);
        Assert.Equal(2, proposal.AssessmentCount);
        Assert.Equal(ProposalStatus.Proposed, proposal.Status);
        Assert.Empty(proposal.GetEvaluations());
        Assert.Equal(1, proposal.Version);
    }

    [Fact]
    public async Task FormalizationMigrationPreservesHistoricalBudgetsAndDoesNotInventContractData()
    {
        var proposalId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await using (var legacy = new ReconstructionDbContext(new DbContextOptionsBuilder<ReconstructionDbContext>().UseSqlite(ConnectionString).Options))
        {
            await legacy.GetService<IMigrator>().MigrateAsync("20261114030249_DefineReconstructionProposals");
            await legacy.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ReconstructionProposals(Id, DisasterId, Zone, AssetType, Damage, AssessmentCount, ProposedBudget, Priority, Status, CreatedAt)
                VALUES({proposalId}, 42, 'Zona', 'Puente', 3, 1, '9876.54', 2, 1, '2026-11-13 12:00:00+00:00');
                INSERT INTO ReconstructionProjects(Id, ProposalId, DisasterId, Status, FinalBudget, FundingSource, Responsible, Contractor, CreatedAt, ClosedAt, Version)
                VALUES({projectId}, {proposalId}, 42, 2, '9876.54', 'Aportes', 'Autoridad', 'Contratista', '2026-11-13 12:00:00+00:00', NULL, 3);
                """);
        }
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        await services.InitializePlatformDalAsync();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var project = await db.Projects.SingleAsync();
        Assert.Equal(projectId, project.Id);
        Assert.Equal(9876.54m, project.FinalBudget);
        Assert.Equal(ProjectStatus.Monitoring, project.Status);
        Assert.True(project.IsBudgetLocked);
        Assert.Equal(3, project.Version);
        Assert.Null(project.BudgetLockedAt);
        Assert.Null(project.ContractSignedAt);
        Assert.Empty(project.SignedContractReference);
        Assert.Throws<Reconstruction.Domain.DomainException>(() => project.SaveFormalization(9999m, "Otra", "Otra", "Otra", []));
    }

    [Fact]
    public async Task RejectsPartialReconstructionSchemaWithoutReplayingInitialMigration()
    {
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ReconstructionProposals (Id TEXT PRIMARY KEY); INSERT INTO ReconstructionProposals VALUES('preserve');";
            await command.ExecuteNonQueryAsync();
        }
        await using var services = Services();
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.InitializePlatformDalAsync());
        await using var db = new ReconstructionDbContext(new DbContextOptionsBuilder<ReconstructionDbContext>().UseSqlite(ConnectionString).Options);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task InconsistentLegacyHistoryIsRejectedBeforeReplayingARename()
    {
        await using (var legacy = new DisasterDbContext(new DbContextOptionsBuilder<DisasterDbContext>().UseSqlite(ConnectionString).Options))
        {
            await legacy.Database.MigrateAsync();
            await legacy.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory WHERE MigrationId='20261106013525_UseClientRequestId';");
        }
        await using var services = Services();
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.InitializePlatformDalAsync());
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        // The column still exists and no second RENAME was attempted.
        Assert.Empty(await db.Disasters.Select(x => x.ClientRequestId).ToListAsync());
    }

    [Fact]
    public async Task InboxAndTimelineRemainIdempotentInTheSharedDatabase()
    {
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        var evt = new EventEnvelope(Guid.NewGuid(), "reconstruction.project.closed", 1, Guid.NewGuid(), "operador", "Cierre", DateTimeOffset.UtcNow);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var delivery = services.CreateAsyncScope();
            await delivery.ServiceProvider.GetRequiredService<TimelineEventHandler>().HandleAsync(evt, default);
        }
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1, await db.InboxMessages.CountAsync());
        Assert.Equal(evt.Id, (await db.TimelineEntries.SingleAsync()).EventId);
    }

    [Fact]
    public async Task BothApiInitializersCanRunTogether()
    {
        await using var first = Services();
        await using var second = Services();
        await Task.WhenAll(first.InitializePlatformDalAsync(), second.InitializePlatformDalAsync());
        await using var scope = first.CreateAsyncScope();
        Assert.Equal(12, (await scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>().Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task ConcurrentProjectUpdateRollsBackItsAuditAndOutbox()
    {
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        var id = Guid.Empty;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            var proposal = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 100m, ProposalPriority.High);
            var project = new ReconstructionProject(proposal);
            proposal.Select();
            db.AddRange(proposal, project);
            await db.SaveChangesAsync();
            id = project.Id;
        }
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var a = await firstDb.Projects.SingleAsync(x => x.Id == id);
        var b = await secondDb.Projects.SingleAsync(x => x.Id == id);
        var schedule = new[] { new ActivityDefinition("Reparar", "Equipo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 100m) };
        a.SaveFormalization(100m, "Aportes", "Entidad", "Contratista A", schedule);
        b.SaveFormalization(100m, "Aportes", "Entidad", "Contratista B", schedule);
        var evt = IntegrationEvent.Typed(1, id, "test", new FormalizationSavedEvent(id, a.Version, a.FinalBudget,
            a.FundingSource, a.Responsible, a.Contractor));
        await first.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(evt, default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>().CommitAsync(evt, default));
        await using var verification = services.CreateAsyncScope();
        var check = verification.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(1, await check.AuditEntries.CountAsync());
        Assert.Equal(1, await check.OutboxMessages.CountAsync());
        Assert.Equal(1, await check.Activities.CountAsync());
        Assert.Equal("Contratista A", (await check.Projects.SingleAsync()).Contractor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentProposalChangeRejectsStaleWritesAndRollsBackProjectAuditAndOutbox(bool selectFirst)
    {
        await using var services = Services();
        await services.InitializePlatformDalAsync();
        var evaluation = ProposalPolicyTests.Evaluation();
        var proposal = new ReconstructionProposal(1, evaluation.Location, evaluation.AssetType, evaluation.DamageLevel, [evaluation]);
        await using (var seed = services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.Proposals.Add(proposal); await db.SaveChangesAsync();
        }
        await using var winner = services.CreateAsyncScope();
        await using var stale = services.CreateAsyncScope();
        var winnerDb = winner.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var staleDb = stale.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.True(winnerDb.Model.FindEntityType(typeof(ReconstructionProposal))!.FindProperty(nameof(ReconstructionProposal.Version))!.IsConcurrencyToken);
        var current = await winnerDb.Proposals.SingleAsync();
        var old = await staleDb.Proposals.SingleAsync();
        IntegrationEvent selection(ReconstructionProject project) => IntegrationEvent.Typed(1, project.Id, "test",
            new ProposalsSelectedEvent(project.Id, proposal.Id, project.Version));
        IntegrationEvent refresh() => IntegrationEvent.Typed(1, null, "test", new ProposalsGeneratedEvent([
            new(proposal.Id, proposal.Zone, proposal.AssetType, proposal.Damage, current.ProposedBudget, current.AssessmentCount)]));
        var winnerWork = winner.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>();
        IntegrationEvent staleEvent;
        if (selectFirst)
        {
            var project = new ReconstructionProject(current);
            current.Select(); winnerDb.Projects.Add(project);
            await winnerWork.CommitAsync(selection(project), default);
            old.RefreshEvaluations([evaluation with { Budget = 50m }]);
            staleEvent = refresh();
        }
        else
        {
            current.RefreshEvaluations([evaluation with { Budget = 50m }]);
            await winnerWork.CommitAsync(refresh(), default);
            // Mismo estado y snapshot originales: solo Version detecta esta escritura obsoleta.
            current.RefreshEvaluations([evaluation]);
            await winnerWork.CommitAsync(refresh(), default);
            var project = new ReconstructionProject(old);
            old.Select(); staleDb.Projects.Add(project); staleEvent = selection(project);
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>()
            .CommitAsync(staleEvent, default));
        await using var verify = services.CreateAsyncScope();
        var saved = verify.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var expectedWrites = selectFirst ? 1 : 2;
        Assert.Equal(expectedWrites, await saved.OutboxMessages.CountAsync());
        Assert.Equal(expectedWrites, await saved.AuditEntries.CountAsync());
        Assert.Equal(selectFirst ? 1 : 0, await saved.Projects.CountAsync());
        var persisted = await saved.Proposals.SingleAsync();
        Assert.Equal(selectFirst ? ProposalStatus.Selected : ProposalStatus.Proposed, persisted.Status);
        Assert.Equal(selectFirst ? 2 : 3, persisted.Version);
        Assert.Equal(0m, persisted.ProposedBudget);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(proposal.Evaluations), System.Text.Json.JsonSerializer.Serialize(persisted.Evaluations));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
