using POS.Application.Models;

namespace POS.Application.Abstractions;

/// <summary>
/// Creates a new independent business (tenant) with its first store and administrator. This is the
/// "minimal authenticated provisioning for a tenant, initial store and first administrator" called for
/// by tenant.md T2 — deliberately not public self-service signup. Callers (e.g. an operator-only API
/// endpoint gated by a shared secret) are responsible for their own authorization; this service only
/// enforces business invariants (unique slug, valid password).
/// </summary>
public interface ITenantProvisioningService
{
    Task<(bool Success, string? Error, TenantProvisioningResult? Result)> ProvisionTenantAsync(
        string tenantName,
        string tenantSlug,
        string storeName,
        string adminUsername,
        string adminPassword,
        string baseCurrencyCode = "ILS",
        CancellationToken cancellationToken = default);
}
