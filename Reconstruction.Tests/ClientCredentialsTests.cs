using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reconstruction.Infrastructure.ExternalAdapters;
using Xunit;

public class ClientCredentialsTests
{
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
    private sealed class Factory(HttpMessageHandler transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, false);
    }
    private static IConfiguration Settings(string endpoint = "https://identity.example.invalid/token") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalApis:DamageAssessments:OAuth:TokenEndpoint"] = endpoint,
            ["ExternalApis:DamageAssessments:OAuth:ClientId"] = "test-client",
            ["ExternalApis:DamageAssessments:OAuth:ClientSecret"] = "synthetic-test-secret"
        }).Build();
    private static HttpResponseMessage Token(string value, int ttl = 300) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"access_token\":\"{value}\",\"token_type\":\"Bearer\",\"expires_in\":{ttl}}}", Encoding.UTF8, "application/json")
    };
    [Fact]
    public async Task ConcurrentOperationsShareOneToken()
    {
        int acquisitions = 0, calls = 0;
        using var oauth = new Transport(_ => Token("token-" + Interlocked.Increment(ref acquisitions)));
        using var handler = new ClientCredentialsHandler(new Factory(oauth), Settings(), "DamageAssessments")
        {
            InnerHandler = new Transport(request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Interlocked.Increment(ref calls);
                return new(HttpStatusCode.OK);
            })
        };
        using var client = new HttpClient(handler);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync("https://owner.example.invalid/")));
        Assert.Equal(1, acquisitions);
        Assert.Equal(8, calls);
    }
    [Fact]
    public async Task UnauthorizedInvalidatesNextOperationWithoutReplayingMutation()
    {
        int acquisitions = 0, calls = 0;
        using var oauth = new Transport(_ => Token("token-" + Interlocked.Increment(ref acquisitions)));
        using var client = new HttpClient(new ClientCredentialsHandler(new Factory(oauth), Settings(), "DamageAssessments")
        {
            InnerHandler = new Transport(_ => new(Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK))
        });
        using var first = await client.PostAsync("https://owner.example.invalid/", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(1, calls);
        using var second = await client.GetAsync("https://owner.example.invalid/");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, acquisitions);
    }
    [Fact]
    public async Task ExpiredTokenIsRenewed()
    {
        int acquisitions = 0;
        using var oauth = new Transport(_ => Token("token-" + Interlocked.Increment(ref acquisitions), 1));
        using var client = new HttpClient(new ClientCredentialsHandler(new Factory(oauth), Settings(), "DamageAssessments")
        { InnerHandler = new Transport(_ => new(HttpStatusCode.OK)) });
        using var first = await client.GetAsync("https://owner.example.invalid/");
        await Task.Delay(1100);
        using var second = await client.GetAsync("https://owner.example.invalid/");
        Assert.Equal(2, acquisitions);
    }
    [Fact]
    public async Task IssuerFailureDoesNotFallbackOrPoisonFutureAcquisition()
    {
        int acquisitions = 0, forwarded = 0;
        using var oauth = new Transport(_ => Interlocked.Increment(ref acquisitions) == 1 ? new(HttpStatusCode.ServiceUnavailable) : Token("recovered"));
        using var client = new HttpClient(new ClientCredentialsHandler(new Factory(oauth), Settings(), "DamageAssessments")
        { InnerHandler = new Transport(_ => { forwarded++; return new(HttpStatusCode.OK); }) });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://owner.example.invalid/"));
        Assert.Equal(0, forwarded);
        using var response = await client.GetAsync("https://owner.example.invalid/");
        Assert.Equal(1, forwarded);
    }
    [Fact]
    public async Task PlainHttpTokenEndpointIsRejected()
    {
        using var oauth = new Transport(_ => throw new Exception("Must not transmit secret"));
        using var client = new HttpClient(new ClientCredentialsHandler(new Factory(oauth), Settings("http://identity.example.invalid/token"), "DamageAssessments")
        { InnerHandler = new Transport(_ => throw new Exception("Must not forward")) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://owner.example.invalid/"));
    }
    public sealed class IdentityFactAttribute : FactAttribute
    {
        public IdentityFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SECURITY_OAUTH_ENDPOINT") is null)
                Skip = "Requires the isolated Keycloak integration environment.";
        }
    }
    [IdentityFact]
    [Trait("Category", "IdentityIntegration")]
    public async Task RealIssuerCredentialsAreRenewed()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalApis:DamageAssessments:OAuth:TokenEndpoint"] = Required("SECURITY_OAUTH_ENDPOINT"),
            ["ExternalApis:DamageAssessments:OAuth:ClientId"] = Required("SECURITY_OAUTH_CLIENT"),
            ["ExternalApis:DamageAssessments:OAuth:ClientSecret"] = Required("SECURITY_OAUTH_SECRET")
        }).Build();
        using var services = new ServiceCollection().AddHttpClient("oauth", c => c.Timeout = TimeSpan.FromSeconds(3)).Services.BuildServiceProvider();
        var tokens = new List<string>();
        using var client = new HttpClient(new ClientCredentialsHandler(services.GetRequiredService<IHttpClientFactory>(), settings, "DamageAssessments")
        { InnerHandler = new Transport(r => { tokens.Add(r.Headers.Authorization!.Parameter!); return new(HttpStatusCode.OK); }) });
        using var first = await client.GetAsync("https://owner.example.invalid/");
        using var cached = await client.GetAsync("https://owner.example.invalid/");
        Assert.Equal(tokens[0], tokens[1]);
        await Task.Delay(6000);
        using var renewed = await client.GetAsync("https://owner.example.invalid/");
        Assert.NotEqual(tokens[0], tokens[2]);
        Assert.All(tokens, token => Assert.Equal(3, token.Split('.').Length));
    }

}
