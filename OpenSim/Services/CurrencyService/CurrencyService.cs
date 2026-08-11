using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Services.CurrencyService
{
    // Native currency ledger - see ICurrencyService for the design rationale.
    // Console commands mirror the shape of the classic MoneyServer's admin tools
    // (money add/set/get) so operators moving off it don't have to relearn anything.
    public class CurrencyService : CurrencyServiceBase, ICurrencyService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public CurrencyService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[CURRENCY SERVICE]: Starting currency service");

            if (MainConsole.Instance != null)
            {
                MainConsole.Instance.Commands.AddCommand("Currency", false,
                        "money add",
                        "money add <agent-uuid> <amount>",
                        "Add to a user's balance (system-generated credit, no counterparty).",
                        HandleMoneyAdd);

                MainConsole.Instance.Commands.AddCommand("Currency", false,
                        "money set",
                        "money set <agent-uuid> <amount>",
                        "Set a user's balance to an exact amount.",
                        HandleMoneySet);

                MainConsole.Instance.Commands.AddCommand("Currency", false,
                        "money get",
                        "money get <agent-uuid>",
                        "Show a user's current balance.",
                        HandleMoneyGet);
            }
        }

        public int GetBalance(UUID agentID)
        {
            return m_Database.GetBalance(agentID);
        }

        public int SetBalance(UUID agentID, int amount, string description)
        {
            int previous = m_Database.GetBalance(agentID);
            m_Database.SetBalance(agentID, amount);

            m_Database.AddTransaction(new CurrencyTransfer
            {
                ID = UUID.Random(),
                ToAgent = agentID,
                FromAgent = UUID.Zero,
                Amount = amount - previous,
                TransferType = 0,
                Description = description ?? "Balance set by administrator",
                TransferDate = DateTime.UtcNow,
                ToBalance = amount,
                FromBalance = 0
            });

            return amount;
        }

        public bool Transfer(UUID toID, UUID fromID, int amount, string description, int transactionType, UUID transactionID)
        {
            if (amount < 0)
                return false;

            int fromBalance = 0;
            if (fromID != UUID.Zero)
            {
                fromBalance = m_Database.GetBalance(fromID);
                if (fromBalance < amount)
                    return false; // insufficient funds

                fromBalance -= amount;
                m_Database.SetBalance(fromID, fromBalance);
            }

            int toBalance = 0;
            if (toID != UUID.Zero)
            {
                toBalance = m_Database.GetBalance(toID) + amount;
                m_Database.SetBalance(toID, toBalance);
            }

            m_Database.AddTransaction(new CurrencyTransfer
            {
                ID = transactionID == UUID.Zero ? UUID.Random() : transactionID,
                ToAgent = toID,
                FromAgent = fromID,
                Amount = amount,
                TransferType = transactionType,
                Description = description ?? string.Empty,
                TransferDate = DateTime.UtcNow,
                ToBalance = toBalance,
                FromBalance = fromBalance
            });

            return true;
        }

        public uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID)
        {
            return m_Database.NumberOfTransactions(toAgentID, fromAgentID);
        }

        public List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            return m_Database.GetTransactionHistory(toAgentID, fromAgentID, dateStart, dateEnd, start, count);
        }

        public uint NumberOfPurchases(UUID agentID)
        {
            return m_Database.NumberOfPurchases(agentID);
        }

        public List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            return m_Database.GetPurchaseHistory(agentID, dateStart, dateEnd, start, count);
        }

        public bool RecordPurchase(UUID agentID, int amount, int realAmountHundredths, string ip)
        {
            m_Database.AddPurchase(new CurrencyPurchase
            {
                ID = UUID.Random(),
                AgentID = agentID,
                IP = ip ?? string.Empty,
                Amount = amount,
                RealAmount = realAmountHundredths,
                PurchaseDate = DateTime.UtcNow
            });

            return Transfer(agentID, UUID.Zero, amount, "Currency purchase", 0 /* BuyMoney */, UUID.Zero);
        }

        public int GetGroupBalance(UUID groupID)
        {
            return GetBalance(groupID);
        }

        public bool GroupCurrencyTransfer(UUID groupID, UUID agentID, int amount, string description,
                int transactionType, UUID transactionID, bool payingIntoGroup)
        {
            return payingIntoGroup
                    ? Transfer(groupID, agentID, amount, description, transactionType, transactionID)
                    : Transfer(agentID, groupID, amount, description, transactionType, transactionID);
        }

        public List<CurrencyTransfer> GetGroupTransactionHistory(UUID groupID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count)
        {
            // AND-vs-OR mismatch with GetTransactionHistory (see ICurrencyService) -
            // fetch both directions in full, then merge/sort/page in memory rather
            // than trying to express "group was either side" as a single call.
            List<CurrencyTransfer> received = m_Database.GetTransactionHistory(groupID, UUID.Zero, dateStart, dateEnd, null, null);
            List<CurrencyTransfer> sent = m_Database.GetTransactionHistory(UUID.Zero, groupID, dateStart, dateEnd, null, null);

            List<CurrencyTransfer> merged = new List<CurrencyTransfer>(received.Count + sent.Count);
            merged.AddRange(received);
            merged.AddRange(sent);
            merged.Sort((a, b) => b.TransferDate.CompareTo(a.TransferDate));

            if (!start.HasValue || !count.HasValue)
                return merged;

            int skip = Math.Min((int)start.Value, merged.Count);
            int take = Math.Min((int)count.Value, merged.Count - skip);
            return merged.GetRange(skip, take);
        }

        public int PayGroupDividend(UUID groupID, List<UUID> memberIDs, string description)
        {
            if (memberIDs == null || memberIDs.Count == 0)
                return 0;

            int balance = GetBalance(groupID);
            int perMember = balance / memberIDs.Count;
            if (perMember <= 0)
                return 0;

            foreach (UUID memberID in memberIDs)
                GroupCurrencyTransfer(groupID, memberID, perMember, description ?? "Group dividend", 0, UUID.Zero, false);

            return perMember;
        }

        #region Console commands

        private void HandleMoneyAdd(string module, string[] cmdparams)
        {
            if (!TryParseUuidAmount(cmdparams, out UUID agentID, out int amount))
                return;

            int newBalance = GetBalance(agentID) + amount;
            SetBalance(agentID, newBalance, "Console: money add");
            MainConsole.Instance.Output("New balance: {0}", null, newBalance);
        }

        private void HandleMoneySet(string module, string[] cmdparams)
        {
            if (!TryParseUuidAmount(cmdparams, out UUID agentID, out int amount))
                return;

            SetBalance(agentID, amount, "Console: money set");
            MainConsole.Instance.Output("Balance set to: {0}", null, amount);
        }

        private void HandleMoneyGet(string module, string[] cmdparams)
        {
            if (cmdparams.Length < 3 || !UUID.TryParse(cmdparams[2], out UUID agentID))
            {
                MainConsole.Instance.Output("Usage: money get <agent-uuid>");
                return;
            }

            MainConsole.Instance.Output("Balance: {0}", null, GetBalance(agentID));
        }

        private bool TryParseUuidAmount(string[] cmdparams, out UUID agentID, out int amount)
        {
            agentID = UUID.Zero;
            amount = 0;

            if (cmdparams.Length < 4 || !UUID.TryParse(cmdparams[2], out agentID) || !int.TryParse(cmdparams[3], out amount))
            {
                MainConsole.Instance.Output("Usage: {0} <agent-uuid> <amount>", null, cmdparams.Length > 0 ? cmdparams[0] + " " + cmdparams[1] : "money");
                return false;
            }

            return true;
        }

        #endregion
    }
}
