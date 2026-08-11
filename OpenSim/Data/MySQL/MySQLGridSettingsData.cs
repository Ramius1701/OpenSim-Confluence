using System.Collections.Generic;
using System.Data;
using System.Reflection;
using MySql.Data.MySqlClient;

namespace OpenSim.Data.MySQL
{
    public class MySqlGridSettingsData : MySqlFramework, IGridSettingsData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlGridSettingsData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "GridSettings");
                m.Update();
                dbcon.Close();
            }
        }

        public string Get(string key)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT SettingValue FROM grid_settings WHERE SettingKey = ?SettingKey", dbcon))
                {
                    cmd.Parameters.AddWithValue("?SettingKey", key);

                    object result = cmd.ExecuteScalar();
                    return result == null ? null : result.ToString();
                }
            }
        }

        public Dictionary<string, string> GetAll()
        {
            Dictionary<string, string> results = new Dictionary<string, string>();

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand("SELECT SettingKey, SettingValue FROM grid_settings", dbcon))
                using (IDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results[reader.GetString(0)] = reader.GetString(1);
                }
            }

            return results;
        }

        public bool Set(string key, string value)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                        "REPLACE INTO grid_settings (SettingKey, SettingValue) VALUES (?SettingKey, ?SettingValue)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?SettingKey", key);
                    cmd.Parameters.AddWithValue("?SettingValue", value ?? string.Empty);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }
    }
}
