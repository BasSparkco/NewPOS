# POS — Roadmap

This file is the **single path from project start to the long-term vision**, stage by stage and step by step. Update [STATUS.md](STATUS.md) as items complete.

---

## Stage 0 — Foundation (before feature code)

**Goal:** Repo, solution structure, and standards so all later work stays consistent.

1. Initialize Git repository (if not already) and a sensible `.gitignore` for .NET / Visual Studio.
2. Create solution and projects: **Core**, **Application**, **Infrastructure**, **Presentation (WPF)** — names aligned with [pos_system_master_plan.md](pos_system_master_plan.md) §23.
3. Add **EF Core** + **SQLite**; implement **DbContext** and baseline **migrations** for **Stores** (and any minimal shared tables needed for FKs).
4. Wire **dependency injection** in the WPF host; add **logging** (e.g. Serilog per [stack.md](stack.md)).
5. Define **coding standards**: namespaces, folder layout per module (Sales, Inventory, …), and MVVM conventions.
6. Add a **test project** (xUnit or NUnit) for domain/application unit tests.

**Exit criteria:** App runs empty shell; DB migrates; one vertical slice can be added without restructuring.

---

## Stage 1 — MVP (Phase 1 from master plan)

**Goal:** One store, core POS flow: products → invoice → pay → print.

1. **Identity (minimal for MVP):** single default user or simple login; defer full roles if needed to ship faster (or implement Admin/Cashier per master plan §5 — pick one and document in STATUS).
2. **Store:** seed one **Store**; all invoices and inventory scoped to **StoreId** ([database.md](database.md) §3.1, §3.6).
3. **Products & categories:** CRUD products, categories, barcode field, basic validation.
4. **Inventory:** **Inventory** row per product/store; stock decrement on sale; configurable “no negative stock” ([database.md](database.md) §8).
5. **Sales — invoice:** open/complete invoice; line items; totals; **Status** (Open → Paid) aligned with [database.md](database.md) §3.7–3.8.
6. **Payments:** cash payment linked to invoice; totals match ([database.md](database.md) constraints).
7. **POS UI:** main cashier screen — search/add lines, totals, complete sale ([pos_system_master_plan.md](pos_system_master_plan.md) §9).
8. **Printing:** ESC/POS abstraction; receipt for completed sale ([stack.md](stack.md) §8).
9. **SQLite-only** deployment path documented (backup file location, restore).

**Exit criteria:** Demo: create products, sell, pay cash, print receipt, stock updates; works offline.

---

## Stage 2 — Operations & UX (Phase 2 from master plan)

**Goal:** Multi-invoice workflow, users/reports, and hardware polish.

1. **Multi-invoice tabs:** hold/resume; map to invoice statuses (Held, etc.) per [database.md](database.md) §12.1.
2. **Users & roles:** Admin, Manager, Cashier; **RBAC** on screens/actions ([pos_system_master_plan.md](pos_system_master_plan.md) §5, §10).
3. **Refund / cancel flows:** domain rules + DB status; stock movements for returns ([database.md](database.md) §11.1).
4. **Quick price check** and **low-stock alerts** ([pos_system_master_plan.md](pos_system_master_plan.md) §5).
5. **Reports (local):** daily sales, basic product/employee metrics; engine choice (FastReports vs RDLC) per [stack.md](stack.md) §7.
6. **Customer display:** secondary WPF window ([stack.md](stack.md) §9).
7. **Barcode scanner & printer** hardening: real hardware testing; keyboard-wedge scanner path.
8. **i18n:** `.resx` for EN/AR/HE; RTL layout for Arabic ([pos_system_master_plan.md](pos_system_master_plan.md) §16).

**Exit criteria:** Role-based POS; held invoices; refunds; reports; customer display; bilingual/RTL usable.

---

## Stage 3 — Multi-currency, pricing rules, commercial depth

**Goal:** Match “production” retail needs before sync.

1. **Currencies** and **exchange rates** ([database.md](database.md) §4.1); store policy (base currency, display).
2. **Discounts & taxes** (tables in [database.md](database.md) §10.1–10.2); invoice totals: items − discounts + taxes ([database.md](database.md) §16).
3. **Optional:** **ProductPrices** windows, **ProductBarcodes** variants, **Units** / weighted products ([database.md](database.md) §10.3–10.5).
4. **Stock movements** as audit trail; reconcile with **Inventory** snapshot ([database.md](database.md) §11).
5. **Settings** table UI ([database.md](database.md) §4.2).
6. **AuditLogs** (minimal): who changed what ([database.md](database.md) §4.3).

**Stage 3 implementation note:** Items 1, 2, 4, 5, and 6 are now implemented in the desktop app. Admins can manage currency policy plus persisted operational settings, and they can review audit entries for critical actions from a dedicated audit viewer. Item 3 remains optional and deferred.

**Planning note:** Item 3 is still optional and should only be promoted into the current implementation pass if the next release needs advanced pricing, barcode variants, or units/weighted-product support.

**Exit criteria:** Tax/discount receipts; movement history; configurable stock rules; audit trail for critical actions.

---

## Stage 4 — Backend API & sync (Phase 3 from master plan)

**Goal:** PostgreSQL server, REST API, device/store identity, offline sync.

1. **ASP.NET Core Web API** project; Clean Architecture ports; **PostgreSQL** provider alongside SQLite ([stack.md](stack.md) §4–5).
2. **Auth:** JWT for API ([stack.md](stack.md) §4); align with desktop identity story.
3. **Sync model:** **Device** entity; invoice **DeviceId**, **SyncVersion**, **IsSynced** ([database.md](database.md) §12.2, §13–14).
4. **Sync engine:** background worker; REST payloads; conflict strategy (version / last-write) decided and documented.
5. **Operational concerns:** API deployment, HTTPS, connection strings, migration strategy local ↔ server.

**Stage 4 implementation note:** Items 1 through 5 are implemented. The sync engine now covers outbound and inbound slices for invoices, audit logs, categories, products, devices, users, store settings, and the store currency-policy aggregate: a WPF background worker pushes the currency-policy aggregate first, then unsynced settings, user, category, product, device, invoice, and audit-log snapshots to the API, before pulling newer currency policy, settings, users, categories, products, devices, invoices, and audit logs back down. The server applies a version-based conflict rule for invoices plus an `UpdatedAt`-based rule for categories, products, settings, users, devices, and currency policy, while audit logs sync in both directions as append-only rows keyed by `AuditLog.Id` and ordered by `CreatedAt` plus `AuditLog.Id`. User sync reconciles by `User.Id` or username, creates missing roles by role name when needed, and keeps username-driven identity flows aligned across clients and server. Synced paid/refunded invoice snapshots reconcile `Inventory` plus `StockMovements` on both server and client when a higher-version invoice snapshot is applied. Device snapshots use `UpdatedAt` plus `DeviceId` cursors in both directions, and invoice sync reuses the same freshness guard so older invoice metadata cannot regress a newer device row. Currency policy is synced as one aggregate because base-currency changes also convert catalog pricing and open invoices. Operationally, the API now has explicit provider/migration startup controls, production JWT-secret enforcement, and documented HTTPS plus reverse-proxy conventions. The broader aggregate-coverage pass is complete for the current mutable admin data in the desktop app; standalone `Role` or `Store` sync should wait until those concepts gain real management write paths beyond user-role reconciliation and the existing currency/settings flows. Full `POS.Tests` validation currently passes, so Stage 4 meets its documented exit criteria within the chosen sync rules. See [docs/SYNC_STRATEGY.md](docs/SYNC_STRATEGY.md) for the current contract and limitations.

**Exit criteria:** Two clients can sync sales/inventory to a shared server without corrupting data (within chosen conflict rules).

---

## Stage 4T — Multi-tenant SaaS foundation

Detailed plan: [tenant.md](tenant.md). For live, detailed per-step progress
(what landed, what's deliberately deferred, and exactly what's left) see
[STATUS.md](STATUS.md)'s Stage 4T section — that's the authoritative
continuation point for picking this work back up, this checklist is just
the summary.

This is a production-readiness prerequisite for Stage 5.1 and for Stage 5.2
multi-branch reporting. Preserve the existing desktop, sync and dashboard work.

- [x] T0 — Audit actual code and approve ownership/migration decisions.
- [~] T1 — Tenant schema, store access and verified data migration. (SQLite rehearsed; live PostgreSQL rehearsal now done — see STATUS.md's 2026-09-22 entry — but it required a separate, properly-typed PostgreSQL migration history (see docs/POSTGRES_MIGRATIONS.md); read-side sync scoping still open.)
- [~] T2 — Verified identity, enrollment and server-side authorization. (Password auth, device enrollment, tenant-scoped login, granular Web/API permissions, change/reset password, minimal tenant provisioning, rate limiting, offline auth expiry, broader API permission coverage, a Web audit viewer, a per-request device credential (`/api/devices/token`), and a fix to invoice-push actor validation all landed; an audit trail/UI for tenant provisioning and wiring the WPF sync worker to actually use its device credential when logged out remain — see STATUS.md "Remaining T2 scope".)
- [~] T3 — Tenant/store-bound desktop profiles and offline access. (A WPF "join an existing business" bootstrap landed, letting a fresh install bind to an existing tenant/store instead of always seeding an independent one; a real WPF logout now exists — previously the sidebar "Logout" button silently just navigated home; re-pointing an already-provisioned device to a different tenant remains deferred — see STATUS.md.)
- [~] T4 — Scoped, authorized and replay-safe synchronization. (A real cross-tenant data leak in categories/products pull+push is fixed and regression-tested; CashSession/CashMovement/Register now have a full sync surface. The live PostgreSQL rehearsal is now done, including a real 10-way concurrent stock reconciliation with zero lost updates — see STATUS.md. A line-by-line audit of every remaining endpoint for the cross-tenant leak class and tombstone/hard-delete propagation remain.)
- [~] T4.5 — Cash session management (added 2026-09-21, promoted from backlog — required before T7; see tenant.md §5b, §7). Cash payments/refunds now require an open, authorized session (offline-capable); closing is concurrency-safe rather than over-strict; CashSession/CashMovement now sync; a full WPF UI exists. `POS.Web` still has no equivalent screen — see STATUS.md.
- [~] T5 — Tenant-aware administration and store access. (Store CRUD, `UserStoreAccess` grants and an authorized active-store selector all landed and are enforced server-side — see STATUS.md.)
- [x] T6 — Isolation, migration and resilience release tests. (All matrix rows now closed: the ManageStores upgrade path, per-request store-access revocation enforcement, retryable-vs-permanent sync conflict classification, invoice/inventory/stock-ledger rollback atomicity, cross-tenant cash-session-close, stale-token-after-revocation, the offline last-unit-sale reconciliation policy (decided and implemented), a web stock-adjustment/device-reconnect test, and the PostgreSQL migration/concurrency rows (live rehearsal done, including a genuine 10-way concurrent write test) — see STATUS.md's 2026-09-22 entries for the full evidence trail.)
- [~] T7 — Recovery rehearsal and controlled pilot. (Isolated staging deployment stood up and internally verified against live PostgreSQL — see STATUS.md. Blocked on external HTTPS access (a DNS/Traefik decision for the project owner, not code — see STATUS.md's staging entry point 9) before the WPF manual rehearsal can run; backup/restore rehearsal and a real pilot business remain unstarted.)

Exit: independent businesses and their stores remain isolated across desktop,
API, web, synchronization, reports and recovery; existing financial history
is preserved; production username-only authentication is removed.

**Terminology decision (2026-09-21):** `Store` = branch (per tenant.md §2.3 — no
separate Branch table). The existing `Device` entity is promoted to be the
formal "Box" (register/terminal) a cashier logs into, rather than staying a
pure sync-identity row. As part of T1/T4, `Invoice.DeviceId` becomes required
(not nullable) and validated as belonging to the invoice's `StoreId`, so every
invoice is keyed by tenant + branch (`StoreId`) + box (`DeviceId`) + user
(`UserId`) — matching tenant.md §3's Invoice ownership row
(`TenantId, StoreId, DeviceId, UserId`). UI/reporting should surface this as
"Branch / Box / Cashier" even though the underlying columns stay `StoreId`
and `DeviceId`.

**Terminology amendment (2026-09-21, same day, follow-up):** `Device` was
further split into physical machine (`Device`, unchanged meaning) vs. logical
till (new `Register` entity — stable number/history, survives a hardware
swap). `Invoice.RegisterId` was added alongside the existing `Invoice.DeviceId`.
See tenant.md §2.3a for the full rationale and migration approach.

---

## Stage 5 — Web dashboard & beyond (Phase 4 from master plan)

**Goal:** Browser-based admin and future channels.

1. **Web dashboard:** reports, user/store management, inventory ([pos_system_master_plan.md](pos_system_master_plan.md) §6).
	Current baseline: `POS.Web` now exists as an ASP.NET Core MVC host with cookie auth, a control-room dashboard over the shared data layer for reports and low-stock inventory, and an editable management surface for store profile/settings/currency policy, store-scoped users, categories, and product/inventory administration with product-, movement-type-, and date-range-filtered stock-movement visibility plus quick date presets for today, yesterday, the last 7 days, the last 30 days, and this month. Focused web integration tests cover login, dashboard access, management postbacks, inline category create/update/delete behavior around downstream product use, product create/update/delete flows, and stock-ledger filtering by product, movement type, date range, and quick presets.
2. **Multi-branch** reporting and policies (central vs store-level).
3. **Mobile / PWA / MAUI** — pick one pilot ([stack.md](stack.md) §13).
4. **Plugin / integration** hooks (payment gateways, accounting) — as needed ([pos_system_master_plan.md](pos_system_master_plan.md) §11).

**Exit criteria:** Stakeholders can run the business from web + POS; roadmap for integrations is clear.

---

## Promoted Extensions — after core roadmap

**Goal:** Keep one canonical roadmap while capturing the highest-value ideas discovered during the `pos_q/` reference review.

These are not separate stages yet. They are prioritized backlog candidates to promote into implementation when the matching core stage is stable.

### Operational Extensions

1. ~~**Cash register management:** opening/closing register, denomination counting, expected-vs-actual reconciliation, cashier session history.~~ **Promoted 2026-09-21** out of this backlog into Stage 4T/T4.5 (required before the T7 pilot) — see [tenant.md](tenant.md) §5b and STATUS.md. Not optional/deferred anymore; kept here only as a pointer.
2. **Customer groups and selling price groups:** retail/wholesale/VIP pricing, customer-tier price rules, location-aware price policy.
3. **Invoice layouts and invoice schemes:** multiple receipt/invoice templates, configurable numbering prefixes/sequences, optional QR/compliance fields.
4. **Stock transfers between stores:** request/dispatch/receive flow, in-transit status, reconciliation.
5. **Expense management:** expense entry, categories, expense reports, optional employee-paid expense attribution.
6. **Granular role-based permissions (RBAC):** ~~Stage 2.2 shipped a binary gate only~~ — now superseded. `Role.PermissionsMask` (a `[Flags] Permission` bitmask: `ManageProducts`, `ViewReports`, `ViewAudit`, `ManageUsers`, `ManageSettings`, `ProcessRefunds`) replaces the old `IsAdmin`/"Admin-or-Manager" string check in the WPF app; every nav button and the Refund command (previously ungated entirely — any Cashier could open Refund) now checks its own permission via `ICurrentSession.HasPermission(...)`. The desktop `POS.Wpf` "Users" screen (sidebar) has a "Role permissions" editor — pick a role, check/uncheck what it can do, including brand-new roles created there. Permission edits propagate through the existing offline sync engine (`UserSyncDto` carries the role's mask and its own `UpdatedAt`, resolved with the same freshness rule used for every other synced aggregate) so multi-device stores stay consistent. The Admin role always keeps `ManageUsers` (server-enforced) to prevent locking everyone out. **Update (2026-09-21, Stage 4T/T2):** `POS.Web` and `POS.Api` now adopt the same `Permission` model — see STATUS.md Stage 4T/T2 "granular Web/API permission enforcement landed". `POS.Web`'s dashboard access and each management action are gated per-permission instead of a role-name string, and `POS.Api`'s refund endpoint checks `ProcessRefunds`. **Remaining:** broader per-endpoint API permission coverage beyond refund, and a `POS.Web` audit-log viewer to use the already-defined `ViewAudit` policy.

### Commercial Extensions

1. **Advanced catalog depth:** product variants matrix, per-variant barcodes, per-location variant stock, rack/bin assignment.
2. **Service billing:** non-stock service items with location/staff-specific pricing.
3. **Warranty management:** warranty definitions, product defaults, sale-line warranty assignment, warranty lookup.
4. **Sales orders / purchase orders / price offers:** pre-sale and pre-purchase documents with conversion into invoices or purchases.

### Platform Extensions

1. **Notification template system:** email/SMS/WhatsApp templates, trigger-based sending, delivery log and retry behavior.
2. **Payment gateway abstraction:** adapter model for multiple providers, callback handling, payment references, per-store configuration.
3. **Connector framework:** outbound webhooks, inbound order sync, retry/failure log, adapters for commerce/accounting systems.
4. **Tax compliance / e-invoice integration:** tax authority configuration, submission logs, filing/export support for regulated markets.
5. **Accounting engine:** chart of accounts, journal entries, debit/credit posting, profit/loss and balance-sheet grade reporting.
6. **Document and notes service:** notes, attachments, and media linked to invoices, products, customers, and future service workflows.

### Optional Future Verticals

Only promote these if product scope expands beyond core retail POS:

1. **CRM:** campaigns, call logs, proposals, schedules, follow-ups.
2. **HR / payroll:** attendance, leave, shifts, payroll groups, employee targets.
3. **Repair workflow:** jobsheets, device models, repair statuses, parts/service tracking.
4. **Restaurant mode:** tables, bookings, kitchen workflow, modifiers.

### Promotion Priority

Best candidates to promote soon after the current Stage 3/4 work:

1. granular role-based permissions (RBAC) — WPF/Core/sync portion done; Web + API auth enforcement remains
2. ~~cash register management~~ — promoted to Stage 4T/T4.5, no longer a backlog candidate
3. customer groups and selling price groups
4. invoice layouts and invoice schemes
5. stock transfers between stores
6. notification template system

---

## Ongoing (all stages)

- Performance: POS UI responsiveness; DB indexes ([database.md](database.md) §6–7, §15).
- Security: bcrypt passwords; least privilege; secrets not in source control ([stack.md](stack.md) §11).
- Documentation: keep **STATUS.md** and this file aligned with reality.

---

*Last updated 2026-09-22 (later session, same day): T6 is now fully closed — the two remaining matrix rows (offline last-unit-sale reconciliation; web stock-adjustment-then-reconnect) had been blocked on a product decision, which the project owner made, then implemented and tested (see [SYNC_STRATEGY.md](docs/SYNC_STRATEGY.md) for the policy). Implementing it surfaced and fixed a real lost-update race in stock reconciliation. Separately, an isolated staging deployment (Docker/Traefik/PostgreSQL) was stood up on the project owner's own host and used to run the first-ever live PostgreSQL rehearsal, which found and fixed a real, functional-breaking bug — the existing migration history produced wrong-typed columns against real PostgreSQL — then proved a genuine 10-way concurrent stock reconciliation against real PostgreSQL MVCC with zero lost updates (see [docs/POSTGRES_MIGRATIONS.md](docs/POSTGRES_MIGRATIONS.md) and [docs/STAGING_DEPLOYMENT.md](docs/STAGING_DEPLOYMENT.md)). **PostgreSQL live rehearsal is no longer pending.** T7 is now in progress: the staging deployment exists and is internally verified, but is not yet externally reachable over HTTPS — a DNS/Traefik decision that needs the project owner's input (see STATUS.md), not further code, before the manual WPF rehearsal ([docs/STAGING_WPF_REHEARSAL.md](docs/STAGING_WPF_REHEARSAL.md)) can run. See [STATUS.md](STATUS.md) Stage 4T for the authoritative, detailed record. Earlier history: Stages 0–4 complete; T0–T5 substantially landed across several prior sessions (device/register/cash-session work, sync isolation fixes, offline-authorization expiry, tenant-aware store administration) — see STATUS.md's dated entries for the full trail.
