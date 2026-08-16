using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Npgsql;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.PGSQL
{
    // Backing store for the native currency ledger - see
    // OpenSim.Data.ICurrencyData / MySqlCurrencyData for the design
    // rationale. Added because ICurrencyData only had a MySQL
    // implementation - every other service in this codebase supports all
    // 3 DB backends, currency was the one exception.
    public class PGSQLCurrencyData : ICurrencyData
    {
        private readonly string m_connectionString;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public PGSQLCurrencyData(string connectionString)
        {
            m_connectionString = connectionString;

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                conn.Open();
                Migration m = new Migration(conn, Assembly, "Currency");
                m.Update();
            }
        }

        public int GetBalance(UUID agentID)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"Balance\" FROM currency_balances WHERE \"PrincipalID\" = :id", conn))
            {
                cmd.Parameters.AddWithValue(":id", agentID.ToString());
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return reader.GetInt32(0);
                }
            }

            SetBalance(agentID, 0);
            return 0;
        }

        public void SetBalance(UUID agentID, int amount)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO currency_balances (\"PrincipalID\", \"Balance\") VALUES (:id, :balance) " +
                    "ON CONFLICT (\"PrincipalID\") DO UPDATE SET \"Balance\" = :balance", conn))
            {
                cmd.Parameters.AddWithValue(":id", agentID.ToString());
                cmd.Parameters.AddWithValue(":balance", amount);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public void AddTransaction(CurrencyTransfer transfer)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO currency_transactions (\"TransactionID\", \"ToAgent\", \"FromAgent\", \"Amount\", " +
                    "\"TransferType\", \"Description\", \"Created\", \"ToBalance\", \"FromBalance\") VALUES " +
                    "(:txid, :to, :from, :amount, :type, :description, :created, :tobalance, :frombalance)", conn))
            {
                cmd.Parameters.AddWithValue(":txid", transfer.ID.ToString());
                cmd.Parameters.AddWithValue(":to", transfer.ToAgent.ToString());
                cmd.Parameters.AddWithValue(":from", transfer.FromAgent.ToString());
                cmd.Parameters.AddWithValue(":amount", transfer.Amount);
                cmd.Parameters.AddWithValue(":type", transfer.TransferType);
                cmd.Parameters.AddWithValue(":description", transfer.Description ?? string.Empty);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(transfer.TransferDate));
                cmd.Parameters.AddWithValue(":tobalance", transfer.ToBalance);
                cmd.Parameters.AddWithValue(":frombalance", transfer.FromBalance);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID)
        {
            StringBuilder where = new StringBuilder();
            List<NpgsqlParameter> parms = new List<NpgsqlParameter>();
            AppendAgentFilters(where, parms, toAgentID, fromAgentID);

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT COUNT(*) FROM currency_transactions" + where, conn))
            {
                foreach (NpgsqlParameter p in parms)
                    cmd.Parameters.Add(p);
                conn.Open();

                return Convert.ToUInt32(cmd.ExecuteScalar());
            }
        }

        public List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            List<CurrencyTransfer> transfers = new List<CurrencyTransfer>();

            StringBuilder where = new StringBuilder();
            List<NpgsqlParameter> parms = new List<NpgsqlParameter>();
            bool haveClause = AppendAgentFilters(where, parms, toAgentID, fromAgentID);

            where.Append(haveClause ? " AND " : " WHERE ");
            where.Append("\"Created\" >= :datestart AND \"Created\" <= :dateend");
            parms.Add(new NpgsqlParameter(":datestart", (int)Utils.DateTimeToUnixTime(dateStart)));
            parms.Add(new NpgsqlParameter(":dateend", (int)Utils.DateTimeToUnixTime(dateEnd)));

            string limit = string.Empty;
            if (start.HasValue && count.HasValue)
            {
                limit = " LIMIT :count OFFSET :start";
                parms.Add(new NpgsqlParameter(":start", (int)start.Value));
                parms.Add(new NpgsqlParameter(":count", (int)count.Value));
            }

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"TransactionID\", \"ToAgent\", \"FromAgent\", \"Amount\", \"TransferType\", \"Description\", " +
                    "\"Created\", \"ToBalance\", \"FromBalance\" FROM currency_transactions" + where +
                    " ORDER BY \"Created\" DESC" + limit, conn))
            {
                foreach (NpgsqlParameter p in parms)
                    cmd.Parameters.Add(p);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        CurrencyTransfer t = new CurrencyTransfer();
                        UUID.TryParse(reader.GetString(0), out t.ID);
                        UUID.TryParse(reader.GetString(1), out t.ToAgent);
                        UUID.TryParse(reader.GetString(2), out t.FromAgent);
                        t.Amount = reader.GetInt32(3);
                        t.TransferType = reader.GetInt32(4);
                        t.Description = reader.GetString(5);
                        t.TransferDate = Utils.UnixTimeToDateTime((uint)reader.GetInt32(6));
                        t.ToBalance = reader.GetInt32(7);
                        t.FromBalance = reader.GetInt32(8);
                        transfers.Add(t);
                    }
                }
            }

            return transfers;
        }

        public void AddPurchase(CurrencyPurchase purchase)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO currency_purchases (\"PurchaseID\", \"PrincipalID\", \"IP\", \"Amount\", \"RealAmount\", \"Created\") " +
                    "VALUES (:id, :principal, :ip, :amount, :realamount, :created)", conn))
            {
                cmd.Parameters.AddWithValue(":id", purchase.ID.ToString());
                cmd.Parameters.AddWithValue(":principal", purchase.AgentID.ToString());
                cmd.Parameters.AddWithValue(":ip", purchase.IP ?? string.Empty);
                cmd.Parameters.AddWithValue(":amount", purchase.Amount);
                cmd.Parameters.AddWithValue(":realamount", purchase.RealAmount);
                cmd.Parameters.AddWithValue(":created", (int)Utils.DateTimeToUnixTime(purchase.PurchaseDate));
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        public uint NumberOfPurchases(UUID agentID)
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                string sql = agentID != UUID.Zero
                        ? "SELECT COUNT(*) FROM currency_purchases WHERE \"PrincipalID\" = :id"
                        : "SELECT COUNT(*) FROM currency_purchases";

                using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
                {
                    if (agentID != UUID.Zero)
                        cmd.Parameters.AddWithValue(":id", agentID.ToString());
                    conn.Open();

                    return Convert.ToUInt32(cmd.ExecuteScalar());
                }
            }
        }

        public List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            List<CurrencyPurchase> purchases = new List<CurrencyPurchase>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            {
                string sql = "SELECT \"PurchaseID\", \"PrincipalID\", \"IP\", \"Amount\", \"RealAmount\", \"Created\" FROM currency_purchases WHERE " +
                        (agentID != UUID.Zero ? "\"PrincipalID\" = :id AND " : string.Empty) +
                        "\"Created\" >= :datestart AND \"Created\" <= :dateend ORDER BY \"Created\" DESC";
                if (start.HasValue && count.HasValue)
                    sql += " LIMIT :count OFFSET :start";

                using (NpgsqlCommand cmd = new NpgsqlCommand(sql, conn))
                {
                    if (agentID != UUID.Zero)
                        cmd.Parameters.AddWithValue(":id", agentID.ToString());
                    cmd.Parameters.AddWithValue(":datestart", (int)Utils.DateTimeToUnixTime(dateStart));
                    cmd.Parameters.AddWithValue(":dateend", (int)Utils.DateTimeToUnixTime(dateEnd));
                    if (start.HasValue && count.HasValue)
                    {
                        cmd.Parameters.AddWithValue(":start", (int)start.Value);
                        cmd.Parameters.AddWithValue(":count", (int)count.Value);
                    }
                    conn.Open();

                    using (NpgsqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            CurrencyPurchase p = new CurrencyPurchase();
                            UUID.TryParse(reader.GetString(0), out p.ID);
                            UUID.TryParse(reader.GetString(1), out p.AgentID);
                            p.IP = reader.GetString(2);
                            p.Amount = reader.GetInt32(3);
                            p.RealAmount = reader.GetInt32(4);
                            p.PurchaseDate = Utils.UnixTimeToDateTime((uint)reader.GetInt32(5));
                            purchases.Add(p);
                        }
                    }
                }
            }

            return purchases;
        }

        public int GetTotalCirculation()
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT COALESCE(SUM(\"Balance\"), 0) FROM currency_balances", conn))
            {
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public int CountAccountsWithBalance()
        {
            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand("SELECT COUNT(*) FROM currency_balances WHERE \"Balance\" > 0", conn))
            {
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public List<CurrencyBalanceEntry> GetTopBalances(int count)
        {
            List<CurrencyBalanceEntry> results = new List<CurrencyBalanceEntry>();

            using (NpgsqlConnection conn = new NpgsqlConnection(m_connectionString))
            using (NpgsqlCommand cmd = new NpgsqlCommand(
                    "SELECT \"PrincipalID\", \"Balance\" FROM currency_balances WHERE \"Balance\" > 0 ORDER BY \"Balance\" DESC LIMIT :count", conn))
            {
                cmd.Parameters.AddWithValue(":count", count <= 0 ? 10 : count);
                conn.Open();

                using (NpgsqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        CurrencyBalanceEntry entry = new CurrencyBalanceEntry();
                        UUID.TryParse(reader.GetString(0), out entry.PrincipalID);
                        entry.Balance = reader.GetInt32(1);
                        results.Add(entry);
                    }
                }
            }

            return results;
        }

        private bool AppendAgentFilters(StringBuilder where, List<NpgsqlParameter> parms, UUID toAgentID, UUID fromAgentID)
        {
            bool haveClause = false;

            if (toAgentID != UUID.Zero)
            {
                where.Append(" WHERE \"ToAgent\" = :to");
                parms.Add(new NpgsqlParameter(":to", toAgentID.ToString()));
                haveClause = true;
            }

            if (fromAgentID != UUID.Zero)
            {
                where.Append(haveClause ? " AND \"FromAgent\" = :from" : " WHERE \"FromAgent\" = :from");
                parms.Add(new NpgsqlParameter(":from", fromAgentID.ToString()));
                haveClause = true;
            }

            return haveClause;
        }
    }
}
