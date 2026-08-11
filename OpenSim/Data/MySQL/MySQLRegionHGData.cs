using System.Reflection;
using System.Data;
using MySql.Data.MySqlClient;
using OpenMetaverse;

namespace OpenSim.Data.MySQL
{
    public class MySqlRegionHGData : MySqlFramework, IRegionHGData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlRegionHGData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "RegionHG");
                m.Update();
                dbcon.Close();
            }
        }

        public bool? GetIsOpen(UUID regionID)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select `IsOpen` from `region_hg_settings` where `RegionID` = ?RegionID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?RegionID", regionID.ToString());

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        if (result.Read())
                            return result.GetInt32(0) != 0;
                    }
                }
            }

            return null;
        }

        public void SetIsOpen(UUID regionID, bool open)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "insert into `region_hg_settings` (`RegionID`, `IsOpen`) values (?RegionID, ?IsOpen) "
                    + "on duplicate key update `IsOpen` = ?IsOpen", dbcon))
                {
                    cmd.Parameters.AddWithValue("?RegionID", regionID.ToString());
                    cmd.Parameters.AddWithValue("?IsOpen", open ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
