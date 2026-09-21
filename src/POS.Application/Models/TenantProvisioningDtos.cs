namespace POS.Application.Models;

public sealed record TenantProvisioningResult(Guid TenantId, Guid StoreId, Guid AdminUserId, Guid AdminRoleId);
