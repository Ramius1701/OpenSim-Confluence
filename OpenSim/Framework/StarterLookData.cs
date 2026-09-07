using OpenMetaverse;

namespace OpenSim.Framework
{
    // Backing row for the WebUI /register starter-look carousel - each row
    // points at an ordinary UserAccount ("model") an admin has dressed up
    // in-world; ApplyStarterLook (WebInterfaceServiceConnector) clones that
    // account's appearance onto a freshly registered one. See ROADMAP.md's
    // "Avatar-selection starter-look carousel" entry for the design.
    public class StarterLookData
    {
        public int LookID; // Handled by SQL
        public string Name = string.Empty;
        public UUID ModelAccountID = UUID.Zero;
        public int SortOrder = 0;
        public bool Enabled = true;
    }
}
