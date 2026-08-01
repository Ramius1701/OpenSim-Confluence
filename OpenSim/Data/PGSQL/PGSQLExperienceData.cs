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
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLExperienceData : IExperienceData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLExperienceData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Experience");
                m.Update();
            }
        }

        public Dictionary<UUID, bool> GetExperiencePermissions(UUID agent_id)
        {
            Dictionary<UUID, bool> experiencePermissions = new Dictionary<UUID, bool>();

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "select experience, allow from experience_permissions where avatar = :avatar", dbcon))
                {
                    cmd.Parameters.AddWithValue(":avatar", agent_id.ToString());

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                        {
                            string uuid = result["experience"].ToString();
                            bool allow = (bool)result["allow"];

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
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "delete from experience_permissions where avatar = :avatar AND experience = :experience", dbcon))
                {
                    cmd.Parameters.AddWithValue(":avatar", agent_id.ToString());
                    cmd.Parameters.AddWithValue(":experience", experience_id.ToString());

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public bool SetExperiencePermissions(UUID agent_id, UUID experience_id, bool allow)
        {
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "insert into experience_permissions (experience, avatar, allow) values (:experience, :avatar, :allow) " +
                    "on conflict (experience, avatar) do update set allow = excluded.allow", dbcon))
                {
                    cmd.Parameters.AddWithValue(":avatar", agent_id.ToString());
                    cmd.Parameters.AddWithValue(":experience", experience_id.ToString());
                    cmd.Parameters.AddWithValue(":allow", allow);

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

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT * FROM experiences WHERE public_id IN (" + joined + ")", dbcon))
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

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT public_id FROM experiences WHERE owner_id = :avatar", dbcon))
                {
                    cmd.Parameters.AddWithValue(":avatar", agent_id.ToString());
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
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "insert into experiences (public_id, owner_id, name, description, group_id, logo, marketplace, slurl, maturity, properties) " +
                    "values (:public_id, :owner_id, :name, :description, :group_id, :logo, :marketplace, :slurl, :maturity, :properties) " +
                    "on conflict (public_id) do update set " +
                    "owner_id = excluded.owner_id, name = excluded.name, description = excluded.description, " +
                    "group_id = excluded.group_id, logo = excluded.logo, marketplace = excluded.marketplace, " +
                    "slurl = excluded.slurl, maturity = excluded.maturity, properties = excluded.properties", dbcon))
                {
                    cmd.Parameters.AddWithValue(":public_id", data.public_id.ToString());
                    cmd.Parameters.AddWithValue(":owner_id", data.owner_id.ToString());
                    cmd.Parameters.AddWithValue(":name", data.name ?? string.Empty);
                    cmd.Parameters.AddWithValue(":description", data.description ?? string.Empty);
                    cmd.Parameters.AddWithValue(":group_id", data.group_id.ToString());
                    cmd.Parameters.AddWithValue(":logo", data.logo.ToString());
                    cmd.Parameters.AddWithValue(":marketplace", data.marketplace ?? string.Empty);
                    cmd.Parameters.AddWithValue(":slurl", data.slurl ?? string.Empty);
                    cmd.Parameters.AddWithValue(":maturity", data.maturity);
                    cmd.Parameters.AddWithValue(":properties", data.properties);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public ExperienceInfoData[] FindExperiences(string search)
        {
            List<ExperienceInfoData> experiences = new List<ExperienceInfoData>();

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT * FROM experiences WHERE name ILIKE :search", dbcon))
                {
                    cmd.Parameters.AddWithValue(":search", string.Format("%{0}%", search));

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

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT public_id FROM experiences WHERE group_id = :group", dbcon))
                {
                    cmd.Parameters.AddWithValue(":group", group_id.ToString());
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

            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT public_id FROM experiences WHERE group_id IN (" + joined + ")", dbcon))
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
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "insert into experience_kv (experience, key, value) values (:experience, :key, :value) " +
                    "on conflict (experience, key) do update set value = excluded.value", dbcon))
                {
                    cmd.Parameters.AddWithValue(":experience", experience.ToString());
                    cmd.Parameters.AddWithValue(":key", key);
                    cmd.Parameters.AddWithValue(":value", val);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public string GetKeyValue(UUID experience, string key)
        {
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT value FROM experience_kv WHERE experience = :experience AND key = :key LIMIT 1", dbcon))
                {
                    cmd.Parameters.AddWithValue(":experience", experience.ToString());
                    cmd.Parameters.AddWithValue(":key", key);

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
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "DELETE FROM experience_kv WHERE experience = :experience AND key = :key", dbcon))
                {
                    cmd.Parameters.AddWithValue(":experience", experience.ToString());
                    cmd.Parameters.AddWithValue(":key", key);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        public int GetKeyCount(UUID experience)
        {
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM experience_kv WHERE experience = :experience", dbcon))
                {
                    cmd.Parameters.AddWithValue(":experience", experience.ToString());

                    return (int)(long)cmd.ExecuteScalar();
                }
            }
        }

        public string[] GetKeys(UUID experience, int start, int count)
        {
            List<string> keys = new List<string>();
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT key FROM experience_kv WHERE experience = :experience ORDER BY key LIMIT :count OFFSET :start", dbcon))
                {
                    cmd.Parameters.AddWithValue(":experience", experience.ToString());
                    cmd.Parameters.AddWithValue(":start", start);
                    cmd.Parameters.AddWithValue(":count", count);

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
            using (NpgsqlConnection dbcon = new NpgsqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT COALESCE(SUM(LENGTH(key) + LENGTH(value)), 0) FROM experience_kv WHERE experience = :experience", dbcon))
                {
                    cmd.Parameters.AddWithValue(":experience", experience.ToString());

                    object result = cmd.ExecuteScalar();
                    return result == null || result is System.DBNull ? 0 : (int)(long)result;
                }
            }
        }
    }
}
