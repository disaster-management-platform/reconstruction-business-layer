using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Reconstruction.Api;
using Reconstruction.Application;
using Xunit;

namespace Reconstruction.Tests;

public sealed class DisasterScopeAccessTests
{
    [Fact]
    public async Task OrganizationAdministratorUsesOwnerAuthorizedScopeWithoutGlobalAccess()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim("sub", "admin"), new Claim("organization_id", "3"), new Claim("role", "disasters.admin"),
            new Claim("disaster_id", "4") }, "Bearer", "sub", "role"));
        context.Request.Headers.Authorization = "Bearer synthetic-user-token";
        var response = new OwnerResponse();
        var access = new DisasterScopeAccess(new HttpContextAccessor { HttpContext = context }, response);
        await access.ResolveAsync(default);
        Assert.Contains(10, access.AllowedDisasters!);
        Assert.DoesNotContain(11, access.AllowedDisasters!);
        Assert.Equal("Bearer synthetic-user-token", response.Authorization);

        response.Json = "[]";
        await access.ResolveAsync(default);
        Assert.Empty(access.AllowedDisasters!);
        response.Status = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => access.ResolveAsync(default));

        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("disaster_id", "4") }, "Bearer"));
        var ordinary = new DisasterScopeAccess(new HttpContextAccessor { HttpContext = context }, response);
        await ordinary.ResolveAsync(default);
        Assert.Equal(new[] { 4 }, ordinary.AllowedDisasters);
    }

    private sealed class OwnerResponse : HttpMessageHandler, IHttpClientFactory
    {
        public string Json = "[{\"id\":4},{\"id\":10}]";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Authorization;
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://owner.test/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://owner.test/api/desastres", request.RequestUri!.AbsoluteUri);
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
