using System.Collections.Generic;
using System.Reflection;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLGridSettingsData : IGridSettingsData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLGridSettingsData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "GridSettings");
                m.Update();
            }
        }

        public string Get(string key)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"SettingValue\" FROM grid_settings WHERE \"SettingKey\" = :key", conn))
            {
                cmd.Parameters.AddWithValue(":key", key);
                conn.Open();

                object result = cmd.ExecuteScalar();
                return result == null ? null : result.ToString();
            }
        }

        public Dictionary<string, string> GetAll()
        {
            Dictionary<string, string> results = new Dictionary<string, string>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT \"SettingKey\", \"SettingValue\" FROM grid_settings", conn))
            {
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        results[reader.GetString(0)] = reader.GetString(1);
                }
            }

            return results;
        }

        public bool Set(string key, string value)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO grid_settings (\"SettingKey\", \"SettingValue\") VALUES (:key, :value) " +
                    "ON CONFLICT (\"SettingKey\") DO UPDATE SET \"SettingValue\" = :value", conn))
            {
                cmd.Parameters.AddWithValue(":key", key);
                cmd.Parameters.AddWithValue(":value", value ?? string.Empty);
                conn.Open();

                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }
}
