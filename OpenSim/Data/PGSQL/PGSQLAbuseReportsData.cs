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
using System.Reflection;
using OpenSim.Framework;
using Npgsql;

namespace OpenSim.Data.PGSQL
{
    public class PGSQLAbuseReportsData : PGSQLGenericTableHandler<AbuseReportData>, IAbuseReportsData
    {
        public PGSQLAbuseReportsData(string connectionString)
            : base(connectionString, "AbuseReports", "AbuseReports")
        {
        }

        // ReportID is a Postgres "serial" column and is always excluded from
        // the INSERT so the sequence assigns it - unlike MySQL, explicitly
        // inserting 0 would just store the literal value 0 rather than
        // triggering autoincrement. Optional fields (screenshot, category,
        // etc.) are frequently left unset by the viewer's report-abuse
        // caps handler, so a null value here falls back to a safe empty
        // default instead of the base handler's null-throws behaviour.
        public override bool Store(AbuseReportData row)
        {
            List<string> names = new List<string>();
            List<string> values = new List<string>();

            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                foreach (FieldInfo fi in m_Fields.Values)
                {
                    if (fi.Name == "ReportID")
                        continue;

                    object value = fi.GetValue(row);
                    if (fi.Name == "ImageData")
                        value ??= Array.Empty<byte>();
                    else if (fi.FieldType == typeof(string))
                        value ??= string.Empty;

                    names.Add(fi.Name);
                    values.Add(":" + fi.Name);

                    if (m_FieldTypes.TryGetValue(fi.Name, out string ftype))
                        cmd.Parameters.Add(m_database.CreateParameter(fi.Name, value, ftype));
                    else
                        cmd.Parameters.Add(m_database.CreateParameter(fi.Name, value));
                }

                cmd.CommandText = string.Format("INSERT INTO {0} (\"{1}\") VALUES ({2})",
                        m_Realm, string.Join("\",\"", names.ToArray()), string.Join(",", values.ToArray()));

                return ExecuteNonQuery(cmd) > 0;
            }
        }
    }
}
