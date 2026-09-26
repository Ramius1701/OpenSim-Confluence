using System;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;

namespace OpenSim.Data.SQLite
{
    // SQLite backend for RegionHGService, equivalent to MySQLRegionHGData.
    public class SQLiteRegionHGData : IRegionHGData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteRegionHGData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "RegionHG");
            m.Update();
        }

        // Null means no row exists yet for this region - the caller treats that as "open".
        public bool? GetIsOpen(UUID regionID)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT IsOpen FROM region_hg_settings WHERE RegionID = :RegionID", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":RegionID", regionID.ToString()));

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        if (result.Read())
                            return Convert.ToInt32(result[0]) != 0;
                    }
                }
            }

            return null;
        }

        public void SetIsOpen(UUID regionID, bool open)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "INSERT OR REPLACE INTO region_hg_settings (RegionID, IsOpen) VALUES (:RegionID, :IsOpen)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":RegionID", regionID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":IsOpen", open ? 1 : 0));
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
