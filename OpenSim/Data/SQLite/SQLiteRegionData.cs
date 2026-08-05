/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using RegionFlags = OpenSim.Framework.RegionFlags;

namespace OpenSim.Data.SQLite
{
    /// <summary>
    /// A SQLite Interface for the Region Server (grid-wide region registry).
    /// </summary>
    public class SQLiteRegionData : IRegionData
    {
        private string m_Realm;
        private SQLiteConnection m_conn;
        private List<string> m_ColumnNames = null;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteRegionData(string connectionString, string realm)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            m_Realm = realm;

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:GridStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "GridStore");
            m.Update();
        }

        public List<RegionData> Get(string regionName, UUID scopeID)
        {
            string sql = "select * from " + m_Realm + " where regionName like :regionName ";
            if (!scopeID.IsZero())
                sql += " and ScopeID = :scopeID";
            sql += " order by regionName";

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":regionName", regionName));
                    if (!scopeID.IsZero())
                        cmd.Parameters.Add(new SQLiteParameter(":scopeID", scopeID.ToString()));

                    return RunCommand(cmd);
                }
            }
        }

        public RegionData GetSpecific(string regionName, UUID scopeID)
        {
            string sql = "select * from " + m_Realm + " where regionName = :regionName ";
            if (!scopeID.IsZero())
                sql += " and ScopeID = :scopeID";

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":regionName", regionName));
                    if (!scopeID.IsZero())
                        cmd.Parameters.Add(new SQLiteParameter(":scopeID", scopeID.ToString()));

                    List<RegionData> ret = RunCommand(cmd);
                    if (ret.Count == 0)
                        return null;

                    return ret[0];
                }
            }
        }

        public RegionData Get(int posX, int posY, UUID scopeID)
        {
            // extend database search for maximum region size area
            string sql = "select * from " + m_Realm + " where locX between :startX and :endX and locY between :startY and :endY";
            if (!scopeID.IsZero())
                sql += " and ScopeID = :scopeID";

            int startX = posX - (int)Constants.MaximumRegionSize;
            int startY = posY - (int)Constants.MaximumRegionSize;

            List<RegionData> ret;
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":startX", startX));
                    cmd.Parameters.Add(new SQLiteParameter(":startY", startY));
                    cmd.Parameters.Add(new SQLiteParameter(":endX", posX));
                    cmd.Parameters.Add(new SQLiteParameter(":endY", posY));
                    if (!scopeID.IsZero())
                        cmd.Parameters.Add(new SQLiteParameter(":scopeID", scopeID.ToString()));

                    ret = RunCommand(cmd);
                }
            }

            if (ret.Count == 0)
                return null;

            // Find the first that contains pos
            foreach (RegionData r in ret)
            {
                if (posX >= r.posX && posX < r.posX + r.sizeX
                    && posY >= r.posY && posY < r.posY + r.sizeY)
                    return r;
            }

            return null;
        }

        public RegionData Get(UUID regionID, UUID scopeID)
        {
            string sql = "select * from " + m_Realm + " where uuid = :regionID";
            if (!scopeID.IsZero())
                sql += " and ScopeID = :scopeID";

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":regionID", regionID.ToString()));
                    if (!scopeID.IsZero())
                        cmd.Parameters.Add(new SQLiteParameter(":scopeID", scopeID.ToString()));

                    List<RegionData> ret = RunCommand(cmd);
                    if (ret.Count == 0)
                        return null;

                    return ret[0];
                }
            }
        }

        public List<RegionData> Get(int startX, int startY, int endX, int endY, UUID scopeID)
        {
            // extend database search for maximum region size area
            string sql = "select * from " + m_Realm + " where locX between :startX and :endX and locY between :startY and :endY";
            if (!scopeID.IsZero())
                sql += " and ScopeID = :scopeID";

            int qstartX = startX - (int)Constants.MaximumRegionSize;
            int qstartY = startY - (int)Constants.MaximumRegionSize;

            List<RegionData> dbret;
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":startX", qstartX));
                    cmd.Parameters.Add(new SQLiteParameter(":startY", qstartY));
                    cmd.Parameters.Add(new SQLiteParameter(":endX", endX));
                    cmd.Parameters.Add(new SQLiteParameter(":endY", endY));
                    if (!scopeID.IsZero())
                        cmd.Parameters.Add(new SQLiteParameter(":scopeID", scopeID.ToString()));

                    dbret = RunCommand(cmd);
                }
            }

            List<RegionData> ret = new List<RegionData>();
            foreach (RegionData r in dbret)
            {
                if (r.posX + r.sizeX > startX && r.posX <= endX
                    && r.posY + r.sizeY > startY && r.posY <= endY)
                    ret.Add(r);
            }

            return ret;
        }

        private List<RegionData> RunCommand(SQLiteCommand cmd)
        {
            List<RegionData> retList = new List<RegionData>();

            using (IDataReader result = cmd.ExecuteReader())
            {
                while (result.Read())
                {
                    RegionData ret = new RegionData();
                    ret.Data = new Dictionary<string, object>();

                    UUID.TryParse(result["uuid"].ToString(), out UUID regionID);
                    ret.RegionID = regionID;
                    UUID.TryParse(result["ScopeID"].ToString(), out UUID scope);
                    ret.ScopeID = scope;
                    ret.RegionName = result["regionName"].ToString();
                    ret.posX = Convert.ToInt32(result["locX"]);
                    ret.posY = Convert.ToInt32(result["locY"]);
                    ret.sizeX = Convert.ToInt32(result["sizeX"]);
                    ret.sizeY = Convert.ToInt32(result["sizeY"]);

                    if (m_ColumnNames == null)
                    {
                        m_ColumnNames = new List<string>();
                        DataTable schemaTable = result.GetSchemaTable();
                        foreach (DataRow row in schemaTable.Rows)
                            m_ColumnNames.Add(row["ColumnName"].ToString());
                    }

                    foreach (string s in m_ColumnNames)
                    {
                        if (s == "uuid" || s == "ScopeID" || s == "regionName" || s == "locX" || s == "locY" || s == "sizeX" || s == "sizeY")
                            continue;

                        ret.Data[s] = result[s].ToString();
                    }

                    retList.Add(ret);
                }
            }

            return retList;
        }

        public bool Store(RegionData data)
        {
            data.Data.Remove("uuid");
            data.Data.Remove("ScopeID");
            data.Data.Remove("regionName");
            data.Data.Remove("posX");
            data.Data.Remove("posY");
            data.Data.Remove("sizeX");
            data.Data.Remove("sizeY");
            data.Data.Remove("locX");
            data.Data.Remove("locY");

            string[] fields = new List<string>(data.Data.Keys).ToArray();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand())
                {
                    List<string> names = new List<string> { "uuid", "ScopeID", "locX", "locY", "sizeX", "sizeY", "regionName" };
                    List<string> values = new List<string> { ":regionID", ":scopeID", ":posX", ":posY", ":sizeX", ":sizeY", ":regionName" };

                    cmd.Parameters.Add(new SQLiteParameter(":regionID", data.RegionID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":scopeID", data.ScopeID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":posX", data.posX));
                    cmd.Parameters.Add(new SQLiteParameter(":posY", data.posY));
                    cmd.Parameters.Add(new SQLiteParameter(":sizeX", data.sizeX));
                    cmd.Parameters.Add(new SQLiteParameter(":sizeY", data.sizeY));
                    cmd.Parameters.Add(new SQLiteParameter(":regionName", data.RegionName));

                    foreach (string field in fields)
                    {
                        names.Add(field);
                        values.Add(":" + field);
                        cmd.Parameters.Add(new SQLiteParameter(":" + field, data.Data[field]));
                    }

                    cmd.CommandText = "replace into " + m_Realm + " (`" +
                            String.Join("`,`", names.ToArray()) +
                            "`) values (" + String.Join(",", values.ToArray()) + ")";
                    cmd.Connection = m_conn;

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool SetDataItem(UUID regionID, string item, string value)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand())
                {
                    cmd.CommandText = "update " + m_Realm + " set `" + item + "` = :" + item + " where uuid = :regionID";
                    cmd.Parameters.Add(new SQLiteParameter(":" + item, value));
                    cmd.Parameters.Add(new SQLiteParameter(":regionID", regionID.ToString()));
                    cmd.Connection = m_conn;

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool Delete(UUID regionID)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("delete from " + m_Realm + " where uuid = :regionID", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":regionID", regionID.ToString()));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public List<RegionData> GetDefaultRegions(UUID scopeID)
        {
            return GetByFlag((int)RegionFlags.DefaultRegion, scopeID);
        }

        public List<RegionData> GetDefaultHypergridRegions(UUID scopeID)
        {
            return GetByFlag((int)RegionFlags.DefaultHGRegion, scopeID);
        }

        public List<RegionData> GetFallbackRegions(UUID scopeID)
        {
            return GetByFlag((int)RegionFlags.FallbackRegion, scopeID);
        }

        public List<RegionData> GetHyperlinks(UUID scopeID)
        {
            return GetByFlag((int)RegionFlags.Hyperlink, scopeID);
        }

        public List<RegionData> GetOnlineRegions(UUID scopeID)
        {
            return GetByFlag((int)RegionFlags.RegionOnline, scopeID);
        }

        private List<RegionData> GetByFlag(int regionFlags, UUID scopeID)
        {
            string sql = "select * from " + m_Realm + " where (flags & " + regionFlags.ToString() + ") <> 0";
            if (!scopeID.IsZero())
                sql += " and ScopeID = :scopeID";

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    if (!scopeID.IsZero())
                        cmd.Parameters.Add(new SQLiteParameter(":scopeID", scopeID.ToString()));

                    return RunCommand(cmd);
                }
            }
        }
    }
}
