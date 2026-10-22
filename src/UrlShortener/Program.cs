using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using UrlShortener;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = builder.Configuration["DATA_DIRECTORY"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var baseUrl = builder.Configuration["BASE_URL"]?.TrimEnd('/');
if (string.IsNullOrWhiteSpace(baseUrl))
{
    var configuredUrl = builder.Configuration["ASPNETCORE_URLS"]?.Split(';').FirstOrDefault();
    baseUrl = (configuredUrl ?? "http://localhost:3000").TrimEnd('/');
}

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:3000");
var databaseProvider = builder.Configuration["Database:Provider"] ?? "InMemory";
var sqlConnectionString = builder.Configuration.GetConnectionString("SqlServer");
if (databaseProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase) ||
    string.IsNullOrWhiteSpace(sqlConnectionString))
{
    builder.Services.AddDbContextFactory<UrlShortenerDbContext>(options =>
        options.UseInMemoryDatabase(builder.Configuration["Database:Name"] ?? "UrlShortenerDevelopment"));
}
else
{
    builder.Services.AddDbContextFactory<UrlShortenerDbContext>(options =>
        options.UseSqlServer(sqlConnectionString, sql => sql.EnableRetryOnFailure(3)));
}
builder.Services.AddSingleton<IWorkflowRepository, SqlWorkflowRepository>();
builder.Services.AddSingleton<SqlUrlStore>();
builder.Services.AddSingleton<IEngineeringAgent, OpenAiCompatibleEngineeringAgent>();
builder.Services.AddHttpClient("EngineeringAgent", client => client.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<Orchestrator>();

var developmentAuthEnabled =
    builder.Environment.IsDevelopment() &&
    bool.TryParse(builder.Configuration["Authentication:EnableDevelopmentAuth"], out var devAuth) &&
    devAuth;
if (developmentAuthEnabled)
{
    builder.Services.AddAuthentication("DevelopmentHeader")
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
            DevelopmentAuthenticationHandler>("DevelopmentHeader", _ => { });
}
else
{
    var tenantId = builder.Configuration["Entra:TenantId"];
    var audience = builder.Configuration["Entra:Audience"];
    if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("Configure Entra__TenantId and Entra__Audience; development header authentication is disabled outside Development.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "name",
                RoleClaimType = "roles",
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true
            };
        });
}
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ReleaseReviewer", policy =>
        policy.RequireAuthenticatedUser().RequireRole("Reviewer"));
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<UrlShortenerDbContext>();
    if (databaseProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        await database.Database.EnsureCreatedAsync();
    else
        await database.Database.MigrateAsync();
}
await app.Services.GetRequiredService<Orchestrator>().InitializeAsync();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception error) when (
        error is UrlStoreException or WorkflowException or AgentUnavailableException)
    {
        var statusCode = error switch
        {
            UrlStoreException storeError => storeError.StatusCode,
            WorkflowException workflowError => workflowError.StatusCode,
            AgentUnavailableException => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestFailure");
        logger.LogWarning("Request failed with status {StatusCode}: {Message}", statusCode, error.Message);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});

app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is > 16_384)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { error = "Request body exceeds the 16 KB limit." });
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
var api = app.MapGroup("/api").RequireAuthorization();

api.MapPost("/urls", async (HttpContext context, SqlUrlStore store) =>
{
    var body = await ReadJsonAsync<CreateUrlRequest>(context);
    if (body is null)
        return Results.BadRequest(new { error = "Request body must be a JSON object containing url." });
    var destination = UrlStore.ValidateDestination(body.Url);
    if (body.Alias is not null && body.Alias.Length > 32)
        throw new UrlStoreException(400, "Alias must be 4-32 letters, numbers, hyphens, or underscores.");
    var record = await store.CreateAsync(destination, body.Alias, context.RequestAborted);
    return Results.Created($"/api/urls/{record.Code}", new
    {
        record.Code,
        record.Url,
        record.CreatedAt,
        record.Clicks,
        shortUrl = $"{baseUrl}/{record.Code}"
    });
});

api.MapGet("/urls", async (SqlUrlStore store, CancellationToken cancellationToken) =>
    Results.Ok(new { urls = await store.ListAsync(cancellationToken) }));

api.MapGet("/urls/{code}", async (string code, SqlUrlStore store, CancellationToken cancellationToken) =>
{
    var record = await store.GetAsync(code, cancellationToken);
    return record is null
        ? Results.NotFound(new { error = "Short URL not found." })
        : Results.Ok(new
        {
            record.Code,
            record.Url,
            record.CreatedAt,
            record.Clicks,
            record.LastAccessedAt,
            record.Referrers,
            shortUrl = $"{baseUrl}/{record.Code}"
        });
});

api.MapGet("/scenarios", () =>
{
    var result = ScenarioCatalog.All.Select(scenario => new
    {
        scenario.Id,
        scenario.Title,
        scenario.Requirements,
        scenario.Assumptions,
        scenario.AcceptanceCriteria,
        dependencyGraph = scenario.Nodes.Select(node => new
        {
            node.Id, node.Stage, node.DependsOn, node.EntryGate, node.ExitGate, node.ApprovalRequired
        })
    });
    return Results.Ok(new { scenarios = result });
});

api.MapPost("/runs", async (HttpContext context, Orchestrator orchestrator) =>
{
    var body = await ReadJsonAsync<StartRunRequest>(context);
    if (body is null || string.IsNullOrWhiteSpace(body.ScenarioId))
        return Results.BadRequest(new { error = "scenarioId is required." });
    var run = await orchestrator.StartAsync(body.ScenarioId, body.ForceTestFailure, context.RequestAborted);
    return Results.Accepted($"/api/runs/{run.Id}", run);
});

api.MapGet("/runs", (Orchestrator orchestrator) => Results.Ok(new { runs = orchestrator.List() }));
api.MapGet("/runs/{runId}", (string runId, Orchestrator orchestrator) =>
{
    var run = orchestrator.Get(runId);
    return run is null ? Results.NotFound(new { error = "Run not found." }) : Results.Ok(run);
});

api.MapPost("/runs/{runId}/approval", async (
    string runId, HttpContext context, Orchestrator orchestrator) =>
{
    var body = await ReadJsonAsync<ApprovalDecisionRequest>(context);
    if (body is null)
        return Results.BadRequest(new { error = "Approval requires approved and optional reason fields." });
    var actor = context.User.FindFirstValue("oid")
        ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.FindFirstValue("sub")
        ?? context.User.Identity?.Name
        ?? throw new WorkflowException(403, "The authenticated reviewer identity is unavailable.");
    return Results.Ok(await orchestrator.ApproveAsync(
        runId, new ApprovalRequest(body.Approved, actor, body.Reason), context.RequestAborted));
}).RequireAuthorization("ReleaseReviewer");

api.MapPost("/runs/{runId}/replan", async (
    string runId, HttpContext context, Orchestrator orchestrator) =>
{
    var body = await ReadJsonAsync<ReplanRequest>(context);
    if (body is null)
        return Results.BadRequest(new { error = "Updated requirements are required." });
    return Results.Ok(await orchestrator.ReplanAsync(runId, body, context.RequestAborted));
});

app.MapGet("/{code}", async (string code, HttpContext context, SqlUrlStore store) =>
{
    if (code.Length is < 4 or > 32 ||
        !code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        return Results.NotFound(new { error = "Short URL not found." });

    var referer = context.Request.Headers.Referer.ToString();
    var referrer = Uri.TryCreate(referer, UriKind.Absolute, out var referrerUri)
        ? referrerUri.Host
        : "direct";
    var destination = await store.ResolveAsync(code, referrer, context.RequestAborted);
    return destination is null ? Results.NotFound(new { error = "Short URL not found." }) : Results.Redirect(destination);
});

app.Run();

static async Task<T?> ReadJsonAsync<T>(HttpContext context)
{
    using var memory = new MemoryStream();
    var buffer = new byte[4096];
    int read;
    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
    {
        if (memory.Length + read > 16_384)
            throw new UrlStoreException(413, "Request body exceeds the 16 KB limit.");
        await memory.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
    }
    try
    {
        return JsonSerializer.Deserialize<T>(memory.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException)
    {
        throw new UrlStoreException(400, "Request body must be valid JSON.");
    }
}

public sealed record CreateUrlRequest(string? Url, string? Alias);
public sealed record StartRunRequest(string? ScenarioId, bool ForceTestFailure = false);
public sealed record ApprovalDecisionRequest(bool Approved, string? Reason);
