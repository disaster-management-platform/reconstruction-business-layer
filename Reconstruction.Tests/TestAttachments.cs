using Reconstruction.Application.Ports;
namespace Reconstruction.Tests;

internal sealed class TestAttachments : IReportAttachmentVerifier
{
    public Task VerifyAsync(Guid projectId, IReadOnlyList<string> evidence, IReadOnlyList<string> documents, CancellationToken ct) => Task.CompletedTask;
}
