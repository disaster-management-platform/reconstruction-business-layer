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
    private readonly PostgresDatabase _database = new();
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-key-never-for-deployment-0123456789-abcdefghijklmnopqrstuv"));
    private WebApplicationFactory<Program> App(string environment = "Development")
    {
        Platform.DAL.PlatformDatabase.MigrateAsync(_database.ConnectionString).GetAwaiter().GetResult();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Reconstruction:DemoExternalData", "true");
            builder.UseSetting("Attachments:LocalDevelopment", "true");
            builder.UseSetting("ConnectionStrings:Disasters", _database.ConnectionString);
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Disasters"] = _database.ConnectionString,
                ["Messaging:Enabled"] = "false",
                ["Security:LocalDemo"] = "false", ["Security:Authority"] = "https://identity.test", ["Security:Audience"] = "platform"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ReconstructionDbContext>>();
                var connection = _database.ConnectionString;
                services.AddDbContext<ReconstructionDbContext>(o => o.UseNpgsql(connection, x => x.MigrationsHistoryTable(Platform.DAL.PlatformDatabase.ReconstructionHistory, Platform.DAL.PlatformDatabase.ReconstructionSchema)));
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
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void DemoExternalDataIsRejectedOutsideDevelopment(string environment)
    {
        using var app = App(environment);
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("Reconstruction:DemoExternalData", error.Message);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task DemoAttachmentReferencesRequireDevelopment(string environment, bool accepted)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Reconstruction:DemoExternalData"] = "true" }).Build();
        var store = new Reconstruction.Infrastructure.ExternalAdapters.FileAttachmentStore(configuration,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment });
        var operation = () => store.VerifyAsync(Guid.NewGuid(), ["demo-evidence"], ["demo-document"], default);
        if (accepted) await operation();
        else await Assert.ThrowsAsync<DomainException>(operation);
    }

    private static void Authenticate(HttpClient client, string role = "reconstruction.read", int disasterId = 1, bool expired = false, bool localDemoClaim = false)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> {
            new Claim("sub", "authenticated-operator"), new Claim("organization_id", "organization-a"),
            new Claim("role", role), new Claim("disaster_id", disasterId.ToString()) };
        if (localDemoClaim) claims.Add(new Claim("local_demo", "true"));
        var token = new JwtSecurityToken("https://identity.test", "platform", claims,
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectListsAndDirectReadsAreScopedToTheDisastersGrantedByTheIssuer(bool localDemoClaim)
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
        Authenticate(client, localDemoClaim: localDemoClaim);
        var visible = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/reconstruccion/proyectos");
        Assert.Equal(1, visible.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reconstruccion/proyectos/{hidden}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reconstruccion/proyectos/{hidden}/adjuntos/{Guid.NewGuid()}")).StatusCode);
        Authenticate(client, "reconstruction.write", localDemoClaim: localDemoClaim);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/reconstruccion/proyectos/{hidden}/adjuntos", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/reconstruccion/propuestas/generar", new { disasterId = 2 })).StatusCode);
    }
    public void Dispose() => _database.Dispose();
}
