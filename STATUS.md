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
| 2.2 Users & roles + RBAC | [x] | Real granular RBAC: `Role.PermissionsMask` (`[Flags] Permission`: ManageProducts/ViewReports/ViewAudit/ManageUsers/ManageSettings/ProcessRefunds) replaces the old binary `IsAdmin` gate in the WPF app; the "Users" screen (sidebar) edits a role's permissions directly, and permission edits sync between devices via the existing offline sync engine. Admin role always keeps ManageUsers (server-enforced) to prevent lockout. `POS.Web` dashboard gating and `POS.Api` JWT claims are unchanged (still coarse Admin/Manager-only) — tracked as the remaining follow-up in [ROADMAP.md](ROADMAP.md) |
| 2.3 Refund / cancel + stock movements | [x] | RefundInvoiceAsync restores stock; RefundWindow lists recent paid invoices; gated by the ProcessRefunds permission (previously undocumented as ungated — any signed-in user could open Refund until this pass) |
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

## Stage 4T — Multi-tenant SaaS foundation

Detailed plan: [tenant.md](tenant.md). Production-readiness prerequisite for Stage 5.1 and Stage 5.2 (multi-branch reporting).

| Step | Status |
|------|--------|
| T0 Repository audit and migration map | [x] |
| T1 Tenant schema, store access, verified data migration | [~] |
| T2 Trusted identity and server authorization | [~] |
| T3 Tenant-safe WPF and offline profiles | [~] |
| T4 Scoped synchronization and stock reconciliation | [ ] |
| T5 Tenant-aware administration and branch access | [ ] |
| T6 Isolation and resilience release gate | [ ] |
| T7 Recovery rehearsal and controlled pilot | [ ] |

**2026-09-21 planning note:** Confirmed the tenancy model in `tenant.md`: true multi-tenant SaaS (shared DB), `Store` remains the branch/location entity (no separate `Branch` table), and `Device` is promoted to be the formal "Box" (register/terminal) a cashier logs into rather than a pure sync-identity row. Every invoice is now keyed by tenant + branch (`StoreId`) + box (`DeviceId`) + user (`UserId`).

**T0 complete:** Full repository audit recorded in [docs/TENANT_T0_AUDIT.md](docs/TENANT_T0_AUDIT.md) — entity ownership inventory, pre-existing gaps (unauthenticated login, unenforced device identity, global role-name matching in sync), and the old-to-new ownership map. Confirmed the database is demonstrably single-business, so one bootstrap tenant unambiguously owns every existing row. Baseline before this work: `dotnet build` clean, `dotnet test` 77/77 passing.

**T1 in progress:** Added `Tenant`, `TenantCurrencyRate` (splits per-tenant exchange rates off the now-global-reference-only `Currency` row so one tenant's base-currency change can never corrupt another tenant's rates), and `UserStoreAccess` entities. Added `TenantId` to Store, User, Role, Product, Category, Device, Inventory, StockMovement, Setting, AuditLog, Invoice, and SyncChange, plus `User.NormalizedUsername` and tenant-scoped uniqueness on `(TenantId, NormalizedUsername)` and `(TenantId, Name)` for Roles. `Invoice.DeviceId` is now required at the application/entity level (every write path always populates it; a one-time migration backfill covers any legacy row that predates `DeviceId`). Single migration `AddTenantFoundation` implements the full expand+backfill+constrain in one pass (no separate phases needed) using `AddColumn` with a uniform bootstrap-tenant default instead of `AlterColumn`, since EF Core's SQLite provider does not support `AlterColumnOperation` at all. Every application service, sync push/pull handler (both `POS.Api/Program.cs` and the WPF-side `InvoiceSyncService.cs` mirror), and the `POS.Web` user-management postback now stamps `TenantId` correctly; full `POS.Tests` suite (77/77) passes against the real migrated SQLite schema via `Database.Migrate()`, not just `EnsureCreated`.

**Known T1 gaps carried forward:**
- Only validated against SQLite so far — the same migration has not yet been rehearsed against PostgreSQL (production provider). Postgres supports `AlterColumn` natively so should behave differently there; needs its own rehearsal before a Postgres rollout.
- `Invoices.DeviceId` and `AuditLogs.StoreId` keep their pre-migration nullability at the **database** level (SQLite can't `AlterColumn` an existing table at all) — enforced at the entity/application level instead. Revisit when a real table-rebuild or Postgres-only tightening is worth the risk.
- `POST /api/sync/categories/push` still has no auth context to derive a tenant from (pre-existing gap, documented in the T0 audit) — falls back to the sole bootstrap tenant; needs a real fix in T4.
- Read-side sync/query scoping by tenant (not just correct writes) is explicitly T4 scope, not done here.
- A hard-coded uppercase-GUID-literal gotcha was hit and fixed during T1: `Microsoft.Data.Sqlite` binds `Guid` parameters as uppercase text while `Guid.ToString()` is lowercase, so raw-SQL-seeded GUIDs (this migration's bootstrap tenant, and the pre-existing `Currencies.Id` seed data) must be uppercased or every future EF-written FK reference to them silently fails. Worth remembering for any future hand-authored migration in this codebase.

**T2 in progress — real password authentication landed (2026-09-21):** `AuthService.LoginAsync` previously matched on username only and never checked `PasswordHash` at all (T0 audit finding — this was a genuine unauthenticated-login gap, not just "weak auth"). Fixed:
- `IAuthService.LoginAsync(username, password, ...)` now verifies the password with `BCrypt.Net.BCrypt.Verify` against the stored hash; a bad username, inactive account, or wrong password all return the same generic "Invalid username or password." (no enumeration).
- WPF `LoginWindow` has a real `PasswordBox` now (was username-only); `POS.Api`'s `LoginRequest` and `POS.Web`'s `AccountController`/`Login.cshtml` both take a password.
- Seeded demo users (`admin`/`cashier`) now get randomly generated passwords instead of the trivial `"admin"`/`"cashier"` literals — see `DatabaseSeeder.DemoAdminPassword`/`DemoCashierPassword` and [start.md](start.md) for the actual values.
- New users created via the WPF Users screen (`UserManagementService.CreateUserAsync`) or the web dashboard's Management page (`ManagementController.CreateUser`) — both previously set `PasswordHash = string.Empty`, meaning no one could ever have logged in as a newly created user even before this pass — now get a random temporary password (`RandomPasswordGenerator`, `POS.Application/Support`) shown once at creation time (`Users_TemporaryPasswordFormat` message box in WPF; `TempData["ManagementSuccess"]` in Web). There is no forced-password-change-on-first-login flow yet.
- Full `POS.Tests` suite (77/77) passes with real password checks exercised end-to-end (API JWT login, Web cookie login, WPF `AuthService` unit-level paths).

**T2 in progress — device (Box) enrollment landed (2026-09-21, same day, follow-up turn):** T0 audit finding #5 ("Device identity is fully unauthenticated and auto-provisioned") fixed as an opt-in feature:
- `Device` gained `EnrollmentCodeHash`, `EnrolledAt`, `IsRevoked`, `RevokedAt` (migration `AddDeviceEnrollment` — plain `AddColumn`s only, no `AlterColumn`, so it's SQLite-safe by construction this time).
- New `IDeviceManagementService`/`DeviceManagementService`: `ProvisionDeviceAsync` (admin creates a not-yet-enrolled device row + one-time enrollment code, shown once — only its BCrypt hash is stored), `EnrollCurrentMachineAsync` (redeems the code and rebinds that device row's `Name` to the current machine, consuming the code), `RevokeDeviceAsync`, `GetDevicesAsync`.
- New WPF "Devices" screen (`DeviceManagementWindow`/`ViewModel`, sidebar entry gated on `Permission.ManageSettings`) to provision, revoke, and self-enroll from the same screen.
- `SaleService.GetOrCreateCurrentDeviceAsync` now throws `DeviceNotAuthorizedException` (caught and shown as a friendly warning in `MainViewModel.NewSaleAsync`) when: the matched device `IsRevoked` (**always enforced, unconditionally** — this part needed no opt-in), or when `Sync:RequireDeviceEnrollment=true` and the device is either missing entirely or provisioned-but-not-yet-enrolled. That config key is **not** set anywhere by default (absent = `false` everywhere, including `POS.Wpf/appsettings.json`), so the existing lenient auto-create-and-trust behavior is completely unchanged out of the box — a store opts into strict enrollment by adding the key to its own `appsettings.json`. This was deliberate: flipping the default now would have broken the demo quick-start flow in `start.md` (no device is pre-provisioned/enrolled for the seeded demo data).
- Enrollment state syncs like any other `Device` field (`DeviceSyncDto` extended, both `UpsertDeviceSnapshotAsync` copies updated) — but revocation is deliberately **sticky** in that sync path: an incoming snapshot can only ever flip `IsRevoked` from false→true, never back, so a stale/compromised client pushing an out-of-date "not revoked" snapshot can't un-revoke itself.
- 6 new tests (`DeviceManagementServiceTests`) cover provisioning, wrong-code rejection, successful enrollment + one-time-code consumption, sale blocked when enrollment is required and the machine isn't provisioned, sale succeeds after enrollment, and revocation blocking sales even in the default lenient mode. Full `POS.Tests` suite: 83/83 passing.

**Important gap surfaced by this work, relevant to the original "multiple Boxes per branch" request:** enrollment only helps a *second physical machine that already has a correctly-configured local database for the same store*. A genuinely fresh WPF install today has no bootstrap flow to "join an existing store" at all — `DatabaseSeeder` unconditionally seeds a **brand-new independent tenant** on first run if none exists locally, so a second real cash register can't yet just be installed and pointed at the same store; that bootstrap flow is T3 scope ("Tenant-safe WPF and offline profiles" — tenant.md explicitly calls out "a separate enrolled data profile/bootstrap" for this). Device enrollment as built here is the right foundation for that later flow, but does not by itself solve "add a second real register."

**Bug found and fixed the same day (2026-09-21):** the password-authentication pass above shipped `NormalizeBrokenDemoPasswordHashes`, a startup fixup that only re-hashes *broken* (empty/plaintext/malformed) demo-user password hashes — it deliberately left any *valid* BCrypt hash alone, including a hash for a different password, on the theory that a real user might have changed it. That safety check backfired for every pre-existing local dev database: `pos.db` files seeded before this feature existed (back in Stage 1) hold a **valid** BCrypt hash for the old literal password `"admin"`/`"cashier"`, so those installs silently kept rejecting the new documented demo passwords in [start.md](start.md) forever, with no way to self-heal short of deleting the database file. Fixed in `DatabaseSeeder.NormalizeBrokenDemoPasswordHashes`: the two seeded demo accounts now also get upgraded when their hash verifies against the specific pre-9/21 literal username-as-password value, while a hash matching neither the current nor that legacy literal password is still left untouched (so an intentionally-changed password is still never clobbered). Verified directly against the reporter's actual local `pos.db` (hash matched `"admin"`, not the new password) before and after the fix.

**T2 in progress — tenant-scoped login landed (2026-09-21, same day, follow-up turn):** `AuthService.LoginAsync` resolved its username lookup with no `TenantId` filter at all (`db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == nameLower ...)`), so the tenant.md T2 exit criterion "same username works independently in two tenants" did not actually hold — with two tenants both containing an `admin` user, login would silently authenticate against whichever tenant's row EF returned first, regardless of which tenant's password was supplied. Not yet exploitable in practice (every current deployment seeds exactly one bootstrap tenant and there is still no tenant-provisioning flow), but a live latent cross-tenant auth bug once T5 adds real tenant onboarding. Fixed:
- `IAuthService.LoginAsync` gained an optional `tenantSlug` parameter. An explicit slug must match an active, non-deleted `Tenant.NormalizedSlug`. With no slug, the service auto-resolves only when exactly one active tenant exists system-wide (true for every desktop SQLite profile and today's single-bootstrap-tenant server) — the moment a second tenant exists, an omitted slug fails closed instead of guessing. A suspended tenant can never authenticate, slug or not.
- The user lookup itself is now scoped by the resolved `TenantId`, so two tenants can safely share the same username with independent passwords.
- `ICurrentSession` gained a `TenantId` member; `Set(...)` now takes it as its first parameter. All three implementations (`CurrentSession` in WPF, `ApiCurrentSession`, `WebCurrentSession`) plus the `TestCurrentSession` test fake were updated. `POS.Api` issues a `tenant_id` JWT claim on login and rehydrates it into `ApiCurrentSession` per request; `POS.Web` does the same as a cookie claim through `WebClaimTypes.TenantId`.
- `POS.Api`'s `LoginRequest` and `POS.Web`'s `LoginViewModel`/`Login.cshtml` gained an optional `TenantSlug`/"Business" field ("leave blank for a single-business deployment"). WPF's `LoginWindow` was deliberately left unchanged — a local WPF install only ever holds one tenant by construction (T3 scope will formalize per-install tenant binding), so the existing username/password-only flow keeps working via auto-resolution.
- 5 new tests (`AuthServiceTests`) cover: same username authenticating independently in two tenants, the right password rejected against the wrong tenant's slug, an omitted slug failing once a second tenant exists, an unknown slug being rejected, and a suspended tenant being blocked. Full `POS.Tests` suite: 88/88 passing.

**T2 in progress — granular Web/API permission enforcement landed (2026-09-21, same day, follow-up turn):** `POS.Web` gated its entire dashboard on `RequireRole("Admin", "Manager")` — a hardcoded role *name* check totally decoupled from the `Permission` bitmask WPF already enforces (a custom role named e.g. "Store Ops" with real permissions couldn't reach the dashboard at all; a role literally named "Manager" got full access even with `PermissionsMask = None`, since no seeded role is even named "Manager"). `POS.Api`'s JWT claims never carried permissions at all — every authenticated user, including a Cashier, could hit `/api/sales/{id}/refund` with no `ProcessRefunds` check, unlike WPF. Fixed:
- `permissions` now flows as a real claim on both surfaces: `POS.Api` issues it on JWT login and rehydrates it into `ApiCurrentSession` per request; `POS.Web` issues it as a cookie claim via `WebClaimTypes.Permissions`. Both previously hardcoded this to `0` with a "not wired up yet" comment.
- `POS.Web`'s `DashboardAccess` policy is now "any permission granted" (`PermissionsMask != Permission.None`) instead of a role-name check, so dashboard reachability now follows the same model as WPF regardless of what a role is named. New per-permission policies (`WebAuthorizationPolicies.ManageProducts/ViewReports/ViewAudit/ManageUsers/ManageSettings/ProcessRefunds`) gate each `ManagementController` mutating action and all of `DashboardController` (reports = `ViewReports`) individually — e.g. `CreateUser`/`UpdateUser` require `ManageUsers`, product/category CRUD require `ManageProducts`, settings/currency-policy updates require `ManageSettings`. `AccountController` now rejects sign-in for a user with `PermissionsMask == None` instead of checking the role name.
- `Views/Management/Index.cshtml` hides (not just disables) the six mutating forms — store profile, operational settings, currency policy, create-user, create-category, create-product — behind the matching `Session.HasPermission(...)` check, showing a "Read-only — requires the ... permission" notice instead; the inline roster edit/delete forms (existing users/categories/products lists) are left visible since the server-side `[Authorize]` on their actions already blocks unauthorized submission via the existing `AccessDeniedPath` redirect. `ViewAudit` has a policy defined but nothing to gate yet — `POS.Web` has no audit-log viewer at all (WPF-only today).
- `POS.Api`: only the refund endpoint (`POST /api/sales/{id}/refund`) is gated behind the new `ProcessRefundsPolicy`, matching WPF's existing enforcement for the same action. Every other endpoint (catalog, cart mutation, sync) deliberately stays at the existing "any authenticated user" `RequireAuthorization()` default — broader per-endpoint API permission coverage is future work, not part of this pass.
- 3 new tests: `ApiIntegrationTests.Refund_endpoint_rejects_a_user_without_ProcessRefunds_permission`, `WebIntegrationTests.Sign_in_is_rejected_for_a_user_with_no_granted_permission`, `WebIntegrationTests.User_without_ManageUsers_permission_cannot_create_users` (also asserts the create-user form is hidden and the user is never persisted). Full `POS.Tests` suite: 91/91 passing.

**T2 in progress — change/reset password flow landed (2026-09-21, same day, follow-up turn):** until now the only way to set a password was `CreateUserAsync`'s one-time random temporary password shown at creation — there was no way for a user to change their own password, or for an admin to reset a forgotten one, ever again. Fixed:
- `IUserManagementService` gained `ChangeOwnPasswordAsync(currentPassword, newPassword)` — verifies the caller's current password, rejects a new password under 8 characters, available to any authenticated user for their own account only (no permission required) — and `ResetUserPasswordAsync(userId)` — generates a new random temporary password the same way user creation does, requires `ManageUsers`, scoped to the caller's own store.
- New shared `PasswordHasher` (`POS.Infrastructure/Services`) dedupes the BCrypt-verify-with-malformed-hash-safety logic that both `AuthService` and the new `UserManagementService` methods need; `AuthService.VerifyPassword` was removed in favor of it.
- **WPF**: a new top-bar padlock button (available to every signed-in user, no permission gate — a Cashier with a temporary password needs this regardless of dashboard access) opens `ChangePasswordWindow` for self-service change. The existing Users screen gained a "Reset password" button next to Save/New, showing the new temporary password the same way user creation already does. Both are fully localized (EN/AR/HE).
- **Web**: `AccountController.ChangePassword` (GET/POST, `[Authorize]` only — reachable by anyone who can sign in) gives self-service change from a "Change password" link in the header nav; `ManagementController.ResetUserPassword` (`ManageUsers`-gated) adds a "Reset password" button to each row of the user roster, showing the generated temporary password via the existing flash-message mechanism.
- Deliberately **not** built for `POS.Api`: nothing calls user-management through the API's HTTP surface today (WPF/Web talk to the shared application services in-process, not over HTTP), so there's no client yet for a `/api/auth/change-password` endpoint — add one if/when a real API consumer needs it.
- 5 new tests: `UserManagementServiceTests.ChangeOwnPassword_succeeds_with_correct_current_password_and_rejects_wrong_one`, `.ChangeOwnPassword_rejects_new_password_shorter_than_8_characters`, `.ResetUserPassword_generates_a_new_password_that_verifies_against_the_stored_hash`, `WebIntegrationTests.Self_service_change_password_updates_the_signed_in_users_own_credential`, `.Admin_can_reset_another_users_password`. Full `POS.Tests` suite: 96/96 passing.

**T2 in progress — minimal authenticated tenant/store/first-admin provisioning landed (2026-09-21, same day, follow-up turn):** until now there was no way to create a *second* tenant at all — `DatabaseSeeder` only ever creates one bootstrap tenant on first run, so T2's tenant-slug login had nothing real to resolve against, and the T2 exit criterion "same username works independently in two tenants" was only proven by test fixtures, never by an actual product flow. Fixed, deliberately staying "minimal" and explicitly *not* public self-service signup per tenant.md:
- New `ITenantProvisioningService`/`TenantProvisioningService` (`POS.Infrastructure`) creates a Tenant + its first Store + an Admin role with `Permission.All` + the first admin User + their `UserStoreAccess`, atomically. Validates a non-empty name/slug/store name/username, a unique slug, an admin password of at least 8 characters (matching `ChangeOwnPasswordAsync`'s rule), and that the requested base-currency code (defaults to `ILS`, matching `DatabaseSeeder`'s convention) exists as global reference data.
- Exposed only via `POS.Api`: `POST /api/platform/tenants`, disabled by default and only enabled by setting `Platform:ProvisioningSecret`; callers authenticate with that shared secret via an `X-Provisioning-Secret` header (constant-time compared), not a normal user/JWT identity — there's no user to log in as yet when bootstrapping a tenant's very first account. No UI anywhere calls this yet; it's operator-only (curl/ops script), matching "avoid public self-service signup in this milestone." Documented in [docs/API_DEPLOYMENT.md](docs/API_DEPLOYMENT.md).
- Config gotcha hit and fixed while building this: reading `Platform:ProvisioningSecret` from `builder.Configuration` *before* `builder.Build()` silently misses test-host configuration overrides (`WebApplicationFactory`'s injected config isn't guaranteed merged in until `Build()` runs) — moved the read to `app.Configuration` after `Build()`. `Jwt:SigningKey` happened to read fine at the earlier point only because `appsettings.Development.json` already supplies a fallback value that masked the same underlying timing issue; worth remembering for any future config read that must observe test-injected values.
- 5 new tests: `TenantProvisioningServiceTests` (creates tenant/store/admin, rejects a slug already in use, rejects a short password) plus `ApiIntegrationTests.Platform_tenant_provisioning_is_disabled_without_a_configured_secret` and `.Platform_tenant_provisioning_requires_the_configured_secret_and_creates_a_working_tenant` (proves the full loop: wrong/missing secret rejected, tenant created, its admin logs in with the new tenant's slug, and the *original* bootstrap tenant's `admin` still logs in independently with its own slug — the actual end-to-end proof of the T2 exit criterion). Full `POS.Tests` suite: 101/101 passing.

**Bug found and fixed the same day (2026-09-21, follow-up turn):** `InvoiceSyncService.TryCreateAuthorizedClientAsync` (the WPF background sync worker's own re-login to the API) sent `new { Username = _session.Username }` with no `Password` field. Once `/api/auth/login` started verifying a real password (the T2 password-auth pass earlier the same day), this call always failed `string.IsNullOrEmpty(password)` on the server, so **WPF-to-API sync silently stopped working entirely** for every real (non-mocked) deployment — every push/pull degraded to its "client is null" failure path without throwing, and no existing test caught it because every sync test fakes the `/api/auth/login` HTTP handler rather than exercising the real `AuthService`. Fixed:
- `ICurrentSession` gained `Password` (get) and `SetPassword(string?)`, kept in memory only (never persisted/logged). `AuthService.LoginAsync` calls `_session.SetPassword(password)` right after `_session.Set(...)`, so the verified plaintext password is available for the device's own outbound HTTP session for as long as that session stays signed in.
- `TryCreateAuthorizedClientAsync` now sends `{ Username, Password }` from the session and fails closed (returns `null` immediately, no HTTP call) if `Password` is empty.
- `UserManagementService.ChangeOwnPasswordAsync` also calls `_session.SetPassword(newPassword)` so a self-service password change doesn't leave the sync worker retrying a now-stale cached password until next login.
- 2 new tests (`InvoiceSyncServiceTests`) assert the real behavior: the login call now carries the session's password, and sync fails safe (no login attempt, invoices counted as failed, no crash) when `Password` is unset. Both were verified to fail against the pre-fix code before the fix was reapplied.

**T2 in progress — broader `POS.Api` sync-endpoint permission coverage landed (2026-09-21, same day, follow-up turn):** the sync push endpoints re-authenticate as the currently signed-in user's own verified identity (see the bug fix above), so — before this pass — **any authenticated user, including a Cashier with `Permission.None`, could push an arbitrary `Role.PermissionsMask`, product price, category, store setting, device row, or currency policy to the server** through `/api/sync/*/push`, with no permission check at all. This is exactly the "remove the ability to escalate privileges through existing user/role sync" gap tenant.md's T2 milestone calls out. Fixed:
- Three new authorization policies (`ManageUsersPolicy`, `ManageSettingsPolicy`, `ManageProductsPolicy`), matching the existing `ProcessRefundsPolicy` pattern (bitmask `HasFlag` assertion against the JWT's `permissions` claim).
- Gated to mirror the equivalent WPF/Web-side gate on the same mutation: `/api/sync/users/push` → `ManageUsers`; `/api/sync/settings/push` and `/api/sync/currency-policy/push` and `/api/sync/devices/push` → `ManageSettings`; `/api/sync/products/push` and `/api/sync/categories/push` → `ManageProducts`.
- Catalog browsing, cart/sale lifecycle endpoints, and every `/pull` endpoint deliberately stay at the existing "any authenticated user" default — matching WPF, where any signed-in cashier can sell and read-scoped sync data isn't a write-escalation risk. `InvoiceSyncService`'s push handlers already treat a non-success response as a soft per-aggregate failure (not a crash), so a lower-permission session simply skips pushing that aggregate on its next sync cycle rather than breaking sync overall.
- 1 new test (`Sync_push_endpoints_reject_a_user_without_the_matching_management_permission`) proves a Cashier-permission session gets `403 Forbidden` from all six gated push endpoints; verified to fail against the pre-fix code before the fix was reapplied. Full `POS.Tests` suite: 104/104 passing.

**T2 in progress — login rate limiting landed (2026-09-21, same day, follow-up turn):** tenant.md's identity section calls for rate limiting to slow brute-force/enumeration against login; neither `POS.Api` nor `POS.Web` had any. Fixed:
- Both hosts register `AddRateLimiter` with a fixed-window policy (10 requests/minute, partitioned by client IP, `429 Too Many Requests` on rejection) and apply it only to the login POST — `POS.Api`'s `POST /api/auth/login` via `.RequireRateLimiting("login")`, `POS.Web`'s `AccountController.Login` POST via `[EnableRateLimiting(WebRateLimitPolicies.Login)]`. Every other endpoint is unaffected.
- Doing this surfaced a real pre-existing inefficiency that would have made the limit unworkable: `InvoiceSyncBackgroundService`'s single sync pass calls up to 16 `IInvoiceSyncService` push/pull methods, and every one of them independently called `TryCreateAuthorizedClientAsync`, which re-logged in from scratch each time — 16 `/api/auth/login` calls per pass (every 30s by default). Fixed by caching the authorized `HttpClient` on the `InvoiceSyncService` instance (`_cachedAuthorizedClient`) — since it's `Scoped` and the background service creates a fresh scope per pass, this reduces it to exactly one login per sync pass without changing the per-pass scope of a fresh instance.
- 2 new tests (`Login_endpoint_rate_limits_repeated_attempts_from_the_same_client` in both `ApiIntegrationTests` and `WebIntegrationTests`) prove the 11th attempt within a minute gets `429`; both verified to fail (return `401`/generic error, not `429`) against the pre-fix code. Full `POS.Tests` suite: 106/106 passing.

**T2 in progress — `POS.Web` audit-log viewer landed (2026-09-21, same day, follow-up turn):** `ViewAudit` had a policy defined since the earlier granular-permissions pass but nothing to gate — `POS.Web` had no audit-log page at all (WPF-only). Fixed:
- New `AuditController` (`[Authorize(Policy = WebAuthorizationPolicies.ViewAudit)]` at the class level, matching `DashboardController`'s single-policy-per-controller pattern), reusing the existing store-scoped `IAuditLogService.GetRecentAsync` — no new query logic. Filters: from/to date, action (exact match), entity name (exact match).
- New `Views/Audit/Index.cshtml` and `AuditViewModel`/`AuditEntryRowViewModel`, styled consistently with the existing stock-ledger table (new `.table-row--audit` CSS rule alongside the existing `--inventory`/`--users`/`--invoices`/`--stock` variants). Nav link added to `_Layout.cshtml`.
- Caught and fixed a real bug before it shipped: the controller's query-string parameter was initially named `action`, which collides with ASP.NET Core's ambient route value `action` (the MVC action name, `"Index"`) — every filtered/unfiltered request silently bound to `"Index"` instead of the query string or `null`, so the page always rendered zero rows regardless of filters. Renamed to `actionName`. Caught by a test asserting on actual rendered row count (`table-row--audit` matches), not just substring presence — a weaker assertion (checking for the word "ProductUpdated" anywhere on the page) would have passed by accident because that string also appears in the filter input's placeholder text.
- 2 new tests (`Audit_page_lists_matching_entries_and_respects_filters`, `Audit_page_is_denied_without_ViewAudit_permission`). Full `POS.Tests` suite: 108/108 passing.

**T2 in progress — offline authorization expiry window landed (2026-09-21, same day, follow-up turn):** tenant.md §5 requires "a finite offline authorization validity period... do not silently permit indefinite cached administrative access." Until now a WPF device that stayed running (no restart) for weeks with no server contact kept full administrative access forever, with no way for a server-side revocation to ever reach it. Fixed, deliberately scoped to **administrative actions only** — selling stays available offline indefinitely, matching this POS's offline-first design; a chosen policy, not an oversight:
- `ICurrentSession` gained `LastOnlineContactUtc` (get) and `SetLastOnlineContactUtc(...)`, mirroring the `Password`/`SetPassword` pattern. `InvoiceSyncService.TryCreateAuthorizedClientAsync` records `DateTime.UtcNow` into it — and into a persisted `Sync.LastOnlineContactUtc` store setting, so it survives app restarts — on every successful online login (a real server-verified login is the strongest signal available that this device's user is still active and its credentials still valid). `AuthService.LoginAsync` reads that persisted value back into the session at every local login.
- New `IOfflineAuthorizationPolicy.IsAdministrativeAccessLocked()` (`POS.Infrastructure`): false whenever `Sync:Enabled` is false (no online concept for a deployment with no API at all — matches the demo quick-start, which ships with sync disabled by default); true if sync is enabled and this device has never once proven itself online; otherwise true once more than `Sync:OfflineAuthorizationHours` (default 24) have passed since `LastOnlineContactUtc`.
- `MainViewModel.EnsurePermission` (WPF's single choke point for `ManageProducts`/`ManageUsers`/`ManageSettings`/`ProcessRefunds` — already gates every admin screen/action) now also denies those four specifically when `IsAdministrativeAccessLocked()` is true, with a distinct "reconnect online first" status message (EN/AR/HE). `ViewReports`/`ViewAudit` (read-only) are deliberately not gated.
- 6 new tests: `OfflineAuthorizationPolicyTests` (sync disabled never locks; never-contacted locks; within/exceeding the default 24h window; configurable window) and `InvoiceSyncServiceTests.Push_unsynced_invoices_records_last_online_contact_after_a_successful_login` (proves a real sync login writes both the in-memory session value and the persisted setting). The window-detection assertions were verified to fail against a deliberately broken policy body before being confirmed correct. Full `POS.Tests` suite: 114/114 passing.

**Remaining T2 scope (not done in this pass):** an audit trail and any in-app UI for tenant provisioning (today it's silent and operator-only), and a per-request device credential for the sync protocol itself (today's enrollment code is a one-time setup gate, not a per-call credential — sync endpoints authenticate as the signed-in user, not the device, so a compromised device still impersonates whoever is currently logged in on it).

**T3 in progress — "join an existing business" bootstrap landed (2026-09-21, follow-up turn):** the real gap surfaced during T2's device-enrollment work: a genuinely fresh WPF install had no way to bind itself to an existing tenant/store — `DatabaseSeeder.SeedIfNeeded` always seeded a brand-new independent business, so a second real cash register could not simply be installed and pointed at an existing store. Fixed:
- New `IBusinessJoinService`/`BusinessJoinService` (`POS.Infrastructure`): given an API URL, business slug, username and password, logs into the remote server (`/api/auth/login`, reused as-is — confirmed with the user that this needed no new admin-issued-code API surface), decodes `tenant_id`/`permissions` from the returned JWT, fetches the store profile (`/api/stores/current`, also reused as-is), writes a minimal local `Tenant`+`Store`, populates `ICurrentSession` in memory, then reuses the existing, already-tested `IInvoiceSyncService` pull methods (`PullCurrencyPolicyAsync` → `PullRemoteSettingsAsync` → `PullRemoteUsersAsync` → `PullRemoteCategoriesAsync` → `PullRemoteProductsAsync` → `PullRemoteDevicesAsync`) to bootstrap everything else — no new reconciliation logic needed, since those methods already handle "the local row doesn't exist yet" as their ordinary steady-state case. Finishes with a real local `AuthService.LoginAsync` against the just-pulled user.
- **Real discovery made during this work, not assumed:** every database's migrations (`AddTenantFoundation`) unconditionally insert one placeholder "bootstrap tenant" row (plus one `TenantCurrencyRate` per global currency) into *every* database, including a brand-new one — to backfill any pre-existing single-tenant installs. This means `Tenants.Any()` is never a valid "is this fresh?" signal; `Users.Any()` is. `BusinessJoinService` defensively re-checks this itself (refuses to run if any local `User` already exists) before replacing the placeholder tenant/rate rows with the real remote ones.
- `InvoiceSyncService`'s pull methods each independently read `Sync:Enabled`/`Sync:ApiBaseUrl` from `IConfiguration` — `BusinessJoinService` sets both via `IConfiguration`'s indexer setter (takes effect immediately, in-process, no file or reload needed) before invoking them, using the server the operator just typed rather than whatever was previously configured.
- New WPF `SetupWindow`/`SetupViewModel`, shown once before `LoginWindow` only when the local database has no `User` rows yet, offering "start a new business" (today's unchanged `DatabaseSeeder.SeedIfNeeded` path) or "join an existing business" (the new flow above); on a successful join, `SetupViewModel` persists `Sync:Enabled`/`Sync:ApiBaseUrl` to a new `appsettings.local.json` (loaded via an additional `AddJsonFile(..., optional: true, reloadOnChange: true)` in `App.xaml.cs`) so future launches keep syncing, and `App.xaml.cs` skips `LoginWindow` entirely since the join already signed the session in for real. `App.xaml.cs`'s migration call changed from auto-seeding to `ApplyPosDatabaseMigrations(seedDemoData: false)` — every existing, already-configured install is unaffected (falls straight through to `LoginWindow` exactly as before).
- 3 new tests (`BusinessJoinServiceTests`): successful join (asserts local Tenant/Store/Users/Roles/Categories/Products/Inventory match the remote business and local login now succeeds), wrong password, and an unreachable server — both failure cases leave the local database still fresh (no `User` rows) so a retry is possible. A new `TestServiceHost.CreateAsync(..., seedBaseline: false)` option produces a genuinely empty, really-migrated local database (not `EnsureCreated`, to get the same fixed global-Currency IDs a real remote server's migrations produce) for this precondition. Full `POS.Tests` suite: 117/117 passing.
- **Deferred, not done in this pass:** re-pointing an *already provisioned* device at a different tenant (tenant.md calls this "profile maintenance" — requires a fresh bootstrap, not a relabel; no existing local profile to preserve here since it only applies to a fresh install); pulling historical invoices/audit logs during the join itself (the ordinary background sync loop, which starts every pull cursor at zero, backfills those automatically once the device starts running normally); WPF UI test coverage (this repo has none for any WPF screen — verification is via `BusinessJoinServiceTests` plus a documented manual walkthrough in `start.md`).

**Stage 4T complete:** [ ]

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
| 4T | Multi-tenant SaaS foundation | No |
| 5 | Web & beyond | No |

**Reference note:** The external reference project reviewed during planning was moved out of this repo to `C:\projects\pos_q`. If we need another comparison pass later, use that location instead of expecting `pos_q/` under this repository root.

*Last updated 2026-09-21: Stages 0–4 complete (Stage 4.4 sync coverage, Stage 5.1 `POS.Web` baseline — see their sections above). Active work is Stage 4T (multi-tenant SaaS foundation, detailed above): T0 complete; T1 in progress (SQLite-only so far, PostgreSQL rehearsal and read-side sync scoping still open); T2 in progress — password auth, device enrollment, tenant-scoped login, granular Web/API permission enforcement, change/reset password, minimal authenticated tenant provisioning, a fix for a same-day regression that had silently broken all WPF-to-API sync, broader `POS.Api` sync-endpoint permission coverage, login rate limiting (plus a sync-worker login-count fix that made the rate limit workable), a `POS.Web` audit-log viewer, and a 24h offline-authorization expiry window for administrative actions have all landed; remaining T2 scope is listed at the end of the T2 notes above (an audit trail/UI for tenant provisioning and a per-request sync device credential). T3 in progress — a WPF "join an existing business" bootstrap now lets a fresh install bind itself to an existing tenant/store instead of always seeding an independent one (see T3 notes above); re-pointing an already-provisioned device to a different tenant remains deferred. T4–T7 not started. Full `POS.Tests` suite: 117 passed, 0 failed.*



