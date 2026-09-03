using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MimeKit;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.MarketplaceService;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.Handlers.WebInterface
{
    // Native grid-wide web/admin UI - the WhiteCore-Dev-inspired piece of the
    // Batch 12 currency work's sibling effort (see PROJECT_LOG.md, "Addon-modules
    // -> core consolidation"). Hosted on Robust for the same reason
    // CurrencyServiceConnector is: a grid-wide feature needs ONE stable address,
    // not a per-region one - RegionWeb (addon-modules/RegionWeb) is deliberately
    // left alone as the per-region alternative, and OpenSim-Grid-Interface remains
    // the user's own separate, optional, swappable grid-wide tool.
    //
    // v1 scope: login against real grid accounts + a dashboard showing the
    // currency balance built in Batch 12. Chosen as the first page because it
    // proves the whole stack (auth, session, page rendering) end to end rather
    // than shipping an empty shell, and it's a natural capstone on already-working
    // code rather than a disconnected new feature.
    //
    // To enable:
    //   [ServiceList] (or [Startup])
    //       WebInterfaceServiceConnector = "${Const|PublicPort}/OpenSim.Server.Handlers.dll:WebInterfaceServiceConnector"
    public class WebInterfaceServiceConnector : ServiceConnector
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string BasePath = "";
        private const string SessionCookieName = "ConfluenceWebSession";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);


        private class WebSession
        {
            public UUID PrincipalID;
            public string Name;
            public bool IsAdmin;
            public DateTime Expires;

            // The linked portal account, if any (UUID.Zero = this avatar
            // has no linked WebAccount yet). PrincipalID keeps meaning
            // exactly what it always has - "the currently active avatar" -
            // this is the one new field the multi-avatar model needed;
            // every existing PrincipalID-scoped page stays correct as long
            // as login/switching keep it pointed at the right avatar.
            public UUID WebAccountID;
        }

        private readonly ConcurrentDictionary<string, WebSession> m_sessions = new ConcurrentDictionary<string, WebSession>();

        // Password-reset tokens - same shape/expiry-check pattern as WebSession
        // above, just keyed by a token handed to the user via email rather than
        // a session cookie. Deliberately short-lived (1 hour): unlike a login
        // session, this token alone is enough to set a new password.
        private class ResetToken
        {
            public UUID PrincipalID;
            public DateTime Expires;
        }

        private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
        private readonly ConcurrentDictionary<string, ResetToken> m_resetTokens = new ConcurrentDictionary<string, ResetToken>();

        // Create Avatar's pending-signup token. Deliberately holds the
        // plaintext password (needed to become the real hashed avatar
        // password via IAuthenticationService.SetPassword once the link is
        // clicked, same as HandleRegister does today) - in-memory only,
        // never written to disk, lost on a Robust restart during the
        // window. Same accepted tradeoff as WebSession/ResetToken above,
        // just a longer window (48h, matching the reference) and a riskier
        // payload, which is why it's called out explicitly here rather than
        // left implicit.
        private class AvatarSignupToken
        {
            public UUID WebAccountID;
            public string FirstName;
            public string LastName;
            public string Email;
            public string Password;
            public UUID HomeRegionID;
            public DateTime Expires;
        }

        private static readonly TimeSpan AvatarSignupTokenLifetime = TimeSpan.FromHours(48);
        private readonly ConcurrentDictionary<string, AvatarSignupToken> m_avatarSignupTokens = new ConcurrentDictionary<string, AvatarSignupToken>();

        private bool m_smtpEnabled;
        private string m_smtpHost = string.Empty;
        private int m_smtpPort = 587;
        private bool m_smtpTls = true;
        private string m_smtpLogin = string.Empty;
        private string m_smtpPassword = string.Empty;
        private MailboxAddress m_smtpFrom;

        private IUserAccountService m_UserAccountService;
        private IAuthenticationService m_AuthenticationService;
        private ICurrencyService m_CurrencyService;
        private IGridService m_GridService;
        private IGridUserService m_GridUserService;
        private IRegionHGService m_RegionHGService;
        private IEstateDataService m_EstateDataService;
        private IAbuseReportsService m_AbuseReportsService;
        private IInventoryService m_InventoryService;
        private IAvatarService m_AvatarService;
        private INewsService m_NewsService;
        private IEventsService m_EventsService;
        private ISupportTicketService m_SupportTicketService;
        private IStaticPageService m_StaticPageService;
        private IWebAccountService m_WebAccountService;
        private ISuggestionService m_SuggestionService;
        private IRecoveryCodeService m_RecoveryCodeService;
        private IGridSettingsService m_GridSettingsService;
        private IUserProfilesService m_UserProfilesService;
        private IFriendsService m_FriendsService;
        private ISearchService m_SearchService;
        private IGroupsSearchProvider m_GroupsSearchService;
        private IAuctionService m_AuctionService;
        private IOfflineIMService m_OfflineIMService;
        private IMessagingService m_MessagingService;
        private IStoreService m_StoreService;
        private IMarketplaceListingsService m_MarketplaceListingsService;
        private IDeliveryLedger m_MarketplaceLedger;
        private UUID m_marketplaceServiceAccountId = UUID.Zero;
        // Separate from m_purchasesInProgress (Store) - an in-progress Store
        // purchase and an in-progress Marketplace purchase by the same
        // avatar are unrelated events and shouldn't block each other.
        private readonly Dictionary<UUID, bool> m_marketplacePurchasesInProgress = new Dictionary<UUID, bool>();
        // See STORE_PURCHASE_TRANSACTION_TYPE - picked clear of it and of
        // the real OpenMetaverse.MoneyTransactionType values.
        private const int MARKETPLACE_PURCHASE_TRANSACTION_TYPE = 5002;
        private OpenSim.Services.StoreService.Gloebit.GloebitClient m_GloebitClient;
        private bool m_gloebitEnabled = false;
        private int m_regionOrderPortStart, m_regionOrderPortEnd;
        private int m_regionOrderGridXStart, m_regionOrderGridYStart, m_regionOrderGridXEnd, m_regionOrderGridYEnd;
        private string m_regionOrderTemplateIniPath = string.Empty;
        private string m_regionOrderGridRoot = string.Empty;
        private string m_regionOrderExternalHostName = string.Empty;
        // Per-avatar purchase lock - rejects a second concurrent "Buy"
        // click from the same avatar before it ever reaches
        // ICurrencyService.Transfer, which is not itself safe against
        // concurrent double-spend (see PROJECT_LOG.md). Same TryAdd-then-
        // remove-in-finally shape as EntityTransferStateMachine.SetInTransit.
        private readonly Dictionary<UUID, bool> m_purchasesInProgress = new Dictionary<UUID, bool>();
        // Avatar -> the order they were mid-checkout on when a Gloebit
        // purchase needed a fresh OAuth2 authorize round-trip. Resumed
        // once /store/gloebit/auth_complete reports success. In-memory
        // only (same lifetime posture as WebSession/ResetToken) - lost on
        // a Robust restart mid-authorize, which just means the resident
        // re-clicks Buy, same as any other interrupted checkout.
        private readonly Dictionary<UUID, UUID> m_pendingGloebitOrders = new Dictionary<UUID, UUID>();
        // ICurrencyService.Transfer's transactionType is a raw int with no
        // enum in this repo - 0 is already double-booked (admin balance-set
        // and currency-buy-credit both use it) and the real
        // OpenMetaverse.MoneyTransactionType values used elsewhere in this
        // codebase top out well under 100, so this is picked clear of both.
        private const int STORE_PURCHASE_TRANSACTION_TYPE = 5001;
        private string m_webConsoleSecret = string.Empty;

        private string m_gridName = "OpenSim Grid";
        private string m_gridNick = "OpenSim";
        private string m_welcomeMessage = "";
        private string m_publicBaseUrl = string.Empty;
        // The same [LoginService] Currency value co-operative viewers already
        // read at login for the currency HUD label - was hardcoded as the
        // literal "C$" in ~20 places across this file (predating the Store
        // feature); centralized here so the portal can never drift out of
        // sync with what residents actually see in-world again.
        private string m_currencySymbol = "C$";

        public WebInterfaceServiceConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            Object[] args = new Object[] { config };

            m_UserAccountService = LoadReusedPlugin<IUserAccountService>(config, "UserAccountService", args);
            m_AuthenticationService = LoadReusedPlugin<IAuthenticationService>(config, "AuthenticationService", args);
            m_CurrencyService = LoadReusedPlugin<ICurrencyService>(config, "CurrencyService", args);
            m_GridService = LoadReusedPlugin<IGridService>(config, "GridService", args);
            m_GridUserService = LoadReusedPlugin<IGridUserService>(config, "GridUserService", args);
            m_RegionHGService = LoadReusedPlugin<IRegionHGService>(config, "RegionHGService", args);
            m_EstateDataService = LoadReusedPlugin<IEstateDataService>(config, "EstateService", args);
            m_AbuseReportsService = LoadReusedPlugin<IAbuseReportsService>(config, "AbuseReportsService", args);
            m_InventoryService = LoadReusedPlugin<IInventoryService>(config, "InventoryService", args);
            m_AvatarService = LoadReusedPlugin<IAvatarService>(config, "AvatarService", args);
            m_NewsService = LoadReusedPlugin<INewsService>(config, "NewsService", args);
            m_EventsService = LoadReusedPlugin<IEventsService>(config, "EventsService", args);
            // Same [AuctionService] LocalServiceModule the region-side
            // LocalAuctionServiceConnector/AuctionModule reuses - land
            // auctions have no in-world bidding UI at all (confirmed
            // against Firestorm's real llfloaterauction.cpp), so this page
            // IS the bidding UI, not just a status display.
            m_AuctionService = LoadReusedPlugin<IAuctionService>(config, "AuctionService", args);
            // Not LoadReusedPlugin - [Messaging]'s key for this plugin is
            // "OfflineIMService", not the "LocalServiceModule" key every
            // other section here uses (confirmed against Robust.HG.ini -
            // the same section HGInstantMessageService/OfflineIMServiceRobustConnector
            // already read from), so the generic helper can't find it.
            IConfig messagingConfig = config.Configs["Messaging"];
            if (messagingConfig != null)
            {
                string offlineImDll = messagingConfig.GetString("OfflineIMService", string.Empty);
                if (!string.IsNullOrEmpty(offlineImDll))
                    m_OfflineIMService = ServerUtils.LoadPlugin<IOfflineIMService>(offlineImDll, args);
            }
            m_MessagingService = LoadReusedPlugin<IMessagingService>(config, "MessagingService", args);
            m_SupportTicketService = LoadReusedPlugin<ISupportTicketService>(config, "SupportTicketService", args);
            m_StaticPageService = LoadReusedPlugin<IStaticPageService>(config, "StaticPageService", args);
            m_WebAccountService = LoadReusedPlugin<IWebAccountService>(config, "WebAccountService", args);
            m_SuggestionService = LoadReusedPlugin<ISuggestionService>(config, "SuggestionService", args);
            m_RecoveryCodeService = LoadReusedPlugin<IRecoveryCodeService>(config, "RecoveryCodeService", args);
            m_GridSettingsService = LoadReusedPlugin<IGridSettingsService>(config, "GridSettingsService", args);
            // Same [UserProfilesService] LocalServiceModule the region-side
            // LocalUserProfilesServiceConnector already reuses - backs the
            // splash page's "Featured Classifieds" widget with the real
            // `classifieds` table users already populate via their
            // viewer's own Profile > Classifieds tab (stock OpenSim
            // functionality, not something this session had to build).
            // Unlike every other LoadReusedPlugin call here, its concrete
            // implementation (OpenSim.Services.ProfilesService.
            // UserProfilesService) takes a 2-arg (IConfigSource, string
            // configName) constructor, not the 1-arg (IConfigSource) shape
            // the rest of this file's reused plugins use - confirmed
            // against LocalUserProfilesServiceConnector's own
            // `new object[] { source, ConfigName }` call, the real,
            // already-working consumer of this same service. Passing the
            // shared 1-arg `args` here throws a reflection "constructor
            // not found" at startup instead of loading.
            m_FriendsService = LoadReusedPlugin<IFriendsService>(config, "FriendsService", args);
            // Same [SearchService] the region-side ConfluenceSearchModule
            // already queries for the in-world Places directory floater -
            // backs the People/Places/Events/Classifieds/Groups
            // /web/search page.
            m_SearchService = LoadReusedPlugin<ISearchService>(config, "SearchService", args);
            // See IGroupsSearchProvider - loads OpenSim.Addons.Groups'
            // plain GroupsService class directly (not through the
            // ISharedRegionModule connector wrappers, which need a live
            // Scene that Robust doesn't have).
            m_GroupsSearchService = LoadReusedPlugin<IGroupsSearchProvider>(config, "GroupsSearchService", args);

            IConfig userProfilesSection = config.Configs["UserProfilesService"];
            if (userProfilesSection != null)
            {
                string userProfilesDll = userProfilesSection.GetString("LocalServiceModule", string.Empty);
                if (!string.IsNullOrEmpty(userProfilesDll))
                    m_UserProfilesService = ServerUtils.LoadPlugin<IUserProfilesService>(userProfilesDll, new object[] { config, "UserProfilesService" });
            }

            IConfig gridInfo = config.Configs["GridInfoService"];
            if (gridInfo != null)
            {
                m_gridName = gridInfo.GetString("gridname", m_gridName);
                m_gridNick = gridInfo.GetString("gridnick", m_gridNick);
                // Same "login" URI value get_grid_info already hands viewers -
                // it's already the grid's own canonical public BaseURL:PublicPort,
                // so password-reset links use the exact same host a user's
                // viewer already points at, rather than a separately-configured URL.
                m_publicBaseUrl = gridInfo.GetString("login", string.Empty).TrimEnd('/');
            }

            IConfig loginService = config.Configs["LoginService"];
            if (loginService != null)
            {
                // [LoginService] WelcomeMessage supports a <USERNAME> token,
                // correctly substituted by LLLoginService's own post-login
                // message once a real login attempt has happened. This
                // pre-login field feeds the WebUI's own generic welcome text
                // (rendered before any login attempt, where no username can
                // ever be known) - strip the token AND its usual surrounding
                // comma, not just the bare token, which used to leave a
                // dangling ", !" visible instead of a clean sentence.
                m_welcomeMessage = loginService.GetString("WelcomeMessage", string.Empty)
                        .Replace(", <USERNAME>", string.Empty)
                        .Replace("<USERNAME>, ", string.Empty)
                        .Replace("<USERNAME>", string.Empty);
                m_currencySymbol = loginService.GetString("Currency", m_currencySymbol);
                if (string.IsNullOrWhiteSpace(m_currencySymbol))
                    m_currencySymbol = "C$";
            }

            // Reuses the exact same [SMTP] section/keys the region-side
            // EmailModule.cs (llEmail's backend) already reads - same config,
            // same MailKit connect/authenticate/send pattern, just invoked
            // from Robust instead of a simulator. Not shared code (that
            // module lives in a different project, aimed at LSL, with
            // per-owner/per-address throttling this feature doesn't need),
            // but deliberately the same config surface so an operator who
            // already has SMTP configured for in-world email doesn't need a
            // second, differently-named section for this.
            IConfig smtpConfig = config.Configs["SMTP"];
            if (smtpConfig != null && smtpConfig.GetBoolean("enabled", false))
            {
                m_smtpHost = smtpConfig.GetString("SMTP_SERVER_HOSTNAME", string.Empty);
                m_smtpPort = smtpConfig.GetInt("SMTP_SERVER_PORT", 587);
                m_smtpTls = smtpConfig.GetBoolean("SMTP_SERVER_TLS", true);
                m_smtpLogin = smtpConfig.GetString("SMTP_SERVER_LOGIN", string.Empty);
                m_smtpPassword = smtpConfig.GetString("SMTP_SERVER_PASSWORD", string.Empty);

                string smtpFrom = smtpConfig.GetString("SMTP_SERVER_FROM", string.Empty);
                if (!string.IsNullOrEmpty(m_smtpHost) && MailboxAddress.TryParse(smtpFrom, out m_smtpFrom))
                    m_smtpEnabled = true;
            }

            m_StoreService = LoadReusedPlugin<IStoreService>(config, "StoreService", args);

            IConfig storeConfig = config.Configs["StoreService"];
            if (storeConfig != null)
            {
                m_regionOrderPortStart = storeConfig.GetInt("RegionOrderPortRangeStart", 9050);
                m_regionOrderPortEnd = storeConfig.GetInt("RegionOrderPortRangeEnd", 9099);
                m_regionOrderGridXStart = storeConfig.GetInt("RegionOrderGridXStart", 1050);
                m_regionOrderGridYStart = storeConfig.GetInt("RegionOrderGridYStart", 1050);
                m_regionOrderGridXEnd = storeConfig.GetInt("RegionOrderGridXEnd", 1099);
                m_regionOrderGridYEnd = storeConfig.GetInt("RegionOrderGridYEnd", 1099);
                m_regionOrderTemplateIniPath = storeConfig.GetString("RegionOrderTemplateIniPath", string.Empty);
                m_regionOrderGridRoot = storeConfig.GetString("RegionOrderGridRoot", string.Empty);
                m_regionOrderExternalHostName = storeConfig.GetString("RegionOrderExternalHostName", string.Empty);
            }

            // Native DirectDelivery marketplace - listing metadata/stock
            // (MarketplaceListingsService) and delivery idempotency
            // (IDeliveryLedger) both come from the same [MarketplaceService]
            // LocalServiceModule; the concrete class implements both, and
            // LoadReusedPlugin creates a fresh instance per call (confirmed
            // via ServerUtils.LoadPlugin - no cross-call instance caching),
            // so this is two lightweight instances, not a double-connect -
            // every data-layer call opens/closes its own MySqlConnection
            // regardless. m_InventoryService/m_UserAccountService (loaded
            // above) are reused directly for delivery - no Scene needed, see
            // MarketplaceInventoryOperations.Deliver's own comment for why.
            m_MarketplaceListingsService = LoadReusedPlugin<IMarketplaceListingsService>(config, "MarketplaceService", args);
            m_MarketplaceLedger = LoadReusedPlugin<IDeliveryLedger>(config, "MarketplaceService", args);
            IConfig marketplaceConfig = config.Configs["MarketplaceService"];
            if (marketplaceConfig != null)
                UUID.TryParse(marketplaceConfig.GetString("ServiceAccountUUID", string.Empty).Trim(), out m_marketplaceServiceAccountId);

            // Store's own Gloebit integration - Robust-native, independent of
            // addon-modules/Gloebit/GloebitMoneyModule (region-Scene-bound,
            // see PROJECT_LOG.md). Reuses the same GLBKey/GLBSecret/
            // GLBEnvironment already configured for the grid's region-side
            // Gloebit integration, copied (not shared/read) into this
            // section - does not touch Gloebit.ini in any way.
            IConfig gloebitConfig = config.Configs["Gloebit"];
            if (gloebitConfig != null && gloebitConfig.GetBoolean("Enabled", false))
            {
                string glbKey = gloebitConfig.GetString("GLBKey", string.Empty);
                string glbSecret = gloebitConfig.GetString("GLBSecret", string.Empty);
                string glbApiUrl = gloebitConfig.GetString("GLBApiUrl", "https://www.gloebit.com/");
                string glbCallbackBaseUri = gloebitConfig.GetString("GLBCallbackBaseURI", string.Empty);
                if (!string.IsNullOrEmpty(glbKey) && !string.IsNullOrEmpty(glbSecret) && !string.IsNullOrEmpty(glbCallbackBaseUri))
                {
                    m_GloebitClient = new OpenSim.Services.StoreService.Gloebit.GloebitClient(glbKey, glbSecret, glbApiUrl, glbCallbackBaseUri);
                    m_gloebitEnabled = true;
                }
            }

            // Same [WebConsole] SharedSecret every region's own WebConsoleModule
            // is configured with - see that module's own security-note comment
            // for why an empty/missing secret disables the feature entirely
            // rather than falling back to "no auth."
            IConfig webConsoleConfig = config.Configs["WebConsole"];
            if (webConsoleConfig != null)
                m_webConsoleSecret = webConsoleConfig.GetString("SharedSecret", string.Empty);

            // BasePath used to be "/web", one prefix registered once. Losing
            // that prefix (routes live at bare /search, /login, etc. now)
            // ran into a real constraint in BaseHttpServer.TryGetSimpleStreamHandler:
            // its varPath lookup only extracts a prefix when the path has a
            // SECOND slash (uripath.IndexOf('/', 2)) - a single-segment path
            // like "/search" has no second slash to find, so it can never
            // match a varPath-registered prefix no matter what that prefix
            // is. A single shared "" prefix (the naive equivalent of the old
            // "/web") therefore can't work as a catch-all here - each
            // distinct top-level segment has to be registered individually,
            // same exact+varPath pair "/web" used to need, so both the bare
            // route and its sub-paths (e.g. /admin and /admin/users) resolve.
            string[] topLevelRoutes =
            {
                "/dashboard", "/login", "/register", "/viewers", "/destinations", "/worldmap", "/gridstatus", "/economy", "/features",
                "/support", "/search", "/landsearch", "/admin", "/profile", "/friends",
                "/change-password", "/change-email", "/transactions", "/myclassifieds",
                "/myevents", "/forgot-password", "/reset-password", "/logout",
                "/myregions", "/myland", "/myinventory", "/page", "/partner", "/myestates", "/delete-account",
                "/offline-messages", "/messages",
                "/help", "/guide", "/static", "/auctions", "/welcome-photos",
                // Multi-avatar accounts (see WebSession.WebAccountID) - the
                // first avatar you register/log in with IS the master
                // account, no separate portal credential.
                "/create-avatar", "/verify-avatar", "/import-avatar", "/my-avatars", "/switch-avatar",
                "/suggestion-box", "/recovery-codes", "/recover-account",
                // Store: prim-capacity packs + self-service region ordering.
                "/store",
                // Native DirectDelivery marketplace - browse/buy (public) and
                // a merchant's own listing management (the edit_url
                // destination DirectDeliveryModule's viewer cap points at).
                "/marketplace"
            };
            foreach (string route in topLevelRoutes)
            {
                server.AddSimpleStreamHandler(new SimpleStreamHandler(route, HandleRequest));
                server.AddSimpleStreamHandler(new SimpleStreamHandler(route, HandleRequest), true);
            }

            // The in-viewer login splash screen (see [GridInfoService] "welcome" -
            // a viewer fetches exactly this filename). Unauthenticated.
            server.AddSimpleStreamHandler(new SimpleStreamHandler("/welcome.php", HandleRequest));

            // Bare "/" is NOT handled through AddSimpleStreamHandler's path table at
            // all - BaseHttpServer.HandleRequest special-cases request.UriPath == "/"
            // before it ever reaches that lookup, and only AddStreamHandler's older
            // IRequestHandler API (which sets a distinct m_RootDefaultGET slot) can
            // answer it. Found this the same way as the currency.php routing bug in
            // Batch 12: registered it the "normal" way first, got a 404, then read
            // BaseHttpServer.HandleRequest to see why. RootHomeHandler below adapts
            // that older API to the same HandleHome method.
            server.AddStreamHandler(new RootHomeHandler(HandleHome));
        }

        // Thin adapter from the older IStreamedRequestHandler API (the only one
        // that can claim the root-GET slot) to the plain (request, response)
        // delegate shape everything else here uses.
        private class RootHomeHandler : BaseStreamHandler
        {
            private readonly Action<IOSHttpRequest, IOSHttpResponse> m_handler;

            public RootHomeHandler(Action<IOSHttpRequest, IOSHttpResponse> handler) : base("GET", "/")
            {
                m_handler = handler;
            }

            protected override byte[] ProcessRequest(string path, System.IO.Stream request,
                    IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            {
                m_handler(httpRequest, httpResponse);
                return httpResponse.RawBuffer;
            }
        }

        // Reuses whatever [SectionName] LocalServiceModule Robust is already
        // configured with for its own services, rather than requiring the
        // operator to duplicate config - same technique CurrencyServiceConnector
        // uses for GridUserService/GridService.
        private static T LoadReusedPlugin<T>(IConfigSource config, string sectionName, Object[] args) where T : class
        {
            IConfig section = config.Configs[sectionName];
            if (section == null)
                return null;

            string dll = section.GetString("LocalServiceModule", string.Empty);
            if (string.IsNullOrEmpty(dll))
                return null;

            return ServerUtils.LoadPlugin<T>(dll, args);
        }

        // Grid settings editor (task #25) - a handful of values an admin can
        // change live from the web UI without a Robust restart, overriding
        // the .ini-configured default when present. Deliberately just these
        // few keys rather than exposing the whole config file - the ones
        // that already had a hardcoded ini-only value somewhere else on this
        // connector (m_gridName/m_gridNick/m_welcomeMessage) plus one new
        // real behavioral toggle (AllowRegistration), not a general-purpose
        // settings dumping ground.
        private string GetSetting(string key, string defaultValue)
        {
            if (m_GridSettingsService == null)
                return defaultValue;

            string value = m_GridSettingsService.Get(key);
            return value ?? defaultValue;
        }

        // Every WritePage/WriteAdaptivePage title across this connector used
        // to hardcode the literal "Confluence Grid" instead of the
        // operator's actual configured name - harmless-looking, but every
        // browser tab on every real deployment (not just this one) said
        // "Confluence Grid" regardless of what grid it actually was. Same
        // GetSetting("GridName", m_gridName) lookup the page bodies
        // themselves already used correctly.
        private string PageTitle(string suffix)
        {
            return GetSetting("GridName", m_gridName) + " - " + suffix;
        }

        // [LoginService] WelcomeMessage supports a <USERNAME> token, correctly
        // substituted by LLLoginService's own post-login message
        // (LLLoginService.cs's ProcessLogin, where the real username is
        // already known - a login attempt has actually happened by then).
        // These pre-login WebUI pages (the splash panel shown inside the
        // viewer's own login screen, and its full-site equivalents) read the
        // same shared setting for their own generic welcome text, rendered
        // before any login attempt, where no username can ever be known -
        // strip the token (and its usual surrounding comma) rather than
        // showing it unresolved as a literal tag or a blank gap.
        private string GetWebSafeWelcomeMessage()
        {
            string message = GetSetting("WelcomeMessage", m_welcomeMessage);
            if (string.IsNullOrEmpty(message))
                return message;

            return message.Replace(", <USERNAME>", string.Empty)
                    .Replace("<USERNAME>, ", string.Empty)
                    .Replace("<USERNAME>", string.Empty);
        }

        private void HandleRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            string path = request.RawUrl ?? "/";
            int query = path.IndexOf('?');
            if (query >= 0)
                path = path.Substring(0, query);
            path = path.TrimEnd('/');
            if (path.Length == 0)
                path = "/";

            try
            {
                switch (path)
                {
                    case "/":
                        HandleHome(request, response);
                        break;
                    case "/welcome.php":
                        HandleWelcome(request, response);
                        break;
                    case BasePath + "/dashboard":
                        HandleDashboard(request, response);
                        break;
                    case BasePath + "/login":
                        HandleLogin(request, response);
                        break;
                    case BasePath + "/register":
                        HandleRegister(request, response);
                        break;
                    case BasePath + "/create-avatar":
                        HandleCreateAvatar(request, response);
                        break;
                    case BasePath + "/verify-avatar":
                        HandleVerifyAvatar(request, response);
                        break;
                    case BasePath + "/import-avatar":
                        HandleImportAvatar(request, response);
                        break;
                    case BasePath + "/my-avatars":
                        HandleMyAvatars(request, response);
                        break;
                    case BasePath + "/switch-avatar":
                        HandleSwitchAvatar(request, response);
                        break;
                    case BasePath + "/suggestion-box":
                        HandleSuggestionBox(request, response);
                        break;
                    case BasePath + "/admin/suggestions":
                        HandleAdminSuggestions(request, response);
                        break;
                    case BasePath + "/store":
                        HandleStore(request, response);
                        break;
                    case BasePath + "/store/buy":
                        HandleStoreBuy(request, response);
                        break;
                    case BasePath + "/store/my-purchases":
                        HandleStoreMyPurchases(request, response);
                        break;
                    case BasePath + "/store/gloebit/authorize":
                        HandleStoreGloebitAuthorize(request, response);
                        break;
                    case BasePath + "/store/gloebit/auth_complete":
                        HandleStoreGloebitAuthComplete(request, response);
                        break;
                    case BasePath + "/store/gloebit/transaction":
                        HandleStoreGloebitTransaction(request, response);
                        break;
                    case BasePath + "/marketplace":
                        HandleMarketplace(request, response);
                        break;
                    case BasePath + "/marketplace/listing":
                        HandleMarketplaceListing(request, response);
                        break;
                    case BasePath + "/marketplace/buy":
                        HandleMarketplaceBuy(request, response);
                        break;
                    case BasePath + "/marketplace/manage":
                        HandleMarketplaceManage(request, response);
                        break;
                    case BasePath + "/marketplace/manage/save":
                        HandleMarketplaceManageSave(request, response);
                        break;
                    case BasePath + "/marketplace/manage/associate":
                        HandleMarketplaceManageAssociate(request, response);
                        break;
                    case BasePath + "/admin/store":
                        HandleAdminStore(request, response);
                        break;
                    case BasePath + "/admin/store/save":
                        HandleAdminStoreSave(request, response);
                        break;
                    case BasePath + "/admin/store/orders":
                        HandleAdminStoreOrders(request, response);
                        break;
                    case BasePath + "/admin/store/orders/renew":
                        HandleAdminStoreOrdersRenew(request, response);
                        break;
                    case BasePath + "/admin/store/orders/start":
                        HandleAdminStoreOrdersStart(request, response);
                        break;
                    case BasePath + "/admin/regions/ini":
                        HandleAdminRegionIniList(request, response);
                        break;
                    case BasePath + "/admin/regions/ini/edit":
                        HandleAdminRegionIniEdit(request, response);
                        break;
                    case BasePath + "/admin/regions/ini/restart":
                        HandleAdminRegionIniRestart(request, response);
                        break;
                    case BasePath + "/admin/simulators":
                        HandleAdminSimulators(request, response);
                        break;
                    case BasePath + "/admin/simulators/start":
                        HandleAdminSimulatorsStart(request, response);
                        break;
                    case BasePath + "/admin/simulators/start-all":
                        HandleAdminSimulatorsStartAll(request, response);
                        break;
                    case BasePath + "/admin/simulators/stop":
                        HandleAdminSimulatorsStop(request, response);
                        break;
                    case BasePath + "/admin/simulators/stop-all":
                        HandleAdminSimulatorsStopAll(request, response);
                        break;
                    case BasePath + "/admin/simulators/remove":
                        HandleAdminSimulatorsRemove(request, response);
                        break;
                    case BasePath + "/viewers":
                        HandleViewers(request, response);
                        break;
                    case BasePath + "/destinations":
                        HandleDestinations(request, response);
                        break;
                    case BasePath + "/offline-messages":
                        HandleOfflineMessages(request, response);
                        break;
                    case BasePath + "/messages":
                        HandleMessagesInbox(request, response);
                        break;
                    case BasePath + "/messages/sent":
                        HandleMessagesSent(request, response);
                        break;
                    case BasePath + "/messages/compose":
                        HandleMessagesCompose(request, response);
                        break;
                    case BasePath + "/messages/send":
                        HandleMessagesSend(request, response);
                        break;
                    case BasePath + "/messages/view":
                        HandleMessagesView(request, response);
                        break;
                    case BasePath + "/messages/delete":
                        HandleMessagesDelete(request, response);
                        break;
                    case BasePath + "/worldmap":
                        HandleWorldMap(request, response);
                        break;
                    case BasePath + "/gridstatus":
                        HandleGridStatus(request, response);
                        break;
                    case BasePath + "/economy":
                        HandleEconomy(request, response);
                        break;
                    case BasePath + "/features":
                        HandleFeatures(request, response);
                        break;
                    case BasePath + "/support":
                        HandleSupport(request, response);
                        break;
                    case BasePath + "/help":
                        HandleHelp(request, response);
                        break;
                    case BasePath + "/guide":
                        HandleGuide(request, response);
                        break;
                    case BasePath + "/search":
                        HandleSearch(request, response);
                        break;
                    case BasePath + "/landsearch":
                        HandleLandSearch(request, response);
                        break;
                    case BasePath + "/auctions":
                        HandleAuctions(request, response);
                        break;
                    case BasePath + "/auctions/bid":
                        HandleAuctionBidPage(request, response);
                        break;
                    case BasePath + "/search/suggest":
                        HandleSearchSuggest(request, response);
                        break;
                    case BasePath + "/admin/support":
                        HandleAdminSupport(request, response);
                        break;
                    case BasePath + "/admin/support/status":
                        HandleAdminSupportStatus(request, response);
                        break;
                    case BasePath + "/profile":
                        HandleProfile(request, response);
                        break;
                    case BasePath + "/friends":
                        HandleFriends(request, response);
                        break;
                    case BasePath + "/partner":
                        HandlePartner(request, response);
                        break;
                    case BasePath + "/change-password":
                        HandleChangePassword(request, response);
                        break;
                    case BasePath + "/change-email":
                        HandleChangeEmail(request, response);
                        break;
                    case BasePath + "/delete-account":
                        HandleDeleteAccount(request, response);
                        break;
                    case BasePath + "/transactions":
                        HandleMyTransactions(request, response);
                        break;
                    case BasePath + "/myclassifieds":
                        HandleMyClassifieds(request, response);
                        break;
                    case BasePath + "/myclassifieds/save":
                        HandleMyClassifiedsSave(request, response);
                        break;
                    case BasePath + "/myclassifieds/delete":
                        HandleMyClassifiedsDelete(request, response);
                        break;
                    case BasePath + "/myevents":
                        HandleMyEvents(request, response);
                        break;
                    case BasePath + "/myevents/save":
                        HandleMyEventsSave(request, response);
                        break;
                    case BasePath + "/myevents/delete":
                        HandleMyEventsDelete(request, response);
                        break;
                    case BasePath + "/forgot-password":
                        HandleForgotPassword(request, response);
                        break;
                    case BasePath + "/reset-password":
                        HandleResetPassword(request, response);
                        break;
                    case BasePath + "/recovery-codes":
                        HandleRecoveryCodes(request, response);
                        break;
                    case BasePath + "/recover-account":
                        HandleRecoverAccount(request, response);
                        break;
                    case BasePath + "/logout":
                        HandleLogout(request, response);
                        break;
                    case BasePath + "/admin":
                        HandleAdmin(request, response);
                        break;
                    case BasePath + "/admin/hg-toggle":
                        HandleAdminHGToggle(request, response);
                        break;
                    case BasePath + "/admin/maptile-regen":
                        HandleAdminMaptileRegen(request, response);
                        break;
                    case BasePath + "/admin/oar-save":
                        HandleAdminOarSave(request, response);
                        break;
                    case BasePath + "/admin/abuse-reports":
                        HandleAdminAbuseReports(request, response);
                        break;
                    case BasePath + "/admin/abuse-reports/image":
                        HandleAdminAbuseReportImage(request, response);
                        break;
                    case BasePath + "/admin/users":
                        HandleAdminUsers(request, response);
                        break;
                    case BasePath + "/admin/users/set-level":
                        HandleAdminUsersSetLevel(request, response);
                        break;
                    case BasePath + "/admin/users/edit-details":
                        HandleAdminUsersEditDetails(request, response);
                        break;
                    case BasePath + "/admin/users/reset-password":
                        HandleAdminUsersResetPassword(request, response);
                        break;
                    case BasePath + "/admin/users/create":
                        HandleAdminUsersCreate(request, response);
                        break;
                    case BasePath + "/admin/users/login-as":
                        HandleAdminUsersLoginAs(request, response);
                        break;
                    case BasePath + "/admin/users/soft-delete":
                        HandleAdminUsersSoftDelete(request, response);
                        break;
                    case BasePath + "/admin/users/remove":
                        HandleAdminUsersRemove(request, response);
                        break;
                    case BasePath + "/admin/users/kick":
                        HandleAdminUsersKick(request, response);
                        break;
                    case BasePath + "/admin/users/message":
                        HandleAdminUsersMessage(request, response);
                        break;
                    case BasePath + "/admin/estates":
                    case BasePath + "/myestates":
                        HandleAdminEstates(request, response);
                        break;
                    case BasePath + "/admin/groups":
                        HandleAdminGroups(request, response);
                        break;
                    case BasePath + "/admin/groups/update":
                        HandleAdminGroupsUpdate(request, response);
                        break;
                    case BasePath + "/admin/groups/delete":
                        HandleAdminGroupsDelete(request, response);
                        break;
                    case BasePath + "/admin/estates/create":
                        HandleAdminEstatesCreate(request, response);
                        break;
                    case BasePath + "/admin/estates/update":
                        HandleAdminEstatesUpdate(request, response);
                        break;
                    case BasePath + "/admin/estates/region-visibility":
                        HandleAdminEstatesRegionVisibility(request, response);
                        break;
                    case BasePath + "/admin/estates/managers":
                        HandleAdminEstatesListAction(request, response, "managers");
                        break;
                    case BasePath + "/admin/estates/access":
                        HandleAdminEstatesListAction(request, response, "access");
                        break;
                    case BasePath + "/admin/estates/bans":
                        HandleAdminEstatesListAction(request, response, "bans");
                        break;
                    case BasePath + "/admin/estates/groups":
                        HandleAdminEstatesGroups(request, response);
                        break;
                    case BasePath + "/admin/transactions":
                        HandleAdminTransactions(request, response);
                        break;
                    case BasePath + "/admin/stats":
                        HandleAdminStats(request, response);
                        break;
                    case BasePath + "/admin/news":
                        HandleAdminNews(request, response);
                        break;
                    case BasePath + "/admin/news/save":
                        HandleAdminNewsSave(request, response);
                        break;
                    case BasePath + "/admin/news/delete":
                        HandleAdminNewsDelete(request, response);
                        break;
                    case BasePath + "/admin/events":
                        HandleAdminEvents(request, response);
                        break;
                    case BasePath + "/admin/events/save":
                        HandleAdminEventsSave(request, response);
                        break;
                    case BasePath + "/admin/events/delete":
                        HandleAdminEventsDelete(request, response);
                        break;
                    case BasePath + "/admin/pages":
                        HandleAdminPages(request, response);
                        break;
                    case BasePath + "/admin/pages/save":
                        HandleAdminPagesSave(request, response);
                        break;
                    case BasePath + "/admin/pages/delete":
                        HandleAdminPagesDelete(request, response);
                        break;
                    case BasePath + "/admin/settings":
                        HandleAdminSettings(request, response);
                        break;
                    case BasePath + "/admin/settings/save":
                        HandleAdminSettingsSave(request, response);
                        break;
                    case BasePath + "/admin/console":
                        HandleAdminConsole(request, response);
                        break;
                    case BasePath + "/admin/console/run":
                        HandleAdminConsoleRun(request, response);
                        break;
                    case BasePath + "/admin/regions/restart":
                        HandleAdminRegionRestart(request, response);
                        break;
                    case BasePath + "/admin/regions/group-auto-invite":
                        HandleAdminRegionGroupAutoInvite(request, response);
                        break;
                    case BasePath + "/admin/regions":
                        HandleAdminRegions(request, response);
                        break;
                    case BasePath + "/myregions":
                        HandleMyRegions(request, response);
                        break;
                    case BasePath + "/myregions/oar-save":
                        HandleMyRegionsOarSave(request, response);
                        break;
                    case BasePath + "/myregions/restart":
                        HandleMyRegionsRestart(request, response);
                        break;
                    case BasePath + "/myland":
                        HandleMyLand(request, response);
                        break;
                    case BasePath + "/myland/toggle":
                        HandleMyLandToggle(request, response);
                        break;
                    case BasePath + "/myinventory":
                        HandleMyInventory(request, response);
                        break;
                    case BasePath + "/myinventory/iar-save":
                        HandleMyInventoryIarSave(request, response);
                        break;
                    default:
                        // Static pages are served at an operator-chosen slug,
                        // not a fixed path this switch can list in advance -
                        // checked last so it never shadows any of the fixed
                        // routes above.
                        if (path.StartsWith(BasePath + "/page/", StringComparison.Ordinal))
                            HandleStaticPage(request, response, path.Substring((BasePath + "/page/").Length));
                        else if (path.StartsWith(BasePath + "/static/", StringComparison.Ordinal))
                            HandleStaticAsset(request, response, path.Substring((BasePath + "/static/").Length));
                        else if (path.StartsWith(BasePath + "/welcome-photos/", StringComparison.Ordinal))
                            HandleWelcomePhoto(request, response, path.Substring((BasePath + "/welcome-photos/").Length));
                        else
                        {
                            // Reference's http_404.html - real gap, a
                            // sub-path under one of this connector's own
                            // registered top-level routes (e.g. an unknown
                            // /admin/* page) that matches no case here used
                            // to fall through to a bare status code with no
                            // body at all, where every other 404 in this
                            // connector (HandleStaticPage, HandleWelcomePhoto,
                            // etc.) at least gets a real rendered page.
                            // NOTE: this is not a site-wide catch-all - a
                            // path outside every registered top-level route
                            // (see topLevelRoutes in the constructor) never
                            // reaches HandleRequest at all; BaseHttpServer's
                            // own built-in default 404 answers those,
                            // upstream of this connector entirely (confirmed
                            // live: hitting a genuinely unrelated path shows
                            // OpenSim core's stock joke 404 page, not this one).
                            response.StatusCode = (int)HttpStatusCode.NotFound;
                            WritePage(request, response, PageTitle("Page Not Found"),
                                    "<h1>Page Not Found</h1><p>The page you're looking for doesn't exist.</p>"
                                    + "<p><a href=\"" + BasePath + "/\">Back to home</a></p>");
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                // Reference's http_500.html - real gap alongside the 404 one
                // above. The raw exception message used to go straight to
                // the client (an info-disclosure smell on top of just being
                // ugly) - full detail stays in the log only now, the client
                // gets a themed page like every other error path here.
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                try
                {
                    WritePage(request, response, PageTitle("Something Went Wrong"),
                            "<h1>Something Went Wrong</h1><p>An unexpected error occurred handling this page. It's been logged.</p>"
                            + "<p><a href=\"" + BasePath + "/\">Back to home</a></p>");
                }
                catch (Exception)
                {
                    // WritePage itself builds the shared header/nav from live
                    // session/settings state - if whatever broke the original
                    // request also breaks that, fall back to a body with no
                    // dependencies at all rather than letting a second
                    // exception escape this handler uncaught.
                    response.RawBuffer = Encoding.UTF8.GetBytes("Something went wrong. Please try again later.");
                }
                // Was previously silent - errors here never reached the log
                // at all, only the raw exception message shown to whoever
                // hit the broken page. Found live chasing a real bug this
                // way once already (the DisplayName column race) - fixed
                // so the next one doesn't need the same from-source trace.
                m_log.WarnFormat("[WEBINTERFACE]: Unhandled exception handling {0}: {1}", path, e);
            }
        }

        #region Session helpers

        private WebSession GetSession(IOSHttpRequest request)
        {
            string token = ReadCookie(request, SessionCookieName);
            if (string.IsNullOrEmpty(token))
                return null;

            if (m_sessions.TryGetValue(token, out WebSession session))
            {
                if (session.Expires > DateTime.UtcNow)
                    return session;

                m_sessions.TryRemove(token, out _);
            }

            return null;
        }

        private string CreateSession(UUID principalID, string name, bool isAdmin, UUID webAccountId)
        {
            string token = UUID.Random().ToString();
            m_sessions[token] = new WebSession
            {
                PrincipalID = principalID,
                Name = name,
                IsAdmin = isAdmin,
                Expires = DateTime.UtcNow.Add(SessionLifetime),
                WebAccountID = webAccountId
            };
            return token;
        }

        // request.RemoteIPEndPoint can be null in some hosting setups
        // (never observed on this deployment, but not worth a null-ref
        // crashing a login just for an activity-log cosmetic field).
        private static string GetClientIP(IOSHttpRequest request)
        {
            return request.RemoteIPEndPoint?.Address?.ToString() ?? "unknown";
        }

        private static string ReadCookie(IOSHttpRequest request, string name)
        {
            string cookies = request.Headers["cookie"] ?? request.Headers["Cookie"];
            if (string.IsNullOrEmpty(cookies))
                return string.Empty;

            foreach (string raw in cookies.Split(';'))
            {
                string part = raw.Trim();
                int equals = part.IndexOf('=');
                if (equals <= 0)
                    continue;
                if (part.Substring(0, equals).Trim().Equals(name, StringComparison.Ordinal))
                    return part.Substring(equals + 1).Trim();
            }

            return string.Empty;
        }

        private static void SetSessionCookie(IOSHttpResponse response, string token)
        {
            response.AddHeader("Set-Cookie", SessionCookieName + "=" + token
                    + "; Path=/; HttpOnly; SameSite=Lax");
        }

        private static void ClearSessionCookie(IOSHttpResponse response)
        {
            response.AddHeader("Set-Cookie", SessionCookieName
                    + "=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT; HttpOnly; SameSite=Lax");
        }

        private static Dictionary<string, string> ReadForm(IOSHttpRequest request)
        {
            Dictionary<string, string> form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (request.HasEntityBody && request.InputStream != null)
            {
                Encoding encoding = request.ContentEncoding ?? Encoding.UTF8;
                using (StreamReader reader = new StreamReader(request.InputStream, encoding))
                {
                    string body = reader.ReadToEnd();
                    if (!string.IsNullOrEmpty(body))
                    {
                        Dictionary<string, object> parsed = ServerUtils.ParseQueryString(body);
                        foreach (KeyValuePair<string, object> entry in parsed)
                            form[entry.Key] = entry.Value == null ? string.Empty : entry.Value.ToString();
                    }
                }
            }

            return form;
        }

        private static string FormValue(Dictionary<string, string> form, string name)
        {
            return form.TryGetValue(name, out string value) ? value : string.Empty;
        }

        #endregion Session helpers

        #region Pages

        // Public grid home page - what a browser sees at the bare hostname
        // (http://<grid>/, port 80), not the /web/* login app on PublicPort.
        // This is deliberately the marketing/sign-up page for a prospective
        // visitor browsing normally, not the in-viewer splash below - full
        // site chrome via WritePage, a real pitch for why to join, and
        // Featured Classifieds (browsing what's for sale is a "look what
        // you could have" hook here, not just status information).
        // Rebuilt (2026-09-02) after stepping back and looking at this page
        // "as a whole" next to welcome.php's own rebuild earlier the same
        // day - everything here sat in one flat, undifferentiated .card
        // (found live: .content-card, used by ~15 other pages too, had no
        // CSS rule anywhere in this file, so it never actually looked like
        // a distinct section), there was no live online-now/region-count
        // proof point until a visitor scrolled past the pitch cards down to
        // Economy, Economy and Classifieds shared the same two data sources
        // welcome.php pairs side by side but stacked here in a different
        // order, and the only CTA sat once above the fold with nothing to
        // act on after being convinced by the content below it.
        private void HandleHome(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string welcomeMessage = GetWebSafeWelcomeMessage();
            string tagline = string.IsNullOrEmpty(welcomeMessage)
                    ? "A free, open virtual world you can visit today."
                    : Html(welcomeMessage);

            bool allowRegistration = GetSetting("AllowRegistration", "true") == "true";

            // Online-now counting stays against every alive region,
            // Unlisted included - a resident standing in an unlisted
            // region is still really online, that flag only opts a
            // region out of public listing/map/counts, not out of the
            // grid's own activity stats. Only the "regions to explore"
            // display count/list is filtered.
            List<GridRegion> aliveRegions = FilterOnlineRegions(
                    m_GridService?.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000) ?? new List<GridRegion>());
            List<GridRegion> regions = FilterListedRegions(aliveRegions);
            int onlineNow = 0;
            if (m_GridUserService != null)
            {
                HashSet<string> aliveRegionIDs = new HashSet<string>(aliveRegions.Select(r => r.RegionID.ToString()));
                onlineNow = m_GridUserService.GetOnlineUserCount(aliveRegionIDs);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>").Append(Html(gridName)).Append("</h1>");
            sb.Append(RenderAnnouncement());
            sb.Append("<p class=\"tagline-lead\">").Append(tagline).Append("</p>");

            sb.Append("<div class=\"home-live-strip\">");
            sb.Append("<span class=\"home-online-badge\">&#9679; Online</span>");
            if (m_GridUserService != null)
                sb.Append("<span>").Append(onlineNow.ToString("N0")).Append(" online now</span>");
            sb.Append("<span>").Append(regions.Count.ToString("N0")).Append(" regions to explore</span>");
            sb.Append("</div>");

            sb.Append("<div class=\"cta-row\">");
            if (allowRegistration)
                sb.Append("<a href=\"").Append(BasePath).Append("/register\" class=\"cta-primary\">Create a Free Account</a>");
            sb.Append("<a href=\"").Append(BasePath).Append("/login\" class=\"cta-secondary\">Log In</a>");
            sb.Append("</div>");

            // Hypergrid address up front, not buried on /viewers - the
            // homepage's other real audience besides a brand-new signup is
            // a Hypergrid traveler from another grid who just wants the
            // address to paste into their own viewer's map bar, no account
            // needed. Same loginUri HandleViewers already computes, not a
            // second value that could drift.
            string loginUri = string.IsNullOrEmpty(m_publicBaseUrl) ? string.Empty : m_publicBaseUrl + "/";
            if (!string.IsNullOrEmpty(loginUri))
            {
                sb.Append("<div class=\"content-card\"><h2><i class=\"bi bi-signpost-2\"></i> Hypergrid Address</h2>")
                  .Append("<p>Already have a viewer or an account on another OpenSim grid? Paste this into your map/search bar to teleport straight in.</p>")
                  .Append("<form onsubmit=\"return false;\"><input type=\"text\" value=\"").Append(Html(loginUri))
                  .Append("\" readonly onclick=\"this.select()\"></form></div>");
            }

            sb.Append("<h2>Why ").Append(Html(gridName)).Append("?</h2><div class=\"widget-grid\">");
            AppendFeatureCard(sb, "Built-In Economy", "No setup required",
                    "A real currency ledger with buy/sell and group treasuries, ready out of the box.");
            AppendFeatureCard(sb, "Hypergrid Ready", "Explore beyond this grid",
                    "Open, standards-based teleporting to other OpenSimulator grids.");
            AppendFeatureCard(sb, "Active Community", "See what's happening",
                    "Live events, classifieds, and grid-wide search across every region.");
            AppendFeatureCard(sb, "Safe & Moderated", "Built in, not bolted on",
                    "Native mute list, grid-wide viewer bans, and in-viewer abuse reporting with a web admin queue.");
            AppendFeatureCard(sb, "Room to Build", "For creators, not just visitors",
                    "Larger-than-standard VarRegions with no sim-crossing stutter, mesh uploads, full LSL/OSSL scripting.");
            AppendFeatureCard(sb, "Runs From a Browser", "No viewer required for the basics",
                    "Search, events, classifieds, your store listings, account and land - all reachable without logging in-world.");
            sb.Append("</div>");

            string classifieds = RenderFeaturedClassifieds(6);
            string economy = RenderEconomyStats();
            if (!string.IsNullOrEmpty(classifieds) || !string.IsNullOrEmpty(economy))
            {
                sb.Append("<div class=\"home-2col\">");
                if (!string.IsNullOrEmpty(classifieds))
                    sb.Append("<div class=\"content-card home-2col-wide\">").Append(classifieds).Append("</div>");
                if (!string.IsNullOrEmpty(economy))
                    sb.Append("<div class=\"content-card\">").Append(economy).Append("</div>");
                sb.Append("</div>");
            }

            string events = RenderUpcomingEvents(5);
            if (!string.IsNullOrEmpty(events))
                sb.Append("<div class=\"content-card\">").Append(events).Append("</div>");

            string news = RenderNewsFeed(5);
            if (!string.IsNullOrEmpty(news))
                sb.Append("<div class=\"content-card\">").Append(news).Append("</div>");

            // Repeated CTA - a visitor who scrolls through Economy/
            // Classifieds/Events and gets convinced shouldn't have to
            // scroll back to the top to act on it.
            if (allowRegistration)
                sb.Append("<div class=\"content-card\" style=\"text-align:center;\"><h2>Ready to join ")
                  .Append(Html(gridName)).Append("?</h2><div class=\"cta-row\" style=\"justify-content:center;\">")
                  .Append("<a href=\"").Append(BasePath).Append("/register\" class=\"cta-primary\">Create a Free Account</a>")
                  .Append("</div></div>");

            sb.Append("<p><a href=\"").Append(BasePath).Append("/viewers\">Get a viewer &rarr;</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/features\">See all features &rarr;</a></p>");

            WritePage(request, response, gridName, sb.ToString());
        }

        // The in-viewer login splash screen - see [GridInfoService] "welcome" in
        // Robust.HG.ini, which tells the viewer to fetch exactly this filename.
        // Rendered inside the viewer's own small embedded login panel via
        // WriteBarePage (no header/nav/footer - nowhere useful to navigate to
        // from inside that panel), and deliberately answers a different
        // question than the home page above: not "why should I join" but
        // "what's happening on this grid right now" for someone who (mostly)
        // already has an account - task #23 from the WhiteCore-Dev re-audit's
        // "all of it" list, a grid-operator announcements feed originally
        // shared with the home page, now specific to this one.
        //
        // Rebuilt (2026-09-02) after the user pointed at two real competing
        // grids' own splash screens (3RD Rock Grid, DigiWorldz) with the
        // explicit framing "why can't our welcome/splash look like these,"
        // then "remember we are going with a 1st impression" - i.e. this
        // page specifically, not the browser home page. Both references
        // share the same real shape, confirmed side by side rather than
        // assumed from one: a branded top bar (logo/tagline left, live
        // online-now/region-count/join-CTA stats right), a multi-item news
        // ticker, Featured Classifieds beside a live Economy dashboard,
        // a full-width Upcoming Events row, and a closing stat/link footer.
        // Everything below reuses this connector's own existing, real
        // DB-driven render methods (RenderFeaturedClassifieds/
        // RenderEconomyStats/RenderUpcomingEvents - the home page's own
        // data sources, not a second divergent copy) rather than inventing
        // new ones - the gap from the reference was page layout/content
        // selection, not missing data sources.
        //
        // One real gap knowingly deferred rather than rushed: the
        // reference's classifieds show actual photo thumbnails
        // (UserClassifiedAdd.SnapshotId is a real texture asset id this
        // grid already has), but rendering it needs a public texture-to-
        // image HTTP endpoint this Robust instance doesn't currently run -
        // GetTextureRobustHandler (OpenSim.Capabilities.Handlers) already
        // does exactly the JP2->JPEG/PNG conversion needed and is proven,
        // real code, just not wired into this grid's ServiceConnectors.
        // Wiring up a new always-on public asset-serving endpoint is an
        // infrastructure decision worth making deliberately, not smuggled
        // into a page-layout pass - text-only classified cards (name/
        // category/location/price/description, same as the home page
        // already shows) ship tonight instead.
        private void HandleWelcome(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string welcomeMessage = GetWebSafeWelcomeMessage();
            string tagline = string.IsNullOrEmpty(welcomeMessage)
                    ? "Explore. Build. Connect."
                    : TruncateText(welcomeMessage, 90);
            bool allowRegistration = GetSetting("AllowRegistration", "true") == "true";

            // Same split as HandleHome above - online-now counts every
            // alive region including Unlisted ones, only the displayed
            // region list/count is filtered.
            List<GridRegion> aliveRegions = FilterOnlineRegions(
                    m_GridService?.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000) ?? new List<GridRegion>());
            List<GridRegion> regions = FilterListedRegions(aliveRegions);

            int onlineNow = 0;
            if (m_GridUserService != null)
            {
                HashSet<string> aliveRegionIDs = new HashSet<string>(aliveRegions.Select(r => r.RegionID.ToString()));
                onlineNow = m_GridUserService.GetOnlineUserCount(aliveRegionIDs);
            }

            StringBuilder sb = new StringBuilder(WelcomeCompactCss);
            sb.Append(RenderWelcomeSlideshow());

            sb.Append("<div class=\"welcome-topbar\">");
            sb.Append("<div class=\"welcome-brand\"><span class=\"welcome-brand-mark\">")
              .Append(Html(gridName.Length > 0 ? gridName.Substring(0, 1) : "?")).Append("</span><div>")
              .Append("<div class=\"welcome-brand-name\">").Append(Html(gridName)).Append("</div>")
              .Append("<div class=\"welcome-tagline\">").Append(Html(tagline)).Append("</div></div></div>");
            sb.Append("<div class=\"welcome-topstats\">");
            sb.Append("<span class=\"welcome-online-badge\">&#9679; Online</span>");
            if (m_GridUserService != null)
                sb.Append("<span>").Append(onlineNow.ToString("N0")).Append(" online now</span>");
            sb.Append("<span>").Append(regions.Count.ToString("N0")).Append(" regions to explore</span>");
            if (allowRegistration)
                sb.Append("<a class=\"welcome-join-cta\" href=\"").Append(BasePath).Append("/register\">Free to Join</a>");
            sb.Append("</div></div>");

            // Multi-item ticker, not a single boxed announcement -
            // RenderAnnouncement (a distinct admin-set notice with its own
            // color/title) still renders separately right below, since
            // squeezing it into ticker rows would lose that.
            if (m_NewsService != null)
            {
                List<NewsItem> newsItems = m_NewsService.GetNews(0, 5);
                if (newsItems.Count > 0)
                {
                    sb.Append("<div class=\"welcome-ticker\"><span class=\"welcome-ticker-label\">News</span>")
                      .Append("<div class=\"welcome-ticker-track\">");
                    for (int i = 0; i < newsItems.Count; i++)
                    {
                        if (i > 0)
                            sb.Append("<span class=\"welcome-ticker-sep\">&middot;</span>");
                        sb.Append("<span>").Append(Html(newsItems[i].Title)).Append("</span>");
                    }
                    sb.Append("</div></div>");
                }
            }

            sb.Append(RenderAnnouncement());

            sb.Append("<div class=\"welcome-stack\">");

            string classifieds = RenderFeaturedClassifieds(6);
            string economy = RenderEconomyStats();
            if (!string.IsNullOrEmpty(classifieds) || !string.IsNullOrEmpty(economy))
            {
                sb.Append("<div class=\"welcome-2col\">");
                if (!string.IsNullOrEmpty(classifieds))
                    sb.Append("<div class=\"welcome-box welcome-box-wide\">").Append(classifieds).Append("</div>");
                if (!string.IsNullOrEmpty(economy))
                    sb.Append("<div class=\"welcome-box\">").Append(economy).Append("</div>");
                sb.Append("</div>");
            }

            string events = RenderUpcomingEvents(6);
            if (!string.IsNullOrEmpty(events))
                sb.Append("<div class=\"welcome-box\">").Append(events).Append("</div>");

            string regionList = RenderRegionListCompact(regions.Take(8).ToList(), IsViewerRequest(request, response));
            if (!string.IsNullOrEmpty(regionList))
            {
                sb.Append("<div class=\"welcome-box\">").Append(regionList);
                if (regions.Count > 8)
                    sb.Append("<p class=\"welcome-more-link\"><a href=\"").Append(BasePath).Append("/worldmap\">View all ")
                      .Append(regions.Count).Append(" regions &rarr;</a></p>");
                sb.Append("</div>");
            }

            sb.Append("</div>");

            sb.Append("<div class=\"welcome-footer\">");
            sb.Append("<span>&#9679; Total Online Now: ").Append(onlineNow.ToString("N0")).Append("</span>");
            sb.Append("<a href=\"").Append(BasePath).Append("/\">Visit Our Main Site</a>");
            sb.Append("<span>&copy; ").Append(DateTime.UtcNow.Year).Append(" ").Append(Html(gridName)).Append("</span>");
            sb.Append("</div>");

            WriteAdaptivePage(request, response, gridName, sb.ToString());
        }

        // Cycles through whatever's in WebSplash/ (see HandleWelcomePhoto) a
        // few seconds apart. Fixed full-viewport background behind
        // everything (WhiteCore-Dev's own body.welcomescreen{background-
        // image:...} pattern, confirmed against its real
        // randomimageswitch.js - a plain div here instead of styling
        // <body> directly, since <body> is shared page chrome this
        // connector doesn't want to reach into). Renders nothing at all
        // when the photo folder is empty/missing, so an operator who
        // doesn't care about the banner gets the old plain-background page.
        //
        // Two stacked layers crossfaded on `opacity`, not a direct
        // `background-image` swap on one layer. Found live - "blinks
        // before displaying the next picture... grayish screen every so
        // often" - tracing it back: a bare
        // `element.style.backgroundImage = url(...)` has no guaranteed
        // fetch-before-swap; the browser starts painting the new url()
        // immediately and renders nothing under it until that request
        // finishes, so anything short of an already-cached image shows
        // as a blank flash (the plain page underneath, hence "grayish"),
        // then pops in with no fade once loaded (the "blink"). The real
        // osloginscreen.css reference this was modeled on only ever
        // looked like a fade because the browser already had that image
        // cached from an earlier cycle - `transition:background 1s` on a
        // url() swap isn't a spec-guaranteed crossfade either way.
        // Fixed properly: preload the next image with a plain JS Image()
        // object first, only write it into the (currently hidden) back
        // layer's background-image once its own onload has fired, then
        // crossfade opacity - which, unlike background-image, browsers
        // reliably animate.
        private string RenderWelcomeSlideshow()
        {
            List<string> photos = GetWelcomePhotoFiles();
            if (photos.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<div class=\"welcome-bg-layer\" id=\"welcome-bg-a\" style=\"opacity:1;background-image:url('")
              .Append(Html(BasePath)).Append("/welcome-photos/").Append(Html(Uri.EscapeDataString(photos[0]))).Append("')\"></div>");
            sb.Append("<div class=\"welcome-bg-layer\" id=\"welcome-bg-b\" style=\"opacity:0\"></div>");
            sb.Append("<div class=\"welcome-bg-overlay\"></div>");

            if (photos.Count > 1)
            {
                StringBuilder urls = new StringBuilder("[");
                for (int i = 0; i < photos.Count; i++)
                {
                    if (i > 0)
                        urls.Append(',');
                    urls.Append('\'').Append(Html(BasePath)).Append("/welcome-photos/")
                        .Append(Html(Uri.EscapeDataString(photos[i]))).Append('\'');
                }
                urls.Append(']');

                sb.Append("<script>(function(){var photos=").Append(urls).Append(";var i=0;")
                  .Append("var layers=[document.getElementById('welcome-bg-a'),document.getElementById('welcome-bg-b')];")
                  .Append("var front=0;")
                  .Append("setInterval(function(){")
                  .Append("i=(i+1)%photos.length;")
                  .Append("var url=photos[i];")
                  .Append("var img=new Image();")
                  .Append("img.onload=function(){")
                  .Append("var back=layers[1-front];")
                  .Append("back.style.backgroundImage=\"url(\"+JSON.stringify(url)+\")\";")
                  .Append("void back.offsetHeight;") // force layout so the opacity change below actually transitions instead of coalescing
                  .Append("back.style.opacity=1;")
                  .Append("layers[front].style.opacity=0;")
                  .Append("front=1-front;")
                  .Append("};")
                  .Append("img.src=url;")
                  .Append("},6000);})();</script>");
            }

            return sb.ToString();
        }

        // Scoped to just this page (id selector, not a global rule) so it
        // can't leak into the full-chrome site pages that share PageCss.
        private const string WelcomeCompactCss =
                "<style>" +
                "body{font-size:15px;}" +
                "h2{font-size:17px;margin:0 0 10px;}" +
                "p{margin:0 0 10px;}" +
                ".welcome-more-link{text-align:center;font-size:13.5px;margin-top:12px;}" +
                ".stats-grid{gap:12px;}" +
                ".stat-card{padding:12px 14px;}" +
                ".stat-value{font-size:20px;}" +
                ".stat-label{font-size:12.5px;}" +
                ".stat-sub{font-size:12px;}" +
                ".widget-grid{gap:10px;}" +
                ".widget-card{padding:12px 14px;}" +
                ".widget-card h3{font-size:15px;margin:0 0 5px;}" +
                ".widget-meta{font-size:13px;}" +
                ".widget-card-thumb{height:110px;margin:-12px -14px 8px;width:calc(100% + 28px);}" +
                // True full-viewport background, not a small strip - matches
                // both references directly (WhiteCore-Dev's welcomescreen
                // screenshot: full-page photo with translucent boxes
                // scattered on top; osloginscreen's own body.full/.fader +
                // img#bgimage{position:fixed;z-index:-2}). html/body need
                // height:100% and no scroll-cutoff for a fixed background to
                // actually cover the whole viewport instead of just the
                // content height.
                "html,body{height:100%;}" +
                // PageCss sets body{background:var(--bg)} - an opaque solid
                // color, painted as part of body's own box regardless of
                // this element's z-index. A negative z-index only pushes an
                // element behind other *positioned* content, not behind its
                // ancestor's own background paint - so with body's
                // background left opaque, .welcome-bg's photo was hidden
                // everywhere except the odd sliver where body's box didn't
                // quite reach (found live: "you can only see the edges").
                // Scoped to just this page, matching the rest of this block.
                // The shared page-chrome template (WriteAdaptivePage) also
                // wraps this page's content in PageCss's own .card - same
                // opaque-background-over-negative-z-index problem one level
                // deeper, found by walking .welcome-bg's actual ancestor
                // chain live rather than assuming body was the only culprit.
                "body{margin:0;background:transparent;}" +
                // Also clears box-shadow (PageCss's .card casts a real
                // drop shadow independent of its background/border, left
                // visible as a stray outline around the whole page after
                // just clearing background/border - found live: "the
                // shadow, which used to be a container that held the
                // cards") and padding (this page supplies its own via
                // .welcome-columns instead).
                ".card{background:transparent;border:none;box-shadow:none;padding:0;}" +
                // Two stacked layers instead of one - see RenderWelcomeSlideshow's
                // own comment for why (opacity crossfades reliably, a
                // background-image swap on a single layer doesn't). Same
                // DOM order both layers, so the browser's default paint
                // order alone decides which one is "on top" while both
                // happen to be visible mid-fade - deliberately left
                // implicit rather than juggling z-index between them.
                ".welcome-bg-layer{position:fixed;inset:0;z-index:-2;background-size:cover;" +
                "background-position:center;transition:opacity 1.2s ease;}" +
                ".welcome-bg-overlay{position:fixed;inset:0;z-index:-2;background:rgba(0,0,0,.28);}" +
                // Branded top bar - grid identity/tagline left, live "this
                // grid is alive" stats and the join CTA right, replacing the
                // earlier centered title-over-photo treatment (redundant
                // once the grid name lives here instead).
                ".welcome-topbar{display:flex;justify-content:space-between;align-items:center;" +
                "flex-wrap:wrap;gap:16px;max-width:1300px;margin:0 auto;padding:20px 24px;}" +
                ".welcome-brand{display:flex;align-items:center;gap:12px;}" +
                ".welcome-brand-mark{display:flex;align-items:center;justify-content:center;width:40px;" +
                "height:40px;border-radius:10px;background:var(--accent);color:#fff;font-weight:800;" +
                "font-size:18px;flex-shrink:0;}" +
                ".welcome-brand-name{font-size:19px;font-weight:800;color:#fff;" +
                "text-shadow:0 1px 4px rgba(0,0,0,.6);}" +
                ".welcome-tagline{font-size:12.5px;color:#d8dce3;text-shadow:0 1px 3px rgba(0,0,0,.6);}" +
                ".welcome-topstats{display:flex;align-items:center;flex-wrap:wrap;gap:18px;font-size:13px;" +
                "color:#e8eaed;text-shadow:0 1px 3px rgba(0,0,0,.6);}" +
                ".welcome-online-badge{display:inline-flex;align-items:center;gap:6px;color:var(--success);" +
                "font-weight:700;}" +
                ".welcome-join-cta{display:inline-block;background:var(--accent);color:#fff;" +
                "text-decoration:none;font-weight:700;padding:8px 18px;border-radius:40px;" +
                "text-transform:uppercase;font-size:12px;letter-spacing:.3px;}" +
                ".welcome-join-cta:hover{background:var(--accent-dark);color:#fff;text-decoration:none;}" +
                // News ticker - several headlines in one compact bar rather
                // than a single boxed announcement.
                ".welcome-ticker{display:flex;align-items:center;gap:14px;max-width:1300px;margin:0 auto;" +
                "padding:8px 24px;background:rgba(21,24,29,.7);border-top:1px solid rgba(255,255,255,.08);" +
                "border-bottom:1px solid rgba(255,255,255,.08);flex-wrap:wrap;}" +
                ".welcome-ticker-label{color:var(--accent-bright);font-weight:800;font-size:11.5px;" +
                "letter-spacing:.5px;flex-shrink:0;}" +
                ".welcome-ticker-track{display:flex;flex-wrap:wrap;gap:10px;font-size:13px;color:#e8eaed;}" +
                ".welcome-ticker-sep{color:var(--muted);}" +
                // Main content stack - full-width boxes (Classifieds+Economy
                // side by side, then Events, then Regions), replacing the
                // earlier edge-pinned/wrapping-tile column layouts.
                ".welcome-stack{max-width:1300px;margin:0 auto;padding:20px 24px 50px;display:flex;" +
                "flex-direction:column;gap:20px;}" +
                ".welcome-2col{display:flex;gap:20px;flex-wrap:wrap;}" +
                // The floating translucent box itself - WhiteCore's
                // #regionbox/#infobox/#news/#gridstatus (semi-transparent,
                // rounded, shadowed) and osloginscreen's .boxtext, adapted to
                // this site's own dark palette instead of copying their
                // literal colors.
                ".welcome-box{background:rgba(21,24,29,.86);border:1px solid rgba(255,255,255,.08);" +
                "border-radius:8px;padding:18px 20px;backdrop-filter:blur(3px);" +
                "box-shadow:0 8px 24px rgba(0,0,0,.45);}" +
                ".welcome-2col>.welcome-box{flex:1 1 320px;}" +
                ".welcome-2col>.welcome-box-wide{flex:2 1 420px;}" +
                ".welcome-box h2:first-child{margin-top:0;}" +
                ".welcome-region-list{list-style:none;margin:0;padding:0;}" +
                ".welcome-region-list li{display:flex;justify-content:space-between;align-items:center;" +
                "padding:8px 0;border-bottom:1px solid var(--border);font-size:14px;}" +
                ".welcome-region-list li:last-child{border-bottom:none;}" +
                ".welcome-region-coords{color:var(--muted);font-size:12.5px;}" +
                // Closing stat/link bar - matches the reference's own
                // "Total Online Now" footer rather than the page just
                // stopping after the last content block.
                ".welcome-footer{display:flex;justify-content:space-between;align-items:center;" +
                "flex-wrap:wrap;gap:10px;max-width:1300px;margin:0 auto;padding:16px 24px 30px;" +
                "border-top:1px solid rgba(255,255,255,.08);font-size:12.5px;color:#c3c8d1;}" +
                ".welcome-footer a{color:var(--accent-bright);}" +
                "@media (max-width:600px){.welcome-brand-name{font-size:16px;}" +
                ".welcome-topstats{gap:10px;font-size:12px;}}" +
                "</style>";

        // Real counterpart to WhiteCore-Dev's welcomescreen/gridstatus.html
        // (total users/regions, online-now count, unique visitors) - read
        // directly this time rather than invented, after the splash was
        // rewritten twice without checking it (see PROJECT_LOG). Reuses the
        // exact same GetOnlineUserCount/GetUserAccountsWhere/
        // GetUniqueVisitorCount calls HandleAdminStats/HandleGridStatus
        // already established as the real data source for these numbers -
        // not a second, divergent counting method. Currency dropped from
        // this specific widget per explicit feedback - "Active/Not
        // configured" isn't useful information for a first-time visitor
        // deciding whether to sign up, unlike the full /gridstatus page
        // where it stays. "Voice active" still omitted: no way to tell if
        // voice is actually configured/working from Robust.
        private string RenderGridStatusWidget(List<GridRegion> regions)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>Grid Status</h2><div class=\"stats-grid\">");

            AppendStat(sb, "Regions", regions.Count.ToString("N0"), "online now");

            if (m_UserAccountService != null)
            {
                int totalAccounts = m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1").Count;
                AppendStat(sb, "Registered Accounts", totalAccounts.ToString("N0"), "all time");
            }

            if (m_GridUserService != null)
            {
                // `regions` here is already FilterOnlineRegions' output (see
                // HandleWelcome), so this only counts someone as online if
                // the region they were last on is confirmed alive right
                // now - a crashed/killed region never clears the "Online"
                // flag for whoever was on it otherwise.
                HashSet<string> aliveRegionIDs = new HashSet<string>(regions.Select(r => r.RegionID.ToString()));
                int online = m_GridUserService.GetOnlineUserCount(aliveRegionIDs);
                AppendStat(sb, "Online Now", online.ToString("N0"), "residents");

                int uniqueVisitors30d = m_GridUserService.GetUniqueVisitorCount(30);
                AppendStat(sb, "Unique Visitors", uniqueVisitors30d.ToString("N0"), "last 30 days");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        // Plain name + coordinates + teleport link, no map-tile thumbnail -
        // used on the login splash's region column. Started as a grid of
        // thumbnail cards (RenderRegionListWidget, since removed - this
        // replaced its only call site), but that was too heavy for a
        // column this narrow (per explicit feedback after seeing it live);
        // a list reads fine at that width and matches osloginscreen's own
        // regionlist.php, which is plain text rows too, not thumbnails.
        private string RenderRegionListCompact(List<GridRegion> regions, bool isViewerContext)
        {
            if (regions.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>Regions</h2><ul class=\"welcome-region-list\">");
            foreach (GridRegion region in regions)
            {
                // This page is always pre-login when rendered inside the
                // viewer's own panel - see this class's own HandleWelcome
                // comment. location_login (not /app/teleport/) is the
                // command that fills the Start Location box instead of
                // trying to run a teleport with no session yet - see
                // BuildLocationLoginUrl's own comment for why. A real
                // external browser gets hop:// instead, since
                // location_login is explicitly blocked from that context.
                string tp = isViewerContext
                        ? BuildLocationLoginUrl(region.RegionName)
                        : BuildHopUrl(region.RegionName);
                sb.Append("<li><a href=\"").Append(Html(tp)).Append("\">").Append(Html(region.RegionName)).Append("</a>")
                  .Append("<span class=\"welcome-region-coords\">").Append(region.RegionCoordX).Append(", ")
                  .Append(region.RegionCoordY).Append("</span></li>");
            }
            sb.Append("</ul>");
            return sb.ToString();
        }

        // "Upcoming Events" splash widget - see EventItem's class-level
        // comment for why this is admin-managed rather than a full
        // in-world event system.
        private string RenderUpcomingEvents(int count)
        {
            if (m_EventsService == null)
                return string.Empty;

            List<EventItem> events = m_EventsService.GetUpcoming(0, count);
            if (events == null || events.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>Upcoming Events</h2><div class=\"widget-grid\">");
            foreach (EventItem ev in events)
            {
                sb.Append("<div class=\"widget-card\"><h3>").Append(Html(ev.Title)).Append("</h3>");
                sb.Append("<div class=\"widget-meta\">").Append(Html(ev.EventDate.ToString("MMM d, h:mm tt"))).Append(" UTC");
                if (!string.IsNullOrEmpty(ev.Category))
                    sb.Append(" &middot; ").Append(Html(ev.Category));
                if (!string.IsNullOrEmpty(ev.Location))
                    sb.Append(" &middot; ").Append(Html(ev.Location));
                sb.Append("</div>");
                if (!string.IsNullOrEmpty(ev.Description))
                    sb.Append("<p>").Append(Html(ev.Description)).Append("</p>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        // Viewer download list - "first-landing" content the user asked to
        // build from what already exists rather than inventing from scratch.
        // Originally seeded from the user's own OpenSim-Grid-Interface
        // (viewers.php) and WhiteCore-Dev's real help.html viewer list, but
        // several of those entries had gone stale by the time this page was
        // actually checked against current reality - per the user (2026-08-12,
        // not independently re-verified against each project's own site):
        // Alchemy and Kokua no longer support OpenSim at all, Singularity
        // hasn't been updated in years, and Lumiya/Pocket Metaverse are gone
        // entirely. Trimmed down to what's actually real today rather than
        // leaving dead links up - the two remaining graphical desktop
        // viewers (Firestorm, Cool VL Viewer), one text-based desktop
        // client still active (Radegast), and one old-but-still-around
        // mobile client (Mobile Grid Client). Not claiming this list is
        // exhaustive, just that everything on it is real.
        private static readonly (string Name, string Url, string Note)[] DesktopViewers =
        {
            ("Firestorm (Windows)", "https://www.firestormviewer.org/windows-for-open-simulator/", "OpenSim-specific build"),
            ("Firestorm (macOS)", "https://www.firestormviewer.org/mac-for-open-simulator/", "OpenSim-specific build"),
            ("Firestorm (Linux)", "https://www.firestormviewer.org/linux-for-open-simulator/", "OpenSim-specific build"),
            ("AyaneStorm", "https://github.com/AyaneStorm/ayanestorm/releases", "Firestorm fork for photographers - Windows/macOS/Linux"),
            ("Cool VL Viewer", "https://sldev.free.fr/", "Long-running, OpenSim-compatible"),
        };

        private static readonly (string Name, string Url, string Note)[] MobileViewers =
        {
            ("Mobile Grid Client", "http://mobilegridclient.com", "Android/iOS - older, not actively updated"),
            ("Radegast", "https://radegast.life/", "Text-based desktop client, still active"),
        };

        private void HandleViewers(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string loginUri = string.IsNullOrEmpty(m_publicBaseUrl) ? "(not configured)" : m_publicBaseUrl + "/";

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Get a Viewer</h1>");
            sb.Append("<p>").Append(Html(gridName)).Append(" runs on OpenSimulator, which works with Second Life-compatible ")
              .Append("viewers. Download one below, then add our grid using the login URI shown here.</p>");

            sb.Append("<h2>Login URI</h2>");
            sb.Append("<form onsubmit=\"return false;\"><label>Add this to your viewer's grid manager<br/>")
              .Append("<input type=\"text\" value=\"").Append(Html(loginUri)).Append("\" readonly onclick=\"this.select()\"></label></form>");
            sb.Append("<p><strong>Firestorm:</strong> Preferences &rarr; OpenSim &rarr; Add new grid &rarr; paste the login URI. ")
              .Append("<strong>Most other viewers:</strong> look for a Grid Manager or Grid Selector in Preferences or on the login screen.</p>");

            sb.Append("<h2>Desktop Viewers</h2><div class=\"widget-grid\">");
            foreach ((string name, string url, string note) in DesktopViewers)
                AppendViewerCard(sb, name, url, note);
            sb.Append("</div>");

            sb.Append("<h2>Mobile &amp; Lightweight Viewers</h2><div class=\"widget-grid\">");
            foreach ((string name, string url, string note) in MobileViewers)
                AppendViewerCard(sb, name, url, note);
            sb.Append("</div>");

            WritePage(request, response, PageTitle("Get a Viewer"), sb.ToString());
        }

        // Bare-chrome getting-started page, meant to be opened from a
        // viewer's own Help menu the same way "welcome" already points a
        // viewer at /welcome.php - an operator can point [GridInfoService]
        // help at this URL to wire that up. First-draft content: neither
        // WhiteCore-Dev nor OpenSim-Grid-Interface has a directly equivalent
        // page to build from, so this is new ground, not a port.
        // Content-parity pass (2026-08-12) against both real named
        // references, WhiteCore-Dev first per the user's explicit priority
        // ("that's where this all came from"): WhiteCore-Dev's own
        // help.html is mostly a login-URI display plus a viewer-download
        // gallery - already covered by Confluence's separate /viewers page,
        // so not duplicated here, but its login-URI framing is kept as the
        // lead section. The "Using Search"/"Troubleshooting" sections below
        // come from OpenSim-Grid-Interface's help.php, which covers ground
        // WhiteCore-Dev's version doesn't - real content, not invented,
        // adapted to Confluence's own search tabs and self-service pages.
        private void HandleHelp(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string loginUri = string.IsNullOrEmpty(m_publicBaseUrl) ? "(not configured)" : m_publicBaseUrl + "/";

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-question-circle\"></i> Help &amp; Support</h1>");
            sb.Append("<p>Quick help for using ").Append(Html(gridName)).Append(" both in your viewer and on the web.</p>");

            sb.Append("<h2><i class=\"bi bi-box-arrow-in-right\"></i> Logging In</h2>");
            sb.Append("<p>Add ").Append(Html(gridName)).Append(" to your viewer's grid manager using this login URI:</p>");
            sb.Append("<form onsubmit=\"return false;\"><label>Login URI<br/>")
              .Append("<input type=\"text\" value=\"").Append(Html(loginUri)).Append("\" readonly onclick=\"this.select()\"></label></form>");
            sb.Append("<p>Don't have a viewer yet? See <a href=\"").Append(BasePath).Append("/viewers\">Get a Viewer</a>.</p>");

            sb.Append("<h2><i class=\"bi bi-person-plus\"></i> Creating an Account</h2>");
            sb.Append("<p>Sign up for free from the home page. You'll get a full inventory and a home region ")
              .Append("assigned automatically.</p>");

            sb.Append("<h2><i class=\"bi bi-list-task\"></i> Common Tasks</h2><div class=\"feature-grid-3\">");
            AppendIconFeatureCard(sb, "person-gear", "Manage Your Account", new[]
            {
                ("Password & email", true, "Change both from My Account."),
                ("Profile", true, "Update your About text, picks and classifieds from your Profile page."),
                ("Regions", true, "See regions you own or manage from My Account.")
            });
            AppendIconFeatureCard(sb, "search", "Search, Friends &amp; Regions", new[]
            {
                ("Search", true, "Find places, events, classifieds, people, groups and land for sale."),
                ("Friends", true, "Manage your friends list from the Friends page or in-world."),
                ("Destinations", true, "Browse the Destination Guide for popular and featured places.")
            });
            sb.Append("</div>");

            sb.Append("<h2><i class=\"bi bi-search\"></i> Using Search From the Viewer</h2>");
            sb.Append("<p>Your viewer's Search window uses the same categories as the <a href=\"")
              .Append(BasePath).Append("/search\">Search</a> page in a normal browser:</p><ul>");
            sb.Append("<li><strong>Places</strong> - find regions and parcels by name, description or keyword.</li>");
            sb.Append("<li><strong>Land Sales</strong> - find parcels that are set for sale.</li>");
            sb.Append("<li><strong>Events</strong> - browse upcoming events; click one to see its details.</li>");
            sb.Append("<li><strong>Classifieds</strong> - resident-created ads for stores, clubs and services.</li>");
            sb.Append("<li><strong>People</strong> - search for residents by name.</li>");
            sb.Append("<li><strong>Groups</strong> - look up groups, then join them in-world.</li>");
            sb.Append("</ul>");

            sb.Append("<h2><i class=\"bi bi-tools\"></i> Troubleshooting</h2><ul>");
            sb.Append("<li><strong>A search tab shows no results:</strong> that category may simply have nothing ")
              .Append("listed yet - land only appears once a parcel owner sets it For Sale and enables Show in ")
              .Append("Search, and events/classifieds only appear once a resident creates one.</li>");
            sb.Append("<li><strong>Pages look cut off in the viewer:</strong> try resizing the window, or open the ")
              .Append("same page in an external browser instead.</li>");
            sb.Append("<li><strong>Password problems:</strong> use <a href=\"").Append(BasePath)
              .Append("/forgot-password\">Forgot Password</a> to reset it, then restart your viewer.</li>");
            sb.Append("</ul>");

            sb.Append("<h2><i class=\"bi bi-question-circle\"></i> Common Questions</h2>");
            sb.Append("<h3>How do I visit other grids?</h3><p>This grid supports Hypergrid teleporting - use a ")
              .Append("Hypergrid address in your viewer's map or search to visit another open grid.</p>");
            sb.Append("<h3>I need more help.</h3><p>Contact us through the <a href=\"").Append(BasePath)
              .Append("/support\">Support</a> page.</p>");

            WriteAdaptivePage(request, response, "Help - " + gridName, sb.ToString());
        }

        // Real, sparse OpenMetaverse.ParcelCategory labels a resident might
        // have actually set on their parcel - same categories a viewer's own
        // "About Land" category dropdown offers. Unset (0/None) parcels
        // fall back to "General" below, same as OpenSim-Grid-Interface's own
        // guide.php this page is modeled on.
        private static readonly Dictionary<int, string> ParcelCategories = new Dictionary<int, string>
        {
            { 3, "Arts & Culture" }, { 4, "Business" }, { 5, "Education" },
            { 6, "Gaming" }, { 7, "Hangout" }, { 8, "Newcomer" },
            { 9, "Parks & Nature" }, { 10, "Residential" }, { 11, "Shopping" },
            { 13, "Other" }, { 14, "Rental" }
        };

        // Native Destination Guide - what a viewer's Help > Destinations
        // floater opens (wired via [GridInfoService] DestinationGuide, the
        // same mechanism "welcome" already uses for the login splash).
        // Ported from OpenSim-Grid-Interface's own guide.php as-is (same
        // header/nav-tabs/card layout, same hue-tinted card-img fallback,
        // same meta-row badge+traffic treatment), per explicit direction to
        // keep that page's actual design rather than reusing Confluence's
        // generic widget-card styling - only the colors are swapped to this
        // site's own theme tokens (guide.php's own CSS is porting-target,
        // not this connector's PageCss). Data comes from Confluence's own
        // ISearchService/IGridService instead of guide.php's raw SQL, same
        // as /destinations already does. Deliberately NOT sharing
        // AppendDestinationTabs with /destinations (see HandleDestinations)
        // - guide.php and destinations.php are two genuinely different
        // pages on the reference site (no search/filters/pagination here,
        // just tabs+cards, sized for the viewer's small embedded browser
        // panel), not one page in two wrappers. Bare chrome via
        // WriteAdaptivePage/WriteBarePage since this renders inside that
        // panel, same as Help/About/the login splash.
        private void HandleGuide(IOSHttpRequest request, IOSHttpResponse response)
        {
            const int maxAccess = 13; // PG - same safe default HandleSearch uses with no explicit maturity preference.
            StringBuilder sb = new StringBuilder(GuideCss);

            // guide.php's own body is "height:100vh; overflow:hidden" with
            // only its inner .viewport scrolling - the header/tabs never
            // move, and at default floater size nothing needs to scroll at
            // all. The first cut of this port dropped that: WriteBarePage's
            // shared .page/.card padding (~150px combined) plus this page's
            // own header/tabs/grid stacked in normal document flow made the
            // whole page taller than the floater and left it scrolling from
            // the very first card - not how the reference behaves. Fixed
            // by reproducing the same fixed-viewport-height/inner-scroll
            // split, but only inside the viewer's actual embedded panel
            // (IsViewerRequest) - forcing height:100vh in a real browser
            // tab would clip WritePage's own header/footer chrome, which
            // guide.php never had to account for since it has no site chrome
            // at all.
            bool isViewerPanel = IsViewerRequest(request, response);
            if (isViewerPanel)
                sb.Append(GuideFixedHeightCss);

            sb.Append("<div class=\"guide-header\"><div class=\"guide-brand\"><i class=\"bi bi-compass\"></i> Guide</div>")
              .Append("<div class=\"guide-nav-tabs\">")
              .Append("<button type=\"button\" class=\"guide-nav-btn active\" id=\"guide-btn-popular\" onclick=\"return guideTab('popular',this)\">Popular</button>")
              .Append("<button type=\"button\" class=\"guide-nav-btn\" id=\"guide-btn-featured\" onclick=\"return guideTab('featured',this)\">Featured</button>")
              .Append("<button type=\"button\" class=\"guide-nav-btn\" id=\"guide-btn-discover\" onclick=\"return guideTab('discover',this)\">Discover</button>")
              .Append("</div></div>");

            sb.Append("<div class=\"guide-viewport\">");

            sb.Append("<div id=\"guide-view-popular\" class=\"guide-view-section active\">");
            if (m_SearchService == null)
                sb.Append("<div class=\"guide-empty\">Search is not available.</div>");
            else
                AppendGuideDestinationCards(sb, m_SearchService.SearchPlaces(string.Empty, 0, 30, maxAccess), "No popular places found yet.", showTraffic: true);
            sb.Append("</div>");

            sb.Append("<div id=\"guide-view-featured\" class=\"guide-view-section\">");
            if (m_SearchService == null)
                sb.Append("<div class=\"guide-empty\">Search is not available.</div>");
            else
                AppendGuideDestinationCards(sb, m_SearchService.GetFeaturedPlaces(30, maxAccess), "No featured places found yet.", showTraffic: false);
            sb.Append("</div>");

            sb.Append("<div id=\"guide-view-discover\" class=\"guide-view-section\">");
            AppendGuideDiscoverCards(sb);
            sb.Append("</div>");

            sb.Append("</div>"); // .guide-viewport

            // Client-side tab switch only, matching guide.php's own script -
            // no page reload, small-panel feel. Scoped to guide- prefixed
            // ids/classes so it can't collide with the shared DropdownScript
            // (not included by WriteBarePage anyway) or any other page.
            sb.Append("<script>function guideTab(name,el){")
              .Append("['popular','featured','discover'].forEach(function(n){")
              .Append("document.getElementById('guide-view-'+n).classList.toggle('active',n===name);});")
              .Append("document.querySelectorAll('.guide-nav-btn').forEach(function(b){b.classList.remove('active');});")
              .Append("el.classList.add('active');return false;}</script>");

            WriteAdaptivePage(request, response, PageTitle("Destination Guide"), sb.ToString());
        }

        // Only ever appended for a real viewer panel (see HandleGuide) -
        // reuses body/.site-main/.page/.card, the exact same selectors
        // PageCss already defines, so these win by document order (this
        // <style> block ends up later in the document than PageCss's own,
        // which lives in <head>) without touching PageCss itself or any
        // other page that shares it.
        private const string GuideFixedHeightCss =
                "<style>" +
                "html,body{height:100%;}" +
                "body{height:100vh;overflow:hidden;display:flex;flex-direction:column;}" +
                ".site-main{padding:0;flex:1;display:flex;flex-direction:column;min-height:0;}" +
                ".page{max-width:100%;margin:0;padding:0;flex:1;display:flex;flex-direction:column;min-height:0;}" +
                ".card{padding:10px;flex:1;display:flex;flex-direction:column;min-height:0;" +
                "border-radius:0;border:none;box-shadow:none;}" +
                ".guide-header{flex:0 0 auto;}" +
                ".guide-viewport{flex:1 1 auto;overflow-y:auto;min-height:0;}" +
                "</style>";

        // Scoped (guide- prefixed) so it can't collide with the site-wide
        // .card WriteBarePage already wraps this content in, or with
        // /destinations' own .widget-card styling. Colors come from this
        // site's own CSS custom properties (PageCss's :root block, already
        // in scope since WriteBarePage always includes PageCss) rather than
        // guide.php's hardcoded dark grays/blue - same layout, this site's
        // theme.
        private const string GuideCss =
                "<style>" +
                ".guide-header{display:flex;justify-content:space-between;align-items:center;" +
                "flex-wrap:wrap;gap:8px;margin-bottom:12px;padding-bottom:10px;border-bottom:1px solid var(--border);}" +
                ".guide-brand{font-weight:700;color:var(--text);display:flex;align-items:center;gap:6px;}" +
                ".guide-nav-tabs{display:flex;background:var(--input-bg);padding:2px;border-radius:6px;gap:2px;}" +
                ".guide-nav-btn{background:transparent;border:none;color:var(--muted);padding:4px 10px;" +
                "border-radius:4px;cursor:pointer;font-size:11px;text-transform:uppercase;letter-spacing:.5px;" +
                "font-family:inherit;}" +
                ".guide-nav-btn.active{background:var(--accent);color:#fff;font-weight:600;}" +
                ".guide-view-section{display:none;}" +
                ".guide-view-section.active{display:block;}" +
                ".guide-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:10px;}" +
                ".guide-card{background:var(--card-bg);border:1px solid var(--border);border-radius:6px;" +
                "overflow:hidden;display:flex;flex-direction:column;transition:transform .1s,border-color .1s;}" +
                ".guide-card:hover{border-color:var(--accent);transform:translateY(-1px);}" +
                ".guide-card-img{height:85px;display:flex;align-items:center;justify-content:center;" +
                "background-size:cover;background-position:center;font-weight:700;font-size:24px;" +
                "text-shadow:0 2px 4px rgba(0,0,0,.5);}" +
                ".guide-card-body{padding:8px;flex:1;display:flex;flex-direction:column;}" +
                ".guide-card-title{font-weight:600;font-size:12px;margin-bottom:2px;white-space:nowrap;" +
                "overflow:hidden;text-overflow:ellipsis;color:var(--text);}" +
                ".guide-card-sub{font-size:10px;color:var(--muted);margin-bottom:6px;}" +
                ".guide-meta-row{display:flex;align-items:center;gap:8px;margin-bottom:8px;}" +
                ".guide-badge{background:var(--input-bg);color:var(--muted);padding:2px 5px;border-radius:3px;font-size:10px;}" +
                ".guide-traffic{font-size:10px;color:var(--muted);display:flex;align-items:center;gap:3px;}" +
                ".guide-card-desc{font-size:11px;color:var(--muted);margin-bottom:8px;height:2.4em;overflow:hidden;" +
                "line-height:1.2;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;}" +
                ".guide-btn-tp{margin-top:auto;display:block;text-align:center;background:var(--input-bg);" +
                "color:var(--text);text-decoration:none;padding:5px;border-radius:4px;font-size:11px;" +
                "border:1px solid var(--border);}" +
                ".guide-btn-tp:hover{background:var(--accent);color:#fff;border-color:var(--accent);text-decoration:none;}" +
                ".guide-empty{text-align:center;color:var(--muted);padding:40px;font-style:italic;}" +
                "</style>";

        // Same hue formula guide.php uses (a stable per-name color so the
        // same place always gets the same card-img tint), but a hand-rolled
        // stable hash instead of C#'s string.GetHashCode() - .NET randomizes
        // string hash codes per process by default, which would make every
        // place's color reshuffle on every region restart.
        private static int StableHueFor(string name)
        {
            unchecked
            {
                int hash = 23;
                foreach (char c in name)
                    hash = hash * 31 + c;
                return Math.Abs(hash) % 360;
            }
        }

        private void AppendGuideDestinationCards(StringBuilder sb, List<LandSearchRecord> places, string emptyMessage, bool showTraffic)
        {
            if (places == null || places.Count == 0)
            {
                sb.Append("<div class=\"guide-empty\">").Append(Html(emptyMessage)).Append("</div>");
                return;
            }

            sb.Append("<div class=\"guide-grid\">");
            foreach (LandSearchRecord place in places)
            {
                string categoryLabel = ParcelCategories.TryGetValue(place.Category, out string label) ? label : "General";
                bool hasLanding = place.LandingX != 0f || place.LandingY != 0f;
                float tpX = hasLanding ? place.LandingX : 128f;
                float tpY = hasLanding ? place.LandingY : 128f;
                float tpZ = hasLanding ? place.LandingZ : 25f;
                string tp = "secondlife:///app/teleport/" + Uri.EscapeDataString(place.RegionName ?? string.Empty)
                        + "/" + (int)tpX + "/" + (int)tpY + "/" + (int)tpZ;
                int hue = StableHueFor(place.RegionName ?? place.Name ?? string.Empty);
                string initial = string.IsNullOrEmpty(place.Name) ? "?" : place.Name.Substring(0, 1);

                sb.Append("<div class=\"guide-card\">")
                  .Append("<div class=\"guide-card-img\" style=\"background-color:hsl(").Append(hue)
                  .Append(",30%,25%);color:hsl(").Append(hue).Append(",80%,70%);\">")
                  .Append(Html(initial)).Append("</div>")
                  .Append("<div class=\"guide-card-body\">")
                  .Append("<div class=\"guide-card-title\">").Append(Html(place.Name)).Append("</div>")
                  .Append("<div class=\"guide-card-sub\">").Append(Html(place.RegionName)).Append("</div>")
                  .Append("<div class=\"guide-meta-row\"><span class=\"guide-badge\">").Append(Html(categoryLabel)).Append("</span>");
                if (showTraffic && place.Dwell > 0)
                {
                    sb.Append("<span class=\"guide-traffic\">&#9679; Traffic: ").Append(((int)place.Dwell).ToString("N0")).Append("</span>");
                }
                sb.Append("</div>");
                if (!string.IsNullOrEmpty(place.Description))
                {
                    string description = place.Description.Length > 90 ? place.Description.Substring(0, 90) + "..." : place.Description;
                    sb.Append("<div class=\"guide-card-desc\">").Append(Html(description)).Append("</div>");
                }
                sb.Append("<a href=\"").Append(Html(tp)).Append("\" class=\"guide-btn-tp\">Teleport</a>")
                  .Append("</div></div>");
            }
            sb.Append("</div>");
        }

        private void AppendGuideDiscoverCards(StringBuilder sb)
        {
            if (m_GridService == null)
            {
                sb.Append("<div class=\"guide-empty\">Grid service is not available.</div>");
                return;
            }

            List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
            regions.Sort((a, b) => string.Compare(a.RegionName, b.RegionName, StringComparison.OrdinalIgnoreCase));
            if (regions.Count == 0)
            {
                sb.Append("<div class=\"guide-empty\">No online regions found.</div>");
                return;
            }

            sb.Append("<div class=\"guide-grid\">");
            foreach (GridRegion region in regions.Take(50))
            {
                string tp = "secondlife:///app/teleport/" + Uri.EscapeDataString(region.RegionName) + "/128/128/25";
                int hue = StableHueFor(region.RegionName);
                string initial = string.IsNullOrEmpty(region.RegionName) ? "?" : region.RegionName.Substring(0, 1);

                sb.Append("<div class=\"guide-card\">")
                  .Append("<div class=\"guide-card-img\" style=\"background-color:hsl(").Append(hue)
                  .Append(",30%,25%);color:hsl(").Append(hue).Append(",80%,70%);\">")
                  .Append(Html(initial)).Append("</div>")
                  .Append("<div class=\"guide-card-body\">")
                  .Append("<div class=\"guide-card-title\">").Append(Html(region.RegionName)).Append("</div>")
                  .Append("<div class=\"guide-card-sub\">Online Region</div>")
                  .Append("<div class=\"guide-meta-row\"><span class=\"guide-badge\">Online</span></div>")
                  .Append("<a href=\"").Append(Html(tp)).Append("\" class=\"guide-btn-tp\">Teleport</a>")
                  .Append("</div></div>");
            }
            sb.Append("</div>");
        }

        // Popular/Featured/Discover browse-with-teleport tabs for the
        // human-facing /destinations page (see HandleDestinations below) -
        // reimplemented against this connector's own ISearchService/
        // IGridService rather than raw SQL. NOT shared with /guide (see
        // HandleGuide) - guide.php and destinations.php are two genuinely
        // different pages on the reference site (destinations.php has
        // search/filters/pagination this doesn't need to match, guide.php
        // is the simple viewer-panel version), confirmed after this
        // connector previously merged them into one shared implementation
        // by mistake. Previously /destinations wrongly held the Leaflet
        // world map instead of this (see HandleWorldMap, now split out to
        // its own /worldmap route - Destinations and World Map are two
        // separate real features on the reference site too, not one page).
        private void AppendDestinationTabs(StringBuilder sb)
        {
            const int maxAccess = 13; // PG - same safe default HandleSearch uses with no explicit maturity preference.

            sb.Append("<div class=\"subnav\">")
              .Append("<a href=\"#\" class=\"active\" onclick=\"return guideTab('popular',this)\">Popular</a>")
              .Append("<a href=\"#\" onclick=\"return guideTab('featured',this)\">Featured</a>")
              .Append("<a href=\"#\" onclick=\"return guideTab('discover',this)\">Discover</a>")
              .Append("</div>");

            sb.Append("<div id=\"guide-popular\">");
            if (m_SearchService == null)
                sb.Append("<p>Search is not available.</p>");
            else
                AppendGuideCards(sb, m_SearchService.SearchPlaces(string.Empty, 0, 30, maxAccess), "No popular places found yet.");
            sb.Append("</div>");

            sb.Append("<div id=\"guide-featured\" style=\"display:none\">");
            if (m_SearchService == null)
                sb.Append("<p>Search is not available.</p>");
            else
                AppendGuideCards(sb, m_SearchService.GetFeaturedPlaces(30, maxAccess), "No featured places found yet.");
            sb.Append("</div>");

            sb.Append("<div id=\"guide-discover\" style=\"display:none\">");
            if (m_GridService == null)
            {
                sb.Append("<p>Grid service is not available.</p>");
            }
            else
            {
                List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
                regions.Sort((a, b) => string.Compare(a.RegionName, b.RegionName, StringComparison.OrdinalIgnoreCase));
                if (regions.Count == 0)
                {
                    sb.Append("<p>No online regions found.</p>");
                }
                else
                {
                    sb.Append("<div class=\"widget-grid\">");
                    foreach (GridRegion region in regions.Take(50))
                    {
                        string tp = "secondlife:///app/teleport/" + Uri.EscapeDataString(region.RegionName) + "/128/128/25";
                        sb.Append("<div class=\"widget-card\"><h3>").Append(Html(region.RegionName)).Append("</h3>")
                          .Append("<div class=\"widget-meta\">Online</div>")
                          .Append("<p><a href=\"").Append(Html(tp)).Append("\">Teleport &rarr;</a></p></div>");
                    }
                    sb.Append("</div>");
                }
            }
            sb.Append("</div>");

            // Client-side tab switch only - no page reload, matching the
            // small-panel feel of an embedded viewer browser. Scoped to this
            // page only (WriteBarePage doesn't include the shared
            // DropdownScript, which is for the full-chrome nav dropdowns).
            sb.Append("<script>function guideTab(name,el){")
              .Append("['popular','featured','discover'].forEach(function(n){")
              .Append("document.getElementById('guide-'+n).style.display=(n===name)?'':'none';});")
              .Append("el.parentNode.querySelectorAll('a').forEach(function(a){a.classList.remove('active');});")
              .Append("el.classList.add('active');return false;}</script>");
        }

        private void AppendGuideCards(StringBuilder sb, List<LandSearchRecord> places, string emptyMessage)
        {
            if (places == null || places.Count == 0)
            {
                sb.Append("<p>").Append(Html(emptyMessage)).Append("</p>");
                return;
            }

            sb.Append("<div class=\"widget-grid\">");
            foreach (LandSearchRecord place in places)
            {
                string categoryLabel = ParcelCategories.TryGetValue(place.Category, out string label) ? label : "General";
                bool hasLanding = place.LandingX != 0f || place.LandingY != 0f;
                float tpX = hasLanding ? place.LandingX : 128f;
                float tpY = hasLanding ? place.LandingY : 128f;
                float tpZ = hasLanding ? place.LandingZ : 25f;
                string tp = "secondlife:///app/teleport/" + Uri.EscapeDataString(place.RegionName ?? string.Empty)
                        + "/" + (int)tpX + "/" + (int)tpY + "/" + (int)tpZ;

                sb.Append("<div class=\"widget-card\"><h3>").Append(Html(place.Name)).Append("</h3>");
                sb.Append("<div class=\"widget-meta\">").Append(Html(categoryLabel));
                if (!string.IsNullOrEmpty(place.RegionName))
                    sb.Append(" &middot; ").Append(Html(place.RegionName));
                if (place.Dwell > 0)
                    sb.Append(" &middot; Traffic: ").Append(((int)place.Dwell).ToString("N0"));
                sb.Append("</div>");
                if (!string.IsNullOrEmpty(place.Description))
                {
                    string description = place.Description.Length > 140 ? place.Description.Substring(0, 140) + "..." : place.Description;
                    sb.Append("<p>").Append(Html(description)).Append("</p>");
                }
                sb.Append("<p><a href=\"").Append(Html(tp)).Append("\">Teleport &rarr;</a></p></div>");
            }
            sb.Append("</div>");
        }

        private static void AppendViewerCard(StringBuilder sb, string name, string url, string note)
        {
            sb.Append("<div class=\"widget-card\"><h3>").Append(Html(name)).Append("</h3>")
              .Append("<div class=\"widget-meta\">").Append(Html(note)).Append("</div>")
              .Append("<p><a href=\"").Append(Html(url)).Append("\" target=\"_blank\" rel=\"noopener\">Download &rarr;</a></p></div>");
        }

        private static void AppendFeatureCard(StringBuilder sb, string name, string status, string description)
        {
            sb.Append("<div class=\"widget-card\"><h3>").Append(Html(name)).Append("</h3>")
              .Append("<div class=\"widget-meta\">").Append(Html(status)).Append("</div>")
              .Append("<p>").Append(Html(description)).Append("</p></div>");
        }

        // Real Destinations page - Popular/Featured/Discover browse-with-
        // teleport, full site chrome for human visitors. See
        // AppendDestinationTabs for why this is a separate implementation
        // from the viewer-embedded /guide rather than a shared one.
        private void HandleDestinations(IOSHttpRequest request, IOSHttpResponse response)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-signpost-2\"></i> Destinations</h1>")
              .Append("<p>Discover places worth visiting across the grid.</p>");
            AppendDestinationTabs(sb);
            WritePage(request, response, PageTitle("Destinations"), sb.ToString());
        }

        // World Map - the "fill in blanks" counterpart to
        // Viewers above. WhiteCore-Dev's own actual equivalent (world.html)
        // is a real, currently-used feature there (unlike region_list.html/
        // region_search.html/online_users.html, which are all explicitly
        // commented "No longer used" by WhiteCore's own maintainers - see
        // PROJECT_LOG.md) - a Leaflet.js map with a region-thumbnail
        // sidebar and hop:// teleport links. Rather than vendoring Leaflet
        // and its custom mapapi.js (uninspected third-party JS, and this
        // connector has held a strict no-external-dependency line all
        // session), this reproduces the same user-facing value - see where
        // regions actually sit relative to each other, click to view/
        // teleport - as a plain CSS absolute-position layout using map
        // tiles this connector's own MapGetServiceConnector already serves
        // (confirmed the real tile path/naming by reading
        // MapImageService.GetFileName: "/map/map-1-{RegionCoordX}-
        // {RegionCoordY}-objects.jpg"). Teleport links use
        // secondlife:///app/teleport/, the same scheme OpenSim-Grid-
        // Interface's guide.php uses for same-grid destinations - simpler
        // and more directly applicable here than hop://, which is for
        // cross-grid Hypergrid teleports.
        // Real Leaflet map, not the earlier CSS-absolute-position
        // reproduction - the user's own OpenSim-Grid-Interface project
        // (maps/map-script.js, "Casperia Prime World Map") already solved
        // this exact problem: L.CRS.Simple (map coordinates ARE region-grid
        // units, not real lat/lng) + one L.imageOverlay per region tile
        // (not a tile layer - region tiles aren't a standard XYZ pyramid).
        // Adapted directly from that real, working script rather than
        // re-deriving the coordinate math - the only real changes are:
        // region data is rendered server-side as inline JSON (no separate
        // map-data.php JSON API needed, this connector already has the
        // region list at page-render time) and tile URLs point straight at
        // this connector's own /map/ route (now byte-correct - see the
        // MapGetServerConnector path-construction fix) instead of through a
        // PHP tile-proxy script. Leaflet itself is vendored (leaflet.css/
        // leaflet.js, see StaticAssetContentTypes) not CDN-linked, same
        // policy as Bootstrap Icons.
        private void HandleWorldMap(IOSHttpRequest request, IOSHttpResponse response)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>World Map</h1><p>Explore regions on this grid. Click a region to see details and teleport.</p>");

            if (m_GridService == null)
            {
                sb.Append("<p>Grid service is not available.</p>");
                WritePage(request, response, PageTitle("World Map"), sb.ToString());
                return;
            }

            List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
            if (regions.Count == 0)
            {
                sb.Append("<p>No regions are online yet.</p>");
                WritePage(request, response, PageTitle("World Map"), sb.ToString());
                return;
            }

            // Same alive-region probe HandleWelcome/HandleGridStatus already
            // use. The map tiles themselves are built ONLY from this list,
            // not the full `regions` set - a region that's registered but
            // not actually running has no live tile server to fetch a
            // current image from anyway, and showing its (possibly very
            // stale) last-known tile as if it were a real, visitable place
            // is misleading. The "All Regions" table below still lists
            // every registered region regardless of status, with its own
            // Online/Offline column, so nothing about the grid's roster is
            // hidden - only the interactive map itself is online-only.
            List<GridRegion> aliveRegions = FilterOnlineRegions(regions);
            // aliveRegionIDs stays unfiltered by Unlisted - it also drives
            // the "Show Users" online-avatar overlay below, and a resident
            // standing in an unlisted region is still really there. Only
            // the map tiles and the "All Regions" table (built from
            // listedRegions/listedAliveRegions below) hide Unlisted
            // regions themselves.
            HashSet<string> aliveRegionIDs = new HashSet<string>(aliveRegions.Select(r => r.RegionID.ToString()));
            List<GridRegion> listedRegions = FilterListedRegions(regions);
            List<GridRegion> listedAliveRegions = FilterListedRegions(aliveRegions);

            // Resolved once per unique estate owner, not once per region -
            // a grid where one resident owns many regions shouldn't cost
            // one UserAccountService lookup per region. Shared by both the
            // map-tile loop (alive regions only) and the full "All Regions"
            // table below (every registered region), so an owner looked up
            // for one isn't looked up again for the other.
            Dictionary<UUID, string> ownerNames = new Dictionary<UUID, string>();
            string ResolveOwnerName(UUID estateOwner)
            {
                if (estateOwner == UUID.Zero || m_UserAccountService == null)
                    return "Unknown";
                if (ownerNames.TryGetValue(estateOwner, out string cached))
                    return cached;
                UserAccount ownerAccount = m_UserAccountService.GetUserAccount(UUID.Zero, estateOwner);
                string resolved = ownerAccount != null ? ownerAccount.Name : "Unknown";
                ownerNames[estateOwner] = resolved;
                return resolved;
            }

            // Viewer-context here means an already-running viewer's own
            // embedded browser (worldmap isn't a pre-login page like
            // welcome.php - a logged-in session is the reasonable
            // assumption), so /app/teleport/ is the right live-teleport
            // command. A real external browser gets hop:// - see
            // BuildHopUrl's own comment for why a bare secondlife:// SLURL
            // isn't enough for a stranger whose viewer may not default to
            // this grid.
            bool isViewerContext = IsViewerRequest(request, response);

            OSDArray regionArray = new OSDArray();
            foreach (GridRegion region in listedAliveRegions)
            {
                OSDMap r = new OSDMap();
                r["name"] = region.RegionName;
                r["uuid"] = region.RegionID.ToString();
                r["gridX"] = region.RegionCoordX;
                r["gridY"] = region.RegionCoordY;
                r["sizeX"] = region.RegionSizeX;
                r["sizeY"] = region.RegionSizeY;
                r["teleportUrl"] = isViewerContext
                        ? "secondlife:///app/teleport/" + Uri.EscapeDataString(region.RegionName) + "/128/128/25"
                        : BuildHopUrl(region.RegionName);
                r["owner"] = ResolveOwnerName(region.EstateOwner);

                int unitsX = Math.Max(1, region.RegionSizeX / 256);
                int unitsY = Math.Max(1, region.RegionSizeY / 256);
                r["sizeLabel"] = unitsX + "×" + unitsY;

                // Absence of a row in IRegionHGService means open (its own
                // documented default) - matches how HandleAdminStats/
                // HandleAdmin already treat a null service the same way.
                r["hgOpen"] = m_RegionHGService == null || m_RegionHGService.IsRegionOpen(region.RegionID);

                regionArray.Add(r);
            }
            string regionJson = OSDParser.SerializeJsonString(regionArray);

            // "Show Users" data - built from the same alive-region set as
            // the tiles above. GridUserInfo.UserID is a plain UUID for a
            // local resident but a "UUID;homeURI;First Last;secret"
            // universal identifier for a Hypergrid visitor (same format
            // HandleFriends already has to account for) - resolved the
            // same way there, not skipped here.
            OSDArray userArray = new OSDArray();
            if (m_GridUserService != null)
            {
                foreach (GridUserInfo info in m_GridUserService.GetOnlineUsers(aliveRegionIDs))
                {
                    GridRegion userRegion = aliveRegions.Find(reg => reg.RegionID == info.LastRegionID);
                    if (userRegion == null)
                        continue;

                    string userName = null;
                    if (UUID.TryParse(info.UserID, out UUID localId))
                    {
                        UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, localId);
                        userName = account?.Name;
                    }
                    else if (Util.ParseUniversalUserIdentifier(info.UserID, out UUID _, out string _, out string hgFirst, out string hgLast))
                    {
                        userName = (hgFirst + " " + hgLast).Trim();
                    }
                    if (string.IsNullOrEmpty(userName))
                        continue;

                    OSDMap u = new OSDMap();
                    u["name"] = userName;
                    // Region's own grid-unit origin plus this avatar's
                    // in-region meters position, converted into the same
                    // grid-unit space the region tiles are drawn in (1
                    // grid unit = 256m) - matches gridX/gridY above.
                    u["x"] = userRegion.RegionCoordX + (info.LastPosition.X / 256.0);
                    u["y"] = userRegion.RegionCoordY + (info.LastPosition.Y / 256.0);
                    userArray.Add(u);
                }
            }
            string userJson = OSDParser.SerializeJsonString(userArray);

            sb.Append("<link rel=\"stylesheet\" href=\"/static/leaflet.css\">");
            sb.Append("<style>#worldMap{width:100%;height:640px;border-radius:8px;border:1px solid var(--border);background:#0a0a0a;}" +
                    ".region-popup h3{margin:0 0 6px;font-size:14px;}" +
                    ".region-popup .wm-meta{color:var(--muted);font-size:12px;margin:0 0 4px;}" +
                    ".region-popup a.wm-tp{display:inline-block;background:var(--accent);color:#fff;padding:6px 14px;" +
                    "border-radius:40px;font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:.3px;margin-top:6px;}" +
                    ".region-popup a.wm-tp:hover{background:var(--accent-dark);text-decoration:none;}" +
                    ".leaflet-popup-content-wrapper{background:var(--card-bg);color:var(--text);border-radius:8px;}" +
                    ".leaflet-popup-tip{background:var(--card-bg);}" +
                    ".map-toolbar{display:flex;align-items:center;gap:16px;margin:0 0 10px;flex-wrap:wrap;}" +
                    "#mapSearch{max-width:260px;margin:0;}" +
                    ".map-toolbar label{display:flex;align-items:center;gap:6px;font-size:13.5px;font-weight:600;color:var(--muted);margin:0;}" +
                    ".map-toolbar label input{width:auto;margin:0;}" +
                    "</style>");
            sb.Append("<div class=\"map-toolbar\">")
              .Append("<input type=\"text\" id=\"mapSearch\" placeholder=\"Search regions…\" autocomplete=\"off\">")
              .Append("<label><input type=\"checkbox\" id=\"mapShowUsers\"> Show Users</label>")
              .Append("</div>");
            sb.Append("<div id=\"worldMap\"></div>");
            sb.Append("<script src=\"/static/leaflet.js\"></script>");
            sb.Append("<script>(function(){");
            sb.Append("var regions=").Append(regionJson).Append(";");
            sb.Append("var onlineUsers=").Append(userJson).Append(";");
            // maxZoom raised 6->8: bounds are defined in whole grid-units
            // (see imgBounds below), so at zoom level N one region is
            // 2^N CSS pixels wide - zoom 8 is where that reaches 256px,
            // the tiles' own native resolution. Below 8 the map was
            // capped well short of the actual image detail; going further
            // than 8 just upscales/blurs the same fixed-resolution JPEGs
            // (there's no higher-res source to reveal), so 8 is the real
            // ceiling, not an arbitrary number.
            sb.Append("var map=L.map('worldMap',{crs:L.CRS.Simple,minZoom:-4,maxZoom:8,attributionControl:false});");
            sb.Append("var bounds=L.latLngBounds([]);");
            sb.Append("var byName={};"); // lowercased region name -> {center:[y,x], layer:firstTileLayer}
            sb.Append("regions.forEach(function(r){");
            sb.Append("var tilesX=Math.max(1,Math.ceil(r.sizeX/256)),tilesY=Math.max(1,Math.ceil(r.sizeY/256));");
            sb.Append("var firstLayer=null,cy=r.gridY+tilesY/2,cx=r.gridX+tilesX/2;");
            sb.Append("for(var ty=0;ty<tilesY;ty++){for(var tx=0;tx<tilesX;tx++){");
            sb.Append("var x=r.gridX+tx,y=r.gridY+ty;");
            sb.Append("var imgBounds=[[y,x],[y+1,x+1]];bounds.extend(imgBounds[0]);bounds.extend(imgBounds[1]);");
            // Each 256m cell of a var region gets its own map tile, uploaded
            // separately at its own grid coordinate (see
            // MapImageServiceModule.UploadMapTile's sub-tile splitting) - the
            // tile URL has to be built per-cell from x/y here, not reused
            // from a single region-wide URL, or every cell of a larger-than-
            // 256m region shows the same one corner tile stretched/repeated
            // across its whole footprint.
            sb.Append("var tileUrl='/map/map-1-'+x+'-'+y+'-objects.jpg';");
            // interactive:true is required - L.imageOverlay defaults to
            // NOT firing mouse events at all (unlike L.marker/L.path),
            // confirmed live: clicking a tile did nothing whatsoever
            // without this, popup bound or not.
            sb.Append("var layer=L.imageOverlay(tileUrl,imgBounds,{opacity:1,interactive:true});");
            sb.Append("var popupHtml='<div class=\"region-popup\"><h3>'+r.name.replace(/</g,'&lt;')+'</h3>'+" +
                    "'<div class=\"wm-meta\">'+r.sizeLabel+' region ('+r.sizeX+'m &times; '+r.sizeY+'m) &middot; ('+r.gridX+', '+r.gridY+')</div>'+" +
                    "'<div class=\"wm-meta\">Owner: '+r.owner.replace(/</g,'&lt;')+'</div>'+" +
                    "'<div class=\"wm-meta\">Hypergrid: '+(r.hgOpen?'Open':'Closed')+'</div>'+" +
                    "'<a class=\"wm-tp\" href=\"'+r.teleportUrl+'\">Teleport &rarr;</a></div>';");
            sb.Append("layer.bindPopup(popupHtml,{className:'region-popup-wrap',closeButton:true});");
            sb.Append("layer.addTo(map);if(!firstLayer)firstLayer=layer;}}");
            sb.Append("byName[r.name.toLowerCase()]={center:[cy,cx],layer:firstLayer};");
            sb.Append("});");
            sb.Append("if(bounds.isValid())map.fitBounds(bounds,{padding:[24,24]});else map.setView([1000,1000],4);");

            sb.Append("var usersLayer=L.layerGroup();");
            sb.Append("onlineUsers.forEach(function(u){");
            // Styled via Leaflet's own circleMarker options, not a CSS
            // class - a circleMarker renders as an SVG <path>, and CSS
            // box-model properties (background/border/border-radius/
            // box-shadow) silently do nothing on SVG elements. Confirmed
            // live: the marker WAS in the DOM and toggling correctly the
            // whole time, just rendered invisible-ish at Leaflet's default
            // 20%-opacity blue fill with no styling actually landing.
            sb.Append("L.circleMarker([u.y,u.x],{radius:6,color:'#fff',weight:2,fillColor:'#22d3ee',fillOpacity:1})" +
                    ".bindTooltip(u.name.replace(/</g,'&lt;')).addTo(usersLayer);");
            sb.Append("});");
            sb.Append("document.getElementById('mapShowUsers').addEventListener('change',function(e){");
            sb.Append("if(e.target.checked)usersLayer.addTo(map);else map.removeLayer(usersLayer);});");

            sb.Append("var searchBox=document.getElementById('mapSearch');");
            sb.Append("function doSearch(){");
            sb.Append("var q=searchBox.value.trim().toLowerCase();if(!q)return;");
            sb.Append("var hit=byName[q];");
            sb.Append("if(!hit){for(var k in byName){if(k.indexOf(q)!==-1){hit=byName[k];break;}}}");
            sb.Append("if(hit){map.setView(hit.center,3);if(hit.layer)hit.layer.openPopup();}");
            sb.Append("}");
            sb.Append("searchBox.addEventListener('keydown',function(e){if(e.key==='Enter'){e.preventDefault();doSearch();}});");
            sb.Append("})();</script>");

            sb.Append("<h2>All Regions</h2><p class=\"news-meta\">Every publicly-listed region on this grid - the map above only draws the ones actually online right now.</p>");
            sb.Append("<table><tr><th>Region</th><th>Status</th><th>Size</th><th>Owner</th><th></th></tr>");
            // Alphabetical, not registration/DB order - found live: the
            // list had no sort applied at all. listedRegions (not regions)
            // so an owner's Unlisted opt-out is honored here too, not just
            // on the map.
            foreach (GridRegion region in listedRegions.OrderBy(r => r.RegionName, StringComparer.OrdinalIgnoreCase))
            {
                string teleportUrl = isViewerContext
                        ? "secondlife:///app/teleport/" + Uri.EscapeDataString(region.RegionName) + "/128/128/25"
                        : BuildHopUrl(region.RegionName);
                bool isOnline = aliveRegionIDs.Contains(region.RegionID.ToString());
                sb.Append("<tr><td>").Append(Html(region.RegionName)).Append("</td>")
                  .Append("<td><span class=\"pill ").Append(isOnline ? "pill-yes\">Online" : "pill-no\">Offline").Append("</span></td>")
                  .Append("<td>").Append(region.RegionSizeX).Append("x").Append(region.RegionSizeY).Append("</td>")
                  .Append("<td>").Append(Html(ResolveOwnerName(region.EstateOwner))).Append("</td>")
                  .Append("<td>").Append(isOnline ? "<a href=\"" + Html(teleportUrl) + "\">Teleport</a>" : string.Empty).Append("</td></tr>");
            }
            sb.Append("</table>");

            WritePage(request, response, PageTitle("World Map"), sb.ToString());
        }

        // Web Profile - the biggest single gap found in the full WhiteCore-Dev
        // audit ("genuinely new ground, not in Confluence at all"): WhiteCore's
        // webprofile/modal_profile.html (real, live) shows online status/
        // location, resident-since, account type, partner, and about-me for
        // any resident, cross-linked from friends lists, classifieds,
        // region profiles, etc. Reuses services already wired into this
        // connector for other features - IUserProfilesService (from the
        // classifieds work), IUserAccountService, IGridUserService - no new
        // service plugins needed. Public (no login required), matching how
        // classifieds/picks are already publicly searchable in stock
        // OpenSim/SL - profiles aren't private data on this grid.
        // Same bitmask label sets OpenSim-Grid-Interface's profile.php uses
        // for profileSkillsMask/profileWantToMask - kept identical so
        // residents coming from that reference see consistent labels.
        private static readonly string[] ProfileSkillLabels =
        {
            "Building", "Texturing", "Scripting", "Clothing", "Photography", "Modeling"
        };
        private static readonly string[] ProfileWantToLabels =
        {
            "Build", "Explore", "Meet friends", "Hang out"
        };

        private static void AppendMaskPills(StringBuilder sb, int mask, string[] labels)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                if ((mask & (1 << i)) != 0)
                    sb.Append("<span class=\"pill pill-yes\">").Append(Html(labels[i])).Append("</span> ");
            }
        }

        private void HandleProfile(IOSHttpRequest request, IOSHttpResponse response)
        {
            // Two lookup paths: "id" (uuid, used by this webUI's own links) and
            // "name" (First.Last, the shape Firestorm's OpenSim web-profile
            // link builds from web_profile_url - see llavataractions.cpp's
            // getProfileURL()/OPENSIM branch, which appends "?name=[AGENT_NAME]").
            UserAccount account = null;

            string idParam = request.QueryString.Get("id");
            if (!string.IsNullOrEmpty(idParam) && UUID.TryParse(idParam, out UUID parsedId))
            {
                account = m_UserAccountService?.GetUserAccount(UUID.Zero, parsedId);
            }
            else
            {
                string nameParam = request.QueryString.Get("name");
                if (!string.IsNullOrEmpty(nameParam))
                {
                    string[] parts = nameParam.Split('.');
                    if (parts.Length == 2)
                        account = m_UserAccountService?.GetUserAccount(UUID.Zero, parts[0], parts[1]);
                }
            }

            if (account == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, PageTitle("Profile"), "<h1>Profile not found</h1><p>No resident matches that profile link.</p>");
                return;
            }

            UUID userId = account.PrincipalID;

            // Self vs. visitor view - About/Picks/Groups/Skills are all
            // edited entirely in-world via the viewer's own Profile floater
            // (no web edit form for any of them, deliberately - see the
            // Search page's Picks section for the same boundary). A visitor
            // seeing an empty section is normal and should stay silent; a
            // resident looking at their OWN profile and seeing nothing was
            // the real complaint here - they need to know it's genuinely
            // empty (not broken) and how to fill it in, not just see three
            // sparse lines with no explanation.
            WebSession session = GetSession(request);
            bool isSelf = session != null && session.PrincipalID == userId;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>").Append(Html(account.Name)).Append("</h1>");
            if (isSelf)
            {
                sb.Append("<p class=\"news-meta\">This is what other residents see when they view your profile. ")
                  .Append("About Me, Picks, Skills and Groups shown on your web profile are all set from your viewer's own Profile panel - there's no separate web form for them.</p>");
            }

            DateTime memberSince = Utils.UnixTimeToDateTime((uint)account.Created);
            sb.Append("<p class=\"news-meta\">Resident since ").Append(Html(memberSince.ToString("MMMM d, yyyy"))).Append("</p>");

            if (m_GridUserService != null)
            {
                GridUserInfo info = m_GridUserService.GetGridUserInfo(userId.ToString());
                if (info != null)
                {
                    // "Online Location" - real gap vs. WhiteCore-Dev's own
                    // webprofile/modal_profile.html ({OnlineLocationText}/
                    // {OnlineLocation}), which names the region a resident
                    // is currently in rather than just showing "Online now".
                    // LastRegionID is kept current by presence reporting
                    // while a session is active, so it's a safe read here.
                    string status;
                    if (info.Online)
                    {
                        GridRegion currentRegion = m_GridService?.GetRegionByUUID(UUID.Zero, info.LastRegionID);
                        status = currentRegion != null
                                ? "Online now @ " + Html(currentRegion.RegionName)
                                : "Online now";
                    }
                    else
                    {
                        status = info.Logout > DateTime.MinValue.AddYears(1)
                                ? "Last seen " + Html(info.Logout.ToString("yyyy-MM-dd"))
                                : "Never logged in";
                    }
                    sb.Append("<p class=\"news-meta\">").Append(status).Append("</p>");
                }
            }

            if (m_UserProfilesService != null)
            {
                UserProfileProperties props = new UserProfileProperties { UserId = userId };
                string propsResult = string.Empty;
                m_UserProfilesService.AvatarPropertiesRequest(ref props, ref propsResult);

                if (m_FriendsService != null)
                {
                    int friendCount = m_FriendsService.GetFriends(userId)?.Length ?? 0;
                    sb.Append("<p class=\"news-meta\"><i class=\"bi bi-people\"></i> ")
                      .Append(friendCount).Append(friendCount == 1 ? " friend" : " friends");
                    if (isSelf)
                        sb.Append(" &middot; <a href=\"").Append(BasePath).Append("/friends\">Manage friends</a>");
                    sb.Append("</p>");
                }

                if (props.PartnerId != UUID.Zero)
                {
                    UserAccount partner = m_UserAccountService?.GetUserAccount(UUID.Zero, props.PartnerId);
                    if (partner != null)
                    {
                        sb.Append("<p><i class=\"bi bi-heart-fill\"></i> <strong>Partner:</strong> <a href=\"").Append(BasePath).Append("/profile?id=")
                          .Append(partner.PrincipalID).Append("\">").Append(Html(partner.Name)).Append("</a></p>");
                    }
                }

                if (!string.IsNullOrEmpty(props.AboutText))
                {
                    sb.Append("<h2><i class=\"bi bi-info-circle\"></i> About</h2><p>").Append(Html(props.AboutText).Replace("\n", "<br/>")).Append("</p>");
                }
                else if (isSelf)
                {
                    sb.Append("<h2><i class=\"bi bi-info-circle\"></i> About</h2>")
                      .Append("<p class=\"news-meta\">You haven't written an About Me yet. In your viewer: Me &rarr; Profile &rarr; Edit Profile.</p>");
                }

                if (!string.IsNullOrEmpty(props.FirstLifeText))
                {
                    sb.Append("<h2><i class=\"bi bi-person-lines-fill\"></i> First Life</h2><p>").Append(Html(props.FirstLifeText).Replace("\n", "<br/>")).Append("</p>");
                }

                if (!string.IsNullOrEmpty(props.WebUrl))
                {
                    sb.Append("<p><i class=\"bi bi-link-45deg\"></i> <a href=\"").Append(Html(props.WebUrl)).Append("\" rel=\"noopener\">")
                      .Append(Html(props.WebUrl)).Append("</a></p>");
                }

                if (!string.IsNullOrEmpty(props.Language))
                {
                    sb.Append("<p><i class=\"bi bi-translate\"></i> <strong>Languages:</strong> ").Append(Html(props.Language)).Append("</p>");
                }

                // Same skills/want-to bit mapping OpenSim-Grid-Interface's
                // profile.php uses (profileSkillsMask/profileWantToMask) -
                // these are free-text-labelled bitmasks with no single
                // canonical meaning across viewers, so matching the named
                // reference keeps the labels consistent with what residents
                // coming from that site already expect.
                if (props.SkillsMask != 0 || !string.IsNullOrEmpty(props.SkillsText))
                {
                    sb.Append("<h3>Skills &amp; Interests</h3>");
                    if (!string.IsNullOrEmpty(props.SkillsText))
                        sb.Append("<p>").Append(Html(props.SkillsText)).Append("</p>");
                    if (props.SkillsMask != 0)
                    {
                        sb.Append("<p>");
                        AppendMaskPills(sb, props.SkillsMask, ProfileSkillLabels);
                        sb.Append("</p>");
                    }
                }

                if (props.WantToMask != 0 || !string.IsNullOrEmpty(props.WantToText))
                {
                    sb.Append("<h3>Wants To</h3>");
                    if (!string.IsNullOrEmpty(props.WantToText))
                        sb.Append("<p>").Append(Html(props.WantToText)).Append("</p>");
                    if (props.WantToMask != 0)
                    {
                        sb.Append("<p>");
                        AppendMaskPills(sb, props.WantToMask, ProfileWantToLabels);
                        sb.Append("</p>");
                    }
                }

                OSD picksOsd = m_UserProfilesService.AvatarPicksRequest(userId);
                if (picksOsd is OSDArray picksArray && picksArray.Count > 0)
                {
                    sb.Append("<h2><i class=\"bi bi-geo-alt\"></i> Picks</h2><div class=\"widget-grid\">");
                    foreach (OSD entry in picksArray)
                    {
                        if (entry is OSDMap pickMap && UUID.TryParse(pickMap["pickuuid"].AsString(), out UUID pickId))
                        {
                            UserProfilePick pick = new UserProfilePick { CreatorId = userId, PickId = pickId };
                            string pickResult = string.Empty;
                            m_UserProfilesService.PickInfoRequest(ref pick, ref pickResult);

                            sb.Append("<div class=\"widget-card\"><h3>");
                            if (pick.TopPick)
                                sb.Append("<span class=\"pill pill-yes\"><i class=\"bi bi-star-fill\"></i> Top Pick</span> ");
                            sb.Append(Html(pickMap["name"].AsString())).Append("</h3>");
                            if (!string.IsNullOrEmpty(pick.Desc) && pick.Desc != "No description given.")
                                sb.Append("<div class=\"widget-meta\">").Append(Html(pick.Desc)).Append("</div>");
                            if (!string.IsNullOrEmpty(pick.SimName))
                                sb.Append("<div class=\"widget-meta\"><i class=\"bi bi-geo-alt\"></i> ").Append(Html(pick.SimName)).Append("</div>");
                            sb.Append("</div>");
                        }
                    }
                    sb.Append("</div>");
                }
                else if (isSelf)
                {
                    sb.Append("<h2><i class=\"bi bi-geo-alt\"></i> Picks</h2>")
                      .Append("<p class=\"news-meta\">You haven't added any Picks yet. In your viewer: Me &rarr; Profile &rarr; Picks &rarr; the + button, at a place you're standing.</p>");
                }

            }

            // Regions this resident owns - real gap vs. WhiteCore-Dev's own
            // webprofile/modal_regions.html, which shows this on anyone's
            // profile (not just your own dashboard). Same GetRegionsOwnedBy
            // helper HandleMyRegions already uses, just against the
            // profile's subject instead of always the logged-in session.
            List<GridRegion> profileRegions = GetRegionsOwnedBy(userId);
            if (profileRegions.Count > 0)
            {
                sb.Append("<h2><i class=\"bi bi-hdd-rack\"></i> Regions</h2><table><tr><th>Region</th><th>Size</th></tr>");
                foreach (GridRegion region in profileRegions)
                {
                    sb.Append("<tr><td>").Append(Html(region.RegionName)).Append("</td>")
                      .Append("<td>").Append(region.RegionSizeX).Append("x").Append(region.RegionSizeY).Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            // Group memberships - real WhiteCore-Dev gap (its user profile
            // page shows these, ours didn't). ListInProfile is a per-
            // membership "show this on my PUBLIC profile" flag the viewer's
            // own profile floater already exposes - it must only ever gate
            // what OTHER people see when looking at this resident's profile.
            // A resident viewing their OWN profile always sees every group
            // they're in, ListInProfile or not - filtering it there too was
            // a real bug (found via OGI's own account/groups.php parity
            // audit): a resident who hadn't flagged a group "show in
            // profile" couldn't see that membership listed anywhere on
            // their own dashboard/profile at all, not even to themselves.
            if (m_GroupsSearchService != null)
            {
                List<GroupMembershipData> memberships = m_GroupsSearchService.GetAgentGroupMemberships(userId.ToString(), userId.ToString());
                List<GroupMembershipData> shown = isSelf ? memberships : memberships.FindAll(m => m.ListInProfile);
                if (shown.Count > 0)
                {
                    sb.Append("<h2><i class=\"bi bi-people\"></i> Groups (").Append(shown.Count).Append(")</h2>");
                    if (isSelf)
                    {
                        sb.Append("<table><tr><th>Group</th><th>Title</th><th>On Public Profile</th><th>Notices</th></tr>");
                        foreach (GroupMembershipData membership in shown)
                        {
                            sb.Append("<tr><td>").Append(Html(membership.GroupName)).Append("</td>")
                              .Append("<td>").Append(Html(membership.GroupTitle)).Append("</td>")
                              .Append("<td><span class=\"pill ").Append(membership.ListInProfile ? "pill-yes\">Shown" : "pill-no\">Hidden").Append("</span></td>")
                              .Append("<td>").Append(membership.AcceptNotices ? "Yes" : "No").Append("</td></tr>");
                        }
                        sb.Append("</table>");
                    }
                    else
                    {
                        sb.Append("<ul>");
                        foreach (GroupMembershipData membership in shown)
                            sb.Append("<li>").Append(Html(membership.GroupName)).Append("</li>");
                        sb.Append("</ul>");
                    }
                }
                else if (isSelf)
                {
                    sb.Append("<h2><i class=\"bi bi-people\"></i> Groups</h2>")
                      .Append("<p class=\"news-meta\">You haven't joined any groups yet. Groups are managed entirely in-world - search for one from your viewer, or find one grid-wide from this site's Search page.</p>");
                }
            }

            WritePage(request, response, PageTitle("") + account.Name, sb.ToString());
        }

        // Offline Messages - real counterpart to OpenSim-Grid-Interface's
        // account/offline_messages.php, backed by IOfflineIMService.
        // Deliberately reads via PeekMessages, NOT GetMessages - GetMessages
        // deletes every message it returns as a side effect (stock "deliver
        // once" semantics also relied on by in-world login delivery), so
        // the old version of this page was silently wiping a resident's
        // offline messages the instant they loaded the page to read them,
        // even before "Clear All" ever entered the picture. PeekMessages/
        // DeleteMessage are non-destructive/single-row respectively, added
        // specifically so this page can be a real, re-visitable inbox
        // instead of a one-shot reveal - a message left here stays pending
        // and still gets delivered normally next time the resident actually
        // logs in-world, unless explicitly deleted here first.
        private void HandleOfflineMessages(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string flash = string.Empty;
            if (request.HttpMethod == "POST" && m_OfflineIMService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                string action = FormValue(form, "action");
                if (action == "delete" && int.TryParse(FormValue(form, "id"), out int deleteId))
                {
                    flash = m_OfflineIMService.DeleteMessage(session.PrincipalID, deleteId)
                            ? "<p class=\"success\">Message deleted.</p>"
                            : "<p class=\"error\">Could not delete that message.</p>";
                }
                else
                {
                    m_OfflineIMService.DeleteMessages(session.PrincipalID);
                    flash = "<p class=\"success\">All offline messages cleared.</p>";
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-envelope-open\"></i> Offline Messages</h1>");
            sb.Append("<p>Instant messages sent to you while you were offline, waiting to be delivered next time you log in.</p>");
            sb.Append(flash);

            if (m_OfflineIMService == null)
            {
                sb.Append("<p class=\"error\">Offline messaging is not available on this grid.</p>");
                WritePage(request, response, PageTitle("Offline Messages"), sb.ToString());
                return;
            }

            List<OfflineIMEntry> entries = m_OfflineIMService.PeekMessages(session.PrincipalID);
            if (entries == null || entries.Count == 0)
            {
                sb.Append("<p>No offline messages are currently stored for your account.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>From</th><th>Message</th><th>Received</th><th></th></tr>");
                foreach (OfflineIMEntry entry in entries.OrderBy(e => e.Message.timestamp))
                {
                    GridInstantMessage im = entry.Message;
                    DateTime received = OpenMetaverse.Utils.UnixTimeToDateTime(im.timestamp);
                    sb.Append("<tr><td>").Append(Html(im.fromAgentName)).Append("</td>")
                      .Append("<td>").Append(Html(im.message)).Append("</td>")
                      .Append("<td>").Append(Html(received.ToString("yyyy-MM-dd HH:mm"))).Append(" UTC</td>")
                      .Append("<td><form method=\"post\" style=\"margin:0\">")
                      .Append("<input type=\"hidden\" name=\"action\" value=\"delete\">")
                      .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(entry.ID).Append("\">")
                      .Append("<button type=\"submit\"><i class=\"bi bi-trash\"></i></button></form></td></tr>");
                }
                sb.Append("</table>");
                sb.Append("<form method=\"post\"><input type=\"hidden\" name=\"action\" value=\"clear\">")
                  .Append("<button type=\"submit\" onclick=\"return confirm('Clear all offline messages?');\">Clear All</button></form>");
            }

            WritePage(request, response, PageTitle("Offline Messages"), sb.ToString());
        }

        // Resident-to-resident web mail (inbox/sent/compose) - real
        // counterpart to OpenSim-Grid-Interface's message.php, but backed
        // by the new IMessagingService/webmessages table rather than raw
        // SQL + a hand-created ws_messages table the connector's own PHP
        // reference builds itself. Recipient search reuses
        // IUserAccountService.GetUserAccounts(scope, query) - the same
        // safe, parameterized name-search call already used by grid-wide
        // People search and the admin Users page - rather than hand-built
        // SQL against user input.
        private string MessagesTabs(string active)
        {
            return "<div class=\"subnav\">"
                    + "<a href=\"" + BasePath + "/messages\"" + (active == "inbox" ? " class=\"active\"" : "") + "><i class=\"bi bi-inbox\"></i> Inbox</a>"
                    + "<a href=\"" + BasePath + "/messages/sent\"" + (active == "sent" ? " class=\"active\"" : "") + "><i class=\"bi bi-send\"></i> Sent</a>"
                    + "<a href=\"" + BasePath + "/messages/compose\"" + (active == "compose" ? " class=\"active\"" : "") + "><i class=\"bi bi-pencil-square\"></i> Compose</a>"
                    + "</div>";
        }

        private void HandleMessagesInbox(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-envelope\"></i> Messages</h1>");
            sb.Append(MessagesTabs("inbox"));

            if (m_MessagingService == null)
            {
                sb.Append("<p class=\"error\">Messaging is not available on this grid.</p>");
                WritePage(request, response, PageTitle("Inbox"), sb.ToString());
                return;
            }

            List<WebMessage> messages = m_MessagingService.GetInbox(session.PrincipalID, 200);
            if (messages == null || messages.Count == 0)
            {
                sb.Append("<p>No messages yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>From</th><th>Subject</th><th>Date</th><th></th></tr>");
                foreach (WebMessage m in messages)
                {
                    UserAccount fromAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, m.SenderID);
                    string fromName = fromAccount != null ? fromAccount.Name : m.SenderID.ToString();
                    string subject = string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject;

                    sb.Append("<tr").Append(m.IsRead ? "" : " style=\"font-weight:700\"").Append("><td>").Append(Html(fromName)).Append("</td>")
                      .Append("<td><a href=\"").Append(BasePath).Append("/messages/view?id=").Append(m.ID).Append("&from=inbox\">")
                      .Append(Html(subject)).Append("</a></td>")
                      .Append("<td>").Append(Html(m.Created.ToString("yyyy-MM-dd HH:mm"))).Append("</td>")
                      .Append("<td><a href=\"").Append(BasePath).Append("/messages/delete?id=").Append(m.ID).Append("&from=inbox\" onclick=\"return confirm('Delete this message?');\"><i class=\"bi bi-trash\"></i></a></td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Inbox"), sb.ToString());
        }

        private void HandleMessagesSent(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-envelope\"></i> Messages</h1>");
            sb.Append(MessagesTabs("sent"));

            if (m_MessagingService == null)
            {
                sb.Append("<p class=\"error\">Messaging is not available on this grid.</p>");
                WritePage(request, response, PageTitle("Sent"), sb.ToString());
                return;
            }

            List<WebMessage> messages = m_MessagingService.GetSent(session.PrincipalID, 200);
            if (messages == null || messages.Count == 0)
            {
                sb.Append("<p>No sent messages yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>To</th><th>Subject</th><th>Date</th><th></th></tr>");
                foreach (WebMessage m in messages)
                {
                    UserAccount toAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, m.ReceiverID);
                    string toName = toAccount != null ? toAccount.Name : m.ReceiverID.ToString();
                    string subject = string.IsNullOrEmpty(m.Subject) ? "(no subject)" : m.Subject;

                    sb.Append("<tr><td>").Append(Html(toName)).Append("</td>")
                      .Append("<td><a href=\"").Append(BasePath).Append("/messages/view?id=").Append(m.ID).Append("&from=sent\">")
                      .Append(Html(subject)).Append("</a></td>")
                      .Append("<td>").Append(Html(m.Created.ToString("yyyy-MM-dd HH:mm"))).Append("</td>")
                      .Append("<td><a href=\"").Append(BasePath).Append("/messages/delete?id=").Append(m.ID).Append("&from=sent\" onclick=\"return confirm('Delete this message?');\"><i class=\"bi bi-trash\"></i></a></td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Sent"), sb.ToString());
        }

        private void HandleMessagesCompose(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string toParam = request.QueryString.Get("to") ?? string.Empty;
            UUID toId = UUID.Zero;
            UUID.TryParse(toParam, out toId);
            string search = request.QueryString.Get("q") ?? string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-envelope\"></i> Messages</h1>");
            sb.Append(MessagesTabs("compose"));

            if (m_MessagingService == null || m_UserAccountService == null)
            {
                sb.Append("<p class=\"error\">Messaging is not available on this grid.</p>");
                WritePage(request, response, PageTitle("Compose"), sb.ToString());
                return;
            }

            sb.Append("<h2>Compose Message</h2>");
            sb.Append("<form method=\"get\" action=\"").Append(BasePath).Append("/messages/compose\">")
              .Append("<label>Find a resident<br/><input type=\"text\" name=\"q\" value=\"").Append(Html(search)).Append("\" placeholder=\"Type a name to search...\"></label> ")
              .Append("<button type=\"submit\"><i class=\"bi bi-search\"></i> Search</button></form>");

            if (!string.IsNullOrEmpty(search))
            {
                List<UserAccount> candidates = m_UserAccountService.GetUserAccounts(UUID.Zero, search);
                if (candidates == null || candidates.Count == 0)
                {
                    sb.Append("<p>No residents found.</p>");
                }
                else
                {
                    sb.Append("<ul>");
                    foreach (UserAccount c in candidates)
                    {
                        sb.Append("<li><a href=\"").Append(BasePath).Append("/messages/compose?to=").Append(c.PrincipalID)
                          .Append("\">").Append(Html(c.Name)).Append("</a></li>");
                    }
                    sb.Append("</ul>");
                }
            }

            string toName = string.Empty;
            if (toId != UUID.Zero)
            {
                UserAccount toAccount = m_UserAccountService.GetUserAccount(UUID.Zero, toId);
                toName = toAccount != null ? toAccount.Name : string.Empty;
            }

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/messages/send\">");
            sb.Append("<input type=\"hidden\" name=\"to_uuid\" value=\"").Append(toId).Append("\">");
            sb.Append("<label>To<br/><input type=\"text\" value=\"").Append(Html(toName != string.Empty ? toName : "Select a resident above")).Append("\" readonly></label><br/>");
            sb.Append("<label>Subject<br/><input type=\"text\" name=\"subject\" maxlength=\"150\"></label><br/>");
            sb.Append("<label>Message<br/><textarea name=\"body\" rows=\"6\"></textarea></label><br/>");
            sb.Append("<button type=\"submit\"><i class=\"bi bi-send\"></i> Send Message</button>");
            sb.Append("</form>");

            WritePage(request, response, PageTitle("Compose"), sb.ToString());
        }

        private void HandleMessagesSend(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || m_MessagingService == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string toParam = FormValue(form, "to_uuid");
            string subject = FormValue(form, "subject").Trim();
            string body = FormValue(form, "body").Trim();

            if (!UUID.TryParse(toParam, out UUID toId) || toId == UUID.Zero)
            {
                response.Redirect(BasePath + "/messages/compose?message=" + Uri.EscapeDataString("Please choose a valid recipient."), HttpStatusCode.Redirect);
                return;
            }
            if (string.IsNullOrEmpty(body))
            {
                response.Redirect(BasePath + "/messages/compose?to=" + toId + "&message=" + Uri.EscapeDataString("Message body cannot be empty."), HttpStatusCode.Redirect);
                return;
            }

            WebMessage message = new WebMessage
            {
                ID = UUID.Random(),
                SenderID = session.PrincipalID,
                ReceiverID = toId,
                Subject = subject,
                Body = body,
                Created = DateTime.UtcNow
            };
            m_MessagingService.Store(message);

            response.Redirect(BasePath + "/messages/sent", HttpStatusCode.Redirect);
        }

        private void HandleMessagesView(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string idParam = request.QueryString.Get("id");
            string fromTab = request.QueryString.Get("from") == "sent" ? "sent" : "inbox";

            if (m_MessagingService == null || !UUID.TryParse(idParam, out UUID id))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, PageTitle("Message"), "<h1>Message not found</h1>");
                return;
            }

            WebMessage message = m_MessagingService.Get(id);
            if (message == null || (message.SenderID != session.PrincipalID && message.ReceiverID != session.PrincipalID))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, PageTitle("Message"), "<h1>Message not found</h1>");
                return;
            }

            if (message.ReceiverID == session.PrincipalID && !message.IsRead)
                m_MessagingService.MarkRead(id);

            UserAccount fromAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, message.SenderID);
            UserAccount toAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, message.ReceiverID);
            string fromName = fromAccount != null ? fromAccount.Name : message.SenderID.ToString();
            string toName = toAccount != null ? toAccount.Name : message.ReceiverID.ToString();

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>").Append(Html(string.IsNullOrEmpty(message.Subject) ? "(no subject)" : message.Subject)).Append("</h1>");
            sb.Append("<p class=\"news-meta\">From: ").Append(Html(fromName)).Append(" &middot; To: ").Append(Html(toName))
              .Append(" &middot; ").Append(Html(message.Created.ToString("yyyy-MM-dd HH:mm"))).Append("</p>");
            sb.Append("<div class=\"content-card\" style=\"white-space:pre-wrap;\">").Append(Html(message.Body)).Append("</div>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/messages/compose?to=").Append(message.SenderID).Append("\"><i class=\"bi bi-reply\"></i> Reply</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/messages/delete?id=").Append(message.ID).Append("&from=").Append(fromTab)
              .Append("\" onclick=\"return confirm('Delete this message?');\"><i class=\"bi bi-trash\"></i> Delete</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/messages").Append(fromTab == "sent" ? "/sent" : string.Empty).Append("\">Back</a></p>");

            WritePage(request, response, PageTitle("Message"), sb.ToString());
        }

        private void HandleMessagesDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string idParam = request.QueryString.Get("id");
            string fromTab = request.QueryString.Get("from") == "sent" ? "sent" : "inbox";

            if (m_MessagingService != null && UUID.TryParse(idParam, out UUID id))
                m_MessagingService.DeleteForUser(id, session.PrincipalID);

            response.Redirect(BasePath + "/messages" + (fromTab == "sent" ? "/sent" : string.Empty), HttpStatusCode.Redirect);
        }

        // Public Economy dashboard - real counterpart to OpenSim-Grid-
        // Interface's economy.php, but scoped down deliberately: that page
        // has 6 different ?action= views (dashboard/my_account/
        // send_money/my_transactions/leaderboard/statistics/recent) with a
        // lot of overlap - "my_account"/"my_transactions" duplicate this
        // connector's own existing My Transactions self-service page
        // (task #40), and "send_money" is a UI shell there with no working
        // backend at all (its own comment says so: "needs the backend API
        // implementation"). This page keeps just the genuinely new part -
        // grid-wide circulation/leaderboard - as one page, reusing
        // RenderEconomyStats (already built for the splash widget) instead
        // of a second volume-window implementation. GetTotalCirculation/
        // CountAccountsWithBalance/GetTopBalances are real DB-side
        // SUM/COUNT/ORDER-BY-LIMIT aggregates (new ICurrencyData methods),
        // not a loop calling GetBalance per account.
        private void HandleEconomy(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-wallet2\"></i> Economy</h1>");
            sb.Append("<p>Monitor currency circulation and see where you stand.</p>");

            if (m_CurrencyService == null)
            {
                sb.Append("<p class=\"error\">Currency service is not available on this grid.</p>");
                WritePage(request, response, PageTitle("Economy"), sb.ToString());
                return;
            }

            // "What am I getting and why" - same Economy & Currency cards
            // the Features page already builds (AppendIconFeatureCard,
            // reused as-is rather than a second hand-written copy), placed
            // here first since a visitor landing directly on /economy
            // (rather than arriving via Features) has had no explanation of
            // the currency system yet before hitting a wall of live numbers.
            sb.Append("<h2><i class=\"bi bi-currency-exchange\"></i> What You're Getting</h2><div class=\"feature-grid-3\">");
            AppendIconFeatureCard(sb, "currency-dollar", "Native Currency" + (m_CurrencyService != null ? " <span class=\"pill pill-yes\">Active</span>" : " <span class=\"pill pill-no\">Unavailable</span>"), new[]
            {
                ("Ledger", false, "Built-in transaction history and group treasuries - not a third-party dependency"),
                ("Web access", false, "Balance and transaction pages from any browser, no separate money-server process"),
                ("Protocol", false, "Answers the same buy/sell currency.php surface real viewers already expect")
            });
            AppendIconFeatureCard(sb, "wallet2", "Gloebit <span class=\"pill\" style=\"background:rgba(59,130,246,.15);color:var(--accent-bright)\">Optional</span>", new[]
            {
                ("What it is", false, "A real-money payment gateway, for grids that want a paid economy instead of (or alongside) the native ledger"),
                ("How it's added", false, "Swappable via the addon-modules Gloebit integration - not required, not enabled by default")
            });
            sb.Append("</div>");

            if (session != null)
            {
                int balance = m_CurrencyService.GetBalance(session.PrincipalID);
                sb.Append("<div class=\"content-card\"><h2>My Balance</h2><p style=\"font-size:28px;font-weight:800;color:var(--accent-bright)\">").Append(m_currencySymbol).Append(" ")
                  .Append(balance.ToString("N0")).Append("</p>")
                  .Append("<p><a href=\"").Append(BasePath).Append("/transactions\">View my transactions &rarr;</a></p></div>");
            }
            else
            {
                sb.Append("<div class=\"content-card\"><p><a href=\"").Append(BasePath).Append("/login\">Log in</a> to see your balance and transaction history.</p></div>");
            }

            sb.Append(RenderEconomyStats());

            sb.Append("<h2><i class=\"bi bi-globe\"></i> Grid Totals</h2><div class=\"stats-grid\">");
            AppendStat(sb, "Money in Circulation", m_currencySymbol + " " + m_CurrencyService.GetTotalCirculation().ToString("N0"), "sum of every resident's balance");
            AppendStat(sb, "Funded Accounts", m_CurrencyService.CountAccountsWithBalance().ToString("N0"), "residents with a non-zero balance");
            AppendStat(sb, "Total Transactions", m_CurrencyService.NumberOfTransactions(UUID.Zero, UUID.Zero).ToString("N0"), "all time");
            sb.Append("</div>");

            List<CurrencyBalanceEntry> topBalances = m_CurrencyService.GetTopBalances(10);
            sb.Append("<h2><i class=\"bi bi-trophy\"></i> Top Balances</h2>");
            if (topBalances == null || topBalances.Count == 0)
            {
                sb.Append("<p>No funded accounts yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>#</th><th>Resident</th><th>Balance</th></tr>");
                int rank = 1;
                foreach (CurrencyBalanceEntry entry in topBalances)
                {
                    UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, entry.PrincipalID);
                    string name = account != null ? account.Name : entry.PrincipalID.ToString();
                    string nameCell = account != null
                            ? "<a href=\"" + BasePath + "/profile?id=" + entry.PrincipalID + "\">" + Html(name) + "</a>"
                            : Html(name);

                    sb.Append("<tr><td>#").Append(rank).Append("</td><td>").Append(nameCell).Append("</td>")
                      .Append("<td>").Append(m_currencySymbol).Append(" ").Append(entry.Balance.ToString("N0")).Append("</td></tr>");
                    rank++;
                }
                sb.Append("</table>");
            }

            List<CurrencyTransfer> recent = m_CurrencyService.GetTransactionHistory(UUID.Zero, UUID.Zero, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, 0, 20);
            sb.Append("<h2><i class=\"bi bi-clock-history\"></i> Recent Transactions</h2>");
            if (recent == null || recent.Count == 0)
            {
                sb.Append("<p>No transactions in the last 30 days.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Date</th><th>From</th><th>To</th><th>Amount</th></tr>");
                foreach (CurrencyTransfer t in recent)
                {
                    UserAccount fromAccount = t.FromAgent != UUID.Zero ? m_UserAccountService?.GetUserAccount(UUID.Zero, t.FromAgent) : null;
                    UserAccount toAccount = t.ToAgent != UUID.Zero ? m_UserAccountService?.GetUserAccount(UUID.Zero, t.ToAgent) : null;
                    string fromName = fromAccount != null ? fromAccount.Name : "System";
                    string toName = toAccount != null ? toAccount.Name : "System";

                    sb.Append("<tr><td>").Append(Html(t.TransferDate.ToString("yyyy-MM-dd HH:mm"))).Append("</td>")
                      .Append("<td>").Append(Html(fromName)).Append("</td>")
                      .Append("<td>").Append(Html(toName)).Append("</td>")
                      .Append("<td>").Append(m_currencySymbol).Append(" ").Append(t.Amount.ToString("N0")).Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Economy"), sb.ToString());
        }

        // Friends list - the second "genuinely new ground" item from the
        // full WhiteCore-Dev audit (WhiteCore's user/friends.html: name
        // linking to a profile, region linking to a region profile, and a
        // hop:// location link). IFriendsService already exists server-side
        // (FriendsService.dll, used by the actual in-viewer friends list)
        // but had never been wired into this connector before - this is the
        // first WebInterface feature to need it. The `Friend` field on each
        // FriendInfo is the OTHER party's principal UUID as a string for a
        // LOCAL friend (the stock OpenSim Friends table schema, confirmed
        // via FriendsStore.migrations) - but for a HYPERGRID friend it's a
        // "UUID;homeURI;First Last;secret" universal identifier instead
        // (confirmed via UserAgentService.GetOnlineFriends's own parsing),
        // which the old plain UUID.TryParse silently failed on and skipped
        // - meaning every HG friend was invisible on this page even though
        // this grid is Hypergrid-enabled. Fixed by falling back to
        // Util.ParseUniversalUserIdentifier and splitting the two into
        // separate tables (OpenSim-Grid-Interface's own account/friends.php
        // does the same split, for the same reason - a HG friend has no
        // local UserAccount to resolve a name/profile link from).
        private void HandleFriends(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>My Friends</h1><p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a></p>");

            if (m_FriendsService == null)
            {
                sb.Append("<p>Friends service is not available.</p>");
                WritePage(request, response, PageTitle("Friends"), sb.ToString());
                return;
            }

            OpenSim.Services.Interfaces.FriendInfo[] friends = m_FriendsService.GetFriends(session.PrincipalID);
            if (friends == null || friends.Length == 0)
            {
                sb.Append("<p>You haven't added any friends yet. Use the Friends panel in your viewer to send a friend request.</p>");
                WritePage(request, response, PageTitle("Friends"), sb.ToString());
                return;
            }

            StringBuilder localRows = new StringBuilder();
            StringBuilder hgRows = new StringBuilder();

            foreach (OpenSim.Services.Interfaces.FriendInfo friend in friends)
            {
                string rightsCell = FriendRightsCell(friend.MyFlags);

                if (UUID.TryParse(friend.Friend, out UUID friendId))
                {
                    UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, friendId);
                    string name = account != null ? account.Name : friend.Friend;

                    string status = "Offline";
                    // Reference's Region + Online Location columns (teleport-
                    // linked) - real gap this table was missing, same
                    // LastRegionID-while-online pattern HandleProfile's own
                    // "Online Location" section already uses.
                    string locationCell = string.Empty;
                    if (m_GridUserService != null)
                    {
                        GridUserInfo info = m_GridUserService.GetGridUserInfo(friendId.ToString());
                        if (info != null && info.Online)
                        {
                            status = "Online now";
                            GridRegion currentRegion = m_GridService?.GetRegionByUUID(UUID.Zero, info.LastRegionID);
                            if (currentRegion != null)
                            {
                                string hopUrl = "secondlife:///app/teleport/" + Uri.EscapeDataString(currentRegion.RegionName) + "/128/128/25";
                                locationCell = "<a href=\"" + Html(hopUrl) + "\">" + Html(currentRegion.RegionName) + "</a>";
                            }
                        }
                    }

                    localRows.Append("<tr><td><a href=\"").Append(BasePath).Append("/profile?id=").Append(friendId).Append("\">")
                      .Append(Html(name)).Append("</a></td><td>").Append(Html(status)).Append("</td><td>").Append(locationCell)
                      .Append("</td><td>").Append(rightsCell).Append("</td></tr>");
                }
                else if (Util.ParseUniversalUserIdentifier(friend.Friend, out UUID hgFriendId, out string homeUrl, out string firstName, out string lastName))
                {
                    string name = (firstName + " " + lastName).Trim();
                    if (string.IsNullOrEmpty(name))
                        name = hgFriendId.ToString();

                    hgRows.Append("<tr><td>").Append(Html(name)).Append("</td><td>")
                      .Append(Html(homeUrl)).Append("</td><td>").Append(rightsCell).Append("</td></tr>");
                }
                // Neither parse succeeded - a malformed/legacy row this
                // page genuinely can't do anything useful with; skipped
                // rather than shown as raw garbage.
            }

            if (localRows.Length > 0)
            {
                sb.Append("<h2><i class=\"bi bi-people\"></i> This Grid</h2>")
                  .Append("<table><tr><th>Name</th><th>Status</th><th>Location</th><th>Rights You've Granted</th></tr>")
                  .Append(localRows).Append("</table>");
            }
            if (hgRows.Length > 0)
            {
                sb.Append("<h2><i class=\"bi bi-globe\"></i> Hypergrid</h2>")
                  .Append("<p class=\"news-meta\">Friends visiting from another OpenSim grid - profile links and online status aren't available for these.</p>")
                  .Append("<table><tr><th>Name</th><th>Home Grid</th><th>Rights You've Granted</th></tr>")
                  .Append(hgRows).Append("</table>");
            }

            WritePage(request, response, PageTitle("Friends"), sb.ToString());
        }

        // "Rights You've Granted" reads MyFlags - what THIS resident has
        // given the friend permission to do, the same direction OGI's own
        // rights columns show. Display-only for now (no edit form yet) -
        // GrantRights isn't exposed anywhere in this connector.
        private static string FriendRightsCell(int myFlags)
        {
            List<string> rights = new List<string>();
            if ((myFlags & (int)FriendRights.CanSeeOnline) != 0)
                rights.Add("See Online");
            if ((myFlags & (int)FriendRights.CanSeeOnMap) != 0)
                rights.Add("See on Map");
            if ((myFlags & (int)FriendRights.CanModifyObjects) != 0)
                rights.Add("Modify Objects");
            return rights.Count > 0 ? Html(string.Join(", ", rights)) : "<span class=\"news-meta\">None</span>";
        }

        // Self-service account pages - "genuinely new ground" items #3/#4
        // from the WhiteCore-Dev audit (user/password.html, user/email.html).
        // WhiteCore's user/deleteaccount.html and user/partnership.html are
        // deliberately NOT ported here: IUserAccountService has no delete
        // primitive at all (confirmed by reading the full interface - only
        // Get/Store/SetDisplayName/InvalidateCache exist, no Delete), and a
        // real partner-proposal flow needs a two-way pending-request/
        // notification workflow, not a one-sided form - both need their own
        // design pass rather than a bolted-on placeholder.
        private void HandleChangePassword(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Change Password"), ChangePasswordForm(null));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string currentPassword = FormValue(form, "current_password");
            string newPassword = FormValue(form, "new_password");
            string confirmPassword = FormValue(form, "confirm_password");

            if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
            {
                WritePage(request, response, PageTitle("Change Password"), ChangePasswordForm("All fields are required."));
                return;
            }
            if (newPassword != confirmPassword)
            {
                WritePage(request, response, PageTitle("Change Password"), ChangePasswordForm("New passwords do not match."));
                return;
            }
            if (newPassword.Length < 6)
            {
                WritePage(request, response, PageTitle("Change Password"), ChangePasswordForm("New password must be at least 6 characters."));
                return;
            }

            // Same MD5-then-Authenticate convention TryLogin uses - confirms
            // the CURRENT password before allowing a change, so a stolen
            // session cookie alone can't be used to lock the real owner out.
            string authToken = m_AuthenticationService?.Authenticate(session.PrincipalID, Util.Md5Hash(currentPassword), 30);
            if (string.IsNullOrEmpty(authToken))
            {
                WritePage(request, response, PageTitle("Change Password"), ChangePasswordForm("Current password is incorrect."));
                return;
            }

            // SetPassword takes the raw new password (it hashes internally) -
            // same convention HandleRegister/HandleResetPassword already use.
            if (m_AuthenticationService == null || !m_AuthenticationService.SetPassword(session.PrincipalID, newPassword))
            {
                WritePage(request, response, PageTitle("Change Password"), ChangePasswordForm("Could not update your password. Please try again."));
                return;
            }

            WritePage(request, response, PageTitle("Change Password"),
                    "<h1>Change Password</h1><p>Your password has been updated.</p><p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>");
        }

        private static string ChangePasswordForm(string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Change Password</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/change-password\">"
                    + "<label>Current password<br/><input type=\"password\" name=\"current_password\" required></label><br/>"
                    + "<label>New password<br/><input type=\"password\" name=\"new_password\" required></label><br/>"
                    + "<label>Confirm new password<br/><input type=\"password\" name=\"confirm_password\" required></label><br/>"
                    + "<button type=\"submit\">Update password</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>";
        }

        // Self-service backup codes for resetting THIS avatar's own
        // in-world password without needing a working email - real gap
        // found auditing OpenSim-Grid-Interface's own account.php.
        // Casperia's login IS the avatar's in-world password (no separate
        // portal password to recover), so these are tied to PrincipalID,
        // the same identity /forgot-password already resets by email.
        private void HandleRecoveryCodes(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (m_RecoveryCodeService == null)
            {
                WritePage(request, response, PageTitle("Recovery Codes"),
                        "<h1>Recovery Codes</h1><p class=\"error\">Recovery codes are not available on this grid.</p>");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-shield-lock\"></i> Recovery Codes</h1>");
            sb.Append("<p>One-time backup codes that let you reset this avatar's password without needing your email - useful if your email on file is out of date. ");
            sb.Append("Each code works once. Generating new codes immediately invalidates any old ones.</p>");

            if (request.HttpMethod == "POST")
            {
                List<string> freshCodes = m_RecoveryCodeService.RegenerateCodes(session.PrincipalID);
                sb.Append("<div class=\"error\" style=\"border-left-color:var(--accent);color:var(--text);\">")
                  .Append("<strong><i class=\"bi bi-exclamation-triangle-fill\"></i> Save these now - they will not be shown again:</strong>")
                  .Append("<div style=\"font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:16px;margin-top:10px;letter-spacing:1px;\">");
                foreach (string code in freshCodes)
                    sb.Append(Html(code)).Append("<br/>");
                sb.Append("</div></div>");
            }
            else
            {
                int remaining = m_RecoveryCodeService.GetRemainingCount(session.PrincipalID);
                sb.Append("<p><strong>").Append(remaining).Append(" of 5</strong> codes remaining.</p>");
            }

            sb.Append("<form method=\"post\"><button type=\"submit\" onclick=\"return confirm('Generate new recovery codes? Any existing codes will stop working.');\">")
              .Append("Generate New Codes</button></form>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a></p>");

            WritePage(request, response, PageTitle("Recovery Codes"), sb.ToString());
        }

        // Self-service counterpart to HandleAdminUsersSoftDelete - same
        // SoftDeleteAccount mechanism, just gated on the resident proving
        // they still know their own current password (the same MD5-then-
        // Authenticate check HandleChangePassword already uses) instead of
        // an admin flag, then logs them out immediately afterward since
        // their session would otherwise still work until it expires.
        private void HandleDeleteAccount(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Delete Account"), DeleteAccountForm(null));
                return;
            }

            if (m_UserAccountService == null || m_AuthenticationService == null)
            {
                WritePage(request, response, PageTitle("Delete Account"), DeleteAccountForm("Account deletion is not available right now."));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string currentPassword = FormValue(form, "current_password");

            string authToken = m_AuthenticationService.Authenticate(session.PrincipalID, Util.Md5Hash(currentPassword), 30);
            if (string.IsNullOrEmpty(authToken))
            {
                WritePage(request, response, PageTitle("Delete Account"), DeleteAccountForm("Current password is incorrect."));
                return;
            }

            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, session.PrincipalID);
            if (account == null)
            {
                WritePage(request, response, PageTitle("Delete Account"), DeleteAccountForm("Account not found."));
                return;
            }

            string result = SoftDeleteAccount(account);
            m_log.InfoFormat("[WEB INTERFACE]: {0} ({1}) deleted their own account", account.Name, account.PrincipalID);

            string token = ReadCookie(request, SessionCookieName);
            if (!string.IsNullOrEmpty(token))
                m_sessions.TryRemove(token, out _);
            ClearSessionCookie(response);

            WritePage(request, response, PageTitle("Delete Account"),
                    "<h1>Account Deleted</h1><p>" + Html(result) + " You have been logged out. "
                    + "If this was a mistake, contact a grid administrator - the account can be recovered before its data is otherwise cleaned up.</p>"
                    + "<p><a href=\"" + BasePath + "/login\">Back to login</a></p>");
        }

        private static string DeleteAccountForm(string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Delete Account</h1>"
                    + "<p class=\"error\">This deactivates your account immediately: your password is scrambled so you can no longer log in "
                    + "(in-world or on this site), though your data is not removed. This cannot be undone by you - only a grid administrator can restore it.</p>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/delete-account\" onsubmit=\"return confirm('Delete your account? You will be logged out immediately.');\">"
                    + "<label>Current password<br/><input type=\"password\" name=\"current_password\" required></label><br/>"
                    + "<button type=\"submit\">Delete my account</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>";
        }

        private void HandleChangeEmail(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, session.PrincipalID);
            if (account == null)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Change Email"), ChangeEmailForm(account.Email, null));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string newEmail = FormValue(form, "email").Trim();
            string confirmEmail = FormValue(form, "confirm_email").Trim();

            if (string.IsNullOrEmpty(newEmail) || !newEmail.Contains("@"))
            {
                WritePage(request, response, PageTitle("Change Email"), ChangeEmailForm(newEmail, "Enter a valid email address."));
                return;
            }
            // Reference's dual email+confirmation fields - a real gap this
            // form was missing. Matters more here than most confirm-fields:
            // this address is where the forgot-password reset link goes, so
            // a silent typo can lock a resident out with no recovery path.
            if (!string.Equals(newEmail, confirmEmail, StringComparison.OrdinalIgnoreCase))
            {
                WritePage(request, response, PageTitle("Change Email"), ChangeEmailForm(newEmail, "Email addresses do not match."));
                return;
            }

            // One email, one master account. Without this check, typing in
            // an email you don't own but merely know would silently link
            // THIS already-logged-in session into whoever's master account
            // actually owns it (via AutoProvisionWebAccount's own matching
            // logic below) - real account-visibility exposure, not just a
            // policy nicety. A match that's already your own account is
            // fine (re-saving the same address, or a second linked avatar
            // sharing it) - only a match belonging to someone else's
            // account is rejected.
            if (m_WebAccountService != null)
            {
                WebAccount ownedBy = m_WebAccountService.GetByEmail(newEmail);
                if (ownedBy != null && ownedBy.ID != session.WebAccountID)
                {
                    WritePage(request, response, PageTitle("Change Email"),
                            ChangeEmailForm(newEmail, "That email is already linked to another account. If it's yours, log in there and use Import Avatar to add this avatar to it instead."));
                    return;
                }
            }

            account.Email = newEmail;
            if (!m_UserAccountService.StoreUserAccount(account))
            {
                WritePage(request, response, PageTitle("Change Email"), ChangeEmailForm(newEmail, "Could not update your email. Please try again."));
                return;
            }

            // A resident who logged in classically with no email set (so
            // TryLogin's auto-provision had nothing to work with) gets
            // linked to a portal account immediately, rather than needing
            // to log out and back in.
            EnsureWebAccountLinked(request, session, account);

            WritePage(request, response, PageTitle("Change Email"),
                    "<h1>Change Email</h1><p>Your email address has been updated.</p><p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>");
        }

        private static string ChangeEmailForm(string email, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Change Email</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/change-email\">"
                    + "<label>Email address<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\" required></label><br/>"
                    + "<label>Confirm email address<br/><input type=\"email\" name=\"confirm_email\" required></label><br/>"
                    + "<button type=\"submit\">Update email</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>";
        }

        // Partner proposal flow. UserProfileProperties.PartnerId was write-
        // once before this batch (see IProfilesData.UpdateAvatarPartner's
        // comment) - that's now fixed, so the only remaining piece is
        // tracking a pending proposal until it's accepted/declined/
        // cancelled. Rather than a new table (and 3 new migrations) for
        // something this small, it reuses the userdata table that already
        // backs UserAppData/RequestUserAppData/SetUserAppData - a plain
        // UserId+TagId key/value slot, which is exactly what "who proposed
        // to me" / "who did I propose to" need. One pending proposal per
        // direction at a time; good enough for v1, easy to widen later.
        private static readonly UUID PartnerIncomingTag = new UUID("9b1f9b1a-0000-4a00-8000-000000000001");
        private static readonly UUID PartnerOutgoingTag = new UUID("9b1f9b1a-0000-4a00-8000-000000000002");

        private UUID GetPartnerAppDataUUID(UUID userId, UUID tag)
        {
            if (m_UserProfilesService == null)
                return UUID.Zero;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = tag.ToString() };
            string result = string.Empty;
            m_UserProfilesService.RequestUserAppData(ref data, ref result);

            return UUID.TryParse(data.DataVal, out UUID value) ? value : UUID.Zero;
        }

        private void SetPartnerAppDataUUID(UUID userId, UUID tag, UUID value)
        {
            if (m_UserProfilesService == null)
                return;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = tag.ToString() };
            string result = string.Empty;
            m_UserProfilesService.RequestUserAppData(ref data, ref result); // ensures the row exists - SetUserAppData only UPDATEs
            data.DataKey = "PartnerProposal";
            data.DataVal = value.ToString();
            m_UserProfilesService.SetUserAppData(data, ref result);
        }

        private UUID GetProfilePartnerId(UUID userId)
        {
            if (m_UserProfilesService == null)
                return UUID.Zero;

            UserProfileProperties props = new UserProfileProperties { UserId = userId };
            string result = string.Empty;
            m_UserProfilesService.AvatarPropertiesRequest(ref props, ref result);
            return props.PartnerId;
        }

        private void HandlePartner(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (m_UserProfilesService == null || m_UserAccountService == null)
            {
                WritePage(request, response, PageTitle("Partner"), "<h1>Partner</h1><p>Profiles service is not available.</p>");
                return;
            }

            string message = string.Empty;
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                message = ApplyPartnerAction(session.PrincipalID, FormValue(form, "action"), FormValue(form, "name"));
            }

            UUID myPartnerId = GetProfilePartnerId(session.PrincipalID);
            UUID incomingFrom = GetPartnerAppDataUUID(session.PrincipalID, PartnerIncomingTag);
            UUID outgoingTo = GetPartnerAppDataUUID(session.PrincipalID, PartnerOutgoingTag);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Partner</h1>");
            if (!string.IsNullOrEmpty(message))
                sb.Append("<p>").Append(Html(message)).Append("</p>");

            if (myPartnerId != UUID.Zero)
            {
                UserAccount partner = m_UserAccountService.GetUserAccount(UUID.Zero, myPartnerId);
                string partnerName = partner != null ? partner.Name : myPartnerId.ToString();

                sb.Append("<p>You are partnered with <a href=\"").Append(BasePath).Append("/profile?id=").Append(myPartnerId).Append("\">")
                        .Append(Html(partnerName)).Append("</a>.</p>");
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/partner\" onsubmit=\"return confirm('End this partnership?');\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"breakup\">")
                        .Append("<button type=\"submit\">End partnership</button>")
                        .Append("</form>");
            }
            else if (incomingFrom != UUID.Zero)
            {
                UserAccount proposer = m_UserAccountService.GetUserAccount(UUID.Zero, incomingFrom);
                string proposerName = proposer != null ? proposer.Name : incomingFrom.ToString();

                sb.Append("<p>").Append(Html(proposerName)).Append(" has proposed a partnership with you.</p>");
                sb.Append("<form style=\"display:inline\" method=\"post\" action=\"").Append(BasePath).Append("/partner\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"accept\">")
                        .Append("<button type=\"submit\">Accept</button>")
                        .Append("</form> ");
                sb.Append("<form style=\"display:inline\" method=\"post\" action=\"").Append(BasePath).Append("/partner\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"decline\">")
                        .Append("<button type=\"submit\">Decline</button>")
                        .Append("</form>");
            }
            else if (outgoingTo != UUID.Zero)
            {
                UserAccount target = m_UserAccountService.GetUserAccount(UUID.Zero, outgoingTo);
                string targetName = target != null ? target.Name : outgoingTo.ToString();

                sb.Append("<p>Proposal sent to <a href=\"").Append(BasePath).Append("/profile?id=").Append(outgoingTo).Append("\">")
                        .Append(Html(targetName)).Append("</a>, awaiting a response.</p>");
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/partner\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"cancel\">")
                        .Append("<button type=\"submit\">Cancel proposal</button>")
                        .Append("</form>");
            }
            else
            {
                sb.Append("<p>You are not currently partnered.</p>");
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/partner\">")
                        .Append("<input type=\"hidden\" name=\"action\" value=\"propose\">")
                        .Append("<label>Propose to (First Last): <input type=\"text\" name=\"name\" required></label> ")
                        .Append("<button type=\"submit\">Propose</button>")
                        .Append("</form>");
            }

            sb.Append("<p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a></p>");

            WritePage(request, response, PageTitle("Partner"), sb.ToString());
        }

        private string ApplyPartnerAction(UUID myId, string action, string targetName)
        {
            switch (action)
            {
                case "propose":
                {
                    if (GetProfilePartnerId(myId) != UUID.Zero)
                        return "End your current partnership before proposing to someone else.";
                    if (GetPartnerAppDataUUID(myId, PartnerOutgoingTag) != UUID.Zero)
                        return "Cancel your existing proposal before sending a new one.";

                    string[] nameParts = (targetName ?? string.Empty).Trim().Split(' ', 2);
                    UserAccount target = nameParts.Length == 2 ? m_UserAccountService.GetUserAccount(UUID.Zero, nameParts[0], nameParts[1]) : null;
                    if (target == null)
                        return "Resident \"" + targetName + "\" not found.";
                    if (target.PrincipalID == myId)
                        return "You can't propose to yourself.";
                    if (GetProfilePartnerId(target.PrincipalID) != UUID.Zero)
                        return target.Name + " is already partnered.";
                    if (GetPartnerAppDataUUID(target.PrincipalID, PartnerIncomingTag) != UUID.Zero)
                        return target.Name + " already has a pending proposal.";

                    SetPartnerAppDataUUID(target.PrincipalID, PartnerIncomingTag, myId);
                    SetPartnerAppDataUUID(myId, PartnerOutgoingTag, target.PrincipalID);
                    return "Proposal sent to " + target.Name + ".";
                }

                case "cancel":
                {
                    UUID outgoing = GetPartnerAppDataUUID(myId, PartnerOutgoingTag);
                    if (outgoing == UUID.Zero)
                        return "You have no pending proposal to cancel.";

                    SetPartnerAppDataUUID(myId, PartnerOutgoingTag, UUID.Zero);
                    SetPartnerAppDataUUID(outgoing, PartnerIncomingTag, UUID.Zero);
                    return "Proposal cancelled.";
                }

                case "accept":
                {
                    UUID incoming = GetPartnerAppDataUUID(myId, PartnerIncomingTag);
                    if (incoming == UUID.Zero)
                        return "You have no pending proposal to accept.";
                    if (GetPartnerAppDataUUID(incoming, PartnerOutgoingTag) != myId)
                        return "That proposal is no longer valid.";
                    if (GetProfilePartnerId(myId) != UUID.Zero || GetProfilePartnerId(incoming) != UUID.Zero)
                        return "One of you is already partnered - the proposal can no longer be accepted.";

                    string updateResult = string.Empty;
                    m_UserProfilesService.UpdateAvatarPartner(myId, incoming, ref updateResult);
                    m_UserProfilesService.UpdateAvatarPartner(incoming, myId, ref updateResult);
                    SetPartnerAppDataUUID(myId, PartnerIncomingTag, UUID.Zero);
                    SetPartnerAppDataUUID(incoming, PartnerOutgoingTag, UUID.Zero);

                    UserAccount partner = m_UserAccountService.GetUserAccount(UUID.Zero, incoming);
                    return "You are now partnered with " + (partner != null ? partner.Name : incoming.ToString()) + ".";
                }

                case "decline":
                {
                    UUID incoming = GetPartnerAppDataUUID(myId, PartnerIncomingTag);
                    if (incoming == UUID.Zero)
                        return "You have no pending proposal to decline.";

                    SetPartnerAppDataUUID(myId, PartnerIncomingTag, UUID.Zero);
                    SetPartnerAppDataUUID(incoming, PartnerOutgoingTag, UUID.Zero);
                    return "Proposal declined.";
                }

                case "breakup":
                {
                    UUID partnerId = GetProfilePartnerId(myId);
                    if (partnerId == UUID.Zero)
                        return "You are not currently partnered.";

                    string updateResult = string.Empty;
                    m_UserProfilesService.UpdateAvatarPartner(myId, UUID.Zero, ref updateResult);
                    m_UserProfilesService.UpdateAvatarPartner(partnerId, UUID.Zero, ref updateResult);
                    return "Partnership ended.";
                }

                default:
                    return string.Empty;
            }
        }

        // My Transactions - self-service counterpart to HandleAdminTransactions
        // (task #20), filtered to the logged-in user's own principal only, no
        // admin gate, no cross-agent search box. Deliberately a simplified
        // sibling rather than a shared/parameterized refactor of the admin
        // version under this pass's time budget - same "surgical, don't
        // rewrite a working page" principle the WhiteCore-Dev docs
        // themselves call out.
        private void HandleMyTransactions(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (m_CurrencyService == null)
            {
                WritePage(request, response, PageTitle("My Transactions"),
                        "<h1>My Transactions</h1><p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p><p>Currency service is not available.</p>");
                return;
            }

            string tab = request.QueryString.Get("tab") == "purchases" ? "purchases" : "transfers";
            int.TryParse(request.QueryString.Get("start"), out int start);
            if (start < 0)
                start = 0;
            const int pageSize = 25;
            const int overfetch = 1000;

            DateTime dateStart = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime dateEnd = DateTime.UtcNow.AddDays(1);
            UUID agentID = session.PrincipalID;

            StringBuilder rows = new StringBuilder();
            bool hasNextPage;

            if (tab == "purchases")
            {
                List<CurrencyPurchase> purchases = m_CurrencyService.GetPurchaseHistory(agentID, dateStart, dateEnd, null, null);
                hasNextPage = start + pageSize < purchases.Count;

                rows.Append("<h2>Real-Money Purchases (L$)</h2>");
                rows.Append("<table><tr><th>Date</th><th>L$ credited</th><th>Real amount (hundredths)</th></tr>");
                foreach (CurrencyPurchase p in purchases.Skip(start).Take(pageSize))
                {
                    rows.Append("<tr><td>").Append(Html(p.PurchaseDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>")
                        .Append("<td>").Append(p.Amount).Append("</td>")
                        .Append("<td>").Append(p.RealAmount).Append("</td></tr>");
                }
                rows.Append("</table>");
                if (purchases.Count == 0)
                    rows.Append("<p>You haven't made any real-money purchases yet.</p>");

                // Store purchases - merged in from a genuinely different
                // table (store_orders, not currency_purchases) at the
                // user's explicit request, since this tab's name reads as
                // "everything I've bought" and Store buys (prim packs,
                // region orders) previously never showed up here at all.
                // Kept to a recent digest rather than sharing the above
                // pagination controls (the two lists have no reason to be
                // the same length) - /store/my-purchases remains the full,
                // paginated-by-scrolling archive with Status/Expires, this
                // is just enough to answer "did I buy this" from one page.
                if (m_StoreService != null)
                {
                    List<StoreOrder> storeOrders = m_StoreService.GetOrdersByResident(agentID)
                            .OrderByDescending(o => o.Created).Take(pageSize).ToList();

                    rows.Append("<h2>Store Purchases</h2>");
                    if (storeOrders.Count == 0)
                    {
                        rows.Append("<p>You haven't bought anything from the Store yet.</p>");
                    }
                    else
                    {
                        rows.Append("<table><tr><th>Date</th><th>Item</th><th>Currency</th><th>Amount</th><th>Status</th></tr>");
                        foreach (StoreOrder order in storeOrders)
                        {
                            StoreCatalogItem item = m_StoreService.GetCatalogItem(order.CatalogItemID);
                            rows.Append("<tr><td>").Append(order.Created.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC</td>")
                                .Append("<td>").Append(Html(item != null ? item.Name : order.OrderType)).Append("</td>")
                                .Append("<td>").Append(order.CurrencyUsed).Append("</td>")
                                .Append("<td>").Append(order.AmountCharged.ToString("N0")).Append("</td>")
                                .Append("<td>").Append(Html(order.Status)).Append("</td></tr>");
                        }
                        rows.Append("</table>");
                        rows.Append("<p><a href=\"").Append(BasePath).Append("/store/my-purchases\">View full Store purchase history</a></p>");
                    }
                }
            }
            else
            {
                List<CurrencyTransfer> sent = m_CurrencyService.GetTransactionHistory(UUID.Zero, agentID, dateStart, dateEnd, 0, overfetch);
                List<CurrencyTransfer> received = m_CurrencyService.GetTransactionHistory(agentID, UUID.Zero, dateStart, dateEnd, 0, overfetch);
                Dictionary<UUID, CurrencyTransfer> merged = new Dictionary<UUID, CurrencyTransfer>();
                foreach (CurrencyTransfer t in sent)
                    merged[t.ID] = t;
                foreach (CurrencyTransfer t in received)
                    merged[t.ID] = t;
                List<CurrencyTransfer> transfers = merged.Values.OrderByDescending(t => t.TransferDate).ToList();

                hasNextPage = start + pageSize < transfers.Count;

                rows.Append("<table><tr><th>Date</th><th>From</th><th>To</th><th>Amount</th><th>Description</th><th>Balance</th></tr>");
                foreach (CurrencyTransfer t in transfers.Skip(start).Take(pageSize))
                {
                    // Reference's running-balance column - real gap, and the
                    // data was already sitting right on this row unused
                    // (ToBalance/FromBalance are populated by the currency
                    // service on every transfer). Show whichever side of the
                    // transfer resolves to this viewer's own resulting balance.
                    int resultingBalance = t.ToAgent.Equals(agentID) ? t.ToBalance : t.FromBalance;

                    rows.Append("<tr><td>").Append(Html(t.TransferDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>")
                        .Append("<td>").Append(Html(ResolveAgentName(t.FromAgent))).Append("</td>")
                        .Append("<td>").Append(Html(ResolveAgentName(t.ToAgent))).Append("</td>")
                        .Append("<td>").Append(t.Amount).Append("</td>")
                        .Append("<td>").Append(Html(t.Description)).Append("</td>")
                        .Append("<td>").Append(resultingBalance).Append("</td></tr>");
                }
                rows.Append("</table>");
                if (transfers.Count == 0)
                    rows.Append("<p>No transactions on file yet.</p>");
            }

            string tabLink(string t, string label) =>
                    "<a href=\"" + BasePath + "/transactions?tab=" + t + "\"" + (t == tab ? " style=\"font-weight:bold\"" : string.Empty) + ">" + label + "</a>";
            string nextLink = hasNextPage
                    ? "<p><a href=\"" + BasePath + "/transactions?tab=" + tab + "&start=" + (start + pageSize) + "\">Next page</a></p>"
                    : string.Empty;
            string prevLink = start > 0
                    ? "<p><a href=\"" + BasePath + "/transactions?tab=" + tab + "&start=" + Math.Max(0, start - pageSize) + "\">Previous page</a></p>"
                    : string.Empty;

            string body = "<h1>My Transactions</h1>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>"
                    + "<p>" + tabLink("transfers", "Transfers") + " | " + tabLink("purchases", "Purchases") + "</p>"
                    + rows.ToString() + prevLink + nextLink;

            WritePage(request, response, PageTitle("My Transactions"), body);
        }

        // My Classifieds - resident self-service create/edit/delete,
        // filling the gap the splash's read-only "Featured Classifieds"
        // widget (task #30) left: users could never create one from the
        // web, only from their viewer's Profile tab. All the backend
        // primitives (ClassifiedUpdate/ClassifiedDelete/
        // ClassifiedInfoRequest/GetClassifiedRecords) already existed on
        // IUserProfilesService for the viewer-facing path - this just
        // exposes the same calls through a web form. No web-based "pick a
        // spot" mechanism exists, so a created listing's location defaults
        // to the chosen region's center (128,128,25), same fallback
        // OpenSim-Grid-Interface's own classifieds tooling uses for messy
        // position data.
        private void HandleMyClassifieds(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (m_UserProfilesService == null)
            {
                WritePage(request, response, PageTitle("My Classifieds"),
                        "<h1>My Classifieds</h1><p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p><p>Profiles service is not available.</p>");
                return;
            }

            string idParam = request.QueryString.Get("id");
            UserClassifiedAdd editing = null;
            if (!string.IsNullOrEmpty(idParam) && UUID.TryParse(idParam, out UUID editId))
            {
                UserClassifiedAdd candidate = new UserClassifiedAdd { ClassifiedId = editId };
                string infoResult = string.Empty;
                if (m_UserProfilesService.ClassifiedInfoRequest(ref candidate, ref infoResult) && candidate.CreatorId == session.PrincipalID)
                    editing = candidate;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>My Classifieds</h1><p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a></p>");

            OSD recordsOsd = m_UserProfilesService.AvatarClassifiedsRequest(session.PrincipalID);
            if (recordsOsd is OSDArray records && records.Count > 0)
            {
                // Reference's list table (user/classifieds.html) shows
                // Creation Date/Category/Description/Price/Expiration, not
                // just Name - real gap. AvatarClassifiedsRequest itself only
                // ever returns id+name (that's the real SL protocol's own
                // AvatarClassifiedsReply shape), so the rest needs one
                // ClassifiedInfoRequest per row, same call HandleMyClassifieds
                // already makes for the single "editing" case above.
                sb.Append("<table><tr><th>Name</th><th>Category</th><th>Description</th><th>Price</th>")
                  .Append("<th>Created</th><th>Expires</th><th></th><th></th></tr>");
                foreach (OSD entry in records)
                {
                    if (entry is not OSDMap map)
                        continue;
                    UUID adId = map["classifieduuid"].AsUUID();
                    string name = map["name"].AsString();

                    UserClassifiedAdd detail = new UserClassifiedAdd { ClassifiedId = adId };
                    string detailResult = string.Empty;
                    bool haveDetail = m_UserProfilesService.ClassifiedInfoRequest(ref detail, ref detailResult);

                    sb.Append("<tr><td>").Append(Html(name)).Append("</td>");
                    if (haveDetail)
                    {
                        string categoryName = detail.Category >= 0 && detail.Category < ClassifiedCategories.Length
                                ? ClassifiedCategories[detail.Category] : "Unknown";
                        sb.Append("<td>").Append(Html(categoryName)).Append("</td>")
                          .Append("<td>").Append(Html(detail.Description)).Append("</td>")
                          .Append("<td>").Append(detail.Price).Append("</td>")
                          .Append("<td>").Append(Html(Utils.UnixTimeToDateTime((uint)detail.CreationDate).ToString("MMM d, yyyy"))).Append("</td>")
                          .Append("<td>").Append(Html(Utils.UnixTimeToDateTime((uint)detail.ExpirationDate).ToString("MMM d, yyyy"))).Append("</td>");
                    }
                    else
                    {
                        sb.Append("<td colspan=\"5\"></td>");
                    }
                    sb.Append("<td><a href=\"").Append(BasePath).Append("/myclassifieds?id=").Append(adId).Append("\">Edit</a></td>")
                      .Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/myclassifieds/delete\">")
                      .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(adId).Append("\">")
                      .Append("<button type=\"submit\">Delete</button></form></td></tr>");
                }
                sb.Append("</table>");
            }
            else
            {
                sb.Append("<p>You haven't posted any classifieds yet.</p>");
            }

            List<GridRegion> regions = m_GridService?.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000) ?? new List<GridRegion>();
            StringBuilder regionOptions = new StringBuilder();
            foreach (GridRegion region in regions)
            {
                bool selected = editing != null && editing.SimName == region.RegionName;
                regionOptions.Append("<option value=\"").Append(Html(region.RegionName)).Append("\"")
                        .Append(selected ? " selected" : string.Empty).Append(">").Append(Html(region.RegionName)).Append("</option>");
            }

            StringBuilder categoryOptions = new StringBuilder();
            for (int i = 1; i < ClassifiedCategories.Length; i++)
            {
                bool selected = editing != null && editing.Category == i;
                categoryOptions.Append("<option value=\"").Append(i).Append("\"").Append(selected ? " selected" : string.Empty)
                        .Append(">").Append(Html(ClassifiedCategories[i])).Append("</option>");
            }

            string formTitle = editing != null ? "Edit Classified" : "Post a Classified";
            sb.Append("<h2>").Append(formTitle).Append("</h2>")
              .Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myclassifieds/save\">")
              .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(editing != null ? editing.ClassifiedId.ToString() : string.Empty).Append("\">")
              .Append("<label>Title<br/><input type=\"text\" name=\"title\" value=\"").Append(Html(editing?.Name ?? string.Empty)).Append("\" required></label><br/>")
              .Append("<label>Category<br/><select name=\"category\">").Append(categoryOptions).Append("</select></label><br/>")
              .Append("<label>Region<br/><select name=\"region\">").Append(regionOptions).Append("</select></label><br/>")
              .Append("<label>Description<br/><textarea name=\"description\" rows=\"4\">").Append(Html(editing?.Description ?? string.Empty)).Append("</textarea></label><br/>")
              .Append("<button type=\"submit\">").Append(editing != null ? "Save changes" : "Post classified").Append("</button>")
              .Append(editing != null ? " <a href=\"" + BasePath + "/myclassifieds\">Cancel</a>" : string.Empty)
              .Append("</form>");

            WritePage(request, response, PageTitle("My Classifieds"), sb.ToString());
        }

        private void HandleMyClassifiedsSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || m_UserProfilesService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string idValue = FormValue(form, "id");
            string title = FormValue(form, "title").Trim();
            string regionName = FormValue(form, "region");
            string description = FormValue(form, "description");
            int.TryParse(FormValue(form, "category"), out int category);

            if (string.IsNullOrEmpty(title))
            {
                response.Redirect(BasePath + "/myclassifieds", HttpStatusCode.Redirect);
                return;
            }

            UserClassifiedAdd ad = null;
            if (!string.IsNullOrEmpty(idValue) && UUID.TryParse(idValue, out UUID existingId))
            {
                UserClassifiedAdd candidate = new UserClassifiedAdd { ClassifiedId = existingId };
                string infoResult = string.Empty;
                if (m_UserProfilesService.ClassifiedInfoRequest(ref candidate, ref infoResult) && candidate.CreatorId == session.PrincipalID)
                    ad = candidate;
            }

            if (ad == null)
                ad = new UserClassifiedAdd { ClassifiedId = UUID.Random(), CreatorId = session.PrincipalID, Flags = 2 };

            ad.Name = title;
            ad.Category = category;
            ad.Description = string.IsNullOrEmpty(description) ? "No Description" : description;
            ad.SimName = regionName;
            // GridRegion.RegionLocX/Y are already in meters (unlike
            // RegionInfo's own same-named fields, which are in region
            // units - see IGridService.cs's own "DANGER DANGER" comment),
            // so the real global position is just the region's origin plus
            // a fixed in-region offset - no x256 conversion needed here.
            // Previously hardcoded to "<128,128,25>" with no region offset
            // at all, which was only ever correct for a region sitting at
            // grid origin (0,0) - every other region got a bogus Teleport/
            // Map target.
            GridRegion targetRegion = m_GridService?.GetRegionByName(UUID.Zero, regionName);
            Vector3 classifiedGlobalPos = targetRegion != null
                    ? new Vector3(targetRegion.RegionLocX + 128, targetRegion.RegionLocY + 128, 25)
                    : new Vector3(128, 128, 25);
            ad.GlobalPos = classifiedGlobalPos.ToString();
            ad.ParcelName = regionName;

            string result = string.Empty;
            m_UserProfilesService.ClassifiedUpdate(ad, ref result);

            response.Redirect(BasePath + "/myclassifieds", HttpStatusCode.Redirect);
        }

        private void HandleMyClassifiedsDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || m_UserProfilesService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (UUID.TryParse(FormValue(form, "id"), out UUID id))
            {
                UserClassifiedAdd candidate = new UserClassifiedAdd { ClassifiedId = id };
                string infoResult = string.Empty;
                if (m_UserProfilesService.ClassifiedInfoRequest(ref candidate, ref infoResult) && candidate.CreatorId == session.PrincipalID)
                    m_UserProfilesService.ClassifiedDelete(id);
            }

            response.Redirect(BasePath + "/myclassifieds", HttpStatusCode.Redirect);
        }

        // My Events - resident self-service counterpart to /admin/events,
        // matching WhiteCore-Dev's own events.html (any logged-in user can
        // add an event there, not just admins - unlike this connector's
        // original admin-only design from task #31/32). Ownership is
        // enforced via EventItem.CreatorId (added specifically to support
        // this - see GridEventData.cs). IEventsService has no
        // "GetByCreator" query, so this filters the same GetUpcoming(0,100)
        // list HandleWelcome's splash widget already uses - acceptable
        // since it's a grid-wide upcoming-events list, not a
        // per-user-scoped query with its own scaling concern.
        private void HandleMyEvents(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (m_EventsService == null)
            {
                WritePage(request, response, PageTitle("My Events"),
                        "<h1>My Events</h1><p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p><p>Events service is not available.</p>");
                return;
            }

            string idParam = request.QueryString.Get("id");
            EventItem editing = null;
            if (!string.IsNullOrEmpty(idParam) && UUID.TryParse(idParam, out UUID editId))
            {
                EventItem candidate = m_EventsService.Get(editId);
                if (candidate != null && candidate.CreatorId == session.PrincipalID)
                    editing = candidate;
            }

            List<EventItem> mine = m_EventsService.GetUpcoming(0, 100)
                    .Where(e => e.CreatorId == session.PrincipalID)
                    .ToList();

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>My Events</h1><p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a></p>");

            if (mine.Count > 0)
            {
                // Reference's list table (user/events.html) also shows
                // Location/Description/Category/Duration - real gap, added
                // below from fields EventItem already carries. Maturity and
                // Cover Charge are NOT added here - EventItem has no such
                // fields anywhere in the model (confirmed via
                // OpenSim/Framework/GridEventData.cs), a real but deeper
                // data-model gap out of scope for a display-only fix.
                sb.Append("<table><tr><th>Date</th><th>Title</th><th>Location</th><th>Category</th>")
                  .Append("<th>Description</th><th>Duration</th><th></th><th></th></tr>");
                foreach (EventItem ev in mine)
                {
                    sb.Append("<tr><td>").Append(Html(ev.EventDate.ToString("yyyy-MM-dd HH:mm"))).Append(" UTC</td>")
                      .Append("<td>").Append(Html(ev.Title)).Append("</td>")
                      .Append("<td>").Append(Html(ev.Location)).Append("</td>")
                      .Append("<td>").Append(Html(ev.Category)).Append("</td>")
                      .Append("<td>").Append(Html(ev.Description)).Append("</td>")
                      .Append("<td>").Append(ev.DurationMinutes).Append(" min</td>")
                      .Append("<td><a href=\"").Append(BasePath).Append("/myevents?id=").Append(ev.ID).Append("\">Edit</a></td>")
                      .Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/myevents/delete\">")
                      .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(ev.ID).Append("\">")
                      .Append("<button type=\"submit\">Delete</button></form></td></tr>");
                }
                sb.Append("</table>");
            }
            else
            {
                sb.Append("<p>You haven't posted any events yet.</p>");
            }

            string dateValue = editing != null ? editing.EventDate.ToString("yyyy-MM-ddTHH:mm") : string.Empty;
            string formTitle = editing != null ? "Edit Event" : "Add Event";

            // Region picker - same pattern as HandleMyClassifieds' own
            // region <select> - needed so the event has a real teleportable
            // location, not just the free-text Location display string.
            // Feeds GlobalPos in HandleMyEventsSave, which is what actually
            // drives the viewer's Teleport/Map buttons via EventInfoReply
            // (see ConfluenceSearchModule.EventInfoRequest).
            List<GridRegion> eventRegions = m_GridService?.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000) ?? new List<GridRegion>();
            StringBuilder eventRegionOptions = new StringBuilder();
            foreach (GridRegion region in eventRegions)
            {
                bool selected = editing != null && editing.Location == region.RegionName;
                eventRegionOptions.Append("<option value=\"").Append(Html(region.RegionName)).Append("\"")
                        .Append(selected ? " selected" : string.Empty).Append(">").Append(Html(region.RegionName)).Append("</option>");
            }

            sb.Append("<h2>").Append(formTitle).Append("</h2>")
              .Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myevents/save\">")
              .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(editing != null ? editing.ID.ToString() : string.Empty).Append("\">")
              .Append("<label>Title<br/><input type=\"text\" name=\"title\" value=\"").Append(Html(editing?.Title ?? string.Empty)).Append("\" required></label><br/>")
              .Append("<label>Category<br/><input type=\"text\" name=\"category\" value=\"").Append(Html(editing?.Category ?? string.Empty)).Append("\" placeholder=\"Live Music, Nightlife, Games...\"></label><br/>")
              .Append("<label>Date/time (grid time, UTC)<br/><input type=\"datetime-local\" name=\"event_date\" value=\"").Append(Html(dateValue)).Append("\" required></label><br/>")
              .Append("<label>Duration (minutes)<br/><input type=\"number\" name=\"duration\" value=\"").Append(editing?.DurationMinutes ?? 60).Append("\" min=\"0\"></label><br/>")
              .Append("<label>Region (for Teleport/Map)<br/><select name=\"region\">").Append(eventRegionOptions).Append("</select></label><br/>")
              .Append("<label>Location<br/><input type=\"text\" name=\"location\" value=\"").Append(Html(editing?.Location ?? string.Empty)).Append("\" placeholder=\"Region or venue name\"></label><br/>")
              .Append("<label>Description<br/><textarea name=\"description\" rows=\"4\">").Append(Html(editing?.Description ?? string.Empty)).Append("</textarea></label><br/>")
              .Append("<button type=\"submit\">").Append(editing != null ? "Save changes" : "Add event").Append("</button>")
              .Append(editing != null ? " <a href=\"" + BasePath + "/myevents\">Cancel</a>" : string.Empty)
              .Append("</form>");

            WritePage(request, response, PageTitle("My Events"), sb.ToString());
        }

        private void HandleMyEventsSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || m_EventsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string idValue = FormValue(form, "id");
            string title = FormValue(form, "title").Trim();
            string category = FormValue(form, "category").Trim();
            string dateValue = FormValue(form, "event_date").Trim();
            string location = FormValue(form, "location").Trim();
            string regionName = FormValue(form, "region").Trim();
            string description = FormValue(form, "description");
            int.TryParse(FormValue(form, "duration"), out int duration);

            if (string.IsNullOrEmpty(title)
                    || !DateTime.TryParse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime eventDate))
            {
                response.Redirect(BasePath + "/myevents", HttpStatusCode.Redirect);
                return;
            }

            EventItem item = null;
            if (!string.IsNullOrEmpty(idValue) && UUID.TryParse(idValue, out UUID existingId))
            {
                EventItem candidate = m_EventsService.Get(existingId);
                if (candidate != null && candidate.CreatorId == session.PrincipalID)
                    item = candidate;
            }

            if (item == null)
                item = new EventItem { ID = UUID.Random(), CreatorId = session.PrincipalID };

            item.Title = title;
            item.Category = category;
            item.EventDate = DateTime.SpecifyKind(eventDate, DateTimeKind.Utc);
            item.DurationMinutes = duration > 0 ? duration : 60;
            item.Location = location;
            item.Description = description;

            // Same region-origin-plus-fixed-offset math as HandleMyClassifiedsSave's
            // GlobalPos fix - GridRegion.RegionLocX/Y are already in meters.
            GridRegion eventRegion = m_GridService?.GetRegionByName(UUID.Zero, regionName);
            Vector3 eventGlobalPos = eventRegion != null
                    ? new Vector3(eventRegion.RegionLocX + 128, eventRegion.RegionLocY + 128, 25)
                    : new Vector3();
            item.GlobalPos = eventRegion != null ? eventGlobalPos.ToString() : string.Empty;

            m_EventsService.Store(item);

            response.Redirect(BasePath + "/myevents", HttpStatusCode.Redirect);
        }

        private void HandleMyEventsDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || m_EventsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (UUID.TryParse(FormValue(form, "id"), out UUID id))
            {
                EventItem candidate = m_EventsService.Get(id);
                if (candidate != null && candidate.CreatorId == session.PrincipalID)
                    m_EventsService.Delete(id);
            }

            response.Redirect(BasePath + "/myevents", HttpStatusCode.Redirect);
        }

        // Special announcement banner - matches WhiteCore-Dev's
        // welcomescreen_manager.html "special window" toggle (grid status
        // online/offline, special window title/text/color/enabled). Reuses
        // the existing IGridSettingsService/GetSetting pattern already
        // used for GridName/WelcomeMessage rather than a new service.
        private string RenderAnnouncement()
        {
            if (GetSetting("AnnouncementEnabled", "false") != "true")
                return string.Empty;

            string title = GetSetting("AnnouncementTitle", string.Empty);
            string text = GetSetting("AnnouncementText", string.Empty);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(text))
                return string.Empty;

            string color = GetSetting("AnnouncementColor", "#3b82f6");
            StringBuilder sb = new StringBuilder();
            sb.Append("<div class=\"announcement\" style=\"border-left-color:").Append(Html(color)).Append(";\">");
            if (!string.IsNullOrEmpty(title))
                sb.Append("<strong>").Append(Html(title)).Append("</strong> ");
            if (!string.IsNullOrEmpty(text))
                sb.Append(Html(text));
            sb.Append("</div>");
            return sb.ToString();
        }

        // Features - the last "first-landing" page from this pass, and
        // deliberately built differently from OpenSim-Grid-Interface's own
        // features.php, which is driven entirely by PHP constants an
        // operator sets by hand (OS_VERSION_MAIN, FEATURE_HYPERGRID, etc.) -
        // essentially a curated claim sheet, not live introspection. This
        // page does the same for the parts it genuinely CAN introspect live
        // (Robust's own GridService/UserAccountService/CurrencyService -
        // same pattern HandleAdminStats already uses), and is honest about
        // the rest: this connector runs in Robust, a separate process from
        // any individual region, so it has no channel to ask a region what
        // script/physics engine it's actually running (confirmed by
        // checking - those settings live in each region's own OpenSim.ini,
        // never surfaced to Robust). The platform capability list below is
        // a curated fact sheet about what this codebase actually has,
        // informed by this session's own real batches (see PROJECT_LOG.md),
        // not a live query - same underlying honesty standard as choosing
        // not to fake ban/kick/message-online-user earlier in this thread.
        private void HandleFeatures(IOSHttpRequest request, IOSHttpResponse response)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-stars\"></i> Grid Features</h1>");
            sb.Append("<p>Confluence runs on OpenSimulator, extended with a set of natively-built systems ")
              .Append("(not addon modules) covering currency, marketplace, search, moderation, and grid administration.</p>");

            // Platform Overview table - same shape as OpenSim-Grid-Interface's
            // features.php ("Supported Viewers"/"Main Simulator"/"Main
            // Version" rows), but every value here is either a compile-time
            // constant already used elsewhere on this page (VersionInfo) or
            // the same viewer list HandleViewers already publishes, not a
            // second hand-typed copy that could drift out of sync.
            sb.Append("<div class=\"content-card\"><h2><i class=\"bi bi-activity\"></i> Platform Overview</h2>")
              .Append("<table><tbody>")
              .Append("<tr><th>Supported Viewers</th><td>").Append(Html(string.Join(", ", DesktopViewers.Select(v => v.Name)))).Append("</td></tr>")
              .Append("<tr><th>Core Platform</th><td>OpenSimulator (Confluence build)</td></tr>")
              .Append("<tr><th>Core Version</th><td><span title=\"commit ").Append(global::OpenSim.VersionInfo.BuildCommitHash).Append("\">")
              .Append(global::OpenSim.VersionInfo.DisplayVersionNumber).Append("</span></td></tr>")
              .Append("</tbody></table></div>");

            sb.Append("<h2><i class=\"bi bi-activity\"></i> Live Grid Snapshot</h2><div class=\"stats-grid\">");
            if (m_GridService != null)
            {
                // FilterListedRegions on top of the usual alive-probe -
                // this whole snapshot is public-facing, so an owner's
                // Unlisted opt-out applies to every figure here (region
                // count, area, largest-region) same as the map/tables.
                List<GridRegion> regions = FilterListedRegions(FilterOnlineRegions(
                        m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000)));
                long totalAreaSqm = 0;
                int hgOpenCount = 0;
                long largestRegionSqm = 0;
                foreach (GridRegion region in regions)
                {
                    long areaSqm = (long)region.RegionSizeX * region.RegionSizeY;
                    totalAreaSqm += areaSqm;
                    if (areaSqm > largestRegionSqm)
                        largestRegionSqm = areaSqm;
                    if (m_RegionHGService == null || m_RegionHGService.IsRegionOpen(region.RegionID))
                        hgOpenCount++;
                }
                AppendStat(sb, "Regions Online", regions.Count.ToString(), totalAreaSqm.ToString("N0") + " m² total");
                AppendStat(sb, "Hypergrid-Open Regions", hgOpenCount + " / " + regions.Count, "Travel here from other OpenSim grids");
                AppendStat(sb, "Largest Region", largestRegionSqm.ToString("N0") + " m²", largestRegionSqm > 256 * 256 ? "VarRegion in use" : "Standard region size");
            }
            if (m_UserAccountService != null)
                AppendStat(sb, "Registered Residents", m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1").Count.ToString(), string.Empty);
            // Real gap vs. OpenSim-Grid-Interface's features.php ("Main
            // Simulator"/"Main Version" table row) - OpenSim.Framework.
            // VersionInfo is the same compile-time constant the console
            // banner and login response already report, not a guess.
            AppendStat(sb, "OpenSimulator Version", global::OpenSim.VersionInfo.DisplayVersionNumber, "Core platform");
            sb.Append("</div>");

            // Open-source repos - real URLs pulled from this checkout's own
            // git remotes (git remote -v), not hand-typed, so they can't
            // drift from the actual project. OGI is credited as the
            // optional swap-out web interface, matching the "not required"
            // framing already used for it in Platform Capabilities below.
            sb.Append("<h2><i class=\"bi bi-github\"></i> Open Source</h2><div class=\"feature-grid-3\">")
              .Append("<div class=\"feature-card\"><h3><i class=\"bi bi-git\"></i> Confluence</h3>")
              .Append("<p>The grid engine itself - OpenSimulator core plus every natively-built system on this page (currency, search, moderation, admin).</p>")
              .Append("<p><a href=\"https://github.com/Ramius1701/OpenSim-Confluence\" target=\"_blank\" rel=\"noopener\"><i class=\"bi bi-github\"></i> Ramius1701/OpenSim-Confluence</a></p></div>")
              .Append("<div class=\"feature-card\"><h3><i class=\"bi bi-layout-text-window-reverse\"></i> Grid Web Interface</h3>")
              .Append("<p>An optional standalone PHP web front-end for the grid - swappable, not required (this built-in WebUI ships by default).</p>")
              .Append("<p><a href=\"https://github.com/Ramius1701/OpenSim-Grid-Interface\" target=\"_blank\" rel=\"noopener\"><i class=\"bi bi-github\"></i> Ramius1701/OpenSim-Grid-Interface</a></p></div>")
              .Append("</div>");

            AppendPoweredBySection(sb, GetSetting("PoweredByItems", string.Empty));

            // Regrouped from a flat 12-item list into themed, icon-headed
            // hover-lift cards - same real capabilities as before (nothing
            // here is fabricated - see the per-item honesty note in the
            // class comment above), just presented the way a grid owner
            // evaluating this platform actually wants to scan it, matching
            // the reference project's own icon-per-category treatment.
            // Native Currency moved out of this list into its own Economy
            // section below rather than duplicated in both places.
            sb.Append("<h2><i class=\"bi bi-sliders\"></i> Platform Capabilities</h2><div class=\"feature-grid-3\">");

            AppendIconFeatureCard(sb, "globe-americas", "World & Travel", new[]
            {
                ("Hypergrid Travel", true, "Teleport to and from other OpenSim grids"),
                ("VarRegions", true, "Larger-than-standard regions with no internal sim-crossing stutter"),
                ("On-Demand Regions", true, "Idle regions sleep until a visitor arrives, then wake automatically"),
                // Real gap vs. the reference's own "Voice" card - Confluence
                // has no bundled voice integration (no Vivox/Mumble/WebRTC
                // anywhere in this codebase, confirmed rather than assumed).
                // Stated honestly rather than left silent, same standard as
                // the ban/kick/message-online-user gaps documented earlier.
                ("Voice", false, "Not bundled - a standard Vivox/Mumble config can be added the same way vanilla OpenSim supports it")
            });

            AppendIconFeatureCard(sb, "shield-check", "Safety & Moderation", new[]
            {
                ("Native Mute List", true, "Server-side mute list, no addon module required"),
                ("Grid-Wide Viewer Ban", true, "IP-range and hardware-signature bans enforced at login"),
                ("Abuse Reports", true, "In-viewer reporting with a web-based admin queue")
            });

            AppendIconFeatureCard(sb, "gear-wide-connected", "Platform Services", new[]
            {
                ("Native Search", true, "Grid-wide place search, integrated with the viewer's own Search window"),
                // Corrected wording (2026-09-03) after verifying the real
                // implementation (SimProtectionModule): it's a hard on/off
                // disable of scripts/physics on sustained low FPS, not
                // graduated throttling, and sustained near-zero FPS
                // triggers a full region restart - a bigger deal than the
                // old wording implied, stated honestly rather than left out.
                ("SimProtection", true, "Auto-disables scripts/physics on sustained low FPS and re-enables once it recovers; restarts the region if FPS stays near zero"),
                ("Scripted NPCs", true, "osNpc bots with avatar-follow and tag-group management")
            });

            AppendIconFeatureCard(sb, "display", "Administration & Building", new[]
            {
                ("Web-Based Admin", true, "Full grid administration - users, estates, regions, currency, events - from any browser"),
                ("Mesh & Scripting", true, "Mesh uploads, LSL and OSSL scripting")
            });

            sb.Append("</div>");

            // Generic OpenSim region-configuration patterns, not a claim
            // that Confluence enforces these as a formal mechanism -
            // "Homestead"/"Openspace" are SL-heritage naming conventions
            // an operator applies to their own region-size/prim-density
            // choices, not an engine-level region type. Framed that way
            // deliberately, matching this page's existing honesty standard.
            sb.Append("<h2><i class=\"bi bi-grid-3x3-gap\"></i> Region Configuration Options</h2><div class=\"feature-grid-3\">");
            AppendIconFeatureCard(sb, "arrows-fullscreen", "VarRegions", new[]
            {
                ("Layout", false, "One region with a larger footprint than standard 256x256 (e.g. 512x512 or 1024x1024), no internal border crossings"),
                ("Use case", false, "Sailing, aviation, road networks, large landscapes"),
                ("Experience", false, "No sim-crossing stutter - avatars and vehicles move smoothly across the whole area")
            });
            AppendIconFeatureCard(sb, "app", "Full-Size Regions", new[]
            {
                ("Layout", false, "Standard 256x256 footprint, the OpenSim default"),
                ("Use case", false, "Events, clubs, communities, roleplay hubs"),
                ("Prim density", false, "Configurable per region/grid policy, same as any standard region")
            });
            AppendIconFeatureCard(sb, "house-door", "Lighter-Traffic Regions", new[]
            {
                ("Common naming", false, "Often called \"Homestead\" or \"Openspace\" style, by SL-era convention - not a distinct Confluence engine feature"),
                ("Use case", false, "Quiet residential areas, scenic or park-style regions, sky/ocean buffer space"),
                ("Configuration", false, "An operator tunes prim caps and avatar limits lower for these, same config surface as any other region")
            });
            sb.Append("</div>");

            sb.Append("<h2><i class=\"bi bi-currency-exchange\"></i> Economy &amp; Currency</h2><div class=\"feature-grid-3\">");
            AppendIconFeatureCard(sb, "currency-dollar", "Native Currency" + (m_CurrencyService != null ? " <span class=\"pill pill-yes\">Active</span>" : " <span class=\"pill pill-no\">Unavailable</span>"), new[]
            {
                ("Ledger", false, "Built-in transaction history and group treasuries - not a third-party dependency"),
                ("Web access", false, "Balance and transaction pages from any browser, no separate money-server process"),
                ("Protocol", false, "Answers the same buy/sell currency.php surface real viewers already expect")
            });
            AppendIconFeatureCard(sb, "wallet2", "Gloebit <span class=\"pill\" style=\"background:rgba(59,130,246,.15);color:var(--accent-bright)\">Optional</span>", new[]
            {
                ("What it is", false, "A real-money payment gateway, for grids that want a paid economy instead of (or alongside) the native ledger"),
                ("How it's added", false, "Swappable via the addon-modules Gloebit integration - not required, not enabled by default")
            });
            // Added 2026-09-03 - real, live-tested this session, but was
            // missing from this page entirely (the page's own intro
            // sentence didn't even list "marketplace" among what it
            // covers). See MARKETPLACE.md for the full setup/limitation
            // writeup this card summarizes.
            AppendIconFeatureCard(sb, "bag", "Marketplace" + (m_MarketplaceListingsService != null ? " <span class=\"pill pill-yes\">Active</span>" : " <span class=\"pill pill-no\">Unavailable</span>"), new[]
            {
                ("Browse & Buy", false, "Grid-wide storefront at /marketplace, ConfluenceCurrency checkout, unlimited or real finite stock per listing"),
                ("Listing Management", false, "Create and manage listings entirely from the web at /marketplace/manage - drag an item into a folder, no in-world listing station needed"),
                // Honest gap, same standard as Voice above - Firestorm/
                // AyaneStorm hard-block their own in-viewer Marketplace
                // Listings floater outside real Second Life, confirmed
                // against source, no known workaround - so web management
                // is the primary path here, not a missing capability of
                // this grid's own DirectDelivery implementation (which is
                // built and dormant, ready if a non-blocking viewer is
                // ever used).
                ("Viewer Floater", false, "Blocked by Firestorm/AyaneStorm outside real Second Life - use the web pages above instead, same login")
            });
            sb.Append("</div>");

            AppendMembershipPerksSection(sb, GetSetting("MembershipPerksFree", string.Empty), GetSetting("MembershipPerksExtra", string.Empty));

            sb.Append("<div class=\"content-card text-center\" style=\"text-align:center;padding-top:20px;\">")
              .Append("<p><a href=\"").Append(BasePath).Append("/viewers\"><i class=\"bi bi-display\"></i> Get a viewer to explore</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/destinations\"><i class=\"bi bi-map\"></i> See where to go</a></p></div>");

            WritePage(request, response, PageTitle("Features"), sb.ToString());
        }

        // Both sections are entirely admin-authored (Grid Settings -> Features
        // Page) and hidden when unconfigured - per-grid infra/perks facts
        // can't be derived from code the way the rest of this page's content
        // is, and OpenSim-Grid-Interface's own template defaults for these
        // turned out to be unconfigured placeholder text (checked its
        // env.php), not real facts about any specific grid. See
        // HandleAdminSettings for the "Group|icon|Title|Subtitle" format.
        private static void AppendPoweredBySection(StringBuilder sb, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;

            string currentGroup = null;
            StringBuilder items = new StringBuilder();
            foreach (string line in raw.Split('\n'))
            {
                string trimmed = line.Trim().TrimEnd('\r');
                if (trimmed.Length == 0)
                    continue;
                string[] parts = trimmed.Split('|');
                if (parts.Length < 3)
                    continue;

                string group = parts[0].Trim();
                string icon = parts[1].Trim();
                string title = parts[2].Trim();
                string sub = parts.Length > 3 ? parts[3].Trim() : string.Empty;

                if (group.Length > 0 && group != currentGroup)
                {
                    items.Append("<div class=\"powered-group-label\">").Append(Html(group)).Append("</div>");
                    currentGroup = group;
                }

                items.Append("<div class=\"powered-tile\">");
                if (icon.Length > 0)
                    items.Append("<i class=\"bi bi-").Append(Html(icon)).Append("\" aria-hidden=\"true\"></i>");
                items.Append("<div class=\"powered-tile-title\">").Append(Html(title)).Append("</div>");
                if (sub.Length > 0)
                    items.Append("<div class=\"powered-tile-sub\">").Append(Html(sub)).Append("</div>");
                items.Append("</div>");
            }

            if (items.Length == 0)
                return;

            sb.Append("<div class=\"content-card\"><h2><i class=\"bi bi-lightning-charge\"></i> Powered By</h2>")
              .Append("<div class=\"powered-grid\">").Append(items).Append("</div></div>");
        }

        private static void AppendMembershipPerksSection(StringBuilder sb, string freeRaw, string extraRaw)
        {
            List<string> free = SplitLines(freeRaw);
            List<string> extra = SplitLines(extraRaw);
            if (free.Count == 0 && extra.Count == 0)
                return;

            sb.Append("<div class=\"content-card\"><h2><i class=\"bi bi-gift\"></i> Membership Perks</h2><div class=\"feature-grid-3\">");
            if (free.Count > 0)
            {
                sb.Append("<div><h3><i class=\"bi bi-check-circle\"></i> Included Free</h3><ul class=\"perks-list\">");
                foreach (string p in free)
                    sb.Append("<li><i class=\"bi bi-check2\"></i>").Append(Html(p)).Append("</li>");
                sb.Append("</ul></div>");
            }
            if (extra.Count > 0)
            {
                sb.Append("<div><h3><i class=\"bi bi-stars\"></i> Community Extras</h3><ul class=\"perks-list\">");
                foreach (string p in extra)
                    sb.Append("<li><i class=\"bi bi-check2\"></i>").Append(Html(p)).Append("</li>");
                sb.Append("</ul></div>");
            }
            sb.Append("</div></div>");
        }

        private static List<string> SplitLines(string raw)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;
            foreach (string line in raw.Split('\n'))
            {
                string trimmed = line.Trim().TrimEnd('\r');
                if (trimmed.Length > 0)
                    result.Add(trimmed);
            }
            return result;
        }

        // Icon-headed, hover-lift feature card (see .feature-card CSS) -
        // each row is either a real yes/no capability (rendered as a
        // colored pill) or a plain descriptive fact with no pill at all
        // (isPill=false), for sections like Region Configuration Options
        // that describe patterns rather than assert Confluence-specific
        // yes/no claims. titleHtml is trusted raw HTML (not escaped) since
        // callers need to embed a status pill in the heading itself.
        private static void AppendIconFeatureCard(StringBuilder sb, string icon, string titleHtml, (string Label, bool IsPill, string Text)[] rows)
        {
            sb.Append("<div class=\"feature-card\"><h3><i class=\"bi bi-").Append(icon).Append("\"></i> ").Append(titleHtml).Append("</h3><ul>");
            foreach ((string label, bool isPill, string text) in rows)
            {
                sb.Append("<li><i class=\"bi bi-check-circle-fill\"></i> ");
                if (isPill)
                    sb.Append("<strong>").Append(Html(label)).Append("</strong> - ").Append(Html(text));
                else
                    sb.Append("<strong>").Append(Html(label)).Append(":</strong> ").Append(Html(text));
                sb.Append("</li>");
            }
            sb.Append("</ul></div>");
        }

        // Grid-wide search - People/Places/Events/Classifieds/Groups.
        // Structurally follows the grid-search reference page the user
        // pasted directly (hero-search/chips/trending/stat-strip), rebuilt
        // in Confluence's own markup/CSS/JS and blue palette - no external
        // fonts or icon-CDN (this connector has to work with no internet
        // egress), so icons are dropped rather than faked with a broken
        // CDN reference. Also the page pointed to by [LoginService]
        // SearchURL, so it needs to render sensibly both in a normal
        // browser and inside a viewer's embedded Search floater. Objects
        // deliberately excluded - neither Confluence nor WhiteCore-Dev
        // (checked before building this) has any real in-world object/
        // content indexing anywhere to query.
        private static readonly Dictionary<string, string> SearchCategories = new Dictionary<string, string>
        {
            { "people", "People" },
            { "places", "Places" },
            { "events", "Events" },
            { "classifieds", "Classifieds" },
            { "groups", "Groups" },
            { "picks", "Picks" }
        };

        // [LoginService] SearchURL points here too - a viewer's own Search
        // floater "Web" tab opens this same URL in its small embedded
        // browser. Chrome is decided per-request (see WriteAdaptivePage/
        // IsViewerRequest) rather than needing a second /websearch route -
        // one canonical URL works for both a normal browser tab and the
        // viewer's embedded panel.
        private void HandleSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
            string selfPath = BasePath + "/search";
            WebSession session = GetSession(request);
            string query = (request.QueryString.Get("q") ?? string.Empty).Trim();
            string category = request.QueryString.Get("cat") ?? "all";
            if (category != "all" && !SearchCategories.ContainsKey(category))
                category = "all";
            // Real per-region maturity ceiling (see ISearchData.SearchPlaces)
            // - only Places is actually filterable by it today: parcels,
            // people, events, classifieds and groups carry no maturity data
            // of their own anywhere in this codebase (confirmed before
            // building this - Events/People/Classifieds have no rating
            // field at all, and Classifieds' protocol-level Flags byte is
            // always written the same fixed value by this connector's own
            // creation form, so it carries no real per-item signal either).
            string mat = request.QueryString.Get("mat") ?? "1";
            int maxAccess = mat == "7" ? 42 : mat == "3" ? 21 : 13;
            string gridName = GetSetting("GridName", m_gridName);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>").Append(Html(gridName)).Append("</h1>");

            sb.Append("<div class=\"subnav\"><a class=\"active\" href=\"").Append(selfPath).Append("\">Search</a>")
              .Append("<a href=\"").Append(BasePath).Append("/landsearch\">Land for Sale</a></div>");

            sb.Append("<div class=\"hero-search-wrap\">");
            sb.Append("<div class=\"tagline\">Search People, Places, Events, Classifieds, Groups &amp; Picks</div>");

            sb.Append("<form method=\"get\" action=\"").Append(selfPath).Append("\" class=\"hero-search\">");
            sb.Append("<div class=\"search-input\">").Append(Icon("search"))
              .Append("<input type=\"text\" name=\"q\" value=\"").Append(Html(query))
              .Append("\" placeholder=\"Search people, places, events, classifieds, groups, picks\" minlength=\"3\"></div>");
            sb.Append("<input type=\"hidden\" name=\"cat\" value=\"").Append(category).Append("\">");
            sb.Append("<select name=\"mat\" title=\"Maturity\">")
              .Append("<option value=\"1\"").Append(mat == "1" ? " selected" : "").Append(">PG</option>")
              .Append("<option value=\"3\"").Append(mat == "3" ? " selected" : "").Append(">Mature</option>")
              .Append("<option value=\"7\"").Append(mat == "7" ? " selected" : "").Append(">Adult</option>")
              .Append("</select>");
            sb.Append("<button class=\"btn\" type=\"submit\">").Append(Icon("search")).Append("Search</button></form>");
            sb.Append(SearchAutocompleteScript);

            sb.Append("<div class=\"chips\">");
            foreach (KeyValuePair<string, string> kv in SearchCategories)
            {
                sb.Append("<button class=\"chip\" type=\"button\" onclick=\"")
                  .Append("var f=document.querySelector('.hero-search');f.querySelector('[name=cat]').value='").Append(kv.Key)
                  .Append("';if(f.querySelector('[name=q]').value)f.submit();else f.querySelector('[name=q]').focus();\">")
                  .Append(Icon(CategoryIcon(kv.Key))).Append(Html(kv.Value)).Append("</button>");
            }
            sb.Append("</div>");

            if (query.Length < 3)
            {
                if (query.Length > 0)
                    sb.Append("<p class=\"error\">Enter at least 3 characters to search.</p>");

                if (m_SearchService != null)
                {
                    List<string> trending = m_SearchService.GetTrendingQueries(8);
                    if (trending.Count > 0)
                    {
                        sb.Append("<div class=\"trending\"><span class=\"trending-label\">").Append(Icon("trend")).Append("Trending</span>");
                        foreach (string t in trending)
                            sb.Append("<a class=\"chip\" href=\"").Append(selfPath).Append("?q=").Append(Uri.EscapeDataString(t)).Append("\">").Append(Html(t)).Append("</a>");
                        sb.Append("</div>");
                    }
                }

                sb.Append("<div class=\"stat-strip\">");
                if (m_UserAccountService != null)
                    AppendSearchStat(sb, "person", m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1").Count, "residents");
                if (m_GridService != null)
                    AppendSearchStat(sb, "globe", m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000).Count, "regions");
                if (m_EventsService != null)
                    AppendSearchStat(sb, "calendar", m_EventsService.GetUpcoming(0, 10000).Count, "upcoming events");
                if (m_UserProfilesService != null)
                    AppendSearchStat(sb, "tag", m_UserProfilesService.GetRecentClassifieds(10000).Count, "classifieds");
                sb.Append("</div>");
                sb.Append("</div>");

                WriteAdaptivePage(request, response, PageTitle("Search"), sb.ToString());
                return;
            }

            sb.Append("</div>");

            int perCategory = category == "all" ? 8 : 40;
            int totalResults = 0;
            StringBuilder resultsSb = new StringBuilder();

            if ((category == "all" || category == "people") && m_UserAccountService != null)
            {
                List<UserAccount> people = m_UserAccountService.GetUserAccounts(UUID.Zero, query);
                if (people.Count > 0)
                {
                    totalResults += people.Count;
                    resultsSb.Append("<h2>People</h2>");
                    foreach (UserAccount p in people.GetRange(0, Math.Min(perCategory, people.Count)))
                    {
                        AppendSearchResultCard(resultsSb, "Person", Html(p.FirstName + " " + p.LastName),
                                string.Empty, string.Empty, BasePath + "/profile?id=" + p.PrincipalID);
                    }
                }
            }

            if ((category == "all" || category == "places") && m_SearchService != null)
            {
                List<LandSearchRecord> places = m_SearchService.SearchPlaces(query, 0, perCategory, maxAccess);
                if (places.Count > 0)
                {
                    totalResults += places.Count;
                    resultsSb.Append("<h2>Places</h2>");
                    foreach (LandSearchRecord place in places)
                    {
                        string meta = place.ForSale ? "For sale - " + place.SalePrice + " " + m_currencySymbol + " (" + place.Area + " m²)" : place.Area + " m²";
                        AppendSearchResultCard(resultsSb, "Place", Html(place.Name), meta, string.Empty, null);
                    }
                }
            }

            if ((category == "all" || category == "events") && m_EventsService != null)
            {
                List<EventItem> events = m_EventsService.SearchEvents(query, 0, perCategory);
                if (events.Count > 0)
                {
                    totalResults += events.Count;
                    resultsSb.Append("<h2>Events</h2>");
                    foreach (EventItem ev in events)
                    {
                        string meta = ev.Location + " &middot; " + ev.EventDate.ToString("yyyy-MM-dd HH:mm") + " UTC";
                        AppendSearchResultCard(resultsSb, "Event", Html(ev.Title), meta, Html(ev.Description), null);
                    }
                }
            }

            if ((category == "all" || category == "classifieds") && m_UserProfilesService != null)
            {
                List<UserClassifiedAdd> classifieds = m_UserProfilesService.SearchClassifieds(query, 0, perCategory);
                if (classifieds.Count > 0)
                {
                    totalResults += classifieds.Count;
                    resultsSb.Append("<h2>Classifieds</h2>");
                    foreach (UserClassifiedAdd ad in classifieds)
                    {
                        AppendSearchResultCard(resultsSb, "Classified", Html(ad.Name), Html(ad.SimName), Html(ad.Description), null);
                    }
                }
            }

            if ((category == "all" || category == "groups") && m_GroupsSearchService != null)
            {
                string requestingAgentId = session != null ? session.PrincipalID.ToString() : UUID.Zero.ToString();
                List<DirGroupsReplyData> groups = m_GroupsSearchService.FindGroups(requestingAgentId, query);
                if (groups.Count > 0)
                {
                    totalResults += groups.Count;
                    resultsSb.Append("<h2>Groups</h2>");
                    foreach (DirGroupsReplyData g in groups.GetRange(0, Math.Min(perCategory, groups.Count)))
                    {
                        AppendSearchResultCard(resultsSb, "Group", Html(g.groupName), g.members + " members", string.Empty, null);
                    }
                }
            }

            // Picks are entirely viewer-managed (no web create/edit page,
            // matching Groups above) - this is how they surface on the web
            // at all: grid-wide keyword search, not a dedicated browse page.
            if ((category == "all" || category == "picks") && m_UserProfilesService != null)
            {
                List<UserProfilePick> picks = m_UserProfilesService.SearchPicks(query, 0, perCategory);
                if (picks.Count > 0)
                {
                    totalResults += picks.Count;
                    resultsSb.Append("<h2>Picks</h2>");
                    foreach (UserProfilePick pick in picks)
                    {
                        AppendSearchResultCard(resultsSb, "Pick", Html(pick.Name), Html(pick.SimName), Html(pick.Desc),
                                BasePath + "/profile?id=" + pick.CreatorId);
                    }
                }
            }

            if (m_SearchService != null)
                m_SearchService.LogSearch(query, category, totalResults);

            sb.Append("<p>").Append(totalResults).Append(totalResults == 1 ? " result for &ldquo;" : " results for &ldquo;")
              .Append(Html(query)).Append("&rdquo;</p>");

            if (totalResults == 0)
                sb.Append("<p>No matches. Try a different or shorter search term.</p>");
            else
                sb.Append(resultsSb);

            WriteAdaptivePage(request, response, PageTitle("Search: ") + Html(query), sb.ToString());
        }

        // Debounced autocomplete against /search/suggest, backed by real
        // logged searches (see GetSuggestions) - own implementation, not
        // copied from any reference, but the same UX shape (debounced,
        // keyboard-navigable, click-outside closes) since that's a
        // well-established pattern, not something worth reinventing.
        private const string SearchAutocompleteScript =
                "<script>(function(){" +
                "var form=document.querySelector('.hero-search');if(!form)return;" +
                "var input=form.querySelector('input[name=q]');if(!input)return;" +
                "var box=document.createElement('div');box.className='ac-box';form.appendChild(box);" +
                "var items=[],active=-1,timer=null,lastQ='';" +
                "function close(){box.style.display='none';active=-1;}" +
                "function choose(v){input.value=v;close();form.submit();}" +
                "function render(){" +
                "if(!items.length){close();return;}" +
                "box.innerHTML='';" +
                "items.forEach(function(t,i){" +
                "var d=document.createElement('div');d.className='ac-item'+(i===active?' active':'');d.textContent=t;" +
                "d.addEventListener('mousedown',function(e){e.preventDefault();choose(t);});" +
                "box.appendChild(d);});" +
                "box.style.display='block';}" +
                "input.addEventListener('input',function(){" +
                "var q=input.value.trim();" +
                "if(q.length<2){close();return;}" +
                "if(q===lastQ)return;lastQ=q;clearTimeout(timer);" +
                "timer=setTimeout(function(){" +
                "fetch('" + BasePath + "/search/suggest?q='+encodeURIComponent(q))" +
                ".then(function(r){return r.json();})" +
                ".then(function(d){items=Array.isArray(d)?d:[];active=-1;render();})" +
                ".catch(function(){close();});" +
                "},180);});" +
                "input.addEventListener('keydown',function(e){" +
                "if(box.style.display==='none'||!items.length)return;" +
                "if(e.key==='ArrowDown'){active=(active+1)%items.length;render();e.preventDefault();}" +
                "else if(e.key==='ArrowUp'){active=(active-1+items.length)%items.length;render();e.preventDefault();}" +
                "else if(e.key==='Enter'&&active>=0){choose(items[active]);e.preventDefault();}" +
                "else if(e.key==='Escape'){close();}});" +
                "document.addEventListener('click',function(e){if(!form.contains(e.target))close();});" +
                "}());</script>";

        private void HandleSearchSuggest(IOSHttpRequest request, IOSHttpResponse response)
        {
            string prefix = request.QueryString.Get("q") ?? string.Empty;
            List<string> suggestions = m_SearchService != null
                    ? m_SearchService.GetSuggestions(prefix, 8)
                    : new List<string>();

            OSDArray arr = new OSDArray();
            foreach (string s in suggestions)
                arr.Add(OSD.FromString(s));

            response.ContentType = "application/json";
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(arr));
        }

        // Small original inline-SVG icon set (not Font Awesome or any other
        // CDN glyph set - this connector has to render with no internet
        // egress, so icons have to ship as literal markup, not a linked
        // font/CDN). Deliberately simple geometric shapes, currentColor so
        // they inherit whatever text color their container already has.
        private static string Icon(string name)
        {
            switch (name)
            {
                case "search":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"><circle cx=\"8.5\" cy=\"8.5\" r=\"5.5\"/><line x1=\"17\" y1=\"17\" x2=\"13\" y2=\"13\"/></svg>";
                case "person":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><circle cx=\"10\" cy=\"6\" r=\"4\"/><path d=\"M2 18c0-4.4 3.6-7 8-7s8 2.6 8 7z\"/></svg>";
                case "place":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><path d=\"M10 1c-3.9 0-7 3.1-7 7 0 5.3 7 11 7 11s7-5.7 7-11c0-3.9-3.1-7-7-7z\"/><circle cx=\"10\" cy=\"8\" r=\"2.6\" fill=\"var(--input-bg)\"/></svg>";
                case "calendar":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\"><rect x=\"2\" y=\"4\" width=\"16\" height=\"14\" rx=\"1.5\"/><line x1=\"2\" y1=\"8\" x2=\"18\" y2=\"8\"/><line x1=\"6\" y1=\"2\" x2=\"6\" y2=\"5\"/><line x1=\"14\" y1=\"2\" x2=\"14\" y2=\"5\"/></svg>";
                case "tag":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><path d=\"M2 3h8l8 8-8 8-8-8z\"/><circle cx=\"6\" cy=\"7\" r=\"1.4\" fill=\"var(--input-bg)\"/></svg>";
                case "group":
                    return "<svg class=\"ico\" viewBox=\"0 0 24 20\" fill=\"currentColor\"><circle cx=\"8\" cy=\"6\" r=\"3.4\"/><circle cx=\"17\" cy=\"7\" r=\"2.8\"/><path d=\"M1 19c0-3.8 3.1-6.2 7-6.2s7 2.4 7 6.2z\"/><path d=\"M14.5 19c.3-2.4 1.8-4.4 4-5.4 2.6.6 4.5 2.7 4.5 5.4z\"/></svg>";
                case "star":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><path d=\"M10 1.2l2.6 5.6 6 .7-4.4 4.2 1.1 6-5.3-3-5.3 3 1.1-6-4.4-4.2 6-.7z\"/></svg>";
                case "trend":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><polyline points=\"2,15 8,9 12,12 18,4\"/><polyline points=\"12,4 18,4 18,10\"/></svg>";
                case "globe":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.4\"><circle cx=\"10\" cy=\"10\" r=\"8.5\"/><ellipse cx=\"10\" cy=\"10\" rx=\"3.5\" ry=\"8.5\"/><line x1=\"1.5\" y1=\"10\" x2=\"18.5\" y2=\"10\"/></svg>";
                case "house":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><path d=\"M10 1 1 9h2.2v10h5V13h3.6v6h5V9H19z\"/></svg>";
                case "list":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\"><line x1=\"3\" y1=\"5\" x2=\"17\" y2=\"5\"/><line x1=\"3\" y1=\"10\" x2=\"17\" y2=\"10\"/><line x1=\"3\" y1=\"15\" x2=\"17\" y2=\"15\"/></svg>";
                case "half":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\"><circle cx=\"10\" cy=\"10\" r=\"8.2\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.4\"/><path d=\"M10 1.8a8.2 8.2 0 010 16.4z\" fill=\"currentColor\"/></svg>";
                case "quarter":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\"><circle cx=\"10\" cy=\"10\" r=\"8.2\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.4\"/><path d=\"M10 10V1.8a8.2 8.2 0 018.2 8.2z\" fill=\"currentColor\"/></svg>";
                case "eighth":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\"><circle cx=\"10\" cy=\"10\" r=\"8.2\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.4\"/><path d=\"M10 10V1.8a8.2 8.2 0 014.1 1.1z\" fill=\"currentColor\"/></svg>";
                case "shapes":
                    return "<svg class=\"ico\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><circle cx=\"5\" cy=\"14\" r=\"3.5\"/><rect x=\"11\" y=\"10\" width=\"7\" height=\"7\"/><path d=\"M9 1 5 9h8z\"/></svg>";
                default:
                    return string.Empty;
            }
        }

        private static string CategoryIcon(string category)
        {
            switch (category)
            {
                case "people": return "person";
                case "places": return "place";
                case "events": return "calendar";
                case "classifieds": return "tag";
                case "groups": return "group";
                case "picks": return "star";
                default: return string.Empty;
            }
        }

        private static void AppendSearchStat(StringBuilder sb, string icon, int count, string label)
        {
            sb.Append("<span class=\"stat\">").Append(Icon(icon)).Append("<strong>").Append(count).Append("</strong> ").Append(Html(label)).Append("</span>");
        }

        private static void AppendSearchResultCard(StringBuilder sb, string typeLabel, string title, string meta, string description, string url)
        {
            sb.Append("<div class=\"result-card\"><span class=\"result-badge\">").Append(Html(typeLabel)).Append("</span>");
            if (!string.IsNullOrEmpty(url))
                sb.Append("<h3><a href=\"").Append(url).Append("\">").Append(title).Append("</a></h3>");
            else
                sb.Append("<h3>").Append(title).Append("</h3>");
            if (!string.IsNullOrEmpty(meta))
                sb.Append("<div class=\"result-meta\">").Append(meta).Append("</div>");
            if (!string.IsNullOrEmpty(description))
                sb.Append("<p class=\"result-desc\">").Append(description).Append("</p>");
            sb.Append("</div>");
        }

        // Land for Sale - the existing ISearchService.SearchLandForSale
        // backend (built for /web/search's Places category) was never
        // wired to its own page until now. Bucketed by parcel Area into
        // the same size classes classic SL/OpenSim parcels are normally
        // subdivided into - exact matches for the standard fractions,
        // "Other" catches VarRegion-sized or custom-sized parcels rather
        // than forcing them into a bucket they don't fit. The backend has
        // no area-range filter, so buckets are computed here from one
        // full fetch rather than N separate queries.
        private const int FullRegionSqm = 65536;

        // Account status sentinels stored directly in UserAccount.UserLevel.
        // LLLoginService already rejects any UserLevel below m_MinLoginLevel
        // (default 0) at login, so a negative level blocks login today with
        // no change needed there - these constants just give the two
        // negative values a clear, consistent meaning across the admin UI.
        // BannedUserLevel, the ban-expiry storage, and the clear-if-expired
        // logic live in the shared AccountBanHelper (OpenSim.Services.
        // Interfaces) now, not here - LLLoginService needs the exact same
        // check for the real grid/viewer login path, which didn't have it
        // before (see PROJECT_LOG.md). DeletedUserLevel has no expiry
        // concept and stays local, since only this admin UI needs it.
        private const int DeletedUserLevel = -2;

        private void HandleLandSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
            string bucket = request.QueryString.Get("search") ?? string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Land for Sale</h1>");
            sb.Append("<div class=\"subnav\"><a href=\"").Append(BasePath).Append("/search\">Search</a>")
              .Append("<a class=\"active\" href=\"").Append(BasePath).Append("/landsearch\">Land for Sale</a></div>");
            sb.Append("<p>Parcels and regions ready for immediate ownership. Check each parcel's ")
              .Append("description for specific terms.</p>");

            if (m_SearchService == null)
            {
                sb.Append("<p class=\"error\">Search service is not available right now.</p>");
                WritePage(request, response, PageTitle("Land for Sale"), sb.ToString());
                return;
            }

            List<LandSearchRecord> all = m_SearchService.SearchLandForSale(0, 0, 0, 1000);
            List<LandSearchRecord> full = all.FindAll(r => r.Area == FullRegionSqm);
            List<LandSearchRecord> half = all.FindAll(r => r.Area == FullRegionSqm / 2);
            List<LandSearchRecord> quarter = all.FindAll(r => r.Area == FullRegionSqm / 4);
            List<LandSearchRecord> eighth = all.FindAll(r => r.Area == FullRegionSqm / 8);
            List<LandSearchRecord> small = all.FindAll(r => r.Area > 0 && r.Area < FullRegionSqm / 8);
            List<LandSearchRecord> other = all.FindAll(r =>
                    r.Area != FullRegionSqm && r.Area != FullRegionSqm / 2 && r.Area != FullRegionSqm / 4 &&
                    r.Area != FullRegionSqm / 8 && !(r.Area > 0 && r.Area < FullRegionSqm / 8));

            if (string.IsNullOrEmpty(bucket))
            {
                sb.Append("<div class=\"bucket-grid\">");
                AppendLandBucket(sb, "globe", "Full Regions", "full", full.Count);
                AppendLandBucket(sb, "half", "1/2 Regions", "one2", half.Count);
                AppendLandBucket(sb, "quarter", "1/4 Regions", "one4", quarter.Count);
                AppendLandBucket(sb, "eighth", "1/8 Regions", "one8", eighth.Count);
                AppendLandBucket(sb, "house", "Small Parcels", "small", small.Count);
                AppendLandBucket(sb, "shapes", "Other Sizes", "other", other.Count);
                AppendLandBucket(sb, "list", "All Land for Sale", "all", all.Count);
                sb.Append("</div>");
            }
            else
            {
                List<LandSearchRecord> shown;
                string label;
                switch (bucket)
                {
                    case "full": shown = full; label = "Full Regions"; break;
                    case "one2": shown = half; label = "1/2 Regions"; break;
                    case "one4": shown = quarter; label = "1/4 Regions"; break;
                    case "one8": shown = eighth; label = "1/8 Regions"; break;
                    case "small": shown = small; label = "Small Parcels"; break;
                    case "other": shown = other; label = "Other Sizes"; break;
                    default: shown = all; label = "All Land for Sale"; break;
                }

                sb.Append("<p><a href=\"").Append(BasePath).Append("/landsearch\">&larr; Back to categories</a></p>");
                sb.Append("<h2>").Append(Html(label)).Append(" (").Append(shown.Count).Append(")</h2>");

                if (shown.Count == 0)
                {
                    sb.Append("<p>No parcels currently for sale in this category.</p>");
                }
                else
                {
                    foreach (LandSearchRecord r in shown)
                    {
                        string meta = r.SalePrice + " " + m_currencySymbol + " &middot; " + r.Area + " m&sup2;" + (r.Auction ? " &middot; Auction" : string.Empty);
                        AppendSearchResultCard(sb, "For Sale", Html(r.Name), meta, string.Empty, null);
                    }
                }
            }

            WritePage(request, response, PageTitle("Land for Sale"), sb.ToString());
        }

        // Land auction web-bidding (2026-08-12) - the real viewer has no
        // in-world bidding UI at all (confirmed against Firestorm's real
        // llfloaterauction.h/.cpp: that floater is seller/admin tooling for
        // STARTING an auction, never for bidding - real SL auctions were
        // always bid on through the website). This page IS that website,
        // not just a status display - see AuctionModule's class comment
        // for the full rationale and PROJECT_LOG.md for how this was
        // decided (explicit user direction after checking real viewer
        // source rather than assuming a wire-protocol bid message exists).
        private void HandleAuctions(IOSHttpRequest request, IOSHttpResponse response)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-hammer\"></i> Land Auctions</h1>");
            sb.Append("<p>Bid on parcels put up for auction. Bidding happens here on the web, ")
              .Append("not in the viewer - the same way it always worked in Second Life.</p>");

            if (m_AuctionService == null)
            {
                sb.Append("<p class=\"error\">Auctions are not available right now.</p>");
                WritePage(request, response, PageTitle("Land Auctions"), sb.ToString());
                return;
            }

            List<LandAuction> active = m_AuctionService.GetActive();
            if (active.Count == 0)
            {
                sb.Append("<p>No auctions are currently running.</p>");
            }
            else
            {
                sb.Append("<div class=\"widget-grid\">");
                foreach (LandAuction auction in active)
                {
                    string bidStatus = auction.HighestBid > 0
                            ? "Current bid: " + auction.HighestBid.ToString("N0") + " " + m_currencySymbol
                            : "No bids yet" + (auction.MinBid > 0 ? " - min bid " + auction.MinBid.ToString("N0") + " " + m_currencySymbol : "");

                    sb.Append("<div class=\"widget-card\">");
                    sb.Append("<h3>").Append(Html(auction.ParcelName)).Append("</h3>");
                    sb.Append("<div class=\"widget-meta\">").Append(Html(auction.RegionName)).Append("</div>");
                    sb.Append("<div class=\"widget-meta\">").Append(Html(bidStatus)).Append("</div>");
                    sb.Append("<div class=\"widget-meta\">Ends ").Append(auction.EndsAt.ToString("yyyy-MM-dd HH:mm")).Append(" UTC</div>");
                    sb.Append("<p><a href=\"").Append(BasePath).Append("/auctions/bid?id=").Append(auction.ID)
                      .Append("\">View &amp; Bid &rarr;</a></p>");
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }

            WritePage(request, response, PageTitle("Land Auctions"), sb.ToString());
        }

        private void HandleAuctionBidPage(IOSHttpRequest request, IOSHttpResponse response)
        {
            string idParam = request.QueryString.Get("id");
            if (string.IsNullOrEmpty(idParam) || !UUID.TryParse(idParam, out UUID auctionId) || m_AuctionService == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, PageTitle("Auction"), "<h1>Auction not found</h1>");
                return;
            }

            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string error = null;
            string notice = null;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string amountStr = FormValue(form, "amount");

                if (!int.TryParse(amountStr, out int amount) || amount <= 0)
                {
                    error = "Enter a valid bid amount.";
                }
                else
                {
                    LandAuction current = m_AuctionService.Get(auctionId);
                    if (current == null || current.Status != LandAuctionStatus.Active)
                    {
                        error = "This auction is no longer active.";
                    }
                    else if (amount < current.MinBid)
                    {
                        error = "Your bid must be at least " + current.MinBid.ToString("N0") + " " + m_currencySymbol + ".";
                    }
                    else if (amount <= current.HighestBid)
                    {
                        error = "Someone else already bid " + current.HighestBid.ToString("N0") + " " + m_currencySymbol + " - your bid must be higher.";
                    }
                    else if (m_CurrencyService != null && m_CurrencyService.GetBalance(session.PrincipalID) < amount)
                    {
                        error = "You don't have enough " + m_currencySymbol + " to place this bid.";
                    }
                    // PlaceBid re-checks all of the above atomically against
                    // the database (see IAuctionData.PlaceBid) - the checks
                    // above exist only to give a specific, friendly error
                    // instead of a generic "bid rejected" when the race is
                    // unlikely but not impossible (another bid landing
                    // between the read above and this write).
                    else if (!m_AuctionService.PlaceBid(auctionId, session.PrincipalID, amount))
                    {
                        error = "Your bid wasn't accepted - someone may have just outbid you. Refresh and try again.";
                    }
                    else
                    {
                        notice = "Bid placed! You're the current highest bidder.";
                    }
                }
            }

            LandAuction auction = m_AuctionService.Get(auctionId);
            if (auction == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, PageTitle("Auction"), "<h1>Auction not found</h1>");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<p><a href=\"").Append(BasePath).Append("/auctions\">&larr; All Auctions</a></p>");
            sb.Append("<h1><i class=\"bi bi-hammer\"></i> ").Append(Html(auction.ParcelName)).Append("</h1>");
            sb.Append("<p class=\"news-meta\">").Append(Html(auction.RegionName)).Append("</p>");

            if (!string.IsNullOrEmpty(error))
                sb.Append("<p class=\"error\">").Append(Html(error)).Append("</p>");
            if (!string.IsNullOrEmpty(notice))
                sb.Append("<p class=\"success\">").Append(Html(notice)).Append("</p>");

            if (auction.Status != LandAuctionStatus.Active)
            {
                string outcome = auction.Status == LandAuctionStatus.Ended && auction.WinnerID != UUID.Zero
                        ? "This auction has ended. Winning bid: " + auction.WinningAmount.ToString("N0") + " " + m_currencySymbol + "."
                        : "This auction has ended with no winner.";
                sb.Append("<p>").Append(Html(outcome)).Append("</p>");
            }
            else
            {
                sb.Append("<div class=\"stats-grid\">");
                AppendStat(sb, "Current Bid", auction.HighestBid > 0 ? auction.HighestBid.ToString("N0") + " " + m_currencySymbol : "No bids yet", string.Empty);
                AppendStat(sb, "Minimum Bid", auction.MinBid.ToString("N0") + " " + m_currencySymbol, string.Empty);
                AppendStat(sb, "Ends", auction.EndsAt.ToString("yyyy-MM-dd HH:mm") + " UTC", string.Empty);
                sb.Append("</div>");

                int minNextBid = Math.Max(auction.HighestBid + 1, auction.MinBid);
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/auctions/bid?id=").Append(auction.ID).Append("\">");
                sb.Append("<label>Your bid (").Append(m_currencySymbol).Append(")<br/><input type=\"number\" name=\"amount\" min=\"").Append(minNextBid)
                  .Append("\" value=\"").Append(minNextBid).Append("\" required></label><br/>");
                sb.Append("<button type=\"submit\">Place Bid</button>");
                sb.Append("</form>");
            }

            List<LandAuctionBid> bids = m_AuctionService.GetBidHistory(auction.ID, 20);
            if (bids.Count > 0)
            {
                sb.Append("<h2>Bid History</h2><table><tr><th>Bidder</th><th>Amount</th><th>When</th></tr>");
                foreach (LandAuctionBid bid in bids)
                {
                    string bidderName = bid.BidderID.ToString();
                    UserAccount bidderAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, bid.BidderID);
                    if (bidderAccount != null)
                        bidderName = bidderAccount.Name;

                    sb.Append("<tr><td>").Append(Html(bidderName)).Append("</td><td>")
                      .Append(bid.Amount.ToString("N0")).Append(" ").Append(m_currencySymbol).Append("</td><td>")
                      .Append(bid.BidTime.ToString("yyyy-MM-dd HH:mm")).Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("") + auction.ParcelName, sb.ToString());
        }

        private static void AppendLandBucket(StringBuilder sb, string icon, string label, string param, int count)
        {
            sb.Append("<a class=\"bucket\" href=\"").Append(BasePath).Append("/landsearch?search=").Append(param).Append("\">")
              .Append(Icon(icon))
              .Append("<div class=\"b-name\">").Append(Html(label)).Append("</div>")
              .Append("<div class=\"b-count\">").Append(count).Append(count == 1 ? " parcel available" : " parcels available").Append("</div>")
              .Append("</a>");
        }

        // Support ticket system - the last item in the marketing/legal
        // content tier, matching OpenSim-Grid-Interface's own support.php
        // policy: guests can submit (name+email required, so "I can't log
        // in" issues can still be reported), but ticket history is only
        // visible to the logged-in resident who filed it. A simple
        // honeypot field (a hidden input real users never fill in, that
        // spam bots often do) deters trivial spam without needing a full
        // CAPTCHA - same technique OpenSim-Grid-Interface's own
        // support.php already uses.
        private static readonly Dictionary<string, string> SupportCategories = new Dictionary<string, string>
        {
            { "account", "Account / Login" },
            { "technical", "Technical Issue" },
            { "region", "Region / Land" },
            { "abuse", "Abuse Report" },
            { "other", "Other" }
        };

        private static readonly Dictionary<string, string> SupportStatuses = new Dictionary<string, string>
        {
            { "open", "Open" },
            { "in_progress", "In Progress" },
            { "closed", "Closed" }
        };

        // Public grid-health dashboard (no login) - real counterpart to
        // OpenSim-Grid-Interface's gridstatus.php. The actual
        // protocol-level mechanism third-party grid directories use to
        // discover a grid (name, login URI, economy, etc.) is the stock
        // OpenSim get_grid_info call (confirmed already correctly
        // configured in this deployment's [GridInfoService] section,
        // GridInfoServerInConnector.cs) - this page is the human-facing
        // companion to that, not a replacement for it. Every number here
        // comes from a real service call wrapped in one try/catch, same
        // shape as OGI's own $dbState detection (a live query either
        // succeeds or it doesn't) - "N/A" or omitted rather than a fake
        // zero when a service isn't wired up, matching this page's own
        // established honesty standard elsewhere (see Features page).
        // Each row below used to just check "is this service's reference
        // non-null" - true the moment Robust loads the plugin at startup,
        // regardless of whether it can actually still reach its database
        // right now. A service that's configured but degraded (DB
        // connection lost, table missing) would have kept reporting
        // "Online" forever. Rewritten so every row is its own real, cheap
        // live call in its own try/catch - "Online" now means "answered
        // just now", "Error" (new) means "configured but the live call
        // just failed", "Not configured" still means the reference itself
        // is null. Overall Status is derived from these per-row results,
        // not a single catch-all around the whole block that could mask
        // which specific service actually failed.
        private void HandleGridStatus(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            int totalRegions = 0, varRegions = 0, singleRegions = 0;
            int totalAccounts = 0, newAccounts7d = 0, onlineNow = 0, uniqueVisitors30d = 0;
            long totalAreaSqm = 0;

            HashSet<string> aliveRegionIDs = new HashSet<string>();

            bool gridServiceOk = false, userAccountsOk = false, currencyOk = false, searchOk = false, inventoryOk = false,
                    eventsOk = false, marketplaceOk = false, storeOk = false, friendsOk = false, profilesOk = false;
            int upcomingEventCount = 0, marketplaceListingCount = 0, storeItemCount = 0, activeClassifiedCount = 0;

            if (m_GridService != null)
            {
                try
                {
                    List<GridRegion> aliveRegions = FilterOnlineRegions(
                            m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000));
                    // aliveRegionIDs feeds GetOnlineUserCount below - stays
                    // unfiltered by Unlisted, a resident standing in an
                    // unlisted region still really counts as online. Only
                    // the displayed region/area stats (totalRegions etc.)
                    // respect the opt-out.
                    foreach (GridRegion region in aliveRegions)
                        aliveRegionIDs.Add(region.RegionID.ToString());

                    List<GridRegion> regions = FilterListedRegions(aliveRegions);
                    totalRegions = regions.Count;
                    foreach (GridRegion region in regions)
                    {
                        totalAreaSqm += (long)region.RegionSizeX * region.RegionSizeY;
                        if (region.RegionSizeX == 256 && region.RegionSizeY == 256)
                            singleRegions++;
                        else
                            varRegions++;
                    }
                    gridServiceOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Grid Service check failed: {0}", e);
                }
            }
            if (m_UserAccountService != null)
            {
                try
                {
                    totalAccounts = m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1").Count;
                    long cutoff = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
                    newAccounts7d = m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "Created > " + cutoff).Count;
                    userAccountsOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus User Accounts check failed: {0}", e);
                }
            }
            if (m_GridUserService != null)
            {
                try
                {
                    // A crashed/killed region never clears the "Online"
                    // flag for whoever was on it - see FilterOnlineRegions'
                    // own comment. Only count someone as genuinely online
                    // if the region they were last on is confirmed alive
                    // right now, not just flagged online in the DB.
                    onlineNow = m_GridUserService.GetOnlineUserCount(aliveRegionIDs);
                    uniqueVisitors30d = m_GridUserService.GetUniqueVisitorCount(30);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Grid User check failed: {0}", e);
                }
            }
            if (m_CurrencyService != null)
            {
                try
                {
                    // Cheapest real call available - a zero-width
                    // transaction-history window still round-trips to the
                    // currency DB and back, proving it's actually
                    // reachable rather than just instantiated.
                    m_CurrencyService.GetTransactionHistory(UUID.Zero, UUID.Zero, DateTime.UtcNow, DateTime.UtcNow, null, null);
                    currencyOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Currency check failed: {0}", e);
                }
            }
            if (m_SearchService != null)
            {
                try
                {
                    m_SearchService.GetTrendingQueries(1);
                    searchOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Search check failed: {0}", e);
                }
            }
            if (m_InventoryService != null)
            {
                try
                {
                    // UUID.Zero has no root folder - a clean null return is
                    // just as valid a "the service answered" signal as a
                    // real result, only an exception means it's actually
                    // unreachable.
                    m_InventoryService.GetRootFolder(UUID.Zero);
                    inventoryOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Inventory check failed: {0}", e);
                }
            }
            if (m_EventsService != null)
            {
                try
                {
                    // Same call RenderUpcomingEvents already makes, reused
                    // here for both the health probe and a real stat tile
                    // instead of querying twice.
                    upcomingEventCount = m_EventsService.GetUpcoming(0, 1000).Count;
                    eventsOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Events check failed: {0}", e);
                }
            }
            if (m_MarketplaceListingsService != null)
            {
                try
                {
                    marketplaceListingCount = m_MarketplaceListingsService.GetListedListings(0, 1000).Count;
                    marketplaceOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Marketplace check failed: {0}", e);
                }
            }
            if (m_StoreService != null)
            {
                try
                {
                    storeItemCount = m_StoreService.GetActiveCatalogItems().Count;
                    storeOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Store check failed: {0}", e);
                }
            }
            if (m_FriendsService != null)
            {
                try
                {
                    // UUID.Zero has no friends list - same "empty result is
                    // still proof it answered" reasoning as Inventory above.
                    m_FriendsService.GetFriends(UUID.Zero);
                    friendsOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Friends check failed: {0}", e);
                }
            }
            if (m_UserProfilesService != null)
            {
                try
                {
                    // Same call RenderFeaturedClassifieds already makes.
                    activeClassifiedCount = m_UserProfilesService.GetRecentClassifieds(1000).Count;
                    profilesOk = true;
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[WEBINTERFACE]: HandleGridStatus Profiles check failed: {0}", e);
                }
            }

            bool servicesOk = (m_GridService == null || gridServiceOk)
                    && (m_UserAccountService == null || userAccountsOk)
                    && (m_CurrencyService == null || currencyOk)
                    && (m_SearchService == null || searchOk)
                    && (m_InventoryService == null || inventoryOk)
                    && (m_EventsService == null || eventsOk)
                    && (m_MarketplaceListingsService == null || marketplaceOk)
                    && (m_StoreService == null || storeOk)
                    && (m_FriendsService == null || friendsOk)
                    && (m_UserProfilesService == null || profilesOk);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-activity\"></i> Grid Status</h1>")
              .Append("<p>Live snapshot of ").Append(Html(gridName)).Append("'s statistics and service health - ")
              .Append("every row below is its own real call made just now, not a cached or assumed value. ")
              .Append("Last updated ").Append(Html(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"))).Append(" UTC.</p>");

            sb.Append("<div class=\"stats-grid\">");
            AppendStat(sb, "Online Now", onlineNow.ToString("N0"), "residents");
            AppendStat(sb, "Regions", totalRegions.ToString("N0"), varRegions + " VarRegion, " + singleRegions + " standard");
            AppendStat(sb, "Accounts", totalAccounts.ToString("N0"), "registered residents");
            AppendStat(sb, "Unique Visitors", uniqueVisitors30d.ToString("N0"), "last 30 days, including hypergrid");
            AppendStat(sb, "New Accounts", newAccounts7d.ToString("N0"), "last 7 days");
            AppendStat(sb, "Land Area", (totalAreaSqm / 1000000.0).ToString("N2") + " km" + (char)0xB2, "total across all regions");
            if (m_EventsService != null)
                AppendStat(sb, "Upcoming Events", upcomingEventCount.ToString("N0"), "scheduled");
            if (m_MarketplaceListingsService != null)
                AppendStat(sb, "Marketplace Listings", marketplaceListingCount.ToString("N0"), "listed for sale");
            if (m_StoreService != null)
                AppendStat(sb, "Store Items", storeItemCount.ToString("N0"), "active catalog items");
            if (m_UserProfilesService != null)
                AppendStat(sb, "Active Classifieds", activeClassifiedCount.ToString("N0"), "posted by residents");
            AppendStat(sb, "OpenSimulator", global::OpenSim.VersionInfo.DisplayVersionNumber, "core version");
            sb.Append("</div>");

            sb.Append("<div class=\"content-card\"><h2><i class=\"bi bi-server\"></i> Service Status</h2><table><tbody>")
              .Append("<tr><th>Grid</th><td>").Append(Html(gridName)).Append("</td></tr>")
              .Append("<tr><th>Status</th><td>").Append(servicesOk
                    ? "<span class=\"pill pill-yes\">Operational</span>"
                    : "<span class=\"pill pill-warn\">Degraded</span>").Append("</td></tr>")
              .Append("<tr><th>Grid Service</th><td>").Append(AppendServicePill(m_GridService != null, gridServiceOk)).Append("</td></tr>")
              .Append("<tr><th>User Accounts</th><td>").Append(AppendServicePill(m_UserAccountService != null, userAccountsOk)).Append("</td></tr>")
              .Append("<tr><th>Currency</th><td>").Append(AppendServicePill(m_CurrencyService != null, currencyOk)).Append("</td></tr>")
              .Append("<tr><th>Search</th><td>").Append(AppendServicePill(m_SearchService != null, searchOk)).Append("</td></tr>")
              .Append("<tr><th>Inventory</th><td>").Append(AppendServicePill(m_InventoryService != null, inventoryOk)).Append("</td></tr>")
              .Append("<tr><th>Events</th><td>").Append(AppendServicePill(m_EventsService != null, eventsOk)).Append("</td></tr>")
              .Append("<tr><th>Marketplace</th><td>").Append(AppendServicePill(m_MarketplaceListingsService != null, marketplaceOk)).Append("</td></tr>")
              .Append("<tr><th>Store</th><td>").Append(AppendServicePill(m_StoreService != null, storeOk)).Append("</td></tr>")
              .Append("<tr><th>Friends</th><td>").Append(AppendServicePill(m_FriendsService != null, friendsOk)).Append("</td></tr>")
              .Append("<tr><th>Profiles &amp; Classifieds</th><td>").Append(AppendServicePill(m_UserProfilesService != null, profilesOk)).Append("</td></tr>")
              .Append("</tbody></table></div>");

            sb.Append("<div class=\"content-card text-center\" style=\"text-align:center;padding-top:20px;\">")
              .Append("<p><a href=\"").Append(BasePath).Append("/worldmap\"><i class=\"bi bi-map\"></i> View the World Map</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/search\"><i class=\"bi bi-search\"></i> Search the grid</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/destinations\"><i class=\"bi bi-signpost-2\"></i> Destinations</a></p></div>");

            WritePage(request, response, PageTitle("Status"), sb.ToString());
        }

        private void HandleSupport(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            string flash = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);

                // Honeypot - real users never see or fill this field.
                if (!string.IsNullOrEmpty(FormValue(form, "website")))
                {
                    flash = "<p>Thanks! Your request has been received.</p>";
                }
                else if (m_SupportTicketService == null)
                {
                    flash = "<p class=\"error\">Support ticket service is not available right now.</p>";
                }
                else
                {
                    string category = FormValue(form, "category");
                    if (!SupportCategories.ContainsKey(category))
                        category = "other";
                    string subject = FormValue(form, "subject").Trim();
                    string message = FormValue(form, "message").Trim();
                    string guestName = FormValue(form, "guest_name").Trim();
                    string guestEmail = FormValue(form, "guest_email").Trim();

                    string error = null;
                    if (session == null)
                    {
                        if (string.IsNullOrEmpty(guestName))
                            error = "Please enter your name.";
                        else if (string.IsNullOrEmpty(guestEmail) || !guestEmail.Contains("@"))
                            error = "Please enter a valid email address.";
                    }
                    if (error == null && (string.IsNullOrEmpty(subject) || subject.Length < 3))
                        error = "Please enter a subject (at least 3 characters).";
                    if (error == null && (string.IsNullOrEmpty(message) || message.Length < 10))
                        error = "Please enter a message (at least 10 characters).";

                    if (error != null)
                    {
                        flash = "<p class=\"error\">" + Html(error) + "</p>";
                    }
                    else
                    {
                        SupportTicket ticket = new SupportTicket
                        {
                            ID = UUID.Random(),
                            UserId = session?.PrincipalID ?? UUID.Zero,
                            UserName = session != null ? session.Name : guestName,
                            ContactEmail = session != null ? string.Empty : guestEmail,
                            Category = category,
                            Subject = subject,
                            Message = message,
                            Status = "open",
                            Created = DateTime.UtcNow,
                            Updated = DateTime.UtcNow
                        };
                        m_SupportTicketService.Store(ticket);

                        flash = session != null
                                ? "<p>Your ticket has been submitted. We'll follow up here or in-world.</p>"
                                : "<p>Your ticket has been submitted. We'll reply to " + Html(guestEmail) + ".</p>";
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Support</h1><p>Open a ticket for account, region, or website issues.</p>");
            sb.Append(flash);

            sb.Append("<h2>Open a Ticket</h2>");
            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/support\">");
            sb.Append("<input type=\"text\" name=\"website\" value=\"\" style=\"display:none\" tabindex=\"-1\" autocomplete=\"off\">");
            if (session == null)
            {
                sb.Append("<label>Your name<br/><input type=\"text\" name=\"guest_name\" required></label><br/>");
                sb.Append("<label>Email<br/><input type=\"email\" name=\"guest_email\" required></label><br/>");
            }
            sb.Append("<label>Category<br/><select name=\"category\">");
            foreach (KeyValuePair<string, string> kv in SupportCategories)
                sb.Append("<option value=\"").Append(kv.Key).Append("\">").Append(Html(kv.Value)).Append("</option>");
            sb.Append("</select></label><br/>");
            sb.Append("<label>Subject<br/><input type=\"text\" name=\"subject\" maxlength=\"150\" required></label><br/>");
            sb.Append("<label>Message<br/><textarea name=\"message\" rows=\"5\" required></textarea></label><br/>");
            sb.Append("<button type=\"submit\">Submit ticket</button>");
            sb.Append("</form>");

            if (session != null && m_SupportTicketService != null)
            {
                List<SupportTicket> mine = m_SupportTicketService.GetByUser(session.PrincipalID, 0, 50);
                sb.Append("<h2>Your Recent Tickets</h2>");
                if (mine.Count == 0)
                {
                    sb.Append("<p>You haven't submitted any tickets yet.</p>");
                }
                else
                {
                    sb.Append("<table><tr><th>Subject</th><th>Category</th><th>Status</th><th>Updated</th></tr>");
                    foreach (SupportTicket t in mine)
                    {
                        sb.Append("<tr><td>").Append(Html(t.Subject)).Append("</td>")
                          .Append("<td>").Append(Html(SupportCategories.TryGetValue(t.Category, out string catLabel) ? catLabel : t.Category)).Append("</td>")
                          .Append("<td>").Append(Html(SupportStatuses.TryGetValue(t.Status, out string statLabel) ? statLabel : t.Status)).Append("</td>")
                          .Append("<td>").Append(Html(t.Updated.ToString("yyyy-MM-dd"))).Append("</td></tr>");
                    }
                    sb.Append("</table>");
                }
            }
            else
            {
                sb.Append("<p class=\"news-meta\">Log in to see your ticket history.</p>");
            }

            WritePage(request, response, PageTitle("Support"), sb.ToString());
        }

        private void HandleAdminSupport(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Support Queue"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_SupportTicketService == null)
            {
                WritePage(request, response, PageTitle("Support Queue"),
                        "<h1>Support Queue</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Support ticket service is not available.</p>");
                return;
            }

            List<SupportTicket> tickets = m_SupportTicketService.GetAll(0, 100);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Support Queue</h1><p><a href=\"").Append(BasePath).Append("/admin\">Back to admin</a></p>");

            if (tickets.Count == 0)
            {
                sb.Append("<p>No support tickets on file.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Submitted</th><th>From</th><th>Category</th><th>Subject</th><th>Message</th><th>Status</th></tr>");
                foreach (SupportTicket t in tickets)
                {
                    string from = t.UserId != UUID.Zero ? t.UserName : t.UserName + " (guest, " + t.ContactEmail + ")";
                    sb.Append("<tr><td>").Append(Html(t.Created.ToString("yyyy-MM-dd HH:mm"))).Append("</td>")
                      .Append("<td>").Append(Html(from)).Append("</td>")
                      .Append("<td>").Append(Html(SupportCategories.TryGetValue(t.Category, out string catLabel) ? catLabel : t.Category)).Append("</td>")
                      .Append("<td>").Append(Html(t.Subject)).Append("</td>")
                      .Append("<td>").Append(Html(t.Message.Length > 200 ? t.Message.Substring(0, 200) + "..." : t.Message)).Append("</td>")
                      .Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/support/status\">")
                      .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(t.ID).Append("\">")
                      .Append("<select name=\"status\">");
                    foreach (KeyValuePair<string, string> kv in SupportStatuses)
                        sb.Append("<option value=\"").Append(kv.Key).Append("\"").Append(kv.Key == t.Status ? " selected" : string.Empty).Append(">").Append(Html(kv.Value)).Append("</option>");
                    sb.Append("</select> <button type=\"submit\">Update</button></form></td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Support Queue"), sb.ToString());
        }

        private void HandleAdminSupportStatus(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_SupportTicketService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (UUID.TryParse(FormValue(form, "id"), out UUID id))
            {
                SupportTicket ticket = m_SupportTicketService.Get(id);
                string status = FormValue(form, "status");
                if (ticket != null && SupportStatuses.ContainsKey(status))
                {
                    ticket.Status = status;
                    ticket.Updated = DateTime.UtcNow;
                    m_SupportTicketService.Store(ticket);
                }
            }

            response.Redirect(BasePath + "/admin/support", HttpStatusCode.Redirect);
        }

        // Classic OpenSim/SL classifieds categories (0-8) - same numbering
        // the viewer itself uses in the Profile > Classifieds editor, so
        // these labels have to match what a user picked there, not
        // anything this connector invents.
        // Index 0 is deliberately unused/reserved - the real SL/OpenSim
        // classified-category protocol is 1-indexed (1=Shopping ... 9=Personal;
        // 0 is "Any Category", a search-filter-only value, never a real
        // classified's own category - confirmed against the viewer's own
        // panel_dir_classified.xml combo_item values). This array previously
        // started "Shopping" at index 0 with no adjustment anywhere in the
        // save path (a raw int.TryParse straight into ad.Category), so every
        // classified posted through this form was stored one category off
        // from whatever was actually selected - e.g. selecting "Special
        // Attraction" (this array's old index 3) stored category 3, which
        // the real protocol reads as "Property Rental". Keeping this array's
        // index aligned with the real protocol value fixes it at the source
        // instead of needing an offset at every read/write site.
        private static readonly string[] ClassifiedCategories =
        {
            string.Empty, "Shopping", "Land Rental", "Property Rental", "Special Attraction",
            "New Products", "Employment", "Wanted", "Service", "Personal"
        };

        // "Featured Classifieds" splash widget - matches the pattern shown
        // on competing grids' own splash screens (3rd Rock Grid, DigiWorldz)
        // per the user's explicit "I'm competing with these grids for
        // users." Surfaces real, already-existing user-generated content:
        // classifieds are a stock OpenSim feature (viewer Profile tab,
        // UserProfileModule/IUserProfilesService), not something built for
        // this widget - GetRecentClassifieds just reads the grid-wide feed.
        // No snapshot images for now (would need the asset server's HTTP
        // texture-fetch endpoint, a separate can of worms); text listing
        // only, same information density as the reference screenshots
        // minus the thumbnail.
        private string RenderFeaturedClassifieds(int count)
        {
            if (m_UserProfilesService == null)
                return string.Empty;

            List<UserClassifiedAdd> ads = m_UserProfilesService.GetRecentClassifieds(count);
            if (ads == null || ads.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>Featured Classifieds</h2><div class=\"widget-grid\">");
            foreach (UserClassifiedAdd ad in ads)
            {
                string category = ad.Category >= 0 && ad.Category < ClassifiedCategories.Length
                        ? ClassifiedCategories[ad.Category]
                        : "Misc";
                sb.Append("<div class=\"widget-card\">");
                if (ad.SnapshotId != UUID.Zero)
                    // /CAPS/GetTexture is registered directly by
                    // GetTextureServerConnector at the server root - a
                    // different connector from this one, not under
                    // BasePath (WebInterfaceServiceConnector's own route
                    // prefix, currently "" but not the same concept).
                    sb.Append("<img class=\"widget-card-thumb\" loading=\"lazy\" alt=\"\" src=\"/CAPS/GetTexture?texture_id=")
                      .Append(ad.SnapshotId).Append("&amp;format=jpeg\">");
                sb.Append("<h3>").Append(Html(ad.Name)).Append("</h3>");
                sb.Append("<div class=\"widget-meta\">").Append(Html(category));
                if (!string.IsNullOrEmpty(ad.SimName))
                    sb.Append(" &middot; ").Append(Html(ad.SimName));
                if (ad.Price > 0)
                    sb.Append(" &middot; ").Append(m_currencySymbol).Append(" ").Append(ad.Price);
                sb.Append("</div>");
                string description = ad.Description ?? string.Empty;
                if (description.Length > 140)
                    description = description.Substring(0, 140) + "...";
                sb.Append("<p>").Append(Html(description)).Append("</p></div>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        // Real economy activity on the splash, not just a login form -
        // added after the user pointed at competing grids' own splash
        // screens (3rd Rock Grid, DigiWorldz) that show exactly this kind
        // of "L$/D$ Economy" box, with the explicit framing "I'm competing
        // with these grids for users." Reuses the existing CurrencyService
        // ledger (task #20's own transactions page already calls
        // GetTransactionHistory(UUID.Zero, UUID.Zero, ...) for a grid-wide
        // feed the same way) rather than adding a new aggregate SQL path -
        // pulling the full row set for a 24h/7d/30d window and summing
        // client-side is fine at Confluence's current scale and matches the
        // precedent already set by that page; a very high-volume grid
        // would eventually want a dedicated SUM/COUNT query instead.
        private string RenderEconomyStats()
        {
            if (m_CurrencyService == null)
                return string.Empty;

            DateTime now = DateTime.UtcNow;
            int volume24h, count24h, volume7d, count7d, volume30d, count30d;
            SumTransactions(now.AddHours(-24), now, out volume24h, out count24h);
            SumTransactions(now.AddDays(-7), now, out volume7d, out count7d);
            SumTransactions(now.AddDays(-30), now, out volume30d, out count30d);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>Confluence Economy</h2><div class=\"stats-grid\">");
            AppendStat(sb, "Last 24 Hours", m_currencySymbol + " " + volume24h.ToString("N0"), count24h + " transactions");
            AppendStat(sb, "Last 7 Days", m_currencySymbol + " " + volume7d.ToString("N0"), count7d + " transactions");
            AppendStat(sb, "Last 30 Days", m_currencySymbol + " " + volume30d.ToString("N0"), count30d + " transactions");
            sb.Append("</div>");
            return sb.ToString();
        }

        private void SumTransactions(DateTime start, DateTime end, out int volume, out int count)
        {
            List<CurrencyTransfer> transfers = m_CurrencyService.GetTransactionHistory(UUID.Zero, UUID.Zero, start, end, null, null);
            volume = 0;
            foreach (CurrencyTransfer t in transfers)
                volume += t.Amount;
            count = transfers.Count;
        }

        // The `regions` table's own online/offline state (flags, last_seen)
        // only ever updates on a clean RegisterRegion/DeregisterRegion
        // call - there is no periodic heartbeat anywhere in this codebase.
        // A region that crashes or is killed (confirmed live: hard-killed
        // for a WebUI restart cycle) leaves its "online" flag stuck
        // forever, since nothing ever runs the deregister path for it -
        // found live when Grid Status kept reporting all 14 regions and 2
        // residents online with every single region process actually
        // stopped. Genuine liveness needs an actual live check, not the
        // DB's last-known state, so this probes each region's own HTTP
        // port directly (a raw TCP connect, not a full request/response -
        // the process either has its listener bound or it doesn't, which
        // is exactly the signal wanted here) with a short timeout, all
        // regions checked in parallel so total latency is one timeout
        // period, not (timeout x region count).
        private static List<GridRegion> FilterOnlineRegions(List<GridRegion> regions, int timeoutMs = 1500)
        {
            if (regions == null || regions.Count == 0)
                return new List<GridRegion>();

            System.Threading.Tasks.Task<bool>[] probes = new System.Threading.Tasks.Task<bool>[regions.Count];
            for (int i = 0; i < regions.Count; i++)
            {
                // Capture a fresh local copy of the loop index - a `for`
                // loop's variable is a single shared slot across every
                // iteration (unlike `foreach`, which gets a new one each
                // time), so the lambda below would otherwise close over
                // whatever `i` happens to be by the time it actually runs
                // on a thread pool thread, not the value at the point
                // Task.Run was called. Found live: every single probe threw
                // "Index was out of range" because by the time any of them
                // executed, the loop had already finished and `i` equaled
                // regions.Count.
                int idx = i;
                probes[idx] = System.Threading.Tasks.Task.Run(() => IsRegionAlive(regions[idx], timeoutMs));
            }

            System.Threading.Tasks.Task.WaitAll(probes, timeoutMs + 1000);

            List<GridRegion> online = new List<GridRegion>();
            for (int i = 0; i < regions.Count; i++)
            {
                if (probes[i].IsCompletedSuccessfully && probes[i].Result)
                    online.Add(regions[i]);
            }
            return online;
        }

        private static bool IsRegionAlive(GridRegion region, int timeoutMs)
        {
            return Util.IsHostAlive(region.ServerURI, timeoutMs);
        }

        // Applied on top of FilterOnlineRegions at every PUBLIC-facing call
        // site (home, welcome.php, world map, features, grid status) - an
        // owner's RegionFlags.Unlisted opt-out (see HandleAdminEstates'
        // region checkboxes) means excluded from public listings and stat
        // counts, not excluded from the grid itself (still fully reachable
        // by name/direct link). Deliberately NOT folded into
        // FilterOnlineRegions itself - Region Management (admin) and any
        // other admin-facing region list needs to keep showing everything,
        // unlisted included.
        private List<GridRegion> FilterListedRegions(List<GridRegion> regions)
        {
            if (m_GridService == null || regions.Count == 0)
                return regions;
            return regions.Where(r => (m_GridService.GetRegionFlags(UUID.Zero, r.RegionID) & (int)OpenSim.Framework.RegionFlags.Unlisted) == 0).ToList();
        }

        private static void AppendStat(StringBuilder sb, string label, string value, string sub)
        {
            sb.Append("<div class=\"stat-card\"><div class=\"stat-label\">").Append(Html(label)).Append("</div>")
              .Append("<div class=\"stat-value\">").Append(Html(value)).Append("</div>")
              .Append("<div class=\"stat-sub\">").Append(Html(sub)).Append("</div></div>");
        }

        // Three real states, not two - a service whose reference is null
        // was never configured; one that's configured but whose live probe
        // just threw is genuinely broken right now (distinct from either
        // "fine" or "not applicable to this grid"), so it gets its own
        // pill color rather than being lumped in with "Not configured".
        private static string AppendServicePill(bool configured, bool healthy)
        {
            if (!configured)
                return "<span class=\"pill pill-no\">Not configured</span>";
            return healthy
                    ? "<span class=\"pill pill-yes\">Online</span>"
                    : "<span class=\"pill pill-warn\">Error</span>";
        }

        private string RenderNewsFeed(int count)
        {
            if (m_NewsService == null)
                return string.Empty;

            List<NewsItem> items = m_NewsService.GetNews(0, count);
            if (items.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>News</h2>");
            foreach (NewsItem item in items)
            {
                sb.Append("<div class=\"news-item\"><h3>").Append(Html(item.Title)).Append("</h3>");
                sb.Append("<p class=\"news-meta\">").Append(Html(item.Date.ToString("yyyy-MM-dd")));
                if (!string.IsNullOrEmpty(item.Author))
                    sb.Append(" - ").Append(Html(item.Author));
                sb.Append("</p>");
                sb.Append("<p>").Append(Html(item.Body).Replace("\n", "<br/>")).Append("</p></div>");
            }

            return sb.ToString();
        }

        // Real landing page, not a link list - matches the 3RD Rock Grid
        // Panel reference the user pointed at directly: a 4-card stat row
        // (My Avatars/My Regions/My Estates/My Events), an Account
        // Information card, a Recent Activity audit log, and a Quick Links
        // card. Balance/Friends moved from the stat row into Account
        // Information (see the reference's own layout) rather than being
        // dropped - My Estates (m_EstateDataService.GetEstatesByOwner) is a
        // real, distinct count from My Regions, confirmed by reading
        // GetRegionsOwnedBy's own implementation below. No separate
        // "portal email" - the avatar you register/log in with IS the
        // master account (see AutoProvisionWebAccount), so there's only
        // ever one real email to show.
        // Scoped to just this page - matches the reference control panel's
        // multi-card dashboard grid (separate elevated cards per section,
        // icon-led stats) rather than the shared single-.card/h2-separated
        // layout every other page here uses. ".card{...}" neutralizes the
        // outer wrapper WritePage always adds so it doesn't show up as a
        // second border around everything; the dash-* classes below supply
        // their own cards instead.
        private const string DashboardCss =
                "<style>" +
                ".card{background:transparent;border:none;box-shadow:none;padding:0;}" +
                ".dash-head{display:flex;align-items:center;gap:10px;margin:0 0 4px;}" +
                ".dash-head .bi{font-size:1.5em;color:var(--accent-bright);}" +
                ".dash-head h1{margin:0;}" +
                ".dash-sub{color:var(--muted);font-size:13.5px;margin:0 0 24px;}" +
                ".dash-stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:16px;margin:0 0 20px;}" +
                ".dash-stat{background:var(--card-bg);border:1px solid var(--border);border-radius:var(--radius);" +
                "padding:18px 20px;display:flex;align-items:center;gap:14px;box-shadow:0 8px 24px rgba(0,0,0,.35);}" +
                ".dash-stat .bi{font-size:1.9em;}" +
                ".dash-stat-num{font-size:1.6em;font-weight:700;color:var(--text);line-height:1.1;}" +
                ".dash-stat-label{color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.4px;margin-top:2px;}" +
                ".dash-row{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:20px;margin:0 0 20px;align-items:start;}" +
                ".dash-card{background:var(--card-bg);border:1px solid var(--border);border-radius:var(--radius);" +
                "padding:22px 24px;box-shadow:0 8px 24px rgba(0,0,0,.35);}" +
                ".dash-card-head{display:flex;align-items:center;justify-content:space-between;margin:0 0 16px;}" +
                ".dash-card-title{display:flex;align-items:center;gap:8px;font-size:15px;font-weight:700;color:var(--text);}" +
                ".dash-card-title .bi{color:var(--accent-bright);}" +
                ".dash-count-pill{background:var(--input-bg);color:var(--muted);border-radius:999px;" +
                "padding:2px 11px;font-size:12px;font-weight:700;}" +
                ".dash-info-row{display:flex;justify-content:space-between;align-items:center;gap:10px;" +
                "padding:10px 0;border-bottom:1px solid var(--border);font-size:13.5px;}" +
                ".dash-info-row:last-of-type{border-bottom:none;}" +
                ".dash-info-label{color:var(--muted);flex:0 0 auto;}" +
                ".dash-info-value{color:var(--text);font-weight:600;text-align:right;}" +
                ".dash-card-actions{display:flex;gap:10px;margin-top:16px;}" +
                "a.dash-btn-outline{flex:1;text-align:center;border:2px solid var(--accent);color:var(--accent-bright);" +
                "border-radius:40px;padding:9px 14px;font-size:12.5px;font-weight:700;text-transform:uppercase;" +
                "letter-spacing:.3px;display:inline-block;}" +
                "a.dash-btn-outline:hover{background:var(--accent-tint);text-decoration:none;}" +
                "a.dash-btn-outline.muted{border-color:var(--border);color:var(--muted);}" +
                "a.dash-btn-outline.muted:hover{border-color:var(--text);color:var(--text);}" +
                "a.dash-link-row{display:flex;align-items:center;gap:14px;padding:11px 0;" +
                "border-bottom:1px solid var(--border);color:inherit;}" +
                "a.dash-link-row:last-child{border-bottom:none;}" +
                "a.dash-link-row:hover{text-decoration:none;color:inherit;}" +
                ".dash-link-icon{width:38px;height:38px;border-radius:9px;background:var(--input-bg);" +
                "display:flex;align-items:center;justify-content:center;font-size:1.1em;flex:0 0 auto;}" +
                ".dash-link-title{font-weight:700;font-size:13.5px;color:var(--text);}" +
                ".dash-link-sub{font-size:12px;color:var(--muted);}" +
                ".dash-link-chev{margin-left:auto;color:var(--muted);}" +
                ".dash-avatar-row{display:flex;align-items:center;gap:10px;padding:10px 0;" +
                "border-bottom:1px solid var(--border);font-size:13.5px;}" +
                ".dash-avatar-row:last-child{border-bottom:none;}" +
                ".dash-empty{text-align:center;color:var(--muted);font-size:13px;padding:26px 10px;}" +
                ".dash-empty .bi{font-size:2.2em;display:block;margin:0 0 10px;color:var(--border);}" +
                ".dash-activity-action{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;" +
                "color:var(--accent-bright);font-size:12px;}" +
                "</style>";

        private void HandleDashboard(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            int regionsCount = GetRegionsOwnedBy(session.PrincipalID).Count;
            int estatesCount = m_EstateDataService != null ? m_EstateDataService.GetEstatesByOwner(session.PrincipalID).Count : 0;
            int balance = m_CurrencyService?.GetBalance(session.PrincipalID) ?? 0;
            int eventsCount = m_EventsService != null
                    ? m_EventsService.GetUpcoming(0, 100).Count(e => e.CreatorId == session.PrincipalID)
                    : 0;
            List<WebAccountAvatarLink> linkedAvatars = session.WebAccountID != UUID.Zero && m_WebAccountService != null
                    ? m_WebAccountService.GetLinkedAvatars(session.WebAccountID)
                    : new List<WebAccountAvatarLink>();
            int avatarsCount = linkedAvatars.Count > 0 ? linkedAvatars.Count : 1;

            UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, session.PrincipalID);
            string memberSince = account != null
                    ? Utils.UnixTimeToDateTime((uint)account.Created).ToString("MMM d, yyyy")
                    : "Unknown";

            string lastLogin = null;
            if (m_GridUserService != null)
            {
                GridUserInfo info = m_GridUserService.GetGridUserInfo(session.PrincipalID.ToString());
                if (info != null && info.Login > DateTime.MinValue)
                    lastLogin = info.Login.ToString("MMM d, yyyy h:mm tt") + " UTC";
            }

            // Notification summary - real gap found auditing OpenSim-Grid-
            // Interface's own account shell (its "you have new activity"
            // banner + nav badge counts): nothing here previously surfaced
            // that a resident had unread mail/waiting offline IMs/open
            // tickets short of clicking into each page individually.
            // Deliberately dashboard-only, not persistent sidebar badges
            // like OGI's - OGI's badges live on its page-scoped account
            // shell (only rendered for account/* pages), while Casperia's
            // sidebar renders on every single page site-wide; computing 3
            // live service calls on every page load would be a real
            // performance cost for what's meant to be a lightweight nav.
            // Pending-friend-request count is NOT included - confirmed via
            // IFriendsService that there's no queryable "pending request"
            // concept at all in this codebase (friendships only exist once
            // accepted; requests are an in-world IM handshake that's never
            // persisted anywhere the web portal can read) - a real, deeper
            // gap than this pass's scope, not a data-fetch that was skipped.
            int unreadMessages = m_MessagingService?.GetInbox(session.PrincipalID, 200)?.Count(m => !m.IsRead) ?? 0;
            int offlineWaiting = m_OfflineIMService?.GetMessageCount(session.PrincipalID) ?? 0;
            int openTickets = m_SupportTicketService?.GetByUser(session.PrincipalID, 0, 100)
                    ?.Count(t => t.Status != "closed") ?? 0;

            StringBuilder sb = new StringBuilder(DashboardCss);
            sb.Append("<div class=\"dash-head\"><i class=\"bi bi-speedometer2\"></i><h1>Dashboard</h1></div>");
            sb.Append("<p class=\"dash-sub\">Welcome back, ").Append(Html(session.Name));
            if (!string.IsNullOrEmpty(lastLogin))
                sb.Append(" &mdash; Last login: ").Append(Html(lastLogin));
            sb.Append("</p>");

            if (unreadMessages > 0 || offlineWaiting > 0 || openTickets > 0)
            {
                sb.Append("<div class=\"announcement\"><div style=\"font-weight:700;margin-bottom:6px;\">")
                  .Append("<i class=\"bi bi-bell\"></i> You have new activity:</div><ul style=\"margin:0;padding-left:20px;\">");
                if (unreadMessages > 0)
                    sb.Append("<li><a href=\"").Append(BasePath).Append("/messages\">").Append(unreadMessages)
                      .Append(unreadMessages == 1 ? " unread message" : " unread messages").Append("</a></li>");
                if (offlineWaiting > 0)
                    sb.Append("<li><a href=\"").Append(BasePath).Append("/offline-messages\">").Append(offlineWaiting)
                      .Append(offlineWaiting == 1 ? " offline message waiting" : " offline messages waiting").Append("</a></li>");
                if (openTickets > 0)
                    sb.Append("<li><a href=\"").Append(BasePath).Append("/support\">").Append(openTickets)
                      .Append(openTickets == 1 ? " open support ticket" : " open support tickets").Append("</a></li>");
                sb.Append("</ul></div>");
            }

            sb.Append("<div class=\"dash-stats\">");
            AppendDashStat(sb, "bi-people", "ic-blue", avatarsCount, "My Avatars");
            AppendDashStat(sb, "bi-map", "ic-blue", regionsCount, "My Regions");
            AppendDashStat(sb, "bi-building", "ic-amber", estatesCount, "My Estates");
            AppendDashStat(sb, "bi-calendar-event", "ic-green", eventsCount, "My Events");
            if (m_CurrencyService != null)
                AppendDashStat(sb, "bi-wallet2", "ic-green", balance, "Balance (" + m_currencySymbol + ")");
            sb.Append("</div>");

            sb.Append("<div class=\"dash-row\">");

            // Account Information - the reference's own 4 fields (Username/
            // Email/Role/Member Since). Balance was previously claimed by a
            // stale comment here to have been "relocated" into this card -
            // it never actually was; it's now a real stat card in the row
            // above instead, not fixed by resurrecting the old claim.
            sb.Append("<div class=\"dash-card\"><div class=\"dash-card-head\"><div class=\"dash-card-title\">")
              .Append("<i class=\"bi bi-person-vcard\"></i> Account Information</div></div>");
            AppendDashInfoRow(sb, "Username", Html(session.Name));
            if (account != null && !string.IsNullOrEmpty(account.Email))
                AppendDashInfoRow(sb, "Email", Html(account.Email));
            AppendDashInfoRow(sb, "Role", "<span class=\"pill " + (session.IsAdmin ? "pill-yes\">Administrator" : "pill-no\">Member") + "</span>");
            AppendDashInfoRow(sb, "Member Since", Html(memberSince));
            sb.Append("<div class=\"dash-card-actions\">")
              .Append("<a class=\"dash-btn-outline\" href=\"").Append(BasePath).Append("/profile?id=").Append(session.PrincipalID)
              .Append("\"><i class=\"bi bi-pencil\"></i> Edit Profile</a>")
              .Append("<a class=\"dash-btn-outline muted\" href=\"").Append(BasePath).Append("/change-password\">")
              .Append("<i class=\"bi bi-gear\"></i> Settings</a>")
              .Append("</div></div>");

            // Quick Links - trimmed to the reference's own 5 actions;
            // Casperia's extra dashboard shortcuts (classifieds, transactions,
            // grid search) are still one click away via the sidebar, just no
            // longer competing for space in this specific card.
            sb.Append("<div class=\"dash-card\"><div class=\"dash-card-head\"><div class=\"dash-card-title\">")
              .Append("<i class=\"bi bi-grid-3x3-gap\"></i> Quick Links</div></div>");
            AppendDashLinkRow(sb, BasePath + "/create-avatar", "bi-person-plus", "ic-blue", "Create Avatar", "Register a new avatar on the grid");
            AppendDashLinkRow(sb, BasePath + "/import-avatar", "bi-box-arrow-in-down", "ic-cyan", "Import Avatar", "Link an existing grid avatar");
            AppendDashLinkRow(sb, BasePath + "/myregions", "bi-arrow-clockwise", "ic-amber", "Restart Region", "Restart one of your regions");
            AppendDashLinkRow(sb, BasePath + "/myevents", "bi-calendar-plus", "ic-green", "Post an Event", "Add an event to the grid calendar");
            AppendDashLinkRow(sb, BasePath + "/support", "bi-headset", "ic-pink", "Submit Support Ticket", "Get help from our team");
            sb.Append("</div>");

            // My Avatars - the same linked-avatar list the sidebar switcher
            // and /my-avatars use, just the compact dashboard-card form of it.
            sb.Append("<div class=\"dash-card\"><div class=\"dash-card-head\"><div class=\"dash-card-title\">")
              .Append("<i class=\"bi bi-people\"></i> My Avatars</div>")
              .Append("<a class=\"dash-btn-outline\" style=\"flex:none;padding:6px 16px;\" href=\"")
              .Append(BasePath).Append("/my-avatars\">View All</a></div>");
            if (linkedAvatars.Count == 0)
            {
                sb.Append("<div class=\"dash-avatar-row\"><i class=\"bi bi-person-circle\"></i> ")
                  .Append(Html(session.Name)).Append("<span class=\"pill pill-yes\" style=\"margin-left:auto;\">Active</span></div>");
            }
            else
            {
                foreach (WebAccountAvatarLink link in linkedAvatars.Take(5))
                {
                    UserAccount linkedAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, link.AvatarPrincipalID);
                    string linkedName = linkedAccount != null ? linkedAccount.Name : link.AvatarPrincipalID.ToString();
                    bool isActive = link.AvatarPrincipalID == session.PrincipalID;
                    sb.Append("<div class=\"dash-avatar-row\"><i class=\"bi bi-person-circle\"></i> ").Append(Html(linkedName));
                    if (isActive)
                        sb.Append("<span class=\"pill pill-yes\" style=\"margin-left:auto;\">Active</span>");
                    sb.Append("</div>");
                }
            }
            sb.Append("</div>");

            sb.Append("</div>"); // .dash-row

            // Online Friends - same online-check HandleFriends already uses
            // (GridUserInfo.Online), just summarized to a short list here.
            sb.Append("<div class=\"dash-card\" style=\"margin:0 0 20px;\"><div class=\"dash-card-head\"><div class=\"dash-card-title\">")
              .Append("<i class=\"bi bi-people-fill\"></i> Online Friends</div>");
            List<(string Name, UUID Id)> onlineFriends = new List<(string, UUID)>();
            if (m_FriendsService != null && m_GridUserService != null)
            {
                OpenSim.Services.Interfaces.FriendInfo[] friends = m_FriendsService.GetFriends(session.PrincipalID);
                if (friends != null)
                {
                    foreach (OpenSim.Services.Interfaces.FriendInfo friend in friends)
                    {
                        if (!UUID.TryParse(friend.Friend, out UUID friendId))
                            continue;
                        GridUserInfo info = m_GridUserService.GetGridUserInfo(friendId.ToString());
                        if (info == null || !info.Online)
                            continue;
                        UserAccount friendAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, friendId);
                        onlineFriends.Add((friendAccount != null ? friendAccount.Name : friendId.ToString(), friendId));
                    }
                }
            }
            sb.Append("<span class=\"dash-count-pill\">").Append(onlineFriends.Count).Append("</span></div>");
            if (onlineFriends.Count == 0)
            {
                sb.Append("<div class=\"dash-empty\"><i class=\"bi bi-person\"></i>You don't have any friends online right now.</div>");
            }
            else
            {
                foreach ((string friendName, UUID friendId) in onlineFriends)
                {
                    sb.Append("<div class=\"dash-avatar-row\"><i class=\"bi bi-person-circle ic-green\"></i> <a href=\"")
                      .Append(BasePath).Append("/profile?id=").Append(friendId).Append("\">").Append(Html(friendName)).Append("</a></div>");
                }
            }
            sb.Append("</div>");

            // Recent Activity - shows the raw EventType (not a humanized
            // label) styled like a log/code token, matching the reference's
            // own treatment, since these are meant to read as an audit
            // trail rather than a friendly narrative.
            sb.Append("<div class=\"dash-card\"><div class=\"dash-card-head\"><div class=\"dash-card-title\">")
              .Append("<i class=\"bi bi-clock-history\"></i> Recent Activity</div></div>");
            if (session.WebAccountID == UUID.Zero || m_WebAccountService == null)
            {
                sb.Append("<div class=\"dash-empty\">Activity tracking starts once you've added an email to your account.</div>");
            }
            else
            {
                List<WebActivityEntry> activity = m_WebAccountService.GetRecentActivity(session.WebAccountID, 10);
                if (activity.Count == 0)
                {
                    sb.Append("<div class=\"dash-empty\">No activity recorded yet.</div>");
                }
                else
                {
                    sb.Append("<table><tr><th>Action</th><th>Description</th><th>IP Address</th><th>Date &amp; Time</th></tr>");
                    foreach (WebActivityEntry entry in activity)
                    {
                        sb.Append("<tr><td><span class=\"dash-activity-action\">").Append(Html(entry.EventType)).Append("</span></td>")
                          .Append("<td>").Append(Html(entry.Description)).Append("</td>")
                          .Append("<td>").Append(Html(entry.IPAddress)).Append("</td>")
                          .Append("<td>").Append(Html(entry.Created.ToString("MMM d, yyyy h:mm tt"))).Append(" UTC</td></tr>");
                    }
                    sb.Append("</table>");
                }
            }
            sb.Append("</div>");

            WritePage(request, response, PageTitle("Dashboard"), sb.ToString());
        }

        private static void AppendDashStat(StringBuilder sb, string icon, string colorClass, int value, string label)
        {
            sb.Append("<div class=\"dash-stat\"><i class=\"bi ").Append(icon).Append(' ').Append(colorClass).Append("\"></i><div>")
              .Append("<div class=\"dash-stat-num\">").Append(value.ToString("N0")).Append("</div>")
              .Append("<div class=\"dash-stat-label\">").Append(Html(label)).Append("</div></div></div>");
        }

        private static void AppendDashInfoRow(StringBuilder sb, string label, string valueHtml)
        {
            sb.Append("<div class=\"dash-info-row\"><span class=\"dash-info-label\">").Append(Html(label))
              .Append("</span><span class=\"dash-info-value\">").Append(valueHtml).Append("</span></div>");
        }

        private static void AppendDashLinkRow(StringBuilder sb, string href, string icon, string colorClass, string title, string subtitle)
        {
            sb.Append("<a class=\"dash-link-row\" href=\"").Append(href).Append("\">")
              .Append("<div class=\"dash-link-icon\"><i class=\"bi ").Append(icon).Append(' ').Append(colorClass).Append("\"></i></div>")
              .Append("<div><div class=\"dash-link-title\">").Append(Html(title)).Append("</div>")
              .Append("<div class=\"dash-link-sub\">").Append(Html(subtitle)).Append("</div></div>")
              .Append("<i class=\"bi bi-chevron-right dash-link-chev\"></i></a>");
        }

        private static void AppendDashboardLink(StringBuilder sb, string href, string icon, string title, string description)
        {
            sb.Append("<a class=\"widget-card dashboard-link\" href=\"").Append(href).Append("\">");
            sb.Append("<h3><i class=\"bi ").Append(icon).Append("\"></i> ").Append(Html(title)).Append("</h3>");
            sb.Append("<div class=\"widget-meta\">").Append(Html(description)).Append("</div>");
            sb.Append("</a>");
        }

        // Region list + per-region Hypergrid open/close toggle - the first
        // admin-only page, and the item PROJECT_LOG.md had marked "In progress"
        // since long before the currency/web-UI architecture thread started.
        private void HandleAdmin(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Admin"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            // Sub-page links used to live in a 13-item "Admin" nav-bar
            // dropdown (see WritePage/RenderSidebar) - the sidebar now only
            // links to this one page for admins, so this grid is the real
            // navigation into every admin sub-page, not just decoration.
            // Same nav-as-cards shape as OpenSim-Grid-Interface's own
            // _account_shell_top.php (icon, label, description card grid).
            StringBuilder adminNav = new StringBuilder();
            adminNav.Append("<h2>Manage</h2><div class=\"widget-grid\">");
            AppendDashboardLink(adminNav, BasePath + "/admin/abuse-reports", "bi-exclamation-triangle", "Abuse Reports", "Review reports filed by residents");
            AppendDashboardLink(adminNav, BasePath + "/admin/users", "bi-people", "User Management", "Search, ban, message and edit accounts");
            AppendDashboardLink(adminNav, BasePath + "/admin/regions", "bi-map", "Region Management", "Search regions, Hypergrid, maptiles, backups, restart, create");
            AppendDashboardLink(adminNav, BasePath + "/admin/estates", "bi-building", "Estate Management", "Edit estate settings and access lists");
            AppendDashboardLink(adminNav, BasePath + "/admin/groups", "bi-people-fill", "Groups Management", "Grid-wide group administration");
            AppendDashboardLink(adminNav, BasePath + "/admin/transactions", "bi-cash-stack", "Purchases & Transactions", "Financial reporting across the grid");
            AppendDashboardLink(adminNav, BasePath + "/admin/stats", "bi-bar-chart", "Grid Statistics", "Accounts, regions and online totals");
            AppendDashboardLink(adminNav, BasePath + "/admin/news", "bi-newspaper", "News Feed", "Post announcements to the splash page");
            AppendDashboardLink(adminNav, BasePath + "/admin/events", "bi-calendar-event", "Events", "Manage the grid-wide events calendar");
            AppendDashboardLink(adminNav, BasePath + "/admin/support", "bi-headset", "Support Queue", "Respond to open support tickets");
            AppendDashboardLink(adminNav, BasePath + "/admin/store", "bi-shop", "Store Catalog", "Manage prim packs and region order listings");
            AppendDashboardLink(adminNav, BasePath + "/admin/store/orders", "bi-receipt-cutoff", "Store Orders", "Fulfillment queue, renewals, Start Region");
            AppendDashboardLink(adminNav, BasePath + "/admin/regions/ini", "bi-file-earmark-code", "Region Config Files", "View/edit any region's raw .ini file");
            AppendDashboardLink(adminNav, BasePath + "/admin/simulators", "bi-play-circle", "Simulators", "Start any region process - only Robust needs to be running for this site itself");
            AppendDashboardLink(adminNav, BasePath + "/admin/pages", "bi-file-earmark-text", "Static Pages", "Edit About/ToS/DMCA and custom pages");
            AppendDashboardLink(adminNav, BasePath + "/admin/settings", "bi-gear", "Grid Settings", "Grid name, welcome message and options");
            AppendDashboardLink(adminNav, BasePath + "/admin/console", "bi-terminal", "Region Console", "Run console commands on a region");
            adminNav.Append("</div>");

            string body = "<h1>Grid Administration</h1>"
                    + message
                    + adminNav.ToString();

            WritePage(request, response, PageTitle("Admin"), body);
        }

        // Region Management - split out of the Grid Administration overview
        // (2026-08-16) into its own page, same as Users/Estates, once the
        // regions table needed a search box and pagination too rather than
        // being a single ever-growing inline table on the admin landing
        // page. Same per-region actions as before (HG toggle, maptile
        // regen, OAR backup, restart), plus name search (blank = every
        // region, same "don't hide behind a required search term" fix as
        // HandleAdminUsers) and pagination.
        private void HandleAdminRegions(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Region Management"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            string query = (request.QueryString.Get("q") ?? string.Empty).Trim();
            string searchForm = "<form method=\"get\" action=\"" + BasePath + "/admin/regions\">"
                    + "<input type=\"text\" name=\"q\" placeholder=\"Search by region name\" value=\"" + Html(query) + "\">"
                    + "<button type=\"submit\">Search</button>"
                    + (query.Length > 0 ? " <a href=\"" + BasePath + "/admin/regions\">Clear</a>" : string.Empty)
                    + "</form>";

            StringBuilder rows = new StringBuilder();
            if (m_GridService == null)
            {
                rows.Append("<p>Grid service is not available.</p>");
            }
            else
            {
                // Wide enough meter range to cover any reasonably-sized grid -
                // IGridService has no direct "get everything" call, this is the
                // standard way to approximate one.
                List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
                if (query.Length > 0)
                    regions = regions.FindAll(r => r.RegionName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                regions.Sort((a, b) => string.Compare(a.RegionName, b.RegionName, StringComparison.OrdinalIgnoreCase));

                const int pageSize = 25;
                int totalPages = Math.Max(1, (int)Math.Ceiling(regions.Count / (double)pageSize));
                int page = 1;
                int.TryParse(request.QueryString.Get("page"), out page);
                page = Math.Max(1, Math.Min(page, totalPages));
                List<GridRegion> pageRegions = regions.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                if (regions.Count == 0)
                {
                    rows.Append("<p>No regions matched that search.</p>");
                }
                else
                {
                    rows.Append("<p class=\"news-meta\">").Append(regions.Count).Append(regions.Count == 1 ? " region" : " regions")
                      .Append(query.Length > 0 ? " matched" : " on this grid").Append("</p>");
                    // Reference's Online column - real gap, this table had no
                    // at-a-glance up/down status at all. Probed once as a
                    // parallel batch over just this page's rows (the same
                    // FilterOnlineRegions helper HandleGridStatus already
                    // uses), not one blocking IsRegionAlive call per row -
                    // sequential probes here could serialize into many
                    // seconds of page-load time on a page full of down regions.
                    HashSet<UUID> onlineRegionIDs = new HashSet<UUID>(
                            FilterOnlineRegions(pageRegions).ConvertAll(r => r.RegionID));
                    rows.Append("<table><tr><th>Region</th><th>Location</th><th>Online</th><th>Hypergrid</th><th></th><th></th><th></th><th></th><th>Group Auto-Invite</th></tr>");
                    foreach (GridRegion region in pageRegions)
                    {
                        bool open = m_RegionHGService == null || m_RegionHGService.IsRegionOpen(region.RegionID);
                        string status = open ? "Open" : "Closed";
                        string actionLabel = open ? "Close to HG" : "Open to HG";

                        // Read-only badge, not a second toggle - the real
                        // Unlisted control already lives on /admin/estates
                        // (CanManageEstate lets an admin manage any estate
                        // there too, not just its owner), this table just
                        // needs to show it's set so an admin isn't confused
                        // about why a region is missing from the public
                        // world map/region tables while still showing up
                        // here.
                        bool isUnlisted = (m_GridService.GetRegionFlags(UUID.Zero, region.RegionID) & (int)OpenSim.Framework.RegionFlags.Unlisted) != 0;
                        rows.Append("<tr><td>").Append(Html(region.RegionName));
                        if (isUnlisted)
                            rows.Append(" <span class=\"pill pill-no\" title=\"Hidden from the public world map, region tables and stat counts - set via Estate Management\">Unlisted</span>");
                        rows.Append("</td>");
                        rows.Append("<td>").Append(region.RegionCoordX).Append(",").Append(region.RegionCoordY).Append("</td>");
                        rows.Append("<td><span class=\"pill ").Append(onlineRegionIDs.Contains(region.RegionID) ? "pill-yes\">Online" : "pill-no\">Offline").Append("</span></td>");
                        rows.Append("<td>").Append(status).Append("</td>");
                        rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/hg-toggle\">");
                        rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                        rows.Append("<input type=\"hidden\" name=\"set_open\" value=\"").Append(open ? "false" : "true").Append("\">");
                        rows.Append("<button type=\"submit\"").Append(m_RegionHGService == null ? " disabled" : "").Append(">").Append(actionLabel).Append("</button>");
                        rows.Append("</form></td>");
                        rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/maptile-regen\">");
                        rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                        rows.Append("<button type=\"submit\">Regenerate maptile</button>");
                        rows.Append("</form></td>");
                        rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/oar-save\">");
                        rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                        rows.Append("<button type=\"submit\">Save OAR backup</button>");
                        rows.Append("</form></td>");
                        rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/regions/restart\" onsubmit=\"return confirm('Restart ")
                                .Append(Html(region.RegionName).Replace("'", "\\'")).Append("? Everyone in the region will be disconnected.');\">");
                        rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                        rows.Append("<button type=\"submit\">Restart</button>");
                        rows.Append("</form></td>");
                        // Live toggle, no restart - runs "group-auto-invite
                        // enable/disable" on the target region via the same
                        // remote-console channel Restart above uses. Off by
                        // default per-region (see OpenSimDefaults.ini); this
                        // is meant for turning it on for a specific sim while
                        // testing, not a permanent setting - a region
                        // restart reverts to whatever's in that region's own
                        // OpenSim.ini.
                        rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/regions/group-auto-invite\">");
                        rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                        rows.Append("<input type=\"text\" name=\"group_id\" placeholder=\"group uuid\" size=\"20\" style=\"font-size:0.85em\"> ");
                        rows.Append("<button type=\"submit\" name=\"action\" value=\"enable\" style=\"font-size:0.85em\"").Append(string.IsNullOrEmpty(m_webConsoleSecret) ? " disabled" : "").Append(">Enable</button> ");
                        rows.Append("<button type=\"submit\" name=\"action\" value=\"disable\" style=\"font-size:0.85em\"").Append(string.IsNullOrEmpty(m_webConsoleSecret) ? " disabled" : "").Append(">Disable</button>");
                        rows.Append("</form></td></tr>");
                    }
                    rows.Append("</table>");

                    if (totalPages > 1)
                    {
                        string qParam = query.Length == 0 ? string.Empty : "&q=" + Uri.EscapeDataString(query);
                        rows.Append("<p class=\"news-meta\">");
                        if (page > 1)
                            rows.Append("<a href=\"").Append(BasePath).Append("/admin/regions?page=").Append(page - 1).Append(qParam).Append("\">&larr; Previous</a> &middot; ");
                        rows.Append("Page ").Append(page).Append(" of ").Append(totalPages);
                        if (page < totalPages)
                            rows.Append(" &middot; <a href=\"").Append(BasePath).Append("/admin/regions?page=").Append(page + 1).Append(qParam).Append("\">Next &rarr;</a>");
                        rows.Append("</p>");
                    }
                }

                if (m_RegionHGService == null)
                    rows.Append("<p class=\"error\">RegionHGService is not configured - toggle is read-only (always shows Open).</p>");
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string body = "<h1>Region Management</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + message
                    + searchForm
                    + rows.ToString();

            WritePage(request, response, PageTitle("Region Management"), body);
        }

        // Grid-wide totals for admins - task #21 from the WhiteCore-Dev
        // re-audit's "all of it" list. Everything here reuses data sources
        // other pages on this connector already established: the
        // GetRegionRange(0,2000000,0,2000000) "get everything" idiom
        // (HandleAdmin), GetUserAccountsWhere for a total account count
        // (same call HandleAdminUsers' search uses, just with a "1=1"
        // catch-all instead of a name), and Hypergrid open/closed status
        // (IRegionHGService, also already used by HandleAdmin). The one
        // genuinely new capability is IGridUserService.GetOnlineUserCount -
        // no existing service method exposed this at all; it previously
        // only existed as a private console-command helper
        // (GridUserService.HandleShowGridUsersOnline), so it was promoted
        // onto the interface itself (and threaded through the Local/Remote
        // connectors and the /griduser HTTP handler, same as every other
        // interface method) rather than reaching past the service layer.
        private void HandleAdminStats(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Statistics"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            StringBuilder rows = new StringBuilder();
            rows.Append("<table>");

            if (m_GridService == null)
            {
                rows.Append("<tr><th>Regions</th><td>Grid service is not available.</td></tr>");
            }
            else
            {
                List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
                long totalAreaSqm = 0;
                int hgOpenCount = 0;
                foreach (GridRegion region in regions)
                {
                    totalAreaSqm += (long)region.RegionSizeX * region.RegionSizeY;
                    if (m_RegionHGService == null || m_RegionHGService.IsRegionOpen(region.RegionID))
                        hgOpenCount++;
                }

                rows.Append("<tr><th>Total regions</th><td>").Append(regions.Count).Append("</td></tr>");
                rows.Append("<tr><th>Total land area</th><td>").Append(totalAreaSqm.ToString("N0")).Append(" m&sup2;</td></tr>");
                rows.Append("<tr><th>Regions open to Hypergrid</th><td>").Append(hgOpenCount).Append(" / ").Append(regions.Count).Append("</td></tr>");
            }

            if (m_UserAccountService == null)
            {
                rows.Append("<tr><th>Registered accounts</th><td>User account service is not available.</td></tr>");
            }
            else
            {
                int totalAccounts = m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1").Count;
                rows.Append("<tr><th>Registered accounts</th><td>").Append(totalAccounts).Append("</td></tr>");
            }

            if (m_GridUserService == null)
            {
                rows.Append("<tr><th>Users online</th><td>Grid user service is not available.</td></tr>");
            }
            else
            {
                int online = m_GridUserService.GetOnlineUserCount();
                rows.Append("<tr><th>Users online</th><td>").Append(online)
                        .Append(" <span style=\"font-size:0.85em;color:#666\">(accuracy note: a region that crashes without a clean logout can overcount this; entries older than 5 days are excluded)</span></td></tr>");
            }

            rows.Append("</table>");

            string body = "<h1>Grid Statistics</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + rows.ToString();

            WritePage(request, response, PageTitle("Statistics"), body);
        }

        // Login-screen/home-page news feed admin (task #23 from the
        // WhiteCore-Dev re-audit's "all of it" list) - list + create/edit +
        // delete. ?id= switches the list into edit mode for that item, the
        // same URL-param convention HandleAdminAbuseReports/HandleAdminEstates
        // already use on this connector.
        private void HandleAdminNews(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("News"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_NewsService == null)
            {
                WritePage(request, response, PageTitle("News"),
                        "<h1>News Feed</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>News service is not available.</p>");
                return;
            }

            string idParam = request.QueryString.Get("id");
            NewsItem editing = null;
            if (!string.IsNullOrEmpty(idParam) && UUID.TryParse(idParam, out UUID editID))
                editing = m_NewsService.Get(editID);

            StringBuilder rows = new StringBuilder();
            List<NewsItem> items = m_NewsService.GetNews(0, 100);
            rows.Append("<table><tr><th>Date</th><th>Title</th><th>Author</th><th></th><th></th></tr>");
            foreach (NewsItem item in items)
            {
                rows.Append("<tr>");
                rows.Append("<td>").Append(Html(item.Date.ToString("yyyy-MM-dd"))).Append("</td>");
                rows.Append("<td>").Append(Html(item.Title)).Append("</td>");
                rows.Append("<td>").Append(Html(item.Author)).Append("</td>");
                rows.Append("<td><a href=\"").Append(BasePath).Append("/admin/news?id=").Append(item.ID).Append("\">Edit</a></td>");
                rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/news/delete\">");
                rows.Append("<input type=\"hidden\" name=\"id\" value=\"").Append(item.ID).Append("\">");
                rows.Append("<button type=\"submit\">Delete</button></form></td>");
                rows.Append("</tr>");
            }
            rows.Append("</table>");

            if (items.Count == 0)
                rows.Append("<p>No news items yet.</p>");

            string formTitle = editing != null ? "Edit News Item" : "Post News Item";
            string body = "<h1>News Feed</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + rows.ToString()
                    + "<h2>" + formTitle + "</h2>"
                    + "<form method=\"post\" action=\"" + BasePath + "/admin/news/save\">"
                    + "<input type=\"hidden\" name=\"id\" value=\"" + (editing != null ? editing.ID.ToString() : string.Empty) + "\">"
                    + "<label>Title<br/><input type=\"text\" name=\"title\" value=\"" + Html(editing?.Title ?? string.Empty) + "\" required></label><br/>"
                    + "<label>Author<br/><input type=\"text\" name=\"author\" value=\"" + Html(editing?.Author ?? session.Name) + "\"></label><br/>"
                    + "<label>Body<br/><textarea name=\"body\" rows=\"6\" required>" + Html(editing?.Body ?? string.Empty) + "</textarea></label><br/>"
                    + "<button type=\"submit\">" + (editing != null ? "Save changes" : "Post") + "</button>"
                    + (editing != null ? " <a href=\"" + BasePath + "/admin/news\">Cancel</a>" : string.Empty)
                    + "</form>";

            WritePage(request, response, PageTitle("News"), body);
        }

        private void HandleAdminNewsSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_NewsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string idValue = FormValue(form, "id");
            string title = FormValue(form, "title").Trim();
            string author = FormValue(form, "author").Trim();
            string bodyText = FormValue(form, "body");

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(bodyText))
            {
                response.Redirect(BasePath + "/admin/news", HttpStatusCode.Redirect);
                return;
            }

            // Editing an existing item keeps its original post date rather than
            // bumping it to "now" - a correction shouldn't reorder the feed.
            NewsItem item = null;
            if (!string.IsNullOrEmpty(idValue) && UUID.TryParse(idValue, out UUID existingID))
                item = m_NewsService.Get(existingID);

            if (item == null)
                item = new NewsItem { ID = UUID.Random(), Date = DateTime.UtcNow };

            item.Title = title;
            item.Author = author;
            item.Body = bodyText;

            m_NewsService.Store(item);

            response.Redirect(BasePath + "/admin/news", HttpStatusCode.Redirect);
        }

        private void HandleAdminNewsDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_NewsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (UUID.TryParse(FormValue(form, "id"), out UUID id))
                m_NewsService.Delete(id);

            response.Redirect(BasePath + "/admin/news", HttpStatusCode.Redirect);
        }

        // Admin management for the splash page's "Upcoming Events" widget -
        // same list + create/edit + delete shape as HandleAdminNews above,
        // added after the user pointed at competing grids' own splash
        // screens showing an events calendar ("I'm competing with these
        // grids for users"). Deliberately admin-managed only, not a full
        // in-world/viewer-created event system - see EventItem's own
        // class-level comment for why.
        private void HandleAdminEvents(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Events"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_EventsService == null)
            {
                WritePage(request, response, PageTitle("Events"),
                        "<h1>Events</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Events service is not available.</p>");
                return;
            }

            string idParam = request.QueryString.Get("id");
            EventItem editing = null;
            if (!string.IsNullOrEmpty(idParam) && UUID.TryParse(idParam, out UUID editID))
                editing = m_EventsService.Get(editID);

            StringBuilder rows = new StringBuilder();
            List<EventItem> items = m_EventsService.GetUpcoming(0, 100);
            rows.Append("<table><tr><th>Date</th><th>Title</th><th>Category</th><th>Location</th><th></th><th></th></tr>");
            foreach (EventItem item in items)
            {
                rows.Append("<tr>");
                rows.Append("<td>").Append(Html(item.EventDate.ToString("yyyy-MM-dd HH:mm"))).Append(" UTC</td>");
                rows.Append("<td>").Append(Html(item.Title)).Append("</td>");
                rows.Append("<td>").Append(Html(item.Category)).Append("</td>");
                rows.Append("<td>").Append(Html(item.Location)).Append("</td>");
                rows.Append("<td><a href=\"").Append(BasePath).Append("/admin/events?id=").Append(item.ID).Append("\">Edit</a></td>");
                rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/events/delete\">");
                rows.Append("<input type=\"hidden\" name=\"id\" value=\"").Append(item.ID).Append("\">");
                rows.Append("<button type=\"submit\">Delete</button></form></td>");
                rows.Append("</tr>");
            }
            rows.Append("</table>");

            if (items.Count == 0)
                rows.Append("<p>No upcoming events.</p>");

            string dateValue = editing != null ? editing.EventDate.ToString("yyyy-MM-ddTHH:mm") : string.Empty;
            string formTitle = editing != null ? "Edit Event" : "Add Event";

            // Same region <select> as HandleMyEvents' self-service form -
            // feeds GlobalPos in HandleAdminEventsSave.
            List<GridRegion> adminEventRegions = m_GridService?.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000) ?? new List<GridRegion>();
            StringBuilder adminEventRegionOptions = new StringBuilder();
            foreach (GridRegion region in adminEventRegions)
            {
                bool selected = editing != null && editing.Location == region.RegionName;
                adminEventRegionOptions.Append("<option value=\"").Append(Html(region.RegionName)).Append("\"")
                        .Append(selected ? " selected" : string.Empty).Append(">").Append(Html(region.RegionName)).Append("</option>");
            }

            string body = "<h1>Events</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + rows.ToString()
                    + "<h2>" + formTitle + "</h2>"
                    + "<form method=\"post\" action=\"" + BasePath + "/admin/events/save\">"
                    + "<input type=\"hidden\" name=\"id\" value=\"" + (editing != null ? editing.ID.ToString() : string.Empty) + "\">"
                    + "<label>Title<br/><input type=\"text\" name=\"title\" value=\"" + Html(editing?.Title ?? string.Empty) + "\" required></label><br/>"
                    + "<label>Category<br/><input type=\"text\" name=\"category\" value=\"" + Html(editing?.Category ?? string.Empty) + "\" placeholder=\"Live Music, Nightlife, Games...\"></label><br/>"
                    + "<label>Date/time (grid time, UTC)<br/><input type=\"datetime-local\" name=\"event_date\" value=\"" + Html(dateValue) + "\" required></label><br/>"
                    + "<label>Duration (minutes)<br/><input type=\"number\" name=\"duration\" value=\"" + (editing?.DurationMinutes ?? 60) + "\" min=\"0\"></label><br/>"
                    + "<label>Region (for Teleport/Map)<br/><select name=\"region\">" + adminEventRegionOptions + "</select></label><br/>"
                    + "<label>Location<br/><input type=\"text\" name=\"location\" value=\"" + Html(editing?.Location ?? string.Empty) + "\" placeholder=\"Region or venue name\"></label><br/>"
                    + "<label>Description<br/><textarea name=\"description\" rows=\"4\">" + Html(editing?.Description ?? string.Empty) + "</textarea></label><br/>"
                    + "<button type=\"submit\">" + (editing != null ? "Save changes" : "Add event") + "</button>"
                    + (editing != null ? " <a href=\"" + BasePath + "/admin/events\">Cancel</a>" : string.Empty)
                    + "</form>";

            WritePage(request, response, PageTitle("Events"), body);
        }

        private void HandleAdminEventsSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_EventsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string idValue = FormValue(form, "id");
            string title = FormValue(form, "title").Trim();
            string category = FormValue(form, "category").Trim();
            string dateValue = FormValue(form, "event_date").Trim();
            string location = FormValue(form, "location").Trim();
            string regionName = FormValue(form, "region").Trim();
            string description = FormValue(form, "description");
            int.TryParse(FormValue(form, "duration"), out int duration);

            if (string.IsNullOrEmpty(title)
                    || !DateTime.TryParse(dateValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime eventDate))
            {
                response.Redirect(BasePath + "/admin/events", HttpStatusCode.Redirect);
                return;
            }

            EventItem item = null;
            if (!string.IsNullOrEmpty(idValue) && UUID.TryParse(idValue, out UUID existingID))
                item = m_EventsService.Get(existingID);

            if (item == null)
                item = new EventItem { ID = UUID.Random(), CreatorId = session.PrincipalID };

            item.Title = title;
            item.Category = category;
            item.EventDate = DateTime.SpecifyKind(eventDate, DateTimeKind.Utc);
            item.DurationMinutes = duration > 0 ? duration : 60;
            item.Location = location;
            item.Description = description;

            // Same region-origin-plus-fixed-offset math as HandleMyClassifiedsSave's
            // GlobalPos fix - GridRegion.RegionLocX/Y are already in meters.
            GridRegion adminEventRegion = m_GridService?.GetRegionByName(UUID.Zero, regionName);
            Vector3 adminEventGlobalPos = adminEventRegion != null
                    ? new Vector3(adminEventRegion.RegionLocX + 128, adminEventRegion.RegionLocY + 128, 25)
                    : new Vector3();
            item.GlobalPos = adminEventRegion != null ? adminEventGlobalPos.ToString() : string.Empty;

            m_EventsService.Store(item);

            response.Redirect(BasePath + "/admin/events", HttpStatusCode.Redirect);
        }

        private void HandleAdminEventsDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_EventsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (UUID.TryParse(FormValue(form, "id"), out UUID id))
                m_EventsService.Delete(id);

            response.Redirect(BasePath + "/admin/events", HttpStatusCode.Redirect);
        }

        // Public-facing side of the static page manager (task #24) - serves
        // whatever an admin has published at /web/page/<slug>, no code
        // changes needed to add a new one. 404s rather than redirecting to
        // login for an unknown slug - this is public content, not an
        // authenticated area.
        // Body is rendered as trusted raw HTML, not escaped-plain-text-with-
        // <br/> (unlike News/WelcomeMessage) - a deliberate difference, not
        // an inconsistency. Static pages exist specifically for long-form
        // admin-authored content (About, ToS, DMCA, Features-style pages -
        // see the About/ToS/DMCA content seeded from OpenSim-Grid-
        // Interface's own about.php/tos.php/dmca.php, which are themselves
        // raw HTML with real headings/links/lists) where that structure is
        // the whole point, unlike News' short blurbs that also have to
        // render inline in a compact ticker context. Safe because static
        // pages are already admin-only (same trust level as this
        // connector's WebConsole/currency-adjustment features, which
        // already give an admin full system access) - this isn't opening
        // raw HTML to anyone who couldn't already do more damage another
        // way. Found the escaping issue live: seeded real HTML content via
        // the admin API and saw literal `&lt;h1&gt;` in the response before
        // fixing this.
        // Fresh install / newly-cloned grid has zero rows in static_pages -
        // the site nav and footer used to link to /page/about, /page/tos,
        // /page/dmca unconditionally, which meant every one of those was a
        // dead 404 link until an admin created matching pages through
        // Admin > Pages. Confluence itself doesn't ship any default About/
        // ToS/DMCA text (that'd be one operator's copy baked into
        // everyone's install), so the actual fix is hiding the link rather
        // than inventing placeholder content.
        private bool HasStaticPage(string slug)
        {
            return m_StaticPageService?.GetBySlug(slug) != null;
        }

        private void HandleStaticPage(IOSHttpRequest request, IOSHttpResponse response, string slug)
        {
            StaticPage page = m_StaticPageService?.GetBySlug(slug);
            if (page == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Any static page could genuinely be opened either way - About
            // from a viewer's Help menu, ToS from an in-viewer first-login
            // consent flow some viewers show, or any of them from a normal
            // browser tab - so this decides per real request (see
            // WriteAdaptivePage/IsViewerRequest) rather than hardcoding one
            // slug as always-embedded the way an earlier pass did.
            WriteAdaptivePage(request, response, page.Title, page.Body);
        }

        // Admin CRUD, same list+edit-form shape as HandleAdminNews - the one
        // extra rule here is slug uniqueness, since two pages sharing a slug
        // would make one permanently unreachable at /web/page/<slug>. Checked
        // here rather than only relying on the DB's unique index so a
        // collision gets a real error message instead of a generic save
        // failure.
        private void HandleAdminPages(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Pages"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_StaticPageService == null)
            {
                WritePage(request, response, PageTitle("Pages"),
                        "<h1>Static Pages</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Static page service is not available.</p>");
                return;
            }

            string idParam = request.QueryString.Get("id");
            StaticPage editing = null;
            if (!string.IsNullOrEmpty(idParam) && UUID.TryParse(idParam, out UUID editID))
                editing = m_StaticPageService.Get(editID);

            string errorParam = request.QueryString.Get("error");

            StringBuilder rows = new StringBuilder();
            List<StaticPage> pages = m_StaticPageService.GetAll();
            rows.Append("<table><tr><th>Slug</th><th>Title</th><th>In Nav</th><th>Updated</th><th></th><th></th><th></th></tr>");
            foreach (StaticPage page in pages)
            {
                rows.Append("<tr>");
                rows.Append("<td>").Append(Html(page.Slug)).Append("</td>");
                rows.Append("<td>").Append(Html(page.Title)).Append("</td>");
                rows.Append("<td>").Append(page.ShowInNav ? "<span class=\"pill pill-yes\">Yes</span> (" + page.NavOrder + ")" : "<span class=\"pill pill-no\">No</span>").Append("</td>");
                rows.Append("<td>").Append(Html(page.Updated.ToString("yyyy-MM-dd"))).Append("</td>");
                rows.Append("<td><a href=\"").Append(BasePath).Append("/page/").Append(Uri.EscapeDataString(page.Slug)).Append("\" target=\"_blank\">View</a></td>");
                rows.Append("<td><a href=\"").Append(BasePath).Append("/admin/pages?id=").Append(page.ID).Append("\">Edit</a></td>");
                rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/pages/delete\">");
                rows.Append("<input type=\"hidden\" name=\"id\" value=\"").Append(page.ID).Append("\">");
                rows.Append("<button type=\"submit\">Delete</button></form></td>");
                rows.Append("</tr>");
            }
            rows.Append("</table>");

            if (pages.Count == 0)
                rows.Append("<p>No static pages yet.</p>");

            // Nav-wiring fields match WhiteCore-Dev's real admin/
            // page_manager.html - pages can place themselves in the header
            // nav (with an order) and gate visibility by login/admin state,
            // not just hold content. See WritePage's nav-building code for
            // where these are actually read.
            string formTitle = editing != null ? "Edit Page" : "Create Page";
            string errorHtml = string.IsNullOrEmpty(errorParam) ? string.Empty : "<p class=\"error\">" + Html(errorParam) + "</p>";
            string body = "<h1>Static Pages</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + rows.ToString()
                    + "<h2>" + formTitle + "</h2>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/admin/pages/save\">"
                    + "<input type=\"hidden\" name=\"id\" value=\"" + (editing != null ? editing.ID.ToString() : string.Empty) + "\">"
                    + "<label>Slug (used as /web/page/&lt;slug&gt;)<br/><input type=\"text\" name=\"slug\" value=\"" + Html(editing?.Slug ?? string.Empty) + "\" pattern=\"[a-z0-9-]+\" required></label><br/>"
                    + "<label>Title<br/><input type=\"text\" name=\"title\" value=\"" + Html(editing?.Title ?? string.Empty) + "\" required></label><br/>"
                    + "<label>Body<br/><textarea name=\"body\" rows=\"10\" required>" + Html(editing?.Body ?? string.Empty) + "</textarea></label><br/>"
                    + "<label><input type=\"checkbox\" name=\"showinnav\" value=\"true\"" + (editing != null && editing.ShowInNav ? " checked" : "") + "> Show in header nav</label><br/>"
                    + "<label>Nav order (lower shows first)<br/><input type=\"number\" name=\"navorder\" value=\"" + (editing?.NavOrder ?? 0) + "\"></label><br/>"
                    + "<label><input type=\"checkbox\" name=\"requireslogin\" value=\"true\"" + (editing != null && editing.RequiresLogin ? " checked" : "") + "> Only show in nav to logged-in residents</label><br/>"
                    + "<label><input type=\"checkbox\" name=\"requiresadmin\" value=\"true\"" + (editing != null && editing.RequiresAdmin ? " checked" : "") + "> Only show in nav to admins</label><br/>"
                    + "<button type=\"submit\">" + (editing != null ? "Save changes" : "Create") + "</button>"
                    + (editing != null ? " <a href=\"" + BasePath + "/admin/pages\">Cancel</a>" : string.Empty)
                    + "</form>";

            WritePage(request, response, PageTitle("Pages"), body);
        }

        private void HandleAdminPagesSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_StaticPageService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string idValue = FormValue(form, "id");
            string slug = FormValue(form, "slug").Trim().ToLowerInvariant();
            string title = FormValue(form, "title").Trim();
            string bodyText = FormValue(form, "body");

            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(bodyText))
            {
                response.Redirect(BasePath + "/admin/pages?error=" + Uri.EscapeDataString("Slug, title, and body are all required."), HttpStatusCode.Redirect);
                return;
            }

            StaticPage page = null;
            if (!string.IsNullOrEmpty(idValue) && UUID.TryParse(idValue, out UUID existingID))
                page = m_StaticPageService.Get(existingID);

            StaticPage slugOwner = m_StaticPageService.GetBySlug(slug);
            if (slugOwner != null && (page == null || slugOwner.ID != page.ID))
            {
                response.Redirect(BasePath + "/admin/pages?" + (page != null ? "id=" + page.ID + "&" : string.Empty)
                        + "error=" + Uri.EscapeDataString("That slug is already used by another page."), HttpStatusCode.Redirect);
                return;
            }

            if (page == null)
                page = new StaticPage { ID = UUID.Random() };

            int.TryParse(FormValue(form, "navorder"), out int navOrder);

            page.Slug = slug;
            page.Title = title;
            page.Body = bodyText;
            page.Updated = DateTime.UtcNow;
            page.ShowInNav = FormValue(form, "showinnav") == "true";
            page.NavOrder = navOrder;
            page.RequiresLogin = FormValue(form, "requireslogin") == "true";
            page.RequiresAdmin = FormValue(form, "requiresadmin") == "true";

            m_StaticPageService.Store(page);

            response.Redirect(BasePath + "/admin/pages", HttpStatusCode.Redirect);
        }

        private void HandleAdminPagesDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_StaticPageService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (UUID.TryParse(FormValue(form, "id"), out UUID id))
                m_StaticPageService.Delete(id);

            response.Redirect(BasePath + "/admin/pages", HttpStatusCode.Redirect);
        }

        // Grid settings editor (task #25) - see the GetSetting helper's
        // comment for why this is a fixed, small set of keys rather than a
        // generic config-file editor.
        private void HandleAdminSettings(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Settings"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_GridSettingsService == null)
            {
                WritePage(request, response, PageTitle("Settings"),
                        "<h1>Grid Settings</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Grid settings service is not available.</p>");
                return;
            }

            string gridName = GetSetting("GridName", m_gridName);
            string gridNick = GetSetting("GridNickname", m_gridNick);
            string welcomeMessage = GetWebSafeWelcomeMessage();
            bool allowRegistration = GetSetting("AllowRegistration", "true") == "true";
            bool announcementEnabled = GetSetting("AnnouncementEnabled", "false") == "true";
            string announcementTitle = GetSetting("AnnouncementTitle", string.Empty);
            string announcementText = GetSetting("AnnouncementText", string.Empty);
            string announcementColor = GetSetting("AnnouncementColor", "#3b82f6");
            // "Powered By" infra grid and Membership Perks lists on the
            // Features page - deliberately NOT hardcoded/defaulted the way
            // OpenSim-Grid-Interface's features.php ships generic template
            // text for these (checked its env.php: FREE_OFFERS/OTHER_PERKS
            // are never actually overridden there either, so even the
            // reference's own copy is unconfigured placeholder text, not a
            // real fact about that grid). Every deployed grid's hosting
            // stack and perks are different, so these start empty and the
            // Features page simply omits the section until an admin fills
            // them in here - same "admin-authored, not fabricated" contract
            // as the static page manager.
            string poweredBy = GetSetting("PoweredByItems", string.Empty);
            string perksFree = GetSetting("MembershipPerksFree", string.Empty);
            string perksExtra = GetSetting("MembershipPerksExtra", string.Empty);

            bool clearMapTilesOnStartup = GetSetting("ClearMapTilesOnStartup", "false") == "true";

            string bankerAvatarID = GetSetting("BankerAvatarID", string.Empty);
            string bankerAvatarName = string.Empty;
            if (UUID.TryParse(bankerAvatarID, out UUID bankerUUID) && bankerUUID != UUID.Zero && m_UserAccountService != null)
            {
                UserAccount bankerAccount = m_UserAccountService.GetUserAccount(UUID.Zero, bankerUUID);
                if (bankerAccount != null)
                    bankerAvatarName = bankerAccount.Name;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string body = "<h1>Grid Settings</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + message
                    + "<form method=\"post\" action=\"" + BasePath + "/admin/settings/save\">"
                    + "<label>Grid name<br/><input type=\"text\" name=\"grid_name\" value=\"" + Html(gridName) + "\" required></label><br/>"
                    + "<label>Grid nickname<br/><input type=\"text\" name=\"grid_nickname\" value=\"" + Html(gridNick) + "\"></label><br/>"
                    + "<label>Welcome message<br/><textarea name=\"welcome_message\" rows=\"3\">" + Html(welcomeMessage) + "</textarea></label><br/>"
                    + "<label><input type=\"checkbox\" name=\"allow_registration\" value=\"true\"" + (allowRegistration ? " checked" : "") + " style=\"width:auto;display:inline\"> Allow new users to self-register</label><br/>"
                    + "<h2>Special Announcement</h2>"
                    + "<p class=\"news-meta\">Shown as a banner at the top of the home page and splash screen (welcome.php, the viewer's login panel), above everything else - matches WhiteCore-Dev's welcomescreen_manager.html \"special window\" toggle.</p>"
                    + "<label><input type=\"checkbox\" name=\"announcement_enabled\" value=\"true\"" + (announcementEnabled ? " checked" : "") + " style=\"width:auto;display:inline\"> Show announcement banner</label><br/>"
                    + "<label>Common reasons<br/><select id=\"announcementPreset\" onchange=\"applyAnnouncementPreset(this.value)\">"
                    + "<option value=\"\">-- Choose a preset to fill in the fields below --</option>"
                    + "<option value=\"maintenance\">Scheduled Maintenance</option>"
                    + "<option value=\"restart\">Grid Restart Tonight</option>"
                    + "<option value=\"downtime\">Unexpected Downtime</option>"
                    + "<option value=\"feature\">New Feature Announcement</option>"
                    + "<option value=\"event\">Upcoming Grid Event</option>"
                    + "</select></label><br/>"
                    + "<label>Title<br/><input type=\"text\" id=\"announcementTitleInput\" name=\"announcement_title\" value=\"" + Html(announcementTitle) + "\"></label><br/>"
                    + "<label>Text<br/><textarea id=\"announcementTextInput\" name=\"announcement_text\" rows=\"2\">" + Html(announcementText) + "</textarea></label><br/>"
                    + "<label>Color<br/><input type=\"color\" name=\"announcement_color\" value=\"" + Html(announcementColor) + "\" style=\"width:auto\"></label><br/>"
                    + "<h2>Economy: Banker Avatar</h2>"
                    + "<p class=\"news-meta\">The account ConfluenceCurrency system transfers (fees, currency purchases, upload charges - anything that previously vanished into an untracked void) now flow through, instead of nowhere. "
                    + "Same concept as the classic MoneyServer's own BankerAvatar setting. Leave blank/zero to keep the old untracked behavior. "
                    + "<strong>Fund this account with a real starting balance (\"money set &lt;uuid&gt; &lt;amount&gt;\" on the region console) before setting it</strong> - once set, currency purchases and other system credits draw down this account's real balance and will fail if it runs out.</p>"
                    + (string.IsNullOrEmpty(bankerAvatarName) ? string.Empty : "<p>Currently: " + Html(bankerAvatarName) + "</p>")
                    + "<label>Banker avatar UUID<br/><input type=\"text\" name=\"banker_avatar_id\" value=\"" + Html(bankerAvatarID) + "\" placeholder=\"00000000-0000-0000-0000-000000000000\"></label><br/>"
                    + "<h2>Map Tiles</h2>"
                    + "<p class=\"news-meta\">Clears every cached map tile the next time Robust starts. Tiles only ever get "
                    + "refreshed by a region actually uploading a new one, so leaving this on wipes the map back to blank water "
                    + "tiles on every single Robust restart until each region re-uploads - meant as a one-time cleanup after a "
                    + "stale tile (a region that's since moved or been rebuilt), not a standing default. Turn it off again after "
                    + "the next restart clears what you needed cleared. Takes effect on Robust's next restart, not live.</p>"
                    + "<label><input type=\"checkbox\" name=\"clear_map_tiles_on_startup\" value=\"true\"" + (clearMapTilesOnStartup ? " checked" : "") + " style=\"width:auto;display:inline\"> Clear all map tiles on Robust's next restart</label><br/>"
                    + "<h2>Features Page: Powered By</h2>"
                    + "<p class=\"news-meta\">Shown on the Features page as an infrastructure grid. Leave blank to hide the section. One item per line, format: <code>Group|icon-name|Title|Subtitle</code> - icon-name is a Bootstrap Icons name without the \"bi-\" prefix (e.g. <code>windows</code>, <code>database</code>, <code>server</code>). Items with the same Group are shown together under that heading.</p>"
                    + "<label>Powered By items<br/><textarea name=\"powered_by\" rows=\"8\" placeholder=\"Infrastructure|windows|Windows|Host OS\nInfrastructure|hdd-network|Proxmox|Virtualization\nGrid Backend|database|MariaDB|Database\">" + Html(poweredBy) + "</textarea></label><br/>"
                    + "<h2>Features Page: Membership Perks</h2>"
                    + "<p class=\"news-meta\">Shown on the Features page. Leave blank to hide the section. One perk per line.</p>"
                    + "<label>Included free<br/><textarea name=\"perks_free\" rows=\"6\" placeholder=\"Free groups\nFree classifieds advertising\nFree mesh uploads\">" + Html(perksFree) + "</textarea></label><br/>"
                    + "<label>Community extras<br/><textarea name=\"perks_extra\" rows=\"6\" placeholder=\"No region setup fees\nRegion referral program\nHypergrid traveling\">" + Html(perksExtra) + "</textarea></label><br/>"
                    + "<button type=\"submit\">Save settings</button>"
                    + "</form>"
                    + AnnouncementPresetScript;

            WritePage(request, response, PageTitle("Settings"), body);
        }

        // Client-side only - just pre-fills the two text fields below so an
        // admin doesn't have to type common notices from scratch each time.
        // Nothing here is stored; the preset picker itself has no server-side
        // state, only the resulting title/text (saved like any other field).
        private const string AnnouncementPresetScript =
                "<script>" +
                "var announcementPresets={" +
                "maintenance:{title:'Scheduled Maintenance',text:'The grid will be briefly unavailable for scheduled maintenance. We expect this to take about 30 minutes.'}," +
                "restart:{title:'Grid Restart Tonight',text:'The grid will be restarted tonight for updates. Please save your work and expect a brief disconnect.'}," +
                "downtime:{title:'Unexpected Downtime',text:'We are aware of an issue affecting the grid and are working to resolve it. Thank you for your patience.'}," +
                "feature:{title:'New Feature Announcement',text:'We just added a new feature to the grid! Check the Features page for details.'}," +
                "event:{title:'Upcoming Grid Event',text:'Join us for an upcoming grid event - see the Events page for the full schedule.'}" +
                "};" +
                "function applyAnnouncementPreset(key){" +
                "if(!key||!announcementPresets[key])return;" +
                "document.getElementById('announcementTitleInput').value=announcementPresets[key].title;" +
                "document.getElementById('announcementTextInput').value=announcementPresets[key].text;" +
                "}" +
                "</script>";

        private void HandleAdminSettingsSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_GridSettingsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string gridName = FormValue(form, "grid_name").Trim();
            string gridNick = FormValue(form, "grid_nickname").Trim();
            string welcomeMessage = FormValue(form, "welcome_message");
            bool allowRegistration = FormValue(form, "allow_registration") == "true";
            bool announcementEnabled = FormValue(form, "announcement_enabled") == "true";
            string announcementTitle = FormValue(form, "announcement_title").Trim();
            string announcementText = FormValue(form, "announcement_text");
            string announcementColor = FormValue(form, "announcement_color");
            string poweredBy = FormValue(form, "powered_by");
            string perksFree = FormValue(form, "perks_free");
            string perksExtra = FormValue(form, "perks_extra");
            string bankerAvatarID = FormValue(form, "banker_avatar_id").Trim();
            bool clearMapTilesOnStartup = FormValue(form, "clear_map_tiles_on_startup") == "true";

            if (string.IsNullOrEmpty(gridName))
            {
                response.Redirect(BasePath + "/admin/settings?message=" + Uri.EscapeDataString("Grid name is required."), HttpStatusCode.Redirect);
                return;
            }

            if (!string.IsNullOrEmpty(bankerAvatarID) && !UUID.TryParse(bankerAvatarID, out _))
            {
                response.Redirect(BasePath + "/admin/settings?message=" + Uri.EscapeDataString("Banker avatar UUID is not valid."), HttpStatusCode.Redirect);
                return;
            }

            m_GridSettingsService.Set("GridName", gridName);
            m_GridSettingsService.Set("GridNickname", gridNick);
            m_GridSettingsService.Set("WelcomeMessage", welcomeMessage);
            m_GridSettingsService.Set("AllowRegistration", allowRegistration ? "true" : "false");
            m_GridSettingsService.Set("AnnouncementEnabled", announcementEnabled ? "true" : "false");
            m_GridSettingsService.Set("AnnouncementTitle", announcementTitle);
            m_GridSettingsService.Set("AnnouncementText", announcementText);
            if (!string.IsNullOrEmpty(announcementColor))
                m_GridSettingsService.Set("AnnouncementColor", announcementColor);
            m_GridSettingsService.Set("PoweredByItems", poweredBy);
            m_GridSettingsService.Set("MembershipPerksFree", perksFree);
            m_GridSettingsService.Set("MembershipPerksExtra", perksExtra);
            m_GridSettingsService.Set("BankerAvatarID", bankerAvatarID);
            m_GridSettingsService.Set("ClearMapTilesOnStartup", clearMapTilesOnStartup ? "true" : "false");

            response.Redirect(BasePath + "/admin/settings?message=" + Uri.EscapeDataString("Settings saved."), HttpStatusCode.Redirect);
        }

        // Web-based region console (task #26) - see WebConsoleModule.cs
        // (region-side) for the actual command-execution/output-capture
        // implementation and its security-note comment on why this is
        // disabled unless a real shared secret is configured on BOTH ends.
        // This page is the Robust-side half: pick a region, send it a
        // command over HTTP, show the result.
        private void HandleAdminConsole(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Console"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (string.IsNullOrEmpty(m_webConsoleSecret))
            {
                WritePage(request, response, PageTitle("Console"),
                        "<h1>Region Console</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                        + "<p>Web console is not configured on this grid. Set <code>[WebConsole] SharedSecret</code> "
                        + "in Robust's config, and matching <code>[WebConsole] Enabled = true</code> / "
                        + "<code>SharedSecret</code> in each region's own config, to enable this page.</p>");
                return;
            }

            if (m_GridService == null)
            {
                WritePage(request, response, PageTitle("Console"),
                        "<h1>Region Console</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Grid service is not available.</p>");
                return;
            }

            List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
            regions.Sort((a, b) => string.Compare(a.RegionName, b.RegionName, StringComparison.OrdinalIgnoreCase));

            string selectedRegionParam = request.QueryString.Get("region_id");
            StringBuilder options = new StringBuilder();
            foreach (GridRegion region in regions)
            {
                bool selected = selectedRegionParam == region.RegionID.ToString();
                options.Append("<option value=\"").Append(region.RegionID).Append("\"").Append(selected ? " selected" : "").Append(">")
                        .Append(Html(region.RegionName)).Append("</option>");
            }

            string output = request.QueryString.Get("output");
            string outputBlock = string.IsNullOrEmpty(output)
                    ? string.Empty
                    : "<h2>Output</h2><pre style=\"background:#222;color:#ddd;padding:12px;overflow-x:auto;white-space:pre-wrap\">" + Html(output) + "</pre>";
            string errorParam = request.QueryString.Get("error");
            string errorHtml = string.IsNullOrEmpty(errorParam) ? string.Empty : "<p class=\"error\">" + Html(errorParam) + "</p>";

            string body = "<h1>Region Console</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + "<p class=\"error\">Commands run here execute with full console privileges on the target region - the same as physical/RDP console access. Use with care.</p>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/admin/console/run\">"
                    + "<label>Region<br/><select name=\"region_id\">" + options.ToString() + "</select></label><br/>"
                    + "<label>Command<br/><input type=\"text\" name=\"command\" placeholder=\"e.g. show info\" required></label><br/>"
                    + "<button type=\"submit\">Run</button>"
                    + "</form>"
                    + outputBlock;

            WritePage(request, response, PageTitle("Console"), body);
        }

        private void HandleAdminConsoleRun(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || string.IsNullOrEmpty(m_webConsoleSecret) || m_GridService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string command = FormValue(form, "command");

            if (!UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
            {
                response.Redirect(BasePath + "/admin/console?error=" + Uri.EscapeDataString("No region selected."), HttpStatusCode.Redirect);
                return;
            }

            GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionID);
            if (region == null || string.IsNullOrEmpty(region.ServerURI))
            {
                response.Redirect(BasePath + "/admin/console?region_id=" + regionID
                        + "&error=" + Uri.EscapeDataString("That region's server address is not known to the grid service."), HttpStatusCode.Redirect);
                return;
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                response.Redirect(BasePath + "/admin/console?region_id=" + regionID
                        + "&error=" + Uri.EscapeDataString("Enter a command to run."), HttpStatusCode.Redirect);
                return;
            }

            string output = RunRegionConsoleCommand(region, command);

            response.Redirect(BasePath + "/admin/console?region_id=" + regionID + "&output=" + Uri.EscapeDataString(output), HttpStatusCode.Redirect);
        }

        // One-click restart from the admin Regions table - same
        // RunRegionConsoleCommand/shared-secret mechanism as the free-form
        // console above. Sends "region restart 30" (RestartModule.cs),
        // NOT the bare "restart" its own name suggests - that shorter
        // command is a real stock OpenSim.cs command but is hardcoded to
        // a no-op ("Restart command disabled, because currently it is
        // unreliable."), confirmed live while testing an unrelated OSSL
        // change this session: it returned success with no actual restart.
        // "region restart <seconds>" is the one that's actually wired up
        // (gives connected residents a warning instead of an instant kill).
        // Any region, no ownership check - admin-only.
        private void HandleAdminRegionRestart(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || string.IsNullOrEmpty(m_webConsoleSecret) || m_GridService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (!UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
            {
                response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString("No region selected."), HttpStatusCode.Redirect);
                return;
            }

            GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionID);
            if (region == null || string.IsNullOrEmpty(region.ServerURI))
            {
                response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString("That region's server address is not known to the grid service."), HttpStatusCode.Redirect);
                return;
            }

            RunRegionConsoleCommand(region, "region restart 30");

            response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString("Restart command sent to " + region.RegionName + "."), HttpStatusCode.Redirect);
        }

        // Same RunRegionConsoleCommand/shared-secret mechanism as Restart
        // above, calling GroupAutoInviteModule's own console commands
        // (GroupAutoInviteModule.cs) instead. This is a live, in-memory
        // toggle only - it does not persist to that region's OpenSim.ini,
        // so a region restart/crash reverts to whatever's on disk there.
        // That's deliberate: GroupAutoInvite is meant to be turned on for a
        // specific sim while testing, not as a standing grid-wide setting -
        // see OpenSimDefaults.ini's own [GroupAutoInvite] section (disabled,
        // group-less by design) and PROJECT_LOG.md for the two-week silent
        // failure that came from it having been grid-wide before this.
        private void HandleAdminRegionGroupAutoInvite(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || string.IsNullOrEmpty(m_webConsoleSecret) || m_GridService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (!UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
            {
                response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString("No region selected."), HttpStatusCode.Redirect);
                return;
            }

            GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionID);
            if (region == null || string.IsNullOrEmpty(region.ServerURI))
            {
                response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString("That region's server address is not known to the grid service."), HttpStatusCode.Redirect);
                return;
            }

            string action = FormValue(form, "action");
            string message;
            if (action == "disable")
            {
                RunRegionConsoleCommand(region, "group-auto-invite disable");
                message = "Group Auto-Invite disabled in " + region.RegionName + ".";
            }
            else if (action == "enable" && UUID.TryParse(FormValue(form, "group_id"), out UUID groupID) && !groupID.IsZero())
            {
                RunRegionConsoleCommand(region, "group-auto-invite enable " + groupID);
                message = "Group Auto-Invite enabled in " + region.RegionName + " with target group " + groupID + ".";
            }
            else
            {
                message = "Enter a valid group UUID to enable Group Auto-Invite.";
            }

            response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Shared by HandleAdminConsoleRun (free-form console page) and the
        // dedicated Kick/Message buttons on the user detail page - both send
        // a command string to the same region-side /consoleweb endpoint over
        // the same shared secret (see WebConsoleModule.cs, task #26).
        private string RunRegionConsoleCommand(GridRegion region, string command)
        {
            try
            {
                // Prefer the same-host loopback address over the region's
                // public ServerURI - Robust and every region process share
                // this same physical host in every deployment this file
                // assumes, and going out through the public hostname needs
                // NAT hairpin/router port-forwarding that isn't guaranteed
                // for every port. Confirmed live: this silently failed for
                // Store-ordered regions specifically (their auto-allocated
                // ports, unlike the grid's original manually-configured
                // regions, aren't forwarded on the router) - a Stop/restart/
                // PrimPack-fulfillment command looked like it was sent, but
                // never reached the region, because every caller here
                // trusted a "sent" result without the call actually having
                // succeeded. Falls back to the public URI only if the
                // ServerURI itself can't be parsed, for safety.
                string url = Uri.TryCreate(region.ServerURI, UriKind.Absolute, out Uri parsedUri)
                        ? "http://127.0.0.1:" + parsedUri.Port + "/consoleweb"
                        : region.ServerURI.TrimEnd('/') + "/consoleweb";
                using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.Add("X-Console-Secret", m_webConsoleSecret);

                    Dictionary<string, string> body = new Dictionary<string, string> { { "command", command } };
                    var result = client.PostAsync(url, new System.Net.Http.FormUrlEncodedContent(body)).GetAwaiter().GetResult();
                    string responseText = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    return result.IsSuccessStatusCode
                            ? responseText
                            : "Region responded with HTTP " + (int)result.StatusCode + ": " + responseText;
                }
            }
            catch (Exception e)
            {
                return "Could not reach " + region.RegionName + ": " + e.Message;
            }
        }

        // Read-only v1: lists/shows reports the AbuseReportsModule viewer cap
        // already captures (OpenSim/Region/ClientStack/Linden/Caps). Note
        // CheckFlags on AbuseReportData is the REPORTER's own submission-time
        // checkboxes, not an admin "resolved" flag - there is no
        // resolved/handled tracking anywhere in the data model today. Adding
        // that would mean a real schema change (new column + migration)
        // across all three data backends (MySQL/PGSQL/SQLite), which is a
        // bigger, separate piece of work than "view the reports that already
        // exist" - left for a future pass if wanted.
        private void HandleAdminAbuseReports(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Abuse Reports"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_AbuseReportsService == null)
            {
                WritePage(request, response, PageTitle("Abuse Reports"),
                        "<h1>Abuse Reports</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Abuse Reports service is not available.</p>");
                return;
            }

            string idParam = request.QueryString.Get("id");
            string body;

            if (!string.IsNullOrEmpty(idParam) && int.TryParse(idParam, out int reportID))
            {
                AbuseReportData report = m_AbuseReportsService.GetAbuseReport(reportID);
                if (report == null)
                {
                    body = "<h1>Abuse Reports</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/abuse-reports\">Back to list</a></p>"
                            + "<p>Report not found.</p>";
                }
                else
                {
                    string when = DateTimeOffset.FromUnixTimeSeconds(report.Time).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                    string image = (report.ImageData != null && report.ImageData.Length > 0)
                            ? "<p><img src=\"" + BasePath + "/admin/abuse-reports/image?id=" + report.ReportID + "\" style=\"max-width:100%\"></p>"
                            : "<p>No screenshot attached.</p>";

                    body = "<h1>Abuse Report #" + report.ReportID + "</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/abuse-reports\">Back to list</a></p>"
                            + "<table>"
                            + "<tr><th>Time</th><td>" + Html(when) + "</td></tr>"
                            + "<tr><th>Reported by</th><td>" + Html(report.SenderName) + "</td></tr>"
                            + "<tr><th>Abuser</th><td>" + Html(report.AbuserName) + "</td></tr>"
                            + "<tr><th>Region</th><td>" + Html(report.AbuseRegionName) + "</td></tr>"
                            + "<tr><th>Category</th><td>" + Html(report.Category) + "</td></tr>"
                            + "<tr><th>Position</th><td>" + Html(report.Position) + "</td></tr>"
                            + "<tr><th>Object</th><td>" + Html(report.ObjectID.ToString()) + "</td></tr>"
                            + "<tr><th>Summary</th><td>" + Html(report.Summary) + "</td></tr>"
                            + "<tr><th>Details</th><td>" + Html(report.Details).Replace("\n", "<br/>") + "</td></tr>"
                            + "<tr><th>Viewer</th><td>" + Html(report.Version) + "</td></tr>"
                            + "</table>"
                            + image;
                }
            }
            else
            {
                int.TryParse(request.QueryString.Get("start"), out int start);
                if (start < 0)
                    start = 0;
                const int pageSize = 25;

                List<AbuseReportData> reports = m_AbuseReportsService.GetAbuseReports(start, pageSize);

                StringBuilder rows = new StringBuilder();
                rows.Append("<table><tr><th>Time</th><th>Reported by</th><th>Abuser</th><th>Region</th><th>Category</th><th>Summary</th></tr>");
                foreach (AbuseReportData report in reports)
                {
                    string when = DateTimeOffset.FromUnixTimeSeconds(report.Time).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                    rows.Append("<tr>");
                    rows.Append("<td>").Append(Html(when)).Append("</td>");
                    rows.Append("<td>").Append(Html(report.SenderName)).Append("</td>");
                    rows.Append("<td>").Append(Html(report.AbuserName)).Append("</td>");
                    rows.Append("<td>").Append(Html(report.AbuseRegionName)).Append("</td>");
                    rows.Append("<td>").Append(Html(report.Category)).Append("</td>");
                    rows.Append("<td><a href=\"").Append(BasePath).Append("/admin/abuse-reports?id=").Append(report.ReportID).Append("\">")
                            .Append(Html(report.Summary)).Append("</a></td>");
                    rows.Append("</tr>");
                }
                rows.Append("</table>");

                if (reports.Count == 0 && start == 0)
                    rows.Append("<p>No abuse reports on file.</p>");

                string nextLink = reports.Count == pageSize
                        ? "<p><a href=\"" + BasePath + "/admin/abuse-reports?start=" + (start + pageSize) + "\">Next page</a></p>"
                        : string.Empty;
                string prevLink = start > 0
                        ? "<p><a href=\"" + BasePath + "/admin/abuse-reports?start=" + Math.Max(0, start - pageSize) + "\">Previous page</a></p>"
                        : string.Empty;

                body = "<h1>Abuse Reports</h1>"
                        + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                        + rows.ToString()
                        + prevLink
                        + nextLink;
            }

            WritePage(request, response, PageTitle("Abuse Reports"), body);
        }

        // Abuse report screenshots arrive over the viewer's
        // SendUserReportWithScreenshot cap as raw JPEG bytes (see
        // AbuseReportsModule.cs) - same assumption real SL viewers make about
        // this specific upload.
        private void HandleAdminAbuseReportImage(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            if (m_AbuseReportsService == null || !int.TryParse(request.QueryString.Get("id"), out int reportID))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            AbuseReportData report = m_AbuseReportsService.GetAbuseReport(reportID);
            if (report == null || report.ImageData == null || report.ImageData.Length == 0)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.ContentType = "image/jpeg";
            response.RawBuffer = report.ImageData;
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        // Grid-wide financial reporting - two tabs (currency transfers,
        // real-money purchases), optionally filtered to one agent by name.
        // Task #20 from the WhiteCore-Dev re-audit's "all of it" list - this
        // is the read side of the ledger Batch 12's CurrencyService already
        // writes; no new currency logic here, just visibility into what
        // already exists. ToAgentName/FromAgentName on CurrencyTransfer are
        // never populated by the DB layer (see MySQLCurrencyData.cs), so
        // names are resolved here via UserAccountService per row, same as
        // HandleAdminUsers already does elsewhere on this page.
        //
        // The agent filter needs "either side" semantics (an agent's
        // activity means money they sent OR received), which
        // GetTransactionHistory doesn't support directly - it ANDs
        // toAgentID/fromAgentID when both are non-zero. Worked around the
        // same way CurrencyService.GetGroupTransactionHistory already does
        // for groups: query both directions, merge, sort, then page in
        // memory. Bounded to a fixed overfetch window rather than being
        // fully exact for large histories - acceptable for an admin
        // reporting tool, not a public/scriptable API.
        private void HandleAdminTransactions(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Transactions"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_CurrencyService == null)
            {
                WritePage(request, response, PageTitle("Transactions"),
                        "<h1>Purchases &amp; Transactions</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Currency service is not available.</p>");
                return;
            }

            string tab = request.QueryString.Get("tab") == "purchases" ? "purchases" : "transfers";
            string agentQuery = request.QueryString.Get("agent") ?? string.Empty;
            int.TryParse(request.QueryString.Get("start"), out int start);
            if (start < 0)
                start = 0;
            const int pageSize = 25;
            const int overfetch = 1000;

            DateTime dateStart = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime dateEnd = DateTime.UtcNow.AddDays(1);

            UUID agentID = UUID.Zero;
            string agentNotFound = string.Empty;
            if (!string.IsNullOrWhiteSpace(agentQuery))
            {
                string[] nameParts = agentQuery.Trim().Split(new[] { ' ' }, 2);
                UserAccount agentAccount = nameParts.Length == 2 && m_UserAccountService != null
                        ? m_UserAccountService.GetUserAccount(UUID.Zero, nameParts[0], nameParts[1])
                        : null;
                if (agentAccount != null)
                    agentID = agentAccount.PrincipalID;
                else
                    agentNotFound = "<p class=\"error\">No user found matching \"" + Html(agentQuery) + "\" (use \"First Last\").</p>";
            }

            StringBuilder rows = new StringBuilder();
            bool hasNextPage;

            if (tab == "purchases")
            {
                List<CurrencyPurchase> purchases = m_CurrencyService.GetPurchaseHistory(agentID, dateStart, dateEnd, null, null);
                hasNextPage = start + pageSize < purchases.Count;

                rows.Append("<table><tr><th>Date</th><th>Agent</th><th>IP</th><th>L$ credited</th><th>Real amount (hundredths)</th></tr>");
                foreach (CurrencyPurchase p in purchases.Skip(start).Take(pageSize))
                {
                    rows.Append("<tr>");
                    rows.Append("<td>").Append(Html(p.PurchaseDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>");
                    rows.Append("<td>").Append(Html(ResolveAgentName(p.AgentID))).Append("</td>");
                    rows.Append("<td>").Append(Html(p.IP)).Append("</td>");
                    rows.Append("<td>").Append(p.Amount).Append("</td>");
                    rows.Append("<td>").Append(p.RealAmount).Append("</td>");
                    rows.Append("</tr>");
                }
                rows.Append("</table>");

                if (purchases.Count == 0)
                    rows.Append("<p>No purchases on file" + (agentID != UUID.Zero ? " for this agent" : string.Empty) + ".</p>");
            }
            else
            {
                List<CurrencyTransfer> transfers;
                if (agentID != UUID.Zero)
                {
                    List<CurrencyTransfer> sent = m_CurrencyService.GetTransactionHistory(UUID.Zero, agentID, dateStart, dateEnd, 0, overfetch);
                    List<CurrencyTransfer> received = m_CurrencyService.GetTransactionHistory(agentID, UUID.Zero, dateStart, dateEnd, 0, overfetch);
                    Dictionary<UUID, CurrencyTransfer> merged = new Dictionary<UUID, CurrencyTransfer>();
                    foreach (CurrencyTransfer t in sent)
                        merged[t.ID] = t;
                    foreach (CurrencyTransfer t in received)
                        merged[t.ID] = t;
                    transfers = merged.Values.OrderByDescending(t => t.TransferDate).ToList();
                }
                else
                {
                    transfers = m_CurrencyService.GetTransactionHistory(UUID.Zero, UUID.Zero, dateStart, dateEnd, (uint)start, (uint)pageSize + 1);
                }

                hasNextPage = transfers.Count > (agentID != UUID.Zero ? start + pageSize : pageSize);
                IEnumerable<CurrencyTransfer> page = agentID != UUID.Zero
                        ? transfers.Skip(start).Take(pageSize)
                        : transfers.Take(pageSize);

                // Reference's {ToBalance} column - same real gap as the
                // self-service /transactions page had, same fix.
                rows.Append("<table><tr><th>Date</th><th>From</th><th>To</th><th>Amount</th><th>Type</th><th>Description</th><th>To Balance</th></tr>");
                foreach (CurrencyTransfer t in page)
                {
                    rows.Append("<tr>");
                    rows.Append("<td>").Append(Html(t.TransferDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>");
                    rows.Append("<td>").Append(Html(ResolveAgentName(t.FromAgent))).Append("</td>");
                    rows.Append("<td>").Append(Html(ResolveAgentName(t.ToAgent))).Append("</td>");
                    rows.Append("<td>").Append(t.Amount).Append("</td>");
                    rows.Append("<td>").Append(t.TransferType).Append("</td>");
                    rows.Append("<td>").Append(Html(t.Description)).Append("</td>");
                    rows.Append("<td>").Append(t.ToBalance).Append("</td>");
                    rows.Append("</tr>");
                }
                rows.Append("</table>");

                if (transfers.Count == 0)
                    rows.Append("<p>No transactions on file" + (agentID != UUID.Zero ? " for this agent" : string.Empty) + ".</p>");
            }

            string tabLink(string t, string label) =>
                    "<a href=\"" + BasePath + "/admin/transactions?tab=" + t
                    + (string.IsNullOrEmpty(agentQuery) ? string.Empty : "&agent=" + Uri.EscapeDataString(agentQuery))
                    + "\"" + (t == tab ? " style=\"font-weight:bold\"" : string.Empty) + ">" + label + "</a>";

            string nextLink = hasNextPage
                    ? "<p><a href=\"" + BasePath + "/admin/transactions?tab=" + tab + "&start=" + (start + pageSize)
                        + (string.IsNullOrEmpty(agentQuery) ? string.Empty : "&agent=" + Uri.EscapeDataString(agentQuery)) + "\">Next page</a></p>"
                    : string.Empty;
            string prevLink = start > 0
                    ? "<p><a href=\"" + BasePath + "/admin/transactions?tab=" + tab + "&start=" + Math.Max(0, start - pageSize)
                        + (string.IsNullOrEmpty(agentQuery) ? string.Empty : "&agent=" + Uri.EscapeDataString(agentQuery)) + "\">Previous page</a></p>"
                    : string.Empty;

            string body = "<h1>Purchases &amp; Transactions</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + "<p>" + tabLink("transfers", "Transfers") + " | " + tabLink("purchases", "Purchases") + "</p>"
                    + "<form method=\"get\" action=\"" + BasePath + "/admin/transactions\">"
                    + "<input type=\"hidden\" name=\"tab\" value=\"" + tab + "\">"
                    + "<label>Filter by agent (First Last): <input type=\"text\" name=\"agent\" value=\"" + Html(agentQuery) + "\"></label> "
                    + "<button type=\"submit\">Filter</button>"
                    + (agentID != UUID.Zero ? " <a href=\"" + BasePath + "/admin/transactions?tab=" + tab + "\">Clear</a>" : string.Empty)
                    + "</form>"
                    + agentNotFound
                    + rows.ToString()
                    + prevLink
                    + nextLink;

            WritePage(request, response, PageTitle("Transactions"), body);
        }

        private string ResolveAgentName(UUID agentID)
        {
            if (agentID == UUID.Zero)
                return "(system)";

            UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, agentID);
            return account != null ? account.FirstName + " " + account.LastName : agentID.ToString();
        }

        // GetUserAccounts(scope, query) requires at least one word >2 chars and
        // caps at two words (first/last), and only searches accounts with
        // active=1 under the hood (MySQLUserAccountData.GetUsers) - there's no
        // "list everyone" call, so an empty search intentionally shows nothing
        // rather than trying to dump a potentially huge table. Ban/Unban and
        // soft-delete are UserLevel-sentinel based (BannedUserLevel/
        // DeletedUserLevel below) rather than the DB's own `active` column -
        // see HandleAdminUsersSoftDelete's comment for why a true hard delete
        // remains out of scope.
        private static string BuildMembershipTypeOptions(int selectedType)
        {
            StringBuilder options = new StringBuilder();
            foreach (KeyValuePair<int, string> type in AccountMembershipHelper.AllTypes)
            {
                options.Append("<option value=\"" + type.Key + "\""
                        + (type.Key == selectedType ? " selected" : "")
                        + ">" + Html(type.Value) + "</option>");
            }
            return options.ToString();
        }

        private void HandleAdminUsers(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("User Management"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string searchForm = "<form method=\"get\" action=\"" + BasePath + "/admin/users\">"
                    + "<input type=\"text\" name=\"q\" placeholder=\"Search by name\" value=\"" + Html(request.QueryString.Get("q") ?? string.Empty) + "\">"
                    + "<button type=\"submit\">Search</button>"
                    + "</form>";

            string principalParam = request.QueryString.Get("principal");
            string body;

            if (!string.IsNullOrEmpty(principalParam) && UUID.TryParse(principalParam, out UUID principalID))
            {
                UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, principalID);
                if (account == null)
                {
                    body = "<h1>User Management</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/users\">Back to search</a></p>"
                            + "<p>Account not found.</p>";
                }
                else
                {
                    AccountBanHelper.ClearExpiredBan(account, m_UserAccountService, m_UserProfilesService);

                    string created = DateTimeOffset.FromUnixTimeSeconds(account.Created).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                    string balance = m_CurrencyService != null
                            ? m_CurrencyService.GetBalance(account.PrincipalID).ToString()
                            : "n/a";

                    DateTime? banExpiry = account.UserLevel == AccountBanHelper.BannedUserLevel ? AccountBanHelper.GetBanExpiry(m_UserProfilesService, account.PrincipalID) : null;
                    string statusLabel = account.UserLevel == DeletedUserLevel ? "Deleted"
                            : account.UserLevel == AccountBanHelper.BannedUserLevel
                                ? (banExpiry.HasValue ? "Banned until " + banExpiry.Value.ToString("yyyy-MM-dd HH:mm") + " UTC" : "Banned")
                            : "Active";

                    GridRegion onlineRegion = FindOnlineUserRegion(account.PrincipalID);
                    string presenceBlock;
                    if (onlineRegion == null || string.IsNullOrEmpty(onlineRegion.ServerURI))
                    {
                        presenceBlock = "<h2>Kick / Message</h2><p class=\"news-meta\">This resident does not appear to be online right now.</p>";
                    }
                    else if (string.IsNullOrEmpty(m_webConsoleSecret))
                    {
                        presenceBlock = "<h2>Kick / Message</h2><p class=\"news-meta\">Online now in " + Html(onlineRegion.RegionName)
                                + ", but the web console is not configured, so kick/message cannot be sent from here. "
                                + "Set <code>[WebConsole] SharedSecret</code> in Robust's config to enable this.</p>";
                    }
                    else
                    {
                        presenceBlock = "<h2>Kick / Message</h2>"
                                + "<p class=\"news-meta\">Online now in " + Html(onlineRegion.RegionName) + ".</p>"
                                + "<form method=\"post\" action=\"" + BasePath + "/admin/users/kick\" onsubmit=\"return confirm('Kick "
                                + Html(account.Name) + " from " + Html(onlineRegion.RegionName) + "?');\">"
                                + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                                + "<button type=\"submit\">Kick from region</button>"
                                + "</form>"
                                + "<form method=\"post\" action=\"" + BasePath + "/admin/users/message\">"
                                + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                                + "<label>Message: <input type=\"text\" name=\"message_text\" placeholder=\"Message to send\" required></label> "
                                + "<button type=\"submit\">Send message</button>"
                                + "</form>";
                    }

                    int currentMembershipType = AccountMembershipHelper.GetMembershipType(account.UserFlags);
                    string membershipOptions = BuildMembershipTypeOptions(currentMembershipType);

                    body = "<h1>" + Html(account.Name) + "</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/users\">Back to search</a></p>"
                            + message
                            + "<table>"
                            + "<tr><th>Principal ID</th><td>" + account.PrincipalID + "</td></tr>"
                            + "<tr><th>Email</th><td>" + Html(account.Email) + "</td></tr>"
                            + "<tr><th>Created</th><td>" + Html(created) + "</td></tr>"
                            + "<tr><th>Status</th><td>" + statusLabel + "</td></tr>"
                            + "<tr><th>User Level</th><td>" + account.UserLevel + "</td></tr>"
                            + "<tr><th>Account Type</th><td>" + Html(AccountMembershipHelper.GetName(currentMembershipType)) + "</td></tr>"
                            + "<tr><th>Profile Title</th><td>" + (string.IsNullOrEmpty(account.UserTitle) ? "<em>none</em>" : Html(account.UserTitle)) + "</td></tr>"
                            + "<tr><th>Currency balance</th><td>" + balance + "</td></tr>"
                            + "</table>"
                            + "<p><a href=\"" + BasePath + "/profile?id=" + account.PrincipalID + "\">View public profile</a></p>"
                            + "<h2>Account details</h2>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/edit-details\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<label>First name: <input type=\"text\" name=\"first_name\" value=\"" + Html(account.FirstName) + "\" required></label>"
                            + "<label>Last name: <input type=\"text\" name=\"last_name\" value=\"" + Html(account.LastName) + "\" required></label>"
                            + "<label>Email: <input type=\"email\" name=\"email\" value=\"" + Html(account.Email) + "\"></label>"
                            + "<label>Account type: <select name=\"membership_type\">" + membershipOptions + "</select></label>"
                            + "<label>Profile title/badge: <input type=\"text\" name=\"user_title\" value=\"" + Html(account.UserTitle) + "\" placeholder=\"Shown in the resident's profile instead of the account type's built-in badge\"></label>"
                            + "<p class=\"news-meta\">Only Trial Member/Charter Member/Grid Team have a built-in badge icon in most viewers - Resident and any other account type need a Profile title set to actually show anything (left blank here, it's auto-filled with the account type's name).</p>"
                            + "<button type=\"submit\">Save</button>"
                            + "</form>"
                            + "<h2>Reset password</h2>"
                            + "<p class=\"news-meta\">Sets a specific new password immediately, for a resident who's locked out and can't use the self-service forgot-password email flow.</p>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/reset-password\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<label>New password: <input type=\"password\" name=\"new_password\" minlength=\"6\" required></label>"
                            + "<button type=\"submit\">Set password</button>"
                            + "</form>"
                            + "<h2>Change user level</h2>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/set-level\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<label>User level: <input type=\"number\" name=\"user_level\" value=\"" + account.UserLevel + "\" min=\"" + DeletedUserLevel + "\" max=\"250\" required></label> "
                            + "<button type=\"submit\">Update</button>"
                            + "</form>"
                            + "<h2>Ban / Unban</h2>"
                            + "<p class=\"news-meta\">A banned account fails login immediately (same check the grid-wide minimum login level already uses), without touching its password or data. "
                            + "A timed ban auto-clears back to Active the next time the account tries to log in - including the real grid/viewer login, not just this page or the web login form.</p>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/set-level\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<input type=\"hidden\" name=\"user_level\" value=\"" + (account.UserLevel == AccountBanHelper.BannedUserLevel ? "UNBAN" : AccountBanHelper.BannedUserLevel.ToString()) + "\">"
                            + (account.UserLevel == AccountBanHelper.BannedUserLevel
                                ? string.Empty
                                : "<label>Ban duration (hours, blank = permanent): <input type=\"number\" name=\"ban_hours\" min=\"1\"></label> ")
                            + "<button type=\"submit\">" + (account.UserLevel == AccountBanHelper.BannedUserLevel ? "Unban this user" : "Ban this user") + "</button>"
                            + "</form>"
                            + presenceBlock
                            + (account.UserLevel != DeletedUserLevel
                                ? "<h2>Delete account</h2>"
                                + "<p class=\"news-meta\">Scrambles the password (so the account can never log in again) and marks it Deleted. "
                                + "This is a soft delete - the account row and its data are not removed, so it can be recovered by an admin "
                                + "un-banning it and walking the resident through a fresh password reset, but it cannot be undone by the resident themselves.</p>"
                                + "<form method=\"post\" action=\"" + BasePath + "/admin/users/soft-delete\" onsubmit=\"return confirm('Delete this account? The resident will not be able to log in until an admin resets their password.');\">"
                                + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                                + "<button type=\"submit\">Delete this account</button>"
                                + "</form>"
                                : string.Empty)
                            + "<h2>Remove account permanently</h2>"
                            + "<p class=\"news-meta\">Unlike Delete above, this is permanent and cannot be undone. Removes the account, "
                            + "login credentials, home/last-location, friendships (both directions), inventory structure, and appearance. "
                            + "Currency balance and transaction history are also removed. Assets this resident ever uploaded are never touched - "
                            + "other things may still reference them. Refuses if the account is currently online, or owns an estate.</p>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/remove\" onsubmit=\"return confirm('Permanently remove "
                                + Html(account.Name).Replace("'", "\\'") + "? This cannot be undone.');\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<button type=\"submit\">Remove account permanently</button>"
                            + "</form>"
                            + "<h2>Log in as this user</h2>"
                            + "<p class=\"news-meta\">Opens a dashboard session as this account, for support/troubleshooting. Logged server-side for audit purposes.</p>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/login-as\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<button type=\"submit\">Log in as " + Html(account.Name) + "</button>"
                            + "</form>";
                }
            }
            else
            {
                string query = request.QueryString.Get("q");
                StringBuilder rows = new StringBuilder();

                // Blank search = every account on the grid, not "nothing" -
                // GetUserAccountsWhere(UUID.Zero, "1=1") is the same
                // internal-only raw-fragment call HandleFeatures/HandleAdminStats
                // already use for a total-account count; safe here too since
                // "1=1" is a fixed literal, not user input reaching SQL.
                // Paginated either way (25/page) so a real resident count
                // doesn't dump one giant unscrollable table.
                if (m_UserAccountService != null)
                {
                    List<UserAccount> results = string.IsNullOrEmpty(query)
                            ? m_UserAccountService.GetUserAccountsWhere(UUID.Zero, "1=1")
                            : m_UserAccountService.GetUserAccounts(UUID.Zero, query);
                    results.Sort((a, b) => string.Compare(a.FirstName + " " + a.LastName, b.FirstName + " " + b.LastName, StringComparison.OrdinalIgnoreCase));

                    const int pageSize = 25;
                    int totalPages = Math.Max(1, (int)Math.Ceiling(results.Count / (double)pageSize));
                    int page = 1;
                    int.TryParse(request.QueryString.Get("page"), out page);
                    page = Math.Max(1, Math.Min(page, totalPages));
                    List<UserAccount> pageResults = results.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                    if (results.Count == 0)
                    {
                        rows.Append("<p>No accounts matched that search.</p>");
                    }
                    else
                    {
                        rows.Append("<p class=\"news-meta\">").Append(results.Count).Append(results.Count == 1 ? " account" : " accounts")
                          .Append(string.IsNullOrEmpty(query) ? " on this grid" : " matched").Append("</p>");
                        // Reference's user_manager.html table shows Region/
                        // Location/Online at a glance - real gap, this list
                        // made you open each account to see presence. Cheap
                        // here since the page is already capped at 25 rows.
                        rows.Append("<table><tr><th>Name</th><th>Email</th><th>User Level</th><th>Online</th></tr>");
                        foreach (UserAccount account in pageResults)
                        {
                            GridRegion onlineRegion = FindOnlineUserRegion(account.PrincipalID);
                            rows.Append("<tr><td><a href=\"").Append(BasePath).Append("/admin/users?principal=").Append(account.PrincipalID).Append("\">")
                                    .Append(Html(account.Name)).Append("</a></td>");
                            rows.Append("<td>").Append(Html(account.Email)).Append("</td>");
                            rows.Append("<td>").Append(account.UserLevel).Append("</td>");
                            rows.Append("<td>").Append(onlineRegion != null ? Html(onlineRegion.RegionName) : "Offline").Append("</td></tr>");
                        }
                        rows.Append("</table>");

                        if (totalPages > 1)
                        {
                            string qParam = string.IsNullOrEmpty(query) ? string.Empty : "&q=" + Uri.EscapeDataString(query);
                            rows.Append("<p class=\"news-meta\">");
                            if (page > 1)
                                rows.Append("<a href=\"").Append(BasePath).Append("/admin/users?page=").Append(page - 1).Append(qParam).Append("\">&larr; Previous</a> &middot; ");
                            rows.Append("Page ").Append(page).Append(" of ").Append(totalPages);
                            if (page < totalPages)
                                rows.Append(" &middot; <a href=\"").Append(BasePath).Append("/admin/users?page=").Append(page + 1).Append(qParam).Append("\">Next &rarr;</a>");
                            rows.Append("</p>");
                        }
                    }
                }

                body = "<h1>User Management</h1>"
                        + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                        + message
                        + searchForm
                        + rows.ToString()
                        + "<h2>Create Account</h2>"
                        + "<form method=\"post\" action=\"" + BasePath + "/admin/users/create\">"
                        + "<label>First name: <input type=\"text\" name=\"first_name\" required></label>"
                        + "<label>Last name: <input type=\"text\" name=\"last_name\" required></label>"
                        + "<label>Email: <input type=\"email\" name=\"email\"></label>"
                        + "<label>Password: <input type=\"password\" name=\"password\" minlength=\"5\" required></label>"
                        + "<label>Confirm password: <input type=\"password\" name=\"confirm_password\" minlength=\"5\" required></label>"
                        + "<label>Account type: <select name=\"membership_type\">" + BuildMembershipTypeOptions(AccountMembershipHelper.Resident) + "</select></label>"
                        + "<button type=\"submit\">Create account</button>"
                        + "</form>";
            }

            WritePage(request, response, PageTitle("User Management"), body);
        }

        private void HandleAdminUsersSetLevel(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Account not found.";
            string principalId = string.Empty;

            if (request.HttpMethod == "POST" && m_UserAccountService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                principalId = FormValue(form, "principal_id");
                string userLevelRaw = FormValue(form, "user_level");

                if (UUID.TryParse(principalId, out UUID principalID))
                {
                    UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);

                    // The "Unban this user" button sends this sentinel
                    // instead of a literal level, so the level it restores
                    // to is computed here from whatever was recorded right
                    // before the ban, rather than being baked into the HTML
                    // form as a hardcoded 0 - a banned admin (UserLevel 200+)
                    // would otherwise get silently downgraded to an ordinary
                    // account on unban, same bug ClearExpiredBan used to have.
                    if (account != null && userLevelRaw == "UNBAN")
                    {
                        account.UserLevel = AccountBanHelper.GetPreBanLevel(m_UserProfilesService, principalID) ?? 0;
                        message = m_UserAccountService.StoreUserAccount(account)
                                ? "User unbanned."
                                : "Failed to unban user.";
                        AccountBanHelper.SetBanExpiry(m_UserProfilesService, principalID, null);
                        AccountBanHelper.SetPreBanLevel(m_UserProfilesService, principalID, null);
                    }
                    else if (account != null && int.TryParse(userLevelRaw, out int userLevel))
                    {
                        userLevel = Math.Clamp(userLevel, DeletedUserLevel, 250);

                        // Record the level this account is about to lose,
                        // but only on the transition INTO a ban - re-banning
                        // an already-banned account (or a manual edit that
                        // happens to land on -1 again) must not clobber the
                        // real pre-ban level with -1.
                        if (userLevel == AccountBanHelper.BannedUserLevel && account.UserLevel != AccountBanHelper.BannedUserLevel)
                            AccountBanHelper.SetPreBanLevel(m_UserProfilesService, principalID, account.UserLevel);

                        account.UserLevel = userLevel;
                        message = m_UserAccountService.StoreUserAccount(account)
                                ? "User level updated."
                                : "Failed to update user level.";

                        if (userLevel == AccountBanHelper.BannedUserLevel && int.TryParse(FormValue(form, "ban_hours"), out int banHours) && banHours > 0)
                        {
                            AccountBanHelper.SetBanExpiry(m_UserProfilesService, principalID, DateTime.UtcNow.AddHours(banHours));
                            message = "User banned until " + DateTime.UtcNow.AddHours(banHours).ToString("yyyy-MM-dd HH:mm") + " UTC.";
                        }
                        else
                        {
                            // Any other level change (permanent ban, manual
                            // level edit) clears a stale expiry/pre-ban
                            // level so they can't resurrect or misapply
                            // themselves against a later, unrelated ban.
                            AccountBanHelper.SetBanExpiry(m_UserProfilesService, principalID, null);
                            AccountBanHelper.SetPreBanLevel(m_UserProfilesService, principalID, null);
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminUsersEditDetails(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_UserAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Account not found.";
            string principalId = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                principalId = FormValue(form, "principal_id");

                if (UUID.TryParse(principalId, out UUID principalID))
                {
                    UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                    if (account != null)
                    {
                        string firstName = FormValue(form, "first_name").Trim();
                        string lastName = FormValue(form, "last_name").Trim();
                        string email = FormValue(form, "email").Trim();
                        string userTitle = FormValue(form, "user_title").Trim();
                        int.TryParse(FormValue(form, "membership_type"), out int membershipType);
                        if (!AccountMembershipHelper.AllTypes.ContainsKey(membershipType))
                            membershipType = AccountMembershipHelper.GetMembershipType(account.UserFlags);

                        if (string.IsNullOrEmpty(firstName) || string.IsNullOrEmpty(lastName))
                        {
                            message = "First and last name are required.";
                        }
                        else
                        {
                            // A rename has to check for a name collision
                            // first - GetUserAccount(first,last) is how every
                            // other lookup in this file (login, search,
                            // estate/proposal name resolution) finds an
                            // account, so two accounts sharing a name would
                            // make one of them unreachable by name.
                            bool nameChanged = !string.Equals(firstName, account.FirstName, StringComparison.Ordinal)
                                    || !string.Equals(lastName, account.LastName, StringComparison.Ordinal);
                            UserAccount collision = nameChanged ? m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName) : null;

                            if (collision != null && collision.PrincipalID != principalID)
                            {
                                message = "Another account is already named \"" + firstName + " " + lastName + "\".";
                            }
                            else
                            {
                                // A membership type past CharterMember has no built-in
                                // viewer badge icon and needs UserTitle set to actually
                                // be visible in the resident's profile - auto-fill it
                                // with the type's own name if the admin left the title
                                // blank, rather than silently saving an invisible badge.
                                if (string.IsNullOrEmpty(userTitle) && AccountMembershipHelper.NeedsTitleToDisplay(membershipType))
                                    userTitle = AccountMembershipHelper.GetName(membershipType);

                                account.FirstName = firstName;
                                account.LastName = lastName;
                                account.Email = email;
                                account.UserTitle = userTitle;
                                account.UserFlags = AccountMembershipHelper.SetMembershipType(account.UserFlags, membershipType);
                                message = m_UserAccountService.StoreUserAccount(account)
                                        ? "Account details updated."
                                        : "Failed to update account details.";
                            }
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Admin-set password - distinct from HandleAdminUsersSoftDelete's
        // SCRAMBLED password (which is deliberately unrecoverable) and from
        // HandleResetPassword's self-service email-token flow. This is for
        // a resident who's locked out and can't receive/use that email -
        // the admin sets a specific new password directly and tells them
        // out of band.
        private void HandleAdminUsersResetPassword(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_UserAccountService == null || m_AuthenticationService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Account not found.";
            string principalId = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                principalId = FormValue(form, "principal_id");
                string newPassword = FormValue(form, "new_password");

                if (UUID.TryParse(principalId, out UUID principalID))
                {
                    UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                    if (account == null)
                    {
                        message = "Account not found.";
                    }
                    else if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
                    {
                        message = "New password must be at least 6 characters.";
                    }
                    else
                    {
                        message = m_AuthenticationService.SetPassword(principalID, newPassword)
                                ? "Password updated for " + account.Name + "."
                                : "Failed to update password.";
                    }
                }
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Admin-side account creation - reuses the exact same validation
        // (ValidateRegistration) and CreateUser sequence (account -> password
        // -> home region -> inventory root) as the public self-service
        // HandleRegister, just without logging the admin in as the new
        // account afterward.
        private void HandleAdminUsersCreate(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_UserAccountService == null || m_AuthenticationService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string firstName = FormValue(form, "first_name").Trim();
            string lastName = FormValue(form, "last_name").Trim();
            string email = FormValue(form, "email").Trim();
            string password = FormValue(form, "password");
            string confirmPassword = FormValue(form, "confirm_password");

            string error = ValidateRegistration(firstName, lastName, password, confirmPassword);
            if (error != null)
            {
                response.Redirect(BasePath + "/admin/users?message=" + Uri.EscapeDataString(error), HttpStatusCode.Redirect);
                return;
            }

            int.TryParse(FormValue(form, "membership_type"), out int membershipType);
            if (!AccountMembershipHelper.AllTypes.ContainsKey(membershipType))
                membershipType = AccountMembershipHelper.Resident;

            UserAccount account = new UserAccount(UUID.Zero, firstName, lastName, email);
            account.UserFlags = AccountMembershipHelper.SetMembershipType(account.UserFlags, membershipType);
            // Same visibility safeguard as the edit-details form: a type
            // past CharterMember has no built-in viewer badge and needs
            // UserTitle set to actually show in the resident's profile.
            if (AccountMembershipHelper.NeedsTitleToDisplay(membershipType))
                account.UserTitle = AccountMembershipHelper.GetName(membershipType);

            if (!m_UserAccountService.StoreUserAccount(account))
            {
                response.Redirect(BasePath + "/admin/users?message=" + Uri.EscapeDataString("Could not create that account."), HttpStatusCode.Redirect);
                return;
            }

            m_AuthenticationService.SetPassword(account.PrincipalID, password);

            if (m_GridService != null && m_GridUserService != null)
            {
                List<GridRegion> defaultRegions = m_GridService.GetDefaultRegions(UUID.Zero);
                if (defaultRegions != null && defaultRegions.Count > 0)
                {
                    GridRegion home = defaultRegions[0];
                    m_GridUserService.SetHome(account.PrincipalID.ToString(), home.RegionID, new Vector3(128, 128, 0), new Vector3(0, 1, 0));
                }
            }

            m_InventoryService?.CreateUserInventory(account.PrincipalID);

            response.Redirect(BasePath + "/admin/users?principal=" + account.PrincipalID + "&message=" + Uri.EscapeDataString("Account created."), HttpStatusCode.Redirect);
        }

        // Soft delete: scrambles the password via the same
        // IAuthenticationService.SetPassword primitive HandleResetPassword
        // already uses elsewhere in this file, then marks the account
        // DeletedUserLevel so it also fails the LLLoginService UserLevel
        // check. This is deliberately NOT a hard delete - IUserAccountService
        // has no Delete method, and hard-removing the account row would
        // orphan Inventory/Groups/Grid/Presence/Currency/Estate rows that
        // reference this PrincipalID. Recovery path: an admin un-bans the
        // account (sets level back to 0+) and walks the resident through
        // the normal forgot-password flow.
        private void HandleAdminUsersSoftDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_UserAccountService == null || m_AuthenticationService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Account not found.";
            string principalId = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                principalId = FormValue(form, "principal_id");

                if (UUID.TryParse(principalId, out UUID principalID))
                {
                    UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                    if (account != null)
                    {
                        message = SoftDeleteAccount(account);
                        m_log.InfoFormat("[WEB INTERFACE]: Admin {0} ({1}) soft-deleted account {2} ({3})",
                                session.Name, session.PrincipalID, account.Name, account.PrincipalID);
                    }
                }
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Shared by the admin-triggered soft-delete above and the self-
        // service delete-my-account page below - same scramble-then-mark-
        // Deleted mechanism either way, see the comment above this section
        // for why it's a soft delete rather than a hard one.
        private string SoftDeleteAccount(UserAccount account)
        {
            string scrambledPassword = UUID.Random().ToString() + UUID.Random().ToString();
            if (!m_AuthenticationService.SetPassword(account.PrincipalID, scrambledPassword))
                return "Failed to delete account.";

            account.UserLevel = DeletedUserLevel;
            return m_UserAccountService.StoreUserAccount(account)
                    ? "Account deleted."
                    : "Password was scrambled, but the account level could not be updated.";
        }

        // Permanent counterpart to Soft Delete above. User's own design
        // decisions (2026-08-29), same shape as the earlier Remove Simulator
        // generalization: delete the account's own rows/relationships, never
        // touch assets - a resident's uploads are shared/dedup'd grid data,
        // not something removing the account gets to decide the fate of.
        // Deletes: UserAccounts, Authentication credentials, GridUser (home/
        // last location), Friends (both directions), inventory structure,
        // avatar appearance, and currency balance/transaction/purchase
        // history. Leaves alone: Store order history (audit trail) and every
        // asset. Refuses if currently online or if they own an estate - both
        // real messes to leave behind, fail closed rather than orphan them.
        // Casts to concrete service types throughout because these Delete
        // methods are deliberately NOT part of their public service
        // interfaces - region-side remote connectors have no business
        // triggering a full account wipe, only Robust's own admin WebUI does.
        private void HandleAdminUsersRemove(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_UserAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Account not found.";
            string principalId = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                principalId = FormValue(form, "principal_id");

                if (UUID.TryParse(principalId, out UUID principalID))
                {
                    UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                    if (account == null)
                    {
                        message = "Account not found.";
                    }
                    else
                    {
                        bool online = m_GridUserService?.GetGridUserInfo(principalID.ToString())?.Online == true;
                        int estateCount = m_EstateDataService?.GetEstatesByOwner(principalID)?.Count ?? 0;

                        if (online)
                        {
                            message = account.Name + " is currently online - they must log out (or be kicked) before their account can be removed.";
                        }
                        else if (estateCount > 0)
                        {
                            message = account.Name + " owns " + estateCount + " estate(s) - reassign or delete " + (estateCount == 1 ? "it" : "them")
                                    + " first, then remove the account.";
                        }
                        else
                        {
                            if (m_FriendsService != null)
                            {
                                foreach (OpenSim.Services.Interfaces.FriendInfo f in m_FriendsService.GetFriends(principalID))
                                {
                                    m_FriendsService.Delete(principalID, f.Friend);
                                    if (UUID.TryParse(f.Friend, out UUID friendID))
                                        m_FriendsService.Delete(friendID, principalID.ToString());
                                }
                            }

                            (m_InventoryService as OpenSim.Services.InventoryService.XInventoryService)?.DeleteAllUserInventory(principalID);
                            (m_CurrencyService as OpenSim.Services.CurrencyService.CurrencyService)?.DeleteAccountData(principalID);
                            (m_GridUserService as OpenSim.Services.UserAccountService.GridUserService)?.DeleteGridUserInfo(principalID.ToString());
                            (m_AuthenticationService as OpenSim.Services.AuthenticationService.AuthenticationServiceBase)?.DeleteAuthInfo(principalID);
                            m_AvatarService?.ResetAvatar(principalID);
                            (m_UserAccountService as OpenSim.Services.UserAccountService.UserAccountService)?.DeleteUserAccount(principalID);
                            m_UserAccountService.InvalidateCache(principalID);

                            m_log.InfoFormat("[WEB INTERFACE]: Admin {0} ({1}) permanently removed account {2} ({3})",
                                    session.Name, session.PrincipalID, account.Name, account.PrincipalID);
                            message = account.Name + " removed. Assets they uploaded were left untouched.";
                            principalId = string.Empty;
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // "Login as user" - CreateSession just needs the target's
        // principal/name/admin-flag, exactly what HandleLogin already
        // builds after a real password check - this skips the password
        // check since the ACTING admin is already authenticated as an
        // admin.
        private void HandleAdminUsersLoginAs(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_UserAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (!UUID.TryParse(FormValue(form, "principal_id"), out UUID targetId))
            {
                response.Redirect(BasePath + "/admin/users", HttpStatusCode.Redirect);
                return;
            }

            UserAccount target = m_UserAccountService.GetUserAccount(UUID.Zero, targetId);
            if (target == null)
            {
                response.Redirect(BasePath + "/admin/users", HttpStatusCode.Redirect);
                return;
            }

            m_log.InfoFormat("[WEB INTERFACE]: Admin {0} ({1}) logged in as {2} ({3})",
                    session.Name, session.PrincipalID, target.Name, target.PrincipalID);

            // The TARGET avatar's own WebAccountID, not the impersonating
            // admin's - otherwise the resulting session would show the
            // admin's own avatar list/activity log while wearing a
            // resident's identity, a real data leak between accounts.
            WebAccountAvatarLink targetLink = m_WebAccountService?.GetLinkForAvatar(target.PrincipalID);
            UUID targetWebAccountId = targetLink?.WebAccountID ?? UUID.Zero;

            string token = CreateSession(target.PrincipalID, target.Name, target.UserLevel >= 200, targetWebAccountId);
            SetSessionCookie(response, token);
            response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
        }

        // IGridUserService already tracks Online + LastRegionID for every
        // resident (used elsewhere in this file for the public profile's
        // "last seen" line) - reusing it here is what makes Kick/Message
        // buildable without any new tracking of our own.
        private GridRegion FindOnlineUserRegion(UUID principalID)
        {
            if (m_GridUserService == null || m_GridService == null)
                return null;

            GridUserInfo info = m_GridUserService.GetGridUserInfo(principalID.ToString());
            if (info == null || !info.Online || info.LastRegionID == UUID.Zero)
                return null;

            return m_GridService.GetRegionByUUID(UUID.Zero, info.LastRegionID);
        }

        // Kick/Message reuse the exact same region-side channel as the free-
        // form Region Console (task #26, RunRegionConsoleCommand above) -
        // they just build the command string server-side from the target
        // account + the admin's message text, instead of requiring the
        // admin to know the "kick user"/"message user" console syntax and
        // pick the right region by hand.
        private void HandleAdminUsersKick(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || string.IsNullOrEmpty(m_webConsoleSecret) || m_UserAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string principalId = FormValue(form, "principal_id");
            string message = "Account not found.";

            if (UUID.TryParse(principalId, out UUID principalID))
            {
                UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                GridRegion region = account != null ? FindOnlineUserRegion(principalID) : null;

                if (account == null)
                    message = "Account not found.";
                else if (region == null || string.IsNullOrEmpty(region.ServerURI))
                    message = account.Name + " does not appear to be online right now.";
                else
                {
                    string command = "kick user " + account.FirstName + " " + account.LastName + " Kicked by a grid administrator.";
                    string output = RunRegionConsoleCommand(region, command);
                    message = "Kick sent to " + region.RegionName + ": " + output;

                    m_log.InfoFormat("[WEB INTERFACE]: Admin {0} ({1}) kicked {2} ({3}) from {4}",
                            session.Name, session.PrincipalID, account.Name, account.PrincipalID, region.RegionName);
                }
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminUsersMessage(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || string.IsNullOrEmpty(m_webConsoleSecret) || m_UserAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string principalId = FormValue(form, "principal_id");
            string messageText = FormValue(form, "message_text");
            string message;

            if (string.IsNullOrWhiteSpace(messageText))
            {
                message = "Enter a message to send.";
            }
            else if (UUID.TryParse(principalId, out UUID principalID))
            {
                UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                GridRegion region = account != null ? FindOnlineUserRegion(principalID) : null;

                if (account == null)
                    message = "Account not found.";
                else if (region == null || string.IsNullOrEmpty(region.ServerURI))
                    message = account.Name + " does not appear to be online right now.";
                else
                {
                    string command = "message user " + account.FirstName + " " + account.LastName + " " + messageText;
                    string output = RunRegionConsoleCommand(region, command);
                    message = "Message sent to " + region.RegionName + ": " + output;

                    m_log.InfoFormat("[WEB INTERFACE]: Admin {0} ({1}) messaged {2} ({3}) in {4}",
                            session.Name, session.PrincipalID, account.Name, account.PrincipalID, region.RegionName);
                }
            }
            else
            {
                message = "Account not found.";
            }

            response.Redirect(BasePath + "/admin/users?principal=" + Uri.EscapeDataString(principalId) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Last page from the original WhiteCore-Dev comparison list
        // (region/user/estate/abuse-report/currency manager - see
        // PROJECT_LOG.md's 2026-08-09 WebInterface correction). EstateSettings
        // has a very large surface (bans, groups, experience lists, terrain
        // flags, ...) - this page edits the fields an admin most commonly
        // needs day to day (name, owner, the most consequential access
        // toggles) plus the four access-control lists (managers, allowed
        // residents, bans, allowed groups) via HandleAdminEstatesListAction
        // below. Experience lists and terrain flags remain out of scope -
        // those belong on the in-world Estate/Region floater, not a basic
        // web console.
        //
        // Also serves the resident-facing /myestates route (same method,
        // same URL space) - matches WhiteCore-Dev's own estate_manager.html/
        // estate_edit.html, which are the SAME pages for admins and estate
        // owners alike (RequiresAdminAuthentication => false, filtered to
        // GetEstates(user.PrincipalID) instead of GetEstatesAll() for a non-
        // admin). CanManageEstate below is the shared ownership-or-admin
        // check every estate action handler uses.
        private bool CanManageEstate(WebSession session, EstateSettings estate)
        {
            return session.IsAdmin || estate.EstateOwner == session.PrincipalID;
        }

        // Grid-wide admin Groups oversight - real gap the 2026-08-11 fresh
        // audit found: Confluence only ever showed a resident's OWN group
        // memberships (their public profile), with no admin view of every
        // group on the grid at all. See IGroupsSearchProvider's
        // GetAllGroups/DeleteGroup/UpdateGroupFlags comments for the real
        // ShowInList-only visibility limit (hidden groups aren't listed
        // here) and the deliberate no-membership-check admin-override
        // design (an admin moderating a group has no reason to also be a
        // member of it).
        private void HandleAdminGroups(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Groups Management"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_GroupsSearchService == null)
            {
                WritePage(request, response, PageTitle("Groups Management"),
                        "<h1>Groups Management</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Groups service is not available.</p>");
                return;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            List<GroupOverviewData> groups = m_GroupsSearchService.GetAllGroups(UUID.Zero.ToString());

            StringBuilder rows = new StringBuilder();
            if (groups.Count == 0)
            {
                rows.Append("<p>No groups found.</p>");
            }
            else
            {
                rows.Append("<table><tr><th>Name</th><th>Members</th><th>Roles</th><th>Flags</th><th></th></tr>");
                foreach (GroupOverviewData group in groups)
                {
                    rows.Append("<tr><td>").Append(Html(group.GroupName)).Append("</td>");
                    rows.Append("<td>").Append(group.MemberCount).Append("</td>");
                    rows.Append("<td>").Append(group.RoleCount).Append("</td>");

                    rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/groups/update\">");
                    rows.Append("<input type=\"hidden\" name=\"group_id\" value=\"").Append(group.GroupID).Append("\">");
                    rows.Append("<label><input type=\"checkbox\" name=\"show_in_list\"").Append(group.ShowInList ? " checked" : "").Append("> Show in list</label> ");
                    rows.Append("<label><input type=\"checkbox\" name=\"open_enrollment\"").Append(group.OpenEnrollment ? " checked" : "").Append("> Open enrollment</label> ");
                    rows.Append("<label><input type=\"checkbox\" name=\"allow_publish\"").Append(group.AllowPublish ? " checked" : "").Append("> Allow publish</label> ");
                    rows.Append("<label><input type=\"checkbox\" name=\"mature_publish\"").Append(group.MaturePublish ? " checked" : "").Append("> Mature</label> ");
                    rows.Append("<button type=\"submit\">Save</button>");
                    rows.Append("</form></td>");

                    rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/groups/delete\" onsubmit=\"return confirm('Delete ")
                            .Append(Html(group.GroupName).Replace("'", "\\'")).Append("? This cannot be undone.');\">");
                    rows.Append("<input type=\"hidden\" name=\"group_id\" value=\"").Append(group.GroupID).Append("\">");
                    rows.Append("<button type=\"submit\">Delete</button>");
                    rows.Append("</form></td></tr>");
                }
                rows.Append("</table>");
            }

            string body = "<h1>Groups Management</h1>"
                    + "<p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                    + "<p class=\"news-meta\">Only lists groups with \"Show in search\" enabled - this connector has no admin channel to surface hidden groups yet.</p>"
                    + message
                    + rows.ToString();

            WritePage(request, response, PageTitle("Groups Management"), body);
        }

        private void HandleAdminGroupsUpdate(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_GroupsSearchService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Group not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "group_id"), out UUID groupID))
                {
                    m_GroupsSearchService.UpdateGroupFlags(UUID.Zero.ToString(), groupID,
                            FormValue(form, "show_in_list") == "on",
                            FormValue(form, "open_enrollment") == "on",
                            FormValue(form, "allow_publish") == "on",
                            FormValue(form, "mature_publish") == "on");
                    message = "Group updated.";
                }
            }

            response.Redirect(BasePath + "/admin/groups?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminGroupsDelete(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_GroupsSearchService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Group not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "group_id"), out UUID groupID))
                {
                    message = m_GroupsSearchService.DeleteGroup(UUID.Zero.ToString(), groupID)
                            ? "Group deleted."
                            : "Failed to delete group.";
                }
            }

            response.Redirect(BasePath + "/admin/groups?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminEstates(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (m_EstateDataService == null)
            {
                WritePage(request, response, PageTitle("Estate Management"),
                        "<h1>Estate Management</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Estate service is not available.</p>");
                return;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string idParam = request.QueryString.Get("id");
            string body;

            if (!string.IsNullOrEmpty(idParam) && int.TryParse(idParam, out int estateID))
            {
                EstateSettings estate = m_EstateDataService.LoadEstateSettings(estateID);
                if (estate == null || estate.EstateID == 0)
                {
                    body = "<h1>Estate Management</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/estates\">Back to list</a></p>"
                            + "<p>Estate not found.</p>";
                }
                else if (!CanManageEstate(session, estate))
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    WritePage(request, response, PageTitle("Estate Management"), "<h1>Not authorized</h1><p>You don't manage this estate.</p>");
                    return;
                }
                else
                {
                    UserAccount owner = m_UserAccountService?.GetUserAccount(UUID.Zero, estate.EstateOwner);
                    string ownerName = owner != null ? owner.Name : estate.EstateOwner.ToString();

                    // Unlisted checkbox per region - self-service opt-out
                    // of public listing (World Map, region tables, grid
                    // stat counts), same self-service pattern this page
                    // already extends to estate owners (not just admins)
                    // for everything else here. GetRegionFlags is a real
                    // per-region call, not derived from GridRegion itself -
                    // see IGridService.GetRegionFlags's own doc comment for
                    // why (flags aren't returned by the region-listing
                    // calls, only this dedicated one).
                    StringBuilder regionRows = new StringBuilder();
                    foreach (UUID regionID in m_EstateDataService.GetRegions(estateID))
                    {
                        GridRegion region = m_GridService?.GetRegionByUUID(UUID.Zero, regionID);
                        string regionName = region != null ? region.RegionName : regionID.ToString();
                        bool isUnlisted = m_GridService != null
                                && (m_GridService.GetRegionFlags(UUID.Zero, regionID) & (int)OpenSim.Framework.RegionFlags.Unlisted) != 0;
                        regionRows.Append("<li><label><input type=\"checkbox\" name=\"unlisted_")
                                .Append(regionID).Append("\"").Append(isUnlisted ? " checked" : "").Append("> ")
                                .Append(Html(regionName)).Append(" <span class=\"news-meta\">- hide from the public World Map, ")
                                .Append("region tables and grid stat counts (still reachable by name or direct link)</span></label></li>");
                    }

                    body = "<h1>" + Html(estate.EstateName) + "</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/estates\">Back to list</a></p>"
                            + message
                            + "<h2>Regions in this estate</h2>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/estates/region-visibility\">"
                            + "<input type=\"hidden\" name=\"estate_id\" value=\"" + estate.EstateID + "\">"
                            + "<ul>" + regionRows.ToString() + "</ul>"
                            + "<button type=\"submit\">Save Visibility</button>"
                            + "</form>"
                            + "<h2>Settings</h2>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/estates/update\">"
                            + "<input type=\"hidden\" name=\"estate_id\" value=\"" + estate.EstateID + "\">"
                            + "<label>Estate name: <input type=\"text\" name=\"estate_name\" value=\"" + Html(estate.EstateName) + "\" required></label>"
                            + (session.IsAdmin
                                ? "<label>Owner (First Last): <input type=\"text\" name=\"owner_name\" value=\"" + Html(ownerName) + "\" required></label>"
                                : "<p class=\"news-meta\">Owner: " + Html(ownerName) + " (only a grid admin can transfer ownership)</p>")
                            + "<label><input type=\"checkbox\" name=\"public_access\"" + (estate.PublicAccess ? " checked" : "") + "> Public access</label>"
                            + "<label><input type=\"checkbox\" name=\"allow_voice\"" + (estate.AllowVoice ? " checked" : "") + "> Allow voice</label>"
                            + "<label><input type=\"checkbox\" name=\"allow_direct_teleport\"" + (estate.AllowDirectTeleport ? " checked" : "") + "> Allow direct teleport</label>"
                            + "<label><input type=\"checkbox\" name=\"deny_anonymous\"" + (estate.DenyAnonymous ? " checked" : "") + "> Deny anonymous (non-identified) visitors</label>"
                            + "<label><input type=\"checkbox\" name=\"deny_minors\"" + (estate.DenyMinors ? " checked" : "") + "> Deny minors</label>"
                            + "<label><input type=\"checkbox\" name=\"deny_new_accounts\"" + (estate.DenyNewAccounts ? " checked" : "") + "> Deny brand-new accounts (an extra layer of content protection - blocks a throwaway account made just to walk in and copy something; the grid's new-account threshold is configured in NewAccountThresholdDays)</label>"
                            + "<label><input type=\"checkbox\" name=\"allow_access_override\"" + (!estate.TaxFree ? " checked" : "") + "> Allow parcels in this estate to override its access rules</label>"
                            + "<label>Land price per square meter: <input type=\"number\" name=\"price_per_meter\" value=\"" + estate.PricePerMeter + "\" min=\"0\" required></label>"
                            + "<button type=\"submit\">Save changes</button>"
                            + "</form>"
                            + AppendEstatePrincipalList("managers", "Estate Managers", "Can manage this estate's settings and access lists in-world.", estate, estate.EstateManagers)
                            + AppendEstatePrincipalList("access", "Allowed Residents", "Only meaningful when Public access is off - lets specific residents in anyway.", estate, estate.EstateAccess)
                            + AppendEstatePrincipalList("bans", "Banned Residents", "Blocked from entering any region on this estate, regardless of Public access.", estate, Array.ConvertAll(estate.EstateBans, b => b.BannedUserID))
                            + AppendEstateGroupList(estate);
                }
            }
            else
            {
                List<int> estateIDs = session.IsAdmin ? m_EstateDataService.GetEstatesAll() : m_EstateDataService.GetEstatesByOwner(session.PrincipalID);

                StringBuilder rows = new StringBuilder();
                if (estateIDs.Count == 0)
                {
                    rows.Append("<p>").Append(session.IsAdmin ? "No estates exist on this grid yet." : "You don't own any estates on this grid.").Append("</p>");
                }
                else
                {
                    // Reference's estate-list table (user/estate_manager.html)
                    // surfaces Public Access/Allow Voice/Tax Free/Allow Direct
                    // Teleport directly as columns, at-a-glance without
                    // opening each estate - real gap, this table only had
                    // Estate/Owner/Regions.
                    rows.Append("<table><tr><th>Estate</th>").Append(session.IsAdmin ? "<th>Owner</th>" : "")
                            .Append("<th>Public</th><th>Voice</th><th>Tax Free</th><th>Direct TP</th><th>Regions</th></tr>");
                    foreach (int existingEstateID in estateIDs)
                    {
                        EstateSettings estate = m_EstateDataService.LoadEstateSettings(existingEstateID);
                        if (estate == null)
                            continue;

                        int regionCount = m_EstateDataService.GetRegions(existingEstateID).Count;

                        rows.Append("<tr><td><a href=\"").Append(BasePath).Append("/admin/estates?id=").Append(existingEstateID).Append("\">")
                                .Append(Html(estate.EstateName)).Append("</a></td>");
                        if (session.IsAdmin)
                        {
                            UserAccount owner = m_UserAccountService?.GetUserAccount(UUID.Zero, estate.EstateOwner);
                            string ownerName = owner != null ? owner.Name : estate.EstateOwner.ToString();
                            rows.Append("<td>").Append(Html(ownerName)).Append("</td>");
                        }
                        rows.Append("<td>").Append(estate.PublicAccess ? "Yes" : "No").Append("</td>")
                                .Append("<td>").Append(estate.AllowVoice ? "Yes" : "No").Append("</td>")
                                .Append("<td>").Append(estate.TaxFree ? "Yes" : "No").Append("</td>")
                                .Append("<td>").Append(estate.AllowDirectTeleport ? "Yes" : "No").Append("</td>")
                                .Append("<td>").Append(regionCount).Append("</td></tr>");
                    }
                    rows.Append("</table>");
                }

                body = "<h1>Estate Management</h1>"
                        + "<p><a href=\"" + BasePath + (session.IsAdmin ? "/admin" : "/dashboard") + "\">Back to " + (session.IsAdmin ? "admin" : "dashboard") + "</a></p>"
                        + message
                        + rows.ToString()
                        + (session.IsAdmin
                            ? "<h2>Create Estate</h2>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/estates/create\">"
                            + "<label>Estate name: <input type=\"text\" name=\"estate_name\" required></label>"
                            + "<label>Owner (First Last): <input type=\"text\" name=\"owner_name\" required></label>"
                            + "<button type=\"submit\">Create</button>"
                            + "</form>"
                            : string.Empty);
            }

            WritePage(request, response, PageTitle("Estate Management"), body);
        }

        private void HandleAdminEstatesUpdate(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Estate not found.";
            string estateIdParam = string.Empty;

            if (request.HttpMethod == "POST" && m_EstateDataService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                estateIdParam = FormValue(form, "estate_id");

                if (int.TryParse(estateIdParam, out int estateID))
                {
                    EstateSettings estate = m_EstateDataService.LoadEstateSettings(estateID);
                    if (estate == null || estate.EstateID == 0)
                    {
                        message = "Estate not found.";
                    }
                    else if (!CanManageEstate(session, estate))
                    {
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }
                    else
                    {
                        // Reassigning the owner is deliberately admin-only -
                        // an estate owner managing their own estate has no
                        // business handing it to someone else through this
                        // form (that's a much more sensitive action than
                        // toggling access flags).
                        UserAccount owner = null;
                        if (session.IsAdmin)
                        {
                            string ownerName = FormValue(form, "owner_name").Trim();
                            string[] nameParts = ownerName.Split(' ', 2);
                            owner = nameParts.Length == 2 && m_UserAccountService != null
                                    ? m_UserAccountService.GetUserAccount(UUID.Zero, nameParts[0], nameParts[1])
                                    : null;

                            if (!string.IsNullOrEmpty(ownerName) && owner == null)
                            {
                                message = "Owner account \"" + ownerName + "\" not found - no changes were saved.";
                                response.Redirect(BasePath + "/admin/estates?id=" + Uri.EscapeDataString(estateIdParam) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
                                return;
                            }
                        }

                        estate.EstateName = FormValue(form, "estate_name");
                        if (owner != null)
                            estate.EstateOwner = owner.PrincipalID;
                        estate.PublicAccess = FormValue(form, "public_access") == "on";
                        estate.AllowVoice = FormValue(form, "allow_voice") == "on";
                        estate.AllowDirectTeleport = FormValue(form, "allow_direct_teleport") == "on";
                        estate.DenyAnonymous = FormValue(form, "deny_anonymous") == "on";
                        estate.DenyMinors = FormValue(form, "deny_minors") == "on";
                        estate.DenyNewAccounts = FormValue(form, "deny_new_accounts") == "on";
                        // TaxFree is the legacy DB column name - its actual
                        // meaning today is !AllowAccessOverride (see
                        // EstateSettings.cs), so the checkbox and the stored
                        // value are deliberately inverted from each other.
                        estate.TaxFree = FormValue(form, "allow_access_override") != "on";
                        if (int.TryParse(FormValue(form, "price_per_meter"), out int pricePerMeter))
                            estate.PricePerMeter = Math.Max(0, pricePerMeter);

                        m_EstateDataService.StoreEstateSettings(estate);
                        message = "Estate settings updated.";
                    }
                }
            }

            response.Redirect(BasePath + "/admin/estates?id=" + Uri.EscapeDataString(estateIdParam) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Self-service "Unlisted" toggle - see HandleAdminEstates' region
        // checkbox list and RegionFlags.Unlisted's own comment. One
        // GetRegionFlags + SetRegionFlags round-trip per region in this
        // estate (not a bulk call - IGridService has no bulk flags
        // primitive, and an estate's own region count is small enough
        // that this isn't worth adding one for).
        private void HandleAdminEstatesRegionVisibility(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Estate not found.";
            string estateIdParam = string.Empty;

            if (request.HttpMethod == "POST" && m_EstateDataService != null && m_GridService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                estateIdParam = FormValue(form, "estate_id");

                if (int.TryParse(estateIdParam, out int estateID))
                {
                    EstateSettings estate = m_EstateDataService.LoadEstateSettings(estateID);
                    if (estate == null || estate.EstateID == 0)
                    {
                        message = "Estate not found.";
                    }
                    else if (!CanManageEstate(session, estate))
                    {
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }
                    else
                    {
                        int updated = 0;
                        foreach (UUID regionID in m_EstateDataService.GetRegions(estateID))
                        {
                            bool wantUnlisted = !string.IsNullOrEmpty(FormValue(form, "unlisted_" + regionID));
                            int currentFlags = m_GridService.GetRegionFlags(UUID.Zero, regionID);
                            if (currentFlags == -1)
                                continue;

                            bool isUnlisted = (currentFlags & (int)OpenSim.Framework.RegionFlags.Unlisted) != 0;
                            if (wantUnlisted == isUnlisted)
                                continue;

                            int newFlags = wantUnlisted
                                    ? currentFlags | (int)OpenSim.Framework.RegionFlags.Unlisted
                                    : currentFlags & ~(int)OpenSim.Framework.RegionFlags.Unlisted;
                            if (m_GridService.SetRegionFlags(UUID.Zero, regionID, newFlags))
                                updated++;
                        }
                        message = updated > 0
                                ? "Visibility updated for " + updated + " region" + (updated == 1 ? "" : "s") + "."
                                : "No changes made.";
                    }
                }
            }

            response.Redirect(BasePath + "/admin/estates?id=" + Uri.EscapeDataString(estateIdParam) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Admin-only: creates a brand new estate with no regions attached yet
        // (regions get linked to it separately, same as stock OpenSim/
        // WhiteCore-Dev - LinkRegion isn't exposed here since region-to-
        // estate assignment normally happens at region setup time, not
        // after the fact from a web form).
        private void HandleAdminEstatesCreate(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_EstateDataService == null || m_UserAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string estateName = FormValue(form, "estate_name").Trim();
            string ownerName = FormValue(form, "owner_name").Trim();
            string message;

            string[] nameParts = ownerName.Split(' ', 2);
            UserAccount owner = nameParts.Length == 2 ? m_UserAccountService.GetUserAccount(UUID.Zero, nameParts[0], nameParts[1]) : null;

            if (string.IsNullOrEmpty(estateName))
            {
                message = "Enter an estate name.";
            }
            else if (owner == null)
            {
                message = "Owner account \"" + ownerName + "\" not found.";
            }
            else
            {
                EstateSettings estate = m_EstateDataService.CreateNewEstate(0);
                estate.EstateName = estateName;
                estate.EstateOwner = owner.PrincipalID;
                m_EstateDataService.StoreEstateSettings(estate);
                message = estate.EstateID > 0 ? "Estate \"" + estateName + "\" created." : "Failed to create estate.";
            }

            response.Redirect(BasePath + "/admin/estates?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Renders one of the three UUID-list sections (Managers/Access/Bans)
        // on the estate detail page - same Add-by-name/Remove-button shape
        // each time, just backed by a different EstateSettings array and a
        // different pair of Add*/Remove* helpers (wired up in
        // HandleAdminEstatesListAction below).
        private string AppendEstatePrincipalList(string listType, string heading, string description, EstateSettings estate, UUID[] members)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>").Append(Html(heading)).Append("</h2>");
            sb.Append("<p class=\"news-meta\">").Append(Html(description)).Append("</p>");

            if (members.Length == 0)
            {
                sb.Append("<p>(none)</p>");
            }
            else
            {
                sb.Append("<ul>");
                foreach (UUID memberID in members)
                {
                    UserAccount member = m_UserAccountService?.GetUserAccount(UUID.Zero, memberID);
                    string memberName = member != null ? member.Name : memberID.ToString();

                    sb.Append("<li>").Append(Html(memberName))
                            .Append(" <form style=\"display:inline\" method=\"post\" action=\"").Append(BasePath).Append("/admin/estates/").Append(listType).Append("\">")
                            .Append("<input type=\"hidden\" name=\"estate_id\" value=\"").Append(estate.EstateID).Append("\">")
                            .Append("<input type=\"hidden\" name=\"action\" value=\"remove\">")
                            .Append("<input type=\"hidden\" name=\"name\" value=\"").Append(Html(memberName)).Append("\">")
                            .Append("<button type=\"submit\">Remove</button>")
                            .Append("</form></li>");
                }
                sb.Append("</ul>");
            }

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/estates/").Append(listType).Append("\">")
                    .Append("<input type=\"hidden\" name=\"estate_id\" value=\"").Append(estate.EstateID).Append("\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"add\">")
                    .Append("<label>Resident (First Last): <input type=\"text\" name=\"name\" required></label> ")
                    .Append("<button type=\"submit\">Add</button>")
                    .Append("</form>");

            return sb.ToString();
        }

        // Groups have no reverse UUID-to-name lookup available today
        // (IGroupsSearchProvider only supports FindGroups-by-name-substring,
        // see ISearchService's per-region maturity search work) - shown as
        // raw group IDs rather than half-faking a name.
        private string AppendEstateGroupList(EstateSettings estate)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h2>Allowed Groups</h2>");
            sb.Append("<p class=\"news-meta\">Only meaningful when Public access is off - lets members of these groups in anyway.</p>");

            if (estate.EstateGroups.Length == 0)
            {
                sb.Append("<p>(none)</p>");
            }
            else
            {
                sb.Append("<ul>");
                foreach (UUID groupID in estate.EstateGroups)
                {
                    sb.Append("<li>").Append(groupID)
                            .Append(" <form style=\"display:inline\" method=\"post\" action=\"").Append(BasePath).Append("/admin/estates/groups\">")
                            .Append("<input type=\"hidden\" name=\"estate_id\" value=\"").Append(estate.EstateID).Append("\">")
                            .Append("<input type=\"hidden\" name=\"action\" value=\"remove\">")
                            .Append("<input type=\"hidden\" name=\"group_id\" value=\"").Append(groupID).Append("\">")
                            .Append("<button type=\"submit\">Remove</button>")
                            .Append("</form></li>");
                }
                sb.Append("</ul>");
            }

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/estates/groups\">")
                    .Append("<input type=\"hidden\" name=\"estate_id\" value=\"").Append(estate.EstateID).Append("\">")
                    .Append("<input type=\"hidden\" name=\"action\" value=\"add\">")
                    .Append("<label>Group name: <input type=\"text\" name=\"name\" required></label> ")
                    .Append("<button type=\"submit\">Add</button>")
                    .Append("</form>");

            return sb.ToString();
        }

        // Shared handler for the Managers/Access/Bans Add-remove forms above -
        // resolves "First Last" to a UUID via the same pattern
        // HandleAdminEstatesUpdate already uses for the owner field, then
        // calls the matching EstateSettings Add*/Remove* helper (all three
        // already exist and already enforce the game's normal list-size
        // limits - see EstateSettings.cs).
        private void HandleAdminEstatesListAction(IOSHttpRequest request, IOSHttpResponse response, string listType)
        {
            WebSession session = GetSession(request);
            if (session == null || m_EstateDataService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Estate not found.";
            string estateIdParam = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                estateIdParam = FormValue(form, "estate_id");
                string action = FormValue(form, "action");
                string name = FormValue(form, "name").Trim();

                if (int.TryParse(estateIdParam, out int estateID))
                {
                    EstateSettings estate = m_EstateDataService.LoadEstateSettings(estateID);
                    if (estate == null || estate.EstateID == 0)
                    {
                        message = "Estate not found.";
                    }
                    else if (!CanManageEstate(session, estate))
                    {
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }
                    else
                    {
                        string[] nameParts = name.Split(' ', 2);
                        UserAccount target = nameParts.Length == 2 && m_UserAccountService != null
                                ? m_UserAccountService.GetUserAccount(UUID.Zero, nameParts[0], nameParts[1])
                                : null;

                        if (target == null)
                        {
                            message = "Resident \"" + name + "\" not found.";
                        }
                        else
                        {
                            switch (listType)
                            {
                                case "managers":
                                    if (action == "add") estate.AddEstateManager(target.PrincipalID); else estate.RemoveEstateManager(target.PrincipalID);
                                    break;
                                case "access":
                                    if (action == "add") estate.AddEstateUser(target.PrincipalID); else estate.RemoveEstateUser(target.PrincipalID);
                                    break;
                                case "bans":
                                    if (action == "add") estate.AddBan(new EstateBan { EstateID = (uint)estate.EstateID, BannedUserID = target.PrincipalID }); else estate.RemoveBan(target.PrincipalID);
                                    break;
                            }

                            m_EstateDataService.StoreEstateSettings(estate);
                            message = target.Name + (action == "add" ? " added." : " removed.");
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/estates?id=" + Uri.EscapeDataString(estateIdParam) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminEstatesGroups(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || m_EstateDataService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Estate not found.";
            string estateIdParam = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                estateIdParam = FormValue(form, "estate_id");
                string action = FormValue(form, "action");

                if (int.TryParse(estateIdParam, out int estateID))
                {
                    EstateSettings estate = m_EstateDataService.LoadEstateSettings(estateID);
                    if (estate == null || estate.EstateID == 0)
                    {
                        message = "Estate not found.";
                    }
                    else if (!CanManageEstate(session, estate))
                    {
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                        return;
                    }
                    else if (action == "remove" && UUID.TryParse(FormValue(form, "group_id"), out UUID removeGroupID))
                    {
                        estate.RemoveEstateGroup(removeGroupID);
                        m_EstateDataService.StoreEstateSettings(estate);
                        message = "Group removed.";
                    }
                    else if (action == "add")
                    {
                        string groupName = FormValue(form, "name").Trim();
                        if (m_GroupsSearchService == null)
                        {
                            message = "Groups service is not available.";
                        }
                        else
                        {
                            List<DirGroupsReplyData> matches = m_GroupsSearchService.FindGroups(UUID.Zero.ToString(), groupName);
                            bool found = false;
                            foreach (DirGroupsReplyData candidate in matches)
                            {
                                if (string.Equals(candidate.groupName, groupName, StringComparison.OrdinalIgnoreCase))
                                {
                                    estate.AddEstateGroup(candidate.groupID);
                                    m_EstateDataService.StoreEstateSettings(estate);
                                    message = candidate.groupName + " added.";
                                    found = true;
                                    break;
                                }
                            }

                            if (!found)
                                message = "Group \"" + groupName + "\" not found.";
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/estates?id=" + Uri.EscapeDataString(estateIdParam) + "&message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminHGToggle(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            if (m_RegionHGService != null && request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionID)
                        && bool.TryParse(FormValue(form, "set_open"), out bool setOpen))
                {
                    m_RegionHGService.SetRegionOpen(regionID, setOpen);
                }
            }

            response.Redirect(BasePath + "/admin/regions", HttpStatusCode.Redirect);
        }

        // Calls out to the target region's own new /MAP/Regenerate/<handle>
        // endpoint (WorldMapModule.HandleRegenerateMaptileRequest) - Robust has
        // no direct access to run this itself, the region process does. Fire-
        // and-forget: the region queues it on a background thread and answers
        // immediately, so this doesn't block the admin page on a potentially
        // slow 3D render.
        private void HandleAdminMaptileRegen(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Region not found.";

            if (request.HttpMethod == "POST" && m_GridService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
                {
                    GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionID);
                    if (region != null && !string.IsNullOrEmpty(region.ServerURI))
                    {
                        try
                        {
                            string url = region.ServerURI + "MAP/Regenerate/" + region.RegionHandle;
                            using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                            {
                                client.Timeout = TimeSpan.FromSeconds(10);
                                var result = client.PostAsync(url, new System.Net.Http.StringContent(string.Empty)).GetAwaiter().GetResult();
                                message = result.IsSuccessStatusCode
                                        ? "Maptile regeneration queued for " + region.RegionName + "."
                                        : "Region " + region.RegionName + " responded with " + (int)result.StatusCode + ".";
                            }
                        }
                        catch (Exception e)
                        {
                            message = "Could not reach " + region.RegionName + ": " + e.Message;
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Same shape as HandleAdminMaptileRegen above - Robust calls out to the
        // target region's own new /OAR/Save/<handle> endpoint
        // (ArchiverModule.HandleSaveOarHttpRequest), since only the region
        // process can actually write the archive.
        private void HandleAdminOarSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Region not found.";

            if (request.HttpMethod == "POST" && m_GridService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
                {
                    GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionID);
                    if (region != null && !string.IsNullOrEmpty(region.ServerURI))
                    {
                        try
                        {
                            string url = region.ServerURI + "OAR/Save/" + region.RegionHandle;
                            using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                            {
                                client.Timeout = TimeSpan.FromSeconds(10);
                                var result = client.PostAsync(url, new System.Net.Http.StringContent(string.Empty)).GetAwaiter().GetResult();
                                message = result.IsSuccessStatusCode
                                        ? "OAR backup queued for " + region.RegionName + "."
                                        : "Region " + region.RegionName + " responded with " + (int)result.StatusCode + ".";
                            }
                        }
                        catch (Exception e)
                        {
                            message = "Could not reach " + region.RegionName + ": " + e.Message;
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/regions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #region Self-service region owner OAR backup/restore

        // Self-service, not a grid-admin action: any logged-in user sees only
        // the region(s) they themselves are the estate owner of, via
        // IEstateDataService - distinct from /web/admin's UserLevel>=200 gate.
        // OpenSim already has its own scheduled AutoBackupModule for operator-
        // side backups; this is specifically for a user to back up or restore
        // their own content on demand.
        // Briefly merged with My Land into one page (2026-08-23), then
        // split back apart the same day after live use showed the merged
        // page getting unwieldy for any resident owning more than a
        // couple of regions. "My Regions" (sims you're the ESTATE OWNER
        // of, with backup/restart access) and "My Land" (individual
        // PARCELS you own, which you can have without owning any region,
        // and vice versa) are genuinely different kinds of ownership -
        // real, separate pages again, matching that difference.
        private void HandleMyRegions(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            StringBuilder rows = new StringBuilder();
            List<GridRegion> ownedRegions = GetRegionsOwnedBy(session.PrincipalID);

            if (m_EstateDataService == null || m_GridService == null)
            {
                rows.Append("<p>Estate/grid service is not available.</p>");
            }
            else if (ownedRegions.Count == 0)
            {
                rows.Append("<p>You are not the estate owner of any region on this grid.</p>");
            }
            else
            {
                // One compact table row per region instead of a full
                // stacked block (heading/location/button/paragraph/button)
                // repeated per region - that layout got unmanageably long
                // for any resident owning more than a couple of regions.
                // The backup/restore explanation now appears once, above
                // the table, rather than duplicated on every row.
                rows.Append("<p class=\"news-meta\">Backups save to each region's configured OAR folder on the server (same as autobackup) - ")
                    .Append("restoring from a browser-uploaded OAR isn't offered here. No OpenSim web UI this project has checked against ")
                    .Append("(including WhiteCore-Dev's) offers browser-based OAR restore either, and relaying a whole region archive through ")
                    .Append("a public-facing reverse proxy has real, environment-dependent failure modes (body size limits, read timeouts) ")
                    .Append("that a self-service page can't fix on its own. Restore an OAR from the region's own console instead.</p>");

                rows.Append("<table><tr><th>Region</th><th>Status</th><th>Location</th><th>Actions</th></tr>");
                foreach (GridRegion region in ownedRegions)
                {
                    rows.Append("<tr><td>").Append(Html(region.RegionName)).Append("</td>");
                    rows.Append("<td>").Append(RenderRegionReachabilityPill(region)).Append("</td>");
                    rows.Append("<td>(").Append(region.RegionCoordX).Append(", ").Append(region.RegionCoordY).Append(")</td>");
                    rows.Append("<td>");
                    rows.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myregions/oar-save\" style=\"margin-right:8px\">");
                    rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                    rows.Append("<button type=\"submit\">Back Up (OAR)</button></form>");
                    rows.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myregions/restart\" onsubmit=\"return confirm('Restart ")
                            .Append(Html(region.RegionName).Replace("'", "\\'")).Append("? Everyone in the region will be disconnected.');\">");
                    rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                    rows.Append("<button type=\"submit\">Restart</button></form>");
                    rows.Append("</td></tr>");
                }
                rows.Append("</table>");
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string body = "<h1><i class=\"bi bi-hdd-rack\"></i> My Regions</h1>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>"
                    + message
                    + rows.ToString();

            WritePage(request, response, PageTitle("My Regions"), body);
        }

        // GetEstatesByOwner + GetRegions rather than iterating every region on
        // the grid and checking each one's estate - scales with how much this
        // one user owns, not with total grid size.
        private List<GridRegion> GetRegionsOwnedBy(UUID principalID)
        {
            List<GridRegion> owned = new List<GridRegion>();
            if (m_EstateDataService == null || m_GridService == null)
                return owned;

            foreach (int estateID in m_EstateDataService.GetEstatesByOwner(principalID))
            {
                foreach (UUID regionID in m_EstateDataService.GetRegions(estateID))
                {
                    GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionID);
                    if (region != null)
                        owned.Add(region);
                }
            }

            return owned;
        }

        // Verifies the region is actually one of this session's owned regions
        // before doing anything - the region_id in the form is client-supplied
        // and must never be trusted on its own for either save or load.
        private GridRegion GetOwnedRegionOrNull(WebSession session, UUID regionID)
        {
            foreach (GridRegion region in GetRegionsOwnedBy(session.PrincipalID))
            {
                if (region.RegionID == regionID)
                    return region;
            }
            return null;
        }

        private void HandleMyRegionsOarSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Region not found or not owned by you.";

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
                {
                    GridRegion region = GetOwnedRegionOrNull(session, regionID);
                    if (region != null && !string.IsNullOrEmpty(region.ServerURI))
                    {
                        // Fire-and-forget, not a blocking wait for the
                        // region to respond - this message already claims
                        // "queued", but the old code actually blocked the
                        // whole request on client.PostAsync(...).GetAwaiter()
                        // .GetResult() first. Harmless for a same-host Save
                        // (empty body, fast), but the identical pattern on
                        // HandleMyRegionsOarLoad below - relaying an entire
                        // uploaded OAR file synchronously - is what produced
                        // a real live 502 (external reverse proxy read
                        // timeout) once an actual OAR was involved. Fixed
                        // both the same way for consistency.
                        string url = region.ServerURI + "OAR/Save/" + region.RegionHandle;
                        string regionName = region.RegionName;
                        Util.FireAndForget(
                            o =>
                            {
                                try
                                {
                                    using System.Net.Http.HttpClient client = new System.Net.Http.HttpClient();
                                    client.Timeout = TimeSpan.FromSeconds(30);
                                    var result = client.PostAsync(url, new System.Net.Http.StringContent(string.Empty)).GetAwaiter().GetResult();
                                    if (!result.IsSuccessStatusCode)
                                        m_log.WarnFormat("[WEBINTERFACE]: OAR save request to {0} responded with {1}.", regionName, (int)result.StatusCode);
                                }
                                catch (Exception e)
                                {
                                    m_log.WarnFormat("[WEBINTERFACE]: OAR save request to {0} failed: {1}", regionName, e.Message);
                                }
                            },
                            null, "MyRegionsOarSave", false);

                        message = "Backup queued for " + region.RegionName + ".";
                    }
                }
            }

            response.Redirect(BasePath + "/myregions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Self-service region restart - same RunRegionConsoleCommand/
        // shared-secret mechanism the admin console page uses, but region
        // ownership is verified via GetOwnedRegionOrNull first (same
        // ownership check HandleMyRegionsOarSave/Load already use) rather
        // than exposing the free-form console box to non-admins - a
        // resident can only ever send exactly "region restart 30" (see the
        // matching comment on HandleAdminRegionRestart above for why it's
        // not the shorter "restart"), and only to a region they actually
        // own.
        private void HandleMyRegionsRestart(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Region not found or not owned by you.";

            if (request.HttpMethod == "POST" && !string.IsNullOrEmpty(m_webConsoleSecret))
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionID))
                {
                    GridRegion region = GetOwnedRegionOrNull(session, regionID);
                    if (region != null && !string.IsNullOrEmpty(region.ServerURI))
                    {
                        RunRegionConsoleCommand(region, "region restart 30");
                        message = "Restart command sent to " + region.RegionName + ".";
                    }
                }
            }
            else if (string.IsNullOrEmpty(m_webConsoleSecret))
            {
                message = "Web console is not configured on this grid - region restart is unavailable.";
            }

            response.Redirect(BasePath + "/myregions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Browser-based OAR restore (upload-through-Robust-relay) used to
        // live here (HandleMyRegionsOarLoad) but was removed - see the
        // README/PROJECT_LOG entry on this. Relaying a whole region archive
        // through a public-facing reverse proxy has real, environment-
        // dependent failure modes (body size limits, read timeouts) no
        // amount of application-level fixing can fully solve, no OpenSim
        // web UI checked against (WhiteCore-Dev included) offers this
        // either, and this project doesn't know of any real grid that
        // does. OAR restore stays console-only. The hand-rolled multipart
        // parser this and the equivalent IAR handler used
        // (ParseMultipartFormData/ExtractQuotedValue/IndexOfSequence) was
        // removed along with both, since nothing else in this file used it.

        #endregion Self-service region owner OAR backup/restore

        #region Self-service parcel "Show in Search" toggle

        // Self-service, not a grid-admin action: any logged-in resident
        // manages only their own parcels' visibility in the native
        // Destination Guide/Search directory (ISearchService.SearchPlaces/
        // GetFeaturedPlaces both gate on ParcelFlags.ShowDirectory, same as
        // the viewer's own About Land > Options > "Show Place in Search"
        // checkbox controls) - previously only settable in-world, which is
        // exactly the kind of thing residents should be able to do for
        // themselves rather than needing the grid team's help. Applies live
        // via LandManagementModule's "land search enable/disable" console
        // command (RunRegionConsoleCommand, same remote-console mechanism
        // the GroupAutoInvite dashboard toggle already uses) so the live
        // in-world parcel and the database stay in sync through the normal
        // SendLandUpdateToAvatarsOverMe/TriggerLandObjectAdded path, not a
        // direct DB write that would leave the live parcel showing stale
        // state until a restart. Same ownership-verification discipline as
        // /myregions: the parcel ID in a toggle POST is client-supplied and
        // is always re-checked against GetParcelsByOwner before acting,
        // never trusted on its own - a resident can only ever toggle a
        // parcel that query actually returns for their own PrincipalID.
        // Split back out from HandleMyRegions (2026-08-23) - the merged
        // "My Land & Regions" page got unwieldy for any resident who
        // owned more than a couple of regions, per direct feedback after
        // live use. Real page again, not a redirect.
        private void HandleMyLand(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            StringBuilder rows = new StringBuilder();
            rows.Append("<p>Control whether your own parcels show up in the grid's Destination Guide and Search ")
              .Append("(Popular/Featured tabs). Once shown, ranking there is based on real traffic (dwell), not this page.</p>");

            if (m_SearchService == null)
            {
                rows.Append("<p>Search service is not available.</p>");
            }
            else
            {
                List<LandSearchRecord> parcels = m_SearchService.GetParcelsByOwner(session.PrincipalID);
                if (parcels.Count == 0)
                {
                    rows.Append("<p>You don't own any parcels on this grid.</p>");
                }
                else
                {
                    rows.Append("<table><tr><th>Parcel</th><th>Region</th><th>Traffic</th><th>Show in Search</th></tr>");
                    foreach (LandSearchRecord parcel in parcels)
                    {
                        rows.Append("<tr><td>").Append(Html(parcel.Name)).Append("</td>");
                        rows.Append("<td>").Append(Html(parcel.RegionName)).Append("</td>");
                        rows.Append("<td>").Append(((int)parcel.Dwell).ToString("N0")).Append("</td>");
                        rows.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/myland/toggle\">");
                        rows.Append("<input type=\"hidden\" name=\"parcel_id\" value=\"").Append(parcel.ParcelID).Append("\">");
                        rows.Append("<input type=\"hidden\" name=\"action\" value=\"").Append(parcel.ShowInSearch ? "disable" : "enable").Append("\">");
                        rows.Append("<button type=\"submit\"").Append(string.IsNullOrEmpty(m_webConsoleSecret) ? " disabled" : "").Append(">")
                              .Append(parcel.ShowInSearch ? "Showing - click to hide" : "Hidden - click to show").Append("</button>");
                        rows.Append("</form></td></tr>");
                    }
                    rows.Append("</table>");
                    if (string.IsNullOrEmpty(m_webConsoleSecret))
                        rows.Append("<p class=\"news-meta\">The web console is not configured, so this toggle can't be applied remotely. Set [WebConsole] SharedSecret to enable it.</p>");
                }
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string body = "<h1><i class=\"bi bi-signpost-split\"></i> My Land</h1>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>"
                    + message
                    + rows.ToString();

            WritePage(request, response, PageTitle("My Land"), body);
        }

        private void HandleMyLandToggle(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Parcel not found or not owned by you.";

            if (request.HttpMethod == "POST" && m_SearchService != null && m_GridService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "parcel_id"), out UUID parcelID))
                {
                    LandSearchRecord parcel = null;
                    foreach (LandSearchRecord candidate in m_SearchService.GetParcelsByOwner(session.PrincipalID))
                    {
                        if (candidate.ParcelID == parcelID)
                        {
                            parcel = candidate;
                            break;
                        }
                    }

                    if (parcel == null)
                    {
                        message = "Parcel not found or not owned by you.";
                    }
                    else
                    {
                        GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, parcel.RegionID);
                        if (region == null || string.IsNullOrEmpty(region.ServerURI))
                        {
                            message = "That parcel's region server address is not known to the grid service.";
                        }
                        else if (string.IsNullOrEmpty(m_webConsoleSecret))
                        {
                            message = "The web console is not configured, so this can't be applied remotely. Set [WebConsole] SharedSecret to enable this.";
                        }
                        else
                        {
                            bool show = FormValue(form, "action") != "disable";
                            RunRegionConsoleCommand(region, "land search " + (show ? "enable" : "disable") + " " + parcelID);
                            message = "\"Show in Search\" " + (show ? "enabled" : "disabled") + " for " + parcel.Name + ".";
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/myland?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #endregion Self-service parcel "Show in Search" toggle

        #region Self-service inventory owner IAR backup/restore

        // Unlike OAR (region/estate scoped), IAR is scoped to the logged-in
        // user's own account - there's no ownership lookup to do, but
        // InventoryArchiverModule's ArchiveInventory/DearchiveInventory API
        // hard-requires a password re-check (see GetUserInfo there), so these
        // forms always ask for the password again even though the user is
        // already logged in. first/last name are always taken from the
        // session, never from the form, so a user can never even attempt to
        // target a different account through this page.
        private void HandleMyInventory(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string body = "<h1>My Inventory</h1>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>"
                    + message
                    + "<h2>Back up my inventory</h2>"
                    + "<form method=\"post\" action=\"" + BasePath + "/myinventory/iar-save\">"
                    + "<label>Password: <input type=\"password\" name=\"password\" required></label> "
                    + "<button type=\"submit\">Back up my inventory (save IAR)</button>"
                    + "</form>"
                    + "<p class=\"news-meta\">Saves to a configured folder on the server. Restoring from a browser-uploaded IAR isn't offered here - same reasoning as OAR restore above, see the My Regions page.</p>";

            WritePage(request, response, PageTitle("My Inventory"), body);
        }

        // IAR isn't tied to any specific region's content the way OAR is - any
        // running region process can service the request, since
        // InventoryService/AssetService are grid-wide. Prefers the region the
        // user was last seen in/calls home (same GridUserService lookup
        // CurrencyServerConnector already uses for its balance-push callback),
        // falling back to any region on the grid that has a reachable
        // ServerURI if that comes up empty (e.g. the user has never logged
        // into the world, only the web UI).
        private string ResolveAnyRegionServerURI(UUID principalID)
        {
            if (m_GridService == null)
                return null;

            if (m_GridUserService != null)
            {
                GridUserInfo userInfo = m_GridUserService.GetGridUserInfo(principalID.ToString());
                if (userInfo != null)
                {
                    foreach (UUID candidate in new[] { userInfo.LastRegionID, userInfo.HomeRegionID })
                    {
                        if (candidate == UUID.Zero)
                            continue;
                        GridRegion candidateRegion = m_GridService.GetRegionByUUID(UUID.Zero, candidate);
                        if (candidateRegion != null && !string.IsNullOrEmpty(candidateRegion.ServerURI))
                            return candidateRegion.ServerURI;
                    }
                }
            }

            foreach (GridRegion region in m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000))
            {
                if (!string.IsNullOrEmpty(region.ServerURI))
                    return region.ServerURI;
            }

            return null;
        }

        private void HandleMyInventoryIarSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Could not reach a region to service this request.";

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string password = FormValue(form, "password");
                string[] nameParts = session.Name.Split(' ', 2);
                string firstName = nameParts.Length > 0 ? nameParts[0] : session.Name;
                string lastName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

                string serverURI = ResolveAnyRegionServerURI(session.PrincipalID);
                if (!string.IsNullOrEmpty(serverURI))
                {
                    try
                    {
                        string url = serverURI + "IAR/Save";
                        var content = new System.Net.Http.FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            { "first_name", firstName },
                            { "last_name", lastName },
                            { "password", password }
                        });
                        using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                        {
                            client.Timeout = TimeSpan.FromSeconds(15);
                            var result = client.PostAsync(url, content).GetAwaiter().GetResult();
                            message = result.StatusCode == HttpStatusCode.Forbidden
                                    ? "Incorrect password."
                                    : result.IsSuccessStatusCode
                                            ? "Inventory backup queued."
                                            : "Region responded with " + (int)result.StatusCode + ".";
                        }
                    }
                    catch (Exception e)
                    {
                        message = "Could not reach a region: " + e.Message;
                    }
                }
            }

            response.Redirect(BasePath + "/myinventory?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Browser-based IAR restore removed for the same reason as OAR
        // restore above - see that comment.

        #endregion Self-service inventory owner IAR backup/restore

        private void HandleLogin(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string firstName = FormValue(form, "first_name").Trim();
                string lastName = FormValue(form, "last_name").Trim();
                string password = FormValue(form, "password");

                string error = TryLogin(request, firstName, lastName, password, out string token);
                if (error == null)
                {
                    SetSessionCookie(response, token);
                    response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                    return;
                }

                WritePage(request, response, PageTitle("Login"), LoginForm(firstName, lastName, error));
                return;
            }

            if (GetSession(request) != null)
            {
                response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                return;
            }

            WritePage(request, response, PageTitle("Login"), LoginForm(string.Empty, string.Empty, null));
        }

        // Public self-service account creation, linked from the home page and
        // login form. Mirrors UserAccountService's own "create user" console
        // command step for step (account -> password -> home region ->
        // inventory root) rather than calling that method directly, since
        // UserAccountService.CreateUser is a concrete-class method, not part
        // of IUserAccountService - this only has the interface reference (the
        // same one already loaded for login/dashboard/user management), so it
        // recreates the same sequence using interface calls this class already
        // has loaded (IUserAccountService/IAuthenticationService/IGridService/
        // IGridUserService/IInventoryService). On success, logs the new
        // account straight in via the same TryLogin path the login form uses,
        // rather than bouncing them back to a login screen right after they
        // just typed their password once already.
        private void HandleRegister(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (GetSession(request) != null)
            {
                response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                return;
            }

            if (GetSetting("AllowRegistration", "true") != "true")
            {
                WritePage(request, response, PageTitle("Sign Up"),
                        "<h1>Sign Up</h1><p>New account registration is currently closed on this grid.</p>"
                        + "<p><a href=\"" + BasePath + "/login\">Back to login</a></p>");
                return;
            }

            // WhiteCore-Dev's own register.html lets the registering resident
            // pick which region to start in (a real <select> populated from
            // the grid's actual default regions) rather than silently always
            // picking the first one - genuinely useful on a grid with more
            // than one DefaultRegion-flagged region. GetDefaultRegions is
            // cheap (Robust already caches this) and safe to call on every
            // GET, matching the reference page always showing the picker.
            List<GridRegion> homeRegionChoices = m_GridService?.GetDefaultRegions(UUID.Zero) ?? new List<GridRegion>();

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Sign Up"), RegisterForm(string.Empty, string.Empty, string.Empty, null, homeRegionChoices, UUID.Zero));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string firstName = FormValue(form, "first_name").Trim();
            string lastName = FormValue(form, "last_name").Trim();
            string email = FormValue(form, "email").Trim();
            string password = FormValue(form, "password");
            string confirmPassword = FormValue(form, "confirm_password");
            UUID.TryParse(FormValue(form, "home_region"), out UUID selectedHomeRegionId);

            string error = ValidateRegistration(firstName, lastName, password, confirmPassword);
            // One email, one master account - same rule SL enforces at
            // signup. Checked here (not just left to AutoProvisionWebAccount
            // on the post-creation auto-login) so a resident who mistypes -
            // or a stranger who already knows this email - gets a clear
            // "log in instead" message rather than a brand-new orphaned
            // avatar with no master account.
            if (error == null && !string.IsNullOrWhiteSpace(email) && m_WebAccountService != null
                    && m_WebAccountService.GetByEmail(email) != null)
                error = "An account already exists for that email. Log in, then use Create Avatar or Import Avatar to add another avatar to it.";
            if (error != null)
            {
                WritePage(request, response, PageTitle("Sign Up"), RegisterForm(firstName, lastName, email, error, homeRegionChoices, selectedHomeRegionId));
                return;
            }

            // Public self-registrations start as Trial Member, not Resident -
            // UserAccountService's own background sweep promotes them once the
            // account is old enough (see PromoteExpiredTrialMembers). Admin-created
            // accounts (HandleAdminUsersCreate) are unaffected and still default to
            // Resident directly, since an admin manually creating an account is
            // already vetting it.
            UserAccount account = new UserAccount(UUID.Zero, firstName, lastName, email);
            account.UserFlags = AccountMembershipHelper.SetMembershipType(account.UserFlags, AccountMembershipHelper.TrialMember);
            if (!m_UserAccountService.StoreUserAccount(account))
            {
                WritePage(request, response, PageTitle("Sign Up"),
                        RegisterForm(firstName, lastName, email, "Could not create that account. Please try again.", homeRegionChoices, selectedHomeRegionId));
                return;
            }

            m_AuthenticationService.SetPassword(account.PrincipalID, password);

            if (m_GridUserService != null)
            {
                // Honor the resident's actual selection if it's one of the
                // real choices offered; fall back to the first default
                // region for a missing/tampered/stale value rather than
                // leaving the new account with no home at all.
                GridRegion home = homeRegionChoices.Find(r => r.RegionID.Equals(selectedHomeRegionId))
                        ?? (homeRegionChoices.Count > 0 ? homeRegionChoices[0] : null);
                if (home != null)
                    m_GridUserService.SetHome(account.PrincipalID.ToString(), home.RegionID, new Vector3(128, 128, 0), new Vector3(0, 1, 0));
            }

            m_InventoryService?.CreateUserInventory(account.PrincipalID);

            string loginError = TryLogin(request, firstName, lastName, password, out string token);
            if (loginError == null)
            {
                SetSessionCookie(response, token);
                response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                return;
            }

            // Account creation itself succeeded even if this one auto-login
            // attempt somehow didn't - send them to a normal login rather than
            // claiming registration failed.
            response.Redirect(BasePath + "/login?message=" + Uri.EscapeDataString("Account created. Please log in."), HttpStatusCode.Redirect);
        }

        #region Multi-avatar portal accounts

        // Create Avatar - deliberately does NOT create the UserAccount until
        // the verification link is clicked (see AvatarSignupToken's own
        // comment) - creating it immediately would let an unverified signup
        // permanently squat an avatar name and instantly show up in
        // /admin/users, search, and profile lookups.
        private void HandleCreateAvatar(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (session.WebAccountID == UUID.Zero)
            {
                WritePage(request, response, PageTitle("Create Avatar"),
                        "<h1>Create Avatar</h1><p>Add an email to your account first.</p>"
                        + "<p><a href=\"" + BasePath + "/change-email\">Add an email</a></p>");
                return;
            }
            // Without SMTP, the verification email that turns a pending
            // signup into a real avatar can never arrive - a real dead end
            // (48h then silent expiry), not just a cosmetic gap. Same
            // explicit-unavailable messaging HandleForgotPassword already
            // uses for the same underlying condition.
            if (!m_smtpEnabled)
            {
                WritePage(request, response, PageTitle("Create Avatar"),
                        "<h1>Create Avatar</h1><p>Avatar creation requires email verification, which is not available on this grid right now.</p>"
                        + "<p><a href=\"" + BasePath + "/import-avatar\">Import an existing avatar instead</a></p>");
                return;
            }

            List<GridRegion> homeRegionChoices = m_GridService?.GetDefaultRegions(UUID.Zero) ?? new List<GridRegion>();

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Create Avatar"), CreateAvatarForm(string.Empty, string.Empty, string.Empty, null, homeRegionChoices, UUID.Zero));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string firstName = FormValue(form, "first_name").Trim();
            string lastName = FormValue(form, "last_name").Trim();
            string email = FormValue(form, "email").Trim();
            string password = FormValue(form, "password");
            string confirmPassword = FormValue(form, "confirm_password");
            UUID.TryParse(FormValue(form, "home_region"), out UUID selectedHomeRegionId);

            string error = ValidateRegistration(firstName, lastName, password, confirmPassword);
            if (error == null && AvatarNamePending(firstName, lastName))
                error = "That avatar name is already taken or pending verification.";
            if (error != null)
            {
                WritePage(request, response, PageTitle("Create Avatar"), CreateAvatarForm(firstName, lastName, email, error, homeRegionChoices, selectedHomeRegionId));
                return;
            }

            GridRegion home = homeRegionChoices.Find(r => r.RegionID.Equals(selectedHomeRegionId))
                    ?? (homeRegionChoices.Count > 0 ? homeRegionChoices[0] : null);

            string token = UUID.Random().ToString();
            m_avatarSignupTokens[token] = new AvatarSignupToken
            {
                WebAccountID = session.WebAccountID,
                FirstName = firstName,
                LastName = lastName,
                Email = email,
                Password = password,
                HomeRegionID = home?.RegionID ?? UUID.Zero,
                Expires = DateTime.UtcNow.Add(AvatarSignupTokenLifetime)
            };

            string gridName = GetSetting("GridName", m_gridName);
            string verifyUrl = m_publicBaseUrl + BasePath + "/verify-avatar?token=" + token;
            SendEmail(email, gridName + " - Verify your new avatar",
                    "Hello " + firstName + ",\n\nClick the link below within the next 48 hours to finish creating your avatar '" + firstName + " " + lastName + "' on " + gridName + ":\n\n"
                    + verifyUrl + "\n\nIf you didn't request this, you can safely ignore this email.");

            m_WebAccountService?.LogActivity(new WebActivityEntry
            {
                WebAccountID = session.WebAccountID,
                EventType = "avatar_request_created",
                Description = "Requested avatar '" + firstName + " " + lastName + "'",
                IPAddress = GetClientIP(request)
            });

            WritePage(request, response, PageTitle("Create Avatar"),
                    "<h1>Check Your Email</h1><p>A verification link has been sent to " + Html(email) + ". Click it within 48 hours to finish creating your avatar.</p>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>");
        }

        // Case-insensitive check against every other currently-pending
        // (unexpired) Create Avatar request - a real UserAccount name
        // collision is already covered by ValidateRegistration.
        private bool AvatarNamePending(string firstName, string lastName)
        {
            foreach (AvatarSignupToken pending in m_avatarSignupTokens.Values)
            {
                if (pending.Expires > DateTime.UtcNow
                        && string.Equals(pending.FirstName, firstName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(pending.LastName, lastName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string CreateAvatarForm(string firstName, string lastName, string email, string error,
                List<GridRegion> homeRegionChoices, UUID selectedHomeRegionId)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            string homeRegionField = string.Empty;
            if (homeRegionChoices != null && homeRegionChoices.Count > 0)
            {
                StringBuilder options = new StringBuilder();
                foreach (GridRegion region in homeRegionChoices)
                {
                    options.Append("<option value=\"").Append(region.RegionID).Append('"')
                            .Append(region.RegionID.Equals(selectedHomeRegionId) ? " selected" : string.Empty)
                            .Append('>').Append(Html(region.RegionName)).Append("</option>");
                }
                homeRegionField = "<label>Starting region<br/><select name=\"home_region\">" + options + "</select></label><br/>";
            }

            return "<h1>Create Avatar</h1>"
                    + "<p>This creates a new avatar and links it to your portal account. A verification link will be sent to the email you enter.</p>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/create-avatar\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\" required autofocus></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\" required></label><br/>"
                    + "<label>Email<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\" required></label><br/>"
                    + "<label>Password (this avatar's in-world login password)<br/><input type=\"password\" name=\"password\" required></label><br/>"
                    + "<label>Confirm password<br/><input type=\"password\" name=\"confirm_password\" required></label><br/>"
                    + homeRegionField
                    + "<button type=\"submit\">Create avatar</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/my-avatars\">Cancel</a></p>";
        }

        private void HandleVerifyAvatar(IOSHttpRequest request, IOSHttpResponse response)
        {
            string token = request.QueryString.Get("token") ?? string.Empty;

            if (string.IsNullOrEmpty(token) || !m_avatarSignupTokens.TryGetValue(token, out AvatarSignupToken signup)
                    || signup.Expires <= DateTime.UtcNow || m_UserAccountService == null || m_AuthenticationService == null)
            {
                m_avatarSignupTokens.TryRemove(token, out _);
                WritePage(request, response, PageTitle("Verify Avatar"),
                        "<h1>Verify Avatar</h1><p class=\"error\">This verification link is invalid or has expired.</p>"
                        + "<p><a href=\"" + BasePath + "/create-avatar\">Start over</a></p>");
                return;
            }

            // Re-check the name is still free - someone else could have
            // completed a different pending request for the same name
            // while this one was waiting to be verified.
            if (m_UserAccountService.GetUserAccount(UUID.Zero, signup.FirstName, signup.LastName) != null)
            {
                m_avatarSignupTokens.TryRemove(token, out _);
                WritePage(request, response, PageTitle("Verify Avatar"),
                        "<h1>Verify Avatar</h1><p class=\"error\">That name was taken while your verification was pending. Please start over.</p>"
                        + "<p><a href=\"" + BasePath + "/create-avatar\">Start over</a></p>");
                return;
            }

            m_avatarSignupTokens.TryRemove(token, out _);

            UserAccount account = new UserAccount(UUID.Zero, signup.FirstName, signup.LastName, signup.Email);
            account.UserFlags = AccountMembershipHelper.SetMembershipType(account.UserFlags, AccountMembershipHelper.TrialMember);
            if (!m_UserAccountService.StoreUserAccount(account))
            {
                WritePage(request, response, PageTitle("Verify Avatar"),
                        "<h1>Verify Avatar</h1><p class=\"error\">Could not create that avatar. Please try again.</p>"
                        + "<p><a href=\"" + BasePath + "/create-avatar\">Start over</a></p>");
                return;
            }

            m_AuthenticationService.SetPassword(account.PrincipalID, signup.Password);
            if (m_GridUserService != null && signup.HomeRegionID != UUID.Zero)
                m_GridUserService.SetHome(account.PrincipalID.ToString(), signup.HomeRegionID, new Vector3(128, 128, 0), new Vector3(0, 1, 0));
            m_InventoryService?.CreateUserInventory(account.PrincipalID);

            if (m_WebAccountService != null)
            {
                List<WebAccountAvatarLink> existingLinks = m_WebAccountService.GetLinkedAvatars(signup.WebAccountID);
                m_WebAccountService.LinkAvatar(signup.WebAccountID, account.PrincipalID, "Created", existingLinks.Count == 0);
                m_WebAccountService.LogActivity(new WebActivityEntry
                {
                    WebAccountID = signup.WebAccountID,
                    AvatarPrincipalID = account.PrincipalID,
                    EventType = "email_verified",
                    Description = "Verified " + signup.Email + " and created avatar '" + account.Name + "'",
                    IPAddress = GetClientIP(request)
                });
            }

            response.Redirect(BasePath + "/dashboard?message=" + Uri.EscapeDataString("Avatar '" + account.Name + "' created."), HttpStatusCode.Redirect);
        }

        // Import Avatar - proves ownership of an EXISTING avatar via its
        // real in-world password, then links it. Deliberately does NOT call
        // CreateSession - this proves ownership, it doesn't log the
        // resident into that avatar. The password is used for exactly one
        // Authenticate call and goes out of scope immediately - never
        // written to any new table/column/log.
        private void HandleImportAvatar(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (session.WebAccountID == UUID.Zero)
            {
                WritePage(request, response, PageTitle("Import Avatar"),
                        "<h1>Import Avatar</h1><p>Add an email to your account first.</p>"
                        + "<p><a href=\"" + BasePath + "/change-email\">Add an email</a></p>");
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Import Avatar"), ImportAvatarForm(string.Empty, string.Empty, null));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string firstName = FormValue(form, "first_name").Trim();
            string lastName = FormValue(form, "last_name").Trim();
            string password = FormValue(form, "password");

            if (m_UserAccountService == null || m_AuthenticationService == null || m_WebAccountService == null)
            {
                WritePage(request, response, PageTitle("Import Avatar"), ImportAvatarForm(firstName, lastName, "Import is not available right now."));
                return;
            }

            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName);
            if (account == null)
            {
                WritePage(request, response, PageTitle("Import Avatar"), ImportAvatarForm(firstName, lastName, "No avatar with that name exists."));
                return;
            }

            WebAccountAvatarLink existingLink = m_WebAccountService.GetLinkForAvatar(account.PrincipalID);
            if (existingLink != null && existingLink.WebAccountID == session.WebAccountID)
            {
                WritePage(request, response, PageTitle("Import Avatar"), ImportAvatarForm(firstName, lastName, "This avatar is already linked to your account."));
                return;
            }

            // Password proof is required either way from here - both for a
            // fresh, never-linked avatar, and (see below) before absorbing
            // another account, since proving you know this avatar's real
            // in-world password is exactly the same proof-of-ownership
            // Import Avatar already relies on for the simple case.
            string authToken = m_AuthenticationService.Authenticate(account.PrincipalID, Util.Md5Hash(password), 30);
            if (string.IsNullOrEmpty(authToken))
            {
                WritePage(request, response, PageTitle("Import Avatar"), ImportAvatarForm(firstName, lastName, "Incorrect password."));
                return;
            }

            if (existingLink != null)
            {
                // This avatar already belongs to a DIFFERENT master account.
                // Only safe to absorb as a one-click self-service action if
                // that account is a solo account (this avatar is the only
                // one on it) - merging two already-multi-avatar accounts
                // raises harder questions (whose activity history/avatar
                // order "wins") that a support person should make a call
                // on, not this form.
                List<WebAccountAvatarLink> otherAccountLinks = m_WebAccountService.GetLinkedAvatars(existingLink.WebAccountID);
                if (otherAccountLinks.Count > 1)
                {
                    WritePage(request, response, PageTitle("Import Avatar"),
                            ImportAvatarForm(firstName, lastName, "This avatar's account has other avatars linked to it. Contact support to merge multi-avatar accounts."));
                    return;
                }

                if (!m_WebAccountService.AbsorbSoloAccount(account.PrincipalID, existingLink.WebAccountID, session.WebAccountID))
                {
                    WritePage(request, response, PageTitle("Import Avatar"), ImportAvatarForm(firstName, lastName, "Could not merge that account. Please try again."));
                    return;
                }

                m_WebAccountService.LogActivity(new WebActivityEntry
                {
                    WebAccountID = session.WebAccountID,
                    AvatarPrincipalID = account.PrincipalID,
                    EventType = "avatar_imported",
                    Description = "Merged avatar '" + account.Name + "' and its account in",
                    IPAddress = GetClientIP(request)
                });

                response.Redirect(BasePath + "/my-avatars?message=" + Uri.EscapeDataString("Avatar '" + account.Name + "' and its account merged in."), HttpStatusCode.Redirect);
                return;
            }

            List<WebAccountAvatarLink> currentLinks = m_WebAccountService.GetLinkedAvatars(session.WebAccountID);
            try
            {
                m_WebAccountService.LinkAvatar(session.WebAccountID, account.PrincipalID, "Imported", currentLinks.Count == 0);
            }
            catch (Exception)
            {
                WritePage(request, response, PageTitle("Import Avatar"),
                        ImportAvatarForm(firstName, lastName, "This avatar is already linked to another portal account. Contact support if this is a mistake."));
                return;
            }

            m_WebAccountService.LogActivity(new WebActivityEntry
            {
                WebAccountID = session.WebAccountID,
                AvatarPrincipalID = account.PrincipalID,
                EventType = "avatar_imported",
                Description = "Imported avatar '" + account.Name + "'",
                IPAddress = GetClientIP(request)
            });

            response.Redirect(BasePath + "/my-avatars?message=" + Uri.EscapeDataString("Avatar '" + account.Name + "' imported."), HttpStatusCode.Redirect);
        }

        private static string ImportAvatarForm(string firstName, string lastName, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Import Avatar</h1>"
                    + "<p>Link an avatar that already exists on this grid to your portal account. Your in-world password is verified but never stored.</p>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/import-avatar\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\" required autofocus></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\" required></label><br/>"
                    + "<label>Grid password<br/><input type=\"password\" name=\"password\" required></label><br/>"
                    + "<button type=\"submit\">Import avatar</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/my-avatars\">Cancel</a></p>";
        }

        private void HandleMyAvatars(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>My Avatars</h1>").Append(message);
            sb.Append("<p><a href=\"").Append(BasePath).Append("/create-avatar\">Create a new avatar</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/import-avatar\">Import an existing avatar</a></p>");

            if (session.WebAccountID == UUID.Zero || m_WebAccountService == null)
            {
                sb.Append("<p>Add an email to your account (<a href=\"").Append(BasePath).Append("/change-email\">Change Email</a>) to link a portal account first.</p>");
                WritePage(request, response, PageTitle("My Avatars"), sb.ToString());
                return;
            }

            List<WebAccountAvatarLink> links = m_WebAccountService.GetLinkedAvatars(session.WebAccountID);
            if (links.Count == 0)
            {
                sb.Append("<p>You haven't linked any avatars yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Avatar Name</th><th>Email</th><th>UUID</th><th>Status</th><th>Type</th><th>Created</th><th></th></tr>");
                foreach (WebAccountAvatarLink link in links)
                {
                    UserAccount avatar = m_UserAccountService?.GetUserAccount(UUID.Zero, link.AvatarPrincipalID);
                    string avatarName = avatar != null ? avatar.Name : link.AvatarPrincipalID.ToString();
                    string status = avatar != null && avatar.UserLevel < 0 ? "Suspended" : "Active";
                    bool isActive = link.AvatarPrincipalID == session.PrincipalID;

                    sb.Append("<tr><td>").Append(Html(avatarName)).Append(isActive ? " <span class=\"pill pill-yes\">Active</span>" : string.Empty).Append("</td>");
                    sb.Append("<td>").Append(Html(avatar?.Email ?? string.Empty)).Append("</td>");
                    sb.Append("<td>").Append(link.AvatarPrincipalID).Append("</td>");
                    sb.Append("<td><span class=\"pill ").Append(status == "Active" ? "pill-yes" : "pill-no").Append("\">").Append(status).Append("</span></td>");
                    sb.Append("<td>").Append(Html(link.LinkType)).Append("</td>");
                    sb.Append("<td>").Append(Html(link.LinkedDate.ToString("MMM d, yyyy"))).Append("</td>");
                    sb.Append("<td>");
                    if (!isActive)
                    {
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/switch-avatar\">")
                          .Append("<input type=\"hidden\" name=\"avatar_principal_id\" value=\"").Append(link.AvatarPrincipalID).Append("\">")
                          .Append("<button type=\"submit\">Switch to</button></form>");
                    }
                    sb.Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("My Avatars"), sb.ToString());
        }

        // Only ever reachable for one of the session's OWN linked avatars -
        // the client-supplied avatar_principal_id is never trusted on its
        // own. Deliberately mutates the existing session in place rather
        // than issuing a fresh CreateSession/cookie like
        // HandleAdminUsersLoginAs does - that precedent exists for crossing
        // between DIFFERENT people's identities (support tooling, where a
        // fresh audit token/expiry matters); switching among your own
        // avatars is lower-stakes and shouldn't reset the session clock.
        private void HandleSwitchAvatar(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (session.WebAccountID == UUID.Zero || m_WebAccountService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            if (!UUID.TryParse(FormValue(form, "avatar_principal_id"), out UUID targetId))
            {
                response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                return;
            }

            List<WebAccountAvatarLink> owned = m_WebAccountService.GetLinkedAvatars(session.WebAccountID);
            if (!owned.Exists(a => a.AvatarPrincipalID == targetId))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            UserAccount target = m_UserAccountService?.GetUserAccount(UUID.Zero, targetId);
            if (target == null)
            {
                response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                return;
            }

            session.PrincipalID = target.PrincipalID;
            session.Name = target.FirstName + " " + target.LastName;
            session.IsAdmin = target.UserLevel >= 200;

            m_WebAccountService.LogActivity(new WebActivityEntry
            {
                WebAccountID = session.WebAccountID,
                AvatarPrincipalID = target.PrincipalID,
                EventType = "active_avatar_switched",
                Description = "Switched active avatar to '" + target.Name + "'",
                IPAddress = GetClientIP(request)
            });

            response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
        }

        #endregion Multi-avatar portal accounts

        #region Suggestion Box

        private void HandleSuggestionBox(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);

            if (m_SuggestionService == null)
            {
                WritePage(request, response, PageTitle("Suggestion Box"),
                        "<h1>Suggestion Box</h1><p>Suggestions are not available on this grid right now.</p>");
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Suggestion Box"), SuggestionBoxForm(null));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string subject = FormValue(form, "subject").Trim();
            string message = FormValue(form, "message").Trim();
            bool anonymous = FormValue(form, "anonymous") == "on";

            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(message))
            {
                WritePage(request, response, PageTitle("Suggestion Box"), SuggestionBoxForm("Enter both a subject and a message."));
                return;
            }

            Suggestion suggestion = new Suggestion
            {
                ID = UUID.Random(),
                SubmitterAvatarID = (session != null && !anonymous) ? session.PrincipalID : UUID.Zero,
                SubmitterName = (session != null && !anonymous) ? session.Name : string.Empty,
                Subject = subject,
                Message = message,
                Status = "new",
                Created = DateTime.UtcNow
            };

            if (!m_SuggestionService.Store(suggestion))
            {
                WritePage(request, response, PageTitle("Suggestion Box"), SuggestionBoxForm("Could not submit your suggestion. Please try again."));
                return;
            }

            if (session != null && session.WebAccountID != UUID.Zero)
            {
                m_WebAccountService?.LogActivity(new WebActivityEntry
                {
                    WebAccountID = session.WebAccountID,
                    AvatarPrincipalID = session.PrincipalID,
                    EventType = "suggestion_submitted",
                    Description = "Submitted a suggestion: " + subject,
                    IPAddress = GetClientIP(request)
                });
            }

            WritePage(request, response, PageTitle("Suggestion Box"),
                    "<h1>Suggestion Box</h1><p>Thanks for your suggestion!</p><p><a href=\"" + BasePath + "/suggestion-box\">Submit another</a></p>");
        }

        private static string SuggestionBoxForm(string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Suggestion Box</h1>"
                    + "<p>Have an idea for the grid? Let us know.</p>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/suggestion-box\">"
                    + "<label>Subject<br/><input type=\"text\" name=\"subject\" required autofocus></label><br/>"
                    + "<label>Message<br/><textarea name=\"message\" rows=\"5\" required></textarea></label><br/>"
                    + "<label><input type=\"checkbox\" name=\"anonymous\" style=\"width:auto;display:inline\"> Submit anonymously</label><br/>"
                    + "<button type=\"submit\">Submit</button>"
                    + "</form>";
        }

        private void HandleAdminSuggestions(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Suggestions"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }
            if (m_SuggestionService == null)
            {
                WritePage(request, response, PageTitle("Suggestions"),
                        "<h1>Suggestions</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Suggestion service is not available.</p>");
                return;
            }

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "id"), out UUID suggestionId))
                {
                    Suggestion existing = m_SuggestionService.Get(suggestionId);
                    if (existing != null)
                    {
                        existing.Status = FormValue(form, "status");
                        m_SuggestionService.Store(existing);
                    }
                }
                response.Redirect(BasePath + "/admin/suggestions", HttpStatusCode.Redirect);
                return;
            }

            List<Suggestion> suggestions = m_SuggestionService.GetAll(0, 100);
            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Suggestions</h1><p><a href=\"").Append(BasePath).Append("/admin\">Back to admin</a></p>");

            if (suggestions.Count == 0)
            {
                sb.Append("<p>No suggestions yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Date</th><th>From</th><th>Subject</th><th>Message</th><th>Status</th></tr>");
                foreach (Suggestion suggestion in suggestions)
                {
                    string from = suggestion.SubmitterAvatarID == UUID.Zero ? "Anonymous" : Html(suggestion.SubmitterName);
                    sb.Append("<tr><td>").Append(Html(suggestion.Created.ToString("yyyy-MM-dd"))).Append("</td>");
                    sb.Append("<td>").Append(from).Append("</td>");
                    sb.Append("<td>").Append(Html(suggestion.Subject)).Append("</td>");
                    sb.Append("<td>").Append(Html(suggestion.Message)).Append("</td>");
                    sb.Append("<td><form method=\"post\" action=\"").Append(BasePath).Append("/admin/suggestions\">")
                      .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(suggestion.ID).Append("\">")
                      .Append("<select name=\"status\" onchange=\"this.form.submit()\">")
                      .Append("<option value=\"new\"").Append(suggestion.Status == "new" ? " selected" : "").Append(">New</option>")
                      .Append("<option value=\"reviewed\"").Append(suggestion.Status == "reviewed" ? " selected" : "").Append(">Reviewed</option>")
                      .Append("<option value=\"closed\"").Append(suggestion.Status == "closed" ? " selected" : "").Append(">Closed</option>")
                      .Append("</select></form></td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Suggestions"), sb.ToString());
        }

        #endregion Suggestion Box

        #region Store: prim-capacity packs + self-service region ordering

        // Resident-facing catalog. Buying is inline on each card (currency
        // choice + a region picker/name field baked into the same POST
        // form) rather than a separate checkout page, matching this file's
        // existing lean-page style (see My Regions).
        private void HandleStore(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);

            if (m_StoreService == null)
            {
                WritePage(request, response, PageTitle("Store"),
                        "<h1><i class=\"bi bi-shop\"></i> Store</h1><p>The store is not available on this grid.</p>");
                return;
            }

            List<StoreCatalogItem> items = m_StoreService.GetActiveCatalogItems().OrderBy(i => i.SortOrder).ToList();
            List<GridRegion> ownedRegions = session != null ? GetRegionsOwnedBy(session.PrincipalID) : new List<GridRegion>();

            // For the RegionOrder estate picker below - the resident's own
            // existing estates, so checkout can offer "join one of these"
            // as an alternative to always creating a brand new estate.
            List<EstateSettings> ownedEstates = new List<EstateSettings>();
            if (session != null && m_EstateDataService != null)
            {
                foreach (int estateId in m_EstateDataService.GetEstatesByOwner(session.PrincipalID))
                {
                    EstateSettings estate = m_EstateDataService.LoadEstateSettings(estateId);
                    if (estate != null && estate.EstateID != 0)
                        ownedEstates.Add(estate);
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-shop\"></i> Store</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a> | <a href=\"")
              .Append(BasePath).Append("/store/my-purchases\">My Purchases</a></p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            if (session == null)
                sb.Append("<p><a href=\"").Append(BasePath).Append("/login\">Log in</a> to buy.</p>");

            if (items.Count == 0)
            {
                sb.Append("<p>Nothing is for sale right now.</p>");
            }
            else
            {
                foreach (StoreCatalogItem item in items)
                {
                    sb.Append("<div class=\"content-card\">");
                    sb.Append("<h2>").Append(Html(item.Name)).Append("</h2>");
                    if (!string.IsNullOrEmpty(item.Description))
                        sb.Append("<p>").Append(Html(item.Description)).Append("</p>");

                    if (item.ItemType == "PrimPack")
                    {
                        sb.Append("<p>Adds <strong>+").Append(item.PrimAmount.ToString("N0"))
                          .Append("</strong> prims to the chosen region's current capacity.</p>");
                    }
                    else if (item.ItemType == "RegionOrder")
                    {
                        sb.Append("<p>").Append(item.RegionSizeX > 0 ? item.RegionSizeX.ToString() : "256").Append("&times;")
                          .Append(item.RegionSizeY > 0 ? item.RegionSizeY.ToString() : "256")
                          .Append(" region, ").Append((item.PrimAmount > 0 ? item.PrimAmount : 15000).ToString("N0")).Append(" prims.</p>");
                    }

                    if (item.DurationDays > 0)
                        sb.Append("<p>Lasts ").Append(item.DurationDays).Append(" days.</p>");

                    if (session != null)
                    {
                        bool canBuy = item.ItemType != "PrimPack" || ownedRegions.Count > 0;

                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/store/buy\">");
                        sb.Append("<input type=\"hidden\" name=\"catalog_item_id\" value=\"").Append(item.ID).Append("\">");

                        if (item.ItemType == "PrimPack")
                        {
                            if (ownedRegions.Count == 0)
                            {
                                sb.Append("<p><em>You don't own a region to apply this to.</em></p>");
                            }
                            else
                            {
                                sb.Append("<p><select name=\"region_id\">");
                                foreach (GridRegion r in ownedRegions)
                                    sb.Append("<option value=\"").Append(r.RegionID).Append("\">").Append(Html(r.RegionName)).Append("</option>");
                                sb.Append("</select></p>");
                            }
                        }
                        else if (item.ItemType == "RegionOrder")
                        {
                            sb.Append("<p><input type=\"text\" name=\"region_name\" placeholder=\"Region name\" maxlength=\"63\" required></p>");

                            // Estate choice: join one of the resident's own
                            // existing estates (re-verified server-side at
                            // checkout - this dropdown is just a UI
                            // convenience, not the trust boundary), or
                            // create a new one. "new" is deliberately the
                            // default option so a resident who ignores this
                            // entirely still gets today's existing behavior.
                            sb.Append("<p><label>Estate: <select name=\"estate_choice\" onchange=\"this.form.querySelector('[name=estate_name]').style.display = this.value === 'new' ? '' : 'none';\">");
                            sb.Append("<option value=\"new\">Create a new estate</option>");
                            foreach (EstateSettings estate in ownedEstates)
                                sb.Append("<option value=\"").Append(estate.EstateID).Append("\">Join \"").Append(Html(estate.EstateName)).Append("\"</option>");
                            sb.Append("</select></label></p>");
                            sb.Append("<p><input type=\"text\" name=\"estate_name\" placeholder=\"New estate name (default: ")
                              .Append(Html(session.Name)).Append("'s Estate)\" maxlength=\"63\"></p>");

                            // Grid location: optional, blank on both means
                            // "pick any free spot in the configured block"
                            // (today's existing behavior). A resident who
                            // fills in just one is caught server-side, not
                            // here - both-or-neither is enforced in
                            // BuildStoreOrder, not by disabling one field
                            // client-side, since that's easy to bypass and
                            // this isn't a trust boundary either way.
                            sb.Append("<p><label>Grid location (optional - leave blank to auto-pick; valid range ")
                              .Append(m_regionOrderGridXStart).Append("-").Append(m_regionOrderGridXEnd).Append(" x ")
                              .Append(m_regionOrderGridYStart).Append("-").Append(m_regionOrderGridYEnd).Append("): ")
                              .Append("<input type=\"number\" name=\"location_x\" placeholder=\"X\" style=\"width:5em\" min=\"")
                              .Append(m_regionOrderGridXStart).Append("\" max=\"").Append(m_regionOrderGridXEnd).Append("\"> ")
                              .Append("<input type=\"number\" name=\"location_y\" placeholder=\"Y\" style=\"width:5em\" min=\"")
                              .Append(m_regionOrderGridYStart).Append("\" max=\"").Append(m_regionOrderGridYEnd).Append("\"></label></p>");
                        }

                        if (canBuy)
                        {
                            if (item.PriceConfluence > 0)
                                sb.Append("<button type=\"submit\" name=\"currency\" value=\"Confluence\">Buy for ").Append(m_currencySymbol).Append(" ")
                                  .Append(item.PriceConfluence.ToString("N0")).Append("</button> ");
                            if (item.PriceGloebits > 0)
                                sb.Append("<button type=\"submit\" name=\"currency\" value=\"Gloebit\"")
                                  .Append(m_gloebitEnabled ? string.Empty : " disabled title=\"Gloebit purchases are not available on this grid\"")
                                  .Append(">Buy for G$ ").Append(item.PriceGloebits.ToString("N0")).Append("</button>");
                        }

                        sb.Append("</form>");
                    }

                    sb.Append("</div>");
                }
            }

            WritePage(request, response, PageTitle("Store"), sb.ToString());
        }

        private void HandleStoreBuy(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            if (m_StoreService == null || request.HttpMethod != "POST")
            {
                response.Redirect(BasePath + "/store", HttpStatusCode.Redirect);
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string redirectUrl = ProcessStoreBuy(session, form);
            response.Redirect(redirectUrl, HttpStatusCode.Redirect);
        }

        private string ProcessStoreBuy(WebSession session, Dictionary<string, string> form)
        {
            if (!UUID.TryParse(FormValue(form, "catalog_item_id"), out UUID catalogItemId))
                return BasePath + "/store?message=" + Uri.EscapeDataString("Invalid item.");

            StoreCatalogItem item = m_StoreService.GetCatalogItem(catalogItemId);
            string currency = FormValue(form, "currency");

            string validationError = ValidateStoreBuy(item, currency);
            if (validationError != null)
                return BasePath + "/store?message=" + Uri.EscapeDataString(validationError);

            // Per-avatar purchase lock - ICurrencyService.Transfer is not
            // itself safe against a double-submitted "Buy" click (see
            // PROJECT_LOG.md), so a second concurrent purchase from the
            // same avatar is rejected here, before it ever reaches Transfer
            // or even creates an order row. Same TryAdd-then-remove-in-
            // finally shape as EntityTransferStateMachine.SetInTransit.
            bool alreadyInProgress;
            lock (m_purchasesInProgress)
            {
                alreadyInProgress = m_purchasesInProgress.ContainsKey(session.PrincipalID);
                if (!alreadyInProgress)
                    m_purchasesInProgress[session.PrincipalID] = true;
            }

            if (alreadyInProgress)
                return BasePath + "/store?message=" + Uri.EscapeDataString("A purchase is already in progress for your account - please wait.");

            try
            {
                StoreOrder order = BuildStoreOrder(session, item, currency, form, out string orderError);
                if (order == null)
                    return BasePath + "/store?message=" + Uri.EscapeDataString(orderError);

                m_StoreService.StoreOrder(order);

                if (currency == "Confluence")
                {
                    string message = ChargeConfluenceCurrency(session, order, item);
                    return BasePath + "/store?message=" + Uri.EscapeDataString(message);
                }

                StoreGloebitAuth auth = m_StoreService.GetGloebitAuth(session.PrincipalID);
                if (auth == null || !auth.Authorized || string.IsNullOrEmpty(auth.AccessToken))
                {
                    lock (m_pendingGloebitOrders)
                        m_pendingGloebitOrders[session.PrincipalID] = order.ID;
                    return m_GloebitClient.BuildAuthorizeUri(session.PrincipalID, session.Name).ToString();
                }

                string gloebitMessage = SubmitGloebitTransaction(session.PrincipalID, session.Name, order, item, auth);
                return BasePath + "/store?message=" + Uri.EscapeDataString(gloebitMessage);
            }
            finally
            {
                lock (m_purchasesInProgress)
                    m_purchasesInProgress.Remove(session.PrincipalID);
            }
        }

        private string ValidateStoreBuy(StoreCatalogItem item, string currency)
        {
            if (item == null || !item.IsActive)
                return "This item is no longer available.";
            if (currency != "Confluence" && currency != "Gloebit")
                return "Choose a currency.";
            if (currency == "Confluence" && item.PriceConfluence <= 0)
                return "This item cannot be purchased with Confluence Currency.";
            if (currency == "Gloebit" && item.PriceGloebits <= 0)
                return "This item cannot be purchased with Gloebit.";
            if (currency == "Gloebit" && (!m_gloebitEnabled || m_GloebitClient == null))
                return "Gloebit purchases are not available on this grid.";
            return null;
        }

        // Client-supplied region_id/region_name are never trusted alone -
        // PrimPack re-verifies ownership via GetOwnedRegionOrNull (same
        // discipline as My Regions), RegionOrder re-checks the name isn't
        // already taken by a real region or another pending order.
        private StoreOrder BuildStoreOrder(WebSession session, StoreCatalogItem item, string currency, Dictionary<string, string> form, out string error)
        {
            error = null;
            StoreOrder order = new StoreOrder
            {
                ID = UUID.Random(),
                CatalogItemID = item.ID,
                OrderType = item.ItemType,
                ResidentAvatarID = session.PrincipalID,
                ResidentName = session.Name,
                CurrencyUsed = currency,
                AmountCharged = currency == "Confluence" ? item.PriceConfluence : item.PriceGloebits,
                Status = "PendingPayment",
                Created = DateTime.UtcNow,
                Updated = DateTime.UtcNow
            };

            if (item.ItemType == "PrimPack")
            {
                if (!UUID.TryParse(FormValue(form, "region_id"), out UUID regionId))
                {
                    error = "Choose a region.";
                    return null;
                }

                if (GetOwnedRegionOrNull(session, regionId) == null)
                {
                    error = "You don't own that region.";
                    return null;
                }

                order.TargetRegionID = regionId;
            }
            else if (item.ItemType == "RegionOrder")
            {
                string regionName = (FormValue(form, "region_name") ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(regionName) || regionName.Length > 63)
                {
                    error = "Enter a valid region name.";
                    return null;
                }

                if (m_GridService != null && m_GridService.GetRegionsByName(UUID.Zero, regionName, 1).Count > 0)
                {
                    error = "That region name is already taken.";
                    return null;
                }

                if (m_StoreService.GetAllOrders().Any(o => o.OrderType == "RegionOrder"
                        && !string.IsNullOrEmpty(o.RequestedRegionName)
                        && string.Equals(o.RequestedRegionName, regionName, StringComparison.OrdinalIgnoreCase)
                        && o.Status != "Cancelled" && o.Status != "PaymentFailed"))
                {
                    error = "That region name is already taken or pending.";
                    return null;
                }

                order.RequestedRegionName = regionName;

                // Estate choice - a submitted estate_choice value is never
                // trusted alone; re-checked against this resident's own
                // GetEstatesByOwner list, same discipline as the PrimPack
                // region_id check above. Anything other than a real owned
                // estate ID (missing field, "new", tampered value, an
                // estate they don't actually own) falls through to
                // "create a new estate," never silently to someone else's.
                string estateChoice = FormValue(form, "estate_choice");
                if (!string.IsNullOrEmpty(estateChoice) && estateChoice != "new"
                        && int.TryParse(estateChoice, out int estateId))
                {
                    if (m_EstateDataService == null || !m_EstateDataService.GetEstatesByOwner(session.PrincipalID).Contains(estateId))
                    {
                        error = "You don't own that estate.";
                        return null;
                    }

                    order.RequestedEstateID = estateId;
                }
                else
                {
                    string estateName = (FormValue(form, "estate_name") ?? string.Empty).Trim();
                    if (estateName.Length > 63)
                    {
                        error = "Estate name is too long.";
                        return null;
                    }

                    order.RequestedEstateName = string.IsNullOrEmpty(estateName) ? null : estateName;
                }

                // Grid location - optional, both-or-neither. Best-effort
                // check here (same caveat as the region-name uniqueness
                // check above: another order can still claim this exact
                // spot between now and payment/fulfillment) - the
                // authoritative re-check happens in
                // AllocateRegionOrderLocation at fulfillment time, which
                // fails the order outright rather than silently picking a
                // different spot if the resident's specific request is no
                // longer free by then.
                string locationXStr = FormValue(form, "location_x");
                string locationYStr = FormValue(form, "location_y");
                bool hasLocationX = !string.IsNullOrEmpty(locationXStr);
                bool hasLocationY = !string.IsNullOrEmpty(locationYStr);
                if (hasLocationX != hasLocationY)
                {
                    error = "Enter both a grid X and Y, or leave both blank to auto-pick.";
                    return null;
                }

                if (hasLocationX && hasLocationY)
                {
                    if (!int.TryParse(locationXStr, out int requestedX) || !int.TryParse(locationYStr, out int requestedY)
                            || requestedX < m_regionOrderGridXStart || requestedX > m_regionOrderGridXEnd
                            || requestedY < m_regionOrderGridYStart || requestedY > m_regionOrderGridYEnd)
                    {
                        error = "Grid location must be within " + m_regionOrderGridXStart + "-" + m_regionOrderGridXEnd
                                + " x " + m_regionOrderGridYStart + "-" + m_regionOrderGridYEnd + ".";
                        return null;
                    }

                    if (ComputeUsedRegionOrderLocations().Contains((requestedX, requestedY)))
                    {
                        error = "That grid location is already taken or pending.";
                        return null;
                    }

                    order.RequestedLocationX = requestedX;
                    order.RequestedLocationY = requestedY;
                }
            }
            else
            {
                error = "Unknown item type.";
                return null;
            }

            return order;
        }

        private string ChargeConfluenceCurrency(WebSession session, StoreOrder order, StoreCatalogItem item)
        {
            if (m_CurrencyService == null)
            {
                order.Status = "PaymentFailed";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return "Currency service is not available.";
            }

            if (m_CurrencyService.GetBalance(session.PrincipalID) < order.AmountCharged)
            {
                order.Status = "PaymentFailed";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return "Insufficient balance.";
            }

            // order.ID doubles as the transactionID - currency_transactions'
            // primary key - so a retried/duplicated POST for this order
            // fails cleanly (rolled back) instead of double-charging. Every
            // other call site in this codebase passes UUID.Zero here and
            // gets no such protection (see PROJECT_LOG.md).
            bool ok = m_CurrencyService.Transfer(UUID.Zero, session.PrincipalID, order.AmountCharged,
                    "Store purchase: " + item.Name, STORE_PURCHASE_TRANSACTION_TYPE, order.ID);

            if (!ok)
            {
                order.Status = "PaymentFailed";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return "Payment failed.";
            }

            order.PaymentTransactionID = order.ID.ToString();
            order.Status = "Paid";
            order.Updated = DateTime.UtcNow;
            m_StoreService.StoreOrder(order);

            ProcessPaidOrder(order, item);
            return "Purchase complete.";
        }

        private string SubmitGloebitTransaction(UUID avatarId, string avatarName, StoreOrder order, StoreCatalogItem item, StoreGloebitAuth auth)
        {
            StoreGloebitTransaction txn = new StoreGloebitTransaction
            {
                ID = UUID.Random(),
                StoreOrderID = order.ID,
                AvatarPrincipalID = avatarId,
                Amount = order.AmountCharged,
                Stage = "Submitted",
                Created = DateTime.UtcNow,
                Updated = DateTime.UtcNow
            };
            m_StoreService.StoreGloebitTransaction(txn);

            bool submitted = m_GloebitClient.Transact(txn.ID, avatarId, auth.AccessToken, auth.GloebitID,
                    order.AmountCharged, "Store purchase: " + item.Name, avatarName, out string error);

            order.PaymentTransactionID = txn.ID.ToString();
            order.Updated = DateTime.UtcNow;

            if (!submitted)
            {
                order.Status = "PaymentFailed";
                m_StoreService.StoreOrder(order);
                txn.Stage = "Failed";
                txn.ResponseReason = error;
                txn.Updated = DateTime.UtcNow;
                m_StoreService.StoreGloebitTransaction(txn);
                return "Gloebit payment failed: " + error;
            }

            m_StoreService.StoreOrder(order);
            return "Payment submitted to Gloebit - awaiting confirmation. Check My Purchases shortly.";
        }

        // Public browse - only ever shows IsListed listings, matching
        // real SL: browsing/checkout live entirely on the marketplace
        // website (this page), never through the viewer's DirectDelivery
        // cap (DirectDeliveryModule), which is merchant-management only.
        private void HandleMarketplace(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);

            if (m_MarketplaceListingsService == null)
            {
                WritePage(request, response, PageTitle("Marketplace"),
                        "<h1><i class=\"bi bi-bag\"></i> Marketplace</h1><p>The marketplace is not available on this grid.</p>");
                return;
            }

            int.TryParse(request.QueryString.Get("start"), out int start);
            if (start < 0)
                start = 0;
            const int pageSize = 24;

            List<MarketplaceListing> listings = m_MarketplaceListingsService.GetListedListings(start, pageSize);

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-bag\"></i> Marketplace</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/dashboard\">Back to dashboard</a>");
            if (session != null)
                sb.Append(" | <a href=\"").Append(BasePath).Append("/marketplace/manage\">My Listings</a>");
            sb.Append("</p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            if (listings.Count == 0)
            {
                sb.Append("<p>Nothing is for sale right now.</p>");
            }
            else
            {
                foreach (MarketplaceListing listing in listings)
                {
                    sb.Append("<div class=\"content-card\">");
                    sb.Append("<h2><a href=\"").Append(BasePath).Append("/marketplace/listing?id=").Append(listing.ID).Append("\">")
                      .Append(Html(listing.Title)).Append("</a></h2>");
                    if (!string.IsNullOrEmpty(listing.Description))
                        sb.Append("<p>").Append(Html(TruncateText(listing.Description, 160))).Append("</p>");
                    sb.Append("<p><strong>").Append(m_currencySymbol).Append(" ").Append(listing.Price.ToString("N0")).Append("</strong>");
                    if (listing.CountOnHand.HasValue)
                        sb.Append(listing.CountOnHand.Value > 0 ? " - " + listing.CountOnHand.Value.ToString("N0") + " in stock" : " - Out of stock");
                    sb.Append("</p>");
                    sb.Append("</div>");
                }

                sb.Append("<p>");
                if (start > 0)
                    sb.Append("<a href=\"").Append(BasePath).Append("/marketplace?start=").Append(Math.Max(0, start - pageSize)).Append("\">Previous</a> ");
                if (listings.Count == pageSize)
                    sb.Append("<a href=\"").Append(BasePath).Append("/marketplace?start=").Append(start + pageSize).Append("\">Next</a>");
                sb.Append("</p>");
            }

            WritePage(request, response, PageTitle("Marketplace"), sb.ToString());
        }

        private void HandleMarketplaceListing(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);

            if (m_MarketplaceListingsService == null || !int.TryParse(request.QueryString.Get("id"), out int id))
            {
                response.Redirect(BasePath + "/marketplace", HttpStatusCode.Redirect);
                return;
            }

            MarketplaceListing listing = m_MarketplaceListingsService.GetListing(id);
            if (listing == null || !listing.IsListed)
            {
                WritePage(request, response, PageTitle("Marketplace"),
                        "<h1><i class=\"bi bi-bag\"></i> Marketplace</h1><p>Listing not found.</p>");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-bag\"></i> ").Append(Html(listing.Title)).Append("</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/marketplace\">Back to Marketplace</a></p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            sb.Append("<div class=\"content-card\">");
            if (!string.IsNullOrEmpty(listing.Description))
                sb.Append("<p>").Append(Html(listing.Description)).Append("</p>");
            sb.Append("<p><strong>").Append(m_currencySymbol).Append(" ").Append(listing.Price.ToString("N0")).Append("</strong></p>");

            bool inStock = !listing.CountOnHand.HasValue || listing.CountOnHand.Value > 0;
            if (listing.CountOnHand.HasValue)
                sb.Append("<p>").Append(inStock ? listing.CountOnHand.Value.ToString("N0") + " in stock" : "Out of stock").Append("</p>");

            if (session == null)
            {
                sb.Append("<p><a href=\"").Append(BasePath).Append("/login\">Log in</a> to buy.</p>");
            }
            else if (session.PrincipalID == listing.SellerID)
            {
                sb.Append("<p><em>This is your own listing.</em> <a href=\"").Append(BasePath)
                  .Append("/marketplace/manage?listing=").Append(listing.ID).Append("\">Manage it</a></p>");
            }
            else if (listing.ListingFolderID == UUID.Zero)
            {
                sb.Append("<p><em>Not available yet - the seller hasn't finished setting this listing up.</em></p>");
            }
            else if (!inStock)
            {
                sb.Append("<p><em>Out of stock.</em></p>");
            }
            else
            {
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/marketplace/buy\">");
                sb.Append("<input type=\"hidden\" name=\"listing_id\" value=\"").Append(listing.ID).Append("\">");
                sb.Append("<button type=\"submit\">Buy for ").Append(m_currencySymbol).Append(" ").Append(listing.Price.ToString("N0")).Append("</button>");
                sb.Append("</form>");
            }
            sb.Append("</div>");

            WritePage(request, response, PageTitle(listing.Title), sb.ToString());
        }

        private void HandleMarketplaceBuy(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            if (m_MarketplaceListingsService == null || request.HttpMethod != "POST")
            {
                response.Redirect(BasePath + "/marketplace", HttpStatusCode.Redirect);
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string redirectUrl = ProcessMarketplaceBuy(session, form);
            response.Redirect(redirectUrl, HttpStatusCode.Redirect);
        }

        // Same double-submit-lock/idempotent-transactionID discipline as
        // ProcessStoreBuy/ChargeConfluenceCurrency, plus the extra stock
        // check (before any currency moves, per the plan) and delivery step
        // Store doesn't need - Store sells grid-provided items (prim packs,
        // region orders), Marketplace listings are peer-to-peer, so payment
        // goes to the seller, not the house, and MarketplaceInventoryOperations.
        // Deliver actually has to run to hand the item over.
        private string ProcessMarketplaceBuy(WebSession session, Dictionary<string, string> form)
        {
            if (!int.TryParse(FormValue(form, "listing_id"), out int listingId))
                return BasePath + "/marketplace?message=" + Uri.EscapeDataString("Invalid listing.");

            MarketplaceListing listing = m_MarketplaceListingsService.GetListing(listingId);
            if (listing == null || !listing.IsListed)
                return BasePath + "/marketplace?message=" + Uri.EscapeDataString("This listing is no longer available.");

            if (listing.SellerID == session.PrincipalID)
                return BasePath + "/marketplace/listing?id=" + listingId + "&message=" + Uri.EscapeDataString("You cannot buy your own listing.");

            if (listing.ListingFolderID == UUID.Zero)
                return BasePath + "/marketplace/listing?id=" + listingId + "&message=" + Uri.EscapeDataString("This listing has no inventory associated with it yet.");

            if (m_CurrencyService == null || m_MarketplaceLedger == null || m_InventoryService == null || m_UserAccountService == null)
                return BasePath + "/marketplace/listing?id=" + listingId + "&message=" + Uri.EscapeDataString("The marketplace is not fully configured on this grid.");

            bool alreadyInProgress;
            lock (m_marketplacePurchasesInProgress)
            {
                alreadyInProgress = m_marketplacePurchasesInProgress.ContainsKey(session.PrincipalID);
                if (!alreadyInProgress)
                    m_marketplacePurchasesInProgress[session.PrincipalID] = true;
            }

            if (alreadyInProgress)
                return BasePath + "/marketplace/listing?id=" + listingId + "&message=" + Uri.EscapeDataString("A purchase is already in progress for your account - please wait.");

            try
            {
                if (!m_MarketplaceListingsService.TryReserveStock(listingId))
                    return BasePath + "/marketplace/listing?id=" + listingId + "&message=" + Uri.EscapeDataString("This item just sold out.");

                // Doubles as both the currency transaction ID (so a
                // retried/duplicated POST can't double-charge - same
                // rationale as Store's order.ID) and Deliver's own delivery
                // ledger idempotency key, in one fresh UUID.
                UUID deliveryId = UUID.Random();

                bool charged = m_CurrencyService.Transfer(listing.SellerID, session.PrincipalID, listing.Price,
                        "Marketplace purchase: " + listing.Title, MARKETPLACE_PURCHASE_TRANSACTION_TYPE, deliveryId);

                if (!charged)
                {
                    m_MarketplaceListingsService.ReleaseStock(listingId);
                    return BasePath + "/marketplace/listing?id=" + listingId + "&message=" + Uri.EscapeDataString("Payment failed - insufficient balance?");
                }

                DeliveryResponse delivery = MarketplaceInventoryOperations.DeliverListingItem(
                        m_InventoryService,
                        m_UserAccountService,
                        UUID.Zero,
                        true,
                        m_marketplaceServiceAccountId,
                        listing.SellerID,
                        listing.ListingFolderID,
                        session.PrincipalID,
                        listing.SnapshotFingerprint,
                        deliveryId.ToString(),
                        m_MarketplaceLedger,
                        m_log,
                        null);

                if (!delivery.Ok)
                {
                    // Currency already moved - refund via a second, distinct
                    // Transfer rather than trying to reverse the first one
                    // (matches the ledger's own append-only posture).
                    m_CurrencyService.Transfer(session.PrincipalID, listing.SellerID, listing.Price,
                            "Marketplace purchase refund (delivery failed): " + listing.Title,
                            MARKETPLACE_PURCHASE_TRANSACTION_TYPE, UUID.Random());
                    m_MarketplaceListingsService.ReleaseStock(listingId);
                    return BasePath + "/marketplace/listing?id=" + listingId + "&message="
                            + Uri.EscapeDataString("Delivery failed and your payment was refunded: " + delivery.Message);
                }

                return BasePath + "/marketplace/listing?id=" + listingId + "&message="
                        + Uri.EscapeDataString("Purchase complete - check your Marketplace Purchases folder.");
            }
            finally
            {
                lock (m_marketplacePurchasesInProgress)
                    m_marketplacePurchasesInProgress.Remove(session.PrincipalID);
            }
        }

        // The edit_url destination DirectDeliveryModule's viewer cap points
        // at (?listing=<id>), and the merchant's own listing dashboard with
        // no id given.
        private void HandleMarketplaceManage(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (m_MarketplaceListingsService == null)
            {
                WritePage(request, response, PageTitle("My Listings"),
                        "<h1><i class=\"bi bi-bag\"></i> My Listings</h1><p>The marketplace is not available on this grid.</p>");
                return;
            }

            string queryMessage = request.QueryString.Get("message");
            int.TryParse(request.QueryString.Get("listing"), out int editId);

            MarketplaceListing editing = editId > 0 ? m_MarketplaceListingsService.GetListing(editId) : null;
            if (editing != null && editing.SellerID != session.PrincipalID)
                editing = null;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-bag\"></i> My Listings</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/marketplace\">Back to Marketplace</a></p>");

            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            // Create/edit form - a listing is created with just a title/
            // description/price/stock; inventory association is a separate
            // step below, done from the web (not the viewer's DirectDelivery
            // Marketplace Listings floater - Firestorm/AyaneStorm both hard-
            // block that UI outside real Second Life, confirmed against
            // source: LLSLMMenuUpdater::checkMerchantStatus returns before
            // ever asking the region, regardless of caps. DirectDeliveryModule
            // itself is untouched and will work immediately if a viewer
            // without that block is ever used - this is purely a web-side
            // path to the same MarketplaceInventoryOperations.Snapshot call
            // that cap would have triggered).
            sb.Append("<div class=\"content-card\">");
            sb.Append("<h2>").Append(editing != null ? "Edit Listing" : "New Listing").Append("</h2>");
            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/marketplace/manage/save\">");
            if (editing != null)
                sb.Append("<input type=\"hidden\" name=\"listing_id\" value=\"").Append(editing.ID).Append("\">");
            sb.Append("<p><input type=\"text\" name=\"title\" placeholder=\"Title\" maxlength=\"255\" required value=\"")
              .Append(editing != null ? Html(editing.Title) : string.Empty).Append("\"></p>");
            sb.Append("<p><textarea name=\"description\" placeholder=\"Description\">")
              .Append(editing != null ? Html(editing.Description) : string.Empty).Append("</textarea></p>");
            sb.Append("<p>Price: ").Append(m_currencySymbol)
              .Append(" <input type=\"number\" name=\"price\" min=\"0\" required value=\"")
              .Append(editing != null ? editing.Price.ToString() : "0").Append("\"></p>");
            sb.Append("<p><label><input type=\"checkbox\" name=\"unlimited\" value=\"1\"")
              .Append(editing == null || !editing.CountOnHand.HasValue ? " checked" : string.Empty)
              .Append("> Unlimited stock</label> or stock on hand: <input type=\"number\" name=\"count_on_hand\" min=\"0\" value=\"")
              .Append(editing != null && editing.CountOnHand.HasValue ? editing.CountOnHand.Value.ToString() : string.Empty).Append("\"></p>");
            if (editing != null)
            {
                sb.Append("<p><label><input type=\"checkbox\" name=\"is_listed\" value=\"1\"")
                  .Append(editing.IsListed ? " checked" : string.Empty).Append("> Listed (visible in the Marketplace)</label>");
                if (editing.ListingFolderID == UUID.Zero)
                    sb.Append(" <em>- not yet associated with inventory; see below.</em>");
                sb.Append("</p>");
            }
            sb.Append("<button type=\"submit\">Save</button>");
            sb.Append("</form></div>");

            if (editing != null)
                sb.Append(BuildInventoryAssociationSection(session, editing));

            List<MarketplaceListing> mine = m_MarketplaceListingsService.GetListingsBySeller(session.PrincipalID);
            if (mine.Count > 0)
            {
                sb.Append("<h2>Your Listings</h2>");
                foreach (MarketplaceListing listing in mine)
                {
                    sb.Append("<div class=\"content-card\">");
                    sb.Append("<h3>").Append(Html(listing.Title)).Append(listing.IsListed ? " <small>(Listed)</small>" : " <small>(Unlisted)</small>").Append("</h3>");
                    sb.Append("<p>").Append(m_currencySymbol).Append(" ").Append(listing.Price.ToString("N0"));
                    if (listing.CountOnHand.HasValue)
                        sb.Append(" - ").Append(listing.CountOnHand.Value.ToString("N0")).Append(" in stock");
                    sb.Append("</p>");
                    sb.Append("<p><a href=\"").Append(BasePath).Append("/marketplace/manage?listing=").Append(listing.ID).Append("\">Edit</a></p>");
                    sb.Append("</div>");
                }
            }

            WritePage(request, response, PageTitle("My Listings"), sb.ToString());
        }

        private void HandleMarketplaceManageSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            if (m_MarketplaceListingsService == null || request.HttpMethod != "POST")
            {
                response.Redirect(BasePath + "/marketplace/manage", HttpStatusCode.Redirect);
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string title = (FormValue(form, "title") ?? string.Empty).Trim();
            string description = FormValue(form, "description") ?? string.Empty;
            int.TryParse(FormValue(form, "price"), out int price);
            bool unlimited = FormValue(form, "unlimited") == "1";
            int? countOnHand = null;
            if (!unlimited && int.TryParse(FormValue(form, "count_on_hand"), out int parsedCount))
                countOnHand = Math.Max(0, parsedCount);

            string redirectUrl;
            if (string.IsNullOrEmpty(title) || price < 0)
            {
                redirectUrl = BasePath + "/marketplace/manage?message=" + Uri.EscapeDataString("Title is required and price must be non-negative.");
            }
            else if (int.TryParse(FormValue(form, "listing_id"), out int listingId) && listingId > 0)
            {
                MarketplaceListing listing = m_MarketplaceListingsService.GetListing(listingId);
                if (listing == null || listing.SellerID != session.PrincipalID)
                {
                    redirectUrl = BasePath + "/marketplace/manage?message=" + Uri.EscapeDataString("Listing not found.");
                }
                else
                {
                    listing.Title = title;
                    listing.Description = description;
                    listing.Price = price;
                    listing.CountOnHand = countOnHand;
                    listing.IsListed = FormValue(form, "is_listed") == "1" && listing.ListingFolderID != UUID.Zero;
                    m_MarketplaceListingsService.UpdateListing(listing);
                    redirectUrl = BasePath + "/marketplace/manage?listing=" + listingId + "&message=" + Uri.EscapeDataString("Listing saved.");
                }
            }
            else
            {
                MarketplaceListing created = m_MarketplaceListingsService.CreateListing(session.PrincipalID, title, description, price, countOnHand);
                redirectUrl = BasePath + "/marketplace/manage?listing=" + created.ID + "&message="
                        + Uri.EscapeDataString("Listing created - associate an inventory folder with it below.");
            }

            response.Redirect(redirectUrl, HttpStatusCode.Redirect);
        }

        // Lists the merchant's own top-level Merchant Outbox product folders
        // (auto-created on first call if they don't exist yet, same as the
        // old v2 addon) via MarketplaceInventoryOperations.Inventory - the
        // exact same Scene-free call DirectDeliveryModule's GET /listings
        // path would make region-side, just made from here instead. Ordinary
        // inventory folder organizing (Inventory > OpenSim Marketplace >
        // Merchant Outbox > <product>) is completely ungated on every
        // viewer - only the SLM floater itself is blocked, not the folders
        // it would have read.
        private string BuildInventoryAssociationSection(WebSession session, MarketplaceListing editing)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<div class=\"content-card\">");
            sb.Append("<h2>Associate Inventory</h2>");

            if (m_InventoryService == null || m_UserAccountService == null)
            {
                sb.Append("<p><em>Inventory service is not available on this grid.</em></p></div>");
                return sb.ToString();
            }

            InventoryResponse products = MarketplaceInventoryOperations.ListListingItems(
                    m_InventoryService, m_UserAccountService, UUID.Zero, session.PrincipalID, 500);

            if (!products.Ok)
            {
                sb.Append("<p><em>").Append(Html(products.Message)).Append("</em></p></div>");
                return sb.ToString();
            }

            if (editing.ListingFolderID != UUID.Zero)
            {
                sb.Append("<p>Currently delivers the item associated on ").Append(editing.Updated.ToString("u"))
                  .Append(". Associating a different item below replaces it for future deliveries - already-delivered copies are unaffected.</p>");
            }

            sb.Append("<p>In your viewer, drag the item for this listing directly into "
                    + "<strong>Inventory &gt; Marketplace Listings</strong> "
                    + "(auto-created the first time you visit this page). Copy and Transfer "
                    + "permissions required. Then pick it below.</p>");

            if (products.Products.Count == 0)
            {
                sb.Append("<p><em>No items found yet in Marketplace Listings. "
                        + "If you just added one, refresh this page.</em></p></div>");
                return sb.ToString();
            }

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/marketplace/manage/associate\">");
            sb.Append("<input type=\"hidden\" name=\"listing_id\" value=\"").Append(editing.ID).Append("\">");
            sb.Append("<select name=\"source_folder_id\">");
            foreach (ProductFolderInfo product in products.Products)
            {
                bool sellable = product.Copy && product.Transfer;
                sb.Append("<option value=\"").Append(product.FolderId).Append("\"")
                  .Append(sellable ? string.Empty : " disabled").Append(">")
                  .Append(Html(product.Name));
                if (!sellable)
                    sb.Append(" - not sellable: ").Append(Html(product.Message));
                sb.Append("</option>");
            }
            sb.Append("</select> ");
            sb.Append("<button type=\"submit\">Associate</button>");
            sb.Append("</form></div>");

            return sb.ToString();
        }

        private void HandleMarketplaceManageAssociate(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            if (m_MarketplaceListingsService == null || request.HttpMethod != "POST")
            {
                response.Redirect(BasePath + "/marketplace/manage", HttpStatusCode.Redirect);
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string redirectUrl;

            if (!int.TryParse(FormValue(form, "listing_id"), out int listingId) || listingId <= 0
                    || !UUID.TryParse(FormValue(form, "source_folder_id"), out UUID sourceFolderId) || sourceFolderId == UUID.Zero)
            {
                redirectUrl = BasePath + "/marketplace/manage?message=" + Uri.EscapeDataString("Invalid request.");
            }
            else
            {
                MarketplaceListing listing = m_MarketplaceListingsService.GetListing(listingId);
                if (listing == null || listing.SellerID != session.PrincipalID)
                {
                    redirectUrl = BasePath + "/marketplace/manage?message=" + Uri.EscapeDataString("Listing not found.");
                }
                else if (m_InventoryService == null || m_UserAccountService == null)
                {
                    redirectUrl = BasePath + "/marketplace/manage?listing=" + listingId + "&message="
                            + Uri.EscapeDataString("Inventory service is not available on this grid.");
                }
                else
                {
                    try
                    {
                        string versionKey = listingId + "|" + DateTime.UtcNow.Ticks;
                        SnapshotResponse snapshot = MarketplaceInventoryOperations.SnapshotListingItem(
                                m_InventoryService,
                                m_UserAccountService,
                                UUID.Zero,
                                m_marketplaceServiceAccountId,
                                session.PrincipalID,
                                sourceFolderId,
                                versionKey);

                        if (!UUID.TryParse(snapshot.SnapshotFolderId, out UUID snapshotFolderId))
                        {
                            redirectUrl = BasePath + "/marketplace/manage?listing=" + listingId + "&message="
                                    + Uri.EscapeDataString("Snapshot did not return a valid item id.");
                        }
                        else
                        {
                            // Same simplification DirectDeliveryModule uses -
                            // no separate version-history concept yet, both
                            // listing_folder_id and version_folder_id point
                            // at this snapshot item.
                            m_MarketplaceListingsService.SetInventoryAssociation(
                                    listingId, snapshotFolderId, snapshotFolderId, snapshotFolderId, snapshot.SnapshotFingerprint);
                            redirectUrl = BasePath + "/marketplace/manage?listing=" + listingId + "&message="
                                    + Uri.EscapeDataString("Inventory associated: " + snapshot.Name + ". You can now list it.");
                        }
                    }
                    catch (MarketplaceInventoryException ex)
                    {
                        redirectUrl = BasePath + "/marketplace/manage?listing=" + listingId + "&message=" + Uri.EscapeDataString(ex.Message);
                    }
                }
            }

            response.Redirect(redirectUrl, HttpStatusCode.Redirect);
        }

        private static string TruncateText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength).TrimEnd() + "...";
        }

        // Redirect target for BuildAuthorizeUri when a resident hasn't
        // authorized Gloebit for the portal yet, and for a proactive
        // "link my Gloebit account" click with no purchase pending.
        private void HandleStoreGloebitAuthorize(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!m_gloebitEnabled || m_GloebitClient == null)
            {
                response.Redirect(BasePath + "/store?message=" + Uri.EscapeDataString("Gloebit purchases are not available on this grid."), HttpStatusCode.Redirect);
                return;
            }

            response.Redirect(m_GloebitClient.BuildAuthorizeUri(session.PrincipalID, session.Name).ToString(), HttpStatusCode.Redirect);
        }

        // Gloebit's OAuth2 redirect target - the query string it appends
        // (agentId/code) is exactly what BuildAuthorizeUri/GloebitClient
        // asked for, not session-cookie-authenticated (this request comes
        // from the resident's browser mid-redirect, but could in principle
        // arrive without our own session cookie depending on browser
        // referrer/SameSite behavior) - agentId in the URL is the identity
        // here, matching how the region-side module's own auth_complete
        // handler works.
        private void HandleStoreGloebitAuthComplete(IOSHttpRequest request, IOSHttpResponse response)
        {
            string code = request.QueryString.Get("code");

            if (!UUID.TryParse(request.QueryString.Get("agentId"), out UUID avatarId) || string.IsNullOrEmpty(code)
                    || m_GloebitClient == null || m_StoreService == null)
            {
                response.Redirect(BasePath + "/store?message=" + Uri.EscapeDataString("Gloebit authorization failed."), HttpStatusCode.Redirect);
                return;
            }

            bool ok = m_GloebitClient.ExchangeAccessToken(avatarId, code, out string accessToken, out string gloebitId, out string error);

            StoreGloebitAuth auth = m_StoreService.GetGloebitAuth(avatarId) ?? new StoreGloebitAuth { AvatarPrincipalID = avatarId, Created = DateTime.UtcNow };
            auth.Updated = DateTime.UtcNow;

            if (!ok)
            {
                auth.Authorized = false;
                m_StoreService.StoreGloebitAuth(auth);
                response.Redirect(BasePath + "/store?message=" + Uri.EscapeDataString("Gloebit authorization failed: " + error), HttpStatusCode.Redirect);
                return;
            }

            auth.AccessToken = accessToken;
            auth.GloebitID = gloebitId;
            auth.Authorized = true;
            m_StoreService.StoreGloebitAuth(auth);

            UUID pendingOrderId = UUID.Zero;
            lock (m_pendingGloebitOrders)
            {
                if (m_pendingGloebitOrders.TryGetValue(avatarId, out UUID orderId))
                {
                    pendingOrderId = orderId;
                    m_pendingGloebitOrders.Remove(avatarId);
                }
            }

            StoreOrder order = pendingOrderId != UUID.Zero ? m_StoreService.GetOrder(pendingOrderId) : null;
            StoreCatalogItem item = order != null ? m_StoreService.GetCatalogItem(order.CatalogItemID) : null;

            if (order == null || order.Status != "PendingPayment" || item == null)
            {
                response.Redirect(BasePath + "/store?message=" + Uri.EscapeDataString("Gloebit account linked."), HttpStatusCode.Redirect);
                return;
            }

            string message = SubmitGloebitTransaction(avatarId, order.ResidentName, order, item, auth);
            response.Redirect(BasePath + "/store?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // The required webhook - see PROJECT_LOG.md for why this isn't
        // optional: it's the only path that ever fires local
        // enact/consume completion. No session/cookie check - Gloebit's
        // own transaction processor calls this directly, identified only
        // by the unguessable transaction id embedded in the callback URL
        // Transact() gave it. Response contract Gloebit's queue expects:
        // a JSON array, [true] on success or [false,"<reason>"] on
        // failure - reproduced exactly, including the "pending" magic
        // retry string on the one path that can legitimately need a retry.
        private void HandleStoreGloebitTransaction(IOSHttpRequest request, IOSHttpResponse response)
        {
            OSDArray result = new OSDArray();

            if (m_StoreService == null || !UUID.TryParse(request.QueryString.Get("id"), out UUID transactionId))
            {
                result.Add(OSD.FromBoolean(false));
                result.Add(OSD.FromString("unknown transaction"));
                WriteGloebitJson(response, result);
                return;
            }

            StoreGloebitTransaction txn = m_StoreService.GetGloebitTransaction(transactionId);
            if (txn == null)
            {
                result.Add(OSD.FromBoolean(false));
                result.Add(OSD.FromString("unknown transaction"));
                WriteGloebitJson(response, result);
                return;
            }

            string state = request.QueryString.Get("state");
            switch (state)
            {
                case "enact":
                    if (!txn.Enacted)
                    {
                        // Pure balance-debit transaction, no in-world object
                        // delivered - nothing to enact locally, matching how
                        // the region-side module's own no-op enact/consume/
                        // cancel handlers work for this transaction shape.
                        txn.Enacted = true;
                        txn.Stage = "EnactAsset";
                        txn.Updated = DateTime.UtcNow;
                        m_StoreService.StoreGloebitTransaction(txn);
                    }
                    result.Add(OSD.FromBoolean(true));
                    break;

                case "consume":
                    if (!txn.Consumed)
                    {
                        txn.Consumed = true;
                        txn.Stage = "ConsumeAsset";
                        txn.Updated = DateTime.UtcNow;
                        m_StoreService.StoreGloebitTransaction(txn);

                        StoreOrder order = m_StoreService.GetOrder(txn.StoreOrderID);
                        if (order != null && order.Status == "PendingPayment")
                        {
                            order.Status = "Paid";
                            order.Updated = DateTime.UtcNow;
                            m_StoreService.StoreOrder(order);

                            StoreCatalogItem item = m_StoreService.GetCatalogItem(order.CatalogItemID);
                            if (item != null)
                                ProcessPaidOrder(order, item);
                        }
                    }
                    result.Add(OSD.FromBoolean(true));
                    break;

                case "cancel":
                    if (!txn.Cancelled)
                    {
                        txn.Cancelled = true;
                        txn.Stage = "Cancelled";
                        txn.Updated = DateTime.UtcNow;
                        m_StoreService.StoreGloebitTransaction(txn);

                        StoreOrder order = m_StoreService.GetOrder(txn.StoreOrderID);
                        if (order != null && order.Status == "PendingPayment")
                        {
                            order.Status = "PaymentFailed";
                            order.Updated = DateTime.UtcNow;
                            m_StoreService.StoreOrder(order);
                        }
                    }
                    result.Add(OSD.FromBoolean(true));
                    break;

                default:
                    result.Add(OSD.FromBoolean(false));
                    result.Add(OSD.FromString("unknown state"));
                    break;
            }

            WriteGloebitJson(response, result);
        }

        private static void WriteGloebitJson(IOSHttpResponse response, OSDArray arr)
        {
            response.ContentType = "application/json";
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(arr));
        }

        private void HandleStoreMyPurchases(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (m_StoreService == null)
            {
                WritePage(request, response, PageTitle("My Purchases"), "<h1>My Purchases</h1><p>The store is not available on this grid.</p>");
                return;
            }

            List<StoreOrder> orders = m_StoreService.GetOrdersByResident(session.PrincipalID).OrderByDescending(o => o.Created).ToList();

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-receipt\"></i> My Purchases</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/store\">Back to Store</a></p>");

            if (orders.Count == 0)
            {
                sb.Append("<p>You haven't bought anything yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Item</th><th>Currency</th><th>Amount</th><th>Status</th><th>Expires</th><th>Date</th></tr>");
                foreach (StoreOrder order in orders)
                {
                    StoreCatalogItem item = m_StoreService.GetCatalogItem(order.CatalogItemID);
                    sb.Append("<tr><td>").Append(Html(item != null ? item.Name : order.OrderType)).Append("</td>");
                    sb.Append("<td>").Append(order.CurrencyUsed).Append("</td>");
                    sb.Append("<td>").Append(order.AmountCharged.ToString("N0")).Append("</td>");
                    sb.Append("<td>").Append(Html(order.Status)).Append("</td>");
                    sb.Append("<td>").Append(order.ExpiresAt.HasValue ? order.ExpiresAt.Value.ToString("yyyy-MM-dd") : "-").Append("</td>");
                    sb.Append("<td>").Append(order.Created.ToString("yyyy-MM-dd HH:mm")).Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("My Purchases"), sb.ToString());
        }

        // Dispatches a newly-Paid order to the matching fulfillment path.
        // Both ConfluenceCurrency checkout and the Gloebit "consume" webhook
        // converge here once payment is confirmed - the one shared place to
        // log the purchase to Recent Activity, so both currencies get it
        // without duplicating the call at each payment path. Real gap this
        // closes: LogActivity previously had 8 call sites (login, avatar
        // import/switch, etc.) and none of them were Store purchases -
        // confirmed live, a resident who'd made real region-order/prim-pack
        // purchases saw only login entries on their own dashboard.
        private void ProcessPaidOrder(StoreOrder order, StoreCatalogItem item)
        {
            WebAccountAvatarLink link = m_WebAccountService?.GetLinkForAvatar(order.ResidentAvatarID);
            if (link != null)
            {
                string currencyLabel = order.CurrencyUsed == "Gloebit" ? "G$" : m_currencySymbol;
                m_WebAccountService.LogActivity(new WebActivityEntry
                {
                    WebAccountID = link.WebAccountID,
                    AvatarPrincipalID = order.ResidentAvatarID,
                    EventType = "store_purchase",
                    Description = "Bought \"" + item.Name + "\" for " + currencyLabel + " " + order.AmountCharged.ToString("N0")
                });
            }

            if (item.ItemType == "PrimPack")
                FulfillPrimPack(order, item);
            else if (item.ItemType == "RegionOrder")
                FulfillRegionOrder(order, item);
        }

        // Instant + persisted, no new communication channel - rides the
        // same /consoleweb remote-console mechanism Restart/Group
        // Auto-Invite/Land Search already use (RunRegionConsoleCommand).
        private void FulfillPrimPack(StoreOrder order, StoreCatalogItem item)
        {
            GridRegion region = order.TargetRegionID.HasValue && m_GridService != null
                    ? m_GridService.GetRegionByUUID(UUID.Zero, order.TargetRegionID.Value)
                    : null;

            if (region == null || string.IsNullOrEmpty(region.ServerURI))
            {
                order.Notes = "Fulfillment failed: target region is not reachable. An admin can retry from the Store Orders queue.";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return;
            }

            if (item.PrimAmount <= 0)
            {
                order.Notes = "Fulfillment failed: this catalog item has no prim amount configured.";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return;
            }

            // Additive - each region can already have a different baseline
            // MaxPrims (its own .ini either sets one or falls back to the
            // 15000 default), so a pack is always "+N on top of whatever
            // this region already has," never a flat replacement.
            string output = RunRegionConsoleCommand(region, "add-prim-limit " + region.RegionID + " " + item.PrimAmount);

            order.Status = "Fulfilled";
            order.ExpiresAt = item.DurationDays > 0 ? DateTime.UtcNow.AddDays(item.DurationDays) : (DateTime?)null;
            order.Notes = output;
            order.Updated = DateTime.UtcNow;
            m_StoreService.StoreOrder(order);
        }

        // Auto-generates the new region's .ini/port/location and launches
        // it automatically (see TryStartRegionProcess) - Start Region in
        // the admin queue is a manual retry path only, for when this
        // automatic launch itself fails.
        private void FulfillRegionOrder(StoreOrder order, StoreCatalogItem item)
        {
            if (string.IsNullOrEmpty(m_regionOrderTemplateIniPath) || !File.Exists(m_regionOrderTemplateIniPath) || string.IsNullOrEmpty(m_regionOrderGridRoot))
            {
                order.Notes = "Provisioning failed: [StoreService] RegionOrderTemplateIniPath/RegionOrderGridRoot is not configured.";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return;
            }

            int? port = AllocateRegionOrderPort();
            if (port == null)
            {
                order.Notes = "Provisioning failed: no free port in the configured range.";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return;
            }

            (int X, int Y)? location = AllocateRegionOrderLocation(order.RequestedLocationX, order.RequestedLocationY);
            if (location == null)
            {
                order.Notes = order.RequestedLocationX.HasValue
                        ? "Provisioning failed: the requested grid location (" + order.RequestedLocationX + "," + order.RequestedLocationY
                                + ") is no longer free or is outside the configured block."
                        : "Provisioning failed: no free grid location in the configured block.";
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
                return;
            }

            try
            {
                string slug = Slugify(order.RequestedRegionName) + "-" + order.ID.ToString().Replace("-", string.Empty).Substring(0, 8);
                string simRoot = Path.Combine(m_regionOrderGridRoot, "Simulators", slug);
                string regionsDir = Path.Combine(simRoot, "Regions");
                Directory.CreateDirectory(regionsDir);

                // Targeted token replacement on a cloned template, not a
                // full semantic rewrite - the template already has every
                // other section correctly configured for this grid.
                string templateText = File.ReadAllText(m_regionOrderTemplateIniPath);
                string logBase = Path.Combine(simRoot, "OpenSim");
                // ${1}, not bare $1 - a bare $1 immediately followed by a
                // digit (http_listener_port's replacement appends the raw
                // port number) gets parsed by .NET's regex engine as an
                // attempt to reference a much higher-numbered capture group
                // (e.g. "$1" + "9050" becomes the literal replacement string
                // "$19050", read as "group 19050") instead of "group 1, then
                // the literal text 9050" - found live: a real region order's
                // http_listener_port line came out as the literal text
                // "$19050", not a valid port, which would have failed to
                // start. The other three substitutions happen to start with
                // a quote character so they were never ambiguous, but ${1}
                // everywhere is the actually-correct, non-fragile form.
                templateText = Regex.Replace(templateText, @"(?m)^(\s*logfile\s*=\s*).*$", "${1}\"" + logBase + ".log\"");
                templateText = Regex.Replace(templateText, @"(?m)^(\s*StatsLogFile\s*=\s*).*$", "${1}\"" + logBase + "Stats.log\"");
                templateText = Regex.Replace(templateText, @"(?m)^(\s*regionload_regionsdir\s*=\s*).*$", "${1}\"" + regionsDir + "\"");
                templateText = Regex.Replace(templateText, @"(?m)^(\s*http_listener_port\s*=\s*).*$", "${1}" + port.Value);

                // Without this, a brand-new region has no estate at all,
                // and stock OpenSim's own startup code
                // (OpenSimBase.PopulateRegionEstateInfo) tries to
                // interactively prompt an admin to create/join one
                // (Console.GetCursorPosition, via ConsoleBase.Prompt) -
                // found live: that throws IOException("The handle is
                // invalid") and kills the whole process instantly on a
                // headless Process.Start()'d child with no real console
                // attached, well before the region ever registers with
                // the grid. DefaultEstateName makes PopulateRegionEstateInfo
                // auto-create (or join, if a later order from the same
                // resident reuses the name) the estate with zero
                // prompting.
                //
                // That alone isn't enough, though - found live, second
                // round: a freshly auto-created estate has no *owner*
                // either, and OpenSimBase.SetUpEstateOwner has its own
                // separate interactive prompt for that (same fatal
                // IOException). DefaultEstateOwnerName (split on the
                // space into first/last) skips it - and since this is
                // grid mode, not standalone, SetUpEstateOwner looks up
                // an existing UserAccount by that name rather than
                // prompting for a password/email/UUID to create one
                // (those prompts are explicitly Standalone-only in the
                // code), so this also correctly makes the purchasing
                // resident the estate's real owner, not just avoiding a
                // crash.
                //
                // Both appended as a second [Estates] section after the
                // template's own (commented-out) one - Nini merges
                // repeated section headers within one file rather than
                // erroring. Always written, even when the resident chose
                // to join an existing estate below (via TargetEstate,
                // checked first by PopulateRegionEstateInfo) - if that
                // join fails for any reason (e.g. the estate was deleted
                // between checkout and provisioning), PopulateRegionEstateInfo
                // falls through to this DefaultEstateName/Owner path
                // instead of the interactive prompt that crashed this
                // whole flow twice already.
                string estateName = !string.IsNullOrEmpty(order.RequestedEstateName)
                        ? order.RequestedEstateName
                        : order.ResidentName + "'s Estate";
                templateText += "\r\n[Estates]\r\n    DefaultEstateName = \"" + estateName + "\"\r\n"
                        + "    DefaultEstateOwnerName = \"" + order.ResidentName + "\"\r\n";

                File.WriteAllText(Path.Combine(simRoot, "OpenSim.ini"), templateText);

                UUID regionId = UUID.Random();
                StringBuilder regionIni = new StringBuilder();
                regionIni.Append("[").Append(order.RequestedRegionName).Append("]\r\n");
                regionIni.Append("RegionUUID = ").Append(regionId).Append("\r\n");
                regionIni.Append("Location = ").Append(location.Value.X).Append(",").Append(location.Value.Y).Append("\r\n");
                regionIni.Append("InternalAddress = 0.0.0.0\r\n");
                regionIni.Append("InternalPort = ").Append(port.Value).Append("\r\n");
                regionIni.Append("AllowAlternatePorts = False\r\n");
                regionIni.Append("ExternalHostName = ").Append(m_regionOrderExternalHostName).Append("\r\n");
                if (item.RegionSizeX > 0)
                    regionIni.Append("SizeX = ").Append(item.RegionSizeX).Append("\r\n");
                if (item.RegionSizeY > 0)
                    regionIni.Append("SizeY = ").Append(item.RegionSizeY).Append("\r\n");
                if (item.PrimAmount > 0)
                    regionIni.Append("MaxPrims = ").Append(item.PrimAmount).Append("\r\n");
                // The resident's checkout choice to join one of their own
                // existing estates. TargetEstate is a per-REGION setting
                // (RegionInfo.GetSetting reads it from this file's own
                // section, via m_extraSettings - unlike DefaultEstateName/
                // Owner above, which live in OpenSim.ini's [Estates]
                // section instead), and PopulateRegionEstateInfo checks it
                // before DefaultEstateName, joining by estate ID directly -
                // no name-collision ambiguity, and no owner setup needed
                // since the estate they're joining already has one.
                if (order.RequestedEstateID.HasValue)
                    regionIni.Append("TargetEstate = ").Append(order.RequestedEstateID.Value).Append("\r\n");
                File.WriteAllText(Path.Combine(regionsDir, "Regions.ini"), regionIni.ToString());

                order.AllocatedPort = port.Value;
                order.AllocatedLocationX = location.Value.X;
                order.AllocatedLocationY = location.Value.Y;
                order.SimulatorFolderName = slug;

                // Fully automatic per the user's explicit direction - the
                // one-click admin gate this originally had was a
                // deliberate testing scaffold for exactly the two real
                // startup crashes it caught (the estate-assignment and
                // estate-owner interactive prompts, both fixed above),
                // not the intended real-world behavior. Payment success
                // now launches the process directly; the admin "Start
                // Region" button (HandleAdminStoreOrdersStart) still
                // exists purely as a manual retry path for when this
                // automatic attempt itself fails.
                if (TryStartRegionProcess(order, out int exitCode, out string startLogPath))
                {
                    order.StartedAt = DateTime.UtcNow;
                    order.Status = "Active";
                    order.Notes = "Provisioned and started automatically.";
                }
                else
                {
                    order.Status = "AwaitingStart";
                    order.Notes = "Provisioned, but the automatic start failed - the process exited within 3 seconds "
                            + "(exit code " + exitCode + "). See " + startLogPath + ". An admin can retry from the Store Orders queue.";
                }

                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
            }
            catch (Exception e)
            {
                order.Notes = "Provisioning failed: " + e.Message;
                order.Updated = DateTime.UtcNow;
                m_StoreService.StoreOrder(order);
            }
        }

        // No filesystem scan - ports are grid-wide, checked against every
        // currently-registered region (wide sanity bound, since
        // GetRegionRange needs explicit bounds) plus any other order still
        // holding a port (AwaitingStart/Active).
        private int? AllocateRegionOrderPort()
        {
            HashSet<int> usedPorts = new HashSet<int>();

            if (m_GridService != null)
            {
                List<GridRegion> allRegions = m_GridService.GetRegionRange(UUID.Zero,
                        (int)Util.RegionToWorldLoc(0), (int)Util.RegionToWorldLoc(20000),
                        (int)Util.RegionToWorldLoc(0), (int)Util.RegionToWorldLoc(20000));
                foreach (GridRegion r in allRegions)
                {
                    if (Uri.TryCreate(r.ServerURI, UriKind.Absolute, out Uri uri))
                        usedPorts.Add(uri.Port);
                }
            }

            if (m_StoreService != null)
            {
                foreach (StoreOrder o in m_StoreService.GetAllOrders())
                {
                    if (o.AllocatedPort.HasValue && (o.Status == "AwaitingStart" || o.Status == "Active"))
                        usedPorts.Add(o.AllocatedPort.Value);
                }
            }

            for (int port = m_regionOrderPortStart; port <= m_regionOrderPortEnd; port++)
            {
                if (!usedPorts.Contains(port))
                    return port;
            }

            return null;
        }

        // Scoped to the configured coordinate block only - that block is
        // dedicated to region orders, so there's no need to scan the whole
        // grid the way the port allocator above does.
        // Shared by AllocateRegionOrderLocation and BuildStoreOrder's own
        // best-effort checkout-time check - one source of truth for what
        // counts as "taken" (a real registered region, or another order
        // still holding its allocated spot).
        private HashSet<(int, int)> ComputeUsedRegionOrderLocations()
        {
            HashSet<(int, int)> usedLocations = new HashSet<(int, int)>();

            if (m_GridService != null)
            {
                List<GridRegion> regionsInBlock = m_GridService.GetRegionRange(UUID.Zero,
                        (int)Util.RegionToWorldLoc((uint)m_regionOrderGridXStart), (int)Util.RegionToWorldLoc((uint)m_regionOrderGridXEnd),
                        (int)Util.RegionToWorldLoc((uint)m_regionOrderGridYStart), (int)Util.RegionToWorldLoc((uint)m_regionOrderGridYEnd));
                foreach (GridRegion r in regionsInBlock)
                    usedLocations.Add((r.RegionCoordX, r.RegionCoordY));
            }

            if (m_StoreService != null)
            {
                foreach (StoreOrder o in m_StoreService.GetAllOrders())
                {
                    if (o.AllocatedLocationX.HasValue && o.AllocatedLocationY.HasValue && (o.Status == "AwaitingStart" || o.Status == "Active"))
                        usedLocations.Add((o.AllocatedLocationX.Value, o.AllocatedLocationY.Value));
                }
            }

            return usedLocations;
        }

        // requestedX/Y come from the resident's own checkout choice
        // (StoreOrder.RequestedLocationX/Y) - re-validated here rather than
        // trusted from BuildStoreOrder's earlier check, since time has
        // passed and another order could have claimed the same spot in the
        // meantime. Null/null (the common case) auto-picks the first free
        // spot, same as before this feature existed. A specific request
        // that's no longer valid returns null - the caller fails the order
        // outright rather than silently placing it somewhere the resident
        // didn't ask for.
        private (int X, int Y)? AllocateRegionOrderLocation(int? requestedX, int? requestedY)
        {
            HashSet<(int, int)> usedLocations = ComputeUsedRegionOrderLocations();

            if (requestedX.HasValue && requestedY.HasValue)
            {
                if (requestedX.Value < m_regionOrderGridXStart || requestedX.Value > m_regionOrderGridXEnd
                        || requestedY.Value < m_regionOrderGridYStart || requestedY.Value > m_regionOrderGridYEnd)
                    return null;

                return usedLocations.Contains((requestedX.Value, requestedY.Value)) ? null : (requestedX.Value, requestedY.Value);
            }

            for (int y = m_regionOrderGridYStart; y <= m_regionOrderGridYEnd; y++)
            {
                for (int x = m_regionOrderGridXStart; x <= m_regionOrderGridXEnd; x++)
                {
                    if (!usedLocations.Contains((x, y)))
                        return (x, y);
                }
            }

            return null;
        }

        private static string Slugify(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "region";

            StringBuilder sb = new StringBuilder();
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                    sb.Append('_');
            }

            string result = sb.ToString().Trim('_');
            return result.Length > 0 ? result : "region";
        }

        private void HandleAdminStore(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Store Catalog"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }
            if (m_StoreService == null)
            {
                WritePage(request, response, PageTitle("Store Catalog"),
                        "<h1>Store Catalog</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Store service is not available.</p>");
                return;
            }

            List<StoreCatalogItem> items = m_StoreService.GetAllCatalogItems().OrderBy(i => i.SortOrder).ToList();
            UUID editId = UUID.TryParse(request.QueryString.Get("edit"), out UUID parsedEditId) ? parsedEditId : UUID.Zero;
            StoreCatalogItem editItem = editId != UUID.Zero ? m_StoreService.GetCatalogItem(editId) : null;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Store Catalog</h1><p><a href=\"").Append(BasePath).Append("/admin\">Back to admin</a> | <a href=\"")
              .Append(BasePath).Append("/admin/store/orders\">Store Orders</a></p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            if (items.Count > 0)
            {
                sb.Append("<table><tr><th>Name</th><th>Type</th><th>Prims</th><th>Region Size</th><th>").Append(m_currencySymbol).Append("</th><th>G$</th><th>Days</th><th>Active</th><th></th></tr>");
                foreach (StoreCatalogItem item in items)
                {
                    sb.Append("<tr><td>").Append(Html(item.Name)).Append("</td>");
                    sb.Append("<td>").Append(item.ItemType).Append("</td>");
                    sb.Append("<td>").Append(item.PrimAmount.ToString("N0")).Append("</td>");
                    sb.Append("<td>").Append(item.RegionSizeX).Append("&times;").Append(item.RegionSizeY).Append("</td>");
                    sb.Append("<td>").Append(item.PriceConfluence.ToString("N0")).Append("</td>");
                    sb.Append("<td>").Append(item.PriceGloebits.ToString("N0")).Append("</td>");
                    sb.Append("<td>").Append(item.DurationDays).Append("</td>");
                    sb.Append("<td>").Append(item.IsActive ? "Yes" : "No").Append("</td>");
                    sb.Append("<td><a href=\"").Append(BasePath).Append("/admin/store?edit=").Append(item.ID).Append("\">Edit</a></td></tr>");
                }
                sb.Append("</table>");
            }
            else
            {
                sb.Append("<p>No catalog items yet.</p>");
            }

            sb.Append("<h2>").Append(editItem != null ? "Edit Item" : "Add Item").Append("</h2>");
            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/store/save\">");
            sb.Append("<input type=\"hidden\" name=\"id\" value=\"").Append(editItem != null ? editItem.ID.ToString() : UUID.Zero.ToString()).Append("\">");
            sb.Append("<p>Name <input type=\"text\" name=\"name\" value=\"").Append(editItem != null ? Html(editItem.Name) : string.Empty).Append("\" required></p>");
            sb.Append("<p>Description <textarea name=\"description\">").Append(editItem != null ? Html(editItem.Description) : string.Empty).Append("</textarea></p>");
            sb.Append("<p>Type <select name=\"item_type\">");
            sb.Append("<option value=\"PrimPack\"").Append(editItem == null || editItem.ItemType == "PrimPack" ? " selected" : string.Empty).Append(">Prim Pack</option>");
            sb.Append("<option value=\"RegionOrder\"").Append(editItem != null && editItem.ItemType == "RegionOrder" ? " selected" : string.Empty).Append(">Region Order</option>");
            sb.Append("</select></p>");
            sb.Append("<p>Prim capacity (PrimPack: prims added on top of the region's current cap, whatever that already is. RegionOrder: starting capacity, 0 = default 15000) <input type=\"number\" name=\"prim_amount\" value=\"")
              .Append(editItem != null ? editItem.PrimAmount : 0).Append("\"></p>");
            sb.Append("<p>Region size X / Y in meters (RegionOrder only, 0 = default 256) <input type=\"number\" name=\"region_size_x\" value=\"")
              .Append(editItem != null ? editItem.RegionSizeX : 0).Append("\"> <input type=\"number\" name=\"region_size_y\" value=\"")
              .Append(editItem != null ? editItem.RegionSizeY : 0).Append("\"></p>");
            {
                // One price, same number in both currencies whenever both
                // are offered - residents shouldn't be able to see one
                // payment method priced higher than another for the exact
                // same item. Existing items where an admin previously
                // entered two different numbers show the Confluence value
                // as the starting point; saving the form re-synchronizes
                // both to whichever one number is submitted.
                int existingPrice = editItem != null ? Math.Max(editItem.PriceConfluence, editItem.PriceGloebits) : 0;
                bool offerConfluence = editItem == null || editItem.PriceConfluence > 0;
                bool offerGloebit = editItem == null || editItem.PriceGloebits > 0;
                sb.Append("<p>Price (same amount in both currencies whenever both are offered - never price one payment method higher than the other) <input type=\"number\" name=\"price\" value=\"")
                  .Append(existingPrice).Append("\"></p>");
                sb.Append("<p><label><input type=\"checkbox\" name=\"offer_confluence\" value=\"true\"").Append(offerConfluence ? " checked" : string.Empty).Append("> Offer via Confluence Currency</label> ");
                sb.Append("<label><input type=\"checkbox\" name=\"offer_gloebit\" value=\"true\"").Append(offerGloebit ? " checked" : string.Empty).Append("> Offer via Gloebit</label></p>");
            }
            sb.Append("<p>Duration days (0 = never expires) <input type=\"number\" name=\"duration_days\" value=\"")
              .Append(editItem != null ? editItem.DurationDays : 0).Append("\"></p>");
            sb.Append("<p>Sort order <input type=\"number\" name=\"sort_order\" value=\"").Append(editItem != null ? editItem.SortOrder : 0).Append("\"></p>");
            sb.Append("<p><label><input type=\"checkbox\" name=\"is_active\" value=\"true\"").Append(editItem == null || editItem.IsActive ? " checked" : string.Empty).Append("> Active</label></p>");
            sb.Append("<p><button type=\"submit\">Save</button>");
            if (editItem != null)
                sb.Append(" <a href=\"").Append(BasePath).Append("/admin/store\">Cancel</a>");
            sb.Append("</p></form>");

            WritePage(request, response, PageTitle("Store Catalog"), sb.ToString());
        }

        private void HandleAdminStoreSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_StoreService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                UUID.TryParse(FormValue(form, "id"), out UUID id);

                StoreCatalogItem item = id != UUID.Zero ? m_StoreService.GetCatalogItem(id) : null;
                if (item == null)
                    item = new StoreCatalogItem { ID = UUID.Random(), Created = DateTime.UtcNow };

                item.Name = FormValue(form, "name") ?? string.Empty;
                item.Description = FormValue(form, "description") ?? string.Empty;
                item.ItemType = FormValue(form, "item_type") == "RegionOrder" ? "RegionOrder" : "PrimPack";
                int.TryParse(FormValue(form, "prim_amount"), out int primAmount);
                item.PrimAmount = Math.Max(0, primAmount);
                int.TryParse(FormValue(form, "region_size_x"), out int sizeX);
                int.TryParse(FormValue(form, "region_size_y"), out int sizeY);
                item.RegionSizeX = Math.Max(0, sizeX);
                item.RegionSizeY = Math.Max(0, sizeY);
                // Same exchange rate for both currencies, deliberately -
                // one price field, applied identically to whichever
                // currencies are offered, so residents never see one
                // payment method priced higher than another for the same
                // item (see PROJECT_LOG.md).
                int.TryParse(FormValue(form, "price"), out int price);
                price = Math.Max(0, price);
                item.PriceConfluence = FormValue(form, "offer_confluence") == "true" ? price : 0;
                item.PriceGloebits = FormValue(form, "offer_gloebit") == "true" ? price : 0;
                int.TryParse(FormValue(form, "duration_days"), out int durationDays);
                item.DurationDays = Math.Max(0, durationDays);
                int.TryParse(FormValue(form, "sort_order"), out int sortOrder);
                item.SortOrder = sortOrder;
                item.IsActive = FormValue(form, "is_active") == "true";
                item.Updated = DateTime.UtcNow;

                m_StoreService.StoreCatalogItem(item);
            }

            response.Redirect(BasePath + "/admin/store", HttpStatusCode.Redirect);
        }

        // Distinguishes "genuinely down" from "the process is running but
        // not reachable from outside this server" - found live: a freshly
        // auto-created Store region order answers fine on 127.0.0.1 (the
        // process really is up) but not on its own registered public
        // ServerURI, because its auto-allocated port (unlike the grid's
        // original, manually-configured regions) was never added to the
        // router's port-forwarding. IsRegionAlive's single public-URI
        // probe can't tell these two failure modes apart and would just
        // read "Offline" either way - actively misleading, since the fix
        // for "genuinely down" (check the region's own OpenSim.log) and
        // "not forwarded" (open the port on the router) are completely
        // different actions for whoever's looking at this pill. Reused by
        // both My Regions and the Store Orders admin queue rather than
        // duplicated in each.
        private static string RenderRegionReachabilityPill(GridRegion region)
        {
            if (IsRegionAlive(region, 1000))
                return "<span class=\"pill pill-yes\">Online</span>";

            bool reachableLocally = Uri.TryCreate(region.ServerURI, UriKind.Absolute, out Uri uri)
                    && Util.IsHostAlive("http://127.0.0.1:" + uri.Port + "/", 1000);

            return reachableLocally
                    ? "<span class=\"pill pill-no\" title=\"The region process is running, but its public address isn't reachable from outside this server - the port likely needs to be added to the router's port forwarding.\">Not reachable publicly</span>"
                    : "<span class=\"pill pill-no\">Offline</span>";
        }

        private void HandleAdminStoreOrders(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Store Orders"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }
            if (m_StoreService == null)
            {
                WritePage(request, response, PageTitle("Store Orders"),
                        "<h1>Store Orders</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Store service is not available.</p>");
                return;
            }

            List<StoreOrder> orders = m_StoreService.GetAllOrders().OrderByDescending(o => o.Created).ToList();

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Store Orders</h1><p><a href=\"").Append(BasePath).Append("/admin\">Back to admin</a> | <a href=\"")
              .Append(BasePath).Append("/admin/store\">Store Catalog</a></p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            if (orders.Count == 0)
            {
                sb.Append("<p>No orders yet.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Date</th><th>Resident</th><th>Item</th><th>Type</th><th>Currency</th><th>Amount</th><th>Status</th><th>Expires</th><th>Actions</th></tr>");
                foreach (StoreOrder order in orders)
                {
                    StoreCatalogItem item = m_StoreService.GetCatalogItem(order.CatalogItemID);
                    sb.Append("<tr><td>").Append(order.Created.ToString("yyyy-MM-dd HH:mm")).Append("</td>");
                    sb.Append("<td>").Append(Html(order.ResidentName)).Append("</td>");
                    sb.Append("<td>").Append(Html(item != null ? item.Name : order.OrderType))
                      .Append(order.OrderType == "RegionOrder" && !string.IsNullOrEmpty(order.RequestedRegionName) ? " (" + Html(order.RequestedRegionName) + ")" : string.Empty)
                      .Append("</td>");
                    sb.Append("<td>").Append(order.OrderType).Append("</td>");
                    sb.Append("<td>").Append(order.CurrencyUsed).Append("</td>");
                    sb.Append("<td>").Append(order.AmountCharged.ToString("N0")).Append("</td>");
                    sb.Append("<td>").Append(Html(order.Status));
                    if (order.OrderType == "RegionOrder" && order.Status == "Active" && m_GridService != null
                            && !string.IsNullOrEmpty(order.RequestedRegionName))
                    {
                        List<GridRegion> matches = m_GridService.GetRegionsByName(UUID.Zero, order.RequestedRegionName, 1);
                        if (matches.Count > 0)
                            sb.Append(" ").Append(RenderRegionReachabilityPill(matches[0]));
                    }
                    sb.Append("</td>");
                    sb.Append("<td>").Append(order.ExpiresAt.HasValue ? order.ExpiresAt.Value.ToString("yyyy-MM-dd") : "-").Append("</td>");

                    sb.Append("<td>");
                    if (order.Status == "Fulfilled" || order.Status == "Active")
                    {
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/store/orders/renew\" style=\"display:inline\">");
                        sb.Append("<input type=\"hidden\" name=\"order_id\" value=\"").Append(order.ID).Append("\">");
                        sb.Append("<input type=\"number\" name=\"extend_days\" value=\"30\" style=\"width:60px\">");
                        sb.Append("<button type=\"submit\">Renew</button></form> ");
                    }
                    if (order.OrderType == "RegionOrder" && order.Status == "AwaitingStart" && !order.StartedAt.HasValue)
                    {
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath)
                          .Append("/admin/store/orders/start\" style=\"display:inline\" onsubmit=\"return confirm('Start a new region process for this order?');\">");
                        sb.Append("<input type=\"hidden\" name=\"order_id\" value=\"").Append(order.ID).Append("\">");
                        sb.Append("<button type=\"submit\">Start Region</button></form> ");
                    }
                    if (order.OrderType == "RegionOrder" && (order.Status == "AwaitingStart" || order.Status == "Active"))
                    {
                        sb.Append("<small>Removed from the <a href=\"").Append(BasePath).Append("/admin/simulators\">Simulators</a> page, not here.</small>");
                    }
                    if (!string.IsNullOrEmpty(order.Notes))
                        sb.Append("<br><small>").Append(Html(order.Notes)).Append("</small>");
                    sb.Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Store Orders"), sb.ToString());
        }

        // Just pushes ExpiresAt forward, no re-charge - per the confirmed
        // no-auto-billing decision, renewal is entirely an admin action.
        private void HandleAdminStoreOrdersRenew(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_StoreService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Order not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "order_id"), out UUID orderId))
                {
                    StoreOrder order = m_StoreService.GetOrder(orderId);
                    if (order == null)
                    {
                        message = "Order not found.";
                    }
                    else
                    {
                        int.TryParse(FormValue(form, "extend_days"), out int extendDays);
                        extendDays = Math.Max(1, extendDays);

                        DateTime baseline = order.ExpiresAt.HasValue && order.ExpiresAt.Value > DateTime.UtcNow ? order.ExpiresAt.Value : DateTime.UtcNow;
                        order.ExpiresAt = baseline.AddDays(extendDays);
                        order.Notes = (string.IsNullOrEmpty(order.Notes) ? string.Empty : order.Notes + "\n")
                                + "Renewed " + extendDays + " day(s) by " + session.Name + " on " + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".";
                        order.Updated = DateTime.UtcNow;
                        m_StoreService.StoreOrder(order);

                        message = "Extended by " + extendDays + " day(s).";
                    }
                }
            }

            response.Redirect(BasePath + "/admin/store/orders?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // The riskiest new mechanism in this feature - first process-
        // spawning code in this codebase (see PROJECT_LOG.md). Admin-only,
        // guarded by order.StartedAt so a double-click can't launch two
        // processes for the same order, fire-and-forget (not supervised
        // after launch - matches this grid's existing manual-launch
        // posture; nothing anywhere in this codebase supervises OpenSim.exe
        // once it's running).
        private void HandleAdminStoreOrdersStart(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_StoreService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Order not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "order_id"), out UUID orderId))
                {
                    StoreOrder order = m_StoreService.GetOrder(orderId);
                    if (order == null || order.OrderType != "RegionOrder" || order.Status != "AwaitingStart" || string.IsNullOrEmpty(order.SimulatorFolderName))
                    {
                        message = "This order is not awaiting start.";
                    }
                    else if (order.StartedAt.HasValue)
                    {
                        message = "Start Region was already triggered for this order.";
                    }
                    else
                    {
                        // Manual retry path only now - the normal, happy-
                        // path launch happens automatically inside
                        // FulfillRegionOrder right after payment. This
                        // button only ever appears when that automatic
                        // attempt itself failed (Status stays
                        // AwaitingStart with StartedAt unset), so this
                        // is purely the "try again by hand" escape
                        // hatch, using the exact same TryStartRegionProcess
                        // helper.
                        try
                        {
                            order.Updated = DateTime.UtcNow;

                            if (TryStartRegionProcess(order, out int exitCode, out string logPath))
                            {
                                order.StartedAt = DateTime.UtcNow;
                                order.Status = "Active";
                                order.Notes = (string.IsNullOrEmpty(order.Notes) ? string.Empty : order.Notes + "\n")
                                        + "Region process started by " + session.Name + " on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC.";
                                m_StoreService.StoreOrder(order);
                                message = "Region process started for " + order.RequestedRegionName + " (port " + order.AllocatedPort + ").";
                            }
                            else
                            {
                                order.Status = "AwaitingStart";
                                order.Notes = (string.IsNullOrEmpty(order.Notes) ? string.Empty : order.Notes + "\n")
                                        + "Start attempt by " + session.Name + " on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
                                        + " UTC failed - the process exited within 3 seconds (exit code " + exitCode + "). See " + logPath + ".";
                                m_StoreService.StoreOrder(order);
                                message = "Region process for " + order.RequestedRegionName + " failed to start - it exited immediately (exit code "
                                        + exitCode + "). Check " + logPath + " and the region's own OpenSim.log, then try again.";
                            }
                        }
                        catch (Exception e)
                        {
                            message = "Failed to start region process: " + e.Message;
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/store/orders?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Shared by both the automatic launch (FulfillRegionOrder, right
        // after payment succeeds) and the admin's manual retry button
        // (HandleAdminStoreOrdersStart, only reachable when the automatic
        // attempt itself failed) - one implementation of the actual
        // Process.Start + liveness check, so the two callers can't drift.
        // Returns true and starts the order's process; on failure returns
        // false with the child's exit code and log path so the caller can
        // report why. Still fire-and-forget past the 3-second check - see
        // the comment above HandleAdminStoreOrdersStart for why that's
        // this codebase's existing posture, not a new one.
        private bool TryStartRegionProcess(StoreOrder order, out int exitCode, out string logPath)
        {
            return TryStartRegionProcess(order.SimulatorFolderName, out exitCode, out logPath);
        }

        // Generalized from the Store-only version above so the admin
        // Simulators page (any discovered simulator, not just Store-
        // ordered ones) can start a region the exact same proven way -
        // same -background=true, same 3-second crash check, one
        // implementation either caller can't drift from.
        private bool TryStartRegionProcess(string simulatorFolderName, out int exitCode, out string logPath)
        {
            exitCode = 0;

            string exePath = Path.Combine(m_regionOrderGridRoot, "OpenSim.exe");
            string relativeIniArg = Path.Combine("Simulators", simulatorFolderName, "OpenSim.ini");
            string simFolder = Path.Combine(m_regionOrderGridRoot, "Simulators", simulatorFolderName);
            string logFilePath = Path.Combine(simFolder, "start.log");
            logPath = logFilePath;

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exePath,
                // -background=true selects OpenSimBackground ("Consoleless
                // OpenSimulator region server") instead of the normal
                // interactive console loop - found live that without it,
                // Application.cs's `while (true) { MainConsole.Instance.
                // Prompt(); }` spins as fast as it can (hundreds of
                // thousands of lines/minute) because a CreateNoWindow child
                // has no real console screen buffer for ReadLine's cursor-
                // position calls to read, and every failed attempt is
                // immediately retried with no backoff. OpenSimBackground
                // blocks on a wait handle instead of ever calling Prompt(),
                // so it never hits this - and it doesn't affect the remote
                // web console (/consoleweb, used by RunRegionConsoleCommand
                // for things like add-prim-limit), which is wired up during
                // normal region-module startup either way.
                Arguments = "-inifile=" + relativeIniArg + " -background=true",
                WorkingDirectory = m_regionOrderGridRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            File.WriteAllText(logFilePath, string.Empty);
            Process proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) { try { File.AppendAllText(logFilePath, e.Data + Environment.NewLine); } catch { } } };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) { try { File.AppendAllText(logFilePath, e.Data + Environment.NewLine); } catch { } } };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Brief liveness check, not full health monitoring - found live
            // that a region can crash within the first second (an unhandled
            // startup exception, e.g. the estate-assignment prompt issue
            // fixed elsewhere in this file) while a naive "Process.Start
            // returned, so it worked" check would still report success.
            System.Threading.Thread.Sleep(3000);

            if (proc.HasExited)
            {
                exitCode = proc.ExitCode;
                return false;
            }

            return true;
        }

        #endregion Store

        #region Admin: region .ini config file viewer/editor

        // Motivated directly by a real, observed data-loss bug: automated
        // writes via RegionInfo.SaveRegionToFile (Nini-based) silently drop
        // any key considered "at its default" and every comment on that
        // file - confirmed live against Sandbox's own Regions.ini after one
        // add-prim-limit call (see PROJECT_LOG.md). This page exists so an
        // admin can actually see - and, if needed, hand-fix - what's really
        // in a region's config without filesystem/RDP access, without this
        // editor itself repeating the same loss: editing here writes the
        // exact raw bytes the admin typed, with zero Nini round-trip.
        // Changes only take effect on that region's NEXT start/restart -
        // there's no live-apply mechanism for arbitrary ini keys the way
        // the Store's own add-prim-limit/set-prim-limit console commands
        // have for prim capacity specifically.
        private List<(string RegionName, UUID RegionID, string FilePath)> DiscoverRegionIniFiles()
        {
            List<(string, UUID, string)> results = new List<(string, UUID, string)>();

            if (string.IsNullOrEmpty(m_regionOrderGridRoot))
                return results;

            string simulatorsRoot = Path.Combine(m_regionOrderGridRoot, "Simulators");
            if (!Directory.Exists(simulatorsRoot))
                return results;

            foreach (string simFolder in Directory.GetDirectories(simulatorsRoot))
            {
                string regionsDir = Path.Combine(simFolder, "Regions");
                if (!Directory.Exists(regionsDir))
                    continue;

                foreach (string file in Directory.GetFiles(regionsDir, "*.ini"))
                {
                    try
                    {
                        // Read-only parse - never calls .Save(), so listing
                        // regions can never itself trigger the comment/
                        // default-key loss this whole feature exists to
                        // work around.
                        IConfigSource source = new IniConfigSource(file);
                        foreach (IConfig config in source.Configs)
                        {
                            UUID.TryParse(config.GetString("RegionUUID", string.Empty), out UUID regionId);
                            results.Add((config.Name, regionId, file));
                        }
                    }
                    catch (Exception e)
                    {
                        m_log.Warn("[WEB INTERFACE]: Could not parse region ini " + file, e);
                    }
                }
            }

            return results;
        }

        private void HandleAdminRegionIniList(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Region Config Files"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            List<(string RegionName, UUID RegionID, string FilePath)> regions = DiscoverRegionIniFiles();

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-file-earmark-code\"></i> Region Config Files</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/admin\">Back to admin</a></p>");
            sb.Append("<p>Every region's own <code>.ini</code> file, discovered under <code>Simulators\\*\\Regions\\</code> "
                    + "on this host. Editing here writes the raw file directly - no validation, no live effect. "
                    + "Changes take effect the next time that region's process (re)starts.</p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            if (regions.Count == 0)
            {
                sb.Append("<p>No region config files found under the configured grid root.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Region</th><th>RegionID</th><th>File</th><th></th></tr>");
                foreach (var r in regions.OrderBy(r => r.RegionName, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append("<tr><td>").Append(Html(r.RegionName)).Append("</td>");
                    sb.Append("<td><code>").Append(r.RegionID).Append("</code></td>");
                    sb.Append("<td><code>").Append(Html(r.FilePath)).Append("</code></td>");
                    sb.Append("<td><a href=\"").Append(BasePath).Append("/admin/regions/ini/edit?path=")
                      .Append(Uri.EscapeDataString(r.FilePath)).Append("\">View / Edit</a></td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Region Config Files"), sb.ToString());
        }

        private void HandleAdminRegionIniEdit(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Edit Region Config"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            // Never trusts a client-supplied path directly - only ever
            // acts on a path this same discovery scan just found, same
            // reverification discipline as every other self-service/admin
            // action in this file that takes an ID from a form.
            List<(string RegionName, UUID RegionID, string FilePath)> discovered = DiscoverRegionIniFiles();
            string requestedPath = request.QueryString.Get("path");
            string filePath = null;
            UUID regionId = UUID.Zero;
            string regionName = null;

            foreach (var r in discovered)
            {
                if (string.Equals(r.FilePath, requestedPath, StringComparison.OrdinalIgnoreCase))
                {
                    filePath = r.FilePath;
                    regionId = r.RegionID;
                    regionName = r.RegionName;
                    break;
                }
            }

            if (filePath == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, PageTitle("Edit Region Config"), "<h1>Not found</h1><p>That config file wasn't found by the current discovery scan.</p>");
                return;
            }

            string message = string.Empty;

            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string content = FormValue(form, "content") ?? string.Empty;

                try
                {
                    File.WriteAllText(filePath, content);
                    message = "Saved. Restart " + regionName + " for this to take effect.";
                }
                catch (Exception e)
                {
                    message = "Save failed: " + e.Message;
                }
            }

            string fileText;
            try
            {
                fileText = File.ReadAllText(filePath);
            }
            catch (Exception e)
            {
                fileText = string.Empty;
                if (string.IsNullOrEmpty(message))
                    message = "Could not read file: " + e.Message;
            }

            GridRegion liveRegion = regionId != UUID.Zero && m_GridService != null
                    ? m_GridService.GetRegionByUUID(UUID.Zero, regionId)
                    : null;

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-file-earmark-code\"></i> ").Append(Html(regionName)).Append("</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/admin/regions/ini\">Back to Region Config Files</a></p>");
            sb.Append("<p><code>").Append(Html(filePath)).Append("</code></p>");

            if (!string.IsNullOrEmpty(message))
                sb.Append("<p>").Append(Html(message)).Append("</p>");

            sb.Append("<p><strong>Changes here only take effect the next time this region's process (re)starts</strong> "
                    + "- this is a raw file write, not a live console command.</p>");

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/regions/ini/edit?path=")
              .Append(Uri.EscapeDataString(filePath)).Append("\">");
            sb.Append("<textarea name=\"content\" rows=\"30\" style=\"width:100%;font-family:monospace;white-space:pre;\">")
              .Append(Html(fileText)).Append("</textarea>");
            sb.Append("<p><button type=\"submit\">Save</button></p>");
            sb.Append("</form>");

            if (liveRegion != null && !string.IsNullOrEmpty(liveRegion.ServerURI) && !string.IsNullOrEmpty(m_webConsoleSecret))
            {
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/regions/ini/restart\" onsubmit=\"return confirm('Restart ")
                  .Append(Html(regionName)).Append(" now? Any residents currently there will be disconnected.');\">");
                sb.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(regionId).Append("\">");
                sb.Append("<button type=\"submit\">Restart Region Now</button>");
                sb.Append("</form>");
            }
            else
            {
                sb.Append("<p><em>This region isn't currently online (or the web console isn't configured), "
                        + "so there's no way to restart it from here - saved changes apply next time it's started manually.</em></p>");
            }

            WritePage(request, response, PageTitle("Edit " + regionName), sb.ToString());
        }

        private void HandleAdminRegionIniRestart(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || string.IsNullOrEmpty(m_webConsoleSecret) || m_GridService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Region not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionId))
                {
                    GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero, regionId);
                    if (region != null && !string.IsNullOrEmpty(region.ServerURI))
                    {
                        RunRegionConsoleCommand(region, "region restart 30");
                        message = "Restart command sent to " + region.RegionName + ".";
                    }
                }
            }

            response.Redirect(BasePath + "/admin/regions/ini?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #endregion Admin: region .ini config file viewer/editor

        #region Admin: Simulators (start any region process)

        // Only Robust needs to be running for this WebUI itself to work -
        // regions are a separate concern entirely. This page exists so an
        // admin can bring any simulator up from here directly, reusing the
        // exact same launch mechanism Store region orders already use
        // (TryStartRegionProcess), rather than needing filesystem/RDP
        // access to run a launcher script by hand.

        // One row per simulator folder under Simulators\ - built on top of
        // DiscoverRegionIniFiles (already proven for the region .ini editor)
        // rather than a second, separately-written filesystem scan. Each
        // simulator here has exactly one region .ini in this codebase's own
        // convention, so RegionName/RegionID from that scan double as this
        // page's display name and liveness key.
        private List<(string SimulatorFolder, string RegionName, UUID RegionID)> DiscoverSimulators()
        {
            List<(string, string, UUID)> results = new List<(string, string, UUID)>();
            foreach (var r in DiscoverRegionIniFiles())
            {
                // r.FilePath is .../Simulators/<folder>/Regions/<name>.ini
                string regionsDir = Path.GetDirectoryName(r.FilePath);
                string simFolder = regionsDir != null ? Path.GetFileName(Path.GetDirectoryName(regionsDir)) : null;
                if (!string.IsNullOrEmpty(simFolder))
                    results.Add((simFolder, r.RegionName, r.RegionID));
            }
            return results;
        }

        // A simulator that's never been started (or was force-killed, like
        // the runaway-process incidents documented in PROJECT_LOG.md) has
        // no live GridRegion to check reachability against - reading its
        // own configured port straight out of its OpenSim.ini and probing
        // 127.0.0.1 directly works regardless of registration state, which
        // is exactly what "should the Start button be enabled" needs here.
        private int? GetSimulatorPort(string simulatorFolder)
        {
            string iniPath = Path.Combine(m_regionOrderGridRoot, "Simulators", simulatorFolder, "OpenSim.ini");
            if (!File.Exists(iniPath))
                return null;

            try
            {
                IConfigSource source = new IniConfigSource(iniPath);
                IConfig networkConfig = source.Configs["Network"];
                int port = networkConfig?.GetInt("http_listener_port", 0) ?? 0;
                return port > 0 ? port : null;
            }
            catch
            {
                return null;
            }
        }

        private void HandleAdminSimulators(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }
            if (!session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                WritePage(request, response, PageTitle("Simulators"), "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            List<(string SimulatorFolder, string RegionName, UUID RegionID)> simulators = DiscoverSimulators();

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-play-circle\"></i> Simulators</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/admin\">Back to admin</a></p>");
            sb.Append("<p>Only Robust needs to be running for this site itself - regions are started separately. "
                    + "This starts a region process directly on this host, the same way a Store region order does.</p>");

            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                sb.Append("<p>").Append(Html(queryMessage)).Append("</p>");

            if (simulators.Count == 0)
            {
                sb.Append("<p>No simulators found under the configured grid root.</p>");
            }
            else
            {
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/simulators/start-all\" style=\"display:inline\">");
                sb.Append("<button type=\"submit\">Start All Stopped</button></form> ");
                sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/simulators/stop-all\" style=\"display:inline;margin:0 0 16px;\" ")
                  .Append("onsubmit=\"return confirm('Gracefully shut down every running simulator? Anyone currently in one will be disconnected.');\">");
                sb.Append("<button type=\"submit\">Stop All Running</button></form>");

                sb.Append("<table><tr><th>Region</th><th>Status</th><th>Actions</th></tr>");
                foreach (var s in simulators.OrderBy(s => s.RegionName, StringComparer.OrdinalIgnoreCase))
                {
                    int? port = GetSimulatorPort(s.SimulatorFolder);
                    bool running = port.HasValue && Util.IsHostAlive("http://127.0.0.1:" + port.Value + "/", 1000);

                    sb.Append("<tr><td>").Append(Html(s.RegionName)).Append("</td>");
                    sb.Append("<td><span class=\"pill ").Append(running ? "pill-yes\">Running" : "pill-no\">Stopped").Append("</span></td>");
                    sb.Append("<td>");
                    if (!running)
                    {
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/simulators/start\" style=\"display:inline\">");
                        sb.Append("<input type=\"hidden\" name=\"folder\" value=\"").Append(Html(s.SimulatorFolder)).Append("\">");
                        sb.Append("<button type=\"submit\">Start</button></form> ");
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/simulators/remove\" style=\"display:inline\" ")
                          .Append("onsubmit=\"return confirm('Remove ").Append(Html(s.RegionName).Replace("'", "\\'"))
                          .Append("? Its config folder is deleted and it can no longer be started or discovered. Its content in the database and any assets it holds are NOT touched.');\">");
                        sb.Append("<input type=\"hidden\" name=\"folder\" value=\"").Append(Html(s.SimulatorFolder)).Append("\">");
                        sb.Append("<button type=\"submit\">Remove</button></form>");
                    }
                    else
                    {
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/simulators/stop\" style=\"display:inline\" ")
                          .Append("onsubmit=\"return confirm('Gracefully shut down ").Append(Html(s.RegionName).Replace("'", "\\'"))
                          .Append("? Anyone currently there will be disconnected.');\">");
                        sb.Append("<input type=\"hidden\" name=\"folder\" value=\"").Append(Html(s.SimulatorFolder)).Append("\">");
                        sb.Append("<button type=\"submit\">Stop</button></form>");
                    }
                    sb.Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            WritePage(request, response, PageTitle("Simulators"), sb.ToString());
        }

        private void HandleAdminSimulatorsStart(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Simulator not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string requestedFolder = FormValue(form, "folder");

                // Client-supplied folder name is never trusted alone - only
                // ever acts on a folder this same discovery scan just
                // found, same reverification discipline as the region .ini
                // editor's own path handling.
                bool known = DiscoverSimulators().Any(s => string.Equals(s.SimulatorFolder, requestedFolder, StringComparison.OrdinalIgnoreCase));
                if (!known)
                {
                    message = "That simulator wasn't found by the current discovery scan.";
                }
                else if (TryStartRegionProcess(requestedFolder, out int exitCode, out string logPath))
                {
                    message = requestedFolder + " started.";
                }
                else
                {
                    message = requestedFolder + " failed to start - it exited within 3 seconds (exit code " + exitCode + "). See " + logPath + ".";
                }
            }

            response.Redirect(BasePath + "/admin/simulators?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminSimulatorsStartAll(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Nothing to do.";
            if (request.HttpMethod == "POST")
            {
                List<(string SimulatorFolder, string RegionName, UUID RegionID)> toStart = DiscoverSimulators()
                        .Where(s =>
                        {
                            int? port = GetSimulatorPort(s.SimulatorFolder);
                            return !(port.HasValue && Util.IsHostAlive("http://127.0.0.1:" + port.Value + "/", 1000));
                        })
                        .ToList();

                if (toStart.Count == 0)
                {
                    message = "Nothing to start - everything discovered is already running.";
                }
                else
                {
                    // Runs in the background rather than blocking this
                    // request for the full duration - found live, a
                    // full-grid Start All (16 simulators, each with its own
                    // ~3+ second check) took long enough to exceed the
                    // shared Apache reverse proxy's own timeout, showing
                    // the admin a "Proxy Error" even though every region
                    // actually started successfully in the background. The
                    // status table on this page already reflects each
                    // simulator's real state live on every load, so there's
                    // nothing lost by not blocking here for a final count.
                    List<(string SimulatorFolder, string RegionName, UUID RegionID)> toStartCaptured = toStart;
                    System.Threading.Thread worker = new System.Threading.Thread(() =>
                    {
                        foreach (var s in toStartCaptured)
                            TryStartRegionProcess(s.SimulatorFolder, out _, out _);
                    })
                    { IsBackground = true };
                    worker.Start();

                    message = "Starting " + toStart.Count + " simulator(s) in the background - refresh this page in a bit to see status.";
                }
            }

            response.Redirect(BasePath + "/admin/simulators?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // A real graceful shutdown, not Process.Kill()/Task Manager "End
        // Task" - rides the exact same /consoleweb remote-console channel
        // already used for restart/kick/add-prim-limit (RunRegionConsoleCommand),
        // sending the region's own "shutdown" command (OpenSim.cs's SIGTERM
        // handler uses the identical RunCommand("shutdown")). This saves
        // state and deregisters from the grid cleanly - a force-kill
        // doesn't, and left a real stale RegionOnline flag behind after
        // exactly that happened live during earlier testing (see
        // PROJECT_LOG.md's Start Region entries) - graceful shutdown avoids
        // reproducing that on the way down, not just on the way up.
        private bool TryStopRegion(UUID regionId, string displayName, out string message)
        {
            if (string.IsNullOrEmpty(m_webConsoleSecret))
            {
                message = displayName + ": the web console isn't configured, so it can't be stopped remotely from here.";
                return false;
            }

            GridRegion region = m_GridService?.GetRegionByUUID(UUID.Zero, regionId);
            if (region == null || string.IsNullOrEmpty(region.ServerURI))
            {
                message = displayName + " isn't currently registered with the grid - it may already be stopped.";
                return false;
            }

            // Refuse rather than risk it - the shutdown sequence's own
            // "final backup" step (Scene.Close -> Backup(true)) silently
            // no-ops instead of waiting if an AutoBackupModule cycle is
            // already running (Backup()'s own re-entrancy guard just logs
            // and returns), so a shutdown mid-backup can let the scene
            // close - and its DB connections tear down - out from under a
            // write still in flight. Fails closed: if the status check
            // itself can't be confirmed (region unreachable, unexpected
            // response), don't send shutdown without knowing it's safe.
            string backupStatus = RunRegionConsoleCommand(region, "backup-status " + regionId);
            if (!backupStatus.Contains("BACKUP_IN_PROGRESS: False"))
            {
                message = backupStatus.Contains("BACKUP_IN_PROGRESS: True")
                        ? displayName + " is currently backing up - wait for it to finish, then try again."
                        : displayName + ": couldn't confirm it's safe to stop, so not stopping it. " + backupStatus;
                return false;
            }

            // RunRegionConsoleCommand's result was previously ignored here
            // entirely - found live, this let a Stop All report "sent" for
            // two regions whose command never actually reached them (see
            // the loopback-vs-public-URI fix above), with no way to tell
            // from the admin page that anything had gone wrong.
            string result = RunRegionConsoleCommand(region, "shutdown");
            bool ok = !result.StartsWith("Region responded with HTTP") && !result.StartsWith("Could not reach ");
            message = ok
                    ? displayName + ": shutdown command sent."
                    : displayName + ": failed to send shutdown - " + result;
            return ok;
        }

        private void HandleAdminSimulatorsStop(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Simulator not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string requestedFolder = FormValue(form, "folder");

                // Same re-verification discipline as Start - never act on a
                // client-supplied folder name that isn't backed by a real,
                // freshly-discovered simulator.
                var match = DiscoverSimulators().FirstOrDefault(s => string.Equals(s.SimulatorFolder, requestedFolder, StringComparison.OrdinalIgnoreCase));
                if (match.SimulatorFolder == null)
                    message = "That simulator wasn't found by the current discovery scan.";
                else
                    TryStopRegion(match.RegionID, match.RegionName, out message);
            }

            response.Redirect(BasePath + "/admin/simulators?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // Generalized decommission action - any discovered simulator, not
        // just Store orders. User's own correction (2026-08-29): the earlier
        // Cancel Order was scoped too narrowly to Store's port/location gap;
        // any region (static or Store-ordered) should be removable the same
        // way. Deleting the folder only removes its OpenSim.ini/Regions.ini
        // (config), not the region's actual world content - that lives in
        // the database, keyed by RegionID, and is deliberately left alone
        // here, same as every asset a resident ever uploaded: both are
        // shared/durable data this action has no business touching, not a
        // decision this button gets to make unilaterally. DeregisterRegion
        // and UnlinkRegion clean up the two grid-registration artifacts
        // (regions row, estate_map row) that would otherwise dangle - a
        // graceful Stop already clears the regions row, so that call is
        // normally a no-op safety net, not the primary mechanism.
        private void HandleAdminSimulatorsRemove(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Simulator not found.";
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string requestedFolder = FormValue(form, "folder");

                // Same re-verification discipline as Start/Stop - never act
                // on a client-supplied folder name that isn't backed by a
                // real, freshly-discovered simulator.
                var match = DiscoverSimulators().FirstOrDefault(s => string.Equals(s.SimulatorFolder, requestedFolder, StringComparison.OrdinalIgnoreCase));
                if (match.SimulatorFolder == null)
                {
                    message = "That simulator wasn't found by the current discovery scan.";
                }
                else
                {
                    int? port = GetSimulatorPort(match.SimulatorFolder);
                    bool running = port.HasValue && Util.IsHostAlive("http://127.0.0.1:" + port.Value + "/", 1000);
                    if (running)
                    {
                        message = match.RegionName + " is still running - stop it first, then remove it.";
                    }
                    else
                    {
                        m_GridService?.DeregisterRegion(match.RegionID);
                        m_EstateDataService?.UnlinkRegion(match.RegionID);

                        if (m_StoreService != null)
                        {
                            StoreOrder order = m_StoreService.GetAllOrders()
                                    .FirstOrDefault(o => o.OrderType == "RegionOrder"
                                            && string.Equals(o.SimulatorFolderName, match.SimulatorFolder, StringComparison.OrdinalIgnoreCase)
                                            && (o.Status == "AwaitingStart" || o.Status == "Active"));
                            if (order != null)
                            {
                                order.Status = "Cancelled";
                                order.Notes = (string.IsNullOrEmpty(order.Notes) ? string.Empty : order.Notes + "\n")
                                        + "Removed by " + session.Name + " on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC - "
                                        + "port " + order.AllocatedPort + " and grid location ("
                                        + order.AllocatedLocationX + "," + order.AllocatedLocationY + ") released for reuse.";
                                order.Updated = DateTime.UtcNow;
                                m_StoreService.StoreOrder(order);
                            }
                        }

                        string simPath = Path.Combine(m_regionOrderGridRoot, "Simulators", match.SimulatorFolder);
                        try
                        {
                            if (Directory.Exists(simPath))
                                Directory.Delete(simPath, true);
                            message = match.RegionName + " removed - its config folder is gone and it will no longer be discovered. "
                                    + "Its region content in the database and any assets it holds were left untouched.";
                        }
                        catch (Exception ex)
                        {
                            message = match.RegionName + ": grid-registration cleanup succeeded, but deleting " + simPath + " failed - " + ex.Message;
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/admin/simulators?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleAdminSimulatorsStopAll(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Nothing to do.";
            if (request.HttpMethod == "POST")
            {
                List<(string SimulatorFolder, string RegionName, UUID RegionID)> toStop = DiscoverSimulators()
                        .Where(s =>
                        {
                            int? port = GetSimulatorPort(s.SimulatorFolder);
                            return port.HasValue && Util.IsHostAlive("http://127.0.0.1:" + port.Value + "/", 1000);
                        })
                        .ToList();

                if (toStop.Count == 0)
                {
                    message = "Nothing to stop - everything discovered is already stopped.";
                }
                else
                {
                    // Same background-thread fix as Start All, and for the
                    // same reason - each stop now includes a backup-status
                    // round trip before the shutdown itself, so a full-grid
                    // Stop All is at least as slow as Start All was when it
                    // first hit the reverse proxy's timeout.
                    List<(string SimulatorFolder, string RegionName, UUID RegionID)> toStopCaptured = toStop;
                    System.Threading.Thread worker = new System.Threading.Thread(() =>
                    {
                        foreach (var s in toStopCaptured)
                            TryStopRegion(s.RegionID, s.RegionName, out _);
                    })
                    { IsBackground = true };
                    worker.Start();

                    message = "Sending shutdown to " + toStop.Count + " simulator(s) in the background - refresh this page in a bit to see status.";
                }
            }

            response.Redirect(BasePath + "/admin/simulators?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #endregion Admin: Simulators

        // Self-service password reset (task #22 from the WhiteCore-Dev
        // re-audit's "all of it" list). Always shows the same generic
        // confirmation message regardless of whether the email matched an
        // account - deliberately does not reveal which emails are
        // registered (standard practice for this kind of form; the
        // alternative, an "email not found" error, would let anyone probe
        // the useraccounts table one address at a time).
        private void HandleForgotPassword(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Forgot Password"), ForgotPasswordForm(null, null));
                return;
            }

            const string genericMessage = "If that email address matches an account, a password reset link has been sent to it.";

            if (!m_smtpEnabled || m_UserAccountService == null || m_AuthenticationService == null)
            {
                WritePage(request, response, PageTitle("Forgot Password"),
                        ForgotPasswordForm(null, "Password reset is not available on this grid right now."));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string email = FormValue(form, "email").Trim();

            if (!string.IsNullOrEmpty(email))
            {
                UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, email);
                if (account != null)
                {
                    string token = UUID.Random().ToString();
                    m_resetTokens[token] = new ResetToken { PrincipalID = account.PrincipalID, Expires = DateTime.UtcNow.Add(ResetTokenLifetime) };

                    string gridName = GetSetting("GridName", m_gridName);
                    string resetUrl = m_publicBaseUrl + BasePath + "/reset-password?token=" + token;
                    string body = "Hello " + account.FirstName + ",\n\n"
                            + "A password reset was requested for your " + gridName + " account. "
                            + "If this was you, click the link below within the next hour to choose a new password:\n\n"
                            + resetUrl + "\n\n"
                            + "If you didn't request this, you can safely ignore this email.";

                    SendEmail(email, gridName + " - Password reset", body);
                }
                // No else branch - same generic message either way, see comment above.
            }

            WritePage(request, response, PageTitle("Forgot Password"), "<h1>Forgot Password</h1><p>" + Html(genericMessage) + "</p>"
                    + "<p><a href=\"" + BasePath + "/login\">Back to login</a></p>");
        }

        private void HandleResetPassword(IOSHttpRequest request, IOSHttpResponse response)
        {
            // The request body stream can only be read once, so a POST's
            // form (which carries the token as a hidden field, not a query
            // param) is parsed exactly once here and reused below - an
            // earlier version of this handler called ReadForm(request) a
            // second time further down and hit "Stream was not readable",
            // caught by live-testing this exact flow.
            Dictionary<string, string> form = request.HttpMethod == "POST" ? ReadForm(request) : null;
            string token = request.HttpMethod == "POST"
                    ? FormValue(form, "token")
                    : request.QueryString.Get("token") ?? string.Empty;

            if (string.IsNullOrEmpty(token) || !m_resetTokens.TryGetValue(token, out ResetToken resetToken)
                    || resetToken.Expires <= DateTime.UtcNow)
            {
                m_resetTokens.TryRemove(token, out _);
                WritePage(request, response, PageTitle("Reset Password"),
                        "<h1>Reset Password</h1><p class=\"error\">This password reset link is invalid or has expired.</p>"
                        + "<p><a href=\"" + BasePath + "/forgot-password\">Request a new one</a></p>");
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Reset Password"), ResetPasswordForm(token, null));
                return;
            }

            string password = FormValue(form, "password");
            string confirmPassword = FormValue(form, "confirm_password");

            if (string.IsNullOrEmpty(password) || password.Length < 6)
            {
                WritePage(request, response, PageTitle("Reset Password"), ResetPasswordForm(token, "Password must be at least 6 characters."));
                return;
            }
            if (password != confirmPassword)
            {
                WritePage(request, response, PageTitle("Reset Password"), ResetPasswordForm(token, "Passwords do not match."));
                return;
            }

            m_resetTokens.TryRemove(token, out _);

            if (m_AuthenticationService == null || !m_AuthenticationService.SetPassword(resetToken.PrincipalID, password))
            {
                WritePage(request, response, PageTitle("Reset Password"),
                        "<h1>Reset Password</h1><p class=\"error\">Could not update your password. Please request a new reset link.</p>");
                return;
            }

            response.Redirect(BasePath + "/login?message=" + Uri.EscapeDataString("Password updated. Please log in."), HttpStatusCode.Redirect);
        }

        // Public, logged-out counterpart to /reset-password - redeems one
        // of the avatar's own recovery codes (see HandleRecoveryCodes)
        // instead of an emailed token, so it works even when the email on
        // file is stale or was never set. One step, not two - a valid
        // code is itself the proof email-based reset gets from clicking a
        // link, so there's no separate "check your email" round trip.
        private void HandleRecoverAccount(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, PageTitle("Recover Account"), RecoverAccountForm(string.Empty, string.Empty, null));
                return;
            }

            if (m_RecoveryCodeService == null || m_UserAccountService == null || m_AuthenticationService == null)
            {
                WritePage(request, response, PageTitle("Recover Account"),
                        RecoverAccountForm(string.Empty, string.Empty, "Account recovery is not available on this grid right now."));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string firstName = FormValue(form, "first_name").Trim();
            string lastName = FormValue(form, "last_name").Trim();
            string code = FormValue(form, "code");
            string password = FormValue(form, "password");
            string confirmPassword = FormValue(form, "confirm_password");

            if (string.IsNullOrEmpty(password) || password.Length < 6)
            {
                WritePage(request, response, PageTitle("Recover Account"), RecoverAccountForm(firstName, lastName, "Password must be at least 6 characters."));
                return;
            }
            if (password != confirmPassword)
            {
                WritePage(request, response, PageTitle("Recover Account"), RecoverAccountForm(firstName, lastName, "Passwords do not match."));
                return;
            }

            // Same generic-failure posture as TryLogin/HandleForgotPassword -
            // "no such avatar" and "wrong code" get the identical message,
            // so this can't be used to enumerate which avatars have
            // recovery codes set up.
            const string genericError = "Invalid name or recovery code.";

            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName);
            if (account == null || !m_RecoveryCodeService.RedeemCode(account.PrincipalID, code))
            {
                WritePage(request, response, PageTitle("Recover Account"), RecoverAccountForm(firstName, lastName, genericError));
                return;
            }

            if (!m_AuthenticationService.SetPassword(account.PrincipalID, password))
            {
                WritePage(request, response, PageTitle("Recover Account"),
                        RecoverAccountForm(firstName, lastName, "Could not update your password. Please try again."));
                return;
            }

            response.Redirect(BasePath + "/login?message=" + Uri.EscapeDataString("Password updated. Please log in."), HttpStatusCode.Redirect);
        }

        private static string RecoverAccountForm(string firstName, string lastName, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Recover Account</h1>"
                    + "<p>Use one of your saved recovery codes to set a new password without needing email access.</p>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/recover-account\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\" required autofocus></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\" required></label><br/>"
                    + "<label>Recovery code<br/><input type=\"text\" name=\"code\" required></label><br/>"
                    + "<label>New password<br/><input type=\"password\" name=\"password\" required></label><br/>"
                    + "<label>Confirm new password<br/><input type=\"password\" name=\"confirm_password\" required></label><br/>"
                    + "<button type=\"submit\">Reset password</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/forgot-password\">Use email instead</a> &middot; <a href=\"" + BasePath + "/login\">Back to login</a></p>";
        }

        // Mirrors the region-side EmailModule.cs's own MailKit connect/
        // authenticate/send sequence (see that file's SendEmail method) -
        // same library, same three calls, just without the LSL-specific
        // per-owner/per-address throttling and in-grid-mailbox routing that
        // exists there for llEmail abuse prevention, which doesn't apply to
        // a grid-operator-configured, self-triggered password reset.
        private void SendEmail(string toAddress, string subject, string body)
        {
            try
            {
                MimeMessage message = new MimeMessage();
                message.From.Add(m_smtpFrom);
                message.To.Add(MailboxAddress.Parse(toAddress));
                message.Subject = subject;
                message.Body = new TextPart("plain") { Text = body };

                using (SmtpClient client = new SmtpClient())
                {
                    if (m_smtpTls)
                        client.Connect(m_smtpHost, m_smtpPort, MailKit.Security.SecureSocketOptions.StartTls);
                    else
                        client.Connect(m_smtpHost, m_smtpPort);

                    if (!string.IsNullOrEmpty(m_smtpLogin) && !string.IsNullOrEmpty(m_smtpPassword))
                        client.Authenticate(m_smtpLogin, m_smtpPassword);

                    client.Send(message);
                    client.Disconnect(true);
                }
            }
            catch (Exception e)
            {
                m_log.Error("[WEB INTERFACE]: Failed to send email to " + toAddress, e);
            }
        }

        // Same excluded-character set as UserAccountService's own "create
        // user" console command (space, @, ., :) - these are reserved for
        // Hypergrid identifiers (first.last@gate.example.com:port), so a
        // first/last name containing them would collide with HG address
        // parsing elsewhere in the stack.
        private string ValidateRegistration(string firstName, string lastName, string password, string confirmPassword)
        {
            if (m_UserAccountService == null || m_AuthenticationService == null)
                return "Registration is not available right now.";

            if (string.IsNullOrEmpty(firstName) || string.IsNullOrEmpty(lastName))
                return "Enter a first and last name.";

            char[] excluded = { ' ', '@', '.', ':' };
            if (firstName.IndexOfAny(excluded) >= 0 || lastName.IndexOfAny(excluded) >= 0)
                return "Names cannot contain spaces or the characters @ . :";

            if (string.IsNullOrEmpty(password) || password.Length < 5)
                return "Password must be at least 5 characters.";

            if (password != confirmPassword)
                return "Passwords do not match.";

            if (m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName) != null)
                return "That name is already taken.";

            return null;
        }

        private static string RegisterForm(string firstName, string lastName, string email, string error,
                List<GridRegion> homeRegionChoices, UUID selectedHomeRegionId)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            string homeRegionField = string.Empty;
            if (homeRegionChoices != null && homeRegionChoices.Count > 0)
            {
                StringBuilder options = new StringBuilder();
                foreach (GridRegion region in homeRegionChoices)
                {
                    options.Append("<option value=\"").Append(region.RegionID).Append('"')
                            .Append(region.RegionID.Equals(selectedHomeRegionId) ? " selected" : string.Empty)
                            .Append('>').Append(Html(region.RegionName)).Append("</option>");
                }
                homeRegionField = "<label>Starting region<br/><select name=\"home_region\">" + options + "</select></label><br/>";
            }

            return "<h1>Sign Up</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/register\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\" required></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\" required></label><br/>"
                    + "<label>Email (optional)<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\"></label><br/>"
                    + "<label>Password<br/><input type=\"password\" name=\"password\" required></label><br/>"
                    + "<label>Confirm password<br/><input type=\"password\" name=\"confirm_password\" required></label><br/>"
                    + homeRegionField
                    + "<button type=\"submit\">Create account</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/login\">Already have an account? Log in</a></p>";
        }

        private void HandleLogout(IOSHttpRequest request, IOSHttpResponse response)
        {
            string token = ReadCookie(request, SessionCookieName);
            if (!string.IsNullOrEmpty(token))
                m_sessions.TryRemove(token, out _);

            ClearSessionCookie(response);
            WritePage(request, response, PageTitle("Logged Out"),
                    "<h1>Logged Out</h1><p>You have been logged out successfully.</p>"
                    + "<script>setTimeout(function() { window.location.href = \"" + BasePath + "/login\"; }, 3000);</script>");
        }

        // Resolves the account, then authenticates via IAuthenticationService the
        // same way any other OpenSim login path does - not a bespoke check against
        // the password hash directly. IAuthenticationService.Authenticate expects
        // an MD5 digest, not the raw plaintext password - real viewers hash it
        // client-side before it's ever sent over the wire (see LLLoginService's
        // own handling: a leading "$1$" means already-hashed, otherwise it MD5s
        // the input itself). A web form only ever has the raw plaintext, so this
        // must do the same hashing step LLLoginService does for that case.
        private string TryLogin(IOSHttpRequest request, string firstName, string lastName, string password, out string token)
        {
            token = null;

            if (m_UserAccountService == null || m_AuthenticationService == null)
                return "Login is not available right now.";

            if (string.IsNullOrEmpty(firstName) || string.IsNullOrEmpty(lastName) || string.IsNullOrEmpty(password))
                return "Enter your first name, last name, and password.";

            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, firstName, lastName);
            if (account == null)
                return "Invalid login.";

            // LLLoginService blocks any UserLevel below its m_MinLoginLevel
            // (default 0) for the real grid/viewer login - this is the same
            // check for the web dashboard's own separate login path, so a
            // banned/deleted account (see AccountBanHelper.BannedUserLevel/
            // DeletedUserLevel) can't still use self-service pages while
            // locked out in-world. AccountBanHelper.ClearExpiredBan here is
            // what actually lifts a timed ban once its timer runs out -
            // LLLoginService calls the exact same helper on its own login
            // path now too, so this is no longer the only path that can
            // self-clear an expired ban.
            AccountBanHelper.ClearExpiredBan(account, m_UserAccountService, m_UserProfilesService);
            if (account.UserLevel < 0)
                return "This account has been suspended. Contact a grid administrator.";

            string hashedPassword = Util.Md5Hash(password);
            string authToken = m_AuthenticationService.Authenticate(account.PrincipalID, hashedPassword, 30);
            if (string.IsNullOrEmpty(authToken))
                return "Invalid login.";

            UUID webAccountId = AutoProvisionWebAccount(request, account);

            token = CreateSession(account.PrincipalID, account.FirstName + " " + account.LastName, account.UserLevel >= 200, webAccountId);
            return null;
        }

        // Additive login model: classic avatar-name+password login (and
        // /register, which calls TryLogin internally) keep working exactly
        // as before - this just silently links a portal account (WebAccount)
        // to whichever avatar just logged in, auto-creating one the first
        // time an avatar with a real email logs in. Never blocks a login,
        // never changes what TryLogin returns on failure.
        private UUID AutoProvisionWebAccount(IOSHttpRequest request, UserAccount account)
        {
            if (m_WebAccountService == null)
                return UUID.Zero;

            string ip = GetClientIP(request);

            WebAccountAvatarLink existing = m_WebAccountService.GetLinkForAvatar(account.PrincipalID);
            if (existing != null)
            {
                m_WebAccountService.LogActivity(new WebActivityEntry
                {
                    WebAccountID = existing.WebAccountID,
                    AvatarPrincipalID = account.PrincipalID,
                    EventType = "user_login",
                    Description = "Logged in as " + account.Name,
                    IPAddress = ip
                });
                return existing.WebAccountID;
            }

            // Nothing to seed a WebAccount with - stays unlinked until the
            // resident sets an email (see EnsureWebAccountLinked, called
            // from HandleChangeEmail).
            if (string.IsNullOrWhiteSpace(account.Email))
                return UUID.Zero;

            // One email, one master account - enforced the same way SL
            // enforces it at signup, not by silently trusting a text-match.
            // If this email already belongs to a DIFFERENT avatar's master
            // account, we do NOT auto-link this avatar into it - typing a
            // matching email is not proof of ownership, and merging on
            // sight here would let anyone gain visibility into a stranger's
            // dashboard/avatar list just by knowing (or guessing) their
            // email, with zero authentication. This avatar just stays
            // unlinked; the resident can consolidate for real via Import
            // Avatar (proves ownership through THIS avatar's own in-world
            // password) from their actual master account's session.
            string normalizedEmail = account.Email.Trim().ToLowerInvariant();
            if (m_WebAccountService.GetByEmail(normalizedEmail) != null)
                return UUID.Zero;

            WebAccount newAccount = new WebAccount
            {
                ID = UUID.Random(),
                Email = normalizedEmail,
                Created = DateTime.UtcNow,
                Updated = DateTime.UtcNow
            };
            if (!m_WebAccountService.Store(newAccount))
                return UUID.Zero;

            try
            {
                m_WebAccountService.LinkAvatar(newAccount.ID, account.PrincipalID, "AutoProvisioned", true);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[WEB INTERFACE]: AutoProvisionWebAccount race creating link for {0}: {1}", account.PrincipalID, e.Message);
                WebAccountAvatarLink raced = m_WebAccountService.GetLinkForAvatar(account.PrincipalID);
                if (raced != null)
                    return raced.WebAccountID;
            }
            m_WebAccountService.LogActivity(new WebActivityEntry { WebAccountID = newAccount.ID, AvatarPrincipalID = account.PrincipalID, EventType = "user_registered", Description = "Portal account created", IPAddress = ip });
            m_WebAccountService.LogActivity(new WebActivityEntry { WebAccountID = newAccount.ID, AvatarPrincipalID = account.PrincipalID, EventType = "user_login", Description = "Logged in as " + account.Name, IPAddress = ip });
            return newAccount.ID;
        }

        // Lets a resident who logged in classically with no email set (so
        // AutoProvisionWebAccount had nothing to work with) get linked
        // immediately once they add one via /change-email, rather than
        // needing to log out and back in.
        private void EnsureWebAccountLinked(IOSHttpRequest request, WebSession session, UserAccount account)
        {
            if (session.WebAccountID != UUID.Zero || m_WebAccountService == null)
                return;

            session.WebAccountID = AutoProvisionWebAccount(request, account);
        }

        #endregion Pages

        #region Rendering

        private static string ForgotPasswordForm(string email, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            return "<h1>Forgot Password</h1>"
                    + errorHtml
                    + "<p>Enter the email address on your account and we'll send you a link to reset your password.</p>"
                    + "<form method=\"post\" action=\"" + BasePath + "/forgot-password\">"
                    + "<label>Email<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\" required autofocus></label><br/>"
                    + "<button type=\"submit\">Send reset link</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/recover-account\">Use a recovery code instead</a> &middot; <a href=\"" + BasePath + "/login\">Back to login</a></p>";
        }

        private static string ResetPasswordForm(string token, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            return "<h1>Reset Password</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/reset-password\">"
                    + "<input type=\"hidden\" name=\"token\" value=\"" + Html(token) + "\">"
                    + "<label>New password<br/><input type=\"password\" name=\"password\" required></label><br/>"
                    + "<label>Confirm new password<br/><input type=\"password\" name=\"confirm_password\" required></label><br/>"
                    + "<button type=\"submit\">Set new password</button>"
                    + "</form>";
        }

        // Single login form, unchanged since day one - no separate portal
        // credential (see AutoProvisionWebAccount's own comment on the
        // multi-avatar account model: the avatar you register/log in with
        // first IS the master account, not a second email+password pair).
        private static string LoginForm(string firstName, string lastName, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            return "<h1>Login</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/login\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\" autofocus></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\"></label><br/>"
                    + "<label>Password<br/><input type=\"password\" name=\"password\"></label><br/>"
                    + "<button type=\"submit\">Log in</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/register\">Sign up for a new account</a></p>"
                    + "<p><a href=\"" + BasePath + "/forgot-password\">Forgot your password?</a> &middot; <a href=\"" + BasePath + "/recover-account\">Use a recovery code</a></p>";
        }

        // Not static (unlike the rest of this #region) specifically so it can
        // read the live grid name for the header brand mark and the current
        // session for the nav bar - `request` was added to every one of this
        // method's ~51 call sites (all already had it in scope, being
        // ordinary Handle* methods) specifically to make that possible.
        //
        // Real, structural rebuild (2026-08-10) after direct feedback that
        // the previous single-narrow-card layout still looked like a bare
        // admin panel next to real competing grids' actual sites (osgrid.org,
        // wolf-grid.com, 3rdrockgrid.com) and even the user's own
        // OpenSim-Grid-Interface - and that this was true "all the way
        // around," not just on the splash screen. Rather than another CSS
        // pass, this changes the page SHAPE: a full-width site header (brand
        // + nav + session-aware login/register or dashboard/admin/logout
        // actions), a hero band carrying the page's own title (every call
        // site already emits "<h1>...</h1>" as the first thing in bodyHtml -
        // pulled out here into the hero rather than requiring 51 call-site
        // rewrites), a wider content area (760px -> 1100px, so the
        // stats-grid/widget-grid rows built for the economy/classifieds/
        // events widgets actually get room to lay out side by side instead
        // of stacking in a narrow column), and a footer - applied to EVERY
        // page (login, dashboard, admin tables, not just home/splash) per
        // explicit direction, so the site feels like one consistent product
        // rather than a marketing page bolted onto a bare admin tool.
        private void WritePage(IOSHttpRequest request, IOSHttpResponse response, string title, string bodyHtml)
        {
            string gridName = GetSetting("GridName", m_gridName);
            WebSession session = GetSession(request);

            string heroTitle = Html(gridName);
            string remainder = bodyHtml;
            if (bodyHtml.StartsWith("<h1>"))
            {
                int end = bodyHtml.IndexOf("</h1>", StringComparison.Ordinal);
                if (end > 0)
                {
                    heroTitle = bodyHtml.Substring(4, end - 4);
                    remainder = bodyHtml.Substring(end + 5);
                }
            }

            string head = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + Html(title) + "</title>"
                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                    + "<link rel=\"stylesheet\" href=\"/static/bootstrap-icons.css\">"
                    + "<style>" + PageCss + "</style></head><body>";

            string footerLinks = "";
            if (HasStaticPage("tos"))
                footerLinks += " &middot; <a href=\"" + BasePath + "/page/tos\">Terms of Service</a>";
            if (HasStaticPage("dmca"))
                footerLinks += " &middot; <a href=\"" + BasePath + "/page/dmca\">DMCA Policy</a>";

            string pageBody = "<section class=\"hero\"><div class=\"hero-inner\"><h1>" + heroTitle + "</h1></div></section>"
                    + "<main class=\"site-main\"><div class=\"page\"><div class=\"card\">" + remainder + "</div></div></main>"
                    + "<footer class=\"site-footer\"><div class=\"site-footer-inner\">"
                    + "&copy; " + DateTime.UtcNow.Year + " " + Html(gridName) + " &middot; Powered by Confluence"
                    + footerLinks
                    + "</div></footer>";

            string html;
            if (session != null)
            {
                // Persistent sidebar app-shell for the logged-in experience -
                // replaces the old giant account/admin dropdowns (25+ links
                // buried two clicks deep in the header) with a real,
                // always-visible nav, matching 3RD Rock Grid's own resident
                // panel structure. Public site-wide links (Search,
                // Destinations, etc.) move to a slim top bar so they're still
                // reachable without duplicating the sidebar's account links.
                string path = request.RawUrl ?? "/";
                html = head
                        + "<div class=\"app-shell\">"
                        + RenderSidebar(session, path)
                        + "<div id=\"sidebarBackdrop\" class=\"sidebar-backdrop\"></div>"
                        + "<div class=\"app-main\">"
                        + "<header class=\"app-topbar\">"
                        + "<button class=\"sidebar-toggle\" aria-label=\"Menu\"><i class=\"bi bi-list\"></i></button>"
                        + "<nav class=\"site-nav\"><a href=\"/\"><i class=\"bi bi-house-door ic-blue\"></i> Home</a>" +
                        "<a href=\"" + BasePath + "/features\"><i class=\"bi bi-stars ic-amber\"></i> Features</a>" +
                        "<a href=\"" + BasePath + "/viewers\"><i class=\"bi bi-display ic-blue\"></i> Get a Viewer</a>" +
                        RenderTopNavGroups(false) +
                        RenderNavPages(session) + "</nav>"
                        + "</header>"
                        + pageBody
                        + "</div></div>"
                        + SidebarToggleScript
                        + DropdownScript
                        + "</body></html>";
            }
            else
            {
                string navActions = "<a href=\"" + BasePath + "/login\">Log In</a>"
                        + "<a href=\"" + BasePath + "/register\" class=\"nav-cta\">Sign Up</a>";

                html = head
                        + "<header class=\"site-header\"><div class=\"site-header-inner\">"
                        + "<a class=\"brand\" href=\"/\"><span class=\"brand-mark\">C</span>" + Html(gridName) + "</a>"
                        + "<nav class=\"site-nav\"><a href=\"/\"><i class=\"bi bi-house-door ic-blue\"></i> Home</a>" +
                        "<a href=\"" + BasePath + "/features\"><i class=\"bi bi-stars ic-amber\"></i> Features</a>" +
                        "<a href=\"" + BasePath + "/viewers\"><i class=\"bi bi-display ic-blue\"></i> Get a Viewer</a>" +
                        RenderTopNavGroups(true) +
                        RenderNavPages(session) + "</nav>"
                        + "<div class=\"site-actions\">" + navActions + "</div>"
                        + "</div></header>"
                        + pageBody
                        + DropdownScript
                        + "</body></html>";
            }

            response.ContentType = "text/html";
            response.RawBuffer = Encoding.UTF8.GetBytes(html);
        }

        // Sidebar link definitions, in display order - single source of truth
        // for both rendering and active-state matching, so a new sidebar
        // entry can't silently be added to one without the other.
        // Kept flat/ungrouped - the small set of pages a resident lands on
        // most often, so they're never more than one click away behind a
        // collapsed group.
        private static readonly (string Path, string Icon, string Label)[] SidebarMainLinks =
        {
            ("/dashboard", "bi-speedometer2", "Dashboard"),
            ("/profile", "bi-person", "My Profile"),
            ("/myinventory", "bi-box-seam", "Inventory"),
        };

        // Grouped under its own "Avatars" section, matching the reference's
        // sidebar submenu - /partner and /transactions are MOVED here (nav
        // placement only, their routes/handlers are unchanged) rather than
        // duplicated between two sections.
        private static readonly (string Path, string Icon, string Label)[] AvatarsSubmenu =
        {
            ("/my-avatars", "bi-people", "My Avatars"),
            ("/create-avatar", "bi-person-plus", "Create Avatar"),
            ("/import-avatar", "bi-box-arrow-in-right", "Import Avatar"),
            ("/partner", "bi-heart", "Partnerships"),
            ("/transactions", "bi-cash-stack", "Transactions"),
        };

        private static readonly (string Path, string Icon, string Label)[] SidebarSocialLinks =
        {
            ("/friends", "bi-people", "Friends"),
            ("/messages", "bi-envelope", "Messages"),
            ("/offline-messages", "bi-envelope-open", "Offline Messages"),
        };

        private static readonly (string Path, string Icon, string Label)[] SidebarCommunityLinks =
        {
            ("/myclassifieds", "bi-megaphone", "Classifieds"),
            ("/myevents", "bi-calendar-event", "Events"),
            ("/auctions", "bi-hammer", "Auctions"),
            ("/suggestion-box", "bi-lightbulb", "Suggestion Box"),
        };

        // Split back into separate My Regions / My Land pages (2026-08-23) -
        // the merged page got unwieldy fast once a resident owned more than
        // a couple of regions.
        private static readonly (string Path, string Icon, string Label)[] SidebarLandLinks =
        {
            ("/myregions", "bi-hdd-rack", "My Regions"),
            ("/myland", "bi-signpost-split", "My Land"),
            ("/myestates", "bi-building", "My Estate"),
        };

        // Store: prim-capacity packs + self-service region ordering
        // (2026-08-23). See PROJECT_LOG.md/FEATURES.md.
        private static readonly (string Path, string Icon, string Label)[] SidebarCommerceLinks =
        {
            ("/store", "bi-shop", "Store"),
            ("/store/my-purchases", "bi-receipt", "My Purchases"),
            ("/marketplace", "bi-bag", "Marketplace"),
            ("/marketplace/manage", "bi-tag", "My Listings"),
        };

        private static readonly (string Path, string Icon, string Label)[] SidebarAccountLinks =
        {
            ("/change-password", "bi-key", "Change Password"),
            ("/change-email", "bi-envelope", "Change Email"),
            ("/recovery-codes", "bi-shield-lock", "Recovery Codes"),
            ("/delete-account", "bi-trash", "Delete Account"),
        };

        private string RenderSidebar(WebSession session, string currentPath)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string initial = string.IsNullOrEmpty(session.Name) ? "?" : session.Name.Substring(0, 1).ToUpperInvariant();

            List<WebAccountAvatarLink> linkedAvatars = session.WebAccountID != UUID.Zero && m_WebAccountService != null
                    ? m_WebAccountService.GetLinkedAvatars(session.WebAccountID)
                    : new List<WebAccountAvatarLink>();

            StringBuilder sb = new StringBuilder();
            sb.Append("<aside id=\"appSidebar\" class=\"app-sidebar\">");
            sb.Append("<a class=\"sidebar-brand\" href=\"/\"><span class=\"brand-mark\">C</span>").Append(Html(gridName)).Append("</a>");

            // Avatar switcher - a dropdown reusing the same .nav-dropdown/
            // .dropdown-toggle/.dropdown-menu/DropdownScript mechanism the
            // top nav's own Explore/Grid Info groups already use (a plain
            // delegated document click handler, not scoped to the top nav -
            // it works anywhere a .nav-dropdown appears). Only rendered
            // when there's actually more than one avatar to switch between.
            if (linkedAvatars.Count > 1)
            {
                sb.Append("<div class=\"sidebar-user nav-dropdown\">");
                sb.Append("<a href=\"#\" class=\"dropdown-toggle\" style=\"display:flex;align-items:center;text-decoration:none;color:inherit\">");
                sb.Append("<div class=\"sidebar-user-avatar\">").Append(Html(initial)).Append("</div><div>");
                sb.Append("<div class=\"sidebar-user-name\">").Append(Html(session.Name)).Append(" <i class=\"bi bi-caret-down-fill\"></i></div>");
                sb.Append("<div class=\"sidebar-user-role\"><span class=\"pill ")
                  .Append(session.IsAdmin ? "pill-yes" : "pill-no").Append("\">")
                  .Append(session.IsAdmin ? "Administrator" : "Resident").Append("</span></div>");
                sb.Append("</div></a>");
                sb.Append("<div class=\"dropdown-menu\">");
                foreach (WebAccountAvatarLink link in linkedAvatars)
                {
                    UserAccount linkedAccount = m_UserAccountService?.GetUserAccount(UUID.Zero, link.AvatarPrincipalID);
                    string linkedName = linkedAccount != null ? linkedAccount.Name : link.AvatarPrincipalID.ToString();
                    bool isActive = link.AvatarPrincipalID == session.PrincipalID;
                    if (isActive)
                    {
                        sb.Append("<span><i class=\"bi bi-check2 ic-green\"></i> ").Append(Html(linkedName)).Append("</span>");
                    }
                    else
                    {
                        sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/switch-avatar\" style=\"margin:0\">")
                          .Append("<input type=\"hidden\" name=\"avatar_principal_id\" value=\"").Append(link.AvatarPrincipalID).Append("\">")
                          .Append("<button type=\"submit\" style=\"background:none;border:none;padding:0;width:100%;text-align:left;color:inherit;font:inherit;cursor:pointer\">")
                          .Append(Html(linkedName)).Append("</button></form>");
                    }
                }
                sb.Append("<a href=\"").Append(BasePath).Append("/my-avatars\"><i class=\"bi bi-gear\"></i> Manage Avatars</a>");
                sb.Append("</div></div>");
            }
            else
            {
                sb.Append("<div class=\"sidebar-user\">");
                sb.Append("<div class=\"sidebar-user-avatar\">").Append(Html(initial)).Append("</div><div>");
                sb.Append("<div class=\"sidebar-user-name\">").Append(Html(session.Name)).Append("</div>");
                sb.Append("<div class=\"sidebar-user-role\"><span class=\"pill ")
                  .Append(session.IsAdmin ? "pill-yes" : "pill-no").Append("\">")
                  .Append(session.IsAdmin ? "Administrator" : "Resident").Append("</span></div>");
                sb.Append("</div></div>");
            }

            sb.Append("<nav class=\"sidebar-nav\">");
            sb.Append("<div class=\"sidebar-nav-label\">My Panel</div>");
            int colorIndex = 0;
            foreach ((string linkPath, string icon, string label) in SidebarMainLinks)
            {
                string href = linkPath == "/profile" ? BasePath + "/profile?id=" + session.PrincipalID : BasePath + linkPath;
                bool active = currentPath.StartsWith(BasePath + linkPath, StringComparison.OrdinalIgnoreCase);
                AppendSidebarLink(sb, href, icon, label, active, SidebarIconColors[colorIndex++ % SidebarIconColors.Length]);
            }

            // Collapsible groups - closed by default to save vertical space
            // (the whole point of collapsing them), but auto-open when the
            // current page is one of their own links, so the active-link
            // highlight is never hidden behind a closed toggle.
            AppendSidebarGroup(sb, "Avatars", AvatarsSubmenu, currentPath, ref colorIndex);
            AppendSidebarGroup(sb, "Social", SidebarSocialLinks, currentPath, ref colorIndex);
            AppendSidebarGroup(sb, "Community", SidebarCommunityLinks, currentPath, ref colorIndex);
            AppendSidebarGroup(sb, "Land & Estate", SidebarLandLinks, currentPath, ref colorIndex);
            AppendSidebarGroup(sb, "Store", SidebarCommerceLinks, currentPath, ref colorIndex);
            AppendSidebarGroup(sb, "Account", SidebarAccountLinks, currentPath, ref colorIndex);

            // Admin gets exactly one extra sidebar entry, not the old
            // dropdown's full 13-item breakdown - /admin itself renders that
            // breakdown as its own card grid (same nav-as-cards treatment as
            // the resident dashboard), so the sidebar stays a fixed, scannable
            // size regardless of role.
            if (session.IsAdmin)
            {
                sb.Append("<div class=\"sidebar-nav-label\">Grid</div>");
                bool adminActive = currentPath.StartsWith(BasePath + "/admin", StringComparison.OrdinalIgnoreCase);
                AppendSidebarLink(sb, BasePath + "/admin", "bi-shield-lock", "Admin Panel", adminActive, "ic-pink");
            }
            sb.Append("</nav>");

            sb.Append("<a class=\"sidebar-logout\" href=\"").Append(BasePath).Append("/logout\"><i class=\"bi bi-box-arrow-right\"></i> Log Out</a>");
            sb.Append("</aside>");
            return sb.ToString();
        }

        // Rotated across sidebar entries in declaration order - a plain
        // index cycle rather than picking one color per item, since there's
        // no meaningful semantic grouping to base it on (unlike the header's
        // Explore/Grid Info split), just the same "stop reading as a flat
        // wall of grey text" goal the header dropdowns were built for.
        private static readonly string[] SidebarIconColors =
        {
            "ic-blue", "ic-cyan", "ic-green", "ic-amber", "ic-purple", "ic-pink"
        };

        private static void AppendSidebarLink(StringBuilder sb, string href, string icon, string label, bool active, string colorClass)
        {
            sb.Append("<a href=\"").Append(href).Append("\"").Append(active ? " class=\"active\"" : string.Empty).Append(">");
            // Active state overrides the per-item color with the shared
            // accent highlight (matches .sidebar-nav a.active's existing
            // background/text tint) - the icon shouldn't clash with it.
            sb.Append("<i class=\"bi ").Append(icon).Append(active ? "" : " " + colorClass).Append("\"></i> ").Append(Html(label)).Append("</a>");
        }

        // Shared renderer for every collapsible sidebar section (Avatars,
        // Social, Community, Land & Estate, Account) - auto-opens when the
        // current page is one of its own links, closed otherwise.
        private static void AppendSidebarGroup(StringBuilder sb, string groupLabel, (string Path, string Icon, string Label)[] links, string currentPath, ref int colorIndex)
        {
            bool groupActive = links.Any(l => currentPath.StartsWith(BasePath + l.Path, StringComparison.OrdinalIgnoreCase));
            sb.Append("<details class=\"sidebar-nav-group\"").Append(groupActive ? " open" : "").Append(">");
            sb.Append("<summary class=\"sidebar-nav-label\">").Append(Html(groupLabel)).Append(" <i class=\"bi bi-chevron-down\"></i></summary>");
            foreach ((string linkPath, string icon, string label) in links)
            {
                bool active = currentPath.StartsWith(BasePath + linkPath, StringComparison.OrdinalIgnoreCase);
                AppendSidebarLink(sb, BasePath + linkPath, icon, label, active, SidebarIconColors[colorIndex++ % SidebarIconColors.Length]);
            }
            sb.Append("</details>");
        }

        // Admin-managed nav entries - matches WhiteCore-Dev's real admin/
        // page_manager.html (pages place themselves in the nav, with an
        // order and visibility rules, rather than needing a code change).
        // Appended after the fixed nav items above rather than replacing
        // them - About/ToS/DMCA etc. stay hardcoded; this only adds
        // whatever additional pages an admin has explicitly opted in via
        // "Show in header nav". An admin who also opts in an already-
        // hardcoded slug (e.g. "about") would see it twice - their choice
        // to avoid, not defended against here.
        private string RenderNavPages(WebSession session)
        {
            if (m_StaticPageService == null)
                return string.Empty;

            List<StaticPage> pages = m_StaticPageService.GetAll();
            if (pages.Count == 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            foreach (StaticPage page in pages
                    .Where(p => p.ShowInNav)
                    .Where(p => !p.RequiresLogin || session != null)
                    .Where(p => !p.RequiresAdmin || (session != null && session.IsAdmin))
                    .OrderBy(p => p.NavOrder))
            {
                sb.Append("<a href=\"").Append(BasePath).Append("/page/").Append(Uri.EscapeDataString(page.Slug)).Append("\">")
                  .Append(Html(page.Title)).Append("</a>");
            }
            return sb.ToString();
        }

        // Chrome-free variant of WritePage for pages meant to be opened
        // inside a viewer's own embedded browser panel (the login splash,
        // Help, About, the embedded search, the Destination Guide) - the
        // full site header/nav/hero/footer have no useful navigation
        // target in that small embedded context and just waste space.
        // Shares PageCss so typography/colors still match the rest of the
        // site. Only ever reached via WriteAdaptivePage's real viewer
        // detection now - a normal browser tab gets WritePage (full
        // chrome) instead, so this no longer needs its own "way back" link
        // (an earlier pass added one here for exactly that case, before
        // WriteAdaptivePage existed - removed per the user, since it's now
        // unnecessary clutter every time a real viewer actually renders
        // this).
        private void WriteBarePage(IOSHttpRequest request, IOSHttpResponse response, string title, string bodyHtml)
        {
            string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + Html(title) + "</title>"
                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                    + "<link rel=\"stylesheet\" href=\"/static/bootstrap-icons.css\">"
                    + "<style>" + PageCss + "</style></head><body>"
                    + "<main class=\"site-main\"><div class=\"page\"><div class=\"card\">" + bodyHtml + "</div></div></main>"
                    + "</body></html>";

            response.ContentType = "text/html";
            response.RawBuffer = Encoding.UTF8.GetBytes(html);
        }

        // Bootstrap Icons, vendored (not CDN-linked - this connector has to
        // work with no internet egress at runtime) per the same icon system
        // OpenSim-Grid-Interface's own docs/icons-and-theme.md standardizes
        // on ("Bootstrap Icons are used for all UI icons"). The actual
        // .css/.woff2/.woff files are embedded resources (see
        // WebInterface/Resources/ and prebuild.xml), fetched once at
        // vendoring time and shipped inside the DLL from then on - the
        // deployed grid never needs network access for this. Resolved via
        // GetManifestResourceNames() rather than a hand-computed name
        // string, the same defensive pattern Migration.cs already uses for
        // its own embedded resources, since the exact compiler-generated
        // name depends on the project's root namespace.
        private static readonly Dictionary<string, string> StaticAssetContentTypes = new Dictionary<string, string>
        {
            { "bootstrap-icons.css", "text/css" },
            { "bootstrap-icons.woff2", "font/woff2" },
            { "bootstrap-icons.woff", "font/woff" },
            // Leaflet 1.9.4, vendored the same way (no CDN) - see
            // HandleWorldMap/WorldMapScript for the actual map page. Uses
            // L.imageOverlay per region instead of L.marker, so the default
            // marker-icon PNGs Leaflet's CSS references were never vendored
            // - nothing in this connector's map ever uses L.marker.
            { "leaflet.css", "text/css" },
            { "leaflet.js", "application/javascript" }
        };

        private static readonly Dictionary<string, byte[]> StaticAssetCache = new Dictionary<string, byte[]>();
        private static readonly object StaticAssetLock = new object();

        private static byte[] LoadStaticAsset(string fileName)
        {
            lock (StaticAssetLock)
            {
                if (StaticAssetCache.TryGetValue(fileName, out byte[] cached))
                    return cached;

                Assembly assembly = typeof(WebInterfaceServiceConnector).Assembly;
                string resourceName = null;
                foreach (string candidate in assembly.GetManifestResourceNames())
                {
                    if (candidate.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        resourceName = candidate;
                        break;
                    }
                }

                if (resourceName == null)
                {
                    StaticAssetCache[fileName] = null;
                    return null;
                }

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    byte[] bytes = buffer.ToArray();
                    StaticAssetCache[fileName] = bytes;
                    return bytes;
                }
            }
        }

        private void HandleStaticAsset(IOSHttpRequest request, IOSHttpResponse response, string fileName)
        {
            if (!StaticAssetContentTypes.TryGetValue(fileName, out string contentType))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            byte[] bytes = LoadStaticAsset(fileName);
            if (bytes == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.ContentType = contentType;
            // Vendored, versioned-by-filename assets - safe to cache
            // aggressively, same reasoning any static-asset pipeline uses.
            response.AddHeader("Cache-Control", "public, max-age=31536000, immutable");
            response.RawBuffer = bytes;
        }

        // Login-splash background slideshow (HandleWelcome) - a fixed
        // WebSplash/ folder next to Robust.exe, same "drop files in a
        // conventionally-named folder, no config key needed" pattern
        // RegionWeb's own region_images/carousel already use, rather than
        // wiring up a new ini section just for a directory path. Empty
        // by default (no folder = no slideshow, not an error) so this is
        // purely additive for an operator who wants it.
        private static readonly string[] WelcomePhotoExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

        private static string WelcomePhotoDirectory =>
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebSplash");

        private static List<string> GetWelcomePhotoFiles()
        {
            List<string> files = new List<string>();
            string dir = WelcomePhotoDirectory;
            if (!Directory.Exists(dir))
                return files;

            foreach (string path in Directory.GetFiles(dir))
            {
                if (Array.IndexOf(WelcomePhotoExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0)
                    files.Add(Path.GetFileName(path));
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        // Path.GetFileName strips any directory traversal from the
        // client-supplied segment before it ever touches the filesystem -
        // same discipline RegionWebModule's SendMedia/SendEstateMedia use
        // for the same kind of "serve a file an operator dropped in a
        // folder" route.
        private void HandleWelcomePhoto(IOSHttpRequest request, IOSHttpResponse response, string unsafeName)
        {
            string fileName = Path.GetFileName(unsafeName);
            if (string.IsNullOrEmpty(fileName) ||
                    Array.IndexOf(WelcomePhotoExtensions, Path.GetExtension(fileName).ToLowerInvariant()) < 0)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            string path = Path.Combine(WelcomePhotoDirectory, fileName);
            if (!File.Exists(path))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.ContentType = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "application/octet-stream",
            };
            response.AddHeader("Cache-Control", "public, max-age=3600");
            response.RawBuffer = File.ReadAllBytes(path);
        }

        // Real viewer-vs-browser detection, ported from the same mechanism
        // OpenSim-Grid-Interface's include/viewer_context.php already uses
        // (os_detect_viewer()) - not invented here, and not the guess this
        // connector wrongly assumed wasn't possible. The X-SecondLife-*
        // headers are the reliable signal: any SL-protocol viewer's
        // embedded browser (login splash, Search "Web" tab, Help/About/
        // Destinations panels) attaches these to every request it makes,
        // the same way it does for in-world web media - a real, standard
        // behavior, not a guess. User-Agent substrings and a
        // ?view=viewer|web override (persisted via a cookie, same pattern
        // the session cookie already uses) are kept as fallbacks for the
        // rare case those headers get stripped by a proxy.
        private static readonly string[] ViewerHeaders =
        {
            "X-SecondLife-Owner-Name", "X-SecondLife-Region", "X-SecondLife-Shard"
        };

        private static readonly string[] ViewerUserAgentNeedles =
        {
            "Firestorm", "Second Life", "SLViewer", "Kokua", "Cool VL", "Singularity",
            "Black Dragon", "Dayturn", "Alchemy"
        };

        private static bool IsViewerRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            foreach (string header in ViewerHeaders)
            {
                if (!string.IsNullOrEmpty(request.Headers[header]))
                    return true;
            }

            string userAgent = request.Headers["User-Agent"] ?? string.Empty;
            foreach (string needle in ViewerUserAgentNeedles)
            {
                if (userAgent.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            string viewParam = request.QueryString.Get("view");
            if (!string.IsNullOrEmpty(viewParam))
            {
                string v = viewParam.ToLowerInvariant();
                if (v == "viewer" || v == "web")
                {
                    response.AddHeader("Set-Cookie", "view=" + v + "; Path=/");
                    return v == "viewer";
                }
            }

            string viewCookie = ReadCookie(request, "view");
            if (!string.IsNullOrEmpty(viewCookie))
                return viewCookie.Equals("viewer", StringComparison.OrdinalIgnoreCase);

            return false;
        }

        // Two teleport-link schemes, not interchangeable - verified against
        // real Firestorm/AyaneStorm source (fspanellogin.cpp) rather than
        // assumed, after live testing found the previous single
        // secondlife:///app/teleport/ link doing nothing at all in every
        // context tried.
        //
        // BuildLocationLoginUrl: secondlife:///app/location_login/... maps
        // to LLLoginLocationAutoHandler ("location_login" command), which
        // is explicitly registered UNTRUSTED_BLOCK ("don't allow from
        // external browsers" - the constructor's own comment) - it only
        // fires from a trusted in-viewer context, e.g. welcome.php's own
        // pre-login MOTD panel, and calls FSPanelLogin::autologinToLocation
        // -> LLStartUp::setStartSLURL -> the location combo's setTextEntry,
        // i.e. it fills the Start Location box rather than attempting a
        // teleport - the only sane behavior pre-login, since no session
        // exists yet to command. /app/teleport/ is the right scheme for an
        // already-logged-in session (Search/Places results shown inside a
        // live viewer), just wrong for welcome.php specifically.
        //
        // BuildHopUrl: for a real external browser, a bare secondlife://
        // SLURL doesn't encode which grid it's on - it only resolves
        // correctly for someone whose viewer already defaults to this
        // grid. hop://host:port/RegionName is the actual Hypergrid address
        // (verified: Firestorm's own installer registers "hop" as an OS
        // protocol handler identically to "secondlife" - both are real
        // browser-clickable links - but only hop:// carries the grid
        // identity a stranger's viewer needs to connect to the right
        // place).
        private string BuildHopUrl(string regionName)
        {
            string hostPort = m_publicBaseUrl ?? string.Empty;
            foreach (string prefix in new[] { "https://", "http://" })
            {
                if (hostPort.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    hostPort = hostPort.Substring(prefix.Length);
                    break;
                }
            }
            hostPort = hostPort.TrimEnd('/');
            return "hop://" + hostPort + "/" + Uri.EscapeDataString(regionName);
        }

        private static string BuildLocationLoginUrl(string regionName, int x = 128, int y = 128, int z = 25)
        {
            return "secondlife:///app/location_login/" + Uri.EscapeDataString(regionName)
                    + "/" + x + "/" + y + "/" + z;
        }

        // Picks WriteBarePage for a real viewer request, WritePage for a
        // normal browser one - the actual "works in both" fix, replacing
        // the earlier band-aid of always using WriteBarePage plus a small
        // home link. One canonical URL per page again (no more /websearch
        // split, no more embedded=1 query flags) since chrome is now
        // decided per-request from a real signal instead of guessed once
        // at build time.
        private void WriteAdaptivePage(IOSHttpRequest request, IOSHttpResponse response, string title, string bodyHtml)
        {
            if (IsViewerRequest(request, response))
                WriteBarePage(request, response, title, bodyHtml);
            else
                WritePage(request, response, title, bodyHtml);
        }

        // Self-contained (no external fonts/CDNs - this connector has to work
        // on a grid with no internet egress at all). History: this started
        // purple/indigo (this session's own invention), then got re-themed
        // to WhiteCore-Dev's real coral/slate identity per an explicit ask
        // to match that instead of guessing. Then the user pulled up real
        // screenshots of competing grids' own splash screens (3rd Rock Grid,
        // DigiWorldz, Wolf Territories) with the explicit framing "I'm
        // competing with these grids for users" - i.e. this isn't cosmetic
        // preference, it's competitive positioning, so it was worth getting
        // right rather than iterating forever. Landed on: 3rd Rock Grid's
        // actual splash layout/darkness (a black background throughout, not
        // just a dark header over a light page - DigiWorldz uses the same
        // underlying template in teal/navy, confirming this is a common,
        // proven "grid status splash" pattern, not one grid's idiosyncrasy),
        // but with 3RG's orange accent swapped for blue per explicit
        // follow-up ("I like 3rd Rock Grid's color scheme... but in Blue").
        // Kept the WhiteCore-derived pill button shape (border-radius:40px,
        // uppercase) since nothing about the color-scheme feedback objected
        // to it, and it reads as more modern than a plain rectangular button.
        //
        // Applies uniformly to every page through the shared WritePage
        // wrapper above, including the in-viewer login splash
        // (HandleWelcome) - kept compact/comfortable at small widths since
        // that one renders inside the viewer's own small embedded panel,
        // not a full browser window.
        private const string PageCss =
                ":root{--accent:#3b82f6;--accent-bright:#60a5fa;--accent-dark:#1d4ed8;" +
                "--accent-tint:rgba(59,130,246,.16);--dark:#000000;--bg:#0b0d10;--card-bg:#15181d;" +
                "--input-bg:#0f1114;--text:#e8eaed;--muted:#9199a6;--border:#262a31;" +
                "--danger:#f87171;--danger-bg:#2a1616;--success:#4ade80;--radius:8px;}" +
                "*{box-sizing:border-box;}" +
                "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;" +
                "margin:0;padding:0;background:var(--bg);color:var(--text);line-height:1.5;min-height:100vh;" +
                "display:flex;flex-direction:column;}" +
                ".site-header{background:var(--dark);padding:0 24px;border-bottom:1px solid var(--border);}" +
                // Full-width header bar in a real browser window - was
                // capped at max-width:1100px and centered, same as the
                // narrower page-content column, leaving dead space on both
                // sides on anything wider than that. Header/footer chrome
                // should use the whole window; only the actual page
                // content (.page/.hero-inner) needs a readable max-width.
                ".site-header-inner{padding:14px 0;display:flex;" +
                "align-items:center;gap:28px;flex-wrap:wrap;}" +
                ".brand{display:flex;align-items:center;gap:10px;color:#fff;text-decoration:none;" +
                "font-weight:700;font-size:17px;letter-spacing:.2px;}" +
                ".brand:hover{text-decoration:none;color:var(--accent-bright);}" +
                ".brand-mark{width:30px;height:30px;border-radius:8px;flex-shrink:0;background:var(--accent);" +
                "display:flex;align-items:center;justify-content:center;color:#fff;font-weight:700;font-size:15px;}" +
                ".site-nav{display:flex;gap:20px;flex:1;}" +
                ".site-nav a{color:var(--muted);font-size:14px;font-weight:600;}" +
                ".site-nav a .bi{margin-right:4px;}" +
                // Home is a bare <a>, a direct child of .site-nav - unlike
                // Explore/Grid Info, whose <a> is nested in .nav-dropdown and
                // gets display:inline-flex;align-items:center from
                // .dropdown-toggle below. Matches that centering so Home's
                // icon doesn't sit at a different vertical position than the
                // other two (found live: "Home is not the same as Explore
                // and Grid Info").
                ".site-nav>a{display:inline-flex;align-items:center;gap:4px;}" +
                ".site-nav a:hover{color:#fff;text-decoration:none;}" +
                ".site-actions{display:flex;align-items:center;gap:18px;}" +
                ".site-actions a{color:var(--muted);font-size:14px;font-weight:600;}" +
                ".site-actions a:hover{color:#fff;text-decoration:none;}" +
                ".site-actions .nav-user{color:#fff;font-size:14px;font-weight:600;}" +
                ".site-actions a.nav-cta{background:var(--accent);color:#fff;padding:8px 18px;" +
                "border-radius:40px;text-transform:uppercase;font-size:12px;letter-spacing:.3px;}" +
                ".site-actions a.nav-cta:hover{background:var(--accent-dark);text-decoration:none;}" +
                ".nav-dropdown{position:relative;}" +
                ".nav-dropdown .dropdown-toggle{display:inline-flex;align-items:center;gap:4px;" +
                "text-decoration:none;}" +
                // Bridges the visual gap between the toggle and the menu below
                // it - without this, moving the mouse from the toggle down to
                // the menu crosses empty space with nothing to :hover, so the
                // menu closes before the pointer ever reaches it.
                ".nav-dropdown::after{content:'';position:absolute;left:0;right:0;top:100%;height:14px;}" +
                ".nav-dropdown .dropdown-menu{display:none;position:absolute;right:0;top:100%;" +
                "margin-top:14px;min-width:190px;background:var(--card-bg);border:1px solid var(--border);" +
                "border-radius:8px;box-shadow:0 12px 28px rgba(0,0,0,.5);padding:6px;z-index:10;flex-direction:column;}" +
                ".nav-dropdown:hover .dropdown-menu,.nav-dropdown:focus-within .dropdown-menu," +
                ".nav-dropdown.open .dropdown-menu{display:flex;}" +
                ".dropdown-menu a{display:block;padding:8px 12px;border-radius:6px;font-size:13.5px;" +
                "font-weight:600;color:var(--text);white-space:nowrap;}" +
                ".dropdown-menu a:hover{background:var(--accent-tint);color:var(--accent-bright);" +
                "text-decoration:none;}" +
                // Small reusable icon-color utilities - previously every
                // .bi icon site-wide inherited plain text color, making a
                // long nav (or dropdown menu) read as a flat wall of
                // same-weight text. Applied to the top nav below; free to
                // reuse anywhere else an icon needs to stand out.
                ".ic-blue{color:#60a5fa;}.ic-cyan{color:#22d3ee;}.ic-green{color:#4ade80;}" +
                ".ic-amber{color:#fbbf24;}.ic-purple{color:#a78bfa;}.ic-pink{color:#f472b6;}" +
                ".site-nav .dropdown-toggle .bi:last-child{font-size:10px;margin-left:2px;color:var(--muted);}" +
                ".hero{background:linear-gradient(135deg,#000000 0%,#0d1a30 100%);" +
                "border-bottom:1px solid var(--border);padding:36px 24px;}" +
                ".hero-inner{max-width:1600px;margin:0 auto;padding:0 24px;}" +
                ".hero h1{font-size:30px;margin:0;color:#fff;}" +
                // Home-page marketing CTA row - deliberately separate from
                // .nav-cta (header sign-up link) since this needs a matching
                // secondary/outline button next to it, which the header never does.
                ".tagline-lead{font-size:16px;color:var(--muted);margin:0 0 22px;}" +
                ".cta-row{display:flex;gap:14px;flex-wrap:wrap;margin:0 0 30px;}" +
                ".cta-primary{background:var(--accent);color:#fff;padding:12px 28px;border-radius:40px;" +
                "text-transform:uppercase;font-size:13px;font-weight:700;letter-spacing:.3px;}" +
                ".cta-primary:hover{background:var(--accent-dark);text-decoration:none;}" +
                ".cta-secondary{border:2px solid var(--border);color:var(--text);padding:10px 26px;" +
                "border-radius:40px;text-transform:uppercase;font-size:13px;font-weight:700;letter-spacing:.3px;}" +
                ".cta-secondary:hover{border-color:var(--accent);color:var(--accent-bright);text-decoration:none;}" +
                ".site-main{padding:0 24px;flex:1 0 auto;}" +
                ".page{max-width:1600px;margin:0 auto;padding:32px 24px 60px;}" +
                ".card{background:var(--card-bg);border:1px solid var(--border);border-radius:var(--radius);" +
                "box-shadow:0 8px 24px rgba(0,0,0,.35);padding:32px 36px;}" +
                // Genuinely missing until now - .content-card is used as a
                // distinct raised section (Features' Platform Overview, My
                // Balance, Admin's Powered By/Membership Perks/Service
                // Status, every marketplace management page, ~15 call sites
                // total) but had no CSS rule anywhere in this file, so every
                // one of those pages has been rendering it as a bare,
                // unstyled div this whole time - found while looking at the
                // home page "as a whole" and noticing content read as flat/
                // undifferentiated compared to welcome.php's floating boxes.
                // h2:first-child below already strips the border-top/margin
                // for a heading that opens one of these, so no extra rule
                // needed for that.
                ".content-card{background:var(--input-bg);border:1px solid var(--border);" +
                "border-radius:var(--radius);padding:20px 24px;margin:0 0 20px;}" +
                ".content-card:last-child{margin-bottom:0;}" +
                // Live "this grid is real right now" strip for the home
                // page - same proof point welcome.php's own top bar leads
                // with (online-now/region-count), missing here entirely
                // until a visitor scrolled past the pitch cards down to
                // Economy.
                ".home-live-strip{display:flex;align-items:center;flex-wrap:wrap;gap:16px;" +
                "margin:0 0 18px;font-size:13.5px;color:var(--muted);}" +
                ".home-online-badge{display:inline-flex;align-items:center;gap:6px;color:var(--success);" +
                "font-weight:700;}" +
                // Classifieds beside Economy, same pairing/visual weight as
                // welcome.php - replaces stacking the same two data sources
                // in a different, inconsistent order.
                ".home-2col{display:flex;gap:20px;flex-wrap:wrap;margin:0 0 20px;}" +
                ".home-2col>.content-card{flex:1 1 320px;margin-bottom:0;}" +
                ".home-2col>.home-2col-wide{flex:2 1 420px;}" +
                ".site-footer{background:var(--dark);border-top:1px solid var(--border);padding:20px 24px;" +
                "margin-top:40px;}" +
                ".site-footer-inner{color:var(--muted);font-size:13.5px;}" +
                // Matches .hero h1 below - one h1 size site-wide instead of a
                // smaller default that made plain pages feel inconsistent
                // with hero-banner pages (explicit feedback).
                "h1{font-size:30px;margin:0 0 14px;color:var(--text);}" +
                "h2{font-size:16px;margin:26px 0 12px;color:var(--text);border-top:1px solid var(--border);" +
                "padding-top:20px;}" +
                "h2:first-child{border-top:none;padding-top:0;margin-top:0;}" +
                "h3{font-size:14.5px;margin:0 0 6px;color:var(--text);}" +
                "p{margin:0 0 14px;}" +
                "a{color:var(--accent-bright);text-decoration:none;}" +
                "a:hover{text-decoration:underline;}" +
                "table{width:100%;border-collapse:collapse;margin:8px 0 18px;font-size:13.5px;}" +
                "th{text-align:left;background:var(--accent-tint);color:var(--accent-bright);font-weight:700;" +
                "padding:10px 12px;border-bottom:1px solid var(--border);white-space:nowrap;" +
                "text-transform:uppercase;font-size:11.5px;letter-spacing:.3px;}" +
                "td{padding:10px 12px;border-bottom:1px solid var(--border);vertical-align:middle;}" +
                "tr:last-child td{border-bottom:none;}" +
                "tr:hover td{background:rgba(255,255,255,.03);}" +
                "td form{display:inline-block;margin:0;}" +
                "td form input[type=hidden]{display:none;}" +
                "form{margin:0 0 6px;}" +
                "form label{display:block;margin:0 0 14px;font-size:13.5px;font-weight:600;color:var(--muted);}" +
                "input,textarea,select{margin-top:6px;padding:9px 11px;width:100%;box-sizing:border-box;" +
                "border:1px solid var(--border);border-radius:6px;font-size:14px;font-family:inherit;" +
                "background:var(--input-bg);color:var(--text);}" +
                "td input,td select{width:auto;margin-top:0;}" +
                "input:focus,textarea:focus,select:focus{outline:none;border-color:var(--accent);" +
                "box-shadow:0 0 0 3px var(--accent-tint);}" +
                "label input[type=checkbox]{width:auto;margin:0 8px 0 0;vertical-align:middle;}" +
                "button{margin-top:6px;padding:11px 26px;border:2px solid var(--accent);border-radius:40px;" +
                "background:var(--accent);color:#fff;font-size:13px;font-weight:700;cursor:pointer;" +
                "text-transform:uppercase;letter-spacing:.3px;transition:background .15s ease,border-color .15s ease;}" +
                "button:hover{background:var(--accent-dark);border-color:var(--accent-dark);}" +
                "button:active{transform:translateY(1px);}" +
                "td button{padding:6px 16px;font-size:12px;margin-top:0;background:transparent;color:var(--danger);" +
                "border-color:var(--border);}" +
                "td button:hover{background:var(--danger-bg);border-color:var(--danger);}" +
                ".balance{display:inline-block;font-size:1.25em;font-weight:700;color:var(--accent-bright);" +
                "background:var(--accent-tint);padding:8px 18px;border-radius:999px;margin-bottom:6px;}" +
                ".error{background:var(--danger-bg);color:var(--danger);border-left:3px solid var(--danger);" +
                "padding:12px 14px;border-radius:6px;font-size:13.5px;margin:0 0 16px;}" +
                ".success{background:rgba(74,222,128,.12);color:var(--success);border-left:3px solid var(--success);" +
                "padding:12px 14px;border-radius:6px;font-size:13.5px;margin:0 0 16px;}" +
                ".announcement{background:var(--input-bg);border-left:4px solid var(--accent);" +
                "padding:14px 16px;border-radius:6px;font-size:14px;margin:0 0 18px;}" +
                ".news-item{padding:16px 0;border-top:1px solid var(--border);}" +
                ".news-item:first-of-type{border-top:none;padding-top:0;}" +
                ".news-item h3{margin-bottom:4px;}" +
                ".news-meta{color:var(--muted);font-size:13px;margin:0 0 8px;}" +
                ".stats-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:14px;margin:0 0 8px;}" +
                ".stat-card{background:var(--input-bg);border:1px solid var(--border);border-radius:8px;padding:14px 16px;}" +
                ".stat-label{color:var(--muted);font-size:12.5px;text-transform:uppercase;letter-spacing:.4px;margin:0 0 6px;}" +
                ".stat-value{color:var(--accent-bright);font-size:1.5em;font-weight:700;}" +
                ".stat-sub{color:var(--muted);font-size:13px;margin-top:2px;}" +
                ".widget-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:16px;margin:0 0 8px;}" +
                ".widget-card{background:var(--input-bg);border:1px solid var(--border);border-radius:8px;padding:14px 16px;}" +
                ".widget-card h3{margin:0 0 4px;}" +
                ".widget-meta{color:var(--muted);font-size:13px;margin:0 0 6px;}" +
                ".widget-card-thumb{width:100%;height:140px;object-fit:cover;border-radius:6px;" +
                "margin:-14px -16px 10px;display:block;width:calc(100% + 32px);}" +
                // Clickable variant of .widget-card (Dashboard's Quick Links) -
                // same hover-lift/no-underline treatment as .bucket, since an
                // entire card acting as one <a> looks broken if hover
                // underlines all its text.
                "a.dashboard-link{display:block;color:inherit;transition:border-color .15s ease,transform .15s ease;}" +
                "a.dashboard-link:hover{border-color:var(--accent);transform:translateY(-2px);text-decoration:none;}" +
                "a.dashboard-link h3{color:var(--text);}" +
                "a.dashboard-link h3 .bi{color:var(--accent-bright);margin-right:6px;}" +
                // Icon-headed, hover-lift cards - matches the reference
                // grid-portal projects' own region/feature-card treatment
                // (translateY lift + accent border-left + real box-shadow on
                // hover) rather than the flat, static widget-card used
                // elsewhere. Reserved for pages that specifically want that
                // heavier, more "eye-catching" presentation (Features,
                // region-type comparisons) rather than applied everywhere.
                ".feature-card{background:var(--input-bg);border:1px solid var(--border);border-left:3px solid transparent;" +
                "border-radius:10px;padding:20px 22px;transition:transform .18s ease,box-shadow .18s ease,border-color .18s ease;}" +
                ".feature-card:hover{transform:translateY(-4px);border-left-color:var(--accent);" +
                "box-shadow:0 14px 28px rgba(0,0,0,.35);}" +
                ".feature-card h3{display:flex;align-items:center;gap:10px;margin:0 0 10px;font-size:16px;}" +
                ".feature-card h3 .bi{color:var(--accent-bright);font-size:1.3em;}" +
                ".feature-card ul{margin:0;padding-left:0;list-style:none;}" +
                ".feature-card li{margin:0 0 8px;padding-left:22px;position:relative;font-size:13.5px;color:var(--text);}" +
                ".feature-card li:last-child{margin-bottom:0;}" +
                ".feature-card li .bi{position:absolute;left:0;top:1px;color:var(--accent-bright);}" +
                ".pill{display:inline-flex;align-items:center;gap:4px;padding:3px 11px;border-radius:999px;" +
                "font-size:11.5px;font-weight:700;text-transform:uppercase;letter-spacing:.3px;}" +
                ".pill-yes{background:rgba(74,222,128,.15);color:var(--success);}" +
                ".pill-no{background:rgba(153,158,166,.15);color:var(--muted);}" +
                ".pill-warn{background:var(--danger-bg);color:var(--danger);}" +
                ".feature-grid-3{display:grid;grid-template-columns:repeat(auto-fit,minmax(290px,1fr));gap:20px;margin:0 0 8px;}" +
                ".powered-group-label{flex:0 0 100%;text-align:center;font-size:11px;letter-spacing:.12em;" +
                "text-transform:uppercase;color:var(--muted);margin:14px 0 2px;}" +
                ".powered-group-label:first-child{margin-top:0;}" +
                ".powered-grid{display:flex;flex-wrap:wrap;justify-content:center;gap:14px;margin:0 0 8px;}" +
                ".powered-tile{flex:0 1 150px;min-width:130px;max-width:150px;text-align:center;" +
                "background:var(--input-bg);border:1px solid var(--border);border-radius:10px;padding:16px 10px;}" +
                ".powered-tile .bi{font-size:1.9rem;color:var(--accent-bright);display:block;margin-bottom:6px;}" +
                ".powered-tile-title{font-weight:700;color:var(--text);font-size:13.5px;}" +
                ".powered-tile-sub{font-size:11.5px;color:var(--muted);margin-top:2px;}" +
                ".perks-list{list-style:none;margin:0;padding:0;}" +
                ".perks-list li{padding-left:24px;position:relative;margin:0 0 9px;font-size:13.5px;color:var(--text);}" +
                ".perks-list li .bi{position:absolute;left:0;top:2px;color:var(--accent-bright);}" +
                // Search landing layout - structurally follows the reference
                // grid-search page (hero-search/chips/trending/stat-strip)
                // the user pasted in, reimplemented in our own markup/CSS/JS
                // and Confluence's palette, with no external fonts/icon-CDN
                // (this connector has to work with no internet egress).
                ".subnav{display:flex;gap:6px;margin:0 0 24px;border-bottom:1px solid var(--border);}" +
                ".subnav a{padding:10px 16px;font-size:13.5px;font-weight:700;color:var(--muted);" +
                "border-bottom:2px solid transparent;margin-bottom:-1px;}" +
                ".subnav a.active{color:var(--accent-bright);border-bottom-color:var(--accent);}" +
                ".subnav a:hover{color:var(--accent-bright);text-decoration:none;}" +
                ".ico{width:14px;height:14px;vertical-align:-2px;margin-right:5px;flex-shrink:0;}" +
                ".search-input{position:relative;}" +
                ".search-input .ico{position:absolute;left:14px;top:50%;margin-top:-7px;color:var(--muted);}" +
                ".search-input input{padding-left:36px !important;}" +
                ".bucket .ico{display:block;width:22px;height:22px;margin:0 0 10px;color:var(--accent-bright);}" +
                ".trending-label .ico{color:var(--muted);}" +
                ".hero-search-wrap{text-align:center;padding:6px 0 4px;}" +
                ".hero-search-wrap .tagline{color:var(--muted);font-size:13px;font-weight:700;" +
                "text-transform:uppercase;letter-spacing:1.2px;margin:0 0 20px;}" +
                ".hero-search{display:flex;gap:10px;flex-wrap:wrap;justify-content:center;" +
                "margin:0 0 18px;position:relative;}" +
                ".hero-search .search-input{flex:1;min-width:240px;max-width:480px;position:relative;}" +
                ".hero-search .search-input input{width:100%;margin-top:0;padding:12px 18px;border-radius:40px;}" +
                ".hero-search select{width:auto;margin-top:0;border-radius:40px;}" +
                ".hero-search button{margin-top:0;}" +
                ".chips{display:flex;flex-wrap:wrap;gap:8px;justify-content:center;margin:0 0 20px;}" +
                ".trending{display:flex;flex-wrap:wrap;gap:8px;align-items:center;justify-content:center;" +
                "margin:0 0 20px;}" +
                ".trending-label{color:var(--muted);font-size:11.5px;" +
                "font-weight:700;text-transform:uppercase;letter-spacing:.6px;margin-right:2px;}" +
                ".chip{background:var(--input-bg);border:1px solid var(--border);" +
                "border-radius:999px;padding:8px 16px;font-size:12.5px;font-weight:700;color:var(--text);" +
                "text-transform:uppercase;letter-spacing:.3px;cursor:pointer;font-family:inherit;}" +
                ".trending .chip{text-transform:none;font-weight:600;padding:6px 14px;color:var(--muted);}" +
                ".chip:hover{border-color:var(--accent);color:var(--accent-bright);text-decoration:none;}" +
                ".stat-strip{display:flex;flex-wrap:wrap;gap:20px;justify-content:center;" +
                "padding-top:18px;border-top:1px solid var(--border);}" +
                ".stat-strip .stat{color:var(--muted);font-size:13px;}" +
                ".stat-strip .stat strong{color:var(--accent-bright);font-size:15px;}" +
                ".bucket-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:16px;}" +
                ".bucket{display:block;background:var(--input-bg);border:1px solid var(--border);" +
                "border-radius:10px;padding:20px;text-decoration:none;" +
                "transition:border-color .15s ease,transform .15s ease;}" +
                ".bucket:hover{border-color:var(--accent);transform:translateY(-2px);text-decoration:none;}" +
                ".bucket .b-name{color:var(--text);font-weight:700;font-size:15px;margin-bottom:4px;}" +
                ".bucket .b-count{color:var(--muted);font-size:12.5px;}" +
                ".result-card{background:var(--input-bg);border:1px solid var(--border);border-radius:8px;" +
                "padding:14px 16px;margin:0 0 10px;}" +
                ".result-badge{display:inline-block;background:var(--accent-tint);color:var(--accent-bright);" +
                "font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.3px;" +
                "padding:3px 9px;border-radius:999px;margin-bottom:6px;}" +
                ".result-card h3{margin:4px 0 4px;font-size:15px;}" +
                ".result-meta{color:var(--muted);font-size:12.5px;margin:0 0 4px;}" +
                ".result-desc{color:var(--text);font-size:13.5px;margin:0;}" +
                ".ac-box{display:none;position:absolute;left:0;right:0;top:100%;margin-top:4px;" +
                "background:var(--card-bg);border:1px solid var(--border);border-radius:8px;" +
                "box-shadow:0 12px 28px rgba(0,0,0,.5);z-index:20;overflow:hidden;}" +
                ".ac-item{padding:9px 14px;font-size:13.5px;cursor:pointer;}" +
                ".ac-item.active,.ac-item:hover{background:var(--accent-tint);color:var(--accent-bright);}" +
                "pre{background:var(--input-bg);border:1px solid var(--border);border-radius:8px;" +
                "padding:12px 14px;font-size:12.5px;line-height:1.5;overflow-x:auto;color:var(--text);}" +
                "@media(max-width:480px){.card{padding:20px 18px;}.page{padding:18px 0 40px;}" +
                ".hero{padding:24px 18px;}.hero h1{font-size:22px;}.site-header,.site-main,.hero," +
                ".site-footer{padding-left:16px;padding-right:16px;}}" +
                // Persistent left sidebar app-shell for logged-in pages -
                // structurally modeled on 3RD Rock Grid's own resident panel
                // (icon-labeled nav list, user identity block up top, Log Out
                // pinned at the bottom) with this codebase's own existing
                // color palette, not a new one. Only wraps logged-in pages
                // (WritePage when session != null); anonymous visitors and
                // viewer-embedded requests (WriteBarePage) are unaffected.
                ".app-shell{display:flex;flex:1 0 auto;min-height:100vh;}" +
                ".app-sidebar{width:250px;flex-shrink:0;background:var(--dark);" +
                "border-right:1px solid var(--border);display:flex;flex-direction:column;" +
                "position:sticky;top:0;height:100vh;overflow-y:auto;}" +
                ".sidebar-brand{display:flex;align-items:center;gap:10px;color:#fff;text-decoration:none;" +
                "font-weight:700;font-size:16px;padding:18px 20px;border-bottom:1px solid var(--border);}" +
                ".sidebar-brand:hover{text-decoration:none;color:var(--accent-bright);}" +
                ".sidebar-user{display:flex;align-items:center;gap:10px;padding:16px 20px;" +
                "border-bottom:1px solid var(--border);}" +
                ".sidebar-user-avatar{width:36px;height:36px;border-radius:50%;background:var(--accent);" +
                "color:#fff;font-weight:700;font-size:15px;display:flex;align-items:center;" +
                "justify-content:center;flex-shrink:0;}" +
                ".sidebar-user-name{color:var(--text);font-size:13.5px;font-weight:700;" +
                "overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}" +
                ".sidebar-user-role{color:var(--muted);font-size:12px;text-transform:uppercase;" +
                "letter-spacing:.3px;margin-top:2px;}" +
                ".sidebar-nav{flex:1;padding:14px 12px;}" +
                ".sidebar-nav-label{color:var(--muted);font-size:11.5px;font-weight:700;" +
                "text-transform:uppercase;letter-spacing:.5px;padding:14px 10px 6px;}" +
                ".sidebar-nav-label:first-child{padding-top:4px;}" +
                // Collapsible sidebar groups (Avatars/Account) - plain
                // <details>/<summary>, same "no JS needed" approach as the
                // login page's own portal-login toggle. ::marker hidden and
                // replaced with a chevron that flips via the [open]
                // attribute selector, no script required either way.
                ".sidebar-nav-group summary.sidebar-nav-label{cursor:pointer;display:flex;" +
                "align-items:center;justify-content:space-between;list-style:none;margin-bottom:2px;}" +
                ".sidebar-nav-group summary.sidebar-nav-label::-webkit-details-marker{display:none;}" +
                ".sidebar-nav-group summary .bi-chevron-down{font-size:11px;transition:transform .15s;}" +
                ".sidebar-nav-group[open] summary .bi-chevron-down{transform:rotate(180deg);}" +
                ".sidebar-nav a{display:flex;align-items:center;gap:10px;padding:10px 10px;" +
                "border-radius:6px;font-size:15px;font-weight:600;color:var(--text);margin-bottom:2px;}" +
                ".sidebar-nav a .bi{font-size:17px;width:18px;text-align:center;flex-shrink:0;}" +
                ".sidebar-nav a:hover{background:var(--accent-tint);color:var(--accent-bright);" +
                "text-decoration:none;}" +
                ".sidebar-nav a.active{background:var(--accent-tint);color:var(--accent-bright);}" +
                ".sidebar-logout{display:flex;align-items:center;gap:10px;padding:14px 20px;" +
                "border-top:1px solid var(--border);color:var(--danger);font-size:13.5px;font-weight:600;}" +
                ".sidebar-logout:hover{background:var(--danger-bg);text-decoration:none;}" +
                ".app-main{flex:1;min-width:0;display:flex;flex-direction:column;}" +
                ".app-topbar{display:flex;align-items:center;justify-content:space-between;" +
                "padding:14px 24px;background:var(--dark);border-bottom:1px solid var(--border);}" +
                ".sidebar-toggle{display:none;background:transparent;border:none;color:var(--text);" +
                "font-size:20px;padding:4px 8px;margin:0;cursor:pointer;}" +
                ".app-topbar .site-nav{gap:16px;}" +
                ".app-topbar .site-nav a{font-size:13px;}" +
                "@media(max-width:900px){.app-sidebar{position:fixed;left:-260px;top:0;bottom:0;z-index:100;" +
                "transition:left .2s ease;box-shadow:0 0 32px rgba(0,0,0,.6);}" +
                ".app-sidebar.open{left:0;}.sidebar-toggle{display:block;}" +
                ".app-topbar .site-nav{display:none;}" +
                ".sidebar-backdrop{display:none;position:fixed;inset:0;background:rgba(0,0,0,.5);z-index:99;}" +
                ".sidebar-backdrop.open{display:block;}}";

        // The nav dropdowns (account/admin) open on :hover for desktop mice,
        // but touch devices have no hover state - tapping a real <a href>
        // toggle just follows the link instead of revealing the menu. This
        // makes the same toggle also open/close on tap/click, universally,
        // without disturbing the hover behavior desktop already has.
        // Grouped top-nav dropdowns, shared by both the logged-in and
        // anonymous headers - the flat link list had grown to 8-9 items as
        // pages were added this pass (Search/Destinations/World Map/
        // Economy/Features/Status/Viewers/Help[/About/Support]), reading as
        // a wall of small same-size text. Reuses the existing .nav-dropdown/
        // .dropdown-menu/DropdownScript infrastructure (already built for
        // the anonymous header's old account menu) rather than a new
        // mechanism - it's already generic (delegated click handler, not
        // wired to one specific dropdown), it just wasn't applied to more
        // than one dropdown before. includeAboutSupport is anonymous-only
        // (About/Support already live in the logged-in sidebar instead).
        private string RenderTopNavGroups(bool includeAboutSupport)
        {
            StringBuilder sb = new StringBuilder();

            sb.Append("<div class=\"nav-dropdown\"><a href=\"#\" class=\"dropdown-toggle\">")
              .Append("<i class=\"bi bi-compass ic-cyan\"></i> Explore <i class=\"bi bi-caret-down-fill\"></i></a>")
              .Append("<div class=\"dropdown-menu\">")
              .Append("<a href=\"").Append(BasePath).Append("/search\"><i class=\"bi bi-search ic-blue\"></i> Search</a>")
              .Append("<a href=\"").Append(BasePath).Append("/destinations\"><i class=\"bi bi-signpost-2 ic-green\"></i> Destinations</a>")
              .Append("<a href=\"").Append(BasePath).Append("/worldmap\"><i class=\"bi bi-map ic-amber\"></i> World Map</a>")
              .Append("<a href=\"").Append(BasePath).Append("/economy\"><i class=\"bi bi-wallet2 ic-green\"></i> Economy</a>")
              .Append("</div></div>");

            sb.Append("<div class=\"nav-dropdown\"><a href=\"#\" class=\"dropdown-toggle\">")
              .Append("<i class=\"bi bi-info-circle ic-purple\"></i> Grid Info <i class=\"bi bi-caret-down-fill\"></i></a>")
              .Append("<div class=\"dropdown-menu\">")
              .Append("<a href=\"").Append(BasePath).Append("/gridstatus\"><i class=\"bi bi-activity ic-green\"></i> Status</a>")
              .Append("<a href=\"").Append(BasePath).Append("/help\"><i class=\"bi bi-question-circle ic-cyan\"></i> Help</a>");
            if (includeAboutSupport)
            {
                if (HasStaticPage("about"))
                    sb.Append("<a href=\"").Append(BasePath).Append("/page/about\"><i class=\"bi bi-info-circle ic-purple\"></i> About</a>");
                sb.Append("<a href=\"").Append(BasePath).Append("/support\"><i class=\"bi bi-life-preserver ic-pink\"></i> Support</a>");
            }
            sb.Append("</div></div>");

            return sb.ToString();
        }

        private const string DropdownScript =
                "<script>document.addEventListener('click',function(e){" +
                "var t=e.target.closest?e.target.closest('.dropdown-toggle'):null;" +
                "if(t){e.preventDefault();var dd=t.closest('.nav-dropdown');" +
                "var wasOpen=dd.classList.contains('open');" +
                "document.querySelectorAll('.nav-dropdown.open').forEach(function(d){d.classList.remove('open');});" +
                "if(!wasOpen)dd.classList.add('open');" +
                "}else if(!e.target.closest||!e.target.closest('.nav-dropdown')){" +
                "document.querySelectorAll('.nav-dropdown.open').forEach(function(d){d.classList.remove('open');});" +
                "}});</script>";

        // Mobile sidebar toggle - the sidebar is position:fixed and slid
        // off-screen below 900px (see .app-sidebar/.app-sidebar.open in
        // PageCss); this just flips the .open class on the sidebar and its
        // backdrop. No Bootstrap JS dependency needed for this one thing.
        private const string SidebarToggleScript =
                "<script>document.addEventListener('click',function(e){" +
                "if(e.target.closest&&e.target.closest('.sidebar-toggle')){" +
                "document.getElementById('appSidebar').classList.toggle('open');" +
                "document.getElementById('sidebarBackdrop').classList.toggle('open');" +
                "}else if(e.target.closest&&e.target.closest('#sidebarBackdrop')){" +
                "document.getElementById('appSidebar').classList.remove('open');" +
                "document.getElementById('sidebarBackdrop').classList.remove('open');" +
                "}});</script>";

        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        #endregion Rendering
    }
}
