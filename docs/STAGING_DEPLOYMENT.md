# Staging deployment (Docker + Traefik + PostgreSQL)

This document records how the POS API, POS Web dashboard, and a dedicated PostgreSQL database are
deployed as an **isolated staging environment** on the shared Docker/Traefik host, and how the
live PostgreSQL verification gate (STATUS.md's long-pending "PostgreSQL live rehearsal") is run
against it.

This is a staging deployment only. It does not authorize live-store rollout — see STATUS.md and
ROADMAP.md for the pilot-readiness gate that still has to pass first.

## What gets deployed

Three containers, isolated from every other tenant already running on the host:

- `pos-postgres` (`postgres:16-alpine`) — a dedicated database, its own named volume
  (`pos_staging_pgdata`), **no host port mapping** (reachable only from `pos-api`/`pos-web` on an
  internal Docker network, never from the host or the public internet).
- `pos-api` — built from [`deploy/Dockerfile.api`](../deploy/Dockerfile.api), routed by Traefik at
  `pos-api-staging.sparkco.vip`.
- `pos-web` — built from [`deploy/Dockerfile.web`](../deploy/Dockerfile.web), routed by Traefik at
  `pos-staging.sparkco.vip`, protected by Traefik HTTP basic auth (see "Access protection" below).

All three run under the Compose project name `pos`, on a private `pos_staging_internal` network
plus the host's existing shared `traefik-public` network (confirmed present on the server; every
other site on the box already uses the same network name and Traefik label convention this stack
follows).

## Access protection

- **PostgreSQL**: never exposed on a host port. This is the primary protection — nothing on the
  public internet, and nothing in another container outside the `pos_staging_internal` network,
  can reach it at all.
- **`pos-web`** (the human/browser-facing dashboard): Traefik HTTP basic auth
  (`TRAEFIK_BASIC_AUTH_USERS` in `.env`), on top of the app's own cookie login.
- **`pos-api`**: deliberately left off Traefik-level auth, so a WPF rehearsal client (see
  [STAGING_WPF_REHEARSAL.md](STAGING_WPF_REHEARSAL.md)) can reach it directly with its existing JWT
  login flow without new client-side code. It is not unprotected — every business endpoint already
  requires a JWT (rate-limited login, permission-gated mutations), and tenant provisioning is
  gated behind `Platform:ProvisioningSecret`. If tighter API-layer access control is wanted later,
  add a Traefik IP-allowlist middleware scoped to known operator IPs — that is a small follow-up,
  not attempted here since it was not requested and would need to be reconciled with the WPF
  rehearsal's connectivity requirement first.
- Secrets live only in `deploy/.env` on the server (gitignored, never committed — see
  [`.env.example`](../deploy/.env.example) for the template) and in Docker's own environment
  variable passing; nothing is baked into the built images.

## Prerequisites

- SSH access to the host (already verified: Docker 26.1.4, Compose v5.1.4, Traefik v3.6.1).
- The `traefik-public` Docker network already exists on the host (confirmed).
- Wildcard DNS for `*.sparkco.vip` already resolves to the host through Cloudflare (confirmed) — no
  new DNS records are needed for `pos-staging.sparkco.vip` / `pos-api-staging.sparkco.vip`.
- `/opt/sites/pos` on the host, created for this deployment.

## Deploy

From a machine with the repository checked out:

```bash
# 1. Sync the repository to the server (adjust the local path if needed).
rsync -az --delete -e "ssh -p 2222" \
  --exclude '.git' --exclude '**/bin' --exclude '**/obj' --exclude '**/node_modules' \
  ./ root@159.195.136.212:/opt/sites/pos/

# 2. On the server: create the real .env from the template (first deploy only).
ssh -p 2222 root@159.195.136.212
cd /opt/sites/pos/deploy
cp .env.example .env
# Edit .env: real POSTGRES_PASSWORD, JWT_SIGNING_KEY, PLATFORM_PROVISIONING_SECRET,
# TRAEFIK_BASIC_AUTH_USERS (see the comments in .env.example for how to generate each one).
nano .env

# 3. Build and start the stack.
docker compose -f docker-compose.staging.yml --env-file .env up -d --build

# 4. Apply migrations once, on a controlled first boot (see next section), then verify.
docker compose -f docker-compose.staging.yml logs -f api
```

### Applying migrations

The runtime image intentionally does not bundle the `dotnet-ef` CLI (kept slim). Migrations apply
through the API's own built-in `Database:ApplyMigrationsOnStartup` flag, matching
[API_DEPLOYMENT.md](API_DEPLOYMENT.md)'s existing "one controlled migrator boot" convention:

```bash
# In .env, set API_APPLY_MIGRATIONS_ON_STARTUP=true, then:
docker compose -f docker-compose.staging.yml --env-file .env up -d --build api
docker compose -f docker-compose.staging.yml logs api   # confirm it migrated and started cleanly

# Then set API_APPLY_MIGRATIONS_ON_STARTUP=false in .env and redeploy so no future restart
# re-races a migration:
docker compose -f docker-compose.staging.yml --env-file .env up -d api
```

### Seeding test data

Two independent seeding steps, both optional but recommended for a real staging rehearsal:

1. **Baseline demo data** — set `API_SEED_DEMO_DATA_ON_STARTUP=true` for the same first boot as the
   migration step above, then set it back to `false`. Creates the same demo store/products/users
   `DatabaseSeeder` creates locally (see `start.md` for the generated demo credentials — check the
   API container's startup logs on this deployment, since they are randomly generated per
   database).
2. **A second, explicitly separate tenant** — to actually exercise multi-tenant isolation on
   staging (not just a single demo business), provision one through the existing operator-only
   endpoint:

   ```bash
   curl -X POST https://pos-api-staging.sparkco.vip/api/platform/tenants \
     -H "X-Provisioning-Secret: <the PLATFORM_PROVISIONING_SECRET from .env>" \
     -H "Content-Type: application/json" \
     -d '{
       "tenantName": "Staging Rehearsal Co",
       "tenantSlug": "staging-rehearsal",
       "storeName": "Main Store",
       "adminUsername": "owner",
       "adminPassword": "a strong password, 8+ characters",
       "baseCurrencyCode": "ILS"
     }'
   ```

   This is the tenant the [WPF rehearsal](STAGING_WPF_REHEARSAL.md) uses.

## Verify

```bash
curl -s https://pos-api-staging.sparkco.vip/health
curl -s https://pos-api-staging.sparkco.vip/api/meta/ping
curl -s -u <basic-auth-user>:<basic-auth-password> https://pos-staging.sparkco.vip/ -o /dev/null -w '%{http_code}\n'
docker ps   # confirm only pos-postgres/pos-api/pos-web changed; every other container's uptime is untouched
```

## PostgreSQL live rehearsal

Generating migration SQL against Npgsql (the only verification available in every prior session,
with no Docker/PostgreSQL access) is explicitly **not** sufficient to close this gate. With the
staging `pos-postgres` container running, the actual rehearsal is:

1. **Fresh install** — the `up -d --build` + controlled-migration-boot sequence above, against a
   brand-new empty database, is itself the fresh-install rehearsal. A clean startup plus a
   successful `/health` check is the pass condition.
2. **Populated pre-upgrade migration, concurrency, rollback, replay-safety, and isolation** — run
   via a short-lived `dotnet/sdk:8.0` container attached to the same `pos_staging_internal`
   network, so `dotnet test` can reach `pos-postgres` directly without exposing it:

   ```bash
   docker run --rm -it \
     --network pos_staging_internal \
     -v /opt/sites/pos:/src -w /src \
     -e POS_TEST_DB_PROVIDER=Postgres \
     -e POS_TEST_PG_CONNECTION="Host=pos-postgres;Port=5432;Database=pos_test_rehearsal;Username=pos_app;Password=<POSTGRES_PASSWORD from .env>" \
     mcr.microsoft.com/dotnet/sdk:8.0 \
     dotnet test tests/POS.Tests/POS.Tests.csproj --filter "FullyQualifiedName~Postgres"
   ```

   `pos_test_rehearsal` is a separate logical database on the same Postgres server/container (not a
   second container/volume) so rehearsal fixtures never touch the real staging app data — Postgres
   creates it on first connection via the test harness. See
   [`tests/POS.Tests/PostgresMigrationRehearsalTests.cs`](../tests/POS.Tests/PostgresMigrationRehearsalTests.cs)
   for exactly what this proves (row counts, IDs, and financial/stock invariants preserved across a
   real migration of a populated database) and the `POS_TEST_DB_PROVIDER` opt-in that re-runs the
   full existing isolation/concurrency/replay-safety suite against real PostgreSQL instead of
   SQLite.

Actual results from running this are recorded in STATUS.md's Stage 4T section, not here — this
file documents the mechanism, not a point-in-time result.

## What this does not authorize

This is an isolated staging environment for verification and the WPF rehearsal only. Live-store
rollout remains pending until the PostgreSQL rehearsal above, the T7 pilot-readiness checks, and a
manual WPF click-through (see [STAGING_WPF_REHEARSAL.md](STAGING_WPF_REHEARSAL.md)) all pass.
