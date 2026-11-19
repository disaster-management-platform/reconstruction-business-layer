using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Reconstruction.Application;
using Reconstruction.Application.Contracts;
using Reconstruction.Application.Facade;
using Reconstruction.Domain;
using Reconstruction.Infrastructure;
using Platform.DAL;
using Platform.Security;
using Reconstruction.Application.Ports;
using Reconstruction.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddReconstructionDataAccess(builder.Configuration.GetConnectionString("Disasters")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Disasters para PostgreSQL."));
builder.Services.AddReconstruction(builder.Configuration, builder.Environment);
builder.Services.AddPlatformSecurity(builder.Configuration, builder.Environment);
if (!builder.Configuration.GetValue<bool>("Security:LocalDemo"))
{
    builder.Services.AddHttpClient("disaster-access", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["ExternalApis:Disasters"] ?? "http://localhost:5156/");
        client.Timeout = TimeSpan.FromSeconds(4);
        client.MaxResponseContentBufferSize = 1024 * 1024;
    });
    builder.Services.AddScoped<IDisasterAccess, DisasterScopeAccess>();
}

var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception switch
    {
        BadHttpRequestException badRequest => badRequest.StatusCode,
        MissingResourceException => 404,
        BusinessConflictException => 409,
        DbUpdateConcurrencyException => 409,
        DbUpdateException update when PlatformDatabase.IsConflict(update) => 409,
        DomainException => 422,
        DependencyUnavailableException => 503,
        _ => 500
    };
    context.Response.StatusCode = status;
    await Results.Problem(statusCode: status,
        title: status == 500 ? "Error interno" : "No se pudo completar la operación",
        detail: status == 500 ? "Consulte el registro del servidor." : exception?.Message)
        .ExecuteAsync(context);
}));
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapGet("/api/reconstruccion/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/api/reconstruccion/ready", async (Platform.DAL.Contexts.ReconstructionDbContext db, IConfiguration configuration, CancellationToken ct) =>
{
    var demo = configuration.GetValue<bool>("Reconstruction:DemoExternalData");
    var integrationsConfigured = demo || (!string.IsNullOrWhiteSpace(configuration["ExternalApis:DamageAssessments:BaseUrl"])
        && !string.IsNullOrWhiteSpace(configuration["ExternalApis:Funding:BaseUrl"]));
    var identityConfigured = configuration.GetValue<bool>("Security:LocalDemo")
        || (!string.IsNullOrWhiteSpace(configuration["Security:Authority"]) && !string.IsNullOrWhiteSpace(configuration["Security:Audience"]));
    var ready = integrationsConfigured && identityConfigured && await db.Database.CanConnectAsync(ct);
    return Results.Json(new { ready, demo, integrationsConfigured, identityConfigured }, statusCode: ready ? 200 : 503);
}).AllowAnonymous();
app.MapGet("/api/reconstruccion/access-mode", (IConfiguration c) => Results.Ok(new { localDemo = c.GetValue<bool>("Security:LocalDemo"), demoExternalData = c.GetValue<bool>("Reconstruction:DemoExternalData") })).AllowAnonymous();
var routes = app.MapGroup("/api/reconstruccion").WithTags("Reconstruccion").RequireAuthorization(PlatformSecurity.Read);
routes.AddEndpointFilter(async (context, next) =>
{
    var user = context.HttpContext.User;
    if (context.HttpContext.Request.Method != "GET" && !user.IsInRole(PlatformSecurity.Write)) return Results.Forbid();
    var access = context.HttpContext.RequestServices.GetRequiredService<IDisasterAccess>();
    if (access is DisasterScopeAccess scope) await scope.ResolveAsync(context.HttpContext.RequestAborted);
    var generate = context.Arguments.OfType<GenerateProposalsRequest>().FirstOrDefault();
    if (generate is not null && !access.CanAccess(generate.DisasterId)) return Results.Forbid();
    if (int.TryParse(context.HttpContext.Request.Query["disasterId"], out var id) && !access.CanAccess(id)) return Results.Forbid();
    return await next(context);
});

routes.MapPost("/proyectos/{id:guid}/adjuntos", async (Guid id, HttpRequest request, IProjectRepository projects,
    IAttachmentStore store, IDisasterAccess access, CancellationToken ct) =>
{
    var project = await projects.FindAsync(id, ct);
    if (project is null || !access.CanAccess(project.DisasterId)) return Results.NotFound();
    if (!request.HasFormContentType) return Results.BadRequest(new { detail = "Utilice multipart/form-data." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest(new { detail = "Falta el archivo." });
    await using var stream = file.OpenReadStream();
    return Results.Ok(await store.SaveAsync(id, Path.GetFileName(file.FileName), file.ContentType, form["category"].ToString(), stream, ct));
});
routes.MapGet("/proyectos/{id:guid}/adjuntos/{attachmentId:guid}", async (Guid id, Guid attachmentId,
    IProjectRepository projects, IAttachmentStore store, IDisasterAccess access, CancellationToken ct) =>
{
    var project = await projects.FindAsync(id, ct);
    if (project is null || !access.CanAccess(project.DisasterId)) return Results.NotFound();
    var file = await store.OpenAsync(id, attachmentId, ct);
    return file is null ? Results.NotFound() : Results.File(file.Content, file.Info.ContentType, file.Info.Name);
});
routes.MapGet("/financiacion", async (int disasterId, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetFundingAsync(disasterId, ct)));
routes.MapGet("/workflow", async (int? disasterId, Guid? projectId, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetWorkflowAsync(disasterId, projectId, ct)));

// Identity comes only from the validated token or the explicit local demonstration scheme.
static string Actor(HttpRequest req) => PlatformSecurity.Actor(req.HttpContext.User);

routes.MapPost("/propuestas/generar", async (GenerateProposalsRequest request,
    HttpRequest http, IReconstructionFacade facade, IConfiguration configuration, CancellationToken ct) =>
{
    http.HttpContext.Response.Headers["X-Evaluations-Mode"] = configuration.GetValue<bool>("Reconstruction:DemoExternalData") ? "demo" : "external";
    return Results.Ok(await facade.GenerateProposalsAsync(request.DisasterId, Actor(http), ct));
});
routes.MapGet("/propuestas", async (int disasterId, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetProposalsAsync(disasterId, ct)));

routes.MapGet("/proyectos", async (int? disasterId, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetProjectsAsync(disasterId, ct)));
routes.MapPost("/proyectos", async (CreateProjectRequest request,
    HttpRequest http, IReconstructionFacade facade, CancellationToken ct) =>
{
    var project = await facade.CreateProjectAsync(request.ProposalId, request.ExpectedVersion, Actor(http), ct);
    return Results.Ok(project); // Idempotente por ProposalId, incluso si ya existía.
});
routes.MapGet("/proyectos/{id:guid}", async (Guid id, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetProjectAsync(id, ct)));
routes.MapGet("/proyectos/{id:guid}/monitoreo", async (Guid id, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetMonitoringAsync(id, ct)));
routes.MapPut("/proyectos/{id:guid}/formalizacion", async (Guid id,
    FormalizeProjectRequest request, HttpRequest http, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.FormalizeAsync(id, request, Actor(http), ct)));
routes.MapPost("/proyectos/{id:guid}/confirmar-formalizacion", async (Guid id, ExpectedVersionRequest request,
    HttpRequest http, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.ConfirmFormalizationAsync(id, request.ExpectedVersion, Actor(http), ct)));
routes.MapPut("/proyectos/{id:guid}/presupuesto", async (Guid id, ReviseBudgetRequest request,
    HttpRequest http, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.ReviseBudgetAsync(id, request, Actor(http), ct)));
routes.MapPost("/proyectos/{id:guid}/contrato-firmado", async (Guid id,
    SignedContractRequest request, HttpRequest http, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.RegisterSignedContractAsync(id, request, Actor(http), ct)));
routes.MapPost("/proyectos/{id:guid}/actividades/{activityId:guid}/reportes", async (Guid id,
    Guid activityId, RegisterProgressRequest request, HttpRequest http,
    IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.RegisterProgressAsync(id, activityId, request, Actor(http), ct)));
routes.MapPost("/proyectos/{id:guid}/cerrar", async (Guid id, ExpectedVersionRequest request,
    HttpRequest http, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.CloseAsync(id, request.ExpectedVersion, Actor(http), ct)));

// Lecturas de auditoría y línea de tiempo a través del puerto de la fachada.
routes.MapGet("/proyectos/{id:guid}/auditoria", async (Guid id,
    IReconstructionFacade facade, CancellationToken ct) => Results.Ok(await facade.GetAuditAsync(id, ct)));
routes.MapGet("/proyectos/{id:guid}/linea-tiempo", async (Guid id,
    IReconstructionFacade facade, CancellationToken ct) => Results.Ok(await facade.GetTimelineAsync(id, ct)));
routes.MapGet("/proyectos/{id:guid}/reporte-final", async (Guid id, IReconstructionFacade facade, CancellationToken ct) =>
    Results.Ok(await facade.GetFinalReportAsync(id, ct)));

app.Run();

public sealed record GenerateProposalsRequest(int DisasterId);
public sealed record ExpectedVersionRequest(long ExpectedVersion);
public partial class Program { }
