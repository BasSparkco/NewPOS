# POS Multi-Tenant and Multi-Store Implementation Plan

Status: Proposed implementation supplement; no repository changes have been made.

## 1. Purpose and roadmap placement

Evolve the existing offline-first POS into a SaaS product serving independent retail businesses. Each business is a tenant, each tenant owns stores, and each store has cashiers and devices.

Keep `ROADMAP.md` as the canonical delivery sequence. This document supplies the detailed tenancy workstream, identified as T0–T7. Insert it after the existing Stage 4 baseline and before treating Stage 5.1 as production-ready or beginning Stage 5.2 multi-branch reporting. Existing Stage 5.1 work remains useful and must be adapted, not rebuilt.

Historical stages marked complete remain historical accomplishments. Their completion does not establish multi-tenant readiness. A tenant release requires the gates in this document.

### Evidence reviewed

- `database.md`: Stores represent business locations; invoices, users, inventory and devices reference stores. Sections 17–18 defer tenancy to the future and only suggest adding TenantId to all tables.
- `pos_system_master_plan.md`: describes an offline-first, multi-branch platform, but has no complete independent-business ownership model.
- `STATUS.md`: reports working API/sync, username-only authentication, desktop granular permissions, and an in-progress web dashboard. Web/API permission enforcement remains incomplete.
- `ROADMAP.md`: describes synchronization and future multi-branch features, but no complete tenant migration or isolation milestone.
- `README.md`: identifies the WPF, application, infrastructure, API, web and test projects.

These are document findings, not a code audit. T0 must reconcile them against the actual repository. For example, older documents describe sync/audit as future work, and the master plan includes Product.StockQuantity while the database plan uses Inventory. The status document also contains older and newer descriptions of synchronization cursors. Do not implement from stale prose without examining the current code.

## 2. Architectural decisions

1. Use one centrally operated application and shared PostgreSQL database for the initial SaaS deployment.
2. Add `Tenant` as the business ownership boundary.
3. Keep `Store` and `StoreId` as the branch/location concept. Do not add a synonymous Branch table or duplicate BranchId column.
4. Preserve WPF and its local SQLite database. Bind each installed local data profile to one tenant, one store and one enrolled device.
5. Share product definitions within a tenant; track stock per store. Do not share business products, customers, prices or roles across independent tenants.
6. Tenant-owned users may access several stores through explicit assignments. One user record belongs to one tenant in this release; cross-tenant personal accounts are deferred.
7. Derive authoritative tenant context from verified identity/device enrollment. Client-supplied IDs identify requested resources; they never grant access.
8. Apply isolation in application services, API/web authorization, persistence, synchronization and background work. UI filtering alone is insufficient.
9. Preserve existing sales, stock movements, IDs and historical monetary values during migration.
10. Keep business invoice status separate from synchronization state. `Synced` must not replace `Paid`, `Held` or `Refunded`.

Separate databases or customer-hosted installations may be offered later. They are not required for this release and must not create a second set of business rules.

## 3. Target ownership model

### New entities and changes

| Entity | Proposed fields or changes | Rules |
|---|---|---|
| Tenants | Id, Name, NormalizedSlug, Status, CreatedAt, UpdatedAt | Slug uniquely identifies a business during login/enrollment; changing a display name does not change its identity. |
| Stores | Add TenantId to existing entity | A store belongs to exactly one tenant. Store codes, if present, are unique within that tenant. |
| Users | Add TenantId and NormalizedUsername; retain credential hash and active state; add/reuse authentication version | Username uniqueness becomes tenant-scoped. Same username may exist in different tenants. |
| Roles | Add TenantId; preserve existing PermissionsMask | Names and edits are tenant-scoped. Built-in role templates may be global definitions, but tenant role instances are independent. |
| UserStoreAccess | TenantId, UserId, StoreId | Explicit allowed stores, with a unique tenant/user/store tuple. Existing User.StoreId can remain temporarily as the default store, not the sole authorization mechanism. |
| Devices | Add TenantId; preserve StoreId; add/reuse enrollment and revocation metadata; add RegisterId, DeviceSecretHash | Device credentials are independently revocable. A caller cannot enroll into an arbitrary store. A Device is the *physical machine*; see the Registers row below for the *logical till*. |
| Registers (new, 2026-09-21) | Id, TenantId, StoreId, Number, Name, IsActive | The logical till/register — a stable identity (number, sale/cash-session history) that outlives the physical machine bound to it. See §2.3a below; replaces the earlier assumption that Device alone could serve as "the register." |
| Tenant currency policy | Canonical tenant base currency and rates, adapted from actual existing entities | See currency compatibility rule below. Do not invent duplicate competing settings. |
| CashSession / CashMovement (new, 2026-09-21) | CashSession: Id, TenantId, StoreId, RegisterId, OpenedByUserId, ClosedByUserId, OpeningCashAmount, ClosingCountedAmount, ExpectedCashAmount, DiscrepancyAmount, CurrencyCode, Status, IsSharedSession. CashMovement: Id, TenantId, CashSessionId, Type (OpeningFloat/SaleReceipt/Refund/CashIn/CashOut), Amount, Method, CurrencyCode, InvoiceId, PaymentId, PerformedByUserId, ApprovedByUserId | The financial cash-drawer session on a Register. See §5a below — this is now a required milestone (T4.5), not an optional backlog item. |

Use tenant-scoped roles plus explicit store access for the first release. A tenant administrator can manage their business; they are not a platform administrator. Permissions and store access must both be satisfied. Preserve the rule preventing removal of the last viable tenant administrator.

### 2.3a Register vs. Device (amendment, 2026-09-21)

The original plan treated `Device` as the sole "Box" concept — one row per physical machine, doubling as the register a cashier sells against. Building the cash-session work surfaced a real gap: **a register must keep its stable number and sale/cash-session history when its computer is replaced**, and `Device` alone cannot express that, since replacing hardware naturally means a new `Device` row (new enrollment, new machine name).

Resolution: `Register` is the stable, logical till (`Id, StoreId, Number, Name`). `Device` remains the physical machine and now carries `RegisterId`, binding it to the logical till it currently serves. `Invoice.RegisterId` (alongside the existing `Invoice.DeviceId`) is the durable key for a sale's till — replacing a till's computer means provisioning a new `Device` bound to the *same* `Register.Id`, not a new register. `Store` is unaffected and remains the branch identifier, unchanged from the original decision in §2.3.

Migration: existing installs backfill one `Register` per pre-existing `Device` (preserving today's 1:1 assumption), using an id **deterministically derived from the Device's own id** (not a random GUID) so a WPF install and its API server, which may each run this backfill independently against their own copy of the same already-synced `Devices` table, converge on the same `Register` identity without a coordinated migration run or a new sync round-trip. `Register` does not yet have its own push/pull sync surface; this deterministic convergence is a deliberate, narrower substitute for one, open for a future T4/T5 pass once cross-store register administration needs it.

### Ownership by data family

| Data | Required ownership/scope | Notes |
|---|---|---|
| Products, Categories, Customers | TenantId | Tenant catalog/customer list; access to sensitive fields still follows permissions. |
| Roles, Users | TenantId | No global reconciliation by username or role name. |
| Inventory | TenantId, StoreId, ProductId | One stock balance per tenant/store/product. |
| Invoices | TenantId, StoreId, DeviceId, UserId | Store and origin device are validated and cannot be reassigned through sync. |
| InvoiceItems, Payments, invoice tax/discount rows | TenantId plus required parent ownership | Derive store from invoice unless an existing direct store field has a justified purpose. |
| StockMovements | TenantId, StoreId, ProductId | Preserve stable operation IDs and source references. |
| AuditLogs | TenantId, optional StoreId, actor/device context | StoreId may be absent for tenant-wide events; historical rows must not disappear when actors are deactivated. |
| Settings | Explicit tenant-level or store-level ownership | Separate tenant defaults from store overrides; no unscoped business settings. |
| Currency definitions | Global only if immutable reference data | ISO code/name may be shared; business rates and policies must be scoped. |
| Devices, local sync state, server sync acknowledgements | Tenant plus store/device where relevant | Never reuse state across tenants or device profiles. |
| Future suppliers, warranties, product barcodes and price lists | TenantId, with store scope where justified | Apply rules when the module is implemented, not by building those modules now. |

Platform configuration and immutable reference tables do not need a business TenantId. Technical child rows must either carry an enforced TenantId or have a proven, mandatory parent ownership path. For mutable business tables, prefer explicit TenantId and ownership-preserving foreign keys.

### Catalog and currency compatibility

The existing status describes changing a store's base currency as converting catalog prices and open invoices. That is unsafe to carry over unchanged when several stores share one tenant catalog.

For this release, require one canonical catalog/base currency per tenant. Existing store settings must resolve to that policy; individual stores cannot independently convert shared product prices. Preserve each posted invoice's historical currency, rates and totals. Block base-currency changes during the initial tenant rollout unless a tenant-wide, versioned conversion procedure is already verified for offline devices and open invoices.

If existing stores that belong to one tenant use different base currencies, T0 must produce an explicit normalization/migration decision before cutover. Do not silently combine their prices. Per-store base currencies and price lists are a later extension.

## 4. Persistence and integrity rules

- TenantId is required on tenant-owned records after the migration backfill.
- Establish suitable alternate keys such as `(TenantId, Id)` on referenced entities and ownership-preserving composite foreign keys on dependents.
- Reject an invoice referencing another tenant's customer, user, store or device. Also ensure a device belongs to the invoice's store; matching TenantId alone is insufficient.
- Enforce product/category, invoice/item/payment, role/user and stock/product/store ownership at database level where supported and in application validation.
- Use a unique inventory key `(TenantId, StoreId, ProductId)`.
- Scope normalized usernames and role names by tenant. Define barcode uniqueness within a tenant when the current catalog requires one barcode per product; do not invent global uniqueness or force it without inspecting current variant behavior.
- Use leading tenant/store keys in indexes for actual query patterns, such as invoice date ranges and stock ledgers. Validate plans on PostgreSQL with representative data.
- Treat tenant ownership as immutable after creation. A future business transfer requires a dedicated workflow, not a generic update.
- Apply EF query filters as one defense. Also validate writes and review raw SQL, bulk operations, filters bypasses, exports, joins and background jobs.
- Enable and verify SQLite foreign-key enforcement. Exercise provider-specific migrations and constraints on both SQLite and PostgreSQL.
- Optional PostgreSQL row-level security is additional defense, not a substitute for application authorization, and is outside this milestone unless deliberately selected at T0.
- Never infer authorized access from a GUID being difficult to guess.

## 5. Identity, authorization and offline access

### Online identity

- Replace production username-only sign-in in WPF, API and web with verified credentials. Use tenant slug plus normalized username, or a previously enrolled device's verified tenant context.
- Reuse sound existing password hashing; do not store plaintext credentials or ship universal demo credentials in production.
- Use generic authentication failures and rate limiting. Prevent tenant/account enumeration through login error details.
- Issue tokens/sessions only after verification. Bind sessions to tenant and user; validate device credentials separately for device operations.
- Implement the same permission model for desktop, web and API services. A valid token never grants every action within a tenant.
- Recheck current active status, store assignments and authentication/permission version online. Bound stale token/session privileges with an explicit invalidation mechanism.
- Tenant administrators manage users and store assignments only inside their tenant. Platform operations need separate authorization and audit; never make every business Admin a platform Admin.

### 5a. Device authentication is not a substitute for employee login (amendment, 2026-09-21)

The T0 audit found that `/api/sync/*` endpoints authenticated every call as "whoever is currently signed in," including background sync — meaning there was no device-scoped credential at all, and a signed-in user's own identity was trusted uncritically for sync payload fields it should not have controlled. Resolution, now implemented:

- **Two distinct credential types issued by the same JWT infrastructure, distinguished by a `token_type` claim.** A **user token** (`token_type` absent/"user") comes from `/api/auth/login`, carries the employee's identity, tenant, store and permissions, and is required for every business operation (sales, refunds, administration) and for accountability. A **device token** (`token_type=device`) comes from `/api/devices/token`, verified against a persistent secret issued once at enrollment (`Device.DeviceSecretHash`, distinct from the one-time `EnrollmentCodeHash`), carries only `tenant_id`/`store_id`/`device_id`, and authorizes background synchronization only.
- **A device token can never reach a business-operation endpoint.** The API's default authorization policy rejects any request whose token carries `token_type=device`; a named `SyncPolicy` opts a deliberately narrow set of endpoints back in — invoice push/pull and every read-only catalog/settings/user/device/audit pull. Administrative pushes (products, categories, settings, users, devices, currency policy) stay on the default (user-only) policy, matching this section's existing rule that admin actions require online *user* authorization, not merely an online *device*.
- **Synchronization may continue after an employee logs out**, using the device token instead of a cached user session — this is the concrete mechanism behind "device credentials authenticate the terminal for scoped background synchronization." (WPF-side: the persistent secret is generated and returned once at enrollment; wiring the background sync worker to fall back to it when no user is logged in — which requires resolving the active tenant/store from the device's own identity rather than the transient user session across every sync method — is scoped but not yet implemented; see STATUS.md.)
- **The server validates authorization rather than trusting a submitted UserId.** The invoice-push endpoint previously resolved a pushed invoice's actor from the payload's username string, silently falling back to whichever user happened to be making the HTTP call if that lookup failed — a real misattribution path ("every business operation must retain its original actor" did not actually hold). Fixed: the payload's username must resolve to a genuine, active user of the *same tenant*, or the invoice is rejected outright (never silently reattributed to the caller). A device-authenticated push additionally cannot claim a `DeviceId` other than its own authenticated identity.
- **Every business operation still retains its original actor and any approving manager.** `Invoice.UserId` is unchanged as the sale's actor. Where a privileged manual action benefits from a second identity's sign-off (a cash-drawer removal above a configurable threshold — see §5b), a distinct `ApprovedByUserId` is recorded and server-verified to belong to a different, sufficiently-permissioned user — never the same person self-approving.

### 5b. Cash session management (amendment, 2026-09-21) — promoted to a required milestone

Cash-register/session management was originally a "promoted extension" backlog candidate (ROADMAP.md), deferred behind core tenancy work. It is now **T4.5, required before the T7 pilot** — a live pilot cannot run without a real accounting of the cash drawer. Full chart-of-accounts/general-ledger accounting remains explicitly out of scope; this is a session ledger, not an accounting engine.

- `CashSession` tracks: opening cash, closing count, computed expected balance, and the resulting discrepancy, scoped to one `Register` at a time (see §2.3a — not per-employee floating tills).
- `CashMovement` rows record every cash receipt, refund, and manual addition/removal against the session, each preserving `InvoiceId`/`PaymentId` back to the originating sale where applicable — the connection to the original sale survives even though the movement is tagged to whichever session is open when the refund happens (a sale and its later refund can legitimately fall in different sessions).
- Payment methods and currencies stay distinguishable per movement (`Method`, `CurrencyCode`); the expected-balance calculation only sums cash-method movements, so card/other-method sales do not distort the cash count.
- **Employee logout never closes a session.** `CashSession`/`CashMovement` live in the database keyed by `Register`/user, entirely independent of `ICurrentSession`; closing a session is always its own explicit action.
- **Configuration, not code, chooses the workflow.** A `Cash:SessionMode` store setting selects `PerCashier` (default — each cashier opens and owns their own session; clearest accountability, matching "start with fixed registers") or `PerRegister` (one shared drawer any authorized cashier can record against/close). Either mode still allows at most one *open* session per register at a time — this is a fixed-register model, not a floating-till one, per this document's explicit scope boundary (§10).
- A manual cash removal (`CashOut`) can require a second, sufficiently-permissioned user's approval via a `Cash:RequireApprovalForCashOut` setting — off by default, so a simple single-cashier store is not forced into a workflow it does not need.
- Recording a sale/refund's cash movement is best-effort and never blocks the sale: if no session happens to be open on that register, the payment simply is not attached to one. Whether opening a session should ever be *mandatory* before selling is a store-policy decision left open for T5.

### Offline behavior

- First-time enrollment and new device activation require online authorization.
- A local profile contains only data needed for its tenant/store and permitted cashier operations. Do not replicate all stores' sales or all users' credential hashes to every register.
- Support offline sign-in only for previously authorized users using a reviewed local credential-verification design. Keep cloud access secrets in OS-protected storage; protect local backups and account for device theft.
- Define and implement a finite offline authorization validity period before the pilot. Display expiry/sync state clearly; do not silently permit indefinite cached administrative access.
- User/role administration, store grants, device enrollment and tenant currency changes require online authorization in this release. Do not accept these changes through generic untrusted device snapshots.
- Online revocation cannot instantly reach a disconnected machine. Document that limitation and enforce revocation at the next contact or offline authorization expiry.
- Preserve pending local transactions after revocation or tenant suspension. Reconciliation must distinguish historical valid sales from unauthorized later operations; rejected data is retained for controlled review, never silently discarded or blindly accepted.
- Decide suspension behavior explicitly before pilot: this plan does not authorize remote erasure or automatic deletion of customer data.

## 6. Synchronization contract

Retain the current engine where it is correct, but review every push/pull surface and its authority model.

1. Scope cursors, bootstrap snapshots, outbox entries, acknowledgements, retry records, tombstones and idempotency keys by tenant and authorized store/device as applicable.
2. Keep the actual current change-sequence model if sound. A global internal sequence can remain; authorization filters must precede delivery, and clients must not gain access by editing cursor values. Bind resumable cursors to their scope or validate that scope server-side.
3. Pull tenant-wide catalog data and the authorized store's operational data. Central multi-store reports are served to authorized managers, not replicated wholesale to every cashier.
4. Derive trusted tenant/store/device context from authenticated enrollment. Reject payload ownership mismatches and validate every nested reference before mutation.
5. Reconcile users only inside a tenant, by stable IDs or a narrowly specified tenant-scoped migration rule. Reconcile roles by tenant and role identity. Never merge unrelated businesses because both have a user named admin.
6. Make credentials, role definitions, permission masks, store access and device grants server-authoritative. Remove the ability to escalate privileges through existing user/role sync. Pull only the authorized security projection needed locally.
7. Define field authority and permission requirements for catalog/settings updates. Catalog changes from authorized devices, if retained, need permission checks beyond ownership.
8. Treat sale/refund/stock effects as atomic, replay-safe operations. Duplicate push, response loss and retries must not double-decrement stock or double-refund.
9. Do not use whole-invoice last-write-wins to silently overwrite competing financial transitions. Preserve immutable posted facts and reject/quarantine incompatible transitions; retain deterministic operation IDs and acknowledgements.
10. Include manual stock adjustments and initial balances in the reconciliation contract. Do not overwrite concurrent balances using the newest snapshot timestamp. Preserve movement history and derive/reconcile the balance from accepted effects.
11. Explicitly test two offline devices selling the last unit. Strict global stock availability cannot be guaranteed while both are disconnected without an allocation scheme. Document the chosen oversell/reconciliation behavior; a local no-negative-stock rule is not a global guarantee.
12. Mark local operations acknowledged only after durable server acceptance. Advance pull progress only after local application commits. Persist visible pending/rejected/conflicting status for recovery.
13. Deliver deletions, deactivations and access removals safely. Tombstones must reach eligible devices without deleting retained financial history.
14. Partition caches, logs, report jobs and exported file access by ownership. Shared cache keys must include tenant and applicable store/permission context.

## 7. Delivery milestones

### T0 — Repository audit and migration map

- Inspect actual entities, migrations, all API/web endpoints, WPF services, query filters, background jobs, sync DTOs/cursors and tests.
- Inventory each table and operation as platform-global, tenant-owned, store-owned or parent-owned.
- Identify the actual source of stock truth, username/role matching rules, store profile writes, current currency policy and current sync sequencing.
- Map every existing store and unscoped shared record to an intended tenant. Do not automatically assume all existing stores have one owner.
- If the current database demonstrably belongs to one business, prepare one initial tenant and preserve its IDs. If ownership is ambiguous, stop that migration path and request a mapping.
- Decide offline authorization lifetime, recovery behavior, currency compatibility and platform provisioning responsibility.
- Record a baseline build/test result; do not assume the documentation's previous test count is still current.

Exit: reviewed scope matrix, explicit old-to-new ownership map, migration/cutover design and a regression baseline. Unresolved ownership or currency conflicts block T1 backfill.

### T1 — Ownership schema and safe migration

- Add Tenants, ownership fields and UserStoreAccess using an expand/backfill/validate/constrain sequence.
- Convert existing User.StoreId associations into initial access assignments. Retain a default store if useful.
- Backfill dependent rows from verified parent ownership; detect orphans and cross-owner relations.
- Split or clone existing shared roles/catalog records only when the mapping requires it, preserving dependent relationships through an explicit mapping table.
- Replace global business uniqueness with tenant-scoped uniqueness where appropriate.
- Preserve invoice, payment and stock history. Do not regenerate financial transactions or recalculate posted values during schema migration.
- Implement both provider paths and an upgrade for local profiles, including ownership metadata and sync-state handling.

Exit: SQLite/PostgreSQL migration rehearsals preserve IDs, row counts and monetary/stock invariants; constraints reject cross-tenant links and missing ownership.

### T2 — Trusted identity and server authorization

- Replace username-only production login; implement tenant resolution, device enrollment and revocation.
- Add trusted tenant/store context to API, web sessions and application services.
- Apply granular permissions consistently and implement controlled store selection.
- Remove unauthorized privilege updates from synchronization surfaces before admitting real tenants.
- Add minimal authenticated provisioning for a tenant, initial store and first administrator; avoid public self-service signup in this milestone.

Exit: one tenant cannot authenticate as, grant rights to, inspect or mutate another; same username works independently in two tenants; unauthorized same-tenant store access is denied.

### T3 — Tenant-safe WPF and offline profiles

- Bind local data to enrolled tenant/store/device and implement permitted offline authentication.
- Support existing cashier workflows with the current languages and hardware.
- Keep company/store context visible in the interface and receipts where appropriate.
- Tenant or store changes require a separate enrolled data profile/bootstrap; do not relabel the old database or carry its cursors into the new scope.
- Preserve pending work during profile maintenance and expose recoverable errors.

Exit: an enrolled cashier can sell offline in the correct store, cannot access another local tenant profile, and survives application restart without losing pending work.

### T4 — Scoped synchronization and stock reconciliation

- Implement the ownership, authority and reliability rules in Section 6 across every existing aggregate.
- Build safe initial bootstrap and scoped reset/recovery. Deduplicate already accepted operations when resetting legacy sync state.
- Test catalog sharing across stores without mixing their inventory.
- Test manual adjustments from the web and desktop, refunds, reconnect conflicts, deletion and revoked credentials.

Exit: two tenants and multiple stores/devices synchronize without leakage, dropped transactions or duplicate stock effects; conflicts remain visible and recoverable.

### T4.5 — Cash session management (added 2026-09-21) — required before T7

Promoted from the "Promoted Extensions" backlog (ROADMAP.md) to a required gate: a live pilot cannot responsibly run without a real cash-drawer accounting. See §5b for the full design. Full accounting-engine work (chart of accounts, journals, P&L) stays out of scope.

- `Register`, `CashSession`, `CashMovement` entities, relationships and a store-configurable `Cash:SessionMode` (per-cashier/shared) land with T1/T4's schema work rather than waiting for T5, so the ownership model is established once rather than retrofitted.
- Cash movements are recorded for sales/refunds automatically when a session is open, without ever blocking the sale itself.
- Open/close/cash-in/cash-out actions are available from at least one client (WPF or Web); a manager-approval option exists for cash removals.
- Cash-session data participates in the same tenant/store isolation guarantees as every other aggregate (T6 below extends its test matrix to cover this).

Exit: a cashier can open a session, sell, take a refund, record a manual cash movement, and close the session with a correct expected-balance/discrepancy calculation; the session survives that cashier logging out mid-shift; two tenants' cash sessions never leak into each other.

### T5 — Tenant-aware administration and branch access

- Adapt existing POS.Web screens, reports, products, categories, users, settings and stock ledger.
- Provide store access management and an authorized active-store selector.
- Make current reports and exports respect tenant/store scope. Broader cross-store analytics remain Stage 5.2.
- Scope all downloads and background report execution, not only report pages.
- Keep minimal platform provisioning separate from tenant business administration.

Exit: tenant admins manage their own stores; managers see only assigned stores; cashier/report restrictions are enforced server-side.

### T6 — Isolation and resilience release gate

Run meaningful integration tests against PostgreSQL and local migration/sync tests against SQLite. Use tenant A with stores A1/A2 and tenant B with B1, repeated usernames/role names/barcodes, and several devices.

Required coverage:

| Scenario | Expected result |
|---|---|
| Guess another tenant's IDs on list/get/create/update/delete/export | No data disclosure or mutation. |
| Reference B's product/customer/role/device inside A's valid request | Rejected, including nested and bulk payloads. |
| Use A1 cashier rights to access A2 | Denied unless explicitly assigned and permitted. |
| Alter tenant/store IDs, cursor, device metadata or permission mask in sync | Rejected or safely constrained; no privilege escalation. |
| Same normalized username or barcode in A and B | Independent records, no cross-tenant merge. |
| Retry sale/refund after server commit but before acknowledgement | Single financial and stock effect. |
| Concurrent offline edits and last-unit sales | Defined reconciliation; no silent loss or false stock guarantee. |
| Web stock adjustment followed by device reconnect | Ledger and balances reconcile correctly. |
| Stale online token after user/device/access revocation | Access blocked within the documented policy. |
| Offline authorization expiry and pending work after revocation | Policy enforced; records retained for recovery. |
| Migration of existing populated database | Counts, IDs, posted totals and stock invariants preserved. |
| Cached reports, exports, jobs and store switches | Correct ownership throughout. |
| Same product sold at A1 and A2 | Shared definition, independent balances. |
| Cross-tenant database FK insertion attempt | Constraint rejects it. |
| Guess/reference another tenant's Register or CashSession id | No data disclosure or mutation (T4.5). |
| Close a cash session opened under a different tenant/store | Rejected. |
| A device token attempts a business operation (sale, refund) or an administrative sync push | Rejected regardless of the token's tenant/store claims. |
| An invoice sync payload attributes the sale to a nonexistent or cross-tenant username | Rejected; never silently attributed to the pushing caller. |

Preserve the existing sale, hold/resume, receipt, refund, permissions, currency and UI regression coverage. Security tests should target real service/endpoints and constraints, not merely assert that a property named TenantId exists.

Exit: required tests pass, critical isolation/recovery issues are resolved, and results are attached to STATUS.md.

### T7 — Recovery rehearsal and controlled pilot

- Back up the central database and local files before migration; test restoration, not just backup creation.
- Maintain encrypted versioned backups outside the running server. Define retention, recovery time and tolerated data-loss targets before onboarding.
- Provide tenant-scoped business data export and a documented customer exit process. This is distinct from administrator disaster recovery.
- Rehearse replacing a lost register from enrolled bootstrap plus central data. Explicitly document that unsynchronized local-only sales may be lost with the device.
- Rehearse restoring one tenant without overwriting unrelated tenants; a whole-database restore alone is insufficient for selective recovery.
- Begin with Ahmed's business after technical gates pass; validate a second isolated test business before customer expansion.
- Observe pending sync, rejected operations, auth failures, stock discrepancies and backups with tenant-aware diagnostics that do not expose unnecessary customer data.

Exit: completed pilot checklist, recorded restoration evidence, and no unresolved critical tenant-isolation or financial-integrity defects.

## 8. Cutover and rollback requirements

1. Take verified backups and record starting row counts, totals, stock balances and client versions.
2. Establish an explicit compatibility barrier. Older clients that cannot identify tenant context must not connect to the multi-tenant server under a guessed/default tenant.
3. Use a coordinated maintenance window for the initial rollout unless a compatible transitional protocol is implemented and tested. Drain/sync clients where possible and account for disconnected clients separately.
4. Apply migration/backfill and validate ownership and financial invariants before enabling traffic.
5. Upgrade clients, bind their profiles and confirm scope-safe cursor/outbox conversion or bootstrap. Never discard pending operations to simplify migration.
6. Keep migration execution controlled to one runner; do not let every API replica independently backfill ownership.
7. On failed validation, stop traffic and use the rehearsed recovery path. After new-version writes occur, a blind schema downgrade or old backup restore can lose sales; preserve and reconcile those writes before rollback.

Do not deploy, migrate a live database or invent existing-store ownership as part of merely implementing the code changes.

## 9. Changes to the existing documentation

Apply these updates in the repository during implementation. This deliverable does not claim that the supplied originals have been edited.

### ROADMAP.md insertion

Insert the following block between Stage 4 and Stage 5:

```markdown
## Stage 4T — Multi-tenant SaaS foundation

Detailed plan: [tenant.md](tenant.md).

This is a production-readiness prerequisite for Stage 5.1 and for Stage 5.2
multi-branch reporting. Preserve the existing desktop, sync and dashboard work.

- [ ] T0 — Audit actual code and approve ownership/migration decisions.
- [ ] T1 — Tenant schema, store access and verified data migration.
- [ ] T2 — Verified identity, enrollment and server-side authorization.
- [ ] T3 — Tenant/store-bound desktop profiles and offline access.
- [ ] T4 — Scoped, authorized and replay-safe synchronization.
- [ ] T4.5 — Cash session management (required before T7; see §5b, §7).
- [ ] T5 — Tenant-aware administration and store access.
- [ ] T6 — Isolation, migration and resilience release tests.
- [ ] T7 — Recovery rehearsal and controlled pilot.

Exit: independent businesses and their stores remain isolated across desktop,
API, web, synchronization, reports and recovery; existing financial history
is preserved; production username-only authentication is removed.
```

### Other documentation updates

- `STATUS.md`: add T0–T7 as pending initially; record evidence as each is completed. Do not mark tenancy complete based on this document alone.
- `database.md`: replace Sections 17–18's blanket future TenantId statements with the ownership model; update Users/Roles/Settings/Currencies and constraints; keep Store as the branch entity; remove Synced as business status.
- `pos_system_master_plan.md`: make multi-tenant SaaS an explicit goal; use per-store Inventory as the stock source, not a second editable Product.StockQuantity; document shared tenant catalog and offline limitations.
- `README.md`: link tenant.md and document current tenant login/enrollment, migration and supported client versions when implemented.
- `docs/SYNC_STRATEGY.md`: document scoped cursors, ownership validation, server-authoritative security data, conflict policy and stock effects based on actual code.
- `docs/API_DEPLOYMENT.md`: document compatibility gates, coordinated migration, recovery and secret management.

## 10. Scope boundaries

Included: ownership, identity, store access, isolation, safe migration, offline profiles, sync adaptation, administration adaptation, tests, recovery readiness, and cash-session management (T4.5, promoted from backlog on 2026-09-21 — see §5b).

Deferred: subscription billing automation, customer self-service signup, full platform support portal, payment processor integration, full accounting engine (chart of accounts, journals, P&L/balance-sheet reporting — the cash-session ledger in §5b is a session ledger, not this), purchases module, IMEI/warranty workflows, stock-transfer UI, advanced branch analytics, mobile POS, restaurant features, per-store currency/price-list expansion, and floating/shared-till workflows beyond the fixed-register model in §5b (a store may configure per-cashier or shared sessions on a *fixed* register; a cashier's session following them between registers is not in scope).

Those features remain in the main roadmap/backlog. They must inherit this ownership model when implemented. Tenant-wide data access does not itself implement branch stock transfers or guarantee operational readiness for every retail vertical.

## 11. Implementation handoff

Start with T0 against the actual repository. Produce the code inventory, proposed migration map and unresolved decisions first. Then implement the milestones in order as focused, reviewable changes, updating ROADMAP.md and STATUS.md with evidence. Preserve working POS flows and avoid unrelated rewrites. Ask the project owner only when a real ownership, business-policy or deployment decision cannot be resolved from the repository and this plan.
