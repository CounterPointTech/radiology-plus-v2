# Radiology Plus v2.1

A multi-tenant SaaS customization layer for NovaPACS and NovaRIS.

Radiology Plus is **two things**:

1. **Customer-facing validation tiers** — Tech, Billing, and Rad validation workflows that help each role catch and fix data quality problems before they become billing, reading, or compliance issues.
2. **NRS-only customization plumbing** — a scripting engine (T-SQL / PL/pgSQL / PowerShell / Batch) and notification subsystem that absorbs unexpected requirements without shipping new releases.

The product's promise is "make NovaRad workflows better." The scripting engine is what makes that promise keepable.

## Stack

| Layer | Tech |
|---|---|
| Web | Next.js (App Router), Tailwind v4, Motion (Framer Motion) + React Spring, shadcn/ui, TypeScript strict |
| API | .NET 10 minimal API, endpoints pattern, SignalR, JWT |
| Service | .NET 10 Worker host (MonitoringEngine, ScriptExecutionEngine, NotificationOrchestrator) |
| DB | PostgreSQL 18+ (multi-tenant: shared DB + `tenant_id` + Row-Level Security) |
| Logging | Serilog → file + console + Postgres sink + outbound Novarad audit sink |
| Auth | Federated against each tenant's Novarad `shared.users` over site-to-site VPN; NRS is local-only |

## Roles

- **NRS** — super-user with scripting engine access (local account, not federated)
- **Admin** — everything except scripting; **cannot** change NRS password
- **Tech** — Tech Validation only (federated)
- **Radiologist** — Rad Validation only (federated)

## Repository layout

```
src/
  RadiologyPlus.Web/            <-- Next.js (alias of apps/web for convenience)
  RadiologyPlus.API/            <-- .NET 10 minimal API
  RadiologyPlus.Service/        <-- .NET 10 Worker host
  RadiologyPlus.Core/           <-- domain, interfaces, enums
  RadiologyPlus.Data/           <-- Npgsql repos + Novarad connection pool
  RadiologyPlus.Scripting/      <-- script executors (T-SQL, PL/pgSQL, PowerShell, Batch)
  RadiologyPlus.Notifications/  <-- email (Graph), Teams, SMS, queue, templates
  RadiologyPlus.Common/         <-- shared helpers, encryption
  RadiologyPlus.Migrator/       <-- DB migration runner
apps/
  web/                          <-- Next.js app (canonical path)
tests/
  *.Tests/                      <-- xUnit projects per src project
docs/
  architecture.md
  runbook.md
  hipaa-checklist.md
References/                     <-- prototype + meeting notes + whiteboards + NRS scripts
```

## Development

### Prerequisites

- .NET SDK 10.0.203+
- Node 20+ (Node 20.19.0 confirmed)
- PostgreSQL 18+
- Windows or WSL (PowerShell available for PowerShell script executor)

### First-time setup

```powershell
# Restore .NET packages and build
dotnet build RadiologyPlus.sln

# Run migrations (after configuring connection string)
dotnet run --project src/RadiologyPlus.Migrator

# Web
cd apps/web
npm install
npm run dev
```

### Run

```powershell
# API (default https://localhost:7171)
dotnet run --project src/RadiologyPlus.API

# Service worker
dotnet run --project src/RadiologyPlus.Service

# Web (default http://localhost:3000)
cd apps/web; npm run dev
```

## Phased plan

See `C:\Users\Daniel\.claude\plans\hey-claude-my-friend-rustling-lighthouse.md` for the approved phased plan.

## License

Proprietary — NovaRad / iPro.
