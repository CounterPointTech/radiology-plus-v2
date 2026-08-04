# Running Radiology Plus in containers

Three stacks on one host — `radplus-dev`, `radplus-test`, `radplus-prod` — plus one
shared reverse proxy. They differ only in project name, env file and image tag.

**Test carries a real Novarad clone, so it is a production system for compliance
purposes**: same TLS, same secret handling, same audit logging, same encrypted backups.

---

## Host layout

```
/opt/radplus/
├── compose.yaml            # copied from the repo
├── compose.proxy.yaml      # copied from the repo
├── deploy.sh               # copied from the repo, chmod +x
├── docker/Caddyfile        # copied from the repo
├── env/
│   ├── dev.env             # 0600, root-owned, NEVER in git
│   ├── test.env
│   ├── prod.env
│   └── proxy.env
└── backups/                # nightly encrypted pg_dump, shipped off-box
```

## One-time provisioning

```bash
# 1. Docker Engine + compose plugin (Ubuntu 24.04)
curl -fsSL https://get.docker.com | sh

# 2. The network the proxy uses to reach every stack
docker network create radplus-edge

# 3. Layout and permissions
sudo mkdir -p /opt/radplus/{env,backups,docker}
sudo chmod 700 /opt/radplus/env

# 4. Copy compose.yaml, compose.proxy.yaml, docker/Caddyfile and deploy/deploy.sh
#    from the repo into /opt/radplus, then:
sudo chmod +x /opt/radplus/deploy.sh

# 5. One env file per stack, from env/example.env. Generate distinct secrets per stack:
openssl rand -base64 32   # ENCRYPTION_KEY  (must decode to exactly 32 bytes)
openssl rand -base64 64   # JWT_SECRET
openssl rand -base64 24   # POSTGRES_PASSWORD
sudo chmod 600 /opt/radplus/env/*.env      # deploy.sh refuses to run otherwise

# 6. Firewall: only 80/443 reach the world. Postgres is never published.
sudo ufw allow 22,80,443/tcp && sudo ufw enable
```

**Back up `ENCRYPTION_KEY` somewhere other than the database backups.** It decrypts every
per-tenant Novarad and M\*Modal credential in `tenancy.*`, there is no key id in the
ciphertext, and losing it makes those rows unrecoverable. If the key and the encrypted
backup live in the same place, the encryption is decorative.

## First deploy of a stack

```bash
cd /opt/radplus
docker compose -p radplus-test --env-file env/test.env -f compose.yaml up -d

# Bootstrap the tenant (once per stack). The migrator encrypts the Novarad password
# with the same ENCRYPTION_KEY the hosts read, because compose feeds both from one value.
CC="docker compose -p radplus-test --env-file env/test.env -f compose.yaml"
$CC run --rm --no-deps migrator init-tenant \
    --code=salient --name="Salient Imaging" \
    --novarad-host=10.30.0.10 --novarad-db=novarad \
    --novarad-user=radiology_plus_app --novarad-password="$NOVARAD_PW"
$CC run --rm --no-deps migrator add-facility --tenant=salient --code=AHC --name="AHC" --novarad-facility-id=2
$CC run --rm --no-deps migrator create-nrs --tenant=salient --username=nrs.dan --display-name="Dan"

# Then the proxy
docker compose -p radplus-proxy --env-file env/proxy.env -f compose.proxy.yaml up -d
```

## Runbook

| Task | Command |
|---|---|
| Deploy | `/opt/radplus/deploy.sh test sha-1a2b3c4` |
| **Roll back** | `/opt/radplus/deploy.sh test sha-<previous>` — same command, earlier tag |
| Apply migrations only | `docker compose -p radplus-test --env-file env/test.env -f compose.yaml run --rm migrator` |
| Logs | `docker compose -p radplus-test --env-file env/test.env -f compose.yaml logs -f api` |
| What is deployed? | `curl -s https://rp-test.example.com/api/diagnostics/version` |

### After changing a Novarad or M\*Modal connection

```bash
docker compose -p radplus-test --env-file env/test.env -f compose.yaml \
    restart api adminapi service adminservice
```

`NovaradConnectionPool` caches per-tenant data sources in process memory and only clears
on restart. Skipping this is the most common "why didn't my change take effect".

### Backups

```bash
# Nightly, via systemd timer or cron:
docker compose -p radplus-prod --env-file env/prod.env -f compose.yaml exec -T postgres \
    pg_dump -U postgres -Fc rad_plus \
  | gpg --encrypt --recipient backups@counterpointtech.com \
  > /opt/radplus/backups/prod-$(date +%F).dump.gpg
```

Ship them off-box, and **restore one into a scratch stack before you need to.** An
untested backup is a hope, not a control.

---

## Things that will bite

- **Postgres is never published.** The `system_bypass` RLS policy means a connection that
  never sets `app.tenant_id` sees every tenant's rows, so an exposed port plus one leaked
  credential is a full-database PHI incident.
- **Never let the app connect as a Postgres superuser** — a superuser bypasses RLS even
  with `FORCE ROW LEVEL SECURITY` set. Give the four hosts a non-owner role.
- **The migrator reads `RADPLUS_`-prefixed config; everything else reads unprefixed.**
  `compose.yaml` maps both from one value, so this only matters if you run the migrator
  by hand outside compose.
- **`TechValidation__LookbackWindow`** is load-bearing, not tuning. On the 7-day default
  the projector finds nothing and prunes the entire worklist within four hours.
- **`NEXT_PUBLIC_*` is inlined at `next build`.** The web images are built same-origin
  (`/api`) so one image is promotable; a rebuild with different values is a different
  artifact. Never build with `NEXT_PUBLIC_SHOW_DEV_TOOLS=true` — it exposes a button that
  wipes the worklist.
- **Do not scale the API past one replica.** SignalR has no backplane configured, so a
  second replica silently drops worklist updates for half the users — which looks like a
  data bug, not an infrastructure one.
- **Containers default to UTC.** `TZ` is set explicitly because billing windows and the
  projector read local wall-clock time.
