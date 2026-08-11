using OpenMetaverse;

namespace OpenSim.Services.Interfaces
{
    // Per-region Hypergrid open/close toggle - see PROJECT_LOG.md, "Grid
    // management verification" (this was the long-standing "In progress" item
    // that predates the whole Batch 12/13 architecture thread). GatekeeperService
    // is a grid-wide singleton with only an all-or-nothing ForeignAgentsAllowed
    // setting; this adds the per-destination-region check it never had. Absence
    // of a row for a region means open (matches the ForeignAgentsAllowed=true
    // default), so existing regions aren't silently closed by upgrading.
    public interface IRegionHGService
    {
        bool IsRegionOpen(UUID regionID);
        void SetRegionOpen(UUID regionID, bool open);
    }
}
