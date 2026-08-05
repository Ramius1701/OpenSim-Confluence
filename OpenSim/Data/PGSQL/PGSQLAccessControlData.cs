using System.Reflection;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLAccessControlData : IAccessControlData
    {
        private string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLAccessControlData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "AccessControl");
                m.Update();
            }
        }

        public bool IsHardwareBanned(string mac, string id0)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "select mac as id from banned_macs where mac = :mac union all select id0 as id from banned_id0s where id0 = :id0 limit 1", conn))
            {
                cmd.Parameters.AddWithValue(":mac", mac);
                cmd.Parameters.AddWithValue(":id0", id0);
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                    return result.Read();
            }
        }

        public bool IsIPBanned(string ip)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("select * from banned_ips where ip = :ip limit 1", conn))
            {
                cmd.Parameters.AddWithValue(":ip", ip);
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                    return result.Read();
            }
        }

        public bool BanIPAddress(string ip)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("insert into banned_ips (ip) values (:ip) on conflict do nothing", conn))
            {
                cmd.Parameters.AddWithValue(":ip", ip);
                conn.Open();
                cmd.ExecuteNonQuery();
                return true;
            }
        }

        public bool UnbanIPAddress(string ip)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("delete from banned_ips where ip = :ip", conn))
            {
                cmd.Parameters.AddWithValue(":ip", ip);
                conn.Open();
                cmd.ExecuteNonQuery();
                return true;
            }
        }

        public bool BanMacAddress(string mac)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("insert into banned_macs (mac) values (:mac) on conflict do nothing", conn))
            {
                cmd.Parameters.AddWithValue(":mac", mac);
                conn.Open();
                cmd.ExecuteNonQuery();
                return true;
            }
        }

        public bool UnbanMacAddress(string mac)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("delete from banned_macs where mac = :mac", conn))
            {
                cmd.Parameters.AddWithValue(":mac", mac);
                conn.Open();
                cmd.ExecuteNonQuery();
                return true;
            }
        }

        public bool BanID0(string id0)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("insert into banned_id0s (id0) values (:id0) on conflict do nothing", conn))
            {
                cmd.Parameters.AddWithValue(":id0", id0);
                conn.Open();
                cmd.ExecuteNonQuery();
                return true;
            }
        }

        public bool UnbanID0(string id0)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("delete from banned_id0s where id0 = :id0", conn))
            {
                cmd.Parameters.AddWithValue(":id0", id0);
                conn.Open();
                cmd.ExecuteNonQuery();
                return true;
            }
        }
    }
}
