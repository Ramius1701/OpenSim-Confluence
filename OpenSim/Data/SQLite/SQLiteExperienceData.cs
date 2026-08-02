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

using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    public class SQLiteExperienceData : IExperienceData
    {
        private SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteExperienceData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:Experience.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Experience");
            m.Update();
        }

        public Dictionary<UUID, bool> GetExperiencePermissions(UUID agent_id)
        {
            Dictionary<UUID, bool> experiencePermissions = new Dictionary<UUID, bool>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "select experience, allow from experience_permissions where avatar = :avatar", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatar", agent_id.ToString()));

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                        {
                            string uuid = result["experience"].ToString();
                            bool allow = System.Convert.ToInt64(result["allow"]) != 0;

                            if (UUID.TryParse(uuid, out UUID experience_key))
                                experiencePermissions[experience_key] = allow;
                        }
                    }
                }
            }

            return experiencePermissions;
        }

        public bool ForgetExperiencePermissions(UUID agent_id, UUID experience_id)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "delete from experience_permissions where avatar = :avatar AND experience = :experience", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatar", agent_id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience_id.ToString()));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool SetExperiencePermissions(UUID agent_id, UUID experience_id, bool allow)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "insert or replace into experience_permissions (experience, avatar, allow) values (:experience, :avatar, :allow)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatar", agent_id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience_id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":allow", allow ? 1 : 0));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        private static ExperienceInfoData ReadExperienceInfo(IDataReader result)
        {
            ExperienceInfoData info = new ExperienceInfoData();
            info.public_id = UUID.Parse(result["public_id"].ToString());
            info.owner_id = UUID.Parse(result["owner_id"].ToString());
            info.group_id = UUID.Parse(result["group_id"].ToString());
            info.name = result["name"].ToString();
            info.description = result["description"].ToString();
            info.logo = UUID.Parse(result["logo"].ToString());
            info.marketplace = result["marketplace"].ToString();
            info.slurl = result["slurl"].ToString();
            info.maturity = int.Parse(result["maturity"].ToString());
            info.properties = int.Parse(result["properties"].ToString());
            return info;
        }

        public ExperienceInfoData[] GetExperienceInfos(UUID[] experiences)
        {
            List<ExperienceInfoData> infos = new List<ExperienceInfoData>();
            if (experiences.Length == 0)
                return infos.ToArray();

            List<string> uuids = new List<string>();
            foreach (var u in experiences)
                uuids.Add("'" + u.ToString() + "'");
            string joined = string.Join(",", uuids);

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT * FROM experiences WHERE public_id IN (" + joined + ")", m_conn))
                {
                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            infos.Add(ReadExperienceInfo(result));
                    }
                }
            }

            return infos.ToArray();
        }

        public UUID[] GetAgentExperiences(UUID agent_id)
        {
            List<UUID> experiences = new List<UUID>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT public_id FROM experiences WHERE owner_id = :avatar", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":avatar", agent_id.ToString()));
                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            experiences.Add(UUID.Parse(result["public_id"].ToString()));
                    }
                }
            }

            return experiences.ToArray();
        }

        public bool UpdateExperienceInfo(ExperienceInfoData data)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "insert or replace into experiences (public_id, owner_id, name, description, group_id, logo, marketplace, slurl, maturity, properties) " +
                    "values (:public_id, :owner_id, :name, :description, :group_id, :logo, :marketplace, :slurl, :maturity, :properties)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":public_id", data.public_id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":owner_id", data.owner_id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":name", data.name ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":description", data.description ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":group_id", data.group_id.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":logo", data.logo.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":marketplace", data.marketplace ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":slurl", data.slurl ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":maturity", data.maturity));
                    cmd.Parameters.Add(new SQLiteParameter(":properties", data.properties));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public ExperienceInfoData[] FindExperiences(string search)
        {
            List<ExperienceInfoData> experiences = new List<ExperienceInfoData>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT * FROM experiences WHERE name LIKE :search", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":search", string.Format("%{0}%", search)));

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            experiences.Add(ReadExperienceInfo(result));
                    }
                }
            }

            return experiences.ToArray();
        }

        public UUID[] GetGroupExperiences(UUID group_id)
        {
            List<UUID> experiences = new List<UUID>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT public_id FROM experiences WHERE group_id = :group", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":group", group_id.ToString()));
                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            experiences.Add(UUID.Parse(result["public_id"].ToString()));
                    }
                }
            }

            return experiences.ToArray();
        }

        public UUID[] GetExperiencesForGroups(UUID[] groups)
        {
            List<UUID> experiences = new List<UUID>();
            if (groups.Length == 0)
                return experiences.ToArray();

            List<string> uuids = new List<string>();
            foreach (var u in groups)
                uuids.Add("'" + u.ToString() + "'");
            string joined = string.Join(",", uuids);

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT public_id FROM experiences WHERE group_id IN (" + joined + ")", m_conn))
                {
                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            experiences.Add(UUID.Parse(result["public_id"].ToString()));
                    }
                }
            }

            return experiences.ToArray();
        }

        // KeyValue

        public bool SetKeyValue(UUID experience, string key, string val)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "insert or replace into experience_kv (experience, key, value) values (:experience, :key, :value)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":key", key));
                    cmd.Parameters.Add(new SQLiteParameter(":value", val));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public string GetKeyValue(UUID experience, string key)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT value FROM experience_kv WHERE experience = :experience AND key = :key LIMIT 1", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":key", key));

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        if (result.Read())
                            return result["value"].ToString();
                    }
                }
            }

            return null;
        }

        public bool DeleteKey(UUID experience, string key)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "DELETE FROM experience_kv WHERE experience = :experience AND key = :key", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":key", key));

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public int GetKeyCount(UUID experience)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM experience_kv WHERE experience = :experience", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience.ToString()));

                    object result = cmd.ExecuteScalar();
                    return result == null ? 0 : (int)(long)result;
                }
            }
        }

        public string[] GetKeys(UUID experience, int start, int count)
        {
            List<string> keys = new List<string>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT key FROM experience_kv WHERE experience = :experience ORDER BY key LIMIT :count OFFSET :start", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":start", start));
                    cmd.Parameters.Add(new SQLiteParameter(":count", count));

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                            keys.Add(result["key"].ToString());
                    }
                }
            }

            return keys.ToArray();
        }

        public int GetKeyValueSize(UUID experience)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                    "SELECT COALESCE(SUM(LENGTH(key) + LENGTH(value)), 0) FROM experience_kv WHERE experience = :experience", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":experience", experience.ToString()));

                    object result = cmd.ExecuteScalar();
                    return result == null ? 0 : (int)(long)result;
                }
            }
        }
    }
}
