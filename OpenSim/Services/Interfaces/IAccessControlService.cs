namespace OpenSim.Services.Interfaces
{
    public interface IAccessControlService
    {
        bool IsIPBanned(string ip);
        bool IsHardwareBanned(string mac, string id0);

        // WhiteCore-Dev-inspired range ban check (see PROJECT_LOG.md Batch 14) -
        // distinct from IsIPBanned's exact-match check.
        bool IsIPRangeBanned(string ip);
    }
}
