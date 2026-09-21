using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using POS.Application.Abstractions;
using POS.Web.Infrastructure;
using POS.Web.Models;

namespace POS.Web.Controllers;

[Authorize(Policy = WebAuthorizationPolicies.ViewAudit)]
public sealed class AuditController : Controller
{
    private readonly IAuditLogService _auditLogService;

    public AuditController(IAuditLogService auditLogService)
    {
        _auditLogService = auditLogService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(DateOnly? fromDate, DateOnly? toDate, string? actionName, string? entityName, CancellationToken cancellationToken)
    {
        // Named "actionName", not "action" — a parameter literally named "action" binds from the
        // ambient route value (the MVC action name, "Index") instead of the query string.
        var fromUtc = fromDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var toUtc = toDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).AddDays(1).ToUniversalTime();

        var entries = await _auditLogService.GetRecentAsync(fromUtc, toUtc, actionName, entityName, cancellationToken: cancellationToken);

        var model = new AuditViewModel
        {
            FromDate = fromDate,
            ToDate = toDate,
            Action = actionName,
            EntityName = entityName,
            Entries = entries
                .Select(entry => new AuditEntryRowViewModel
                {
                    CreatedAtLocal = entry.CreatedAt.ToLocalTime(),
                    Username = entry.Username,
                    Action = entry.Action,
                    EntityName = entry.EntityName,
                    EntityId = entry.EntityId,
                    Details = entry.Details
                })
                .ToList()
        };

        return View(model);
    }
}
