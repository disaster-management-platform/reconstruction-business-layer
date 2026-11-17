using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reconstruction.Application;
using Reconstruction.Application.Ports;
using Reconstruction.Infrastructure;
using Reconstruction.Infrastructure.ExternalAdapters;
using Xunit;

namespace Reconstruction.Tests;

public sealed class DisasterHttpReaderTests
{
    [Fact]
    public void InfrastructureRegistersTheHttpPortWithoutRegisteringPersistence()
    {
        var services = new ServiceCollection();
        services.AddReconstruction(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalApis:Disasters"] = "http://disasters.test/"
        }).Build());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IProposalRepository));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(DisasterLocation.Core.IDisasterRepository));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<DisasterHttpReader>(scope.ServiceProvider.GetRequiredService<IDisasterReader>());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task ReadsDisasterSeverityUsingHttp(HttpStatusCode status, bool exists)
    {
        using var client = new HttpClient(new StubHandler(status)) { BaseAddress = new Uri("http://disasters.test/") };
        Assert.Equal(exists ? new DisasterSnapshot(42, 4, DisasterLocation.Core.DisasterPhase.Recovery) : null, await new DisasterHttpReader(client, DisasterHttpResilience.Create()).GetAsync(42, default));
    }

    [Fact]
    public async Task ServiceFailureRemainsADependencyFailure()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable)) { BaseAddress = new Uri("http://disasters.test/") };
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => new DisasterHttpReader(client, DisasterHttpResilience.Create()).GetAsync(42, default));
    }

    [Fact]
    public async Task RejectsAnInvalidDisasterSeverity()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, 0)) { BaseAddress = new Uri("http://disasters.test/") };
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => new DisasterHttpReader(client, DisasterHttpResilience.Create()).GetAsync(42, default));
    }

    private sealed class StubHandler(HttpStatusCode status, int severity = 4) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("http://disasters.test/api/desastres/42", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new { id = 42, severidad = severity, phase = "Recovery" })
            });
        }
    }
}
