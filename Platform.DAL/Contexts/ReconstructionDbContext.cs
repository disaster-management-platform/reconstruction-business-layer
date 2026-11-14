using Microsoft.EntityFrameworkCore;
using Reconstruction.Domain.Models;
using Platform.DAL.Models;
using Reconstruction.Application.Ports;

namespace Platform.DAL.Contexts;

public sealed class ReconstructionDbContext(DbContextOptions<ReconstructionDbContext> options) : DbContext(options)
{
    public DbSet<ReconstructionProposal> Proposals => Set<ReconstructionProposal>();
    public DbSet<ReconstructionProject> Projects => Set<ReconstructionProject>();
    public DbSet<ProjectActivity> Activities => Set<ProjectActivity>();
    public DbSet<ProgressReport> Reports => Set<ProgressReport>();
    public DbSet<DisasterFundingReservation> FundingReservations => Set<DisasterFundingReservation>();
    public DbSet<WorkflowItem> WorkflowItems => Set<WorkflowItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<TimelineEntry> TimelineEntries => Set<TimelineEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The domain assigns these GUIDs, including new children of tracked aggregates.
        modelBuilder.Entity<ReconstructionProposal>(b =>
        {
            b.ToTable("ReconstructionProposals"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Ignore(x => x.AverageSeverity);
            b.Property(x => x.Evaluations).HasJsonCollection("EvaluationsJson").IsRequired().IsConcurrencyToken();
            b.Property(x => x.Status).IsConcurrencyToken();
            b.Property(x => x.Version).HasDefaultValue(1L).ValueGeneratedNever().IsConcurrencyToken();
            b.Property(x => x.ReviewFingerprint).IsRequired().HasDefaultValue("").IsConcurrencyToken();
            // Columnas históricas conservadas sin representar estados del dominio.
            b.Property<bool>("IsSelectedForReconstruction").HasDefaultValue(false).IsConcurrencyToken();
            b.Property<Guid?>("SelectedForProjectId");
            b.Property(x => x.Zone).HasMaxLength(180).IsRequired();
            b.Property(x => x.AssetType).HasMaxLength(120).IsRequired();
            b.Property(x => x.ProposedBudget).HasConversion<string>().HasColumnType("TEXT");
            b.HasIndex(x => new { x.DisasterId, x.Zone, x.AssetType }).IsUnique()
                .HasFilter("\"Status\" = 0 AND \"IsSelectedForReconstruction\" = 0");
        });
        modelBuilder.Entity<ReconstructionProject>(b =>
        {
            b.ToTable("ReconstructionProjects"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Ignore(x => x.IsBudgetLocked);
            b.Property(x => x.SignedContractReference).HasMaxLength(1000).IsRequired().HasDefaultValue("");
            b.Property(x => x.ContractRecordedBy).HasMaxLength(200).IsRequired().HasDefaultValue("");
            b.HasIndex(x => x.ProposalId).IsUnique();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.Property(x => x.FinalBudget).HasConversion<string>().HasColumnType("TEXT");
            b.Property(x => x.FundingSource).HasMaxLength(200).IsRequired();
            b.Property(x => x.FundingSourceId).HasMaxLength(200).HasDefaultValue("donations").IsRequired();
            b.Property(x => x.Responsible).HasMaxLength(200).IsRequired();
            b.Property(x => x.Contractor).HasMaxLength(200).IsRequired();
            b.HasMany(x => x.Activities).WithOne().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Activities).HasField("_activities").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<ProjectActivity>(b =>
        {
            b.ToTable("ReconstructionActivities"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(250).IsRequired();
            b.Property(x => x.Responsible).HasMaxLength(200).IsRequired();
            b.Property(x => x.Budget).HasConversion<string>().HasColumnType("TEXT");
            b.HasMany(x => x.Reports).WithOne().HasForeignKey(x => x.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Reports).HasField("_reports").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<ProgressReport>(b =>
        {
            b.ToTable("ReconstructionReports"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ActualCost).HasConversion<string>().HasColumnType("TEXT");
            b.Property(x => x.Evidence).HasJsonCollection("EvidenceJson").IsRequired();
            b.Property(x => x.Documents).HasJsonCollection("DocumentsJson").IsRequired();
            b.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.RequestId).IsUnique().HasFilter("\"RequestId\" IS NOT NULL");
        });
        modelBuilder.Entity<DisasterFundingReservation>(b =>
        {
            b.ToTable("ReconstructionFundingReservations"); b.HasKey(x => x.DisasterId);
            b.Property(x => x.DisasterId).ValueGeneratedNever();
            b.Property(x => x.ReservedAmount).HasConversion<string>().HasColumnType("TEXT");
            b.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<WorkflowItem>(b =>
        {
            b.ToTable("ReconstructionWorkflow"); b.HasKey(x => new { x.Kind, x.SubjectId });
            b.Property(x => x.SubjectId).ValueGeneratedNever();
            b.Property(x => x.DataJson).IsRequired();
            b.Property(x => x.Version).IsConcurrencyToken();
            b.HasIndex(x => new { x.DisasterId, x.IsActive });
            b.HasIndex(x => x.ProjectId);
        });
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("OutboxMessages"); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.SentAt, x.CreatedUnixMs });
        });
        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.ToTable("ReconstructionAudit"); b.HasKey(x => x.Id);
            b.HasIndex(x => x.ProjectId);
            b.Property(x => x.DataJson).HasDefaultValue("{}");
            b.HasIndex(x => x.EventId).IsUnique();
        });
        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("InboxMessages"); b.HasKey(x => new { x.ConsumerName, x.EventId });
            b.Property(x => x.ConsumerName).HasMaxLength(160).HasDefaultValue(ConsumerNames.Timeline);
            b.Property(x => x.EventId).HasColumnName("Id").ValueGeneratedNever();
            b.Property(x => x.OutcomeJson).IsRequired().HasDefaultValue("");
        });
        modelBuilder.Entity<TimelineEntry>(b =>
        {
            b.ToTable("ReconstructionTimeline"); b.HasKey(x => x.Id);
            b.HasIndex(x => x.EventId).IsUnique();
            b.HasIndex(x => x.ProjectId);
        });
    }
}
