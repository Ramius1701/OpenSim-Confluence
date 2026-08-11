using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.RegionHGService
{
    public class RegionHGService : RegionHGServiceBase, IRegionHGService
    {
        public RegionHGService(IConfigSource config)
            : base(config)
        {
        }

        public bool IsRegionOpen(UUID regionID)
        {
            bool? stored = m_Database.GetIsOpen(regionID);
            return stored ?? true;
        }

        public void SetRegionOpen(UUID regionID, bool open)
        {
            m_Database.SetIsOpen(regionID, open);
        }
    }
}
