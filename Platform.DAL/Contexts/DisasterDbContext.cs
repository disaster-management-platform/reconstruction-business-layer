using DisasterLocation.Core;
using Microsoft.EntityFrameworkCore;

namespace Platform.DAL.Contexts;

public sealed class DisasterDbContext(DbContextOptions<DisasterDbContext> options) : DbContext(options)
{
    public DbSet<Disaster> Disasters => Set<Disaster>();
    public DbSet<Platform.DAL.Models.DisasterPhaseAudit> PhaseAudit => Set<Platform.DAL.Models.DisasterPhaseAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var disaster = modelBuilder.Entity<Disaster>();
        disaster.ToTable("Desastres", table =>
        {
            table.HasCheckConstraint("CK_Desastres_Severidad", "Severidad BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_Desastres_Latitud", "Latitud BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_Desastres_Longitud", "Longitud BETWEEN -180 AND 180");
        });
        modelBuilder.Entity<Platform.DAL.Models.DisasterPhaseAudit>(b =>
        {
            b.ToTable("DisasterPhaseAudit"); b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.DisasterId, x.Version }).IsUnique();
        });
        disaster.Property(d => d.Phase).HasDefaultValue(DisasterPhase.Registered);
        disaster.Property(d => d.Version).HasDefaultValue(1L).IsConcurrencyToken();
        disaster.HasKey(d => d.Id);
        disaster.HasIndex(d => d.ClientRequestId).IsUnique();
        disaster.Property(d => d.Nombre).IsRequired().HasMaxLength(120);
        disaster.Property(d => d.Ubicacion).IsRequired().HasMaxLength(200);
        disaster.Property(d => d.Descripcion).IsRequired().HasMaxLength(2000);
    }
}
