using System.Collections.Generic;

namespace OpenSim.Data
{
    public interface IAccessControlData
    {
        bool IsHardwareBanned(string mac, string id0);
        bool IsIPBanned(string ip);

        bool BanIPAddress(string ip);
        bool UnbanIPAddress(string ip);

        bool BanMacAddress(string mac);
        bool UnbanMacAddress(string mac);

        bool BanID0(string id0);
        bool UnbanID0(string id0);

        // WhiteCore-Dev-inspired (see PROJECT_LOG.md Batch 14) - a range ban
        // list, distinct from the single-IP exact-match banned_ips table
        // above. Kept as a small in-memory-checked list (fetch all, compare
        // in C#) rather than a SQL range query, since operators only ever
        // have a handful of these and it avoids needing numeric IP columns
        // with different unsigned-int handling across MySQL/PGSQL/SQLite.
        List<(string StartIP, string EndIP)> GetIPRangeBans();
        bool BanIPRange(string startIp, string endIp);
        bool UnbanIPRange(string startIp, string endIp);
    }
}
