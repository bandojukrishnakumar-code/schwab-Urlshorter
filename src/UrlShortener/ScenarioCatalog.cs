namespace UrlShortener;

public sealed record WorkflowNodeDefinition(
    string Id,
    string Stage,
    string[] DependsOn,
    string EntryGate,
    string ExitGate,
    int RetryLimit = 0,
    bool ApprovalRequired = false,
    bool HighImpact = false);

public sealed record ScenarioDefinition(
    string Id,
    string Title,
    string Requirements,
    string[] Assumptions,
    string[] AcceptanceCriteria,
    WorkflowNodeDefinition[] Nodes);

public static class ScenarioCatalog
{
    private static WorkflowNodeDefinition[] Graph() =>
    [
        new("requirements", "requirements", [], "Requirement exists and scope is understood.",
            "Normalized problem, assumptions, and acceptance criteria are recorded."),
        new("architecture", "architecture", ["requirements"], "Requirements passed their exit gate.",
            "Component boundaries and decisions are documented."),
        new("implementation", "implementation", ["architecture"], "Architecture is approved for implementation.",
            "A bounded implementation plan and change set are recorded."),
        new("change-approval", "governance", ["implementation"],
            "Implementation proposal is generated and remains unapplied.",
            "An authorized human accepts or rejects the proposed change set.",
            ApprovalRequired: true, HighImpact: true),
        new("tests", "testing", ["change-approval"], "Implementation proposal passed human review.",
            "Validation evidence and remaining failures are recorded.", RetryLimit: 1),
        new("documentation", "documentation", ["change-approval"], "Implementation proposal passed human review.",
            "Setup, API, and operating guidance are recorded."),
        new("review", "quality", ["tests", "documentation"],
            "Testing and documentation branches have both completed.",
            "Risks, policy checks, and release recommendation are recorded."),
        new("release-approval", "release", ["review"], "Review passed and release scope is bounded.",
            "An authorized human explicitly approves release readiness.",
            ApprovalRequired: true, HighImpact: true),
        new("release-readiness", "release", ["release-approval"], "Release approval is recorded.",
            "Prototype release checklist is complete; deployment remains out of scope.")
    ];

    private static readonly ScenarioDefinition[] Definitions =
    [
        new("greenfield", "Greenfield: create URL analytics",
            "Add a short-link creation API with optional aliases and click analytics. Use HTTP(S) destinations only.",
            ["The prototype stores data in a local JSON file.",
             "Authentication, abuse prevention, and production deployment are out of scope."],
            ["Create and resolve a short URL.",
             "Reject unsupported destination schemes and duplicate aliases.",
             "Record click counts and bounded referrer counts.",
             "Exercise API and persistence behavior with automated tests."],
            Graph()),
        new("brownfield", "Brownfield: add durable click statistics",
            "Enhance an existing shortener to persist click counts and last-access time without changing existing short-link behavior.",
            ["Existing short codes and destinations remain stable.",
             "A migration to a production database is not part of this prototype."],
            ["Existing short links continue to resolve.",
             "Click statistics survive process restart.",
             "Concurrent resolutions do not silently lose recorded clicks.",
             "Regression tests cover existing and changed behavior."],
            Graph()),
        new("ambiguous", "Ambiguous: define a safe sharing experience",
            "Make links easier to share and understand, while keeping users safe.",
            ["“Easier to share” means readable aliases and a copyable short URL, not social integrations.",
             "“Safe” means strict HTTP(S) validation and no destination fetching in this release.",
             "Abuse reporting, authentication, privacy retention, and link expiration need product decisions."],
            ["Present open questions separately from accepted assumptions.",
             "Validate destination scheme without fetching user-provided URLs.",
             "Identify security and privacy trade-offs for human review.",
             "Block release until a human reviews scope and risk."],
            Graph())
    ];

    public static IReadOnlyList<ScenarioDefinition> All => Definitions;

    public static ScenarioDefinition? Find(string? scenarioId) =>
        Definitions.SingleOrDefault(scenario =>
            string.Equals(scenario.Id, scenarioId, StringComparison.OrdinalIgnoreCase));
}
