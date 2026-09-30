using System.Collections.Generic;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the admin accountability trail - see
    // OpenSim.Framework.AdminAuditEntry for the design rationale.
    // Deliberately append-only: no Update, no Delete. An audit log that
    // can be edited or removed after the fact isn't a real one.
    public interface IAdminAuditData
    {
        bool LogAction(AdminAuditEntry entry);

        List<AdminAuditEntry> GetRecent(int count);
    }
}
