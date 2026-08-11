using System;
using System.Reflection;
using System.Data;
using System.Text;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data.MySQL
{
    public class MySqlCurrencyData : MySqlFramework, ICurrencyData
    {
        protected virtual Assembly Assembly
        {
            get { return GetType().Assembly; }
        }

        public MySqlCurrencyData(string connectionString)
                : base(connectionString)
        {
            m_connectionString = connectionString;

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();
                Migration m = new Migration(dbcon, Assembly, "Currency");
                m.Update();
                dbcon.Close();
            }
        }

        public int GetBalance(UUID agentID)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select `Balance` from `currency_balances` where `PrincipalID` = ?PrincipalID", dbcon))
                {
                    cmd.Parameters.AddWithValue("?PrincipalID", agentID.ToString());

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        if (result.Read())
                            return result.GetInt32(0);
                    }
                }
            }

            // No row yet - create one at 0 so future updates have something to touch.
            SetBalance(agentID, 0);
            return 0;
        }

        public void SetBalance(UUID agentID, int amount)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "insert into `currency_balances` (`PrincipalID`, `Balance`) values (?PrincipalID, ?Balance) "
                    + "on duplicate key update `Balance` = ?Balance", dbcon))
                {
                    cmd.Parameters.AddWithValue("?PrincipalID", agentID.ToString());
                    cmd.Parameters.AddWithValue("?Balance", amount);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void AddTransaction(CurrencyTransfer transfer)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "insert into `currency_transactions` "
                    + "(`TransactionID`, `ToAgent`, `FromAgent`, `Amount`, `TransferType`, `Description`, "
                    + "`Created`, `ToBalance`, `FromBalance`) values "
                    + "(?TransactionID, ?ToAgent, ?FromAgent, ?Amount, ?TransferType, ?Description, "
                    + "?Created, ?ToBalance, ?FromBalance)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?TransactionID", transfer.ID.ToString());
                    cmd.Parameters.AddWithValue("?ToAgent", transfer.ToAgent.ToString());
                    cmd.Parameters.AddWithValue("?FromAgent", transfer.FromAgent.ToString());
                    cmd.Parameters.AddWithValue("?Amount", transfer.Amount);
                    cmd.Parameters.AddWithValue("?TransferType", transfer.TransferType);
                    cmd.Parameters.AddWithValue("?Description", transfer.Description ?? string.Empty);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(transfer.TransferDate));
                    cmd.Parameters.AddWithValue("?ToBalance", transfer.ToBalance);
                    cmd.Parameters.AddWithValue("?FromBalance", transfer.FromBalance);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID)
        {
            StringBuilder where = new StringBuilder();
            List<MySqlParameter> parms = new List<MySqlParameter>();
            AppendAgentFilters(where, parms, toAgentID, fromAgentID);

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select count(*) from `currency_transactions`" + where, dbcon))
                {
                    foreach (MySqlParameter p in parms)
                        cmd.Parameters.Add(p);

                    return Convert.ToUInt32(cmd.ExecuteScalar());
                }
            }
        }

        public List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            List<CurrencyTransfer> transfers = new List<CurrencyTransfer>();

            StringBuilder where = new StringBuilder();
            List<MySqlParameter> parms = new List<MySqlParameter>();
            bool haveClause = AppendAgentFilters(where, parms, toAgentID, fromAgentID);

            where.Append(haveClause ? " and " : " where ");
            where.Append("`Created` >= ?DateStart and `Created` <= ?DateEnd");
            parms.Add(new MySqlParameter("?DateStart", Utils.DateTimeToUnixTime(dateStart)));
            parms.Add(new MySqlParameter("?DateEnd", Utils.DateTimeToUnixTime(dateEnd)));

            string limit = string.Empty;
            if (start.HasValue && count.HasValue)
            {
                limit = " limit ?Start, ?Count";
                parms.Add(new MySqlParameter("?Start", start.Value));
                parms.Add(new MySqlParameter("?Count", count.Value));
            }

            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "select * from `currency_transactions`" + where + " order by `Created` desc" + limit, dbcon))
                {
                    foreach (MySqlParameter p in parms)
                        cmd.Parameters.Add(p);

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                        {
                            CurrencyTransfer t = new CurrencyTransfer();
                            UUID.TryParse(result["TransactionID"].ToString(), out t.ID);
                            UUID.TryParse(result["ToAgent"].ToString(), out t.ToAgent);
                            UUID.TryParse(result["FromAgent"].ToString(), out t.FromAgent);
                            t.Amount = Convert.ToInt32(result["Amount"]);
                            t.TransferType = Convert.ToInt32(result["TransferType"]);
                            t.Description = result["Description"].ToString();
                            t.TransferDate = Utils.UnixTimeToDateTime(Convert.ToUInt32(result["Created"]));
                            t.ToBalance = Convert.ToInt32(result["ToBalance"]);
                            t.FromBalance = Convert.ToInt32(result["FromBalance"]);
                            transfers.Add(t);
                        }
                    }
                }
            }

            return transfers;
        }

        public void AddPurchase(CurrencyPurchase purchase)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                using (MySqlCommand cmd = new MySqlCommand(
                    "insert into `currency_purchases` "
                    + "(`PurchaseID`, `PrincipalID`, `IP`, `Amount`, `RealAmount`, `Created`) values "
                    + "(?PurchaseID, ?PrincipalID, ?IP, ?Amount, ?RealAmount, ?Created)", dbcon))
                {
                    cmd.Parameters.AddWithValue("?PurchaseID", purchase.ID.ToString());
                    cmd.Parameters.AddWithValue("?PrincipalID", purchase.AgentID.ToString());
                    cmd.Parameters.AddWithValue("?IP", purchase.IP ?? string.Empty);
                    cmd.Parameters.AddWithValue("?Amount", purchase.Amount);
                    cmd.Parameters.AddWithValue("?RealAmount", purchase.RealAmount);
                    cmd.Parameters.AddWithValue("?Created", Utils.DateTimeToUnixTime(purchase.PurchaseDate));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // agentID == UUID.Zero means "don't filter by principal" - see
        // GetPurchaseHistory below.
        public uint NumberOfPurchases(UUID agentID)
        {
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                string sql = agentID != UUID.Zero
                        ? "select count(*) from `currency_purchases` where `PrincipalID` = ?PrincipalID"
                        : "select count(*) from `currency_purchases`";

                using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
                {
                    if (agentID != UUID.Zero)
                        cmd.Parameters.AddWithValue("?PrincipalID", agentID.ToString());
                    return Convert.ToUInt32(cmd.ExecuteScalar());
                }
            }
        }

        // agentID == UUID.Zero means "don't filter by principal" - a grid-wide
        // feed for the WebInterface financial reporting page - matching how
        // GetTransactionHistory/AppendAgentFilters already treat UUID.Zero.
        public List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            List<CurrencyPurchase> purchases = new List<CurrencyPurchase>();

            string limit = string.Empty;
            using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
            {
                dbcon.Open();

                string sql = "select * from `currency_purchases` where "
                    + (agentID != UUID.Zero ? "`PrincipalID` = ?PrincipalID and " : string.Empty)
                    + "`Created` >= ?DateStart and `Created` <= ?DateEnd order by `Created` desc";
                if (start.HasValue && count.HasValue)
                    sql += " limit ?Start, ?Count";

                using (MySqlCommand cmd = new MySqlCommand(sql, dbcon))
                {
                    if (agentID != UUID.Zero)
                        cmd.Parameters.AddWithValue("?PrincipalID", agentID.ToString());
                    cmd.Parameters.AddWithValue("?DateStart", Utils.DateTimeToUnixTime(dateStart));
                    cmd.Parameters.AddWithValue("?DateEnd", Utils.DateTimeToUnixTime(dateEnd));
                    if (start.HasValue && count.HasValue)
                    {
                        cmd.Parameters.AddWithValue("?Start", start.Value);
                        cmd.Parameters.AddWithValue("?Count", count.Value);
                    }

                    using (IDataReader result = cmd.ExecuteReader())
                    {
                        while (result.Read())
                        {
                            CurrencyPurchase p = new CurrencyPurchase();
                            UUID.TryParse(result["PurchaseID"].ToString(), out p.ID);
                            UUID.TryParse(result["PrincipalID"].ToString(), out p.AgentID);
                            p.IP = result["IP"].ToString();
                            p.Amount = Convert.ToInt32(result["Amount"]);
                            p.RealAmount = Convert.ToInt32(result["RealAmount"]);
                            p.PurchaseDate = Utils.UnixTimeToDateTime(Convert.ToUInt32(result["Created"]));
                            purchases.Add(p);
                        }
                    }
                }
            }

            return purchases;
        }

        // Builds "where ..."/"and ..." clauses for optional to/from agent filters -
        // UUID.Zero means "don't filter on this side", matching how the region-edge
        // IMoneyModule already treats a zero agent as "system", not a real principal.
        private bool AppendAgentFilters(StringBuilder where, List<MySqlParameter> parms, UUID toAgentID, UUID fromAgentID)
        {
            bool haveClause = false;

            if (toAgentID != UUID.Zero)
            {
                where.Append(" where `ToAgent` = ?ToAgent");
                parms.Add(new MySqlParameter("?ToAgent", toAgentID.ToString()));
                haveClause = true;
            }

            if (fromAgentID != UUID.Zero)
            {
                where.Append(haveClause ? " and `FromAgent` = ?FromAgent" : " where `FromAgent` = ?FromAgent");
                parms.Add(new MySqlParameter("?FromAgent", fromAgentID.ToString()));
                haveClause = true;
            }

            return haveClause;
        }
    }
}
