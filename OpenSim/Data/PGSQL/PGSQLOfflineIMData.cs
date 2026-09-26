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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenMetaverse;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLOfflineIMData : PGSQLGenericTableHandler<OfflineIMData>, IOfflineIMData
    {
        public PGSQLOfflineIMData(string connectionString, string realm)
            : base(connectionString, realm, "IM_Store")
        {
        }

        // Every offline message is a new row. The generic Store first tries an UPDATE keyed on the
        // row's fields (recipient and sender), which for this table meant a second message from
        // the same sender to the same offline recipient silently overwrote the first, so only the
        // last message per sender was ever delivered. MySQL always inserts; so does this.
        public override bool Store(OfflineIMData row)
        {
            string message = row.Data != null && row.Data.TryGetValue("Message", out string m) ? m : string.Empty;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_ConnectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                String.Format("INSERT INTO {0} (\"PrincipalID\", \"FromID\", \"Message\") VALUES (:PrincipalID, :FromID, :Message)", m_Realm), conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter(":PrincipalID", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = row.PrincipalID.Guid });
                cmd.Parameters.Add(new NpgsqlParameter(":FromID", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = row.FromID.Guid });
                cmd.Parameters.Add(new NpgsqlParameter(":Message", NpgsqlTypes.NpgsqlDbType.Text) { Value = message });
                conn.Open();
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public void DeleteOld()
        {
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                cmd.CommandText = String.Format("delete from {0} where \"TMStamp\" < CURRENT_DATE - INTERVAL '2 week'", m_Realm);

                ExecuteNonQuery(cmd);
            }

        }
    }
}
