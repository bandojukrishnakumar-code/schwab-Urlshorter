# Architecture, Governance, and Risk

## Components

- **ASP.NET Core API (`src/UrlShortener/Program.cs`):** link and workflow routes, bounded JSON requests, explicit validation errors, and redirects.
- **URL store (`src/UrlShortener/UrlStore.cs`):** HTTP(S)-only destination validation, collision-safe aliases, process-wide serialization of updates, atomic JSON-file replacement, and click/referrer analytics.
- **Scenario catalog (`src/UrlShortener/ScenarioCatalog.cs`):** greenfield, brownfield, and ambiguous requirement sets, assumptions, acceptance criteria, and gates.
- **Orchestrator (`src/UrlShortener/Orchestrator.cs`):** explicit dependency graph, parallel branches, run state, decision lineage, bounded retries, prototype rollback, replanning, and audit events.
- **Validation (`tests/UrlShortener.Tests`):** framework-only executable test harness with unit, workflow, and live HTTP integration coverage.

## Dependency graph and flow

```text
requirements -> architecture -> implementation -> tests --------┐
                                               └-> documentation ├-> review
                                                                  -> release-approval (human)
                                                                  -> release-readiness (no deploy)
```

Tests and documentation run concurrently after implementation. Review is a synchronization barrier over both branches. Every graph node declares an entry and exit gate. A failed dependency prevents downstream progress.

Release is explicitly gated by a human approval/rejection. Rejection safe-stops the run. Approval only completes a prototype readiness checklist; it never deploys. API clients can revise requirements only at a waiting or terminal checkpoint. A replan increments the requirement version, invalidates the transitive downstream outputs, records lineage, and re-executes the graph.

## Guardrails, audit, and reliability

- Only absolute HTTP(S) destinations are accepted; the service never fetches user-provided URLs.
- Aliases are limited to 4–32 URL-safe characters; duplicates return `409`.
- Request bodies are capped at 16 KiB, including chunked requests.
- The test node has one retry. Exhaustion records failure, invalidates the implementation artifact, records rollback, and safe-stops without a success-shaped fallback.
- Approval, rejection, state transitions, retries, rollback, and replans are written as JSON Lines audit events with run ID, timestamp, scenario, and requirement version.
- Run snapshots report retry/rollback counts, success rate, end-to-end latency, and a basic MTTR proxy. These metrics are illustrative: success rate is process-local, and MTTR is the average full duration of runs with recovery activity, not incident recovery telemetry.

The workflow workers produce deterministic review artifacts. This demonstrates orchestration mechanics and traceability, not an LLM's ability to generate or validate application code.

## Risks and trade-offs

| Concern | Prototype choice | Risk and follow-up |
| --- | --- | --- |
| Persistence | Single-process JSON file for URLs; in-memory workflow runs | Run state is lost on restart; JSON is not multi-process safe. Use a transactional database and durable workflow engine before scaling. |
| Authorization | No authentication; approval records a supplied actor string | Actor is not identity proof. Add authenticated ownership and reviewer roles before network exposure. |
| Abuse and link safety | Strict HTTP(S) validation; no target fetch | Phishing and abuse remain possible. Add rate limits, reporting, and reviewed reputation policy. |
| Analytics privacy | Referrer hostname counters | Retention and privacy requirements remain unresolved. Define policy and deletion/retention mechanisms first. |
| Workflow recovery | Bounded local retry; clears in-memory implementation output | Does not compensate external side effects. Use idempotency and explicit compensating actions for integrations. |
| Audit durability | JSONL append on local disk | Not tamper-evident, transactionally coupled, or centrally retained. Protect and centralize audit storage in production. |
| Release | Approval gate only; no deployment action | Real release, environment validation, and deployment rollback are intentionally out of scope. |
