using System;
using System.Reflection;
using Npgsql;
using OpenMetaverse;

namespace OpenSim.Data.PGSQL
{
    // PostgreSQL backend for RegionHGService, equivalent to MySQLRegionHGData.
    public class PGSQLRegionHGData : IRegionHGData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLRegionHGData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "RegionHG");
                m.Update();
            }
        }

        // Null means no row exists yet for this region - the caller treats that as "open".
        public bool? GetIsOpen(UUID regionID)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "SELECT \"IsOpen\" FROM region_hg_settings WHERE \"RegionID\" = :RegionID", conn))
            {
                cmd.Parameters.AddWithValue(":RegionID", regionID.ToString());
                conn.Open();

                using (NpgsqlDataReader result = cmd.ExecuteReader())
                {
                    if (result.Read())
                        return Convert.ToInt32(result[0]) != 0;
                }
            }

            return null;
        }

        public void SetIsOpen(UUID regionID, bool open)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                "INSERT INTO region_hg_settings (\"RegionID\", \"IsOpen\") VALUES (:RegionID, :IsOpen) "
                + "ON CONFLICT (\"RegionID\") DO UPDATE SET \"IsOpen\" = :IsOpen", conn))
            {
                cmd.Parameters.AddWithValue(":RegionID", regionID.ToString());
                cmd.Parameters.AddWithValue(":IsOpen", open ? 1 : 0);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }
    }
}
