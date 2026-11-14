using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Platform.DAL;

public static class PlatformDatabase
{
    public const string DisasterHistory = "__EFMigrationsHistory_Disaster";
    public const string ReconstructionHistory = "__EFMigrationsHistory_Reconstruction";

    public static string ResolveConnectionString(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
            throw new ArgumentException("Configure una ruta persistente para disasters.db.", nameof(connectionString));
        if (builder.DataSource != ":memory:" && builder.Mode != SqliteOpenMode.Memory)
        {
            builder.DataSource = Path.GetFullPath(builder.DataSource, contentRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
        }
        return builder.ToString();
    }

    internal static async Task<FileStream?> AcquireMigrationLockAsync(DbContext context, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder(context.Database.GetConnectionString());
        if (builder.DataSource == ":memory:" || builder.Mode == SqliteOpenMode.Memory) return null;
        var path = Path.GetFullPath(builder.DataSource) + ".migration.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(100, ct); }
        }
    }
}
