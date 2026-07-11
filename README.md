# POS System

A professional **Point of Sale** system for small to medium retail businesses: offline-first, WPF desktop client, with a path toward multi-branch sync and a web dashboard.

## Documentation

| Document | Purpose |
|----------|---------|
| [pos_system_master_plan.md](pos_system_master_plan.md) | Vision, features, phases, architecture overview |
| [stack.md](stack.md) | Technology choices and libraries |
| [database.md](database.md) | Schema, indexes, sync-ready fields |
| [ddd.md](ddd.md) | Bounded contexts, aggregates, domain rules |
| [ROADMAP.md](ROADMAP.md) | Stages and steps from start to finish |
| [STATUS.md](STATUS.md) | What is done vs pending |
| [docs/CODING_STANDARDS.md](docs/CODING_STANDARDS.md) | Layout, naming, MVVM, EF migrations |
| [docs/API_DEPLOYMENT.md](docs/API_DEPLOYMENT.md) | API/runtime provider setup, deployment vars, migration workflow |

## Tech stack (summary)

- **Desktop POS:** .NET (LTS), WPF, MVVM (e.g. CommunityToolkit.Mvvm)
- **Local DB:** SQLite; **server (future):** PostgreSQL — **ORM:** Entity Framework Core
- **Patterns:** Modular monolith, Clean Architecture (Core → Application → Infrastructure → Presentation)

## Principles

- Offline-first; GUIDs and audit fields for future sync
- Separation of concerns; interfaces for hardware and data access
- RTL and multi-language (Arabic, English, Hebrew) via `.resx`

## Repository status

**Stages 0-4 are implemented:** foundation, MVP POS flow, operations/UX, multi-currency, persisted store settings, stock movement history, audit logging, and the API/sync milestone are in place. Stage 5.1 is now in progress with a `POS.Web` dashboard slice that includes reports, store/user/category administration, and product/inventory management. See [STATUS.md](STATUS.md).

## Solution structure

```
POS.sln
src/
  POS.Core/              # Domain entities (e.g. Store)
  POS.Application/       # Use cases (Modules/* per bounded context)
  POS.Infrastructure/    # EF Core, SQLite, migrations
   POS.Api/               # ASP.NET Core API + sync endpoints
   POS.Web/               # ASP.NET Core MVC admin dashboard host
  POS.Wpf/               # WPF shell (Windows UI)
tests/
  POS.Tests/             # xUnit
```

## Getting started

1. Install [.NET SDK 8](https://dotnet.microsoft.com/download). The repo pins a version in [`global.json`](global.json); matching it avoids SDK mismatches.
2. **Optional (Linux/macOS):** if you use a [install script](https://learn.microsoft.com/dotnet/core/install/linux-scripted-manual) SDK under `~/.dotnet`, add to your shell profile:
   - `export DOTNET_ROOT="$HOME/.dotnet"`
   - `export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"`
3. Restore and build:
   - `dotnet restore`
   - `dotnet build`  
   The WPF project sets `EnableWindowsTargeting` so it can compile on non-Windows machines; **running the POS UI requires Windows** (WPF).
4. **Run tests:** `dotnet test`
5. **Run the desktop app (Windows):** set `POS.Wpf` as startup project and run, or `dotnet run --project src/POS.Wpf/POS.Wpf.csproj`. On first launch, sign in with the seeded usernames **`cashier`** or **`admin`**. The current WPF login flow is username-only.
6. **Run the API:** `dotnet run --project src/POS.Api/POS.Api.csproj`. In development, `appsettings.Development.json` supplies the local JWT signing key and enables startup migration/seeding for the local PostgreSQL flow.
7. **Run the web dashboard:** `dotnet run --project src/POS.Web/POS.Web.csproj`. Sign in with **`admin`**. The current Stage 5.1 slice includes reports plus editable store profile/settings/currency policy, user management, inline category create/rename/delete, product/inventory administration, and product-, movement-type-, and date-range-filtered stock-movement visibility with quick date presets for today, yesterday, the last 7 days, the last 30 days, and this month, and is restricted to Admin or Manager roles.
8. **EF Core CLI** (new migrations): `dotnet tool install dotnet-ef --global --version 8.0.11` (or align with your EF package version). Create migrations from repo root:
   - `dotnet ef migrations add <Name> --project src/POS.Infrastructure/POS.Infrastructure.csproj --startup-project src/POS.Api/POS.Api.csproj --output-dir Data/Migrations`
9. **Database deployment and migration workflow:** see [docs/API_DEPLOYMENT.md](docs/API_DEPLOYMENT.md) for SQLite-vs-PostgreSQL provider selection, environment variables, startup migration flags, HTTPS/reverse-proxy settings, and safer multi-instance rollout guidance.

On first run, the app applies migrations, seeds the demo store/users if needed, and creates `pos.db` next to the executable (per `appsettings.json`). Logs go under `logs/`.

## License

Specify your license here when you choose one.
