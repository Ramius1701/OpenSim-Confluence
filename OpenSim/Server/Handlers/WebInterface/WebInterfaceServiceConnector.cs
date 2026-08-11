using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
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
        private INewsService m_NewsService;
        private IEventsService m_EventsService;
        private ISupportTicketService m_SupportTicketService;
        private IStaticPageService m_StaticPageService;
        private IGridSettingsService m_GridSettingsService;
        private IUserProfilesService m_UserProfilesService;
        private IFriendsService m_FriendsService;
        private ISearchService m_SearchService;
        private IGroupsSearchProvider m_GroupsSearchService;
        private string m_webConsoleSecret = string.Empty;

        private string m_gridName = "OpenSim Grid";
        private string m_gridNick = "OpenSim";
        private string m_welcomeMessage = "";
        private string m_publicBaseUrl = string.Empty;

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
            m_NewsService = LoadReusedPlugin<INewsService>(config, "NewsService", args);
            m_EventsService = LoadReusedPlugin<IEventsService>(config, "EventsService", args);
            m_SupportTicketService = LoadReusedPlugin<ISupportTicketService>(config, "SupportTicketService", args);
            m_StaticPageService = LoadReusedPlugin<IStaticPageService>(config, "StaticPageService", args);
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
                m_welcomeMessage = loginService.GetString("WelcomeMessage", string.Empty).Replace("<USERNAME>", "");

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
                "/dashboard", "/login", "/register", "/viewers", "/destinations", "/features",
                "/support", "/search", "/landsearch", "/admin", "/profile", "/friends",
                "/change-password", "/change-email", "/transactions", "/myclassifieds",
                "/myevents", "/forgot-password", "/reset-password", "/logout",
                "/myregions", "/myinventory", "/page", "/partner", "/myestates", "/delete-account"
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
                    case BasePath + "/viewers":
                        HandleViewers(request, response);
                        break;
                    case BasePath + "/destinations":
                        HandleDestinations(request, response);
                        break;
                    case BasePath + "/features":
                        HandleFeatures(request, response);
                        break;
                    case BasePath + "/support":
                        HandleSupport(request, response);
                        break;
                    case BasePath + "/search":
                        HandleSearch(request, response);
                        break;
                    case BasePath + "/landsearch":
                        HandleLandSearch(request, response);
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
                    case BasePath + "/myregions":
                        HandleMyRegions(request, response);
                        break;
                    case BasePath + "/myregions/oar-save":
                        HandleMyRegionsOarSave(request, response);
                        break;
                    case BasePath + "/myregions/oar-load":
                        HandleMyRegionsOarLoad(request, response);
                        break;
                    case BasePath + "/myinventory":
                        HandleMyInventory(request, response);
                        break;
                    case BasePath + "/myinventory/iar-save":
                        HandleMyInventoryIarSave(request, response);
                        break;
                    case BasePath + "/myinventory/iar-load":
                        HandleMyInventoryIarLoad(request, response);
                        break;
                    default:
                        // Static pages are served at an operator-chosen slug,
                        // not a fixed path this switch can list in advance -
                        // checked last so it never shadows any of the fixed
                        // routes above.
                        if (path.StartsWith(BasePath + "/page/", StringComparison.Ordinal))
                            HandleStaticPage(request, response, path.Substring((BasePath + "/page/").Length));
                        else
                            response.StatusCode = (int)HttpStatusCode.NotFound;
                        break;
                }
            }
            catch (Exception e)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.RawBuffer = Encoding.UTF8.GetBytes("Internal error: " + e.Message);
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

        private string CreateSession(UUID principalID, string name, bool isAdmin)
        {
            string token = UUID.Random().ToString();
            m_sessions[token] = new WebSession
            {
                PrincipalID = principalID,
                Name = name,
                IsAdmin = isAdmin,
                Expires = DateTime.UtcNow.Add(SessionLifetime)
            };
            return token;
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
        private void HandleHome(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string welcomeMessage = GetSetting("WelcomeMessage", m_welcomeMessage);
            string welcome = string.IsNullOrEmpty(welcomeMessage)
                    ? "Welcome to " + Html(gridName) + "."
                    : Html(welcomeMessage);

            bool allowRegistration = GetSetting("AllowRegistration", "true") == "true";
            string registerLink = allowRegistration
                    ? "<p><a href=\"" + BasePath + "/register\">Sign up for a new account</a></p>"
                    : string.Empty;

            string body = "<h1>" + Html(gridName) + "</h1>"
                    + RenderAnnouncement()
                    + "<p>" + welcome + "</p>"
                    + "<p><a href=\"" + BasePath + "/login\">Log in to your account</a></p>"
                    + registerLink
                    + RenderEconomyStats()
                    + RenderFeaturedClassifieds(6)
                    + RenderUpcomingEvents(5)
                    + RenderNewsFeed(5);

            WritePage(request, response, gridName, body);
        }

        // The in-viewer login splash screen - see [GridInfoService] "welcome" in
        // Robust.HG.ini, which tells the viewer to fetch exactly this filename.
        // Rendered inside the viewer's own (small, embedded) login panel, so kept
        // deliberately simpler/shorter than the full home page - task #23 from
        // the WhiteCore-Dev re-audit's "all of it" list, a grid-operator
        // announcements feed shown on both this splash screen and the home page.
        private void HandleWelcome(IOSHttpRequest request, IOSHttpResponse response)
        {
            string gridName = GetSetting("GridName", m_gridName);
            string welcomeMessage = GetSetting("WelcomeMessage", m_welcomeMessage);
            string welcome = string.IsNullOrEmpty(welcomeMessage)
                    ? "Welcome to " + Html(gridName) + "."
                    : Html(welcomeMessage);

            string body = "<h1>" + Html(gridName) + "</h1>"
                    + RenderAnnouncement()
                    + "<p>" + welcome + "</p>"
                    + RenderEconomyStats()
                    + RenderFeaturedClassifieds(6)
                    + RenderUpcomingEvents(5)
                    + RenderNewsFeed(3);

            WritePage(request, response, gridName, body);
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
        // Firestorm's OpenSim-specific download pages and Cool VL Viewer
        // come from the user's own OpenSim-Grid-Interface (viewers.php);
        // Alchemy/Kokua/Singularity/Lumiya/MobileGridClient/
        // PocketMetaverse/Radegast come from WhiteCore-Dev's real,
        // currently-used help.html viewer list (WhiteCore's own
        // region_list.html/region_search.html/online_users.html were all
        // explicitly commented "No longer used" by its own maintainers -
        // checked before reusing anything, not everything WhiteCore ships
        // is still live). Real URLs from both sources, not placeholders.
        private static readonly (string Name, string Url, string Note)[] DesktopViewers =
        {
            ("Firestorm (Windows)", "https://www.firestormviewer.org/windows-for-open-simulator/", "OpenSim-specific build"),
            ("Firestorm (macOS)", "https://www.firestormviewer.org/mac-for-open-simulator/", "OpenSim-specific build"),
            ("Firestorm (Linux)", "https://www.firestormviewer.org/linux-for-open-simulator/", "OpenSim-specific build"),
            ("Alchemy", "https://www.alchemyviewer.org/pages/downloads.html", "Modern, actively developed"),
            ("Kokua", "http://kokuaviewer.org", "OpenSim-focused fork"),
            ("Singularity", "http://www.singularityviewer.org", "Lightweight, classic interface"),
            ("Cool VL Viewer", "https://sldev.free.fr/", "Long-running, OpenSim-compatible"),
        };

        private static readonly (string Name, string Url, string Note)[] MobileViewers =
        {
            ("Lumiya", "http://www.lumiyaviewer.com", "Android"),
            ("Mobile Grid Client", "http://mobilegridclient.com", "Android/iOS"),
            ("Pocket Metaverse", "http://www.pocketmetaverse.com", "iOS"),
            ("Radegast", "https://radegast.life/", "Lightweight desktop/text client"),
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

            WritePage(request, response, "Confluence Grid - Get a Viewer", sb.ToString());
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

        // Destinations / world map - the "fill in blanks" counterpart to
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
        private void HandleDestinations(IOSHttpRequest request, IOSHttpResponse response)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>Destinations</h1><p>Explore regions on this grid. Click a region to teleport (opens in your viewer).</p>");

            if (m_GridService == null)
            {
                sb.Append("<p>Grid service is not available.</p>");
                WritePage(request, response, "Confluence Grid - Destinations", sb.ToString());
                return;
            }

            List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
            if (regions.Count == 0)
            {
                sb.Append("<p>No regions are online yet.</p>");
                WritePage(request, response, "Confluence Grid - Destinations", sb.ToString());
                return;
            }

            // Positions expressed in 256m "region units" (the same unit
            // RegionCoordX/Y are already in) so var-regions occupy
            // proportionally more space than standard ones.
            const double unitMeters = 256.0;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (GridRegion region in regions)
            {
                double x0 = region.RegionCoordX;
                double y0 = region.RegionCoordY;
                double x1 = x0 + region.RegionSizeX / unitMeters;
                double y1 = y0 + region.RegionSizeY / unitMeters;
                if (x0 < minX) minX = x0;
                if (y0 < minY) minY = y0;
                if (x1 > maxX) maxX = x1;
                if (y1 > maxY) maxY = y1;
            }

            double spanX = Math.Max(maxX - minX, 1);
            double spanY = Math.Max(maxY - minY, 1);
            // Pad 5% on every side so edge regions aren't flush against the frame.
            double pad = 0.05;

            sb.Append("<div class=\"world-map\">");
            foreach (GridRegion region in regions)
            {
                double x0 = region.RegionCoordX;
                double y0 = region.RegionCoordY;
                double wUnits = region.RegionSizeX / unitMeters;
                double hUnits = region.RegionSizeY / unitMeters;

                double leftPct = (pad + (1 - 2 * pad) * (x0 - minX) / spanX) * 100;
                // Screen Y grows downward; grid Y grows northward - flip so north is up.
                double topPct = (pad + (1 - 2 * pad) * (maxY - (y0 + hUnits)) / spanY) * 100;
                double widthPct = (1 - 2 * pad) * wUnits / spanX * 100;
                double heightPct = (1 - 2 * pad) * hUnits / spanY * 100;

                string tileUrl = "/map/map-1-" + region.RegionCoordX + "-" + region.RegionCoordY + "-objects.jpg";
                string teleportUrl = "secondlife:///app/teleport/" + Uri.EscapeDataString(region.RegionName) + "/128/128/25";

                sb.Append("<a class=\"world-map-region\" href=\"").Append(Html(teleportUrl)).Append("\" ")
                  .Append("style=\"left:").Append(leftPct.ToString("0.###", CultureInfo.InvariantCulture)).Append("%;top:")
                  .Append(topPct.ToString("0.###", CultureInfo.InvariantCulture)).Append("%;width:")
                  .Append(widthPct.ToString("0.###", CultureInfo.InvariantCulture)).Append("%;height:")
                  .Append(heightPct.ToString("0.###", CultureInfo.InvariantCulture)).Append("%;")
                  .Append("background-image:url('").Append(Html(tileUrl)).Append("');\">")
                  .Append("<span class=\"world-map-label\">").Append(Html(region.RegionName)).Append("</span>")
                  .Append("</a>");
            }
            sb.Append("</div>");

            sb.Append("<h2>All Regions</h2><table><tr><th>Region</th><th>Size</th><th></th></tr>");
            foreach (GridRegion region in regions)
            {
                string teleportUrl = "secondlife:///app/teleport/" + Uri.EscapeDataString(region.RegionName) + "/128/128/25";
                sb.Append("<tr><td>").Append(Html(region.RegionName)).Append("</td>")
                  .Append("<td>").Append(region.RegionSizeX).Append("x").Append(region.RegionSizeY).Append("</td>")
                  .Append("<td><a href=\"").Append(Html(teleportUrl)).Append("\">Teleport</a></td></tr>");
            }
            sb.Append("</table>");

            WritePage(request, response, "Confluence Grid - Destinations", sb.ToString());
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
        private void HandleProfile(IOSHttpRequest request, IOSHttpResponse response)
        {
            string idParam = request.QueryString.Get("id");
            if (string.IsNullOrEmpty(idParam) || !UUID.TryParse(idParam, out UUID userId))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, "Confluence Grid - Profile", "<h1>Profile not found</h1><p>No profile ID given.</p>");
                return;
            }

            UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, userId);
            if (account == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                WritePage(request, response, "Confluence Grid - Profile", "<h1>Profile not found</h1><p>No resident with that ID.</p>");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<h1>").Append(Html(account.Name)).Append("</h1>");

            DateTime memberSince = Utils.UnixTimeToDateTime((uint)account.Created);
            sb.Append("<p class=\"news-meta\">Resident since ").Append(Html(memberSince.ToString("MMMM d, yyyy"))).Append("</p>");

            if (m_GridUserService != null)
            {
                GridUserInfo info = m_GridUserService.GetGridUserInfo(userId.ToString());
                if (info != null)
                {
                    string status = info.Online
                            ? "Online now"
                            : info.Logout > DateTime.MinValue.AddYears(1)
                                    ? "Last seen " + Html(info.Logout.ToString("yyyy-MM-dd"))
                                    : "Never logged in";
                    sb.Append("<p class=\"news-meta\">").Append(status).Append("</p>");
                }
            }

            if (m_UserProfilesService != null)
            {
                UserProfileProperties props = new UserProfileProperties { UserId = userId };
                string propsResult = string.Empty;
                m_UserProfilesService.AvatarPropertiesRequest(ref props, ref propsResult);

                if (props.PartnerId != UUID.Zero)
                {
                    UserAccount partner = m_UserAccountService?.GetUserAccount(UUID.Zero, props.PartnerId);
                    if (partner != null)
                    {
                        sb.Append("<p><strong>Partner:</strong> <a href=\"").Append(BasePath).Append("/profile?id=")
                          .Append(partner.PrincipalID).Append("\">").Append(Html(partner.Name)).Append("</a></p>");
                    }
                }

                if (!string.IsNullOrEmpty(props.AboutText))
                {
                    sb.Append("<h2>About</h2><p>").Append(Html(props.AboutText).Replace("\n", "<br/>")).Append("</p>");
                }

                if (!string.IsNullOrEmpty(props.FirstLifeText))
                {
                    sb.Append("<h2>First Life</h2><p>").Append(Html(props.FirstLifeText).Replace("\n", "<br/>")).Append("</p>");
                }

                OSD picksOsd = m_UserProfilesService.AvatarPicksRequest(userId);
                if (picksOsd is OSDArray picksArray && picksArray.Count > 0)
                {
                    sb.Append("<h2>Picks</h2><div class=\"widget-grid\">");
                    foreach (OSD entry in picksArray)
                    {
                        if (entry is OSDMap pickMap)
                        {
                            sb.Append("<div class=\"widget-card\"><h3>")
                              .Append(Html(pickMap["name"].AsString())).Append("</h3></div>");
                        }
                    }
                    sb.Append("</div>");
                }
            }

            // Group memberships - real WhiteCore-Dev gap (its user profile
            // page shows these, ours didn't). ListInProfile is the same
            // per-membership "show this on my profile" flag the viewer's own
            // profile floater already exposes - filtering to it here is what
            // makes this a resident's own choice rather than a full, possibly
            // unwanted membership dump.
            if (m_GroupsSearchService != null)
            {
                List<GroupMembershipData> memberships = m_GroupsSearchService.GetAgentGroupMemberships(userId.ToString(), userId.ToString());
                List<GroupMembershipData> visible = memberships.FindAll(m => m.ListInProfile);
                if (visible.Count > 0)
                {
                    sb.Append("<h2>Groups</h2><ul>");
                    foreach (GroupMembershipData membership in visible)
                    {
                        sb.Append("<li>").Append(Html(membership.GroupName)).Append("</li>");
                    }
                    sb.Append("</ul>");
                }
            }

            // Regions owned - real WhiteCore-Dev gap. Deliberately public
            // (same as everything else on this page) and reuses the exact
            // same GetRegionsOwnedBy helper /myregions already uses for the
            // logged-in owner's own private management page - this is just
            // the read-only, anyone-can-view list of region names, no OAR
            // save/load actions.
            List<GridRegion> ownedRegions = GetRegionsOwnedBy(userId);
            if (ownedRegions.Count > 0)
            {
                sb.Append("<h2>Regions</h2><ul>");
                foreach (GridRegion region in ownedRegions)
                {
                    sb.Append("<li>").Append(Html(region.RegionName)).Append("</li>");
                }
                sb.Append("</ul>");
            }

            WritePage(request, response, "Confluence Grid - " + account.Name, sb.ToString());
        }

        // Friends list - the second "genuinely new ground" item from the
        // full WhiteCore-Dev audit (WhiteCore's user/friends.html: name
        // linking to a profile, region linking to a region profile, and a
        // hop:// location link). IFriendsService already exists server-side
        // (FriendsService.dll, used by the actual in-viewer friends list)
        // but had never been wired into this connector before - this is the
        // first WebInterface feature to need it. The `Friend` field on each
        // FriendInfo is the OTHER party's principal UUID as a string (the
        // stock OpenSim Friends table schema - confirmed via
        // FriendsStore.migrations - not a display name), so each entry
        // needs its own UserAccountService/GridUserService lookups to show
        // anything human-readable.
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
                WritePage(request, response, "Confluence Grid - Friends", sb.ToString());
                return;
            }

            OpenSim.Services.Interfaces.FriendInfo[] friends = m_FriendsService.GetFriends(session.PrincipalID);
            if (friends == null || friends.Length == 0)
            {
                sb.Append("<p>You haven't added any friends yet. Use the Friends panel in your viewer to send a friend request.</p>");
                WritePage(request, response, "Confluence Grid - Friends", sb.ToString());
                return;
            }

            sb.Append("<table><tr><th>Name</th><th>Status</th></tr>");
            foreach (OpenSim.Services.Interfaces.FriendInfo friend in friends)
            {
                if (!UUID.TryParse(friend.Friend, out UUID friendId))
                    continue;

                UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, friendId);
                string name = account != null ? account.Name : friend.Friend;

                string status = "Offline";
                if (m_GridUserService != null)
                {
                    GridUserInfo info = m_GridUserService.GetGridUserInfo(friendId.ToString());
                    if (info != null && info.Online)
                        status = "Online now";
                }

                sb.Append("<tr><td><a href=\"").Append(BasePath).Append("/profile?id=").Append(friendId).Append("\">")
                  .Append(Html(name)).Append("</a></td><td>").Append(Html(status)).Append("</td></tr>");
            }
            sb.Append("</table>");

            WritePage(request, response, "Confluence Grid - Friends", sb.ToString());
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
                WritePage(request, response, "Confluence Grid - Change Password", ChangePasswordForm(null));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string currentPassword = FormValue(form, "current_password");
            string newPassword = FormValue(form, "new_password");
            string confirmPassword = FormValue(form, "confirm_password");

            if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
            {
                WritePage(request, response, "Confluence Grid - Change Password", ChangePasswordForm("All fields are required."));
                return;
            }
            if (newPassword != confirmPassword)
            {
                WritePage(request, response, "Confluence Grid - Change Password", ChangePasswordForm("New passwords do not match."));
                return;
            }
            if (newPassword.Length < 6)
            {
                WritePage(request, response, "Confluence Grid - Change Password", ChangePasswordForm("New password must be at least 6 characters."));
                return;
            }

            // Same MD5-then-Authenticate convention TryLogin uses - confirms
            // the CURRENT password before allowing a change, so a stolen
            // session cookie alone can't be used to lock the real owner out.
            string authToken = m_AuthenticationService?.Authenticate(session.PrincipalID, Util.Md5Hash(currentPassword), 30);
            if (string.IsNullOrEmpty(authToken))
            {
                WritePage(request, response, "Confluence Grid - Change Password", ChangePasswordForm("Current password is incorrect."));
                return;
            }

            // SetPassword takes the raw new password (it hashes internally) -
            // same convention HandleRegister/HandleResetPassword already use.
            if (m_AuthenticationService == null || !m_AuthenticationService.SetPassword(session.PrincipalID, newPassword))
            {
                WritePage(request, response, "Confluence Grid - Change Password", ChangePasswordForm("Could not update your password. Please try again."));
                return;
            }

            WritePage(request, response, "Confluence Grid - Change Password",
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
                WritePage(request, response, "Confluence Grid - Delete Account", DeleteAccountForm(null));
                return;
            }

            if (m_UserAccountService == null || m_AuthenticationService == null)
            {
                WritePage(request, response, "Confluence Grid - Delete Account", DeleteAccountForm("Account deletion is not available right now."));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string currentPassword = FormValue(form, "current_password");

            string authToken = m_AuthenticationService.Authenticate(session.PrincipalID, Util.Md5Hash(currentPassword), 30);
            if (string.IsNullOrEmpty(authToken))
            {
                WritePage(request, response, "Confluence Grid - Delete Account", DeleteAccountForm("Current password is incorrect."));
                return;
            }

            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, session.PrincipalID);
            if (account == null)
            {
                WritePage(request, response, "Confluence Grid - Delete Account", DeleteAccountForm("Account not found."));
                return;
            }

            string result = SoftDeleteAccount(account);
            m_log.InfoFormat("[WEB INTERFACE]: {0} ({1}) deleted their own account", account.Name, account.PrincipalID);

            string token = ReadCookie(request, SessionCookieName);
            if (!string.IsNullOrEmpty(token))
                m_sessions.TryRemove(token, out _);
            ClearSessionCookie(response);

            WritePage(request, response, "Confluence Grid - Delete Account",
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
                WritePage(request, response, "Confluence Grid - Change Email", ChangeEmailForm(account.Email, null));
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string newEmail = FormValue(form, "email").Trim();

            if (string.IsNullOrEmpty(newEmail) || !newEmail.Contains("@"))
            {
                WritePage(request, response, "Confluence Grid - Change Email", ChangeEmailForm(newEmail, "Enter a valid email address."));
                return;
            }

            account.Email = newEmail;
            if (!m_UserAccountService.StoreUserAccount(account))
            {
                WritePage(request, response, "Confluence Grid - Change Email", ChangeEmailForm(newEmail, "Could not update your email. Please try again."));
                return;
            }

            WritePage(request, response, "Confluence Grid - Change Email",
                    "<h1>Change Email</h1><p>Your email address has been updated.</p><p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>");
        }

        private static string ChangeEmailForm(string email, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";
            return "<h1>Change Email</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/change-email\">"
                    + "<label>Email address<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\" required></label><br/>"
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
                WritePage(request, response, "Confluence Grid - Partner", "<h1>Partner</h1><p>Profiles service is not available.</p>");
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

            WritePage(request, response, "Confluence Grid - Partner", sb.ToString());
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
                WritePage(request, response, "Confluence Grid - My Transactions",
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

                rows.Append("<table><tr><th>Date</th><th>L$ credited</th><th>Real amount (hundredths)</th></tr>");
                foreach (CurrencyPurchase p in purchases.Skip(start).Take(pageSize))
                {
                    rows.Append("<tr><td>").Append(Html(p.PurchaseDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>")
                        .Append("<td>").Append(p.Amount).Append("</td>")
                        .Append("<td>").Append(p.RealAmount).Append("</td></tr>");
                }
                rows.Append("</table>");
                if (purchases.Count == 0)
                    rows.Append("<p>You haven't made any purchases yet.</p>");
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

                rows.Append("<table><tr><th>Date</th><th>From</th><th>To</th><th>Amount</th><th>Description</th></tr>");
                foreach (CurrencyTransfer t in transfers.Skip(start).Take(pageSize))
                {
                    rows.Append("<tr><td>").Append(Html(t.TransferDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>")
                        .Append("<td>").Append(Html(ResolveAgentName(t.FromAgent))).Append("</td>")
                        .Append("<td>").Append(Html(ResolveAgentName(t.ToAgent))).Append("</td>")
                        .Append("<td>").Append(t.Amount).Append("</td>")
                        .Append("<td>").Append(Html(t.Description)).Append("</td></tr>");
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

            WritePage(request, response, "Confluence Grid - My Transactions", body);
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
                WritePage(request, response, "Confluence Grid - My Classifieds",
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
                sb.Append("<table><tr><th>Name</th><th></th><th></th></tr>");
                foreach (OSD entry in records)
                {
                    if (entry is not OSDMap map)
                        continue;
                    UUID adId = map["classifieduuid"].AsUUID();
                    sb.Append("<tr><td>").Append(Html(map["name"].AsString())).Append("</td>")
                      .Append("<td><a href=\"").Append(BasePath).Append("/myclassifieds?id=").Append(adId).Append("\">Edit</a></td>")
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
            for (int i = 0; i < ClassifiedCategories.Length; i++)
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

            WritePage(request, response, "Confluence Grid - My Classifieds", sb.ToString());
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
            ad.GlobalPos = "<128,128,25>";
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
                WritePage(request, response, "Confluence Grid - My Events",
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
                sb.Append("<table><tr><th>Date</th><th>Title</th><th></th><th></th></tr>");
                foreach (EventItem ev in mine)
                {
                    sb.Append("<tr><td>").Append(Html(ev.EventDate.ToString("yyyy-MM-dd HH:mm"))).Append(" UTC</td>")
                      .Append("<td>").Append(Html(ev.Title)).Append("</td>")
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
            sb.Append("<h2>").Append(formTitle).Append("</h2>")
              .Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myevents/save\">")
              .Append("<input type=\"hidden\" name=\"id\" value=\"").Append(editing != null ? editing.ID.ToString() : string.Empty).Append("\">")
              .Append("<label>Title<br/><input type=\"text\" name=\"title\" value=\"").Append(Html(editing?.Title ?? string.Empty)).Append("\" required></label><br/>")
              .Append("<label>Category<br/><input type=\"text\" name=\"category\" value=\"").Append(Html(editing?.Category ?? string.Empty)).Append("\" placeholder=\"Live Music, Nightlife, Games...\"></label><br/>")
              .Append("<label>Date/time (grid time, UTC)<br/><input type=\"datetime-local\" name=\"event_date\" value=\"").Append(Html(dateValue)).Append("\" required></label><br/>")
              .Append("<label>Duration (minutes)<br/><input type=\"number\" name=\"duration\" value=\"").Append(editing?.DurationMinutes ?? 60).Append("\" min=\"0\"></label><br/>")
              .Append("<label>Location<br/><input type=\"text\" name=\"location\" value=\"").Append(Html(editing?.Location ?? string.Empty)).Append("\" placeholder=\"Region or venue name\"></label><br/>")
              .Append("<label>Description<br/><textarea name=\"description\" rows=\"4\">").Append(Html(editing?.Description ?? string.Empty)).Append("</textarea></label><br/>")
              .Append("<button type=\"submit\">").Append(editing != null ? "Save changes" : "Add event").Append("</button>")
              .Append(editing != null ? " <a href=\"" + BasePath + "/myevents\">Cancel</a>" : string.Empty)
              .Append("</form>");

            WritePage(request, response, "Confluence Grid - My Events", sb.ToString());
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
            sb.Append("<h1>Grid Features</h1>");
            sb.Append("<p>Confluence runs on OpenSimulator, extended with a set of natively-built systems ")
              .Append("(not addon modules) covering currency, search, moderation, and grid administration.</p>");

            sb.Append("<h2>Live Grid Snapshot</h2><div class=\"stats-grid\">");
            if (m_GridService != null)
            {
                List<GridRegion> regions = m_GridService.GetRegionRange(UUID.Zero, 0, 2000000, 0, 2000000);
                long totalAreaSqm = 0;
                int hgOpenCount = 0;
                int largestRegionSqm = 0;
                foreach (GridRegion region in regions)
                {
                    int areaSqm = region.RegionSizeX * region.RegionSizeY;
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
            sb.Append("</div>");

            sb.Append("<h2>Platform Capabilities</h2><div class=\"widget-grid\">");
            AppendFeatureCard(sb, "Hypergrid Travel", "Enabled", "Teleport to and from other OpenSim grids");
            AppendFeatureCard(sb, "VarRegions", "Supported", "Regions larger than the standard 256x256, with no internal sim-crossing stutter");
            AppendFeatureCard(sb, "Native Currency", m_CurrencyService != null ? "Active" : "Unavailable", "Built-in ledger, transaction history, and web-based balance/transaction pages - not a third-party dependency");
            AppendFeatureCard(sb, "Native Search", "Active", "Grid-wide place search, integrated with the viewer's own Search window");
            AppendFeatureCard(sb, "Native Mute List", "Active", "Server-side mute list, replacing the legacy addon module");
            AppendFeatureCard(sb, "SimProtection", "Active", "Automatic script/physics throttling on FPS drops, with auto-recovery");
            AppendFeatureCard(sb, "On-Demand Regions", "Active", "Idle regions sleep until a visitor arrives, then wake automatically");
            AppendFeatureCard(sb, "Grid-Wide Viewer Ban", "Active", "IP-range and hardware-signature bans enforced at login, grid-wide");
            AppendFeatureCard(sb, "Scripted NPCs", "Active", "osNpc bots with avatar-follow and tag-group management");
            AppendFeatureCard(sb, "Abuse Reports", "Active", "In-viewer abuse reporting with a web-based admin queue");
            AppendFeatureCard(sb, "Web-Based Admin", "Active", "Full grid administration - users, estates, regions, currency, events - from any browser");
            AppendFeatureCard(sb, "Mesh & Scripting", "Supported", "Mesh uploads, LSL and OSSL scripting");
            sb.Append("</div>");

            sb.Append("<p><a href=\"").Append(BasePath).Append("/viewers\">Get a viewer to explore</a> &middot; ")
              .Append("<a href=\"").Append(BasePath).Append("/destinations\">See where to go</a></p>");

            WritePage(request, response, "Confluence Grid - Features", sb.ToString());
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
            { "groups", "Groups" }
        };

        private void HandleSearch(IOSHttpRequest request, IOSHttpResponse response)
        {
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

            sb.Append("<div class=\"subnav\"><a class=\"active\" href=\"").Append(BasePath).Append("/search\">Search</a>")
              .Append("<a href=\"").Append(BasePath).Append("/landsearch\">Land for Sale</a></div>");

            sb.Append("<div class=\"hero-search-wrap\">");
            sb.Append("<div class=\"tagline\">Search People, Places, Events, Classifieds &amp; Groups</div>");

            sb.Append("<form method=\"get\" action=\"").Append(BasePath).Append("/search\" class=\"hero-search\">");
            sb.Append("<div class=\"search-input\">").Append(Icon("search"))
              .Append("<input type=\"text\" name=\"q\" value=\"").Append(Html(query))
              .Append("\" placeholder=\"Search people, places, events, classifieds, groups\" minlength=\"3\"></div>");
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
                            sb.Append("<a class=\"chip\" href=\"").Append(BasePath).Append("/search?q=").Append(Uri.EscapeDataString(t)).Append("\">").Append(Html(t)).Append("</a>");
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

                WritePage(request, response, "Confluence Grid - Search", sb.ToString());
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
                        string meta = place.ForSale ? "For sale - " + place.SalePrice + " C$ (" + place.Area + " m²)" : place.Area + " m²";
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

            if (m_SearchService != null)
                m_SearchService.LogSearch(query, category, totalResults);

            sb.Append("<p>").Append(totalResults).Append(totalResults == 1 ? " result for &ldquo;" : " results for &ldquo;")
              .Append(Html(query)).Append("&rdquo;</p>");

            if (totalResults == 0)
                sb.Append("<p>No matches. Try a different or shorter search term.</p>");
            else
                sb.Append(resultsSb);

            WritePage(request, response, "Confluence Grid - Search: " + Html(query), sb.ToString());
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
        private const int BannedUserLevel = -1;
        private const int DeletedUserLevel = -2;

        // Timed-ban expiry, same reused-userdata-table pattern as the
        // partner proposal tags above (see PartnerIncomingTag) - one more
        // UserId+TagId slot, this time holding a Unix timestamp string
        // instead of a UUID. Zero/absent means "no expiry" (permanent ban).
        private static readonly UUID BanExpiryTag = new UUID("9b1f9b1a-0000-4a00-8000-000000000003");

        private DateTime? GetBanExpiry(UUID userId)
        {
            if (m_UserProfilesService == null)
                return null;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = BanExpiryTag.ToString() };
            string result = string.Empty;
            m_UserProfilesService.RequestUserAppData(ref data, ref result);

            return long.TryParse(data.DataVal, out long unixSeconds) && unixSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime
                    : (DateTime?)null;
        }

        private void SetBanExpiry(UUID userId, DateTime? expiry)
        {
            if (m_UserProfilesService == null)
                return;

            UserAppData data = new UserAppData { UserId = userId.ToString(), TagId = BanExpiryTag.ToString() };
            string result = string.Empty;
            m_UserProfilesService.RequestUserAppData(ref data, ref result);
            data.DataKey = "BanExpiry";
            data.DataVal = expiry.HasValue ? new DateTimeOffset(expiry.Value, TimeSpan.Zero).ToUnixTimeSeconds().ToString() : "0";
            m_UserProfilesService.SetUserAppData(data, ref result);
        }

        // Called wherever an account's UserLevel is read for a login/admin
        // decision - a temp-banned account whose timer has run out reverts
        // to Active on next check rather than needing an admin to manually
        // unban it. Returns true if it just cleared an expired ban (callers
        // that already loaded the account's old UserLevel into a local
        // should re-check after calling this).
        private bool ClearExpiredBan(UserAccount account)
        {
            if (account == null || account.UserLevel != BannedUserLevel || m_UserAccountService == null)
                return false;

            DateTime? expiry = GetBanExpiry(account.PrincipalID);
            if (expiry == null || expiry.Value > DateTime.UtcNow)
                return false;

            account.UserLevel = 0;
            m_UserAccountService.StoreUserAccount(account);
            SetBanExpiry(account.PrincipalID, null);
            return true;
        }

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
                WritePage(request, response, "Confluence Grid - Land for Sale", sb.ToString());
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
                        string meta = r.SalePrice + " C$ &middot; " + r.Area + " m&sup2;" + (r.Auction ? " &middot; Auction" : string.Empty);
                        AppendSearchResultCard(sb, "For Sale", Html(r.Name), meta, string.Empty, null);
                    }
                }
            }

            WritePage(request, response, "Confluence Grid - Land for Sale", sb.ToString());
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

            WritePage(request, response, "Confluence Grid - Support", sb.ToString());
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
                WritePage(request, response, "Confluence Grid - Support Queue", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_SupportTicketService == null)
            {
                WritePage(request, response, "Confluence Grid - Support Queue",
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

            WritePage(request, response, "Confluence Grid - Support Queue", sb.ToString());
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
        private static readonly string[] ClassifiedCategories =
        {
            "Shopping", "Land Rental", "Property Rental", "Special Attraction",
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
                sb.Append("<div class=\"widget-card\"><h3>").Append(Html(ad.Name)).Append("</h3>");
                sb.Append("<div class=\"widget-meta\">").Append(Html(category));
                if (!string.IsNullOrEmpty(ad.SimName))
                    sb.Append(" &middot; ").Append(Html(ad.SimName));
                if (ad.Price > 0)
                    sb.Append(" &middot; C$ ").Append(ad.Price);
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
            AppendStat(sb, "Last 24 Hours", "C$ " + volume24h.ToString("N0"), count24h + " transactions");
            AppendStat(sb, "Last 7 Days", "C$ " + volume7d.ToString("N0"), count7d + " transactions");
            AppendStat(sb, "Last 30 Days", "C$ " + volume30d.ToString("N0"), count30d + " transactions");
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

        private static void AppendStat(StringBuilder sb, string label, string value, string sub)
        {
            sb.Append("<div class=\"stat-card\"><div class=\"stat-label\">").Append(Html(label)).Append("</div>")
              .Append("<div class=\"stat-value\">").Append(Html(value)).Append("</div>")
              .Append("<div class=\"stat-sub\">").Append(Html(sub)).Append("</div></div>");
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

        private void HandleDashboard(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            int balance = m_CurrencyService != null ? m_CurrencyService.GetBalance(session.PrincipalID) : 0;

            // Account links used to be repeated here as a flat <p><a> list -
            // they now live in the nav-bar dropdown (see WritePage) so
            // they're reachable from every page, not just this one. This
            // page's job is just the balance/welcome landing view.
            string body = "<h1>Welcome, " + Html(session.Name) + "</h1>"
                    + "<p class=\"balance\">Balance: " + balance + "</p>"
                    + "<p>Use the menu in the top-right of any page to reach your profile, friends, "
                    + "transactions, classifieds, events, and account settings.</p>";

            WritePage(request, response, "Confluence Grid - Dashboard", body);
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
                WritePage(request, response, "Confluence Grid - Admin", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

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
                regions.Sort((a, b) => string.Compare(a.RegionName, b.RegionName, StringComparison.OrdinalIgnoreCase));

                rows.Append("<table><tr><th>Region</th><th>Location</th><th>Hypergrid</th><th></th><th></th><th></th></tr>");
                foreach (GridRegion region in regions)
                {
                    bool open = m_RegionHGService == null || m_RegionHGService.IsRegionOpen(region.RegionID);
                    string status = open ? "Open" : "Closed";
                    string actionLabel = open ? "Close to HG" : "Open to HG";

                    rows.Append("<tr><td>").Append(Html(region.RegionName)).Append("</td>");
                    rows.Append("<td>").Append(region.RegionCoordX).Append(",").Append(region.RegionCoordY).Append("</td>");
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
                    rows.Append("</form></td></tr>");
                }
                rows.Append("</table>");

                if (m_RegionHGService == null)
                    rows.Append("<p class=\"error\">RegionHGService is not configured - toggle is read-only (always shows Open).</p>");
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            // Sub-page links used to be a flat <p><a> list here - they now
            // live in the "Admin" nav-bar dropdown (see WritePage), reachable
            // from every page instead of just this one.
            string body = "<h1>Grid Administration</h1>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>"
                    + message
                    + rows.ToString();

            WritePage(request, response, "Confluence Grid - Admin", body);
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
                WritePage(request, response, "Confluence Grid - Statistics", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
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

            WritePage(request, response, "Confluence Grid - Statistics", body);
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
                WritePage(request, response, "Confluence Grid - News", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_NewsService == null)
            {
                WritePage(request, response, "Confluence Grid - News",
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

            WritePage(request, response, "Confluence Grid - News", body);
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
                WritePage(request, response, "Confluence Grid - Events", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_EventsService == null)
            {
                WritePage(request, response, "Confluence Grid - Events",
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
                    + "<label>Location<br/><input type=\"text\" name=\"location\" value=\"" + Html(editing?.Location ?? string.Empty) + "\" placeholder=\"Region or venue name\"></label><br/>"
                    + "<label>Description<br/><textarea name=\"description\" rows=\"4\">" + Html(editing?.Description ?? string.Empty) + "</textarea></label><br/>"
                    + "<button type=\"submit\">" + (editing != null ? "Save changes" : "Add event") + "</button>"
                    + (editing != null ? " <a href=\"" + BasePath + "/admin/events\">Cancel</a>" : string.Empty)
                    + "</form>";

            WritePage(request, response, "Confluence Grid - Events", body);
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
        private void HandleStaticPage(IOSHttpRequest request, IOSHttpResponse response, string slug)
        {
            StaticPage page = m_StaticPageService?.GetBySlug(slug);
            if (page == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            WritePage(request, response, page.Title, page.Body);
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
                WritePage(request, response, "Confluence Grid - Pages", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_StaticPageService == null)
            {
                WritePage(request, response, "Confluence Grid - Pages",
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
            rows.Append("<table><tr><th>Slug</th><th>Title</th><th>Updated</th><th></th><th></th><th></th></tr>");
            foreach (StaticPage page in pages)
            {
                rows.Append("<tr>");
                rows.Append("<td>").Append(Html(page.Slug)).Append("</td>");
                rows.Append("<td>").Append(Html(page.Title)).Append("</td>");
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
                    + "<button type=\"submit\">" + (editing != null ? "Save changes" : "Create") + "</button>"
                    + (editing != null ? " <a href=\"" + BasePath + "/admin/pages\">Cancel</a>" : string.Empty)
                    + "</form>";

            WritePage(request, response, "Confluence Grid - Pages", body);
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

            page.Slug = slug;
            page.Title = title;
            page.Body = bodyText;
            page.Updated = DateTime.UtcNow;

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
                WritePage(request, response, "Confluence Grid - Settings", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_GridSettingsService == null)
            {
                WritePage(request, response, "Confluence Grid - Settings",
                        "<h1>Grid Settings</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p><p>Grid settings service is not available.</p>");
                return;
            }

            string gridName = GetSetting("GridName", m_gridName);
            string gridNick = GetSetting("GridNickname", m_gridNick);
            string welcomeMessage = GetSetting("WelcomeMessage", m_welcomeMessage);
            bool allowRegistration = GetSetting("AllowRegistration", "true") == "true";
            bool announcementEnabled = GetSetting("AnnouncementEnabled", "false") == "true";
            string announcementTitle = GetSetting("AnnouncementTitle", string.Empty);
            string announcementText = GetSetting("AnnouncementText", string.Empty);
            string announcementColor = GetSetting("AnnouncementColor", "#3b82f6");

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
                    + "<p class=\"news-meta\">Shown as a banner at the top of the home page and splash screen, above everything else - matches WhiteCore-Dev's welcomescreen_manager.html \"special window\" toggle.</p>"
                    + "<label><input type=\"checkbox\" name=\"announcement_enabled\" value=\"true\"" + (announcementEnabled ? " checked" : "") + " style=\"width:auto;display:inline\"> Show announcement banner</label><br/>"
                    + "<label>Title<br/><input type=\"text\" name=\"announcement_title\" value=\"" + Html(announcementTitle) + "\"></label><br/>"
                    + "<label>Text<br/><textarea name=\"announcement_text\" rows=\"2\">" + Html(announcementText) + "</textarea></label><br/>"
                    + "<label>Color<br/><input type=\"color\" name=\"announcement_color\" value=\"" + Html(announcementColor) + "\" style=\"width:auto\"></label><br/>"
                    + "<button type=\"submit\">Save settings</button>"
                    + "</form>";

            WritePage(request, response, "Confluence Grid - Settings", body);
        }

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

            if (string.IsNullOrEmpty(gridName))
            {
                response.Redirect(BasePath + "/admin/settings?message=" + Uri.EscapeDataString("Grid name is required."), HttpStatusCode.Redirect);
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
                WritePage(request, response, "Confluence Grid - Console", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (string.IsNullOrEmpty(m_webConsoleSecret))
            {
                WritePage(request, response, "Confluence Grid - Console",
                        "<h1>Region Console</h1><p><a href=\"" + BasePath + "/admin\">Back to admin</a></p>"
                        + "<p>Web console is not configured on this grid. Set <code>[WebConsole] SharedSecret</code> "
                        + "in Robust's config, and matching <code>[WebConsole] Enabled = true</code> / "
                        + "<code>SharedSecret</code> in each region's own config, to enable this page.</p>");
                return;
            }

            if (m_GridService == null)
            {
                WritePage(request, response, "Confluence Grid - Console",
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

            WritePage(request, response, "Confluence Grid - Console", body);
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

        // Shared by HandleAdminConsoleRun (free-form console page) and the
        // dedicated Kick/Message buttons on the user detail page - both send
        // a command string to the same region-side /consoleweb endpoint over
        // the same shared secret (see WebConsoleModule.cs, task #26).
        private string RunRegionConsoleCommand(GridRegion region, string command)
        {
            try
            {
                string url = region.ServerURI.TrimEnd('/') + "/consoleweb";
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
                WritePage(request, response, "Confluence Grid - Abuse Reports", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_AbuseReportsService == null)
            {
                WritePage(request, response, "Confluence Grid - Abuse Reports",
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

            WritePage(request, response, "Confluence Grid - Abuse Reports", body);
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
                WritePage(request, response, "Confluence Grid - Transactions", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_CurrencyService == null)
            {
                WritePage(request, response, "Confluence Grid - Transactions",
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

                rows.Append("<table><tr><th>Date</th><th>From</th><th>To</th><th>Amount</th><th>Type</th><th>Description</th></tr>");
                foreach (CurrencyTransfer t in page)
                {
                    rows.Append("<tr>");
                    rows.Append("<td>").Append(Html(t.TransferDate.ToString("yyyy-MM-dd HH:mm:ss"))).Append(" UTC</td>");
                    rows.Append("<td>").Append(Html(ResolveAgentName(t.FromAgent))).Append("</td>");
                    rows.Append("<td>").Append(Html(ResolveAgentName(t.ToAgent))).Append("</td>");
                    rows.Append("<td>").Append(t.Amount).Append("</td>");
                    rows.Append("<td>").Append(t.TransferType).Append("</td>");
                    rows.Append("<td>").Append(Html(t.Description)).Append("</td>");
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

            WritePage(request, response, "Confluence Grid - Transactions", body);
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
        // remains out of scope. Still missing here: admin-editable email/name,
        // admin-set (not scrambled) password reset, and admin-side account
        // creation - real gaps, not yet built.
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
                WritePage(request, response, "Confluence Grid - User Management", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
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
                    ClearExpiredBan(account);

                    string created = DateTimeOffset.FromUnixTimeSeconds(account.Created).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                    string balance = m_CurrencyService != null
                            ? m_CurrencyService.GetBalance(account.PrincipalID).ToString()
                            : "n/a";

                    DateTime? banExpiry = account.UserLevel == BannedUserLevel ? GetBanExpiry(account.PrincipalID) : null;
                    string statusLabel = account.UserLevel == DeletedUserLevel ? "Deleted"
                            : account.UserLevel == BannedUserLevel
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

                    body = "<h1>" + Html(account.Name) + "</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/users\">Back to search</a></p>"
                            + message
                            + "<table>"
                            + "<tr><th>Principal ID</th><td>" + account.PrincipalID + "</td></tr>"
                            + "<tr><th>Email</th><td>" + Html(account.Email) + "</td></tr>"
                            + "<tr><th>Created</th><td>" + Html(created) + "</td></tr>"
                            + "<tr><th>Status</th><td>" + statusLabel + "</td></tr>"
                            + "<tr><th>User Level</th><td>" + account.UserLevel + "</td></tr>"
                            + "<tr><th>Currency balance</th><td>" + balance + "</td></tr>"
                            + "</table>"
                            + "<p><a href=\"" + BasePath + "/profile?id=" + account.PrincipalID + "\">View public profile</a></p>"
                            + "<h2>Account details</h2>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/edit-details\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<label>First name: <input type=\"text\" name=\"first_name\" value=\"" + Html(account.FirstName) + "\" required></label>"
                            + "<label>Last name: <input type=\"text\" name=\"last_name\" value=\"" + Html(account.LastName) + "\" required></label>"
                            + "<label>Email: <input type=\"email\" name=\"email\" value=\"" + Html(account.Email) + "\"></label>"
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
                            + "A timed ban auto-clears back to Active the next time this page or the login form checks that account - it does not (yet) reach the real grid/viewer login path on its own timer.</p>"
                            + "<form method=\"post\" action=\"" + BasePath + "/admin/users/set-level\">"
                            + "<input type=\"hidden\" name=\"principal_id\" value=\"" + account.PrincipalID + "\">"
                            + "<input type=\"hidden\" name=\"user_level\" value=\"" + (account.UserLevel == BannedUserLevel ? "0" : BannedUserLevel.ToString()) + "\">"
                            + (account.UserLevel == BannedUserLevel
                                ? string.Empty
                                : "<label>Ban duration (hours, blank = permanent): <input type=\"number\" name=\"ban_hours\" min=\"1\"></label> ")
                            + "<button type=\"submit\">" + (account.UserLevel == BannedUserLevel ? "Unban this user" : "Ban this user") + "</button>"
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

                if (!string.IsNullOrEmpty(query) && m_UserAccountService != null)
                {
                    List<UserAccount> results = m_UserAccountService.GetUserAccounts(UUID.Zero, query);
                    if (results.Count == 0)
                    {
                        rows.Append("<p>No accounts matched that search.</p>");
                    }
                    else
                    {
                        rows.Append("<table><tr><th>Name</th><th>Email</th><th>User Level</th></tr>");
                        foreach (UserAccount account in results)
                        {
                            rows.Append("<tr><td><a href=\"").Append(BasePath).Append("/admin/users?principal=").Append(account.PrincipalID).Append("\">")
                                    .Append(Html(account.Name)).Append("</a></td>");
                            rows.Append("<td>").Append(Html(account.Email)).Append("</td>");
                            rows.Append("<td>").Append(account.UserLevel).Append("</td></tr>");
                        }
                        rows.Append("</table>");
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
                        + "<button type=\"submit\">Create account</button>"
                        + "</form>";
            }

            WritePage(request, response, "Confluence Grid - User Management", body);
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

                if (UUID.TryParse(principalId, out UUID principalID) && int.TryParse(FormValue(form, "user_level"), out int userLevel))
                {
                    userLevel = Math.Clamp(userLevel, DeletedUserLevel, 250);
                    UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, principalID);
                    if (account != null)
                    {
                        account.UserLevel = userLevel;
                        message = m_UserAccountService.StoreUserAccount(account)
                                ? "User level updated."
                                : "Failed to update user level.";

                        if (userLevel == BannedUserLevel && int.TryParse(FormValue(form, "ban_hours"), out int banHours) && banHours > 0)
                        {
                            SetBanExpiry(principalID, DateTime.UtcNow.AddHours(banHours));
                            message = "User banned until " + DateTime.UtcNow.AddHours(banHours).ToString("yyyy-MM-dd HH:mm") + " UTC.";
                        }
                        else
                        {
                            // Any other level change (unban, permanent ban,
                            // manual level edit) clears a stale expiry so it
                            // can't resurrect a ban that was already lifted.
                            SetBanExpiry(principalID, null);
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
                                account.FirstName = firstName;
                                account.LastName = lastName;
                                account.Email = email;
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

            UserAccount account = new UserAccount(UUID.Zero, firstName, lastName, email);
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

            string token = CreateSession(target.PrincipalID, target.Name, target.UserLevel >= 200);
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
        // FEATURES_VS_MASTER.md's "Correction (2026-08-09)"). EstateSettings
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
                WritePage(request, response, "Confluence Grid - Groups Management", "<h1>Not authorized</h1><p>This page requires a grid administrator account.</p>");
                return;
            }

            if (m_GroupsSearchService == null)
            {
                WritePage(request, response, "Confluence Grid - Groups Management",
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

            WritePage(request, response, "Confluence Grid - Groups Management", body);
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
                WritePage(request, response, "Confluence Grid - Estate Management",
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
                    WritePage(request, response, "Confluence Grid - Estate Management", "<h1>Not authorized</h1><p>You don't manage this estate.</p>");
                    return;
                }
                else
                {
                    UserAccount owner = m_UserAccountService?.GetUserAccount(UUID.Zero, estate.EstateOwner);
                    string ownerName = owner != null ? owner.Name : estate.EstateOwner.ToString();

                    StringBuilder regionRows = new StringBuilder();
                    foreach (UUID regionID in m_EstateDataService.GetRegions(estateID))
                    {
                        GridRegion region = m_GridService?.GetRegionByUUID(UUID.Zero, regionID);
                        regionRows.Append("<li>").Append(Html(region != null ? region.RegionName : regionID.ToString())).Append("</li>");
                    }

                    body = "<h1>" + Html(estate.EstateName) + "</h1>"
                            + "<p><a href=\"" + BasePath + "/admin/estates\">Back to list</a></p>"
                            + message
                            + "<h2>Regions in this estate</h2>"
                            + "<ul>" + regionRows.ToString() + "</ul>"
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
                    rows.Append("<table><tr><th>Estate</th>").Append(session.IsAdmin ? "<th>Owner</th>" : "").Append("<th>Regions</th></tr>");
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
                        rows.Append("<td>").Append(regionCount).Append("</td></tr>");
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

            WritePage(request, response, "Confluence Grid - Estate Management", body);
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

            response.Redirect(BasePath + "/admin", HttpStatusCode.Redirect);
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

            response.Redirect(BasePath + "/admin?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
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

            response.Redirect(BasePath + "/admin?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #region Self-service region owner OAR backup/restore

        // Self-service, not a grid-admin action: any logged-in user sees only
        // the region(s) they themselves are the estate owner of, via
        // IEstateDataService - distinct from /web/admin's UserLevel>=200 gate.
        // OpenSim already has its own scheduled AutoBackupModule for operator-
        // side backups; this is specifically for a user to back up or restore
        // their own content on demand.
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
                foreach (GridRegion region in ownedRegions)
                {
                    rows.Append("<h2>").Append(Html(region.RegionName)).Append("</h2>");

                    rows.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myregions/oar-save\">");
                    rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                    rows.Append("<button type=\"submit\">Back up my region (save OAR)</button>");
                    rows.Append("</form>");

                    rows.Append("<form method=\"post\" enctype=\"multipart/form-data\" action=\"")
                            .Append(BasePath).Append("/myregions/oar-load\">");
                    rows.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");
                    rows.Append("<p class=\"error\">Warning: restoring an OAR REPLACES everything currently in this region. This cannot be undone.</p>");
                    rows.Append("<label><input type=\"checkbox\" name=\"confirm\" required> I understand this will replace all current content in ")
                            .Append(Html(region.RegionName)).Append("</label><br/>");
                    rows.Append("<input type=\"file\" name=\"file\" accept=\".oar\" required><br/>");
                    rows.Append("<button type=\"submit\">Restore from OAR</button>");
                    rows.Append("</form>");
                }
            }

            string message = string.Empty;
            string queryMessage = request.QueryString.Get("message");
            if (!string.IsNullOrEmpty(queryMessage))
                message = "<p>" + Html(queryMessage) + "</p>";

            string body = "<h1>My Regions</h1>"
                    + "<p><a href=\"" + BasePath + "/dashboard\">Back to dashboard</a></p>"
                    + message
                    + rows.ToString();

            WritePage(request, response, "Confluence Grid - My Regions", body);
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
                        try
                        {
                            string url = region.ServerURI + "OAR/Save/" + region.RegionHandle;
                            using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                            {
                                client.Timeout = TimeSpan.FromSeconds(10);
                                var result = client.PostAsync(url, new System.Net.Http.StringContent(string.Empty)).GetAwaiter().GetResult();
                                message = result.IsSuccessStatusCode
                                        ? "Backup queued for " + region.RegionName + "."
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

            response.Redirect(BasePath + "/myregions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        private void HandleMyRegionsOarLoad(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message;

            if (request.HttpMethod != "POST")
            {
                message = "Invalid request.";
            }
            else
            {
                Dictionary<string, string> textFields;
                byte[] fileBytes;
                ParseMultipartFormData(request, out textFields, out fileBytes);

                if (!UUID.TryParse(textFields.GetValueOrDefault("region_id", string.Empty), out UUID regionID))
                {
                    message = "No region specified.";
                }
                else if (textFields.GetValueOrDefault("confirm", string.Empty) != "on")
                {
                    message = "You must check the confirmation box to restore an OAR.";
                }
                else if (fileBytes == null || fileBytes.Length == 0)
                {
                    message = "No file was uploaded.";
                }
                else
                {
                    GridRegion region = GetOwnedRegionOrNull(session, regionID);
                    if (region == null || string.IsNullOrEmpty(region.ServerURI))
                    {
                        message = "Region not found or not owned by you.";
                    }
                    else
                    {
                        try
                        {
                            string url = region.ServerURI + "OAR/Load/" + region.RegionHandle;
                            using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                            {
                                client.Timeout = TimeSpan.FromSeconds(30);
                                var content = new System.Net.Http.ByteArrayContent(fileBytes);
                                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                                var result = client.PostAsync(url, content).GetAwaiter().GetResult();
                                message = result.IsSuccessStatusCode
                                        ? "Restore queued for " + region.RegionName + ". This will take a little while."
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

            response.Redirect(BasePath + "/myregions?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        // No multipart/form-data parser existed anywhere in this codebase
        // (confirmed before writing this - OpenSim/Framework/MultipartForm.cs
        // only builds outgoing requests). Hand-rolled: splits the raw body on
        // the boundary marker from the Content-Type header, then for each part
        // reads its Content-Disposition to get the field name and (for the
        // file part) the filename, treating everything after the blank line as
        // that field's value/content.
        private static void ParseMultipartFormData(IOSHttpRequest request, out Dictionary<string, string> textFields, out byte[] fileBytes)
        {
            textFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            fileBytes = null;

            string contentType = request.ContentType ?? string.Empty;
            int boundaryIndex = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
            if (boundaryIndex < 0)
                return;

            string boundary = contentType.Substring(boundaryIndex + "boundary=".Length).Trim().Trim('"');
            byte[] boundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);

            byte[] body;
            using (MemoryStream buffer = new MemoryStream())
            {
                request.InputStream.CopyTo(buffer);
                body = buffer.ToArray();
            }

            List<int> boundaryPositions = new List<int>();
            for (int i = 0; i <= body.Length - boundaryBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < boundaryBytes.Length; j++)
                {
                    if (body[i + j] != boundaryBytes[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    boundaryPositions.Add(i);
            }

            for (int p = 0; p < boundaryPositions.Count - 1; p++)
            {
                int partStart = boundaryPositions[p] + boundaryBytes.Length;
                int partEnd = boundaryPositions[p + 1];
                if (partEnd <= partStart)
                    continue;

                // Header/body split: first blank line ("\r\n\r\n")
                int headerEnd = IndexOfSequence(body, Encoding.ASCII.GetBytes("\r\n\r\n"), partStart, partEnd);
                if (headerEnd < 0)
                    continue;

                string headerText = Encoding.ASCII.GetString(body, partStart, headerEnd - partStart);
                int contentStart = headerEnd + 4;
                int contentEnd = partEnd - 2; // trailing "\r\n" before next boundary
                if (contentEnd < contentStart)
                    contentEnd = contentStart;

                string nameMatch = ExtractQuotedValue(headerText, "name=");
                string filenameMatch = ExtractQuotedValue(headerText, "filename=");

                if (!string.IsNullOrEmpty(filenameMatch))
                {
                    byte[] partBytes = new byte[contentEnd - contentStart];
                    Array.Copy(body, contentStart, partBytes, 0, partBytes.Length);
                    fileBytes = partBytes;
                }
                else if (!string.IsNullOrEmpty(nameMatch))
                {
                    textFields[nameMatch] = Encoding.UTF8.GetString(body, contentStart, contentEnd - contentStart);
                }
            }
        }

        private static string ExtractQuotedValue(string headerText, string key)
        {
            int idx = headerText.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            int quoteStart = headerText.IndexOf('"', idx);
            if (quoteStart < 0)
                return null;

            int quoteEnd = headerText.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0)
                return null;

            return headerText.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static int IndexOfSequence(byte[] haystack, byte[] needle, int start, int end)
        {
            for (int i = start; i <= end - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        #endregion Self-service region owner OAR backup/restore

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
                    + "<h2>Restore from inventory archive</h2>"
                    + "<p class=\"error\">Restoring an IAR adds its contents into your inventory as new folders. It does not delete anything you currently have.</p>"
                    + "<form method=\"post\" enctype=\"multipart/form-data\" action=\"" + BasePath + "/myinventory/iar-load\">"
                    + "<label>Password: <input type=\"password\" name=\"password\" required></label><br/>"
                    + "<label><input type=\"checkbox\" name=\"confirm\" required> I understand this will add the archive's contents to my inventory</label><br/>"
                    + "<input type=\"file\" name=\"file\" accept=\".iar\" required><br/>"
                    + "<button type=\"submit\">Restore from IAR</button>"
                    + "</form>";

            WritePage(request, response, "Confluence Grid - My Inventory", body);
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

        private void HandleMyInventoryIarLoad(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message;

            if (request.HttpMethod != "POST")
            {
                message = "Invalid request.";
            }
            else
            {
                Dictionary<string, string> textFields;
                byte[] fileBytes;
                ParseMultipartFormData(request, out textFields, out fileBytes);

                string password = textFields.GetValueOrDefault("password", string.Empty);
                string confirm = textFields.GetValueOrDefault("confirm", string.Empty);

                if (confirm != "on")
                {
                    message = "You must check the confirmation box to restore an inventory archive.";
                }
                else if (fileBytes == null || fileBytes.Length == 0)
                {
                    message = "No file was uploaded.";
                }
                else
                {
                    string[] nameParts = session.Name.Split(' ', 2);
                    string firstName = nameParts.Length > 0 ? nameParts[0] : session.Name;
                    string lastName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

                    string serverURI = ResolveAnyRegionServerURI(session.PrincipalID);
                    if (string.IsNullOrEmpty(serverURI))
                    {
                        message = "Could not reach a region to service this request.";
                    }
                    else
                    {
                        try
                        {
                            string url = serverURI + "IAR/Load";
                            using (System.Net.Http.HttpClient client = new System.Net.Http.HttpClient())
                            {
                                client.Timeout = TimeSpan.FromSeconds(60);
                                client.DefaultRequestHeaders.Add("X-Iar-First-Name", Uri.EscapeDataString(firstName));
                                client.DefaultRequestHeaders.Add("X-Iar-Last-Name", Uri.EscapeDataString(lastName));
                                client.DefaultRequestHeaders.Add("X-Iar-Password", Uri.EscapeDataString(password));
                                var content = new System.Net.Http.ByteArrayContent(fileBytes);
                                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                                var result = client.PostAsync(url, content).GetAwaiter().GetResult();
                                message = result.StatusCode == HttpStatusCode.Forbidden
                                        ? "Incorrect password."
                                        : result.IsSuccessStatusCode
                                                ? "Inventory restore queued. This will take a little while."
                                                : "Region responded with " + (int)result.StatusCode + ".";
                            }
                        }
                        catch (Exception e)
                        {
                            message = "Could not reach a region: " + e.Message;
                        }
                    }
                }
            }

            response.Redirect(BasePath + "/myinventory?message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #endregion Self-service inventory owner IAR backup/restore

        private void HandleLogin(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod == "POST")
            {
                Dictionary<string, string> form = ReadForm(request);
                string firstName = FormValue(form, "first_name").Trim();
                string lastName = FormValue(form, "last_name").Trim();
                string password = FormValue(form, "password");

                string error = TryLogin(firstName, lastName, password, out string token);
                if (error == null)
                {
                    SetSessionCookie(response, token);
                    response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                    return;
                }

                WritePage(request, response, "Confluence Grid - Login", LoginForm(firstName, lastName, error));
                return;
            }

            if (GetSession(request) != null)
            {
                response.Redirect(BasePath + "/dashboard", HttpStatusCode.Redirect);
                return;
            }

            WritePage(request, response, "Confluence Grid - Login", LoginForm(string.Empty, string.Empty, null));
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
                WritePage(request, response, "Confluence Grid - Sign Up",
                        "<h1>Sign Up</h1><p>New account registration is currently closed on this grid.</p>"
                        + "<p><a href=\"" + BasePath + "/login\">Back to login</a></p>");
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, "Confluence Grid - Sign Up", RegisterForm(string.Empty, string.Empty, string.Empty, null));
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
                WritePage(request, response, "Confluence Grid - Sign Up", RegisterForm(firstName, lastName, email, error));
                return;
            }

            UserAccount account = new UserAccount(UUID.Zero, firstName, lastName, email);
            if (!m_UserAccountService.StoreUserAccount(account))
            {
                WritePage(request, response, "Confluence Grid - Sign Up", RegisterForm(firstName, lastName, email, "Could not create that account. Please try again."));
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

            string loginError = TryLogin(firstName, lastName, password, out string token);
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
                WritePage(request, response, "Confluence Grid - Forgot Password", ForgotPasswordForm(null, null));
                return;
            }

            const string genericMessage = "If that email address matches an account, a password reset link has been sent to it.";

            if (!m_smtpEnabled || m_UserAccountService == null || m_AuthenticationService == null)
            {
                WritePage(request, response, "Confluence Grid - Forgot Password",
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

            WritePage(request, response, "Confluence Grid - Forgot Password", "<h1>Forgot Password</h1><p>" + Html(genericMessage) + "</p>"
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
                WritePage(request, response, "Confluence Grid - Reset Password",
                        "<h1>Reset Password</h1><p class=\"error\">This password reset link is invalid or has expired.</p>"
                        + "<p><a href=\"" + BasePath + "/forgot-password\">Request a new one</a></p>");
                return;
            }

            if (request.HttpMethod != "POST")
            {
                WritePage(request, response, "Confluence Grid - Reset Password", ResetPasswordForm(token, null));
                return;
            }

            string password = FormValue(form, "password");
            string confirmPassword = FormValue(form, "confirm_password");

            if (string.IsNullOrEmpty(password) || password.Length < 6)
            {
                WritePage(request, response, "Confluence Grid - Reset Password", ResetPasswordForm(token, "Password must be at least 6 characters."));
                return;
            }
            if (password != confirmPassword)
            {
                WritePage(request, response, "Confluence Grid - Reset Password", ResetPasswordForm(token, "Passwords do not match."));
                return;
            }

            m_resetTokens.TryRemove(token, out _);

            if (m_AuthenticationService == null || !m_AuthenticationService.SetPassword(resetToken.PrincipalID, password))
            {
                WritePage(request, response, "Confluence Grid - Reset Password",
                        "<h1>Reset Password</h1><p class=\"error\">Could not update your password. Please request a new reset link.</p>");
                return;
            }

            response.Redirect(BasePath + "/login?message=" + Uri.EscapeDataString("Password updated. Please log in."), HttpStatusCode.Redirect);
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

        private static string RegisterForm(string firstName, string lastName, string email, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            return "<h1>Sign Up</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/register\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\" required></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\" required></label><br/>"
                    + "<label>Email (optional)<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\"></label><br/>"
                    + "<label>Password<br/><input type=\"password\" name=\"password\" required></label><br/>"
                    + "<label>Confirm password<br/><input type=\"password\" name=\"confirm_password\" required></label><br/>"
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
            response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
        }

        // Resolves the account, then authenticates via IAuthenticationService the
        // same way any other OpenSim login path does - not a bespoke check against
        // the password hash directly. IAuthenticationService.Authenticate expects
        // an MD5 digest, not the raw plaintext password - real viewers hash it
        // client-side before it's ever sent over the wire (see LLLoginService's
        // own handling: a leading "$1$" means already-hashed, otherwise it MD5s
        // the input itself). A web form only ever has the raw plaintext, so this
        // must do the same hashing step LLLoginService does for that case.
        private string TryLogin(string firstName, string lastName, string password, out string token)
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
            // banned/deleted account (see BannedUserLevel/DeletedUserLevel)
            // can't still use self-service pages while locked out in-world.
            // ClearExpiredBan here is what actually lifts a timed ban once
            // its timer runs out - see that method's comment for the real
            // limitation (this only clears it in the DB on next check, it
            // doesn't reach into LLLoginService's own separate viewer-login
            // check on a timer of its own).
            ClearExpiredBan(account);
            if (account.UserLevel < 0)
                return "This account has been suspended. Contact a grid administrator.";

            string hashedPassword = Util.Md5Hash(password);
            string authToken = m_AuthenticationService.Authenticate(account.PrincipalID, hashedPassword, 30);
            if (string.IsNullOrEmpty(authToken))
                return "Invalid login.";

            token = CreateSession(account.PrincipalID, account.FirstName + " " + account.LastName, account.UserLevel >= 200);
            return null;
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
                    + "<label>Email<br/><input type=\"email\" name=\"email\" value=\"" + Html(email) + "\" required></label><br/>"
                    + "<button type=\"submit\">Send reset link</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/login\">Back to login</a></p>";
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

        private static string LoginForm(string firstName, string lastName, string error)
        {
            string errorHtml = string.IsNullOrEmpty(error) ? string.Empty : "<p class=\"error\">" + Html(error) + "</p>";

            return "<h1>Confluence Grid Login</h1>"
                    + errorHtml
                    + "<form method=\"post\" action=\"" + BasePath + "/login\">"
                    + "<label>First name<br/><input type=\"text\" name=\"first_name\" value=\"" + Html(firstName) + "\"></label><br/>"
                    + "<label>Last name<br/><input type=\"text\" name=\"last_name\" value=\"" + Html(lastName) + "\"></label><br/>"
                    + "<label>Password<br/><input type=\"password\" name=\"password\"></label><br/>"
                    + "<button type=\"submit\">Log in</button>"
                    + "</form>"
                    + "<p><a href=\"" + BasePath + "/register\">Sign up for a new account</a></p>"
                    + "<p><a href=\"" + BasePath + "/forgot-password\">Forgot your password?</a></p>";
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

            string navActions;
            if (session != null)
            {
                // Account links used to live entirely on a dedicated /dashboard
                // page (nothing but a list of <a> tags); moved into this
                // dropdown so they're reachable straight from the menu bar on
                // every page instead of needing a click-through to a page
                // whose only content was more links.
                navActions = "<div class=\"nav-dropdown\">"
                        + "<a href=\"" + BasePath + "/dashboard\" class=\"nav-user dropdown-toggle\">" + Html(session.Name) + " &#9662;</a>"
                        + "<div class=\"dropdown-menu\">"
                        + "<a href=\"" + BasePath + "/dashboard\">Dashboard</a>"
                        + "<a href=\"" + BasePath + "/profile?id=" + session.PrincipalID + "\">My Profile</a>"
                        + "<a href=\"" + BasePath + "/friends\">My Friends</a>"
                        + "<a href=\"" + BasePath + "/partner\">Partner</a>"
                        + "<a href=\"" + BasePath + "/transactions\">My Transactions</a>"
                        + "<a href=\"" + BasePath + "/myclassifieds\">My Classifieds</a>"
                        + "<a href=\"" + BasePath + "/myevents\">My Events</a>"
                        + "<a href=\"" + BasePath + "/myregions\">My Regions</a>"
                        + "<a href=\"" + BasePath + "/myestates\">My Estate</a>"
                        + "<a href=\"" + BasePath + "/myinventory\">My Inventory</a>"
                        + "<a href=\"" + BasePath + "/change-password\">Change Password</a>"
                        + "<a href=\"" + BasePath + "/change-email\">Change Email</a>"
                        + "<a href=\"" + BasePath + "/delete-account\">Delete Account</a>"
                        + "<a href=\"" + BasePath + "/logout\">Log Out</a>"
                        + "</div></div>";

                // Admin sub-pages used to be a flat <p><a> list on the /admin
                // page itself (same pattern as the old Dashboard). This
                // dropdown is only added to navActions server-side when
                // session.IsAdmin is true - i.e. exactly the residents who
                // would get a real 200 from every link inside it, rather
                // than a link shown to everyone that 403s for non-admins.
                if (session.IsAdmin)
                {
                    navActions += "<div class=\"nav-dropdown\">"
                            + "<a href=\"" + BasePath + "/admin\" class=\"dropdown-toggle\">Admin &#9662;</a>"
                            + "<div class=\"dropdown-menu\">"
                            + "<a href=\"" + BasePath + "/admin\">Grid Overview</a>"
                            + "<a href=\"" + BasePath + "/admin/abuse-reports\">Abuse Reports</a>"
                            + "<a href=\"" + BasePath + "/admin/users\">User Management</a>"
                            + "<a href=\"" + BasePath + "/admin/estates\">Estate Management</a>"
                            + "<a href=\"" + BasePath + "/admin/groups\">Groups Management</a>"
                            + "<a href=\"" + BasePath + "/admin/transactions\">Purchases &amp; Transactions</a>"
                            + "<a href=\"" + BasePath + "/admin/stats\">Grid Statistics</a>"
                            + "<a href=\"" + BasePath + "/admin/news\">News Feed</a>"
                            + "<a href=\"" + BasePath + "/admin/events\">Events</a>"
                            + "<a href=\"" + BasePath + "/admin/support\">Support Queue</a>"
                            + "<a href=\"" + BasePath + "/admin/pages\">Static Pages</a>"
                            + "<a href=\"" + BasePath + "/admin/settings\">Grid Settings</a>"
                            + "<a href=\"" + BasePath + "/admin/console\">Region Console</a>"
                            + "</div></div>";
                }
            }
            else
            {
                navActions = "<a href=\"" + BasePath + "/login\">Log In</a>"
                        + "<a href=\"" + BasePath + "/register\" class=\"nav-cta\">Sign Up</a>";
            }

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

            string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + Html(title) + "</title>"
                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                    + "<style>" + PageCss + "</style></head><body>"
                    + "<header class=\"site-header\"><div class=\"site-header-inner\">"
                    + "<a class=\"brand\" href=\"/\"><span class=\"brand-mark\">C</span>" + Html(gridName) + "</a>"
                    + "<nav class=\"site-nav\"><a href=\"/\">Home</a><a href=\"" + BasePath + "/search\">Search</a>" +
                    "<a href=\"" + BasePath + "/destinations\">Destinations</a>" +
                    "<a href=\"" + BasePath + "/features\">Features</a><a href=\"" + BasePath + "/viewers\">Get a Viewer</a>" +
                    "<a href=\"" + BasePath + "/page/about\">About</a><a href=\"" + BasePath + "/support\">Support</a></nav>"
                    + "<div class=\"site-actions\">" + navActions + "</div>"
                    + "</div></header>"
                    + "<section class=\"hero\"><div class=\"hero-inner\"><h1>" + heroTitle + "</h1></div></section>"
                    + "<main class=\"site-main\"><div class=\"page\"><div class=\"card\">" + remainder + "</div></div></main>"
                    + "<footer class=\"site-footer\"><div class=\"site-footer-inner\">"
                    + "&copy; " + DateTime.UtcNow.Year + " " + Html(gridName) + " &middot; Powered by Confluence"
                    + " &middot; <a href=\"" + BasePath + "/page/tos\">Terms of Service</a>"
                    + " &middot; <a href=\"" + BasePath + "/page/dmca\">DMCA Policy</a>"
                    + "</div></footer>"
                    + DropdownScript
                    + "</body></html>";

            response.ContentType = "text/html";
            response.RawBuffer = Encoding.UTF8.GetBytes(html);
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
                ".site-header-inner{max-width:1100px;margin:0 auto;padding:14px 0;display:flex;" +
                "align-items:center;gap:28px;flex-wrap:wrap;}" +
                ".brand{display:flex;align-items:center;gap:10px;color:#fff;text-decoration:none;" +
                "font-weight:700;font-size:17px;letter-spacing:.2px;}" +
                ".brand:hover{text-decoration:none;color:var(--accent-bright);}" +
                ".brand-mark{width:30px;height:30px;border-radius:8px;flex-shrink:0;background:var(--accent);" +
                "display:flex;align-items:center;justify-content:center;color:#fff;font-weight:700;font-size:15px;}" +
                ".site-nav{display:flex;gap:20px;flex:1;}" +
                ".site-nav a{color:var(--muted);font-size:14px;font-weight:600;}" +
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
                ".hero{background:linear-gradient(135deg,#000000 0%,#0d1a30 100%);" +
                "border-bottom:1px solid var(--border);padding:36px 24px;}" +
                ".hero-inner{max-width:1100px;margin:0 auto;}" +
                ".hero h1{font-size:30px;margin:0;color:#fff;}" +
                ".site-main{padding:0 24px;flex:1 0 auto;}" +
                ".page{max-width:1100px;margin:0 auto;padding:32px 0 60px;}" +
                ".card{background:var(--card-bg);border:1px solid var(--border);border-radius:var(--radius);" +
                "box-shadow:0 8px 24px rgba(0,0,0,.35);padding:32px 36px;}" +
                ".site-footer{background:var(--dark);border-top:1px solid var(--border);padding:20px 24px;" +
                "margin-top:40px;}" +
                ".site-footer-inner{max-width:1100px;margin:0 auto;color:var(--muted);font-size:12.5px;}" +
                "h1{font-size:21px;margin:0 0 14px;color:var(--text);}" +
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
                "td button{padding:6px 16px;font-size:11px;margin-top:0;background:transparent;color:var(--danger);" +
                "border-color:var(--border);}" +
                "td button:hover{background:var(--danger-bg);border-color:var(--danger);}" +
                ".balance{display:inline-block;font-size:1.25em;font-weight:700;color:var(--accent-bright);" +
                "background:var(--accent-tint);padding:8px 18px;border-radius:999px;margin-bottom:6px;}" +
                ".error{background:var(--danger-bg);color:var(--danger);border-left:3px solid var(--danger);" +
                "padding:12px 14px;border-radius:6px;font-size:13.5px;margin:0 0 16px;}" +
                ".announcement{background:var(--input-bg);border-left:4px solid var(--accent);" +
                "padding:14px 16px;border-radius:6px;font-size:14px;margin:0 0 18px;}" +
                ".news-item{padding:16px 0;border-top:1px solid var(--border);}" +
                ".news-item:first-of-type{border-top:none;padding-top:0;}" +
                ".news-item h3{margin-bottom:4px;}" +
                ".news-meta{color:var(--muted);font-size:12px;margin:0 0 8px;}" +
                ".stats-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin:0 0 8px;}" +
                ".stat-card{background:var(--input-bg);border:1px solid var(--border);border-radius:8px;padding:14px 16px;}" +
                ".stat-label{color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.4px;margin:0 0 6px;}" +
                ".stat-value{color:var(--accent-bright);font-size:1.3em;font-weight:700;}" +
                ".stat-sub{color:var(--muted);font-size:12px;margin-top:2px;}" +
                ".widget-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:14px;margin:0 0 8px;}" +
                ".widget-card{background:var(--input-bg);border:1px solid var(--border);border-radius:8px;padding:14px 16px;}" +
                ".widget-card h3{margin:0 0 4px;}" +
                ".widget-meta{color:var(--muted);font-size:12px;margin:0 0 6px;}" +
                ".world-map{position:relative;width:100%;padding-top:66%;background:var(--input-bg);" +
                "border:1px solid var(--border);border-radius:8px;margin:0 0 20px;overflow:hidden;}" +
                ".world-map-region{position:absolute;background-color:#1c2430;background-size:cover;" +
                "background-position:center;border:1px solid var(--accent);border-radius:3px;" +
                "display:flex;align-items:flex-end;text-decoration:none;transition:transform .15s ease,z-index 0s;}" +
                ".world-map-region:hover{transform:scale(1.08);z-index:2;text-decoration:none;" +
                "box-shadow:0 6px 18px rgba(0,0,0,.5);}" +
                ".world-map-label{background:rgba(0,0,0,.72);color:#fff;font-size:11px;padding:3px 6px;" +
                "width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}" +
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
                ".bucket-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:14px;}" +
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
                ".site-footer{padding-left:16px;padding-right:16px;}}";

        // The nav dropdowns (account/admin) open on :hover for desktop mice,
        // but touch devices have no hover state - tapping a real <a href>
        // toggle just follows the link instead of revealing the menu. This
        // makes the same toggle also open/close on tap/click, universally,
        // without disturbing the hover behavior desktop already has.
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

        private static string Html(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        #endregion Rendering
    }
}
