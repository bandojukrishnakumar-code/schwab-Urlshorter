using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using UrlShortener;

var tests = new (string Name, Func<Task> Run)[]
{
    ("URL store validates aliases and persists concurrent click analytics", StoreBehaviorAsync),
    ("workflow executes parallel branches and requires human approval", ApprovalGateAsync),
    ("concurrent release decisions cannot overwrite each other", ApprovalRaceAsync),
    ("rejected approval safe-stops and prevents release readiness", RejectionAsync),
    ("workflow run state survives orchestrator restart", RunRecoveryAsync),
    ("replanning invalidates downstream outputs and preserves version lineage", ReplanAsync),
    ("bounded retries roll back implementation and safe-stop", RetryAndRollbackAsync),
    ("HTTP API creates links, redirects, records analytics, and gates release", ApiSmokeAsync)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures.Add($"{name}: {error}");
        Console.Error.WriteLine($"FAIL {name}: {error.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
if (failures.Count != 0) Environment.ExitCode = 1;

static async Task StoreBehaviorAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var store = new UrlStore(Path.Combine(directory, "urls.json"));
        await store.LoadAsync();
        await store.CreateAsync("https://example.com/path", "demo1");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.ResolveAsync("demo1", "example.org")));
        Equal(10L, (await store.GetAsync("demo1"))!.Clicks);
        var restored = new UrlStore(Path.Combine(directory, "urls.json"));
        await restored.LoadAsync();
        Equal(10L, (await restored.GetAsync("demo1"))!.Clicks);
        await ThrowsStoreAsync(() => store.CreateAsync("https://elsewhere.com", "demo1"), 409);
        ThrowsStore(() => UrlStore.ValidateDestination("javascript:alert(1)"), 400);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task ApprovalGateAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var orchestrator = CreateOrchestrator();
        var run = await orchestrator.StartAsync("greenfield");
        Equal("waiting_approval", run.Status);
        Equal("succeeded", run.Nodes["implementation"].Status);
        Equal("pending", run.Nodes["tests"].Status);
        Equal("pending", run.Nodes["release-readiness"].Status);
        var reviewed = await orchestrator.ApproveAsync(run.Id,
            new ApprovalRequest(true, "reviewer", "Reviewed proposed change set."));
        Equal("waiting_approval", reviewed.Status);
        Equal("succeeded", reviewed.Nodes["tests"].Status);
        Equal("succeeded", reviewed.Nodes["documentation"].Status);
        var complete = await orchestrator.ApproveAsync(run.Id,
            new ApprovalRequest(true, "reviewer", "Approved release readiness."));
        Equal("succeeded", complete.Status);
        Equal(false, complete.Nodes["release-readiness"].Output!.Value.GetProperty("deploymentPerformed").GetBoolean());
        True(complete.DecisionLineage.Count > 0);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task RejectionAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var orchestrator = CreateOrchestrator();
        var run = await orchestrator.StartAsync("ambiguous");
        var stopped = await orchestrator.ApproveAsync(run.Id,
            new ApprovalRequest(false, "reviewer", "Unresolved retention policy."));
        Equal("safe_stopped", stopped.Status);
        Equal("pending", stopped.Nodes["release-readiness"].Status);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task RunRecoveryAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var repository = new TestWorkflowRepository();
        var first = new Orchestrator(repository, new DeterministicEngineeringAgent());
        var paused = await first.StartAsync("greenfield");
        var recovered = new Orchestrator(repository, new DeterministicEngineeringAgent());
        await recovered.InitializeAsync();
        Equal("waiting_approval", recovered.Get(paused.Id)!.Status);
        var approved = await recovered.ApproveAsync(paused.Id,
            new ApprovalRequest(true, "reviewer", "Approve proposed change."));
        Equal("waiting_approval", approved.Status);
        True(approved.Audit.Any(item => item.Event == "approval.granted"));
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task ApprovalRaceAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var orchestrator = CreateOrchestrator();
        var run = await orchestrator.StartAsync("greenfield");
        var decisions = await Task.WhenAll(
            AttemptApprovalAsync(orchestrator, run.Id, new ApprovalRequest(true, "reviewer-a", "approve")),
            AttemptApprovalAsync(orchestrator, run.Id, new ApprovalRequest(false, "reviewer-b", "reject")));
        Equal(1, decisions.Count(status => status == 200));
        Equal(1, decisions.Count(status => status == 409));
        True(orchestrator.Get(run.Id)!.Status is "succeeded" or "safe_stopped");
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task<int> AttemptApprovalAsync(
    Orchestrator orchestrator,
    string runId,
    ApprovalRequest request)
{
    try
    {
        await orchestrator.ApproveAsync(runId, request);
        return 200;
    }
    catch (WorkflowException error) when (error.StatusCode == 409)
    {
        return error.StatusCode;
    }
}

static async Task ReplanAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var orchestrator = CreateOrchestrator();
        var run = await orchestrator.StartAsync("brownfield");
        var revised = await orchestrator.ReplanAsync(run.Id,
            new ReplanRequest("Persist click analytics; also support expiry."));
        Equal(2, revised.RequirementVersion);
        Equal("waiting_approval", revised.Status);
        var output = revised.Nodes["architecture"].Output!.Value;
        True(output.GetRawText().Contains("expiry", StringComparison.Ordinal));
        True(revised.Audit.Any(entry => entry.Event == "run.replanned"));
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task RetryAndRollbackAsync()
{
    var directory = NewTemporaryDirectory();
    try
    {
        var orchestrator = CreateOrchestrator();
        var run = await orchestrator.StartAsync("greenfield", forceTestFailure: true);
        run = await orchestrator.ApproveAsync(run.Id,
            new ApprovalRequest(true, "reviewer", "Run proposed validations."));
        Equal("safe_stopped", run.Status);
        Equal("failed", run.Nodes["tests"].Status);
        Equal(2, run.Nodes["tests"].Attempts);
        Equal("rolled_back", run.Nodes["implementation"].Status);
        Equal(1, run.Metrics.Retries);
        Equal(1, run.Metrics.Rollbacks);
        Equal("pending", run.Nodes["review"].Status);
    }
    finally { Directory.Delete(directory, recursive: true); }
}

static async Task ApiSmokeAsync()
{
    var directory = NewTemporaryDirectory();
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var project = Path.Combine(projectRoot, "src", "UrlShortener", "UrlShortener.csproj");
    var port = GetAvailablePort();
    var baseUrl = $"http://127.0.0.1:{port}";
    using var process = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            ArgumentList = { "run", "--no-build", "--project", project, "--no-launch-profile" },
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.StartInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
    process.StartInfo.Environment["BASE_URL"] = baseUrl;
    process.StartInfo.Environment["DATA_DIRECTORY"] = directory;
    if (!process.Start()) throw new InvalidOperationException("Could not start the API process.");

    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForHealthAsync(client, process);
        var createResponse = await client.PostAsJsonAsync("/api/urls",
            new { url = "https://example.com/article", alias = "test1" });
        Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var redirectClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        var redirect = await redirectClient.GetAsync("/test1");
        Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Equal("https://example.com/article", redirect.Headers.Location!.ToString());
        using var stats = await client.GetAsync("/api/urls/test1");
        Equal(1L, (await stats.Content.ReadFromJsonAsync<StatsResponse>())!.Clicks);
        using var invalid = await client.PostAsJsonAsync("/api/urls", new { url = "javascript:alert(1)" });
        Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var runResponse = await client.PostAsJsonAsync("/api/runs", new { scenarioId = "greenfield" });
        var run = await runResponse.Content.ReadFromJsonAsync<WorkflowEnvelope>();
        Equal("waiting_approval", run!.Status);
        var decision = await client.PostAsJsonAsync($"/api/runs/{run.Id}/approval",
            new { approved = true, actor = "smoke-test", reason = "API smoke validation" });
        Equal(HttpStatusCode.OK, decision.StatusCode);
        var nextGate = await decision.Content.ReadFromJsonAsync<WorkflowEnvelope>();
        Equal("waiting_approval", nextGate!.Status);
        var finalDecision = await client.PostAsJsonAsync($"/api/runs/{run.Id}/approval",
            new { approved = true, actor = "smoke-test", reason = "Release readiness validation" });
        Equal(HttpStatusCode.OK, finalDecision.StatusCode);
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        Directory.Delete(directory, recursive: true);
    }
}

static async Task WaitForHealthAsync(HttpClient client, System.Diagnostics.Process process)
{
    for (var attempt = 0; attempt < 80; attempt++)
    {
        if (process.HasExited)
            throw new InvalidOperationException($"API exited early with exit code {process.ExitCode}.");
        try
        {
            using var response = await client.GetAsync("/health");
            if (response.IsSuccessStatusCode) return;
        }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new TimeoutException("API did not become healthy within eight seconds.");
}

static int GetAvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static string NewTemporaryDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), $"url-shortener-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    return directory;
}

static Orchestrator CreateOrchestrator() =>
    new(new TestWorkflowRepository(), new DeterministicEngineeringAgent());

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; received {actual}.");
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected condition to be true.");
}

static void ThrowsStore(Action action, int statusCode)
{
    try { action(); }
    catch (UrlStoreException exception) when (exception.StatusCode == statusCode) { return; }
    throw new InvalidOperationException($"Expected UrlStoreException with status {statusCode}.");
}

static async Task ThrowsStoreAsync(Func<Task> action, int statusCode)
{
    try { await action(); }
    catch (UrlStoreException exception) when (exception.StatusCode == statusCode) { return; }
    throw new InvalidOperationException($"Expected UrlStoreException with status {statusCode}.");
}

internal sealed record WorkflowEnvelope(string Id, string Status);
internal sealed record StatsResponse(long Clicks);

internal sealed class TestWorkflowRepository : IWorkflowRepository
{
    private readonly ConcurrentDictionary<string, string> runs = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> LoadRunsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(runs.Values.ToArray());

    public Task SaveRunAndAuditAsync(
        string runId,
        string scenarioId,
        string status,
        int requirementVersion,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt,
        string payload,
        WorkflowAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        runs[runId] = payload;
        return Task.CompletedTask;
    }
}
