using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Concierge;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Server.Handlers.WebInterface
{
    // Concierge (region greeter) settings in the web portal: an estate owner
    // edits their own region's welcome text, rules and switches; a grid
    // administrator edits the defaults every region falls back to. Everything
    // is stored in the grid settings table (see ConciergeSettingKeys) and read
    // by each region through ConciergeServiceConnector, so an edit reaches the
    // region within about a minute with no restart.
    public partial class WebInterfaceServiceConnector
    {
        private const int ConciergeTextMax = 4000;
        private const int ConciergeVisitorDays = 7;
        private const int ConciergeVisitorMax = 50;
        private static readonly TimeSpan ConciergeVisitorCacheTtl = TimeSpan.FromSeconds(60);

        private readonly Dictionary<UUID, (DateTime At, List<GridUserInfo> Visitors)> m_conciergeVisitorCache =
            new Dictionary<UUID, (DateTime, List<GridUserInfo>)>();

        // The region an owner may edit; a grid administrator may edit any.
        private GridRegion GetConciergeRegion(WebSession session, UUID regionID)
        {
            GridRegion owned = GetOwnedRegionOrNull(session, regionID);
            if (owned != null)
                return owned;

            return session.IsAdmin ? m_GridService?.GetRegionByUUID(UUID.Zero, regionID) : null;
        }

        private static string CleanConciergeText(string text)
        {
            text = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return text.Length > ConciergeTextMax ? text.Substring(0, ConciergeTextMax) : text;
        }

        private string ConciergeSetting(string kind, string scope)
        {
            return m_GridSettingsService.Get(ConciergeSettingKeys.Key(kind, scope)) ?? string.Empty;
        }

        // A three-way switch: blank means "not set here - use the next level
        // down" (for a region the grid default, for the grid default the
        // module's own ini setting).
        private static string ConciergeFlagSelect(string name, string current, string inheritLabel)
        {
            current = ConciergeSettingKeys.NormalizeFlag(current);

            var sb = new StringBuilder();
            sb.Append("<select name=\"").Append(name).Append("\">");
            sb.Append("<option value=\"\"").Append(current.Length == 0 ? " selected" : "").Append(">").Append(Html(inheritLabel)).Append("</option>");
            sb.Append("<option value=\"true\"").Append(current == "true" ? " selected" : "").Append(">On</option>");
            sb.Append("<option value=\"false\"").Append(current == "false" ? " selected" : "").Append(">Off</option>");
            sb.Append("</select>");
            return sb.ToString();
        }

        private static string ConciergeFlagWord(string flag)
        {
            switch (ConciergeSettingKeys.NormalizeFlag(flag))
            {
                case "true": return "on";
                case "false": return "off";
                default: return "the module's ini setting";
            }
        }

        private const string ConciergeTextHelp =
            "<p class=\"news-meta\">One chat line per line. Fill in details with <code>{displayname}</code>, <code>{name}</code>, " +
            "<code>{firstname}</code>, <code>{region}</code>, <code>{count}</code> (people here), <code>{grid}</code>, " +
            "<code>{estate}</code>, <code>{owner}</code> and <code>{concierge}</code>. Show a different text to some visitors by " +
            "starting a section on its own line: <code>[new]</code> (a new resident), <code>[trial]</code> (a Trial Member), " +
            "<code>[hg]</code> (a visitor from another grid); everything else, or text before any section, is the " +
            "<code>[default]</code>.</p>";

        #region My Regions: per-region editor

        private void HandleMyRegionsConcierge(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.Redirect(BasePath + "/login", HttpStatusCode.Redirect);
                return;
            }

            if (m_GridSettingsService == null)
            {
                response.Redirect(BasePath + "/myregions?message=" + Uri.EscapeDataString("Grid settings are not available, so the concierge can't be edited."), HttpStatusCode.Redirect);
                return;
            }

            GridRegion region = null;
            if (UUID.TryParse(request.QueryString.Get("region") ?? string.Empty, out UUID regionID))
                region = GetConciergeRegion(session, regionID);

            if (region == null)
            {
                response.Redirect(BasePath + "/myregions?message=" + Uri.EscapeDataString("Region not found or not owned by you."), HttpStatusCode.Redirect);
                return;
            }

            string scope = region.RegionID.ToString();
            string message = request.QueryString.Get("message");
            string defaultWelcome = ConciergeSetting(ConciergeSettingKeys.KindWelcome, ConciergeSettingKeys.Default);
            string defaultRules = ConciergeSetting(ConciergeSettingKeys.KindRules, ConciergeSettingKeys.Default);

            var sb = new StringBuilder();
            sb.Append("<h1><i class=\"bi bi-chat-dots\"></i> Concierge: ").Append(Html(region.RegionName)).Append("</h1>");
            sb.Append("<p><a href=\"").Append(BasePath).Append("/myregions\">Back to My Regions</a></p>");
            if (!string.IsNullOrEmpty(message))
                sb.Append("<p>").Append(Html(message)).Append("</p>");

            sb.Append("<p>The concierge greets people as they arrive in this region, can announce arrivals and departures in local chat, ")
              .Append("and answers a few chat commands. Changes here reach the region within about a minute - no restart needed.</p>");

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/myregions/concierge/save\">");
            sb.Append("<input type=\"hidden\" name=\"region_id\" value=\"").Append(region.RegionID).Append("\">");

            sb.Append("<h2>Welcome message</h2>").Append(ConciergeTextHelp);
            sb.Append("<textarea name=\"welcome\" rows=\"8\" maxlength=\"").Append(ConciergeTextMax).Append("\" placeholder=\"Leave empty to use the grid-wide welcome\">")
              .Append(Html(ConciergeSetting(ConciergeSettingKeys.KindWelcome, scope))).Append("</textarea>");
            if (defaultWelcome.Length > 0)
                sb.Append("<details><summary>Grid-wide welcome used when this is empty</summary><pre>").Append(Html(defaultWelcome)).Append("</pre></details>");

            sb.Append("<h2>Region rules</h2>")
              .Append("<p class=\"news-meta\">Shown privately to anyone who types the <code>rules</code> chat command. Same fill-in details and sections as the welcome.</p>");
            sb.Append("<textarea name=\"rules\" rows=\"6\" maxlength=\"").Append(ConciergeTextMax).Append("\" placeholder=\"Leave empty to use the grid-wide rules\">")
              .Append(Html(ConciergeSetting(ConciergeSettingKeys.KindRules, scope))).Append("</textarea>");
            if (defaultRules.Length > 0)
                sb.Append("<details><summary>Grid-wide rules used when this is empty</summary><pre>").Append(Html(defaultRules)).Append("</pre></details>");

            sb.Append("<h2>Switches</h2><table>");
            AppendConciergeSwitchRow(sb, "Concierge active in this region", "enabled", scope);
            AppendConciergeSwitchRow(sb, "Announce arrivals and departures in local chat", "announce", scope);
            AppendConciergeSwitchRow(sb, "Tell estate owner and managers when a new resident, Trial Member or visitor arrives", "notify", scope);
            sb.Append("</table>");

            sb.Append("<p><button type=\"submit\">Save</button></p></form>");

            sb.Append("<h2>Chat commands</h2><p class=\"news-meta\">Anyone here can type <code>/4242 help</code> (or another channel, if your grid changed it): ")
              .Append("<code>who</code>, <code>info</code>, <code>rules</code>, <code>welcome</code> and <code>staff</code>. Replies are private.</p>");

            AppendConciergeVisitors(sb, region.RegionID);

            WritePage(request, response, PageTitle("Concierge: " + region.RegionName), sb.ToString());
        }

        private void AppendConciergeSwitchRow(StringBuilder sb, string label, string kind, string scope)
        {
            string gridDefault = ConciergeSetting(kind, ConciergeSettingKeys.Default);
            string inherit = "Grid default (" + ConciergeFlagWord(gridDefault) + ")";

            sb.Append("<tr><td>").Append(Html(label)).Append("</td><td>")
              .Append(ConciergeFlagSelect(kind, ConciergeSetting(kind, scope), inherit))
              .Append("</td></tr>");
        }

        private void HandleMyRegionsConciergeSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            string message = "Region not found or not owned by you.";
            string redirect = BasePath + "/myregions";

            if (request.HttpMethod == "POST" && m_GridSettingsService != null)
            {
                Dictionary<string, string> form = ReadForm(request);
                if (UUID.TryParse(FormValue(form, "region_id"), out UUID regionID) && GetConciergeRegion(session, regionID) != null)
                {
                    string scope = regionID.ToString();
                    m_GridSettingsService.Set(ConciergeSettingKeys.Key(ConciergeSettingKeys.KindWelcome, scope), CleanConciergeText(FormValue(form, "welcome")));
                    m_GridSettingsService.Set(ConciergeSettingKeys.Key(ConciergeSettingKeys.KindRules, scope), CleanConciergeText(FormValue(form, "rules")));
                    foreach (string kind in new[] { ConciergeSettingKeys.KindEnabled, ConciergeSettingKeys.KindAnnounce, ConciergeSettingKeys.KindNotify })
                        m_GridSettingsService.Set(ConciergeSettingKeys.Key(kind, scope), ConciergeSettingKeys.NormalizeFlag(FormValue(form, kind)));

                    message = "Concierge settings saved. The region picks them up within about a minute.";
                    redirect = BasePath + "/myregions/concierge?region=" + regionID;
                }
            }

            response.Redirect(redirect + (redirect.Contains('?') ? "&" : "?") + "message=" + Uri.EscapeDataString(message), HttpStatusCode.Redirect);
        }

        #endregion

        #region Recent visitors

        private List<GridUserInfo> GetConciergeVisitors(UUID regionID)
        {
            IRegionVisitorQuery query = m_GridUserService as IRegionVisitorQuery;
            if (query == null)
                return null;

            lock (m_conciergeVisitorCache)
            {
                if (m_conciergeVisitorCache.TryGetValue(regionID, out var cached) && DateTime.UtcNow - cached.At < ConciergeVisitorCacheTtl)
                    return cached.Visitors;
            }

            List<GridUserInfo> visitors = query.GetRecentVisitors(regionID, ConciergeVisitorDays, ConciergeVisitorMax);

            lock (m_conciergeVisitorCache)
            {
                if (m_conciergeVisitorCache.Count > 256)
                    m_conciergeVisitorCache.Clear();

                m_conciergeVisitorCache[regionID] = (DateTime.UtcNow, visitors);
            }

            return visitors;
        }

        private string ConciergeVisitorName(GridUserInfo info)
        {
            string id = info.UserID ?? string.Empty;

            // A visitor from another grid is stored as "uuid;home-uri;First Last".
            if (id.Length > 36 && Util.ParseUniversalUserIdentifier(id, out UUID _, out string url, out string first, out string last))
            {
                string host = string.Empty;
                try { host = new Uri(url).Host; } catch (UriFormatException) { }
                string name = (first + " " + last).Trim();
                return host.Length > 0 ? name + " @ " + host : name;
            }

            if (UUID.TryParse(id.Length >= 36 ? id.Substring(0, 36) : id, out UUID uuid))
            {
                UserAccount account = m_UserAccountService?.GetUserAccount(UUID.Zero, uuid);
                if (account != null)
                    return account.Name;
            }

            return id;
        }

        private void AppendConciergeVisitors(StringBuilder sb, UUID regionID)
        {
            sb.Append("<h2>Recent visitors</h2>");

            List<GridUserInfo> visitors = GetConciergeVisitors(regionID);
            if (visitors == null)
            {
                sb.Append("<p class=\"news-meta\">Visitor history isn't available on this grid.</p>");
                return;
            }

            sb.Append("<p class=\"news-meta\">People last seen in this region in the past ").Append(ConciergeVisitorDays)
              .Append(" days, most recent first (someone who has since moved on to another region no longer appears). Only you and grid administrators can see this.</p>");

            if (visitors.Count == 0)
            {
                sb.Append("<p>Nobody has been recorded here recently.</p>");
                return;
            }

            sb.Append("<table><tr><th>Name</th><th>Last login (UTC)</th><th>Now</th></tr>");
            foreach (GridUserInfo info in visitors)
            {
                sb.Append("<tr><td>").Append(Html(ConciergeVisitorName(info))).Append("</td><td>")
                  .Append(info.Login.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("</td><td>")
                  .Append(info.Online ? "here or online" : "offline").Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        #endregion

        #region Admin: grid-wide defaults

        private void HandleAdminSettingsConcierge(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!RequireAdminSettingsSession(request, response, "Concierge"))
                return;

            string scope = ConciergeSettingKeys.Default;

            var sb = new StringBuilder();
            sb.Append("<h1>Concierge defaults</h1>")
              .Append("<p><a href=\"").Append(BasePath).Append("/admin/settings\">Back to settings</a></p>")
              .Append(SettingsMessageBanner(request))
              .Append("<p>What every region uses until its estate owner sets their own in My Regions. The module itself is turned on ")
              .Append("in each region's configuration (<code>[Concierge] enabled = true</code>).</p>");

            sb.Append("<form method=\"post\" action=\"").Append(BasePath).Append("/admin/settings/concierge/save\">");

            sb.Append("<h2>Welcome message</h2>").Append(ConciergeTextHelp);
            sb.Append("<textarea name=\"welcome\" rows=\"8\" maxlength=\"").Append(ConciergeTextMax).Append("\">")
              .Append(Html(ConciergeSetting(ConciergeSettingKeys.KindWelcome, scope))).Append("</textarea>");

            sb.Append("<h2>Region rules</h2><textarea name=\"rules\" rows=\"6\" maxlength=\"").Append(ConciergeTextMax).Append("\">")
              .Append(Html(ConciergeSetting(ConciergeSettingKeys.KindRules, scope))).Append("</textarea>");

            sb.Append("<h2>Switches</h2><table>");
            foreach ((string kind, string label) in new[]
            {
                (ConciergeSettingKeys.KindEnabled, "Concierge active"),
                (ConciergeSettingKeys.KindAnnounce, "Announce arrivals and departures in local chat"),
                (ConciergeSettingKeys.KindNotify, "Tell estate owner and managers when a new resident, Trial Member or visitor arrives")
            })
            {
                sb.Append("<tr><td>").Append(Html(label)).Append("</td><td>")
                  .Append(ConciergeFlagSelect(kind, ConciergeSetting(kind, scope), "Module ini setting"))
                  .Append("</td></tr>");
            }
            sb.Append("</table><p><button type=\"submit\">Save</button></p></form>");

            WritePage(request, response, PageTitle("Concierge"), sb.ToString());
        }

        private void HandleAdminSettingsConciergeSave(IOSHttpRequest request, IOSHttpResponse response)
        {
            WebSession session = GetSession(request);
            if (session == null || !session.IsAdmin || m_GridSettingsService == null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            Dictionary<string, string> form = ReadForm(request);
            string scope = ConciergeSettingKeys.Default;

            m_GridSettingsService.Set(ConciergeSettingKeys.Key(ConciergeSettingKeys.KindWelcome, scope), CleanConciergeText(FormValue(form, "welcome")));
            m_GridSettingsService.Set(ConciergeSettingKeys.Key(ConciergeSettingKeys.KindRules, scope), CleanConciergeText(FormValue(form, "rules")));
            foreach (string kind in new[] { ConciergeSettingKeys.KindEnabled, ConciergeSettingKeys.KindAnnounce, ConciergeSettingKeys.KindNotify })
                m_GridSettingsService.Set(ConciergeSettingKeys.Key(kind, scope), ConciergeSettingKeys.NormalizeFlag(FormValue(form, kind)));

            response.Redirect(BasePath + "/admin/settings/concierge?message=" + Uri.EscapeDataString("Settings saved."), HttpStatusCode.Redirect);
        }

        #endregion
    }
}
