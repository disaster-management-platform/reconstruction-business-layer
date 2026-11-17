using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Reconstruction.Application;
using Reconstruction.Application.Ports;
using Reconstruction.Infrastructure;
using Reconstruction.Infrastructure.ExternalAdapters;
using Xunit;

namespace Reconstruction.Tests;

public sealed class DisasterCircuitBreakerTests
{
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = JsonContent.Create(new { id = 42, severidad = 4, phase = "Recovery" })
    };
    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://disasters.test/"), Timeout = TimeSpan.FromSeconds(4)
    };

    [Theory]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("408")]
    [InlineData("429")]
    [InlineData("network")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task RepeatedDependencyFailuresOpenTheCircuitAndStopHttpRequests(string fault)
    {
        using var handler = new Handler(async token =>
        {
            if (fault == "network") throw new HttpRequestException("Red no disponible.");
            if (fault == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (fault == "json") return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{invalid-json", Encoding.UTF8, "application/json") };
            return Response((HttpStatusCode)int.Parse(fault));
        });
        using var client = Client(handler);
        if (fault == "timeout") client.Timeout = TimeSpan.FromMilliseconds(30);
        var reader = new DisasterHttpReader(client, DisasterHttpResilience.Create());
        for (var i = 0; i < 5; i++)
        {
            var failure = await Assert.ThrowsAsync<DependencyUnavailableException>(() => reader.GetAsync(42, default));
            Assert.IsNotType<BrokenCircuitException>(failure.InnerException);
        }
        var blocked = await Assert.ThrowsAsync<DependencyUnavailableException>(() => reader.GetAsync(42, default));
        Assert.IsType<BrokenCircuitException>(blocked.InnerException);
        Assert.Equal(5, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task MissingDisastersAndNonTransientClientErrorsDoNotOpenTheCircuit(HttpStatusCode status)
    {
        using var handler = new Handler(_ => Task.FromResult(Response(status)));
        using var client = Client(handler);
        var reader = new DisasterHttpReader(client, DisasterHttpResilience.Create());
        for (var i = 0; i < 10; i++)
        {
            if (status == HttpStatusCode.NotFound) Assert.Null(await reader.GetAsync(42, default));
            else
            {
                var failure = await Assert.ThrowsAsync<DependencyUnavailableException>(() => reader.GetAsync(42, default));
                Assert.IsType<HttpRequestException>(failure.InnerException);
            }
        }
        Assert.Equal(10, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationDuringRequestsIsPropagatedAndDoesNotOpenTheCircuit()
    {
        var cancelRequests = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async token =>
        {
            if (cancelRequests)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response();
        });
        using var client = Client(handler);
        var reader = new DisasterHttpReader(client, DisasterHttpResilience.Create());
        for (var i = 0; i < 5; i++)
        {
            entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var request = reader.GetAsync(42, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        cancelRequests = false;
        Assert.Equal(new DisasterSnapshot(42, 4, DisasterLocation.Core.DisasterPhase.Recovery), await reader.GetAsync(42, default));
        Assert.Equal(6, handler.Calls);
    }

    [Fact]
    public async Task FactorySharesTheCircuitAcrossScopesAndProbesRecoveryAfterTheBreak()
    {
        var clock = new ManualClock();
        var status = HttpStatusCode.ServiceUnavailable;
        using var handler = new Handler(_ => Task.FromResult(Response(status)));
        var services = new ServiceCollection();
        services.AddReconstruction(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalApis:Disasters"] = "http://disasters.test/"
        }).Build());
        services.AddSingleton<ResiliencePipeline<DisasterSnapshot?>>(_ => DisasterHttpResilience.Create(clock));
        services.AddHttpClient<IDisasterReader, DisasterHttpReader>()
            .ConfigureHttpClient(client => Assert.Equal(TimeSpan.FromSeconds(4), client.Timeout))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        async Task<DisasterSnapshot?> Call()
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IDisasterReader>().GetAsync(42, default);
        }
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<DependencyUnavailableException>(Call);
        Assert.IsType<BrokenCircuitException>((await Assert.ThrowsAsync<DependencyUnavailableException>(Call)).InnerException);
        Assert.Equal(5, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(15));
        // La primera prueba sigue fallando: se abre de nuevo el circuito.
        Assert.IsType<HttpRequestException>((await Assert.ThrowsAsync<DependencyUnavailableException>(Call)).InnerException);
        Assert.IsType<BrokenCircuitException>((await Assert.ThrowsAsync<DependencyUnavailableException>(Call)).InnerException);
        Assert.Equal(6, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(15)); status = HttpStatusCode.OK;
        Assert.Equal(new DisasterSnapshot(42, 4, DisasterLocation.Core.DisasterPhase.Recovery), await Call());
        Assert.Equal(new DisasterSnapshot(42, 4, DisasterLocation.Core.DisasterPhase.Recovery), await Call());
        Assert.Equal(8, handler.Calls);
    }

    [Fact]
    public async Task OnlyOneRecoveryProbeReachesTheDependencyWhileHalfOpen()
    {
        var clock = new ManualClock();
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeResult = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probing = false;
        using var handler = new Handler(token =>
        {
            if (!probing) return Task.FromResult(Response(HttpStatusCode.ServiceUnavailable));
            probeEntered.TrySetResult();
            return probeResult.Task.WaitAsync(token);
        });
        using var client = Client(handler);
        var reader = new DisasterHttpReader(client, DisasterHttpResilience.Create(clock));
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<DependencyUnavailableException>(() => reader.GetAsync(42, default));
        clock.Advance(TimeSpan.FromSeconds(15)); probing = true;
        var probe = reader.GetAsync(42, default);
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = await Assert.ThrowsAsync<DependencyUnavailableException>(() => reader.GetAsync(42, default));
        Assert.IsType<BrokenCircuitException>(blocked.InnerException); Assert.Equal(6, handler.Calls);
        probeResult.SetResult(Response());
        Assert.Equal(new DisasterSnapshot(42, 4, DisasterLocation.Core.DisasterPhase.Recovery), await probe);
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return send(ct);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
