using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Timers;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.Handlers.Currency
{
    // Robust-hosted viewer currency-buy surface. Registered here rather than
    // per-region: the viewer only ever learns ONE grid-wide "economy" URL, from
    // Robust's [GridInfo] economy setting, fixed at login regardless of which
    // region the avatar is currently in. Registering getCurrencyQuote/buyCurrency
    // per-region (as ConfluenceCurrencyModule alone does) breaks the instant a grid
    // has more than one region behind that single URL - this is what actually
    // answers it, with a stable address. ConfluenceCurrencyModule keeps its own
    // region-local copies too so a region can still validate land buys etc.
    // without a round trip to Robust; both read/write the same ICurrencyService
    // tables, so they agree.
    public class CurrencyServiceConnector : ServiceConnector
    {
        private ICurrencyService m_CurrencyService;
        private IGridUserService m_GridUserService;
        private IGridService m_GridService;
        private IUserAccountService m_UserAccountService;
        private int m_currencyRate;

        // Stipend economy (WhiteCore-Dev's ScheduledPayments.cs ported the
        // cadence math and payment loop from - see PROJECT_LOG.md Batch 14).
        // Hosted here rather than a separate connector since it needs exactly
        // the services this class already loads (Currency/UserAccount/GridUser),
        // and grid-wide scheduled payments are a Robust-level concern, not
        // anything region-specific.
        private readonly Timer m_stipendTimer = new Timer();
        private bool m_payStipends;
        private int m_stipendAmount;
        private string m_stipendPeriod = "w";
        private int m_stipendInterval = 1;
        private string m_stipendPayDay = "mo";
        private string m_stipendPayTime = "09:00";
        private bool m_stipendsLoginRequired;
        private int m_stipendLoginWindowDays = 7;
        private DateTime m_nextStipendPayment;

        public CurrencyServiceConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            IConfig serverConfig = config.Configs["CurrencyService"];
            if (serverConfig == null)
                throw new Exception("No section CurrencyService in config file");

            string service = serverConfig.GetString("LocalServiceModule", String.Empty);
            if (service == String.Empty)
                throw new Exception("LocalServiceModule not present in CurrencyService config section");

            Object[] args = new Object[] { config };
            m_CurrencyService = ServerUtils.LoadPlugin<ICurrencyService>(service, args);

            // Needed only to find which region to call back after a purchase - see
            // NotifyRegionOfBalanceChange. Reuses Robust's own already-configured
            // [GridUserService]/[GridService] sections rather than requiring
            // duplicate config.
            IConfig gridUserConfig = config.Configs["GridUserService"];
            if (gridUserConfig != null)
            {
                string gridUserServiceDll = gridUserConfig.GetString("LocalServiceModule", String.Empty);
                if (gridUserServiceDll != String.Empty)
                    m_GridUserService = ServerUtils.LoadPlugin<IGridUserService>(gridUserServiceDll, args);
            }

            IConfig gridConfig = config.Configs["GridService"];
            if (gridConfig != null)
            {
                string gridServiceDll = gridConfig.GetString("LocalServiceModule", String.Empty);
                if (gridServiceDll != String.Empty)
                    m_GridService = ServerUtils.LoadPlugin<IGridService>(gridServiceDll, args);
            }

            IConfig userAccountConfig = config.Configs["UserAccountService"];
            if (userAccountConfig != null)
            {
                string userAccountServiceDll = userAccountConfig.GetString("LocalServiceModule", String.Empty);
                if (userAccountServiceDll != String.Empty)
                    m_UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(userAccountServiceDll, args);
            }

            IConfig economyConfig = config.Configs["Economy"];
            m_currencyRate = economyConfig != null ? economyConfig.GetInt("CurrencyRate", 10) : 10;

            InitStipendConfig(economyConfig);

            server.AddXmlRPCHandler("getCurrencyQuote", HandleGetCurrencyQuote);
            server.AddXmlRPCHandler("buyCurrency", HandleBuyCurrency);
            server.AddXmlRPCHandler("preflightBuyLandPrep", HandlePreflightBuyLandPrep);
            server.AddXmlRPCHandler("buyLandPrep", HandleBuyLandPrep);

            // The above only ever fires for requests to the bare root path ("/") -
            // BaseHttpServer.HandleRequest special-cases request.UriPath == "/" as
            // the only place XML-RPC method dispatch happens; everything else falls
            // through to path-keyed stream-handler lookup instead. But the real
            // viewer protocol (see LLCurrencyUIManager::Impl::startTransaction in
            // the viewer source) always POSTs to <helper_uri>currency.php, never to
            // the bare helper_uri - so the handlers above never actually fire for a
            // real client. Found this by testing against the actual path a real
            // viewer requests, not just the bare root. Fixed by also registering an
            // explicit stream handler at "/currency.php" that runs the same request
            // through the framework's own XML-RPC dispatcher (BaseHttpServer already
            // has a public overload that takes a handler dictionary for exactly this
            // kind of non-root case) with just these four methods.
            Dictionary<string, XmlRpcMethod> currencyPhpHandlers = new Dictionary<string, XmlRpcMethod>
            {
                { "getCurrencyQuote", HandleGetCurrencyQuote },
                { "buyCurrency", HandleBuyCurrency },
                { "preflightBuyLandPrep", HandlePreflightBuyLandPrep },
                { "buyLandPrep", HandleBuyLandPrep }
            };

            server.AddSimpleStreamHandler(new SimpleStreamHandler("/currency.php",
                (httpRequest, httpResponse) =>
                {
                    if (server is BaseHttpServer baseServer
                            && httpRequest is OSHttpRequest osRequest
                            && httpResponse is OSHttpResponse osResponse)
                    {
                        baseServer.HandleXmlRpcRequests(osRequest, osResponse, currencyPhpHandlers);
                    }
                }));
        }

        private int EstimatedCostHundredths(int amount)
        {
            if (m_currencyRate <= 0)
                return 0;
            return (int)Math.Round((amount / (float)m_currencyRate) * 100.0);
        }

        private XmlRpcResponse HandleGetCurrencyQuote(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            Hashtable quoteResponse = new Hashtable();
            XmlRpcResponse response = new XmlRpcResponse();

            if (requestData.ContainsKey("agentId") && requestData.ContainsKey("currencyBuy"))
            {
                int amount = 0;
                try { amount = Convert.ToInt32(requestData["currencyBuy"]); }
                catch (Exception) { }

                Hashtable currency = new Hashtable
                {
                    { "estimatedCost", EstimatedCostHundredths(amount) },
                    { "currencyBuy", amount }
                };

                quoteResponse["success"] = true;
                quoteResponse["currency"] = currency;
                quoteResponse["confirm"] = UUID.Random().ToString();
                response.Value = quoteResponse;
                return response;
            }

            quoteResponse["success"] = false;
            quoteResponse["errorMessage"] = "Invalid parameters passed to the quote box";
            quoteResponse["errorURI"] = string.Empty;
            response.Value = quoteResponse;
            return response;
        }

        private XmlRpcResponse HandleBuyCurrency(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            Hashtable result = new Hashtable();
            XmlRpcResponse response = new XmlRpcResponse();

            UUID agentId;
            int amount = 0;
            if (requestData.ContainsKey("agentId") && UUID.TryParse(requestData["agentId"].ToString(), out agentId)
                    && requestData.ContainsKey("currencyBuy"))
            {
                try { amount = Convert.ToInt32(requestData["currencyBuy"]); }
                catch (Exception) { }

                if (amount > 0 && m_CurrencyService.RecordPurchase(agentId, amount, EstimatedCostHundredths(amount),
                        remoteClient != null ? remoteClient.Address.ToString() : string.Empty))
                {
                    NotifyRegionOfBalanceChange(agentId);
                    result["success"] = true;
                    response.Value = result;
                    return response;
                }
            }

            result["success"] = false;
            response.Value = result;
            return response;
        }

        // Robust credited the purchase, but has no direct connection to the
        // viewer to push a balance update through - without this the balance HUD
        // stays stale until the user manually clicks it (see PROJECT_LOG.md,
        // Batch 12). Finds whichever region currently has this agent via
        // GridUserService's last-known-region tracking, then calls back into
        // that region's own ConfluenceCurrencyModule ("UpdateBalance" - see there)
        // to do the actual client push, since only the region process holds the
        // live client connection.
        private void NotifyRegionOfBalanceChange(UUID agentId)
        {
            if (m_GridUserService == null || m_GridService == null)
                return;

            try
            {
                GridUserInfo userInfo = m_GridUserService.GetGridUserInfo(agentId.ToString());
                if (userInfo == null || !userInfo.Online || userInfo.LastRegionID == UUID.Zero)
                    return;

                OpenSim.Services.Interfaces.GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, userInfo.LastRegionID);
                if (region == null || string.IsNullOrEmpty(region.ServerURI))
                    return;

                Hashtable callParams = new Hashtable { { "agentId", agentId.ToString() } };
                ArrayList sendParams = new ArrayList { callParams };
                XmlRpcRequest updateRequest = new XmlRpcRequest("UpdateBalance", sendParams);
                updateRequest.Send(region.ServerURI, 10000);
            }
            catch (Exception)
            {
                // Best-effort - the purchase itself already succeeded and was
                // recorded; the user can still manually refresh their balance if
                // this notification doesn't get through.
            }
        }

        private XmlRpcResponse HandlePreflightBuyLandPrep(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable result = new Hashtable
            {
                { "success", true },
                { "currency", new Hashtable { { "estimatedCost", 0 } } },
                { "membership", new Hashtable { { "id", UUID.Zero.ToString() }, { "description", "Membership" } } },
                { "landuse", new Hashtable() },
                { "confirm", UUID.Random().ToString() }
            };
            response.Value = result;
            return response;
        }

        private XmlRpcResponse HandleBuyLandPrep(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            response.Value = new Hashtable { { "success", true } };
            return response;
        }

        #region Stipend economy

        private void InitStipendConfig(IConfig economyConfig)
        {
            if (economyConfig == null)
                return;

            m_payStipends = economyConfig.GetBoolean("PayStipends", false);
            m_stipendAmount = economyConfig.GetInt("StipendAmount", 0);
            m_stipendPeriod = economyConfig.GetString("StipendPeriod", m_stipendPeriod);
            m_stipendInterval = economyConfig.GetInt("StipendInterval", m_stipendInterval);
            m_stipendPayDay = economyConfig.GetString("StipendPayDay", m_stipendPayDay).ToLowerInvariant();
            m_stipendPayTime = economyConfig.GetString("StipendPayTime", m_stipendPayTime);
            m_stipendsLoginRequired = economyConfig.GetBoolean("StipendsLoginRequired", false);
            m_stipendLoginWindowDays = economyConfig.GetInt("StipendLoginWindowDays", m_stipendLoginWindowDays);

            m_payStipends &= m_stipendAmount > 0 && m_CurrencyService != null && m_UserAccountService != null;
            if (!m_payStipends)
                return;

            m_nextStipendPayment = GetStipendPaytime(0);
            m_stipendTimer.Interval = Math.Max(60, economyConfig.GetInt("StipendCheckIntervalSeconds", 300)) * 1000;
            m_stipendTimer.Elapsed += StipendTimerElapsed;
            m_stipendTimer.Enabled = true;

            if (MainConsole.Instance != null)
            {
                MainConsole.Instance.Commands.AddCommand("Currency", false,
                        "stipend info",
                        "stipend info",
                        "Show stipend payment configuration and the next scheduled run.",
                        HandleStipendInfo);

                MainConsole.Instance.Commands.AddCommand("Currency", false,
                        "stipend paynow",
                        "stipend paynow",
                        "Run the stipend payment cycle immediately, out of schedule.",
                        HandleStipendPayNow);
            }
        }

        // Ported from WhiteCore-Dev's ScheduledPayments.cs (PaymentCycleDays/
        // PayDayOfWeek/GetStipendPaytime) - the cadence math itself had no
        // DataManager coupling at all, it's pure date arithmetic.
        private int PaymentCycleDays()
        {
            int periodMult = (m_stipendPeriod.Length > 0 ? m_stipendPeriod[0] : 'w') switch
            {
                'd' => 1,
                'w' => 7,
                'm' => 30,
                'y' => 365,
                _ => 7
            };

            if (m_stipendInterval < 1)
                m_stipendInterval = 1;

            return periodMult * m_stipendInterval;
        }

        private int PayDayOfWeek()
        {
            if (m_stipendPayDay.Length < 2)
                return (int)DateTime.Now.DayOfWeek;

            return m_stipendPayDay.Substring(0, 2) switch
            {
                "su" => 0,
                "mo" => 1,
                "tu" => 2,
                "we" => 3,
                "th" => 4,
                "fr" => 5,
                "sa" => 6,
                _ => (int)DateTime.Now.DayOfWeek
            };
        }

        private DateTime GetStipendPaytime(int minutesOffset)
        {
            int payHour = 9, payMinute = 0;
            string[] parts = m_stipendPayTime.Split(':');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out payHour);
                int.TryParse(parts[1], out payMinute);
            }

            int paydayDow = PayDayOfWeek();
            DateTime today = DateTime.Now;
            int todayDow = (int)today.DayOfWeek;
            int cycleDays = PaymentCycleDays();

            if (paydayDow < todayDow)
                paydayDow += cycleDays;

            double dayOffset = paydayDow - todayDow;
            DateTime nextPayTime = (today.Date + new TimeSpan(payHour, payMinute + minutesOffset, 0)).AddDays(dayOffset);

            while (nextPayTime < DateTime.Now)
                nextPayTime = nextPayTime.AddDays(cycleDays);

            return nextPayTime;
        }

        private void StipendTimerElapsed(object sender, ElapsedEventArgs e)
        {
            m_stipendTimer.Enabled = false;

            if (DateTime.Now.AddSeconds(m_stipendTimer.Interval / 2000.0) > m_nextStipendPayment)
                ProcessStipendPayments();

            m_stipendTimer.Enabled = true;
        }

        // Universal stipend payment: every account gets paid on a schedule.
        // Simplified from WhiteCore's original in one deliberate way - paid
        // with fromID=UUID.Zero through ICurrencyService.Transfer, which
        // Confluence's own ledger already treats as "system-generated credit, no
        // counterparty" (see SetBalance/RecordPurchase). WhiteCore instead paid
        // out of a literal "Banker" account UUID and had its own commented-out,
        // never-finished TODO to reconcile that account back to zero - Confluence's
        // ledger already has the concept WhiteCore was working around, so there's
        // no equivalent bookkeeping debt here.
        private void ProcessStipendPayments()
        {
            List<UserAccount> accounts = m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1");
            int paid = 0;

            foreach (UserAccount account in accounts)
            {
                if (m_stipendsLoginRequired && m_GridUserService != null)
                {
                    GridUserInfo userInfo = m_GridUserService.GetGridUserInfo(account.PrincipalID.ToString());
                    if (userInfo == null || userInfo.Login < DateTime.UtcNow.AddDays(-m_stipendLoginWindowDays))
                        continue;
                }

                if (m_CurrencyService.Transfer(account.PrincipalID, UUID.Zero, m_stipendAmount,
                        "Stipend payment", 5 /* next after ConfluenceTransactionType.LandSale=4, see ConfluenceCurrencyModule.cs - no shared enum crosses the Robust/region project boundary today */,
                        UUID.Zero))
                    paid++;
            }

            m_nextStipendPayment = GetStipendPaytime(0);
        }

        private void HandleStipendInfo(string module, string[] args)
        {
            MainConsole.Instance.Output(m_payStipends
                    ? $"Stipends enabled: {m_stipendAmount} every {m_stipendInterval} {m_stipendPeriod}(s), next payment {m_nextStipendPayment:f}"
                    : "Stipends are not enabled ([Economy] PayStipends/StipendAmount).");
        }

        private void HandleStipendPayNow(string module, string[] args)
        {
            if (!m_payStipends)
            {
                MainConsole.Instance.Output("Stipends are not enabled.");
                return;
            }

            ProcessStipendPayments();
            MainConsole.Instance.Output($"Stipend payment cycle run. Next scheduled payment: {m_nextStipendPayment:f}");
        }

        #endregion Stipend economy
    }
}
