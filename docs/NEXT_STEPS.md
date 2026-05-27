# Phase 0 — what's done and what's next

## What sessions 1–2 laid down

**Session 1** scaffolded the solution, the foundations migration, the core
contracts, the local-NRS login path, the scripting engine, the notification
queue, the Next.js shell, and CI.

**Session 2** built the federated Novarad credential validator (pure-.NET, no
Novarad DLLs) and the bootstrap CLI for the Migrator.

## Phase 0 completion table

| Feature | Status |
|---|---|
| P0.1 — Repo scaffold | done |
| P0.2 — Multi-tenant DB migration | done |
| P0.3 — Core contracts | done |
| P0.4a — Local NRS path + JWT + middleware | done |
| P0.4b — Federated Novarad credential validator | **done** *(session 2)* |
| P0.5 — Scripting engine + 4 executors | done |
| P0.6 — Notification queue + Graph email + LogOnly channels | done |
| P0.7 — Next.js shell + design tokens | done |
| P0.8 — CI workflow | done |
| P0.9 — Bootstrap CLI (init-tenant, create-nrs, add-facility) | **done** *(session 2)* |
| P0.10 — Container publish job in CI | todo |

## Verified

- `dotnet build RadiologyPlus.sln` → 0 errors, 8 NU1510 warnings (safelisted).

## Federated auth — implementation summary

`RadiologyPlus.NovaradAuth` is a new project (#9 in the solution) that
replicates Novarad's password algorithm in pure .NET. See
[`novarad-password-algorithm.md`](./novarad-password-algorithm.md) for the
reverse-engineered spec; the short version is:

- **format 0** — plaintext `citext` compare
- **format 1** — unsalted SHA-256 of UTF-16LE password, hyphen-hex uppercase
- **format 2** — AES (Rijndael) CBC/PKCS7, key=SHA256(UTF-16LE(systemEncryptionKey)), IV=password_salt, output hyphen-hex

Policy (from `decisions.md` 2026-05-11):

- MFA skipped in v1.
- Lockout shared with Novarad's `failed_password_attempt_count`, local cap 5.
- `anonymous=true` or `is_visible=false` → reject.
- `is_vendor=true` → `Role.Tech` default.
- `is_ldap_user=true` or `use_ad_authentication=true` → LDAP branch via
  `System.DirectoryServices.Protocols`.

DI in `RadiologyPlus.API/Program.cs` is now
`builder.Services.AddNovaradFederatedAuth(builder.Configuration)`; the stub
validator file is gone.

## Bootstrap CLI

`RadiologyPlus.Migrator` is now a subcommand dispatcher. Migration runner
(default behavior) preserved; three new commands:

```
init-tenant   --code --name --novarad-host [--novarad-port=5432] --novarad-db --novarad-user --novarad-password [--use-ssl=true]
create-nrs    --tenant --username [--display-name --email --password]
add-facility  --tenant --code --name --novarad-facility-id
```

When `create-nrs` is called without `--password`, a 16-character temporary
password is generated and printed once to stdout. Stack-up-from-zero example
is in `.claude/context/session-2.md`.

## What to pick up next

### Phase 0 cleanup (one item left)
- **Container publish job** in CI (`.github/workflows/build.yml`) — Docker
  image build + push for both API and Service workers on `main`.

### Pre-Phase-1 validation
- **Integration test** of the federated validator against a real Novarad-clone
  Postgres. Seed two users with different `password_format` values, plus an
  `is_vendor` account and an `is_visible=false` account; run `/auth/login`
  end-to-end.
- **Unit tests** for `NovaradPasswordHasher` — golden plaintext → stored value
  per format, anchored to the algorithm doc.
- **Smoke test** of the bootstrap CLI: init-tenant → add-facility →
  create-nrs → log in with the printed temp password.

### Phase 1 — Tech Validation (next big chunk)

- Migration `0002_tech_validation.sql` for `tech_validation.validations`,
  `tech_validation.tech_notes_templates`, materialized view
  `tech_validation.ready_studies`.
- `RadiologyPlus.Service/TechValidation/ReadyStudiesProjector.cs` — polls each
  tenant's Novarad for `custom_3='Ready'` + `last_image_processed_date`.
- `RadiologyPlus.API/Endpoints/TechValidationEndpoints.cs` — list ready
  studies, wizard step submissions, "Do the Do" trigger.
- `RadiologyPlus.Service/TechValidation/DoTheDoOrchestrator.cs` — PACS/RIS/FFI
  writes through `INovaradWriter`, emits SignalR progress events.
- Frontend: `app/(tech)/validation/page.tsx` (worklist) +
  `wizard/[studyId]/page.tsx` with Motion step transitions + cooking progress
  + `ding.mp3`.

## Open items still to resolve

- VPN concentrator choice (WireGuard / OpenVPN / vendor).
- SMS provider — Twilio assumed.
- HIPAA retention policy for `audit.access_logs` — 7-year default assumed.

## Helpful commands

```powershell
# Build everything
dotnet build RadiologyPlus.sln

# Stand up a fresh stack end-to-end (after creating the radiology_plus_v21 database)
$env:RADPLUS_ConnectionStrings__AppDb = "Host=localhost;Database=radiology_plus_v21;Username=postgres;Password=...;"
$env:RADPLUS_Encryption__Key          = "<base64 of 32 random bytes>"
dotnet run --project src/RadiologyPlus.Migrator
dotnet run --project src/RadiologyPlus.Migrator -- init-tenant `
    --code=salient --name="Salient Imaging" `
    --novarad-host=10.30.0.10 --novarad-db=novarad `
    --novarad-user=radiology_plus_app --novarad-password='<secret>'
dotnet run --project src/RadiologyPlus.Migrator -- add-facility `
    --tenant=salient --code=SAL-MAIN --name="Salient Main" --novarad-facility-id=1
dotnet run --project src/RadiologyPlus.Migrator -- create-nrs `
    --tenant=salient --username=nrs.dan --display-name="Dan (NRS)"

# Run the API (Swagger at https://localhost:7171/swagger)
dotnet run --project src/RadiologyPlus.API

# Run the Service worker
dotnet run --project src/RadiologyPlus.Service

# Web dev server (after npm install in apps/web)
cd apps/web; npm install; npm run dev
```
