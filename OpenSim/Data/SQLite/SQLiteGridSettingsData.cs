using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteGridSettingsData : IGridSettingsData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteGridSettingsData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "GridSettings");
            m.Update();
        }

        public string Get(string key)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT SettingValue FROM grid_settings WHERE SettingKey = :key", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":key", key));

                    object result = cmd.ExecuteScalar();
                    return result == null ? null : result.ToString();
                }
            }
        }

        public Dictionary<string, string> GetAll()
        {
            Dictionary<string, string> results = new Dictionary<string, string>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT SettingKey, SettingValue FROM grid_settings", m_conn))
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
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO grid_settings (SettingKey, SettingValue) VALUES (:key, :value)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":key", key));
                    cmd.Parameters.Add(new SQLiteParameter(":value", value ?? string.Empty));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }
    }
}
