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
    }
}
