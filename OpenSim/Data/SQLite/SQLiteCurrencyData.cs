using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.SQLite
{
    // Backing store for the native currency ledger - see
    // OpenSim.Data.ICurrencyData / MySqlCurrencyData for the design
    // rationale. Added because ICurrencyData only had a MySQL
    // implementation - every other service in this codebase supports all
    // 3 DB backends, currency was the one exception.
    public class SQLiteCurrencyData : ICurrencyData
    {
        private readonly SQLiteConnection m_conn;

        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public SQLiteCurrencyData(string connectionString)
        {
            DllmapConfigHelper.RegisterAssembly(typeof(SQLiteConnection).Assembly);

            if (string.IsNullOrEmpty(connectionString))
                connectionString = "URI=file:RegionStore.db";

            m_conn = new SQLiteConnection(connectionString);
            m_conn.Open();

            Migration m = new Migration(m_conn, Assembly, "Currency");
            m.Update();
        }

        public int GetBalance(UUID agentID)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT Balance FROM currency_balances WHERE PrincipalID = :id", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", agentID.ToString()));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            return Convert.ToInt32(reader.GetValue(0));
                    }
                }
            }

            SetBalance(agentID, 0);
            return 0;
        }

        public void SetBalance(UUID agentID, int amount)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT OR REPLACE INTO currency_balances (PrincipalID, Balance) VALUES (:id, :balance)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", agentID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":balance", amount));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void AddTransaction(CurrencyTransfer transfer)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO currency_transactions (TransactionID, ToAgent, FromAgent, Amount, TransferType, " +
                        "Description, Created, ToBalance, FromBalance) VALUES " +
                        "(:txid, :to, :from, :amount, :type, :description, :created, :tobalance, :frombalance)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":txid", transfer.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":to", transfer.ToAgent.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":from", transfer.FromAgent.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":amount", transfer.Amount));
                    cmd.Parameters.Add(new SQLiteParameter(":type", transfer.TransferType));
                    cmd.Parameters.Add(new SQLiteParameter(":description", transfer.Description ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(transfer.TransferDate)));
                    cmd.Parameters.Add(new SQLiteParameter(":tobalance", transfer.ToBalance));
                    cmd.Parameters.Add(new SQLiteParameter(":frombalance", transfer.FromBalance));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void ApplyTransfer(UUID fromID, int? newFromBalance, UUID toID, int? newToBalance, CurrencyTransfer transfer)
        {
            lock (this)
            {
                using (SQLiteTransaction sqltx = m_conn.BeginTransaction())
                {
                    try
                    {
                        if (newFromBalance.HasValue)
                            SetBalanceInTransaction(sqltx, fromID, newFromBalance.Value);

                        if (newToBalance.HasValue)
                            SetBalanceInTransaction(sqltx, toID, newToBalance.Value);

                        using (SQLiteCommand cmd = new SQLiteCommand(
                                "INSERT INTO currency_transactions (TransactionID, ToAgent, FromAgent, Amount, TransferType, " +
                                "Description, Created, ToBalance, FromBalance) VALUES " +
                                "(:txid, :to, :from, :amount, :type, :description, :created, :tobalance, :frombalance)", m_conn, sqltx))
                        {
                            cmd.Parameters.Add(new SQLiteParameter(":txid", transfer.ID.ToString()));
                            cmd.Parameters.Add(new SQLiteParameter(":to", transfer.ToAgent.ToString()));
                            cmd.Parameters.Add(new SQLiteParameter(":from", transfer.FromAgent.ToString()));
                            cmd.Parameters.Add(new SQLiteParameter(":amount", transfer.Amount));
                            cmd.Parameters.Add(new SQLiteParameter(":type", transfer.TransferType));
                            cmd.Parameters.Add(new SQLiteParameter(":description", transfer.Description ?? string.Empty));
                            cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(transfer.TransferDate)));
                            cmd.Parameters.Add(new SQLiteParameter(":tobalance", transfer.ToBalance));
                            cmd.Parameters.Add(new SQLiteParameter(":frombalance", transfer.FromBalance));
                            cmd.ExecuteNonQuery();
                        }

                        sqltx.Commit();
                    }
                    catch
                    {
                        sqltx.Rollback();
                        throw;
                    }
                }
            }
        }

        private void SetBalanceInTransaction(SQLiteTransaction sqltx, UUID agentID, int amount)
        {
            using (SQLiteCommand cmd = new SQLiteCommand(
                    "INSERT OR REPLACE INTO currency_balances (PrincipalID, Balance) VALUES (:id, :balance)", m_conn, sqltx))
            {
                cmd.Parameters.Add(new SQLiteParameter(":id", agentID.ToString()));
                cmd.Parameters.Add(new SQLiteParameter(":balance", amount));
                cmd.ExecuteNonQuery();
            }
        }

        public uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID)
        {
            StringBuilder where = new StringBuilder();
            List<SQLiteParameter> parms = new List<SQLiteParameter>();
            AppendAgentFilters(where, parms, toAgentID, fromAgentID);

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM currency_transactions" + where, m_conn))
                {
                    foreach (SQLiteParameter p in parms)
                        cmd.Parameters.Add(p);

                    return Convert.ToUInt32(cmd.ExecuteScalar());
                }
            }
        }

        public List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            List<CurrencyTransfer> transfers = new List<CurrencyTransfer>();

            StringBuilder where = new StringBuilder();
            List<SQLiteParameter> parms = new List<SQLiteParameter>();
            bool haveClause = AppendAgentFilters(where, parms, toAgentID, fromAgentID);

            where.Append(haveClause ? " AND " : " WHERE ");
            where.Append("Created >= :datestart AND Created <= :dateend");
            parms.Add(new SQLiteParameter(":datestart", Utils.DateTimeToUnixTime(dateStart)));
            parms.Add(new SQLiteParameter(":dateend", Utils.DateTimeToUnixTime(dateEnd)));

            string limit = string.Empty;
            if (start.HasValue && count.HasValue)
            {
                limit = " LIMIT :count OFFSET :start";
                parms.Add(new SQLiteParameter(":start", start.Value));
                parms.Add(new SQLiteParameter(":count", count.Value));
            }

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT TransactionID, ToAgent, FromAgent, Amount, TransferType, Description, Created, ToBalance, FromBalance " +
                        "FROM currency_transactions" + where + " ORDER BY Created DESC" + limit, m_conn))
                {
                    foreach (SQLiteParameter p in parms)
                        cmd.Parameters.Add(p);

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            CurrencyTransfer t = new CurrencyTransfer();
                            UUID.TryParse(reader.GetString(0), out t.ID);
                            UUID.TryParse(reader.GetString(1), out t.ToAgent);
                            UUID.TryParse(reader.GetString(2), out t.FromAgent);
                            t.Amount = Convert.ToInt32(reader.GetValue(3));
                            t.TransferType = Convert.ToInt32(reader.GetValue(4));
                            t.Description = reader.GetString(5);
                            t.TransferDate = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(6)));
                            t.ToBalance = Convert.ToInt32(reader.GetValue(7));
                            t.FromBalance = Convert.ToInt32(reader.GetValue(8));
                            transfers.Add(t);
                        }
                    }
                }
            }

            return transfers;
        }

        public void AddPurchase(CurrencyPurchase purchase)
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "INSERT INTO currency_purchases (PurchaseID, PrincipalID, IP, Amount, RealAmount, Created) VALUES " +
                        "(:id, :principal, :ip, :amount, :realamount, :created)", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":id", purchase.ID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":principal", purchase.AgentID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":ip", purchase.IP ?? string.Empty));
                    cmd.Parameters.Add(new SQLiteParameter(":amount", purchase.Amount));
                    cmd.Parameters.Add(new SQLiteParameter(":realamount", purchase.RealAmount));
                    cmd.Parameters.Add(new SQLiteParameter(":created", Utils.DateTimeToUnixTime(purchase.PurchaseDate)));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public uint NumberOfPurchases(UUID agentID)
        {
            lock (this)
            {
                string sql = agentID != UUID.Zero
                        ? "SELECT COUNT(*) FROM currency_purchases WHERE PrincipalID = :id"
                        : "SELECT COUNT(*) FROM currency_purchases";

                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    if (agentID != UUID.Zero)
                        cmd.Parameters.Add(new SQLiteParameter(":id", agentID.ToString()));
                    return Convert.ToUInt32(cmd.ExecuteScalar());
                }
            }
        }

        public List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            List<CurrencyPurchase> purchases = new List<CurrencyPurchase>();

            lock (this)
            {
                string sql = "SELECT PurchaseID, PrincipalID, IP, Amount, RealAmount, Created FROM currency_purchases WHERE " +
                        (agentID != UUID.Zero ? "PrincipalID = :id AND " : string.Empty) +
                        "Created >= :datestart AND Created <= :dateend ORDER BY Created DESC";
                if (start.HasValue && count.HasValue)
                    sql += " LIMIT :count OFFSET :start";

                using (SQLiteCommand cmd = new SQLiteCommand(sql, m_conn))
                {
                    if (agentID != UUID.Zero)
                        cmd.Parameters.Add(new SQLiteParameter(":id", agentID.ToString()));
                    cmd.Parameters.Add(new SQLiteParameter(":datestart", Utils.DateTimeToUnixTime(dateStart)));
                    cmd.Parameters.Add(new SQLiteParameter(":dateend", Utils.DateTimeToUnixTime(dateEnd)));
                    if (start.HasValue && count.HasValue)
                    {
                        cmd.Parameters.Add(new SQLiteParameter(":start", start.Value));
                        cmd.Parameters.Add(new SQLiteParameter(":count", count.Value));
                    }

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            CurrencyPurchase p = new CurrencyPurchase();
                            UUID.TryParse(reader.GetString(0), out p.ID);
                            UUID.TryParse(reader.GetString(1), out p.AgentID);
                            p.IP = reader.GetString(2);
                            p.Amount = Convert.ToInt32(reader.GetValue(3));
                            p.RealAmount = Convert.ToInt32(reader.GetValue(4));
                            p.PurchaseDate = Utils.UnixTimeToDateTime(Convert.ToUInt32(reader.GetValue(5)));
                            purchases.Add(p);
                        }
                    }
                }
            }

            return purchases;
        }

        public int GetTotalCirculation()
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT COALESCE(SUM(Balance), 0) FROM currency_balances", m_conn))
                {
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public int CountAccountsWithBalance()
        {
            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM currency_balances WHERE Balance > 0", m_conn))
                {
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
        }

        public List<CurrencyBalanceEntry> GetTopBalances(int count)
        {
            List<CurrencyBalanceEntry> results = new List<CurrencyBalanceEntry>();

            lock (this)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(
                        "SELECT PrincipalID, Balance FROM currency_balances WHERE Balance > 0 ORDER BY Balance DESC LIMIT :count", m_conn))
                {
                    cmd.Parameters.Add(new SQLiteParameter(":count", count <= 0 ? 10 : count));

                    using (IDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            CurrencyBalanceEntry entry = new CurrencyBalanceEntry();
                            UUID.TryParse(reader.GetString(0), out entry.PrincipalID);
                            entry.Balance = Convert.ToInt32(reader.GetValue(1));
                            results.Add(entry);
                        }
                    }
                }
            }

            return results;
        }

        private bool AppendAgentFilters(StringBuilder where, List<SQLiteParameter> parms, UUID toAgentID, UUID fromAgentID)
        {
            bool haveClause = false;

            if (toAgentID != UUID.Zero)
            {
                where.Append(" WHERE ToAgent = :to");
                parms.Add(new SQLiteParameter(":to", toAgentID.ToString()));
                haveClause = true;
            }

            if (fromAgentID != UUID.Zero)
            {
                where.Append(haveClause ? " AND FromAgent = :from" : " WHERE FromAgent = :from");
                parms.Add(new SQLiteParameter(":from", fromAgentID.ToString()));
                haveClause = true;
            }

            return haveClause;
        }
    }
}
