using System;
using Nini.Config;
using OpenSim.Data;
using OpenSim.Services.Base;

namespace OpenSim.Services.AccessControlService
{
    public class AccessControlServiceBase : ServiceBase
    {
        protected IAccessControlData m_Database;

        public AccessControlServiceBase(IConfigSource config) : base(config)
        {
            string dllName = String.Empty;
            string connString = String.Empty;

            //
            // Try reading the [AccessControlService] section first, if it exists
            //
            IConfig authConfig = config.Configs["AccessControlService"];
            if (authConfig != null)
            {
                dllName = authConfig.GetString("StorageProvider", dllName);
                connString = authConfig.GetString("ConnectionString", connString);
            }

            //
            // Try reading the [DatabaseService] section, if it exists
            //
            IConfig dbConfig = config.Configs["DatabaseService"];
            if (dbConfig != null)
            {
                if (dllName == String.Empty)
                    dllName = dbConfig.GetString("StorageProvider", String.Empty);
                if (connString == String.Empty)
                    connString = dbConfig.GetString("ConnectionString", String.Empty);
            }

            //
            // We tried, but this doesn't exist. We can't proceed.
            //
            if (dllName == String.Empty)
                throw new Exception("No StorageProvider configured");

            m_Database = LoadPlugin<IAccessControlData>(dllName, new Object[] {connString});
            if (m_Database == null)
                throw new Exception(string.Format("Could not find a storage interface in module {0}", dllName));
        }

        public bool IsIPBanned(string ip)
        {
            return m_Database.IsIPBanned(ip);
        }

        public bool IsHardwareBanned(string mac, string id0)
        {
            return m_Database.IsHardwareBanned(mac, id0);
        }

        // Range list is expected to be small (a handful of operator-entered
        // ranges, not a huge blocklist) - fetching all of them and comparing
        // in C# each login is simpler and fast enough, and avoids needing
        // numeric IP columns with different unsigned-int handling across
        // MySQL/PGSQL/SQLite (see IAccessControlData.GetIPRangeBans).
        public bool IsIPRangeBanned(string ip)
        {
            if (!TryIPToUInt32(ip, out uint candidate))
                return false;

            foreach ((string startIp, string endIp) in m_Database.GetIPRangeBans())
            {
                if (TryIPToUInt32(startIp, out uint start) && TryIPToUInt32(endIp, out uint end)
                        && candidate >= start && candidate <= end)
                    return true;
            }

            return false;
        }

        private static bool TryIPToUInt32(string ip, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(ip))
                return false;

            string[] octets = ip.Split('.');
            if (octets.Length != 4)
                return false;

            uint result = 0;
            foreach (string octet in octets)
            {
                if (!byte.TryParse(octet, out byte b))
                    return false;
                result = (result << 8) | b;
            }

            value = result;
            return true;
        }
    }
}
