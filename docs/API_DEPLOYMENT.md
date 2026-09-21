# API deployment and migrations

This document records the current Stage 4.5 operational workflow for running the desktop client locally with SQLite and the API against PostgreSQL.

## Current database model

- `POS.Wpf` defaults to SQLite with `Data Source=pos.db` in [../src/POS.Wpf/appsettings.json](../src/POS.Wpf/appsettings.json).
- `POS.Api` defaults to PostgreSQL with `Database:Provider=Postgres` in [../src/POS.Api/appsettings.json](../src/POS.Api/appsettings.json).
- `POS.Wpf` still applies migrations and seeds demo data automatically on startup for the local single-machine flow.
- `POS.Api` now defaults to `Database:ApplyMigrationsOnStartup=false` and `Database:SeedDemoDataOnStartup=false` outside development, so server rollout does not rely on every API replica racing to migrate or seed.
- EF design-time tooling now follows the same provider-selection rules as runtime: it reads `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and optional CLI overrides.

## Runtime configuration

### Desktop client

The WPF client uses SQLite by default:

```json
{
  "ConnectionStrings": {
    "Default": "Data Source=pos.db"
  }
}
```

Relative SQLite paths are resolved under the app base directory, so `pos.db` is created next to the executable.

### API host

The API defaults to PostgreSQL:

```json
{
  "Database": {
    "Provider": "Postgres",
    "ApplyMigrationsOnStartup": false,
    "SeedDemoDataOnStartup": false
  },
  "Http": {
    "RequireHttps": true,
    "UseHsts": true
  },
  "ReverseProxy": {
    "UseForwardedHeaders": true,
    "ForwardLimit": 1,
    "KnownProxies": [],
    "KnownNetworks": []
  },
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=pos_api;Username=postgres;Password=change-me"
  }
}
```

Development overrides live in [../src/POS.Api/appsettings.Development.json](../src/POS.Api/appsettings.Development.json), where automatic migration/seeding and the local development JWT signing key are enabled for local runs.

For deployment, prefer environment variables over editing committed config:

```powershell
$env:Database__Provider = "Postgres"
$env:Database__ApplyMigrationsOnStartup = "false"
$env:Database__SeedDemoDataOnStartup = "false"
$env:Http__RequireHttps = "true"
$env:Http__UseHsts = "true"
$env:ReverseProxy__UseForwardedHeaders = "true"
$env:ConnectionStrings__Default = "Host=db.example;Port=5432;Database=pos_api;Username=pos_app;Password=change-me"
$env:Jwt__SigningKey = "replace-with-a-real-32-char-minimum-secret"
```

The API now refuses to start outside development if `Jwt:SigningKey` is missing or still looks like a placeholder/development secret.

### Tenant provisioning (creating a second business)

`POST /api/platform/tenants` creates a new tenant with its first store and administrator — the "minimal authenticated provisioning" tenant.md T2 calls for, deliberately not a public signup page. It is disabled by default; set `Platform:ProvisioningSecret` to enable it, then present that value on every call via the `X-Provisioning-Secret` header (a wrong or missing header returns 401, a disabled endpoint returns 404 — same response either way to anyone probing without the secret):

```powershell
$env:Platform__ProvisioningSecret = "replace-with-an-operator-only-secret"
```

```
POST /api/platform/tenants
X-Provisioning-Secret: <the configured secret>
Content-Type: application/json

{
  "tenantName": "Example Retail Co",
  "tenantSlug": "example-retail",
  "storeName": "Main Store",
  "adminUsername": "owner",
  "adminPassword": "a strong password, 8+ characters",
  "baseCurrencyCode": "ILS"
}
```

Only an operator with the shared secret can call this — there is still no in-app UI for it, and no platform-administrator identity concept yet (see STATUS.md Stage 4T/T2 for what's deliberately still missing: rate limiting, an audit trail for this endpoint, and self-service).

## HTTPS and reverse proxy conventions

- `Http:RequireHttps=true` enables `UseHttpsRedirection()` outside development.
- `Http:UseHsts=true` enables HSTS outside development.
- `ReverseProxy:UseForwardedHeaders=true` enables `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` processing before HTTPS redirection.
- `ReverseProxy:ForwardLimit` defaults to `1`, which matches a single proxy or load balancer hop.
- `ReverseProxy:KnownProxies` and `ReverseProxy:KnownNetworks` should be populated when TLS terminates on a non-loopback proxy or load balancer so forwarded headers are only trusted from infrastructure you control.

Example reverse-proxy trust configuration:

```json
{
  "ReverseProxy": {
    "UseForwardedHeaders": true,
    "ForwardLimit": 1,
    "KnownProxies": ["10.0.0.10"],
    "KnownNetworks": ["10.0.1.0/24"]
  }
}
```

If TLS terminates at a reverse proxy and `UseForwardedHeaders` is not configured correctly, the API may see the inner HTTP hop instead of the original HTTPS request and redirect unexpectedly. For same-host reverse proxies on loopback, the default forwarded-header trust model is typically sufficient.

## Migration workflow

### Apply SQLite migrations for the desktop app

```powershell
dotnet ef database update \
  --project src/POS.Infrastructure/POS.Infrastructure.csproj \
  --startup-project src/POS.Wpf/POS.Wpf.csproj
```

### Apply PostgreSQL migrations for the API

```powershell
dotnet ef database update \
  --project src/POS.Infrastructure/POS.Infrastructure.csproj \
  --startup-project src/POS.Api/POS.Api.csproj
```

Because the design-time factory now honors the startup project's configuration, the command above uses the API's PostgreSQL settings automatically.

If you want one API instance to perform startup migration during a controlled rollout, enable it explicitly for that instance only:

```powershell
$env:Database__ApplyMigrationsOnStartup = "true"
$env:Database__SeedDemoDataOnStartup = "false"
dotnet run --project src/POS.Api/POS.Api.csproj
```

Leave the setting disabled on all other replicas.

### Override provider or connection explicitly

If you need a one-off target without relying on the startup project's appsettings, pass overrides after `--`:

```powershell
dotnet ef database update \
  --project src/POS.Infrastructure/POS.Infrastructure.csproj \
  --startup-project src/POS.Api/POS.Api.csproj \
  -- \
  --provider Postgres \
  --connection "Host=db.example;Port=5432;Database=pos_api;Username=pos_app;Password=change-me"
```

Supported design-time overrides:

- `--provider`
- `--connection`
- `--base-path` for relative SQLite file resolution

## Creating new migrations

Create migrations from the repository root:

```powershell
dotnet ef migrations add <Name> \
  --project src/POS.Infrastructure/POS.Infrastructure.csproj \
  --startup-project src/POS.Api/POS.Api.csproj \
  --output-dir Data/Migrations
```

Use the API as the default startup project for new migrations so the operational server path stays the reference configuration. Use the WPF startup project only when you specifically need to validate local SQLite behavior.

## Deployment guidance

### Local single-machine deployment

- Run `POS.Wpf` on Windows.
- Let startup apply migrations automatically.
- Back up `pos.db` regularly. See [SQLITE_DEPLOYMENT.md](SQLITE_DEPLOYMENT.md).

### Server deployment

- Provision PostgreSQL first.
- Set `Database__Provider=Postgres` and `ConnectionStrings__Default` for the API host.
- Set a production-grade `Jwt__SigningKey`.
- Keep `Http__RequireHttps=true` and `Http__UseHsts=true` unless you are intentionally running behind a non-public internal hop during a controlled transition.
- If TLS terminates at a reverse proxy or load balancer, set `ReverseProxy__UseForwardedHeaders=true` and configure `ReverseProxy__KnownProxies` and/or `ReverseProxy__KnownNetworks` for the trusted ingress path.
- Run the migration command once during deployment, or enable `Database__ApplyMigrationsOnStartup=true` on exactly one controlled startup instance.
- Expose the API over HTTPS, typically via a reverse proxy or load balancer with TLS termination.

### Multi-instance rollout sequencing

Recommended sequence:

1. apply migrations out of band with `dotnet ef database update`, or start one designated migrator instance with `Database__ApplyMigrationsOnStartup=true`
2. verify `/health` against the upgraded database
3. start or rotate the remaining API replicas with `Database__ApplyMigrationsOnStartup=false`, `Http__RequireHttps=true`, and the correct reverse-proxy trust configuration
4. keep `Database__SeedDemoDataOnStartup=false` in shared or production environments unless you are intentionally bootstrapping an empty demo deployment

## Operational notes

- `/health` checks whether the configured database is reachable.
- `/api/meta/ping` reports the active database provider.
- Swagger is enabled only in development.
- Automatic seeding still creates the demo store and users when enabled and the target database is empty, so shared or production deployments should keep seeding disabled and provision real credentials intentionally.
- Forwarded headers are only useful when the trusted proxy path is configured correctly; otherwise the host falls back to direct request behavior.