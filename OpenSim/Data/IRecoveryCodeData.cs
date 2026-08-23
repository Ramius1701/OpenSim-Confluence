using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the WebInterface's account-recovery codes - see
    // OpenSim.Framework.RecoveryCode for the design rationale.
    public interface IRecoveryCodeData
    {
        List<RecoveryCode> GetByPrincipal(UUID principalID);
        bool Store(RecoveryCode code);
        bool DeleteAllForPrincipal(UUID principalID);
        bool MarkUsed(UUID id);
    }
}
