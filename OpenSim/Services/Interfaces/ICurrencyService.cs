using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces
{
    // Native, DB-backed currency ledger. Modeled on the ledger shape WhiteCore-Dev's
    // BaseCurrencyConnector proved out (balance, transfer, transaction/purchase
    // history), but re-implemented against Confluence's own Data/Services/Connectors
    // layering (see IExperienceData/ExperienceService) so this stays a plain OpenSim
    // service plugin rather than depending on WhiteCore's own DataManager. The
    // region-edge ConfluenceCurrencyModule adapts this to the existing IMoneyModule
    // contract so every current call site (land buy, upload charges, llGiveMoney,
    // etc.) keeps working unchanged regardless of which IMoneyModule is selected.
    public interface ICurrencyService
    {
        int GetBalance(UUID agentID);

        // Direct administrative set (console "money set"), not a transfer - no
        // counterparty, no transaction record beyond the adjustment itself.
        int SetBalance(UUID agentID, int amount, string description);

        bool Transfer(UUID toID, UUID fromID, int amount, string description, int transactionType, UUID transactionID);

        uint NumberOfTransactions(UUID toAgentID, UUID fromAgentID);

        List<CurrencyTransfer> GetTransactionHistory(UUID toAgentID, UUID fromAgentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count);

        // agentID == UUID.Zero means "don't filter by principal" - a
        // grid-wide feed, for the WebInterface financial reporting page.
        uint NumberOfPurchases(UUID agentID);

        List<CurrencyPurchase> GetPurchaseHistory(UUID agentID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count);

        // Records a real-money currency-buy event (viewer "buyCurrency" flow) and
        // credits the resulting balance in one step.
        bool RecordPurchase(UUID agentID, int amount, int realAmountHundredths, string ip);

        // Thin, explicitly-named wrappers over the agent-shaped API above - the
        // underlying ledger (currency_balances/currency_transactions) has no
        // schema-level distinction between an agent UUID and a group UUID at
        // all (no foreign key to useraccounts), so GetBalance/Transfer already
        // work correctly if handed a group's UUID today. These exist purely to
        // give callers a clear, self-documenting group-currency API instead of
        // relying on that undocumented fact. Added per the WhiteCore-Dev
        // re-audit's finding that Confluence's currency service had no
        // group-balance concept at all (WhiteCore's BaseCurrencyServiceModule
        // has GetGroupBalance/GroupCurrencyTransfer) - a prerequisite for
        // porting group liability/dividend payments (stipend economy).
        int GetGroupBalance(UUID groupID);

        // payingIntoGroup=true: agentID -> groupID (e.g. a member funding the
        // group). payingIntoGroup=false: groupID -> agentID (e.g. a dividend
        // payout). Same insufficient-funds/failure semantics as Transfer -
        // callers are responsible for checking the acting agent actually has
        // group financial permission before calling this; the ledger itself
        // has no concept of group roles.
        bool GroupCurrencyTransfer(UUID groupID, UUID agentID, int amount, string description, int transactionType, UUID transactionID, bool payingIntoGroup);

        // Unlike GetTransactionHistory (which ANDs toAgentID/fromAgentID when
        // both are non-zero), this returns every transfer where the group was
        // EITHER side - a group's activity page needs to show money paid in
        // and money paid out, not just one direction.
        List<CurrencyTransfer> GetGroupTransactionHistory(UUID groupID, DateTime dateStart, DateTime dateEnd, uint? start, uint? count);

        // Divides the group's current balance evenly among memberIDs and pays
        // each one out, leaving any integer-division remainder in the group
        // balance for next time (matches WhiteCore's own dividend algorithm).
        // Returns the per-member amount paid (0 if the balance/member count
        // made a payout impossible - e.g. balance is 0, or less than 1 per
        // member). Deliberately takes memberIDs as a parameter rather than
        // discovering them itself: this service has no dependency on the
        // Groups subsystem at all (by design, same as the rest of this
        // interface), so the caller - wherever group membership/role data is
        // actually available - is responsible for deciding who's accountable.
        int PayGroupDividend(UUID groupID, List<UUID> memberIDs, string description);

        // Grid-wide economy summary for the public Economy dashboard - see
        // ICurrencyData for the design rationale.
        int GetTotalCirculation();

        int CountAccountsWithBalance();

        List<CurrencyBalanceEntry> GetTopBalances(int count);
    }
}
