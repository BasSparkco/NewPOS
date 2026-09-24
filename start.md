dotnet run --project src\POS.Wpf\POS.Wpf.csproj

## ⏸ Resume here — WPF staging rehearsal paused 2026-09-25, mid-session

The project owner is mid-way through the manual WPF staging rehearsal (`docs/STAGING_WPF_REHEARSAL.md`)
and paused for the night. **Read this section first** in the next session before doing anything else.

### What's already done (all fixed and verified, don't re-investigate)

Five real bugs were found live during this rehearsal and are already fixed, committed, and deployed:
1. Receipt printing blocked sale completion for several seconds (fixed: printing now runs off the UI thread).
2. `SetupWindow`'s join-form submit button was clipped below the visible window (fixed: window is now content-sized).
3. **Background sync had never actually run in the real compiled WPF app at all** — `App.xaml.cs` built the
   Generic Host but never called `_host.Start()` (fixed).
4. The sync worker's own re-login never sent a tenant slug, so it silently failed the moment staging had more
   than one tenant (fixed: `BusinessJoinService` now persists `Sync.TenantSlug`, read back on re-login).
5. PostgreSQL/Npgsql rejected every `DateTime` from the SQLite-backed WPF client (`Kind=Unspecified`) — fixed
   globally via `PosDbContext.ConfigureConventions` forcing `Kind=Utc` everywhere, no schema change needed.

See STATUS.md's dated "Windows client (WPF) staging rehearsal, part 1" entry for the full account of each,
including exact evidence. Full `POS.Tests`: 168/169 (the one failure is an unrelated pre-existing local-time
flake — see STATUS.md, not something to fix as part of this rehearsal).

**Verified end-to-end, live:** the real WPF app, running on this same laptop, successfully joined the
`rehearsal-co` test business, ran a full cash session with offline sales/refunds, and — after the fixes above —
successfully synced its device, register, and all 13 queued invoices to the live staging PostgreSQL database.
This is the first time in this project's history that has actually been confirmed working.

### Exact state to resume from

- **Staging deployment**: still running, untouched, on `159.195.136.212` (`/opt/sites/pos`). Verify quickly:
  `curl https://pos-api-staging.sparkco.vip/health` should return `{"status":"ok","database":"reachable"}`.
- **Web dashboard**: `https://pos-staging.sparkco.vip` — Traefik basic auth `admin` / `YafGoVhqcps71sujQBGj8XtM`,
  then app login `owner` / `RehearsalPass123!` (tenant slug `rehearsal-co`).
- **Local WPF app**: not currently running (process was closed cleanly before ending the session — nothing to
  kill). `src/POS.Wpf/bin/Debug/net8.0-windows/appsettings.local.json` already has
  `Sync:Enabled=true`, `Sync:ApiBaseUrl=https://pos-api-staging.sparkco.vip`, `Sync:IntervalSeconds=15`
  (sped up from the 30s default for faster rehearsal iteration — fine to leave, or reset to 30 for a more
  realistic pace).
- **Local pos.db**: already joined to `rehearsal-co` as device "ASUS" — just relaunch
  (`dotnet run --project src\POS.Wpf\POS.Wpf.csproj`, or run the built exe directly) and log in with
  `owner` / `RehearsalPass123!`. No need to re-join.
- All code changes are committed to `main` (nothing pending except an unrelated note the project owner was
  jotting into this file's credentials table above — leave that alone unless asked).

### What's left in the runbook (pick up here)

1. **Verify the synced data on the Web dashboard** — log in per above, check Management: the "ASUS" device,
   the offline sales/refund, and the product stock quantity should all be visible and match what the WPF app
   shows locally.
2. **Close the cash session** — back in the WPF Cash screen, close with a counted amount deliberately
   different from the expected balance, confirm the discrepancy calculation (counted − expected) and that the
   session status changes to closed.
3. That completes `docs/STAGING_WPF_REHEARSAL.md`'s core steps. Record the outcome in that file's own
   "Results" section, then update STATUS.md/ROADMAP.md's T7 line accordingly.

### Backlog items raised during this rehearsal, not yet built (see ROADMAP.md for the full write-up)

- **A "receipt printer" selection setting** — pick a specific installed Windows printer instead of trusting
  whatever the OS-wide default happens to be (see ROADMAP.md Operational Extensions).
- **Device identity is fragile: it's keyed off `Environment.MachineName`.** The rehearsal laptop was renamed
  mid-session (from "ASUS" to something else) — since `GetOrCreateCurrentDeviceAsync` matches an existing
  Device row by name, a machine rename after first use would silently create a *second* Device/Register
  instead of recognizing the same physical till, fragmenting its history. Worth a Settings-page override
  (an editable "This till's name" independent of the Windows computer name) — see ROADMAP.md for the full
  write-up of what this would need.

---

Login now requires a username **and** password (password verification was added 2026-09-21 — see STATUS.md Stage 4T / T2).

Demo users (randomly generated passwords, defined in `DatabaseSeeder.DemoAdminPassword` / `DemoCashierPassword` — change both places together if you rotate them):

| Username | Password |
|----------|----------|
| admin    | TQteaiwQUF!tGDGx | RehearsalPass123! (connected to web)
| cashier  | qMMWfqwXqeE69h87 |

These apply to a fresh database (WPF SQLite file or a freshly migrated API/Postgres database). An existing local database that was seeded before this change keeps its old password ("admin"/"cashier") — delete/reset the local database file if you want it to pick up the new demo passwords.

New users created from the Users screen (WPF) or the web dashboard's Management page get a random temporary password shown once at creation time — write it down there, it is not stored anywhere retrievable afterward.

## Devices (Boxes)

By default every machine that runs the WPF app is auto-trusted as a Box the first time it starts a sale (no change from before). A revoked device is always blocked, regardless of this setting.

To require an admin to explicitly provision and enroll every register (multi-Box-per-branch trust model), add this to `src/POS.Wpf/appsettings.json`:

```json
"Sync": {
  "RequireDeviceEnrollment": true
}
```

With that on, a machine must be provisioned (Sidebar → Devices → "Provision a new Box", requires the ManageSettings permission) and then enrolled with the one-time code shown (Sidebar → Devices → "Enroll this machine") before it can start a sale.

## Joining an existing business (Stage 4T/T3)

A genuinely fresh WPF install (no local `pos.db`, or one with no accounts in it yet) shows a one-time
"Set up this register" screen before login, offering two paths:

- **Start a new business** — today's existing behavior: seeds an independent demo business locally
  (see the demo credentials above).
- **Join an existing business** — enter the API server address, business slug, and an existing user's
  username/password for that business. This signs in against the remote server, pulls its
  catalog/settings/users/devices down into the local database, and signs this session in for real —
  no separate login step afterward. `Sync:Enabled`/`Sync:ApiBaseUrl` are set for this run immediately
  and also written to `appsettings.local.json` next to `appsettings.json` so future launches keep
  syncing with the same server.

