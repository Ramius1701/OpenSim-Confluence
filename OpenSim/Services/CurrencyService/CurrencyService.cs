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

        // Real-money purchase caps (hundredths of a dollar, same unit as
        // CurrencyPurchase.RealAmount/EstimatedCostHundredths - a "$500 cap"
        // limits actual dollars spent, not virtual currency units received,
        // since the whole point is bounding real financial exposure from a
        // compromised account or a payment-flow bug, not limiting how much
        // in-world currency someone can hold). MoneyServer/DTLNSLMoneyModule
        // had this ("Configurable daily, weekly, and monthly purchase
        // limits" - see README.md); the native ledger never got an
        // equivalent when Batch 12 made ConfluenceCurrencyModule the active
        // EconomyModule instead - a real gap, found live (a 300,000-unit
        // test purchase went through with nothing to stop it). Configurable
        // via [CurrencyService] in Robust.HG.ini; 0 disables that one cap.
        private readonly int m_dailyCapHundredths;
        private readonly int m_weeklyCapHundredths;
        private readonly int m_monthlyCapHundredths;

        // DTLNSLMoneyModule/MoneyServer had a real "Banker" concept
        // (MoneyServer.ini's BankerAvatar + AddBankerMoneyHandler) that the
        // native ledger never got an equivalent for: every "system"
        // transfer (fees, currency purchases, upload charges - anything
        // that passes UUID.Zero as a counterparty) currently just skips
        // balance tracking entirely for that side of the transfer - money
        // vanishes into or appears from nowhere, untracked, unauditable.
        // When a Banker Avatar is configured (Grid Settings admin page,
        // key "BankerAvatarID" - same live IGridSettingsService store
        // already backing that page, so it's settable without a restart,
        // matching MoneyServer's own settable-without-recompiling
        // BankerAvatar), Transfer() substitutes it for UUID.Zero on
        // either side, so that money actually comes from and goes to a
        // real, balance-tracked account. Unset (empty, or explicitly
        // "00000000-0000-0000-0000-000000000000", matching MoneyServer's
        // own "unset" convention) means unchanged current behavior.
        //
        // Real behavioral consequence once configured, not just cosmetic:
        // a "system credit" transfer (fromID=UUID.Zero, e.g. a currency
        // purchase credit) now debits the banker's own real balance and
        // can fail with insufficient funds if the banker isn't funded -
        // this is the whole point (money has to come from somewhere real
        // now), but it means the banker account needs a real starting
        // balance (e.g. "money set <bankerUUID> <amount>") before this is
        // turned on, or every system credit will start failing.
        private IGridSettingsService m_GridSettingsService;

        public CurrencyService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[CURRENCY SERVICE]: Starting currency service");

            IConfig currencyConfig = config.Configs["CurrencyService"];
            m_dailyCapHundredths = (currencyConfig?.GetInt("DailyPurchaseCapUSD", 500) ?? 500) * 100;
            m_weeklyCapHundredths = (currencyConfig?.GetInt("WeeklyPurchaseCapUSD", 2000) ?? 2000) * 100;
            m_monthlyCapHundredths = (currencyConfig?.GetInt("MonthlyPurchaseCapUSD", 5000) ?? 5000) * 100;

            IConfig gridSettingsConfig = config.Configs["GridSettingsService"];
            string gridSettingsDll = gridSettingsConfig?.GetString("LocalServiceModule", string.Empty);
            if (!string.IsNullOrEmpty(gridSettingsDll))
            {
                try
                {
                    m_GridSettingsService = LoadPlugin<IGridSettingsService>(gridSettingsDll, new object[] { config });
                }
                catch (Exception e)
                {
                    m_log.Warn("[CURRENCY SERVICE]: Could not load IGridSettingsService for Banker Avatar support - system transfers will use the old untracked UUID.Zero behavior", e);
                }
            }

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

        // Live-reads the current Grid Settings value on every call (no
        // caching) so an admin changing/unsetting the Banker Avatar takes
        // effect immediately, without a Robust restart - same as every
        // other Grid Settings-backed admin control in the WebUI.
        private UUID GetBankerAvatarID()
        {
            if (m_GridSettingsService == null)
                return UUID.Zero;

            string value = m_GridSettingsService.Get("BankerAvatarID");
            if (string.IsNullOrEmpty(value) || !UUID.TryParse(value, out UUID bankerID))
                return UUID.Zero;

            return bankerID;
        }

        public bool Transfer(UUID toID, UUID fromID, int amount, string description, int transactionType, UUID transactionID)
        {
            if (amount < 0)
                return false;

            UUID bankerID = GetBankerAvatarID();
            if (bankerID != UUID.Zero)
            {
                if (toID == UUID.Zero)
                    toID = bankerID;
                if (fromID == UUID.Zero)
                    fromID = bankerID;
            }

            int? fromBalance = null;
            int? toBalance = null;

            if (fromID != UUID.Zero && fromID == toID)
            {
                // Same account on both sides - most commonly the Banker
                // Avatar making a purchase or triggering a fee themselves,
                // since the substitution above can turn a UUID.Zero
                // counterparty into the same avatar who's also the real
                // other side of the transfer (found live: a resident who
                // is also the configured banker buying something with
                // ConfluenceCurrency). Net balance change is genuinely
                // zero. Reading GetBalance independently for "from" and
                // "to" (the normal path below) both read the same
                // pre-write balance and both write back to the same row -
                // the second write silently clobbers the first, which
                // inflated the balance by the full transferred amount
                // instead of leaving it unchanged. Still requires the
                // account to actually hold the amount (same "can't spend
                // more than you have" rule as a real transfer) and still
                // records a real ledger row for audit history - just with
                // no actual balance movement, both sides written as the
                // same unchanged value.
                int currentBalance = m_Database.GetBalance(fromID);
                if (currentBalance < amount)
                    return false; // insufficient funds

                fromBalance = currentBalance;
                toBalance = currentBalance;
            }
            else
            {
                if (fromID != UUID.Zero)
                {
                    int currentFromBalance = m_Database.GetBalance(fromID);
                    if (currentFromBalance < amount)
                        return false; // insufficient funds

                    fromBalance = currentFromBalance - amount;
                }

                if (toID != UUID.Zero)
                    toBalance = m_Database.GetBalance(toID) + amount;
            }

            CurrencyTransfer transfer = new CurrencyTransfer
            {
                ID = transactionID == UUID.Zero ? UUID.Random() : transactionID,
                ToAgent = toID,
                FromAgent = fromID,
                Amount = amount,
                TransferType = transactionType,
                Description = description ?? string.Empty,
                TransferDate = DateTime.UtcNow,
                ToBalance = toBalance ?? 0,
                FromBalance = fromBalance ?? 0
            };

            // Balance changes and the ledger record are applied together in
            // one DB transaction (ApplyTransfer) - if the ledger insert
            // fails (e.g. a reused transaction ID hitting the TransactionID
            // primary key), the balance changes roll back too, instead of
            // leaving currency moved with no corresponding record. Found
            // live: ProcessObjectBuy reusing an object's own UUID as the
            // transaction ID caused exactly this on a repeat purchase. See
            // PROJECT_LOG.md.
            try
            {
                m_Database.ApplyTransfer(fromID, fromBalance, toID, toBalance, transfer);
            }
            catch (Exception e)
            {
                m_log.Error($"[CURRENCY SERVICE]: Transfer failed and was rolled back ({e.Message})");
                return false;
            }

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
            if (!CheckPurchaseCaps(agentID, realAmountHundredths))
                return false;

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

        // Rolling windows ending now, not calendar day/week/month boundaries -
        // "$500 in the last 24 hours" rather than "$500 since midnight",
        // which can't be reset early by timing a purchase just after a
        // boundary. A cap of 0 disables that particular check.
        private bool CheckPurchaseCaps(UUID agentID, int newAmountHundredths)
        {
            DateTime now = DateTime.UtcNow;

            if (!CheckOneCap(agentID, newAmountHundredths, now.AddDays(-1), now, m_dailyCapHundredths, "daily"))
                return false;
            if (!CheckOneCap(agentID, newAmountHundredths, now.AddDays(-7), now, m_weeklyCapHundredths, "weekly"))
                return false;
            if (!CheckOneCap(agentID, newAmountHundredths, now.AddDays(-30), now, m_monthlyCapHundredths, "monthly"))
                return false;

            return true;
        }

        private bool CheckOneCap(UUID agentID, int newAmountHundredths, DateTime windowStart, DateTime windowEnd, int capHundredths, string label)
        {
            if (capHundredths <= 0)
                return true;

            List<CurrencyPurchase> history = m_Database.GetPurchaseHistory(agentID, windowStart, windowEnd, null, null);
            int spentHundredths = 0;
            foreach (CurrencyPurchase p in history)
                spentHundredths += p.RealAmount;

            if (spentHundredths + newAmountHundredths > capHundredths)
            {
                m_log.WarnFormat("[CURRENCY SERVICE]: Purchase of ${0:0.00} by {1} rejected - would exceed {2} cap of ${3:0.00} (already spent ${4:0.00} in that window)",
                        newAmountHundredths / 100.0, agentID, label, capHundredths / 100.0, spentHundredths / 100.0);
                return false;
            }

            return true;
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

        public int GetTotalCirculation()
        {
            return m_Database.GetTotalCirculation();
        }

        public int CountAccountsWithBalance()
        {
            return m_Database.CountAccountsWithBalance();
        }

        public List<CurrencyBalanceEntry> GetTopBalances(int count)
        {
            return m_Database.GetTopBalances(count);
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
