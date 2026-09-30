using System.Collections.Generic;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Backing service for the admin accountability trail (WebUI
    // /admin/audit-log). See OpenSim.Framework.AdminAuditEntry for the
    // design rationale - a real, purpose-built, structured log distinct
    // from the pre-existing resident-facing web_activity_log.
    public interface IAdminAuditService
    {
        // Assigns ID/Created and stores the entry - callers only need to
        // fill in the actor/action/target/value fields, same convention
        // IWebAccountService.LogActivity already uses.
        bool LogAction(AdminAuditEntry entry);

        List<AdminAuditEntry> GetRecent(int count);
    }
}
