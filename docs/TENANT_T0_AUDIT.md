# Tenant T0 — Repository Audit & Migration Map

Status: T0 audit complete. Feeds Stage 4T in [ROADMAP.md](../ROADMAP.md) / [STATUS.md](../STATUS.md); full milestone plan in [tenant.md](../tenant.md).

Baseline recorded 2026-09-21: `dotnet build` — 0 warnings, 0 errors. `dotnet test` — **77/77 passed** (prior docs cited 69; the suite has grown since Stage 4 closed — this 77 count is the regression baseline for Stage 4T).

## 1. Entity ownership inventory

| Entity | Scoping today | Classification | T1 action |
|---|---|---|---|
| Store | none (is the root) | Becomes the branch under Tenant | Add `TenantId` |
| Device | `StoreId` | Per-store — promoted to "Box" | Add `TenantId`; add enrollment metadata; make `Invoice.DeviceId` required |
| User | `StoreId`, `RoleId` | Per-store | Add `TenantId`; add `UserStoreAccess`; scope `Username` uniqueness to tenant |
| Role | none — **globally unique `Name`** | Mutable, currently global | Add `TenantId`; scope `Name` uniqueness to tenant |
| Currency | none — **globally unique `Code`**, single global `ExchangeRate` per code | Global, but not immutable | Per tenant.md §3: one canonical tenant base currency; current single global rate table maps cleanly since there's only one store/tenant today — no conflict to resolve yet, but this is the thing to watch once a 2nd tenant exists |
| Category | none | Global shared catalog | Add `TenantId` |
| Product | none (`CategoryId` only) | Global shared catalog | Add `TenantId` |
| Inventory | `ProductId`, `StoreId` (own column, not parent-derived) | Per-store stock-of-truth | Add `TenantId`; unique key becomes `(TenantId, StoreId, ProductId)` |
| Invoice | `StoreId`, `DeviceId` (nullable), `UserId` | Per-store sale root | Add `TenantId`; make `DeviceId` required + validated against `StoreId` |
| InvoiceItem | `InvoiceId`, `ProductId` — no own StoreId | Child of Invoice | Derive `TenantId`/store from parent only (already the right pattern — don't duplicate) |
| Payment | `InvoiceId` — no own StoreId | Child of Invoice | Same as InvoiceItem |
| StockMovement | `ProductId`, `StoreId` (own column) | Per-store ledger | Add `TenantId` |
| Setting | `StoreId` | Per-store only (no tenant-level defaults exist) | Add `TenantId`; decide tenant-default vs store-override split |
| AuditLog | `StoreId` (**required**, not nullable) | Per-store | Add `TenantId`; tenant.md wants `StoreId` optional for tenant-wide events — today it's mandatory, needs a nullable migration |
| SyncChange | `StoreId` (nullable = global aggregate) | Sync ledger, single global auto-increment `Id` cursor | Add `TenantId` scoping alongside existing `StoreId`; the global-sequence-with-server-filter pattern itself can stay (tenant.md agrees) |

`Invoice.CustomerId` FK exists but no `Customer` entity is implemented — dangling reference, out of scope for this pass but flag for whoever implements Customers (must inherit the ownership model per tenant.md §10).

## 2. Confirmed pre-existing gaps (not new — but Stage 4T must not carry them forward silently)

1. **No global query filters exist at all**, not even for `IsDeleted` — every query hand-rolls `!IsDeleted`. T1 can add a `TenantId` `HasQueryFilter` cleanly (nothing to conflict with), but must first grep every query for a missing `!IsDeleted` check before trusting the filter pattern.
2. **Login has no password check.** `AuthService.LoginAsync` (src/POS.Infrastructure/Services/AuthService.cs:18-63) matches on username only, case-insensitive, **globally across all users**, and never compares against the stored BCrypt hash despite `DatabaseSeeder` seeding real hashes. This is more than "username-only auth" — it's unauthenticated. T2 must fix this before any tenant isolation claim is meaningful, since anyone who knows (or guesses) a username from Tenant B can sign in as that user today.
3. **Device identity is fully unauthenticated and auto-provisioned** by machine name (`SaleService.GetOrCreateCurrentDeviceAsync`, src/POS.Infrastructure/Services/SaleService.cs:727-751, and the sync push/pull device-upsert path) — no enrollment, no credential, no revocation. T2/T3 must add real enrollment before "Box" can be a trust boundary.
4. **Global name-based reconciliation exists today**: `EnsureRoleAsync` matches roles by `Name` with no scope at all (src/POS.Api/Program.cs:1699-1735) — the exact "merge unrelated businesses because both have a role named Admin" risk tenant.md warns against, currently latent only because there's one store.
5. **`Users.Username` and `Roles.Name` have global unique DB constraints** — both block same-username-different-tenant today and must become tenant-scoped composite uniqueness in T1.
6. **`POST /api/sync/categories/push` has no `ClaimsPrincipal` parameter at all**, unlike every sibling sync endpoint, despite the route group requiring authorization — needs a scope check added before tenant boundaries mean anything for category writes.
7. **`UpsertDeviceSnapshotAsync` will silently rebind an existing device row's `Id`** to an incoming device's `Id` when matched by `(StoreId, Name)` instead of `Id` (src/POS.Api/Program.cs:1624-1664) — a device-identity-merge bug worth fixing alongside T2 enrollment work, not deferring.
8. **Every POS.Web controller hard-codes `_session.StoreId`** with no store selector (`DashboardController`, `ManagementController`) — this is the concrete thing T5's "authorized store selector" replaces.

## 3. Old-to-new ownership map (migration path)

The current database is demonstrably single-business: one seeded `Store`, all `User`/`Device`/`Invoice`/`Inventory`/`Setting`/`AuditLog`/`StockMovement` rows already key to that one `StoreId`. Per tenant.md §T0 guidance ("If the current database demonstrably belongs to one business, prepare one initial tenant and preserve its IDs"):

- Create exactly **one initial `Tenant`** row during T1 backfill.
- Assign every existing `Store` row (there's currently exactly one, `DatabaseSeeder`-seeded) that `TenantId`.
- Backfill `TenantId` on every tenant-owned table (Product, Category, User, Role, Invoice, Inventory, StockMovement, Setting, AuditLog, Device, SyncChange) by joining through their existing `StoreId` (or, for global tables like Product/Category/Role that have no StoreId today, assign the single initial tenant directly since there is only one tenant in this database).
- No cross-owner ambiguity exists in the current data — this path is unblocked.

## 4. Currency compatibility

No conflict to resolve today: there is one `Store`, so there is trivially one canonical base currency. This section becomes load-bearing only once a second store/tenant with a different base currency needs onboarding — defer that decision to whenever T1's backfill actually encounters it (not before).

## 5. Open decisions T0 calls for that still need an answer

tenant.md §T0 lists three business/policy decisions this milestone must make before T1 starts. They aren't derivable from the code — see the follow-up question after this document.
