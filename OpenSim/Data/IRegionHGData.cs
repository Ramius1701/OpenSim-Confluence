using OpenMetaverse;

namespace OpenSim.Data
{
    public interface IRegionHGData
    {
        // Null means no row exists yet for this region - caller should treat
        // that as "open" (see IRegionHGService for why).
        bool? GetIsOpen(UUID regionID);

        void SetIsOpen(UUID regionID, bool open);
    }
}
