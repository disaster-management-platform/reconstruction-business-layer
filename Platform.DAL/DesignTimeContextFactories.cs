using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Platform.DAL.Contexts;

namespace Platform.DAL;

// Use --project Platform.DAL from the solution root. No API or broker is started by EF tools.
public sealed class DisasterContextFactory : IDesignTimeDbContextFactory<DisasterDbContext>
{
    public DisasterDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<DisasterDbContext>().UseSqlite(DesignTimeDatabase.ConnectionString(),
            sqlite => sqlite.MigrationsHistoryTable(PlatformDatabase.DisasterHistory)).Options);
}

public sealed class ReconstructionContextFactory : IDesignTimeDbContextFactory<ReconstructionDbContext>
{
    public ReconstructionDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<ReconstructionDbContext>().UseSqlite(DesignTimeDatabase.ConnectionString(),
            sqlite => sqlite.MigrationsHistoryTable(PlatformDatabase.ReconstructionHistory)).Options);
}

internal static class DesignTimeDatabase
{
    internal static string ConnectionString() => PlatformDatabase.ResolveConnectionString(
        Environment.GetEnvironmentVariable("ConnectionStrings__Disasters")
            ?? "Data Source=DisasterLocation/App_Data/disasters.db", Directory.GetCurrentDirectory());
}
