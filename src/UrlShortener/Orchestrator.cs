using System.Collections.Concurrent;
using System.Text.Json;

namespace UrlShortener;

public sealed class WorkflowNodeState(WorkflowNodeDefinition definition)
{
    public string Id { get; init; } = definition.Id;
    public string Stage { get; init; } = definition.Stage;
    public string[] DependsOn { get; init; } = [.. definition.DependsOn];
    public string EntryGate { get; init; } = definition.EntryGate;
    public string ExitGate { get; init; } = definition.ExitGate;
    public bool ApprovalRequired { get; init; } = definition.ApprovalRequired;
    public bool HighImpact { get; init; } = definition.HighImpact;
    public int RetryLimit { get; init; } = definition.RetryLimit;
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public JsonElement? Output { get; set; }
    public string? Error { get; set; }
}

public sealed record WorkflowAuditEvent(
    DateTimeOffset Timestamp,
    string RunId,
    string ScenarioId,
    int RequirementVersion,
    string Event,
    JsonElement Details);

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioTitle { get; init; }
    public required string Requirements { get; set; }
    public required string[] Assumptions { get; init; }
    public required string[] AcceptanceCriteria { get; init; }
    public int RequirementVersion { get; set; } = 1;
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required Dictionary<string, WorkflowNodeState> Nodes { get; init; }
    public List<WorkflowAuditEvent> Audit { get; } = [];
    public List<JsonElement> DecisionLineage { get; } = [];
    public WorkflowMetrics Metrics { get; set; } = new();
}

public sealed class WorkflowMetrics
{
    public int Retries { get; set; }
    public int Rollbacks { get; set; }
    public long DurationMs { get; set; }
    public double SuccessRate { get; set; }
    public long EndToEndLatencyMs => DurationMs;
    public long MeanTimeToRecoveryMs { get; set; }
}

public sealed record ApprovalRequest(bool Approved, string Actor, string? Reason);
public sealed record ReplanRequest(string Requirements);

public sealed class Orchestrator(IWorkflowRepository repository, IEngineeringAgent agent)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, WorkflowRun> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> runGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim auditGate = new(1, 1);
    private readonly HashSet<string> forcedTestFailures = new(StringComparer.Ordinal);
    private readonly object optionsGate = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var payload in await repository.LoadRunsAsync(cancellationToken))
        {
            var run = DeserializeRun(payload);
            if (run.Status == "running")
            {
                foreach (var node in run.Nodes.Values.Where(node => node.Status == "running"))
                {
                    node.Status = "failed";
                    node.Error = "Interrupted when the service restarted.";
                }
                run.Status = "safe_stopped";
                Finish(run);
                runs[run.Id] = run;
                await RecordAsync(run, "run.safe_stopped",
                    new { reason = "Process restarted while this run was active." }, cancellationToken);
            }
            else
            {
                runs[run.Id] = run;
            }
        }
    }

    public async Task<WorkflowRun> StartAsync(
        string scenarioId,
        bool forceTestFailure = false,
        CancellationToken cancellationToken = default)
    {
        var scenario = ScenarioCatalog.Find(scenarioId)
            ?? throw new WorkflowException(404, "Unknown scenario.");
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid().ToString("N"),
            ScenarioId = scenario.Id,
            ScenarioTitle = scenario.Title,
            Requirements = scenario.Requirements,
            Assumptions = [.. scenario.Assumptions],
            AcceptanceCriteria = [.. scenario.AcceptanceCriteria],
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Nodes = scenario.Nodes.ToDictionary(node => node.Id, node => new WorkflowNodeState(node))
        };
        runs[run.Id] = run;
        if (forceTestFailure)
        {
            lock (optionsGate) forcedTestFailures.Add(run.Id);
        }
        await RecordAsync(run, "run.started", new { scenarioId = scenario.Id, requirementVersion = 1 }, cancellationToken);
        await ExecuteAsync(run, cancellationToken);
        return Snapshot(run);
    }

    public WorkflowRun? Get(string runId) =>
        runs.TryGetValue(runId, out var run) ? Snapshot(run) : null;

    public IReadOnlyList<WorkflowRun> List() => runs.Values.Select(Snapshot).ToArray();

    public async Task<WorkflowRun> ApproveAsync(
        string runId,
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var runGate = runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await runGate.WaitAsync(cancellationToken);
        try
        {
            var run = RequireRun(runId);
            var checkpoint = run.Nodes.Values.SingleOrDefault(node => node.Status == "waiting_approval");
            if (checkpoint is null || run.Status != "waiting_approval")
                throw new WorkflowException(409, "This run is not waiting for an approval decision.");
            if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 128 ||
                request.Reason is { Length: > 1000 })
                throw new WorkflowException(400, "Actor is required (max 128 characters); reason must not exceed 1000 characters.");

            var approval = new
            {
                approved = request.Approved,
                actor = request.Actor.Trim(),
                reason = request.Reason ?? "",
                at = DateTimeOffset.UtcNow
            };
            checkpoint.Status = request.Approved ? "succeeded" : "rejected";
            checkpoint.Output = JsonSerializer.SerializeToElement(approval, JsonOptions);
            run.DecisionLineage.Add(JsonSerializer.SerializeToElement(new { nodeId = checkpoint.Id, approval }, JsonOptions));
            await RecordAsync(run, request.Approved ? "approval.granted" : "approval.rejected",
                new { nodeId = checkpoint.Id, actor = request.Actor.Trim(), reason = request.Reason ?? "" }, cancellationToken);
            if (!request.Approved)
            {
                run.Status = "safe_stopped";
                await RecordAsync(run, "run.safe_stopped", new { nodeId = checkpoint.Id }, cancellationToken);
                Finish(run);
                return Snapshot(run);
            }

            run.Status = "running";
            await ExecuteAsync(run, cancellationToken);
            return Snapshot(run);
        }
        finally
        {
            runGate.Release();
        }
    }

    public async Task<WorkflowRun> ReplanAsync(
        string runId,
        ReplanRequest request,
        CancellationToken cancellationToken = default)
    {
        var runGate = runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await runGate.WaitAsync(cancellationToken);
        try
        {
            var run = RequireRun(runId);
            if (run.Status is not ("waiting_approval" or "succeeded" or "safe_stopped"))
                throw new WorkflowException(409, "Replanning is allowed only at a paused or completed checkpoint.");
            if (string.IsNullOrWhiteSpace(request.Requirements) || request.Requirements.Length > 4000)
                throw new WorkflowException(400, "Updated requirements must be 1-4000 characters.");

            var oldVersion = run.RequirementVersion;
            var invalidated = Descendants(run, "requirements");
            run.Requirements = request.Requirements.Trim();
            run.RequirementVersion++;
            foreach (var nodeId in new[] { "requirements" }.Concat(invalidated))
            {
                var node = run.Nodes[nodeId];
                node.Status = "pending";
                node.Attempts = 0;
                node.Output = null;
                node.Error = null;
            }
            run.DecisionLineage.Add(JsonSerializer.SerializeToElement(new
            {
                nodeId = "requirements",
                decision = "upstream-change",
                fromVersion = oldVersion,
                toVersion = run.RequirementVersion,
                invalidated
            }, JsonOptions));
            run.Status = "running";
            await RecordAsync(run, "run.replanned",
                new { fromVersion = oldVersion, toVersion = run.RequirementVersion, invalidated }, cancellationToken);
            await ExecuteAsync(run, cancellationToken);
            return Snapshot(run);
        }
        finally
        {
            runGate.Release();
        }
    }

    private async Task ExecuteAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        run.Status = "running";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = run.Nodes.Values.Where(node =>
                node.Status == "pending" &&
                node.DependsOn.All(dependency => run.Nodes[dependency].Status == "succeeded")).ToArray();

            if (ready.Length == 0)
            {
                if (run.Nodes.Values.Any(node => node.Status == "waiting_approval"))
                {
                    run.Status = "waiting_approval";
                    return;
                }

                if (run.Nodes.Values.All(node => node.Status == "succeeded"))
                {
                    run.Status = "succeeded";
                    Finish(run);
                    await RecordAsync(run, "run.succeeded", new { run.RequirementVersion }, cancellationToken);
                    return;
                }

                if (run.Nodes.Values.Any(node => node.Status == "failed" || node.Status == "rolled_back"))
                {
                    run.Status = "safe_stopped";
                    Finish(run);
                    await RecordAsync(run, "run.safe_stopped",
                        new { reason = "A required dependency did not pass." }, cancellationToken);
                    return;
                }

                run.Status = "safe_stopped";
                Finish(run);
                await RecordAsync(run, "run.safe_stopped",
                    new { reason = "Workflow execution stalled without a valid next step." }, cancellationToken);
                return;
            }

            var approvalNode = ready.SingleOrDefault(node => node.ApprovalRequired);
            var parallelNodes = ready.Where(node => !node.ApprovalRequired).ToArray();
            await Task.WhenAll(parallelNodes.Select(node => ExecuteNodeAsync(run, node, cancellationToken)));
            if (run.Status == "safe_stopped") return;
            if (approvalNode is not null)
            {
                approvalNode.Status = "waiting_approval";
                run.Status = "waiting_approval";
                await RecordAsync(run, "approval.requested", new
                {
                    nodeId = approvalNode.Id,
                    highImpact = approvalNode.HighImpact,
                    approvalNode.EntryGate,
                    approvalNode.ExitGate
                }, cancellationToken);
                return;
            }
        }
    }

    private async Task ExecuteNodeAsync(
        WorkflowRun run,
        WorkflowNodeState node,
        CancellationToken cancellationToken)
    {
        node.Status = "running";
        await RecordAsync(run, "node.started", new
        {
            nodeId = node.Id,
            node.EntryGate,
            requirementVersion = run.RequirementVersion
        }, cancellationToken);
        var maximumAttempts = node.RetryLimit + 1;
        while (node.Attempts < maximumAttempts)
        {
            node.Attempts++;
            try
            {
                lock (optionsGate)
                {
                    if (node.Id == "tests" && forcedTestFailures.Contains(run.Id))
                        throw new InvalidOperationException("Injected deterministic test failure.");
                }
                var artifact = await agent.GenerateAsync(CreateAgentRequest(run, node.Id), cancellationToken);
                if (node.Id == "tests" && (artifact.Tests.Length == 0 || artifact.Tests.Any(string.IsNullOrWhiteSpace)))
                    throw new AgentUnavailableException("Validation agent did not produce any concrete test checks.");
                node.Output = JsonSerializer.SerializeToElement(artifact, JsonOptions);
                node.Status = "succeeded";
                await RecordAsync(run, "node.succeeded",
                    new { nodeId = node.Id, attempt = node.Attempts, node.ExitGate }, cancellationToken);
                return;
            }
            catch (AgentUnavailableException error)
            {
                node.Error = error.Message;
                if (node.Attempts < maximumAttempts)
                {
                    run.Metrics.Retries++;
                    await RecordAsync(run, "node.retry",
                        new { nodeId = node.Id, attempt = node.Attempts, maximumAttempts, error = error.Message },
                        cancellationToken);
                    continue;
                }

                node.Status = "failed";
                await RecordAsync(run, "node.failed", new
                {
                    nodeId = node.Id,
                    classification = "agent_or_validation",
                    error = error.Message
                }, cancellationToken);
                await RollbackAsync(run, node, cancellationToken);
                run.Status = "safe_stopped";
                Finish(run);
                await RecordAsync(run, "run.safe_stopped",
                    new { reason = "Agent or validation retry budget exhausted.", nodeId = node.Id }, cancellationToken);
                return;
            }
            catch (InvalidOperationException error)
            {
                node.Error = error.Message;
                if (node.Attempts < maximumAttempts)
                {
                    run.Metrics.Retries++;
                    await RecordAsync(run, "node.retry",
                        new { nodeId = node.Id, attempt = node.Attempts, maximumAttempts, error = error.Message },
                        cancellationToken);
                    continue;
                }

                node.Status = "failed";
                await RecordAsync(run, "node.failed", new
                {
                    nodeId = node.Id,
                    classification = "validation",
                    error = error.Message
                }, cancellationToken);
                await RollbackAsync(run, node, cancellationToken);
                run.Status = "safe_stopped";
                Finish(run);
                await RecordAsync(run, "run.safe_stopped",
                    new { reason = "Validation retry budget exhausted.", nodeId = node.Id }, cancellationToken);
                return;
            }
        }
    }

    private EngineeringAgentRequest CreateAgentRequest(WorkflowRun run, string nodeId)
    {
        var inputs = run.Nodes[nodeId].DependsOn.ToDictionary(
            dependency => dependency,
            dependency => run.Nodes[dependency].Output?.Clone() ??
                JsonSerializer.SerializeToElement<object?>(null, JsonOptions),
            StringComparer.Ordinal);
        return new EngineeringAgentRequest(
            run.ScenarioId, run.ScenarioTitle, run.Nodes[nodeId].Stage, run.Requirements,
            run.RequirementVersion, [.. run.Assumptions], [.. run.AcceptanceCriteria], inputs);
    }

    private async Task RollbackAsync(
        WorkflowRun run,
        WorkflowNodeState failedNode,
        CancellationToken cancellationToken)
    {
        var implementation = run.Nodes["implementation"];
        if (implementation.Status != "succeeded") return;
        implementation.Status = "rolled_back";
        implementation.Output = null;
        run.Metrics.Rollbacks++;
        await RecordAsync(run, "node.rolled_back",
            new { nodeId = implementation.Id, trigger = failedNode.Id }, cancellationToken);
    }

    private static string[] Descendants(WorkflowRun run, string rootId)
    {
        var impacted = new HashSet<string>(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in run.Nodes.Values)
            {
                if (node.Id != rootId && !impacted.Contains(node.Id) &&
                    node.DependsOn.Any(dependency => dependency == rootId || impacted.Contains(dependency)))
                    changed |= impacted.Add(node.Id);
            }
        }
        return impacted.ToArray();
    }

    private WorkflowRun RequireRun(string runId) =>
        runs.TryGetValue(runId, out var run) ? run : throw new WorkflowException(404, "Run not found.");

    private void Finish(WorkflowRun run)
    {
        run.UpdatedAt = DateTimeOffset.UtcNow;
        run.Metrics.DurationMs = Math.Max(0, (long)(run.UpdatedAt - run.StartedAt).TotalMilliseconds);
    }

    private WorkflowRun Snapshot(WorkflowRun run)
    {
        var snapshot = new WorkflowRun
        {
            Id = run.Id,
            ScenarioId = run.ScenarioId,
            ScenarioTitle = run.ScenarioTitle,
            Requirements = run.Requirements,
            Assumptions = [.. run.Assumptions],
            AcceptanceCriteria = [.. run.AcceptanceCriteria],
            RequirementVersion = run.RequirementVersion,
            Status = run.Status,
            StartedAt = run.StartedAt,
            UpdatedAt = run.UpdatedAt,
            Metrics = new WorkflowMetrics
            {
                Retries = run.Metrics.Retries,
                Rollbacks = run.Metrics.Rollbacks,
                DurationMs = run.Metrics.DurationMs
            },
            Nodes = run.Nodes.ToDictionary(pair => pair.Key, pair =>
            {
                var node = pair.Value;
                return new WorkflowNodeState(new WorkflowNodeDefinition(
                    node.Id, node.Stage, [.. node.DependsOn], node.EntryGate, node.ExitGate,
                    node.RetryLimit, node.ApprovalRequired, node.HighImpact))
                {
                    Status = node.Status,
                    Attempts = node.Attempts,
                    Output = node.Output?.Clone(),
                    Error = node.Error
                };
            }, StringComparer.Ordinal)
        };
        snapshot.Audit.AddRange(run.Audit);
        snapshot.DecisionLineage.AddRange(run.DecisionLineage.Select(item => item.Clone()));
        snapshot.Metrics.SuccessRate = runs.IsEmpty ? 0 :
            (double)runs.Values.Count(item => item.Status == "succeeded") / runs.Count;
        var recovered = runs.Values.Where(item => item.Metrics.Retries > 0 || item.Metrics.Rollbacks > 0).ToArray();
        snapshot.Metrics.MeanTimeToRecoveryMs = recovered.Length == 0
            ? 0
            : (long)recovered.Average(item => item.Metrics.DurationMs);
        return snapshot;
    }

    private async Task RecordAsync(
        WorkflowRun run,
        string eventName,
        object details,
        CancellationToken cancellationToken)
    {
        var entry = new WorkflowAuditEvent(
            DateTimeOffset.UtcNow, run.Id, run.ScenarioId, run.RequirementVersion,
            eventName, JsonSerializer.SerializeToElement(details, JsonOptions));
        await auditGate.WaitAsync(cancellationToken);
        try
        {
            lock (run)
            {
                run.Audit.Add(entry);
                run.UpdatedAt = entry.Timestamp;
            }
            await repository.SaveRunAndAuditAsync(
                run.Id, run.ScenarioId, run.Status, run.RequirementVersion, run.StartedAt,
                run.UpdatedAt, SerializeRun(run), entry, cancellationToken);
        }
        finally
        {
            auditGate.Release();
        }
    }

    private static string SerializeRun(WorkflowRun run)
    {
        var dto = new PersistedRun(
            run.Id, run.ScenarioId, run.ScenarioTitle, run.Requirements,
            run.Assumptions, run.AcceptanceCriteria, run.RequirementVersion, run.Status,
            run.StartedAt, run.UpdatedAt,
            run.Nodes.Values.Select(node => new PersistedNode(
                node.Id, node.Stage, node.DependsOn, node.EntryGate, node.ExitGate,
                node.ApprovalRequired, node.HighImpact, node.RetryLimit, node.Status,
                node.Attempts, node.Output, node.Error)).ToArray(),
            run.Audit.ToArray(), run.DecisionLineage.ToArray(),
            run.Metrics.Retries, run.Metrics.Rollbacks, run.Metrics.DurationMs);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static WorkflowRun DeserializeRun(string payload)
    {
        var data = JsonSerializer.Deserialize<PersistedRun>(payload, JsonOptions)
            ?? throw new InvalidDataException("Stored workflow run is empty.");
        var run = new WorkflowRun
        {
            Id = data.Id,
            ScenarioId = data.ScenarioId,
            ScenarioTitle = data.ScenarioTitle,
            Requirements = data.Requirements,
            Assumptions = data.Assumptions,
            AcceptanceCriteria = data.AcceptanceCriteria,
            RequirementVersion = data.RequirementVersion,
            Status = data.Status,
            StartedAt = data.StartedAt,
            UpdatedAt = data.UpdatedAt,
            Nodes = data.Nodes.ToDictionary(node => node.Id, node =>
                new WorkflowNodeState(new WorkflowNodeDefinition(
                    node.Id, node.Stage, node.DependsOn, node.EntryGate, node.ExitGate,
                    node.RetryLimit, node.ApprovalRequired, node.HighImpact))
                {
                    Status = node.Status,
                    Attempts = node.Attempts,
                    Output = node.Output,
                    Error = node.Error
                }, StringComparer.Ordinal),
            Metrics = new WorkflowMetrics
            {
                Retries = data.Retries,
                Rollbacks = data.Rollbacks,
                DurationMs = data.DurationMs
            }
        };
        run.Audit.AddRange(data.Audit);
        run.DecisionLineage.AddRange(data.DecisionLineage);
        return run;
    }

    private sealed record PersistedRun(
        string Id, string ScenarioId, string ScenarioTitle, string Requirements, string[] Assumptions,
        string[] AcceptanceCriteria, int RequirementVersion, string Status, DateTimeOffset StartedAt,
        DateTimeOffset UpdatedAt, PersistedNode[] Nodes, WorkflowAuditEvent[] Audit,
        JsonElement[] DecisionLineage, int Retries, int Rollbacks, long DurationMs);

    private sealed record PersistedNode(
        string Id, string Stage, string[] DependsOn, string EntryGate, string ExitGate,
        bool ApprovalRequired, bool HighImpact, int RetryLimit, string Status, int Attempts,
        JsonElement? Output, string? Error);
}

public sealed class WorkflowException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
