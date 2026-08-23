using System;
using OpenMetaverse;

namespace OpenSim.Framework
{
    // One-time backup codes for resetting an avatar's own in-world
    // password without needing a working email - real gap found auditing
    // OpenSim-Grid-Interface's own account/account.php (its 5-code
    // recovery-code system). Tied to PrincipalID (an avatar), not a
    // WebAccount - Casperia's login *is* the avatar's in-world password
    // (there's no separate portal password to recover), so this is the
    // same identity the existing email-based forgot-password flow resets,
    // just reachable without email. Each row is one code; regenerating
    // deletes every existing row for that avatar and inserts 5 fresh ones.
    public class RecoveryCode
    {
        public UUID ID = UUID.Zero;
        public UUID PrincipalID = UUID.Zero;
        public string CodeHash = string.Empty;
        public string CodeSalt = string.Empty;
        public bool Used = false;
        public DateTime Created = DateTime.UtcNow;
    }
}
