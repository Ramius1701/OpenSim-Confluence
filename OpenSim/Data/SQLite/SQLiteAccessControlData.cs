using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteAccessControlData : IAccessControlData
    {
        private SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteAccessControlData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:AccessControl.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "AccessControl");
            m.Update();
        }

        public bool IsHardwareBanned(string mac, string id0)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "select mac as id from banned_macs where mac = :mac union all select id0 as id from banned_id0s where id0 = :id0 limit 1", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":mac", mac));
                    cmd.Parameters.Add(new SQLiteParameter(":id0", id0));

                    using (IDataReader result = cmd.ExecuteReader())
                        return result.Read();
                }
            }
        }

        public bool IsIPBanned(string ip)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("select * from banned_ips where ip = :ip limit 1", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":ip", ip));

                    using (IDataReader result = cmd.ExecuteReader())
                        return result.Read();
                }
            }
        }

        public bool BanIPAddress(string ip)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("insert or ignore into banned_ips (ip) values (:ip)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":ip", ip));
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }

        public bool UnbanIPAddress(string ip)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("delete from banned_ips where ip = :ip", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":ip", ip));
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }

        public bool BanMacAddress(string mac)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("insert or ignore into banned_macs (mac) values (:mac)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":mac", mac));
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }

        public bool UnbanMacAddress(string mac)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("delete from banned_macs where mac = :mac", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":mac", mac));
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }

        public bool BanID0(string id0)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("insert or ignore into banned_id0s (id0) values (:id0)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id0", id0));
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }

        public bool UnbanID0(string id0)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("delete from banned_id0s where id0 = :id0", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id0", id0));
                    cmd.ExecuteNonQuery();
                    return true;
                }
            }
        }
    }
}
