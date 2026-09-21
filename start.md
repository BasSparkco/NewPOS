dotnet run --project src\POS.Wpf\POS.Wpf.csproj

Login now requires a username **and** password (password verification was added 2026-09-21 — see STATUS.md Stage 4T / T2).

Demo users (randomly generated passwords, defined in `DatabaseSeeder.DemoAdminPassword` / `DemoCashierPassword` — change both places together if you rotate them):

| Username | Password |
|----------|----------|
| admin    | TQteaiwQUF!tGDGx |
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

