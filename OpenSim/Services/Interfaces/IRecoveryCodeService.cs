using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    public interface IRecoveryCodeService
    {
        // Deletes any existing codes for this avatar and generates 5 new
        // ones, returning them in PLAINTEXT - the only time they're ever
        // available unhashed. Callers must show these to the resident
        // once and never persist the plaintext anywhere themselves.
        List<string> RegenerateCodes(UUID principalID);

        // How many of this avatar's codes are still unused - shown next
        // to "Regenerate" so a resident knows when they're running low.
        int GetRemainingCount(UUID principalID);

        // Case/whitespace-insensitive match against this avatar's stored
        // code hashes. A matching, not-yet-used code is marked used and
        // this returns true (single-use, like OGI's own recovery codes).
        bool RedeemCode(UUID principalID, string code);
    }
}
