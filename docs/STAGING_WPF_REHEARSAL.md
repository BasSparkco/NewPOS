# Windows client (WPF) staging rehearsal

Exact steps to point a real `POS.Wpf` install at the staging environment and exercise cash-session
management, offline sales/refunds, logout, device-based synchronization, reconnection, and closing
reconciliation. This is a **manual runbook** — it requires a real Windows machine and has not been
executed yet (this environment cannot drive a WPF GUI). See "What's already proven automatically"
below for what does not need to be repeated by hand.

## Prerequisite

`https://pos-api-staging.sparkco.vip` must be externally reachable first — see
[STATUS.md](../STATUS.md)'s staging deployment entry (point 9) for the current blocker (Cloudflare
proxying breaks Traefik's ACME challenge for the wildcard subdomain) and the two options to resolve
it. Everything below assumes that is resolved and a test tenant has been provisioned per
[STAGING_DEPLOYMENT.md](STAGING_DEPLOYMENT.md)'s "Seeding test data" section (tenant slug
`staging-rehearsal`, admin username `owner`).

## Setup: join the staging business

1. On a clean or freshly-reset `POS.Wpf` install (no local `pos.db`, or delete it first — this is
   the same "fresh install" path `BusinessJoinServiceTests` already covers automatically, see
   below), launch the app. `SetupWindow` appears (only shown when the local database has no `User`
   rows yet).
2. Choose "Join an existing business."
3. Enter:
   - API URL: `https://pos-api-staging.sparkco.vip`
   - Business slug: `staging-rehearsal`
   - Username: `owner`
   - Password: (the one used when provisioning the tenant)
4. Confirm the join succeeds and the app proceeds straight to the main cashier screen (no separate
   login step — `BusinessJoinService` signs the session in as part of the join). Confirm the store
   name shown matches "Main Store."
5. Confirm `Sync:Enabled`/`Sync:ApiBaseUrl` were persisted to `appsettings.local.json` next to the
   executable, so sync keeps working on the next launch without repeating this step.

## Cash session

6. Open the "Cash" screen from the sidebar (no permission gate — any signed-in cashier manages
   their own till).
7. Open a session with a float amount, e.g. 100.00 ILS. Confirm the summary shows opening float
   100.00, zero sales/refunds/movements, expected balance 100.00.

## Offline sales and refunds

8. Disconnect the machine from the network (or set `Sync:Enabled=false` in
   `appsettings.local.json` and restart — either simulates offline).
9. Ring up at least two sales of the product created during staging setup (or any product visible
   in the catalog pulled down during the join). Complete them as cash sales.
10. Refund one of them via the Refund window.
11. Confirm the Cash screen's running summary updates for both the sale and the refund even though
    the device is offline (cash movements are recorded locally, atomically, regardless of sync
    state).
12. Confirm the product's stock decremented/incremented correctly in the local Products screen.

## Logout survives the session

13. Use the sidebar "Logout" button (a real logout — closes the window, clears `ICurrentSession`,
    returns to `LoginWindow`). Confirm you are returned to the login screen, not just the cashier
    home tab.
14. Log back in as the same user (or a different user with cash-session permission, if testing
    per-cashier vs. per-register mode). Confirm the cash session is **still open** — logout must
    never close it — and the running summary still reflects the offline sales/refund from steps
    9-10.

## Reconnect and verify sync

15. Reconnect the network (or set `Sync:Enabled=true` again and restart).
16. Wait for the background sync worker's next pass (default interval — check
    `appsettings.local.json`/`appsettings.json` for `Sync:IntervalSeconds`, or trigger manually if
    the build exposes a manual-sync action).
17. On `https://pos-staging.sparkco.vip` (Web dashboard, basic-auth protected), open Management and
    confirm:
    - The offline invoices now appear in the stock ledger with the correct movement types and
      quantities.
    - The product's stock quantity matches what the device showed locally.
    - If a second device/session was used to create an overlapping sale (see
      "Two-device discrepancy check" below), confirm the discrepancy badge/filter surfaces it
      correctly.

## Closing reconciliation

18. Back in the WPF Cash screen, close the session with a counted cash amount. Enter a value that
    deliberately differs slightly from the expected balance shown.
19. Confirm the discrepancy amount displayed matches (counted − expected), and that the session
    status changes to closed.
20. Confirm the closed session can no longer accept new movements, and that closing it a second
    time is rejected (already covered by `CashSessionServiceTests.Closing_a_cash_session_...`
    automated tests, but worth a manual sanity check here too).

## Optional: two-device discrepancy check

To manually exercise the exact scenario `Two_offline_devices_selling_the_last_unit_...` proves
automatically (see below), repeat steps 8-9 on a **second** WPF install joined to the same
business/store, both starting from the same low-stock product, both going offline, both selling
the last unit before either reconnects. After both reconnect, confirm on the Web dashboard that
both sales are present (neither discarded), the resulting stock is negative, and it is flagged as
a discrepancy.

## What's already proven automatically — do not re-derive by hand

The underlying sync/reconciliation/cash-session *logic* exercised by this runbook is already
covered by the automated `POS.Tests` suite (169 SQLite-based tests, plus the live PostgreSQL
rehearsal recorded in STATUS.md) — this runbook exists to catch what automated tests structurally
cannot: real GUI wiring, real hardware/network disconnection behavior, actual button clicks,
printing, and the operator experience. In particular:

- `BusinessJoinServiceTests` already proves the "join an existing business" flow end-to-end
  (successful join, wrong password, unreachable server), including that local data matches the
  remote business afterward.
- `CashSessionServiceTests` already proves opening, closing, expected-balance/discrepancy
  computation, cash-out approval, and that logout must never close a session.
- `Two_offline_devices_selling_the_last_unit_both_apply_and_the_shortfall_is_a_visible_discrepancy`
  (API test) and `Web_stock_adjustment_followed_by_a_reconnecting_devices_sale_reconciles_both_effects`
  (Web test) already prove the reconciliation policy this runbook's steps 9-17 exercise manually,
  including against live PostgreSQL.
- `InvoiceSyncServiceTests` already proves sync continues correctly after logout via the device
  credential, and that a pulled invoice with an unresolvable username is skipped rather than
  misattributed.

If a manual run of this checklist finds behavior that contradicts what the automated tests assert,
that is a real bug in either the GUI layer or a gap in test coverage — worth a fresh investigation,
not a reason to distrust either the tests or the manual result without checking which one is wrong.

## Results

Not yet executed — record the date, WPF build/commit, and outcome of each numbered step here once
run.
