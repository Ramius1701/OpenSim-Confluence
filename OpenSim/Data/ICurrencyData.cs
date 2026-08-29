using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Data
{
    public interface ICurrencyData
    {
        // Returns 0 (and creates the row) if the agent has no balance yet.
        int GetBalance(UUID agentID);

        // Persists an absolute balance for an agent, creating the row if needed.
        void SetBalance(UUID agentID, int amount);

        void AddTransaction(CurrencyTransfer transfer);

        // Atomically applies up to two balance changes and the ledger record
        // for a transfer in a single DB transaction, so a failure partway
        // through (e.g. a duplicate TransactionID primary key) rolls back
        // the balance changes too, instead of leaving currency moved with no
        // corresponding ledger entry. Pass null for a side that shouldn't be
        // touched (e.g. a system credit with no "from" account).
        void ApplyTransfer(UUID fromID, int? newFromBalance, UUID toID, int? newToBalance, CurrencyTransfer transfer);

        uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID);

        List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count);

        void AddPurchase(CurrencyPurchase purchase);

        uint NumberOfPurchases(UUID agentID);

        List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count);

        // Grid-wide economy summary for the public Economy dashboard - real
        // DB-side aggregates (SUM/COUNT), not a loop over every account
        // calling GetBalance individually.
        int GetTotalCirculation();

        int CountAccountsWithBalance();

        // Highest balances first, excluding zero balances (system/never-
        // funded accounts) - same filter OpenSim-Grid-Interface's own
        // economy.php leaderboard query uses.
        List<CurrencyBalanceEntry> GetTopBalances(int count);

        /// <summary>
        /// Remove this agent's balance row, transaction ledger entries (as
        /// either party), and purchase history - for decommissioning an
        /// account, not a routine zero-out (use SetBalance for that).
        /// </summary>
        void DeleteAccountData(UUID agentID);
    }
}
