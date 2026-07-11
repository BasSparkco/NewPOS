# Sync Strategy

This document records the current Stage 4.4 sync behavior implemented in the repository.

## Current Scope

- Outbound invoice sync from the desktop client to `POS.Api`
- Outbound audit-log sync from the desktop client to `POS.Api`
- Outbound category snapshot sync from the desktop client to `POS.Api`
- Outbound product snapshot sync from the desktop client to `POS.Api`
- Outbound store settings sync from the desktop client to `POS.Api`
- Outbound user snapshot sync from the desktop client to `POS.Api`
- Outbound currency-policy aggregate sync from the desktop client to `POS.Api`
- Outbound device snapshot sync from the desktop client to `POS.Api`
- Background sync worker in the WPF host
- Aggregate-level invoice snapshot push over REST
- Aggregate-level category snapshot pull from server to client
- Aggregate-level invoice snapshot pull from server to client
- Aggregate-level product snapshot pull from server to client
- Store settings pull from server to client
- User snapshot pull from server to client
- Currency-policy pull from server to client
- Device snapshot pull from server to client
- Audit-log pull from server to client
- Version-based conflict detection on the server
- Server-side inventory reconciliation for synced paid/refunded invoice snapshots
- Client-side invoice apply plus inventory reconciliation for pulled higher-version snapshots
- Client-side category/product/settings/user/currency-policy/device/invoice/audit-log apply using sequence-based pull cursors stored in `Settings`

This is the first sync slice, not the final sync platform. It focuses on moving unsynced invoice aggregates safely enough to establish the transport and conflict contract.

## Current Flow

1. The desktop app marks invoices `IsSynced = false` and increments `SyncVersion` whenever invoice state changes.
2. The WPF background worker wakes on a configurable interval when `Sync:Enabled` is true.
3. The worker logs into the API with the current username and gets a JWT.
4. The worker pushes the local currency-policy aggregate whenever the local `SyncChanges` sequence shows a newer currency-policy change than the last persisted push cursor.
5. The worker then pushes newer local settings, user snapshots, category snapshots, product snapshots, device snapshots, and audit-log rows selected by aggregate-specific `SyncChanges` sequences to `/api/sync/settings/push`, `/api/sync/users/push`, `/api/sync/categories/push`, `/api/sync/products/push`, `/api/sync/devices/push`, and `/api/sync/audit-logs/push`, followed by unsynced invoice snapshots to `/api/sync/invoices/push`.
6. Currency policy runs first because a base-currency change also converts catalog pricing and open invoices.
7. The API applies or rejects each pushed aggregate independently under its aggregate-specific conflict rule.
8. When an applied invoice snapshot changes the effective stock impact of an invoice, the server reconciles `Inventory` and appends `StockMovements` in the same transaction.
9. Product push also aligns the server inventory snapshot for that product/store and appends a manual-adjustment stock movement when the quantity changes.
10. The worker then calls `/api/sync/currency-policy/pull`, `/api/sync/settings/pull`, `/api/sync/users/pull`, `/api/sync/categories/pull`, `/api/sync/products/pull`, `/api/sync/devices/pull`, `/api/sync/invoices/pull`, and `/api/sync/audit-logs/pull` with the last stored sequence cursor for those aggregates.
11. The desktop client applies pulled invoice snapshots only when the local invoice is missing or older, reconciles local inventory deltas for paid/refunded state changes, and persists the invoice pull cursor in `Settings` as the latest applied sequence.
12. The desktop client also applies newer currency-policy, settings, user, category, product, and device snapshots locally. User reconciliation matches by `User.Id` or username and creates missing roles by role name so username-driven auth and audit flows remain resolvable after sync. Pulling currency policy also advances the local push cursor to avoid immediately echoing the same aggregate back to the server.
13. The desktop client also advances store-scoped aggregate push cursors as the server applies or skips category, product, setting, user, device, currency-policy, and audit-log batches, then appends pulled remote audit-log rows when they do not already exist locally and persists a separate audit-log pull cursor as the latest applied sequence.
14. The desktop client marks pushed invoices synced only when the server accepts the same `SyncVersion` it sent.
15. Aggregate push ordering for categories, products, settings, devices, currency policy, and audit logs now follows the same local `SyncChanges` sequence model used by pull cursors, while invoice sync still relies on `IsSynced` plus `SyncVersion`, and invoice sync reuses the same device upsert guard so an older invoice snapshot cannot overwrite a newer device row by name.

## Conflict Rule

Current rule: invoice aggregate version precedence.

- If the server has no invoice with that ID for the authenticated store, the snapshot is applied.
- If the server has the same invoice ID with a lower `SyncVersion`, the incoming snapshot replaces the server aggregate.
- If the server has the same invoice ID with the same `SyncVersion`, the push is treated as idempotent and skipped.
- If the server has the same invoice ID with a higher `SyncVersion`, the push is rejected as a conflict.

This is effectively a last-write-wins policy constrained by an explicit aggregate version.

For categories, products, settings, users, devices, and currency policy in both directions, the current rule is `UpdatedAt` precedence: if the destination row or aggregate is missing or older, the snapshot applies; if it is equally new or newer, the row or aggregate is skipped. User sync reconciles by `User.Id` first and falls back to username when IDs differ, then ensures the referenced role exists by name before applying the snapshot.

For audit logs, the current rule is append-only idempotence: if the destination store already has the same `AuditLog.Id`, the row is skipped; otherwise the row is inserted and preserved by its original `CreatedAt` value.

## Current Limitations

- It assumes referenced product IDs already exist on the server.
- It also assumes referenced product IDs already exist on the client before a pulled invoice is applied.
- Product pull is snapshot-based and uses the current server inventory quantity, not a dedicated stock-adjustment change stream.
- Currency policy is aggregate-based rather than a dedicated per-currency change stream, so it is optimized for correctness around base-currency conversion, not row-level merge semantics.
- User sync currently treats username as the practical natural identity when user IDs differ across nodes, which fits the current auth/audit model but is not yet a full identity-federation design.

Server-to-client pull cursors are now backed by `SyncChanges` rows keyed by a monotonic numeric sequence, persisted as store-scoped settings. The same numeric sequence model also backs outbound aggregate push cursors for categories, products, settings, users, devices, currency policy, and audit logs. Each pull or aggregate-push slice reads changes after the supplied sequence and persists `NextSinceVersion` as the latest accepted sequence for that aggregate. Legacy pre-sequence cursor values are treated as zero for compatibility. Invoice push remains intentionally separate and still uses `IsSynced` plus `SyncVersion`.

## Stage 4.4 Baseline

The current Stage 4.4 baseline is complete.

Future sync expansion should follow these rules:

1. hold the current aggregate set unless new management write paths appear for additional admin data
2. keep standalone `Role` sync out of scope until roles are independently managed rather than created indirectly through user reconciliation
3. keep `Store` profile sync out of scope until the app has real store-profile mutations beyond the already-covered currency-policy and settings flows
