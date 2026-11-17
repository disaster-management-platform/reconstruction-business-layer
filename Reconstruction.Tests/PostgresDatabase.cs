using System.Net;
using Npgsql;
using Platform.DAL;

namespace Reconstruction.Tests;

internal sealed class PostgresDatabase : IDisposable
{
    private readonly string _admin;
    private readonly string _name = "reconstruction_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; }

    public PostgresDatabase()
    {
        _admin = PlatformDatabase.ResolveConnectionString(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("Configure TEST_POSTGRES_CONNECTION para PostgreSQL local aislado."));
        var options = new NpgsqlConnectionStringBuilder(_admin);
        if (!options.Host!.StartsWith('/') && !options.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !(IPAddress.TryParse(options.Host, out var address) && IPAddress.IsLoopback(address)))
            throw new InvalidOperationException("Las pruebas destructivas requieren PostgreSQL local; no se ejecutan contra Supabase.");
        using var connection = new NpgsqlConnection(_admin); connection.Open();
        using var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", connection); create.ExecuteNonQuery();
        options.Database = _name; options.MaxPoolSize = 20;
        ConnectionString = options.ToString();
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_admin); connection.Open();
        using var clear = new NpgsqlConnection(ConnectionString); NpgsqlConnection.ClearPool(clear);
        using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection); drop.ExecuteNonQuery();
    }
}
