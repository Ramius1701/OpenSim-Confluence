using System;
using System.Data;
using System.Data.SQLite;
using System.Text.RegularExpressions;

namespace OpenSim.Data.SQLite
{
    // SQLite backend for offline instant messages, equivalent to MySQLOfflineIMData and
    // PGSQLOfflineIMData: the generic table handler does Get / Store / Delete, this adds the
    // two members the generic SQLite handler does not have (GetCount and DeleteOld).
    public class SQLiteOfflineIMData : SQLiteGenericTableHandler<OfflineIMData>, IOfflineIMData
    {
        // Column names come from the calling service, never from a user, but they are still
        // spliced into SQL, so only plain identifiers are accepted.
        private static readonly Regex s_identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        public SQLiteOfflineIMData(string connectionString, string realm)
            : base(connectionString, realm, "IM_Store")
        {
        }

        public long GetCount(string field, string key)
        {
            if (!s_identifier.IsMatch(field))
                return 0;

            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = String.Format("select count(*) from {0} where `{1}` = :key", m_Realm, field);
                cmd.Parameters.Add(new SQLiteParameter(":key", key));

                using (IDataReader reader = ExecuteReader(cmd, m_Connection))
                {
                    return reader.Read() ? Convert.ToInt64(reader[0]) : 0;
                }
            }
        }

        // Messages older than two weeks are dropped when the service starts, as on the other backends.
        public void DeleteOld()
        {
            using (SQLiteCommand cmd = new SQLiteCommand())
            {
                cmd.CommandText = String.Format("delete from {0} where TMStamp < datetime('now', '-14 days')", m_Realm);
                ExecuteNonQuery(cmd, m_Connection);
            }
        }
    }
}
