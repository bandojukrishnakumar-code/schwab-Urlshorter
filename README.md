# URL Shortener + Governed SDLC Orchestrator

A runnable .NET 10 / ASP.NET Core prototype for short links, click analytics, and a reviewable SDLC workflow. It uses only the .NET shared framework; no NuGet packages are required.

## Requirements and setup

- .NET 10 SDK

```powershell
dotnet build .\UrlShortener.sln
dotnet run --project .\tests\UrlShortener.Tests\UrlShortener.Tests.csproj --no-build
dotnet run --project .\src\UrlShortener\UrlShortener.csproj
```

The API listens on `http://localhost:3000`. Link records and append-only workflow audit events are stored under `src\UrlShortener\data` by default. Configure `ASPNETCORE_URLS`, `BASE_URL`, or `DATA_DIRECTORY` to override defaults. Use `scripts\smoke.ps1` to exercise the live API; use `scripts\demo.ps1` to run the three workflow scenarios through API approval/rejection.

Create a link and inspect its click analytics:

```powershell
$link = Invoke-RestMethod -Method Post http://localhost:3000/api/urls `
  -ContentType 'application/json' `
  -Body '{"url":"https://example.com/article","alias":"article1"}'
$link
Invoke-WebRequest -MaximumRedirection 0 $link.shortUrl
Invoke-RestMethod http://localhost:3000/api/urls/article1
```

`GET /article1` redirects to the target; `GET /api/urls/article1` returns the click count and referrer summary.

## SDLC scenarios

```powershell
.\scripts\demo.ps1
.\scripts\demo.ps1 -Approve
```

By default, the demo rejects each release gate and safely stops. `-Approve` explicitly approves the prototype release-readiness checkpoint. Neither option deploys.

For direct API use:

```powershell
$run = Invoke-RestMethod -Method Post http://localhost:3000/api/runs `
  -ContentType 'application/json' -Body '{"scenarioId":"greenfield"}'
$run.status
$run = Invoke-RestMethod -Method Post "http://localhost:3000/api/runs/$($run.id)/approval" `
  -ContentType 'application/json' `
  -Body '{"approved":true,"actor":"reviewer","reason":"Reviewed prototype risks."}'
```

Use scenario IDs `greenfield`, `brownfield`, and `ambiguous`. Reject with `approved:false` to safe-stop. To demonstrate retry and rollback, start a run with `{"scenarioId":"greenfield","forceTestFailure":true}`. To change requirements at a waiting or terminal checkpoint:

```powershell
Invoke-RestMethod -Method Post "http://localhost:3000/api/runs/$($run.id)/replan" `
  -ContentType 'application/json' `
  -Body '{"requirements":"Persist analytics and support expiring links."}'
```

## HTTP API

| Method | Path | Behavior |
| --- | --- | --- |
| `GET` | `/health` | Liveness check |
| `POST` | `/api/urls` | Create: `{"url":"https://example.com","alias":"optional"}` |
| `GET` | `/api/urls` | List links |
| `GET` | `/api/urls/{code}` | Read click and referrer statistics |
| `GET` | `/{code}` | Redirect and increment analytics |
| `GET` | `/api/scenarios` | Return assumptions, acceptance criteria, and dependency graphs |
| `POST` | `/api/runs` | Start a scenario (`scenarioId`, optional `forceTestFailure`) |
| `GET` | `/api/runs` | List process-local orchestration runs |
| `GET` | `/api/runs/{id}` | Read workflow state, outputs, audit history, and reliability metrics |
| `POST` | `/api/runs/{id}/approval` | Record `{ "approved": true, "actor": "...", "reason": "..." }` |
| `POST` | `/api/runs/{id}/replan` | Record `{ "requirements": "..." }` and rerun downstream work |

## Engineering summary

- **Greenfield:** short-link API, aliases, redirects, click analytics, and persistence.
- **Brownfield:** compatibility-focused durable analytics enhancement and regression validation.
- **Ambiguous:** normalize vague sharing/safety goals into explicit assumptions and open questions; require review before release.
- **Architecture, controls, risks, and trade-offs:** [docs/architecture.md](docs/architecture.md).
- **Validation:** dependency-free .NET test harness includes service/workflow tests and a live HTTP API smoke test.

## Limitations

JSON-file link persistence and in-memory orchestration runs are single-process prototype choices. The actor supplied to the approval API is audit metadata, not authenticated identity. Authentication, authorization, rate limiting, abuse reporting, destination reputation, analytics retention, database durability, multi-instance consistency, deployment, and production rollback are intentionally not implemented. The workflow generates deterministic review artifacts; it does not call a model, edit its own source, or deploy.
