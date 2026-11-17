using DisasterLocation.Core;
using Npgsql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
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
    private readonly PostgresDatabase _database = new();
    private string ConnectionString => _database.ConnectionString;

    private ServiceProvider Services() => new ServiceCollection().AddPlatformDal(ConnectionString).AddScoped<TimelineEventHandler>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    private static Task InitializeAsync(IServiceProvider services) => services.InitializePlatformDalAsync();

    [Fact]
    public async Task FreshDatabaseHasBothModelsAndIndependentHistories()
    {
        await using var services = Services();
        await InitializeAsync(services);
        await InitializeAsync(services);
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

    [Fact]
    public async Task DisasterRegistrationPreservesClientRequestIdAcrossMigrationReruns()
    {
        await PlatformDatabase.MigrateAsync(ConnectionString);
        var requestId = Guid.NewGuid();
        await using var services = DisasterLocation.DAL.DependencyInjection.AddDataAccess(
            new ServiceCollection(), ConnectionString).BuildServiceProvider();
        await using (var seed = services.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<DisasterDbContext>();
            context.Disasters.Add(new Disaster { Id = 42, ClientRequestId = requestId, Nombre = "Reporte",
                Ubicacion = "Bogotá", Severidad = 4, Descripcion = "Conservar", Latitud = 4.71, Longitud = -74.07 });
            await context.SaveChangesAsync();
        }
        await PlatformDatabase.MigrateAsync(ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        var record = await scope.ServiceProvider.GetRequiredService<IDisasterRepository>().GetByClientRequestIdAsync(requestId);
        Assert.NotNull(record); Assert.Equal(42, record.Id); Assert.Null(record.Radio);
        Assert.Equal(DisasterPhase.Registered, record.Phase); Assert.Equal(1, record.Version);
        Assert.Empty(await db.PhaseAudit.ToListAsync());
        Assert.Null(scope.ServiceProvider.GetService<ReconstructionDbContext>());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("Severidad", 0, false)]
    [InlineData("Severidad", 5, true)]
    [InlineData("Latitud", 91, false)]
    [InlineData("Latitud", -90, true)]
    [InlineData("Longitud", -181, false)]
    [InlineData("Longitud", 180, true)]
    public async Task PostgreSqlEnforcesDisasterBoundaries(string column, int value, bool valid)
    {
        await using var services = Services(); await InitializeAsync(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        db.Disasters.Add(new Disaster { Nombre = "Reporte", Ubicacion = "Bogotá", Severidad = 4,
            Descripcion = "Conservar", Latitud = 4.71, Longitud = -74.07 });
        await db.SaveChangesAsync();
        await using var connection = new NpgsqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"UPDATE disasters.\"Desastres\" SET \"{column}\" = @value", connection);
        command.Parameters.AddWithValue("value", value);
        if (valid) Assert.Equal(1, await command.ExecuteNonQueryAsync());
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("CK_Desastres_" + column, error.ConstraintName);
        }
    }

    [Fact]
    public async Task MigrationRerunPreservesProjectsAuditAndOutbox()
    {
        await using var services = Services(); await InitializeAsync(services);
        var proposal = new ReconstructionProposal(42, "Zona Norte", "Puente", DamageLevel.Severe, 1, 100m, ProposalPriority.High);
        var project = new ReconstructionProject(proposal); proposal.Select();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.AddRange(proposal, project);
            await new ReconstructionUnitOfWork(db).CommitAsync(
                IntegrationEvent.Typed(42, project.Id, "operador", new ProposalsSelectedEvent(project.Id, proposal.Id, project.Version)), default);
        }
        await InitializeAsync(services);
        await using var check = services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(project.Id, (await saved.Projects.SingleAsync()).Id);
        Assert.Equal(1, await saved.OutboxMessages.CountAsync()); Assert.Equal(1, await saved.AuditEntries.CountAsync());
        Assert.Equal(saved.Database.GetMigrations(), await saved.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task ProposalRoundTripPreservesExactMoneyAndDefaults()
    {
        await using var services = Services(); await InitializeAsync(services);
        var proposal = new ReconstructionProposal(42, "Zona", "Puente", DamageLevel.Severe, 2, 1234.56789m, ProposalPriority.High);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.Proposals.Add(proposal); await db.SaveChangesAsync();
        }
        await using var check = services.CreateAsyncScope();
        var saved = await check.ServiceProvider.GetRequiredService<ReconstructionDbContext>().Proposals.SingleAsync();
        Assert.Equal(proposal.Id, saved.Id); Assert.Equal(1234.56789m, saved.ProposedBudget);
        Assert.Equal(2, saved.AssessmentCount); Assert.Equal(ProposalStatus.Proposed, saved.Status);
        Assert.Empty(saved.GetEvaluations()); Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task TemporalRoundTripPreservesInstantsAndNormalizesUtc()
    {
        await using var services = Services(); await InitializeAsync(services);
        var original = DateTimeOffset.Parse("2026-11-13T12:00:00-05:00");
        var id = Guid.NewGuid();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.InboxMessages.Add(new InboxMessage { ConsumerName = ConsumerNames.Timeline, EventId = id, ProcessedAt = original });
            await db.SaveChangesAsync();
        }
        await using var check = services.CreateAsyncScope();
        var saved = await check.ServiceProvider.GetRequiredService<ReconstructionDbContext>().InboxMessages.SingleAsync();
        Assert.Equal(original, saved.ProcessedAt); Assert.Equal(TimeSpan.Zero, saved.ProcessedAt.Offset);
    }

    [Fact]
    public async Task PublishedLifecycleTimestampsMatchPersistedProjectTimestamps()
    {
        await using var services = Services(); await InitializeAsync(services);
        var proposal = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 200m, ProposalPriority.Unassigned);
        var project = ProjectMonitoringDomainTests.Project(proposal: proposal);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            db.AddRange(proposal, project);
            var work = scope.ServiceProvider.GetRequiredService<IReconstructionUnitOfWork>();
            await work.CommitAsync(IntegrationEvent.Typed(1, project.Id, "operador", new ProjectFormalizedEvent(project.Id,
                project.Version, project.FinalBudget, project.SignedContractReference, project.ContractSignedAt,
                project.Activities.Select(activity => new ScheduledActivityData(activity.Id, activity.Name, activity.Responsible,
                    activity.StartsAt, activity.DueAt, activity.Budget)).ToArray())), default);
            foreach (var activity in project.Activities)
                project.RegisterProgress(activity.Id, ActivityStatus.Completed, 10m, ["foto"], ["acta"], "operador");
            project.Close();
            await work.CommitAsync(IntegrationEvent.Typed(1, project.Id, "operador", new ProjectClosedEvent(project.Id,
                project.Version, project.ClosedAt!.Value, project.FinalBudget, 20m, 2, 2)), default);
        }
        await using var check = services.CreateAsyncScope();
        var saved = check.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        var record = await saved.Projects.Include(x => x.Activities).SingleAsync();
        var events = (await saved.OutboxMessages.ToListAsync()).Select(message =>
            System.Text.Json.JsonSerializer.Deserialize<EventEnvelope>(message.PayloadJson)!).ToArray();
        var formalized = events.Single(envelope => envelope.Type == "reconstruction.project.formalized").Read<ProjectFormalizedEvent>()!;
        Assert.Equal(record.ContractSignedAt, formalized.SignedAt);
        Assert.All(formalized.Activities, activity => {
            var persisted = record.Activities.Single(value => value.Id == activity.ActivityId);
            Assert.Equal(persisted.StartsAt, activity.StartsAt); Assert.Equal(persisted.DueAt, activity.DueAt);
        });
        Assert.Equal(record.ClosedAt, events.Single(envelope => envelope.Type == "reconstruction.project.closed").Read<ProjectClosedEvent>()!.ClosedAt);
    }

    [Fact]
    public async Task ExistingPartialSchemaFailsWithoutOverwritingItsRows()
    {
        await using var connection = new NpgsqlConnection(ConnectionString); await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("CREATE SCHEMA reconstruction; CREATE TABLE reconstruction.\"ReconstructionProposals\" (\"Id\" text PRIMARY KEY); INSERT INTO reconstruction.\"ReconstructionProposals\" VALUES ('preserve');", connection))
            await command.ExecuteNonQueryAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => PlatformDatabase.MigrateAsync(ConnectionString));
        Assert.Equal(PostgresErrorCodes.DuplicateTable, error.SqlState);
        await using var verify = new NpgsqlCommand("SELECT \"Id\" FROM reconstruction.\"ReconstructionProposals\"", connection);
        Assert.Equal("preserve", await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OnlyKnownReconstructionUniquenessViolationsAreHttpConflicts()
    {
        var unique = new PostgresException("duplicate", "ERROR", "ERROR", "23505", schemaName: "reconstruction", constraintName: "PK_InboxMessages");
        Assert.True(PlatformDatabase.IsConflict(new DbUpdateException("save", unique)));
        foreach (var error in new[] {
            new PostgresException("foreign key", "ERROR", "ERROR", "23503", schemaName: "reconstruction", constraintName: "PK_InboxMessages"),
            new PostgresException("unknown unique", "ERROR", "ERROR", "23505", schemaName: "reconstruction", constraintName: "unexpected"),
            new PostgresException("other domain", "ERROR", "ERROR", "23505", schemaName: "disasters", constraintName: "PK_InboxMessages") })
            Assert.False(PlatformDatabase.IsConflict(new DbUpdateException("save", error)));
    }

    [Fact]
    public async Task IncrementalAuditConstraintPreservesHistoryAndRejectsOrphansAndParentDeletion()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await db.GetService<IMigrator>().MigrateAsync("20261117163129_PostgreSqlDisasters");
        var disaster = new Disaster { Nombre = "Reporte", Ubicacion = "Bogotá", Severidad = 4,
            Descripcion = "Conservar", Latitud = 4.71, Longitud = -74.07 };
        db.Disasters.Add(disaster); await db.SaveChangesAsync();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDisasterRepository>().ChangePhaseAsync(disaster.Id,
            DisasterPhase.Registered, DisasterPhase.Response, 1, "operador", default));
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Equal(disaster.Id, (await db.PhaseAudit.SingleAsync()).DisasterId);
        Assert.Equal(2, (await db.Disasters.SingleAsync()).Version);
        db.ChangeTracker.Clear();
        db.Disasters.Remove(await db.Disasters.SingleAsync());
        var deletion = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(deletion.InnerException).SqlState);
        db.ChangeTracker.Clear();
        db.PhaseAudit.Add(new DisasterPhaseAudit { Id = Guid.NewGuid(), DisasterId = int.MaxValue, Actor = "operador",
            Before = DisasterPhase.Registered, After = DisasterPhase.Response, Version = 2, OccurredAt = DateTimeOffset.UtcNow });
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Disasters.CountAsync()); Assert.Equal(1, await db.PhaseAudit.CountAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task IncrementalProposalConstraintPreservesProjectsAndRejectsOrphansAndParentDeletion()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        await db.GetService<IMigrator>().MigrateAsync("20261117163132_PostgreSqlReconstruction");
        var proposal = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 100m, ProposalPriority.High);
        var project = new ReconstructionProject(proposal); proposal.Select();
        db.AddRange(proposal, project); await db.SaveChangesAsync();
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Equal(proposal.Id, (await db.Projects.SingleAsync()).ProposalId);
        db.ChangeTracker.Clear();
        db.Proposals.Remove(await db.Proposals.SingleAsync());
        var deletion = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(deletion.InnerException).SqlState);
        db.ChangeTracker.Clear();
        db.Projects.Add(new ReconstructionProject(new ReconstructionProposal(1, "Otra zona", "Puente",
            DamageLevel.Severe, 1, 100m, ProposalPriority.High)));
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.Equal(project.Id, (await db.Projects.SingleAsync()).Id); Assert.Equal(1, await db.Proposals.CountAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task InboxAndTimelineRemainIdempotentInTheSharedDatabase()
    {
        await using var services = Services();
        await InitializeAsync(services);
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
    public async Task BothMigrationInitializersCanRunTogether()
    {
        await using var first = Services();
        await using var second = Services();
        await Task.WhenAll(first.InitializePlatformDalAsync(), second.InitializePlatformDalAsync());
        await using var scope = first.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task ConcurrentProjectUpdateRollsBackItsAuditAndOutbox()
    {
        await using var services = Services();
        await InitializeAsync(services);
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
        await InitializeAsync(services);
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

    public void Dispose() => _database.Dispose();
}
