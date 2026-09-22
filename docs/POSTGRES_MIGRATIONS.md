# PostgreSQL migrations: a separate history from SQLite

## What happened and why this exists

The very first live PostgreSQL rehearsal against this project (2026-09-22, against the staging
Docker/Traefik host) found a real, functional-breaking bug: applying this repository's existing
migration history (`src/POS.Infrastructure/Data/Migrations/`) directly against a real PostgreSQL
database produces a schema where every `Guid`/`DateTime`/`bool`/`decimal` column is typed as
`text`/`integer` instead of `uuid`/`timestamp with time zone`/`boolean`/`numeric`. This is not
cosmetic — it broke the very first real query:

```
Npgsql.PostgresException: 42804: argument of NOT must be type boolean, not type integer
```

The root cause: every migration in that history was scaffolded with the SQLite provider active
(a deliberate, documented workaround for a *different*, earlier gotcha — see
[STATUS.md](../STATUS.md)'s Stage 4T history — where scaffolding against `POS.Api`'s
Postgres-by-default configuration produced a bogus ~340-operation diff). EF Core bakes the active
provider's native type strings into each migration's `Up()`/`Down()` methods at scaffold time;
replaying those exact strings against a different provider does not re-infer correct types.

Static verification (`dotnet ef migrations script` against the Npgsql provider) had been treated
as sufficient evidence in every prior session, explicitly because no live PostgreSQL instance was
available to actually apply the history. That check renders SQL text but does not catch this
class of bug — it was never actually proven correct until a live database existed. Generating
migration SQL alone does not verify a migration history; applying it does.

## The fix

Per EF Core's own documented pattern for one `DbContext` type used with multiple providers
([Migrations with Multiple Providers](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers)),
PostgreSQL now has its own, independent migration history:

- `src/POS.Infrastructure/Data/Migrations/` — SQLite, unchanged, still the desktop client's and
  the whole test suite's migration path.
- `src/POS.Infrastructure.Migrations.Postgres/Migrations/` — PostgreSQL, new. A single
  `InitialPostgresSchema` migration that creates the full current schema from scratch with proper
  native types, plus the one seed-data statement the SQLite history needed
  (`AddCurrenciesAndStorePolicy`'s hand-authored `INSERT` for the three global reference
  currencies — SQLite-specific raw SQL that a from-scratch squash does not replay automatically).

`DependencyInjection.ConfigurePosDbContext` selects the migrations assembly based on the active
provider:

```csharp
options.UseNpgsql(connectionString, npgsql =>
    npgsql.MigrationsAssembly("POS.Infrastructure.Migrations.Postgres"));
```

`POS.Api` and `POS.Web` both carry a `ProjectReference` to the new project purely so its assembly
is present in the published output — neither references its types directly.

## What this means going forward

**Every schema change now needs two migrations, not one**, until/unless a real production
PostgreSQL deployment with real data makes squashing impractical:

1. Add the SQLite migration as before (`dotnet ef migrations add <Name> --project src/POS.Infrastructure/POS.Infrastructure.csproj --startup-project src/POS.Api/POS.Api.csproj --output-dir Data/Migrations`), forcing the provider explicitly (`Database__Provider=Sqlite` — see the existing gotcha in project memory about `POS.Api`'s Postgres-by-default config).
2. Add the equivalent PostgreSQL migration:
   ```bash
   Database__Provider=Postgres ConnectionStrings__Default="Host=localhost;Port=5432;Database=pos_design;Username=pos_app;Password=design" \
   dotnet ef migrations add <Name> \
     --project src/POS.Infrastructure.Migrations.Postgres/POS.Infrastructure.Migrations.Postgres.csproj \
     --startup-project src/POS.Api/POS.Api.csproj \
     --context POS.Infrastructure.Data.PosDbContext \
     -- --provider Postgres --connection "Host=localhost;Port=5432;Database=pos_design;Username=pos_app;Password=design"
   ```
   (The connection string only needs to be *syntactically valid* — `migrations add` diffs the model, it does not connect to a live database. A real connection is only needed for `database update`/`Database.Migrate()`.)
3. Verify both apply cleanly — SQLite via the normal `dotnet test` run (169+ tests), PostgreSQL via
   the live rehearsal process in [STAGING_DEPLOYMENT.md](STAGING_DEPLOYMENT.md).

Since staging is the *first* real PostgreSQL deployment this project has ever had, `InitialPostgresSchema`
is deliberately a single from-scratch migration rather than a step-by-step mirror of the SQLite
history's ~20 migrations — there is no pre-existing PostgreSQL data to preserve. If a real
production PostgreSQL database accumulates data before a future schema change, that change's
Postgres migration must be a normal incremental `AlterColumn`/`AddColumn` diff against
`InitialPostgresSchema`, the same as any other migration history — the squash was a one-time
starting point, not an ongoing pattern.

## Verified evidence, not assumed

See STATUS.md's dated Stage 4T entry for the actual commands run and results: a fresh
`Database:ApplyMigrationsOnStartup=true` boot against an empty PostgreSQL database, real tenant
provisioning, a real product/category/invoice sync push, and a real 10-way concurrent invoice push
against the same product — all executed against the live staging PostgreSQL container, not
inferred from generated SQL.
