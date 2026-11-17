using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reconstruction.Application.Ports;

namespace Platform.Security;

public static class PlatformSecurity
{
    public const string Read = "reconstruction.read";
    public const string Write = "reconstruction.write";
    public static IServiceCollection AddPlatformSecurity(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var localDemo = config.GetValue<bool>("Security:LocalDemo");
        if (localDemo && !environment.IsDevelopment())
            throw new InvalidOperationException("Security:LocalDemo solo está permitido en Development.");
        services.AddHttpContextAccessor();
        services.AddScoped<IDisasterAccess>(provider =>
            new ClaimsDisasterAccess(provider.GetRequiredService<IHttpContextAccessor>(), localDemo));
        var auth = services.AddAuthentication(localDemo ? "LocalDemo" : JwtBearerDefaults.AuthenticationScheme);
        if (localDemo) auth.AddScheme<AuthenticationSchemeOptions, LocalDemoAuthentication>("LocalDemo", _ => { });
        else auth.AddJwtBearer(options =>
        {
            options.Authority = config["Security:Authority"];
            options.Audience = config["Security:Audience"];
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters.NameClaimType = "sub";
            options.TokenValidationParameters.RoleClaimType = "role";
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidateLifetime = true;
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Read, p => p.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("organization_id")
                .RequireRole(Read, Write));
            options.AddPolicy(Write, p => p.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("organization_id")
                .RequireRole(Write));
        });
        return services;
    }
    public static string Actor(ClaimsPrincipal user) => user.FindFirstValue("sub")
        ?? throw new InvalidOperationException("No se encontró la identidad autenticada.");
}

internal sealed class ClaimsDisasterAccess(IHttpContextAccessor accessor, bool localDemo) : IDisasterAccess
{
    public IReadOnlyCollection<int>? AllowedDisasters
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user is null || (localDemo && user.HasClaim("local_demo", "true"))) return null;
            return user.FindAll("disaster_id").Select(c => int.TryParse(c.Value, out var id) ? id : 0)
                .Where(id => id > 0).Distinct().ToArray();
        }
    }
}

internal sealed class LocalDemoAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Explicit opt-in only, with no caller-supplied identity or permissions.
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "local-demonstration"),
            new Claim("organization_id", "demonstration"), new Claim("role", PlatformSecurity.Write),
            new Claim("local_demo", "true") }, Scheme.Name, "sub", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
