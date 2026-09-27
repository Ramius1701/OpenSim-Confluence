using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;

namespace OpenSim.Data.SQLite
{
    // SQLite backend for FSAssets, equivalent to PGSQLFSAssetData (metadata only - name/description
    // are never displayed for an FSAsset, so unlike the legacy MySQL table they are not stored here;
    // the asset's actual bytes live on disk in a file named after "hash", handled by FSAssetService
    // itself, not this class). One connection, every call serialized under a lock, matching this
    // project's other SQLite backends (SQLite is single-writer anyway).
    public class SQLiteFSAssetData : IFSAssetDataPlugin
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private SQLiteConnection m_conn;
        private string m_Table = "fsassets";
        private int m_DaysBetweenAccessTimeUpdates;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteFSAssetData()
        {
        }

        #region IPlugin Members

        public string Version { get { return "1.0.0.0"; } }

        public string Name { get { return "SQLite FSAsset storage engine"; } }

        public void Dispose() { }

        #endregion

        public void Initialise(string connect, string realm, int UpdateAccessTime)
        {
            m_DaysBetweenAccessTimeUpdates = UpdateAccessTime;
            if (!string.IsNullOrEmpty(realm))
                m_Table = realm;

            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connect))
                connect = "URI=file:FSAssetStore.db";

            m_conn = new SQLiteConnection(connect);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "FSAssetStore");
            m.Update();
        }

        public void Initialise()
        {
            throw new NotImplementedException();
        }

        #region IFSAssetDataPlugin Members

        public AssetMetadata Get(string id, out string hash)
        {
            hash = String.Empty;
            AssetMetadata meta = null;

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "select id, type, hash, create_time, access_time, asset_flags from " + m_Table + " where id = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            meta = new AssetMetadata();
                            hash = reader["hash"].ToString();
                            meta.ID = id;
                            meta.FullID = new UUID(id);
                            meta.Name = String.Empty;
                            meta.Description = String.Empty;
                            meta.Type = (sbyte)Convert.ToInt32(reader["type"]);
                            meta.ContentType = SLUtil.SLAssetTypeToContentType(meta.Type);
                            meta.CreationDate = Util.ToDateTime(Convert.ToInt32(reader["create_time"]));
                            meta.Flags = (AssetFlags)Convert.ToInt32(reader["asset_flags"]);

                            int accessTime = Convert.ToInt32(reader["access_time"]);
                            UpdateAccessTimeLocked(id, accessTime);
                        }
                    }
                }
            }

            return meta;
        }

        // Called only while already holding the lock (from Get, itself locked).
        private void UpdateAccessTimeLocked(string id, int accessTime)
        {
            // Reduce DB work by only updating access time if the asset hasn't recently been
            // accessed. 0 by default; config option is "DaysBetweenAccessTimeUpdates".
            if (m_DaysBetweenAccessTimeUpdates > 0
                && (DateTime.UtcNow - Utils.UnixTimeToDateTime(accessTime)).TotalDays < m_DaysBetweenAccessTimeUpdates)
                return;

            using (SQLiteCommand cmd = new SQLiteCommand(
                "update " + m_Table + " set access_time = :access_time where id = :id", m_conn))
            {
                cmd.Parameters.Add(new SQLiteParameter(":id", id));
                cmd.Parameters.Add(new SQLiteParameter(":access_time", Util.UnixTimeSinceEpoch()));
                cmd.ExecuteNonQuery();
            }
        }

        public bool Store(AssetMetadata meta, string hash)
        {
            try
            {
                lock (this)
                {
                    string oldHash;
                    bool existed;
                    using (SQLiteCommand check = new SQLiteCommand(
                        "select hash from " + m_Table + " where id = :id", m_conn))
                    {
                        check.Parameters.Add(new SQLiteParameter(":id", meta.ID));
                        using (IDataReader reader = check.ExecuteReader())
                        {
                            existed = reader.Read();
                            oldHash = existed ? reader["hash"].ToString() : String.Empty;
                        }
                    }

                    int now = Util.UnixTimeSinceEpoch();

                    if (!existed)
                    {
                        using (SQLiteCommand cmd = new SQLiteCommand(
                            "insert into " + m_Table
                            + " (id, type, hash, asset_flags, create_time, access_time) values"
                            + " (:id, :type, :hash, :asset_flags, :create_time, :access_time)", m_conn))
                        {
                            cmd.Parameters.Add(new SQLiteParameter(":id", meta.ID));
                            cmd.Parameters.Add(new SQLiteParameter(":type", (int)meta.Type));
                            cmd.Parameters.Add(new SQLiteParameter(":hash", hash));
                            cmd.Parameters.Add(new SQLiteParameter(":asset_flags", (int)meta.Flags));
                            cmd.Parameters.Add(new SQLiteParameter(":create_time", now));
                            cmd.Parameters.Add(new SQLiteParameter(":access_time", now));
                            cmd.ExecuteNonQuery();
                        }
                        return true;
                    }

                    // The asset already exists: assume it was correctly stored, except a row that
                    // holds no data at all, which is never correct (see FSAssetHashes.Empty). A
                    // later store of real data for the same ID (a good re-import, a fixed upload)
                    // replaces it.
                    if (oldHash == FSAssetHashes.Empty && hash != FSAssetHashes.Empty)
                    {
                        using (SQLiteCommand heal = new SQLiteCommand(
                            "update " + m_Table + " set hash = :hash, access_time = :access_time where id = :id", m_conn))
                        {
                            heal.Parameters.Add(new SQLiteParameter(":id", meta.ID));
                            heal.Parameters.Add(new SQLiteParameter(":hash", hash));
                            heal.Parameters.Add(new SQLiteParameter(":access_time", now));
                            heal.ExecuteNonQuery();
                        }
                        m_log.InfoFormat("[FSASSETS]: Replaced empty data for asset {0}", meta.ID);
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.Error("[FSASSETS]: Failed to store asset with ID " + meta.ID);
                m_log.Error(e.ToString());
                return false;
            }
        }

        public bool[] AssetsExist(UUID[] uuids)
        {
            if (uuids.Length == 0)
                return Array.Empty<bool>();

            HashSet<UUID> exists = new HashSet<UUID>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(m_conn))
                {
                    string[] placeholders = new string[uuids.Length];
                    for (int i = 0; i < uuids.Length; i++)
                    {
                        string p = ":id" + i;
                        placeholders[i] = p;
                        cmd.Parameters.Add(new SQLiteParameter(p, uuids[i].ToString()));
                    }
                    cmd.CommandText = "select id from " + m_Table + " where id in (" + String.Join(",", placeholders) + ")";

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            exists.Add(new UUID(reader["id"].ToString()));
                    }
                }
            }

            bool[] results = new bool[uuids.Length];
            for (int i = 0; i < uuids.Length; i++)
                results[i] = exists.Contains(uuids[i]);
            return results;
        }

        public int Count()
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("select count(*) from " + m_Table, m_conn))
                {
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public bool Delete(string id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("delete from " + m_Table + " where id = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", id));
                    cmd.ExecuteNonQuery();
                }
            }

            return true;
        }

        public void Import(string conn, string table, int start, int count, bool force, FSStoreDelegate store)
        {
            int imported = 0;
            string limit = String.Empty;
            if (count != -1)
                limit = String.Format(" limit {0} offset {1}", count, start);

            try
            {
                using (SQLiteConnection remote = new SQLiteConnection(conn))
                {
                    remote.Open();
                    using (SQLiteCommand cmd = new SQLiteCommand("select * from " + table + limit, remote))
                    {
                        MainConsole.Instance.Output("Querying database");
                        MainConsole.Instance.Output("Reading data");
                        using (IDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                if ((imported % 100) == 0)
                                    MainConsole.Instance.Output(String.Format("{0} assets imported so far", imported));

                                AssetBase asset = new AssetBase();
                                AssetMetadata meta = new AssetMetadata();

                                meta.ID = reader["id"].ToString();
                                meta.FullID = new UUID(meta.ID);
                                meta.Name = String.Empty;
                                meta.Description = String.Empty;
                                meta.Type = (sbyte)Convert.ToInt32(reader["assetType"]);
                                meta.ContentType = SLUtil.SLAssetTypeToContentType(meta.Type);
                                meta.CreationDate = Util.ToDateTime(Convert.ToInt32(reader["create_time"]));

                                asset.Metadata = meta;
                                asset.Data = (byte[])reader["data"];

                                store(asset, force);

                                imported++;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[FSASSETS]: Error importing assets: {0}", e.Message);
                return;
            }

            MainConsole.Instance.Output(String.Format("Import done, {0} assets imported", imported));
        }

        #endregion
    }
}
