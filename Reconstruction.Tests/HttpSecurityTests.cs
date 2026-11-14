using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.DAL.Contexts;
using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Xunit;

namespace Reconstruction.Tests;

public sealed class HttpSecurityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "reconstruction-security-" + Guid.NewGuid());
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-key-never-for-deployment-0123456789-abcdefghijklmnopqrstuv"));
    private WebApplicationFactory<Program> App()
    {
        Directory.CreateDirectory(_folder);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Disasters"] = $"Data Source={Path.Combine(_folder, "test.db")};Pooling=False",
                ["Messaging:Enabled"] = "false", ["Reconstruction:DemoExternalData"] = "true",
                ["Security:LocalDemo"] = "false", ["Security:Authority"] = "https://identity.test", ["Security:Audience"] = "platform"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<DisasterDbContext>>();
                services.RemoveAll<DbContextOptions<ReconstructionDbContext>>();
                var connection = $"Data Source={Path.Combine(_folder, "test.db")};Pooling=False";
                services.AddDbContext<DisasterDbContext>(o => o.UseSqlite(connection, x => x.MigrationsHistoryTable(Platform.DAL.PlatformDatabase.DisasterHistory)));
                services.AddDbContext<ReconstructionDbContext>(o => o.UseSqlite(connection, x => x.MigrationsHistoryTable(Platform.DAL.PlatformDatabase.ReconstructionHistory)));
                foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType?.Namespace?.StartsWith("Reconstruction.Infrastructure.Messaging") == true).ToArray()) services.Remove(worker);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = "https://identity.test" };
                configuration.SigningKeys.Add(Key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                });
            });
        });
    }
    private static void Authenticate(HttpClient client, string role = "reconstruction.read", int disasterId = 1, bool expired = false)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("https://identity.test", "platform", new[] {
            new Claim("sub", "authenticated-operator"), new Claim("organization_id", "organization-a"),
            new Claim("role", role), new Claim("disaster_id", disasterId.ToString()) },
            now.AddHours(-2), expired ? now.AddHours(-1) : now.AddHours(1), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    [Fact]
    public async Task AnonymousExpiredReadOnlyAndUnauthorizedDisasterRequestsAreRejected()
    {
        await using var app = App(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reconstruccion/proyectos")).StatusCode);
        Authenticate(client, expired: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reconstruccion/proyectos")).StatusCode);
        Authenticate(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reconstruccion/proyectos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/reconstruccion/propuestas/generar", new { disasterId = 1 })).StatusCode);
        Authenticate(client, "reconstruction.write");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/reconstruccion/propuestas/generar", new { disasterId = 2 })).StatusCode);
    }
    [Fact]
    public async Task ProjectListsAndDirectReadsAreScopedToTheDisastersGrantedByTheIssuer()
    {
        await using var app = App(); using var client = app.CreateClient();
        Guid hidden;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReconstructionDbContext>();
            var a = new ReconstructionProposal(1, "Zona", "Puente", DamageLevel.Severe, 1, 10, ProposalPriority.High);
            var b = new ReconstructionProposal(2, "Zona", "Puente", DamageLevel.Severe, 1, 10, ProposalPriority.High);
            var first = new ReconstructionProject(a); var second = new ReconstructionProject(b); hidden = second.Id;
            db.AddRange(a, b, first, second); await db.SaveChangesAsync();
        }
        Authenticate(client);
        var visible = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/reconstruccion/proyectos");
        Assert.Equal(1, visible.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reconstruccion/proyectos/{hidden}")).StatusCode);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
