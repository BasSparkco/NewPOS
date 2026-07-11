# Stage 3 Execution Checklist

This checklist translates the remaining Stage 3 work into ticket-sized implementation steps with concrete file targets.

Status baseline:
- 3.1 is complete.
- 3.2 is complete.
- 3.4 is complete.
- 3.5 is partially implemented through currency policy UI; general persisted settings remain.
- 3.6 is not started.
- 3.3 remains optional and should not block Stage 4 unless the release needs it.

## Ticket 1 - Add typed settings backend

Goal: create a persisted settings slice instead of hard-coded operational defaults.

Primary file targets:
- `src/POS.Core/Entities/Setting.cs`
- `src/POS.Infrastructure/Data/PosDbContext.cs`
- `src/POS.Infrastructure/Data/Configurations/SettingConfiguration.cs`
- `src/POS.Infrastructure/Data/Migrations/<new migration>.cs`
- `src/POS.Application/Abstractions/ISettingsService.cs`
- `src/POS.Application/Models/StoreSettingsDto.cs`
- `src/POS.Infrastructure/Services/SettingsService.cs`
- `src/POS.Infrastructure/DependencyInjection.cs`

Acceptance notes:
- Support at least low-stock threshold, negative-stock policy, default tax percent, and receipt/footer text.
- Keep access typed in the service even if storage is key/value.

## Ticket 2 - Wire settings into stock and sale behavior

Goal: make operational rules driven by persisted settings instead of constants.

Primary file targets:
- `src/POS.Infrastructure/Services/SaleService.cs`
- `src/POS.Wpf/Converters/LowStockToVisibilityConverter.cs`
- `src/POS.Wpf/ViewModels/MainViewModel.cs`
- `src/POS.Wpf/ViewModels/ProductManagementViewModel.cs`
- `src/POS.Wpf/Windows/PriceCheckWindow.xaml.cs`

Acceptance notes:
- No-negative-stock behavior must be configurable.
- Low-stock warnings must use the stored threshold instead of the current default `5`.
- New/open invoices should use the configured default tax where appropriate.

## Ticket 3 - Expand admin settings UI beyond currency

Goal: keep the existing currency window and add a real general-settings surface for admins.

Primary file targets:
- `src/POS.Wpf/ViewModels/MainViewModel.cs`
- `src/POS.Wpf/Windows/CurrencySettingsWindow.xaml`
- `src/POS.Wpf/Windows/CurrencySettingsWindow.xaml.cs`
- `src/POS.Wpf/ViewModels/CurrencySettingsViewModel.cs`
- `src/POS.Wpf/Localization/AppStrings.resx`
- `src/POS.Wpf/Localization/AppStrings.ar.resx`
- `src/POS.Wpf/Localization/AppStrings.he.resx`

Implementation direction:
- Either convert the current currency window into a multi-section settings window, or open a broader settings shell that contains the existing currency policy editor.
- Do not remove the existing currency-management behavior.

Acceptance notes:
- Admin can edit and save the settings introduced in Ticket 1.
- Existing currency policy flow continues to work.

## Ticket 4 - Add minimal audit log model and service

Goal: record who changed what for critical business actions.

Primary file targets:
- `src/POS.Core/Entities/AuditLog.cs`
- `src/POS.Infrastructure/Data/PosDbContext.cs`
- `src/POS.Infrastructure/Data/Configurations/AuditLogConfiguration.cs`
- `src/POS.Infrastructure/Data/Migrations/<new migration>.cs`
- `src/POS.Application/Abstractions/IAuditLogService.cs`
- `src/POS.Application/Models/AuditLogDto.cs`
- `src/POS.Infrastructure/Services/AuditLogService.cs`
- `src/POS.Infrastructure/DependencyInjection.cs`

Minimum fields:
- `UserId`
- `StoreId`
- `Action`
- `EntityName`
- `EntityId`
- `Details`
- `CreatedAt`

## Ticket 5 - Audit critical write paths

Goal: write audit entries from the core write-side services.

Primary file targets:
- `src/POS.Infrastructure/Services/CurrencyService.cs`
- `src/POS.Infrastructure/Services/ProductCatalogService.cs`
- `src/POS.Infrastructure/Services/SaleService.cs`

Actions to audit first:
- Currency policy changes
- Product create
- Product update
- Product delete
- Manual stock adjustments
- Complete sale
- Refund invoice
- Cancel invoice
- Hold invoice
- Resume invoice

Acceptance notes:
- Audit logging should not silently corrupt or block the business transaction.
- Details should be concise and readable in an admin view.

## Ticket 6 - Add audit viewer for admins

Goal: make the minimal audit trail visible in the desktop app.

Primary file targets:
- `src/POS.Wpf/ViewModels/MainViewModel.cs`
- `src/POS.Wpf/Windows/ReportsWindow.xaml` or a new `src/POS.Wpf/Windows/AuditLogWindow.xaml`
- `src/POS.Wpf/Windows/ReportsWindow.xaml.cs` or new audit window code-behind
- `src/POS.Wpf/ViewModels/<new audit view model>.cs`
- `src/POS.Wpf/Localization/AppStrings.resx`
- `src/POS.Wpf/Localization/AppStrings.ar.resx`
- `src/POS.Wpf/Localization/AppStrings.he.resx`

Acceptance notes:
- Filter by date range.
- Filter by action and entity.
- Show actor, timestamp, entity, and details.

## Ticket 7 - Add focused automated tests

Goal: cover the new Stage 3 settings and audit slices with narrow tests.

Primary file targets:
- `tests/POS.Tests/SettingsServiceTests.cs`
- `tests/POS.Tests/SaleServiceSettingsTests.cs`
- `tests/POS.Tests/AuditLogTests.cs`

Minimum cases:
- Settings round-trip load/save.
- Negative-stock policy on and off.
- Low-stock threshold behavior.
- Audit rows written for currency changes, product edits, and refunds.

## Ticket 8 - Close Stage 3 docs

Goal: update planning docs only after Tickets 1 through 6 are complete.

Primary file targets:
- `STATUS.md`
- `ROADMAP.md`
- `README.md`

Acceptance notes:
- Mark 3.5 complete only when general persisted settings exist, not just currency policy.
- Mark 3.6 complete only when audit capture and audit viewing both exist.
- Fix the README login text so it matches the current username-only flow.

## Optional Ticket 9 - Promote Stage 3.3 if release needs it

Goal: add advanced pricing, barcode variants, or units only if the release actually requires them.

Likely file targets:
- `src/POS.Core/Entities/Product.cs`
- `src/POS.Application/Models/`
- `src/POS.Infrastructure/Data/`
- `src/POS.Infrastructure/Services/ProductCatalogService.cs`
- `src/POS.Wpf/Windows/ProductEditWindow.xaml`
- `src/POS.Wpf/ViewModels/ProductManagementViewModel.cs`

Decision rule:
- Skip this ticket for the fastest path to Stage 4 unless there is a real business requirement.