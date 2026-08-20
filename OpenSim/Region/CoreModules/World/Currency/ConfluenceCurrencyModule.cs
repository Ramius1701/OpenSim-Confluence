using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.World.Currency
{
    // Local transaction-type tags for this module's own ledger records - not a
    // shared/framework enum (OpenSim has no single canonical one; every existing
    // money module, e.g. SampleMoneyModule's own TransactionType, defines its own).
    // Never parsed by the viewer, so exact numbering doesn't need to match SL's.
    public enum ConfluenceTransactionType : int
    {
        SystemGenerated = 0,
        ObjectPays = 1,
        UploadCharge = 2,
        MoveMoney = 3,
        LandSale = 4,
        ObjectSale = 5
    }


    // Region-edge protocol adapter for the native ICurrencyService ledger (see
    // OpenSim/Services/CurrencyService). Implements the existing IMoneyModule
    // contract - the same one DTLNSLMoneyModule/Gloebit implement - so every
    // existing call site (land buy, upload charges, llGiveMoney, etc.) keeps
    // working unchanged regardless of which one is selected, and answers the
    // getCurrencyQuote/buyCurrency/preflightBuyLandPrep/buyLandPrep XML-RPC
    // surface documented on the OpenSimulator wiki as what a viewer's currency
    // display actually calls (the same surface WhiteCore-Dev's own currency
    // stub registers). No separate MoneyServer process required.
    //
    // To enable (disabled by default - DTLNSLMoneyModule/Gloebit/whatever is
    // already configured keeps working untouched until this is opted into):
    //
    //   [Modules]
    //       CurrencyService = LocalCurrencyServiceConnector
    //
    //   [CurrencyService]
    //       LocalServiceModule = "OpenSim.Services.CurrencyService.dll:CurrencyService"
    //       StorageProvider = "OpenSim.Data.MySQL.dll:MySqlCurrencyData"
    //       ConnectionString = "<same connection string as the rest of the grid's MySQL>"
    //
    //   [Economy]
    //       EconomyModule = ConfluenceCurrencyModule
    //       economymodule = ConfluenceCurrencyModule
    //       PriceUpload = 0
    //       PriceGroupCreate = 0
    //       CurrencyRate = 10
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ConfluenceCurrencyModule")]
    public class ConfluenceCurrencyModule : IMoneyModule, ISharedRegionModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // True only when [Economy] EconomyModule actually names this module - same
        // selector key DTLNSLMoneyModule/Gloebit already read, so operators pick
        // exactly one active money module the same way they always have.
        private bool m_isSelectedEconomyModule = false;
        private static bool m_xmlRpcHandlersRegistered = false;

        private IConfigSource m_config;
        private ICurrencyService m_currency;
        private List<Scene> m_Scenes = new List<Scene>();

        private int m_uploadCharge = 0;
        private int m_groupCreationCharge = 0;
        private int m_currencyRate = 10; // in-world units per real-currency unit, matches MoneyServer's CalculateCurrency

        public event ObjectPaid OnObjectPaid;

        #region ISharedRegionModule

        public Type ReplaceableInterface { get { return null; } }
        public string Name { get { return "ConfluenceCurrencyModule"; } }

        public void Initialise(IConfigSource config)
        {
            m_config = config;

            IConfig economyConfig = config.Configs["Economy"];
            if (economyConfig == null || economyConfig.GetString("EconomyModule") != Name)
            {
                // Not the configured [Economy] EconomyModule (e.g. DTLNSLMoneyModule or
                // Gloebit is selected instead) - stay completely inert. Registering
                // OnValidateLandBuy/OnLandBuy/IMoneyModule here regardless would silently
                // take over money handling out from under whichever module IS selected.
                return;
            }

            m_isSelectedEconomyModule = true;

            m_uploadCharge = economyConfig.GetInt("PriceUpload", 0);
            m_groupCreationCharge = economyConfig.GetInt("PriceGroupCreate", 0);
            m_currencyRate = economyConfig.GetInt("CurrencyRate", 10);
        }

        public void PostInitialise() { }
        public void Close() { }

        public void AddRegion(Scene scene)
        {
            if (!m_isSelectedEconomyModule)
                return;

            // Only register ourselves here (matching DTLNSLMoneyModule's own
            // convention of registering IMoneyModule during AddRegion) - actually
            // resolving ICurrencyService has to wait for RegionLoaded, since
            // LocalCurrencyServiceConnector registers it in its own AddRegion and
            // module load order across different modules isn't guaranteed within
            // the same AddRegion pass. RegionLoaded only fires once every module's
            // AddRegion has completed, so it's the safe place to depend on another
            // module's interface.
            scene.RegisterModuleInterface<IMoneyModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_isSelectedEconomyModule)
                return;

            m_currency = scene.RequestModuleInterface<ICurrencyService>();
            if (m_currency == null)
            {
                m_log.Error("[CONFLUENCE CURRENCY]: No ICurrencyService available - is [Modules] CurrencyService "
                        + "and [CurrencyService] LocalServiceModule configured? This module cannot function "
                        + "without it.");
                m_isSelectedEconomyModule = false;
                return;
            }

            lock (m_Scenes)
                m_Scenes.Add(scene);

            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnValidateLandBuy += ValidateLandBuy;
            scene.EventManager.OnLandBuy += ProcessLandBuy;

            RegisterXmlRpcHandlers();
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_isSelectedEconomyModule)
                return;

            lock (m_Scenes)
                m_Scenes.Remove(scene);

            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnValidateLandBuy -= ValidateLandBuy;
            scene.EventManager.OnLandBuy -= ProcessLandBuy;
        }

        // The XML-RPC handlers are process-wide (MainServer.Instance), not
        // per-scene - guard against re-registering once per region.
        private void RegisterXmlRpcHandlers()
        {
            if (m_xmlRpcHandlersRegistered)
                return;
            m_xmlRpcHandlersRegistered = true;

            MainServer.Instance.AddXmlRPCHandler("getCurrencyQuote", HandleGetCurrencyQuote);
            MainServer.Instance.AddXmlRPCHandler("buyCurrency", HandleBuyCurrency);
            MainServer.Instance.AddXmlRPCHandler("preflightBuyLandPrep", HandlePreflightBuyLandPrep);
            MainServer.Instance.AddXmlRPCHandler("buyLandPrep", HandleBuyLandPrep);

            // Callback target for CurrencyServiceConnector (Robust) after a
            // buyCurrency purchase completes there - Robust has no direct client
            // connection to push a balance update through, so it looks up which
            // region currently has this agent and calls back here. Root path is
            // fine (unlike the viewer-facing methods above): only Robust calls
            // this, never the viewer.
            MainServer.Instance.AddXmlRPCHandler("UpdateBalance", HandleUpdateBalance);

            // The above only fires for requests to the bare root path ("/") -
            // BaseHttpServer.HandleRequest special-cases request.UriPath == "/" as
            // the only place XML-RPC method dispatch happens. The real viewer
            // protocol always POSTs to <helper_uri>currency.php, never the bare
            // helper_uri (see LLCurrencyUIManager::Impl::startTransaction in the
            // viewer source) - so for a standalone deployment using this module
            // directly (no separate Robust), also register an explicit stream
            // handler at "/currency.php" running the same request through the
            // framework's own dispatcher with just these four methods.
            Dictionary<string, XmlRpcMethod> currencyPhpHandlers = new Dictionary<string, XmlRpcMethod>
            {
                { "getCurrencyQuote", HandleGetCurrencyQuote },
                { "buyCurrency", HandleBuyCurrency },
                { "preflightBuyLandPrep", HandlePreflightBuyLandPrep },
                { "buyLandPrep", HandleBuyLandPrep }
            };

            MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/currency.php",
                (httpRequest, httpResponse) =>
                {
                    if (MainServer.Instance is BaseHttpServer baseServer
                            && httpRequest is OSHttpRequest osRequest
                            && httpResponse is OSHttpResponse osResponse)
                    {
                        baseServer.HandleXmlRpcRequests(osRequest, osResponse, currencyPhpHandlers);
                    }
                }));

            // Firestorm's OpenSim compatibility patch ("COLOSI opensim
            // multi-currency support" in llfloaterbuyland.cpp) posts
            // preflightBuyLandPrep/buyLandPrep to <helper_uri>landtool.php,
            // not currency.php - only getCurrencyQuote/buyCurrency use
            // currency.php. Without this, Buy Land hangs forever on
            // "(waiting for data)" with zero trace in any server log, since
            // the request hits a dead path rather than failing in our code.
            // Same handler set as currency.php; only the path differs.
            MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/landtool.php",
                (httpRequest, httpResponse) =>
                {
                    if (MainServer.Instance is BaseHttpServer baseServer
                            && httpRequest is OSHttpRequest osRequest
                            && httpResponse is OSHttpResponse osResponse)
                    {
                        baseServer.HandleXmlRpcRequests(osRequest, osResponse, currencyPhpHandlers);
                    }
                }));
        }

        #endregion ISharedRegionModule

        #region Client event wiring

        private void OnNewClient(IClientAPI client)
        {
            client.OnEconomyDataRequest += EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest += SendMoneyBalanceHandler;
            client.OnMoneyTransferRequest += ProcessMoneyTransferRequest;
            client.OnObjectBuy += ProcessObjectBuy;
            client.OnRequestPayPrice += ProcessRequestPayPrice;
            client.OnCompleteMovementToRegion += OnCompleteMovementToRegion;
            client.OnLogout += OnClientLoggedOut;
        }

        private void OnClientLoggedOut(IClientAPI client)
        {
            client.OnEconomyDataRequest -= EconomyDataRequestHandler;
            client.OnMoneyBalanceRequest -= SendMoneyBalanceHandler;
            client.OnMoneyTransferRequest -= ProcessMoneyTransferRequest;
            client.OnObjectBuy -= ProcessObjectBuy;
            client.OnRequestPayPrice -= ProcessRequestPayPrice;
            client.OnCompleteMovementToRegion -= OnCompleteMovementToRegion;
            client.OnLogout -= OnClientLoggedOut;
        }

        // GloebitMoneyModule pushes a fresh balance on every OnCompleteMovementToRegion
        // (covers login AND crossing/teleport in from a region running a different
        // economy module) specifically because the viewer otherwise only re-queries
        // balance on login or a manual click - found live testing a mixed-economy grid
        // (some regions on real Gloebit, others on this module): crossing from a Gloebit
        // region into a ConfluenceCurrencyModule one kept showing the stale Gloebit
        // balance until manually refreshed. This module never had the same push.
        private void OnCompleteMovementToRegion(IClientAPI client, bool blah)
        {
            int balance = GetBalance(client.AgentId);
            client.SendMoneyBalance(UUID.Zero, true, Utils.EmptyBytes, balance,
                    0, UUID.Zero, false, UUID.Zero, false, 0, string.Empty);
        }

        // Answers the viewer's "what are this object's configured Pay-dialog quick-pick
        // amounts" query (the values set via llSetPayPrice) with SendPayPrice. Both other
        // selectable currency modules in this repo (DTLNSLMoneyModule, GloebitMoneyModule)
        // already implement this; this native module never had, until now - found by
        // systematically diffing every client event those two wire up against this
        // module's own subscriptions after the OnObjectBuy gap turned up the same way.
        private void ProcessRequestPayPrice(IClientAPI remoteClient, UUID objectID)
        {
            if (remoteClient.Scene is not Scene scene)
                return;

            SceneObjectPart part = scene.GetSceneObjectPart(objectID);
            if (part == null)
                return;

            SceneObjectGroup group = part.ParentGroup;
            if (group == null || group.IsDeleted)
                return;

            remoteClient.SendPayPrice(objectID, group.RootPart.PayPrice);
        }

        private void EconomyDataRequestHandler(IClientAPI client)
        {
            client.SendEconomyData(0f, client.Scene.RegionInfo.ObjectCapacity, 0, 0,
                    m_groupCreationCharge, 0, 0f, 0f, 0, 0f, 0, 0, 0, 0, m_uploadCharge, 0, 0f);
        }

        private void SendMoneyBalanceHandler(IClientAPI client, UUID agentID, UUID sessionID, UUID transactionID)
        {
            if (client.AgentId != agentID || client.SessionId != sessionID)
            {
                client.SendAlertMessage("Unable to send your money balance to you!");
                return;
            }

            int balance = GetBalance(agentID);
            client.SendMoneyBalance(transactionID, true, Utils.EmptyBytes, balance,
                    0, UUID.Zero, false, UUID.Zero, false, 0, string.Empty);
        }

        private void ProcessMoneyTransferRequest(UUID sourceID, UUID destID, int amount, int transactionType, string description)
        {
            if (m_currency.Transfer(destID, sourceID, amount, description, transactionType, UUID.Zero))
            {
                PushBalanceUpdate(sourceID);
                PushBalanceUpdate(destID);
            }
        }

        // The classic OpenSim pattern: BuySellModule (right-click "Buy" on a for-sale
        // object) handles delivery only and has no currency awareness at all - a real
        // money module is expected to independently subscribe to this same client event
        // and charge for the sale before delivery happens. DTLNSLMoneyModule and
        // GloebitMoneyModule (this repo's other selectable currency modules) already do
        // this; this native module never had, until now - meaning object purchases were
        // silently free under it regardless of price. See PROJECT_LOG.md.
        private void ProcessObjectBuy(IClientAPI remoteClient, UUID agentID, UUID sessionID, UUID groupID,
                UUID categoryID, uint localID, byte saleType, int salePrice)
        {
            if (remoteClient.AgentId != agentID || remoteClient.SessionId != sessionID || salePrice < 0)
                return;

            if (remoteClient.Scene is not Scene scene)
                return;

            SceneObjectPart part = scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                remoteClient.SendAgentAlertMessage("Unable to buy now. The object was not found", false);
                return;
            }

            SceneObjectGroup group = part.ParentGroup;
            if (group == null || group.IsDeleted || group.inTransit)
                return;

            SceneObjectPart rootPart = group.RootPart;

            // Trust the object's own server-side sale terms (set via ObjectSaleInfo, which
            // is itself permission-checked), not whatever the requesting viewer claims - a
            // modified client could otherwise ask to pay less than the actual listed price.
            if (rootPart.ObjectSaleType == 0 || saleType != rootPart.ObjectSaleType || salePrice != rootPart.SalePrice)
            {
                remoteClient.SendAgentAlertMessage("Unable to buy now. This item doesn't appear to be for sale", false);
                return;
            }

            UUID sellerID = rootPart.OwnerID;

            if (salePrice > 0)
            {
                if (GetBalance(agentID) < salePrice)
                {
                    remoteClient.SendAgentAlertMessage("Unable to buy now. You don't have sufficient funds", false);
                    return;
                }

                // transactionID is UUID.Zero, not group.UUID: the object's own UUID is
                // stable across repeat purchases (e.g. a for-sale copy bought twice), but
                // CurrencyTransfer.ID/TransactionID is a MySQL primary key - reusing it
                // caused a duplicate-key failure on the second purchase of the same
                // object. Zero lets CurrencyService.Transfer generate a fresh random ID
                // per call, same as every other transaction type already does. See
                // PROJECT_LOG.md.
                if (!m_currency.Transfer(sellerID, agentID, salePrice, "Object purchase: " + rootPart.Name,
                        (int)ConfluenceTransactionType.ObjectSale, UUID.Zero))
                {
                    remoteClient.SendAgentAlertMessage("Unable to buy now. Payment failed", false);
                    return;
                }

                PushBalanceUpdate(agentID);
                PushBalanceUpdate(sellerID);
            }

            IBuySellModule buySell = scene.RequestModuleInterface<IBuySellModule>();
            if (buySell != null && buySell.BuyObject(remoteClient, categoryID, localID, saleType, salePrice))
                return;

            // Delivery failed after payment already succeeded - refund immediately rather
            // than leaving the buyer charged for nothing.
            if (salePrice > 0)
            {
                if (m_currency.Transfer(agentID, sellerID, salePrice,
                        "Object purchase refund (delivery failed): " + rootPart.Name,
                        (int)ConfluenceTransactionType.ObjectSale, UUID.Zero))
                {
                    PushBalanceUpdate(agentID);
                    PushBalanceUpdate(sellerID);
                }
                else
                {
                    m_log.Error(
                        $"[CONFLUENCE CURRENCY]: Object purchase delivery failed for {rootPart.Name} ({group.UUID}) " +
                        $"after payment succeeded, and the refund ALSO failed - buyer {agentID} was charged " +
                        $"{salePrice} with nothing delivered. Needs manual reconciliation.");
                }
            }

            remoteClient.SendAgentAlertMessage(
                "Unable to buy now. Delivery failed" + (salePrice > 0 ? " - you have been refunded" : ""), false);
        }

        // Real SL/OpenSim behavior: the server pushes an unsolicited balance
        // update after any transaction completes, rather than waiting for the
        // viewer to ask - without this, the balance HUD only refreshes when the
        // user manually clicks it. Only pushes if the agent has a connected
        // client in one of this process's regions; the Robust-hosted purchase
        // path (CurrencyServiceConnector) can't reach the client directly, so it
        // calls back into whichever region actually has them via the
        // "UpdateBalance" XML-RPC method below instead.
        private void PushBalanceUpdate(UUID agentID)
        {
            if (agentID == UUID.Zero)
                return;

            List<Scene> scenes;
            lock (m_Scenes)
                scenes = new List<Scene>(m_Scenes);

            foreach (Scene scene in scenes)
            {
                ScenePresence sp = scene.GetScenePresence(agentID);
                if (sp != null && !sp.IsChildAgent && !sp.IsDeleted)
                {
                    int balance = GetBalance(agentID);
                    sp.ControllingClient.SendMoneyBalance(UUID.Random(), true, Utils.EmptyBytes, balance,
                            0, UUID.Zero, false, UUID.Zero, false, 0, string.Empty);
                    return;
                }
            }
        }

        #endregion Client event wiring

        #region Land buy

        private void ValidateLandBuy(Object sender, EventManager.LandBuyArgs e)
        {
            if (GetBalance(e.agentId) >= e.parcelPrice)
                e.economyValidated = true;
        }

        private void ProcessLandBuy(Object sender, EventManager.LandBuyArgs e)
        {
            if (!e.economyValidated || e.transactionID != 0)
                return;

            e.transactionID = Util.UnixTimeSinceEpoch();

            if (m_currency.Transfer(e.parcelOwnerID, e.agentId, e.parcelPrice, "Land Purchase",
                    (int)ConfluenceTransactionType.LandSale, UUID.Zero))
            {
                e.amountDebited = e.parcelPrice;
                PushBalanceUpdate(e.agentId);
                PushBalanceUpdate(e.parcelOwnerID);
            }
        }

        #endregion Land buy

        #region IMoneyModule

        public int UploadCharge { get { return m_uploadCharge; } }
        public int GroupCreationCharge { get { return m_groupCreationCharge; } }

        public int GetBalance(UUID agentID)
        {
            return m_currency.GetBalance(agentID);
        }

        public bool UploadCovered(UUID agentID, int amount)
        {
            return GetBalance(agentID) >= amount;
        }

        public bool AmountCovered(UUID agentID, int amount)
        {
            return GetBalance(agentID) >= amount;
        }

        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string reason)
        {
            bool result = m_currency.Transfer(toID, fromID, amount, "Object payment",
                    (int)ConfluenceTransactionType.ObjectPays, txn);

            reason = result ? string.Empty : "Insufficient funds";

            if (result)
            {
                PushBalanceUpdate(fromID);
                PushBalanceUpdate(toID);
                OnObjectPaid?.Invoke(objectID, fromID, amount);
            }

            return result;
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData = "")
        {
            if (m_currency.Transfer(UUID.Zero, agentID, amount, extraData, (int)type, UUID.Zero))
                PushBalanceUpdate(agentID);
        }

        public void ApplyUploadCharge(UUID agentID, int amount, string text)
        {
            if (m_currency.Transfer(UUID.Zero, agentID, amount, text, (int)ConfluenceTransactionType.UploadCharge, UUID.Zero))
                PushBalanceUpdate(agentID);
        }

        public void MoveMoney(UUID fromUser, UUID toUser, int amount, string text)
        {
            if (m_currency.Transfer(toUser, fromUser, amount, text, (int)ConfluenceTransactionType.MoveMoney, UUID.Zero))
            {
                PushBalanceUpdate(fromUser);
                PushBalanceUpdate(toUser);
            }
        }

        public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
        {
            bool result = m_currency.Transfer(toUser, fromUser, amount, text, (int)type, UUID.Zero);
            if (result)
            {
                PushBalanceUpdate(fromUser);
                PushBalanceUpdate(toUser);
            }
            return result;
        }

        #endregion IMoneyModule

        #region Viewer currency-buy XML-RPC surface

        // "currency.php" equivalent - see http://opensimulator.org/wiki/Webinterface
        // and OpenSim's FAQ: a viewer's currency display hits this XML-RPC surface at
        // whatever URL [GridInfo] economy points to. No real payment gateway is wired
        // in here - like the classic MoneyServer, this credits the requested amount
        // directly (a self-hosted grid trusting its own buy button). Operators who
        // need real payment processing can use RegionWeb's own PayPal-backed
        // wallet donations, or Gloebit, unaffected by this module being selected or not.

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

        // Failure path must set errorMessage/errorURI, not just success=false -
        // confirmed against the real viewer source (LLCurrencyUIManager::Impl::
        // finishCurrencyBuy, llcurrencyuimanager.cpp): on failure it reads
        // result["errorMessage"]/result["errorURI"] unconditionally. LLSD
        // tolerates the missing keys (empty string, no crash), but the resident
        // saw a blank failure dialog instead of a real reason - same
        // errorMessage/errorURI shape HandleGetCurrencyQuote's failure path
        // already uses below.
        private XmlRpcResponse HandleBuyCurrency(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            Hashtable result = new Hashtable();
            XmlRpcResponse response = new XmlRpcResponse();

            UUID agentId;
            int amount = 0;
            string errorMessage = "Unable to process this purchase.";

            if (!requestData.ContainsKey("agentId") || !UUID.TryParse(requestData["agentId"].ToString(), out agentId))
            {
                errorMessage = "Invalid parameters passed to the purchase.";
            }
            else if (!requestData.ContainsKey("currencyBuy"))
            {
                errorMessage = "Invalid parameters passed to the purchase.";
            }
            else
            {
                try { amount = Convert.ToInt32(requestData["currencyBuy"]); }
                catch (Exception) { }

                if (amount <= 0)
                {
                    errorMessage = "Invalid purchase amount.";
                }
                else if (m_currency.RecordPurchase(agentId, amount, EstimatedCostHundredths(amount),
                        remoteClient != null ? remoteClient.Address.ToString() : string.Empty))
                {
                    PushBalanceUpdate(agentId);
                    result["success"] = true;
                    response.Value = result;
                    return response;
                }
                else
                {
                    errorMessage = "Could not record this purchase. Please try again.";
                }
            }

            result["success"] = false;
            result["errorMessage"] = errorMessage;
            result["errorURI"] = string.Empty;
            response.Value = result;
            return response;
        }

        private XmlRpcResponse HandleUpdateBalance(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            Hashtable requestData = (Hashtable)request.Params[0];
            Hashtable result = new Hashtable();
            XmlRpcResponse response = new XmlRpcResponse();

            UUID agentId;
            if (requestData.ContainsKey("agentId") && UUID.TryParse(requestData["agentId"].ToString(), out agentId))
            {
                PushBalanceUpdate(agentId);
                result["success"] = true;
            }
            else
            {
                result["success"] = false;
            }

            response.Value = result;
            return response;
        }

        private int EstimatedCostHundredths(int amount)
        {
            if (m_currencyRate <= 0)
                return 0;
            return (int)Math.Round((amount / (float)m_currencyRate) * 100.0);
        }

        // Response shape confirmed against the real viewer source
        // (LLFloaterBuyLandUI::finishWebSiteInfo, llfloaterbuyland.cpp), not
        // guessed: it reads result["landUse"] (capital U - our previous
        // "landuse" was a silent no-op, LLSD map keys are case-sensitive)
        // and result["membership"]["upgrade"]/["action"]/["levels"] (an
        // array of {id, description} objects), not a bare id/description
        // pair directly under "membership". Both explicitly say "no
        // upgrade needed" here since Confluence's currency has no SL-style
        // paid membership tiers or land-use-fee upsell to offer - the
        // previous shape happened to reach the same safe default via LLSD's
        // undefined-value fallbacks, but only by accident, not by design.
        private XmlRpcResponse HandlePreflightBuyLandPrep(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable result = new Hashtable
            {
                { "success", true },
                { "currency", new Hashtable { { "estimatedCost", 0 } } },
                { "membership", new Hashtable
                    {
                        { "upgrade", false },
                        { "action", string.Empty },
                        { "levels", new ArrayList() }
                    }
                },
                { "landUse", new Hashtable { { "upgrade", false }, { "action", string.Empty } } },
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

        #endregion Viewer currency-buy XML-RPC surface
    }
}
