using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Reconstruction.Application;
using Reconstruction.Domain;
using Reconstruction.Infrastructure.ExternalAdapters;
using Xunit;

namespace Reconstruction.Tests;

public sealed class ExternalModuleTests
{
    private sealed class Handler(object body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
    }
    [Fact]
    public async Task FundingUsesOnlyAFreshExclusiveAllocationForTheRequestedDisaster()
    {
        var body = new { schemaVersion = 1, disasterId = 1, availableAmount = 100.25m,
            source = "Donaciones", sourceId = "allocation-1", currency = "COP", asOf = DateTimeOffset.UtcNow };
        using var client = new HttpClient(new Handler(body)) { BaseAddress = new Uri("https://funding.test/") };
        var valid = await new FundingHttpReader(client).GetAvailableAsync(1, default);
        Assert.Equal(100.25m, valid.AvailableAmount);
        Assert.Equal("allocation-1", valid.SourceId);
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => new FundingHttpReader(client).GetAvailableAsync(2, default));
        using var stale = new HttpClient(new Handler(body with { asOf = DateTimeOffset.UtcNow.AddHours(-1) })) { BaseAddress = client.BaseAddress };
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => new FundingHttpReader(stale).GetAvailableAsync(1, default));
    }
    [Fact]
    public async Task AssessmentsRequireTheVersionedEnvelopeAndMatchingDisaster()
    {
        var evaluation = ProposalPolicyTests.Evaluation() with { AssetId = "asset-1" };
        using var client = new HttpClient(new Handler(new { schemaVersion = 1, disasterId = 1, evaluations = new[] { evaluation } }))
            { BaseAddress = new Uri("https://assessments.test/") };
        Assert.Single(await new DamageAssessmentHttpReader(client).GetForDisasterAsync(1, default));
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => new DamageAssessmentHttpReader(client).GetForDisasterAsync(2, default));
    }
    [Fact]
    public async Task AttachmentsRejectInventedReferencesOtherProjectsAndTamperedContent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "attachments-test-" + Guid.NewGuid());
        try
        {
            var store = new FileAttachmentStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Attachments:Directory"] = folder }).Build(), new HostingEnvironment { EnvironmentName = Environments.Production });
            var project = Guid.NewGuid();
            using var pdf = new MemoryStream("%PDF-1.4\nTest"u8.ToArray());
            var evidence = await store.SaveAsync(project, "foto.pdf", "application/pdf", "evidence", pdf, default);
            pdf.Position = 0;
            var document = await store.SaveAsync(project, "acta.pdf", "application/pdf", "document", pdf, default);
            var e = new[] { $"attachment:{evidence.Id}" }; var d = new[] { $"attachment:{document.Id}" };
            await store.VerifyAsync(project, e, d, default);
            await Assert.ThrowsAsync<DomainException>(() => store.VerifyAsync(project, ["invented"], d, default));
            await Assert.ThrowsAsync<DomainException>(() => store.VerifyAsync(Guid.NewGuid(), e, d, default));
            await File.WriteAllTextAsync(Path.Combine(folder, evidence.Id.ToString("N") + ".bin"), "%PDF-altered");
            await Assert.ThrowsAsync<DomainException>(() => store.VerifyAsync(project, e, d, default));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
