# POS — Status

Track completed **stages** and **steps** against [ROADMAP.md](ROADMAP.md). Update this file when something is done.

**Legend:** `[ ]` not started · `[~]` in progress · `[x]` done

---

## Stage 0 — Foundation

| Step | Status |
|------|--------|
| 0.1 Git repo + `.gitignore` | [x] |
| 0.2 Solution + Core / Application / Infrastructure / WPF projects | [x] |
| 0.3 EF Core + SQLite + DbContext + migrations (incl. Stores) | [x] |
| 0.4 DI + logging in WPF host | [x] |
| 0.5 Coding standards / folder layout documented | [x] |
| 0.6 Test project for unit tests | [x] |

**Stage 0 complete:** [x]

---

## Stage 1 — MVP

| Step | Status |
|------|--------|
| 1.1 Identity (minimal or full roles — document choice) | [x] |
| 1.2 Store seed + StoreId on invoices/inventory | [x] |
| 1.3 Products & categories CRUD | [x] |
| 1.4 Inventory per store; stock on sale | [x] |
| 1.5 Invoice + line items + Open → Paid | [x] |
| 1.6 Cash payment + totals validation | [x] |
| 1.7 Main POS UI (search, lines, complete sale) | [x] |
| 1.8 ESC/POS receipt printing | [x] |
| 1.9 SQLite deployment / backup notes | [x] |

**Identity choice (1.1):** Minimal login — `Users` + `Roles` in SQLite with seeded demo users **`admin`** and **`cashier`**. Password hashes still exist in storage, but the current WPF login flow is username-only. Session is in-memory after sign-in (`ICurrentSession`).

**Stage 1 complete:** [x]

---

## Stage 2 — Operations & UX

| Step | Status | Notes |
|------|--------|-------|
| 2.1 Multi-invoice tabs + hold/resume | [x] | Tabs, concurrent invoices, Hold/Resume (F3), orange dot on held tabs, guard on pay |
| 2.2 Users & roles + RBAC | [x] | Catalog button hidden for Cashier; role shown in user badge; IsAdmin gate in ViewModel |
| 2.3 Refund / cancel + stock movements | [x] | RefundInvoiceAsync restores stock; RefundWindow lists recent paid invoices; Admin-only |
| 2.4 Quick price check + low-stock alerts | [x] | Price check window (F2), low-stock badge on cards + detail view |
| 2.5 Local reports (daily sales, basic metrics) | [x] | ReportsWindow: revenue, invoices, items sold, avg, top products, per-invoice list; date picker |
| 2.6 Customer display window | [x] | CustomerDisplayWindow: live cart + total, clock, idle screen; toggle via header button |
| 2.7 Barcode + printer hardening (real hardware) | [x] | Scanner burst detection (timing + Enter) tested; exact-barcode auto-add enabled; raw print verifies bytes written and safely falls back to file |
| 2.8 i18n + RTL (EN/AR/HE) | [x] | EN/AR/HE: main cashier (VM) + subsidiary windows via `Localization/AppStrings*.resx`, `Locale` helper, `CurrentUICulture`; RTL FlowDirection on dialogs; barcode row uses localized format |
| 2.9+2.10 UI Overhaul (Bagisto + pos_cashier.html) | [x] | Sidebar nav (Home/Customers/Cashier/Orders/Products/Reports/Settings), top bar with refresh(F5)/fullscreen(F11)/dark-mode/customer-display/price-check(F2), large Bagisto-style product cards with shadow hover, collapsible numpad, DynamicResource dark mode |

**Stage 2 complete:** [x]

---

## Stage 3 — Multi-currency & commercial depth

| Step | Status |
|------|--------|
| 3.1 Currencies + exchange rates + policy | [x] |
| 3.2 Discounts & taxes on invoices | [x] |
| 3.3 Optional: advanced pricing, barcodes, units | [ ] |
| 3.4 Stock movements audit + inventory snapshot | [x] |
| 3.5 Settings UI (general settings beyond currency policy) | [x] |
| 3.6 Audit logs | [x] |

**3.1 complete:** Currency entities/migration are in place, seeded currencies exist, stores carry a base currency, session/UI display the store currency, new sales inherit the store base currency, and admins now have a settings window to manage exchange rates and base-currency policy. Changing the base currency re-normalizes rates and converts catalog pricing plus open invoices into the new base currency.

**3.4 complete:** Stock changes are now written to a dedicated `StockMovements` ledger for opening stock, manual stock set adjustments, completed sales, and refunds. Admins can review the current inventory snapshot and recent movement history from the product management screen, including filtering history by the selected product.

**3.5 complete:** The admin settings window now includes persisted operational settings in addition to currency policy. Low-stock threshold, negative-stock policy, default tax percent, and receipt footer text are stored through the typed settings service and editable from the desktop app.

**3.6 complete:** A dedicated `AuditLogs` slice now records critical write-side actions, including currency policy changes, product create/update/delete, manual stock adjustments, sale completion, refunds, hold/resume, and cancel flows. Admins can review the audit trail from the desktop audit viewer with date, action, and entity filters.

**Stage 3 note:** Step 3.3 remains optional and should not block Stage 4 unless the next release explicitly needs advanced pricing, barcode variants, or units/weighted-product workflows.

**Stage 3 complete:** [x]

---

## Stage 4 — API & sync

| Step | Status |
|------|--------|
| 4.1 ASP.NET Core API + PostgreSQL | [x] |
| 4.2 JWT auth | [x] |
| 4.3 Device + sync fields on entities | [x] |
| 4.4 Sync engine + conflict strategy | [x] |
| 4.5 Deployment + migrations (local/server) | [x] |

**4.1 complete:** `POS.Api` now exists as an ASP.NET Core host, Swagger is enabled in development, infrastructure can target PostgreSQL or SQLite from configuration, and the solution includes the first API deployment surface alongside the desktop app.

**4.2 complete:** The API now issues JWT bearer tokens from the existing username-only login flow, hydrates session context from claims, and exposes authenticated catalog, store, and sales endpoints. Focused API integration tests cover login, catalog access, sale mutations, hold/resume/cancel, cash completion, and refund.

**4.3 complete:** The persistence model now includes `Device`, invoice `DeviceId`, invoice `SyncVersion`, and invoice `IsSynced`. The current host device is auto-registered per store, new invoices are stamped with that device and initial sync metadata, and invoice write paths increment `SyncVersion` while marking invoices unsynced whenever they change.

**4.4 complete:** The repo now includes outbound sync for invoices, audit logs, categories, products, devices, users, store settings, and the store currency-policy aggregate, plus inbound pull slices for those same aggregates and for audit logs. The desktop app background sync worker now pushes currency policy first, then settings, users, categories, products, devices, invoices, and append-only audit rows, before pulling newer currency policy, settings, users, categories, products, devices, invoices, and finally audit logs through `IInvoiceSyncService`; `POS.Api` now exposes `/api/sync/invoices/push`, `/api/sync/invoices/pull`, `/api/sync/audit-logs/push`, `/api/sync/audit-logs/pull`, `/api/sync/categories/push`, `/api/sync/categories/pull`, `/api/sync/products/push`, `/api/sync/products/pull`, `/api/sync/devices/push`, `/api/sync/devices/pull`, `/api/sync/users/push`, `/api/sync/users/pull`, `/api/sync/settings/push`, `/api/sync/settings/pull`, `/api/sync/currency-policy/push`, and `/api/sync/currency-policy/pull`; and the client persists separate per-store cursors for each aggregate while applying higher-version invoice snapshots plus category/product/settings/user/device/currency changes and appended audit rows locally. The current conflict rule remains invoice-level version precedence for invoice aggregates, while category/product/settings/user/device/currency-policy sync uses aggregate `UpdatedAt` precedence with skipped snapshots when the destination side is already as new or newer. User sync reconciles by `User.Id` or username and creates missing roles by role name when a pushed or pulled user references one that is not present locally. Audit logs now sync in both directions under append-only idempotence, ordered by `CreatedAt` plus `AuditLog.Id`. Device sync now uses `UpdatedAt` plus `DeviceId` cursors in both directions, and invoice sync reuses the same freshness guard so older invoice metadata cannot regress a newer device row. Currency policy now syncs as a single aggregate because base-currency changes also convert catalog pricing and open invoices, so it must run before category, product, and invoice synchronization. The broader aggregate-coverage pass is complete for the current desktop/admin mutation surface; `Role` still has no standalone management flow, and `Store` profile fields currently have no write path beyond the already-synced currency-policy/settings behavior. The full `POS.Tests` suite currently passes at 58/58, so the Stage 4 exit criteria are satisfied within the documented sync rules. See [docs/SYNC_STRATEGY.md](docs/SYNC_STRATEGY.md).

**4.5 complete:** Runtime provider switching is in place for SQLite desktop and PostgreSQL API hosts, and the design-time EF factory follows the same provider/connection resolution path instead of being hardcoded to SQLite. The API host hardens startup behavior for non-development environments: `Jwt:SigningKey` is required and must not be a development placeholder, `Database:ApplyMigrationsOnStartup` and `Database:SeedDemoDataOnStartup` default to disabled outside development, and the HTTP edge is now explicit through `Http:RequireHttps`, `Http:UseHsts`, and `ReverseProxy:*` forwarded-header settings. The repo includes [docs/API_DEPLOYMENT.md](docs/API_DEPLOYMENT.md) with explicit `dotnet ef database update` examples for WPF/SQLite versus API/PostgreSQL startup projects, environment-variable guidance for connection strings and JWT secrets, reverse-proxy trust configuration, and rollout sequencing for one migrator instance plus regular API replicas.

**Stage 4 complete:** [x]

---

## Stage 5 — Web dashboard & beyond

| Step | Status |
|------|--------|
| 5.1 Web dashboard (reports, users, inventory) | [~] | `POS.Web` MVC host added with cookie auth, Admin/Manager access policy, a reports dashboard, editable store/user/category management for profile, operational settings, currency policy, store-scoped users, and product/inventory administration, plus focused web integration coverage for login, dashboard access, and management postbacks |
| 5.2 Multi-branch reporting / policies | [ ] |
| 5.3 Mobile / PWA / MAUI pilot | [ ] |
| 5.4 Integrations / plugins (as needed) | [ ] |

**Stage 5 complete:** [ ]

---

## Summary

| Stage | Name | Complete |
|-------|------|----------|
| 0 | Foundation | Yes |
| 1 | MVP | Yes |
| 2 | Operations & UX | Yes |
| 3 | Commercial depth | Yes |
| 4 | API & sync | Yes |
| 5 | Web & beyond | No |

**Reference note:** The external reference project reviewed during planning was moved out of this repo to `C:\projects\pos_q`. If we need another comparison pass later, use that location instead of expecting `pos_q/` under this repository root.

*Last updated: Stage 4 is complete. Stage 5.1 is now in progress with a new `POS.Web` host that reuses the shared application/infrastructure layer, cookie-backed admin sessions, a server-rendered reports dashboard, editable store/user/category management for profile fields, operational settings, currency policy, store-scoped users, and product/inventory administration with product-, movement-type-, and date-range-filtered stock-movement visibility plus quick date presets for today, yesterday, the last 7 days, the last 30 days, and this month. Focused `POS.Web` integration coverage exercises login, dashboard access, user/settings management, category create/update/delete lifecycle around downstream product usage, product create/update/delete flows with stock-movement persistence, stock-ledger filtering by product, stock-ledger filtering by movement type, stock-ledger filtering by date range, and stock-ledger quick date presets, including explicit this-month month-boundary inclusion and exclusion coverage. Stage 4.4 covers outbound and inbound sync for invoices, audit logs, categories, products, devices, users, store settings, and the currency-policy aggregate, a hosted desktop sync worker, documented aggregate conflict rules, username-aware user reconciliation, device freshness guards, server-side inventory reconciliation for synced paid/refunded invoices, local apply paths for invoices, audit logs, categories, products, devices, users, settings, and currency policy, and a dedicated global change-sequence model for both pull cursors and outbound aggregate push cursors. The broader aggregate-coverage pass is complete for the current mutation surface; future `Role` or `Store` sync should be driven by real management features, not by table presence alone. Full-suite validation now stands at 69 passed, 0 failed in `POS.Tests`.*



