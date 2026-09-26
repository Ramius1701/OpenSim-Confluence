/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    /// <summary>
    /// A region greeter. On the regions it is enabled for it welcomes each
    /// arriving avatar with a message picked for who they are (a returning
    /// resident, a brand-new resident, a Trial Member, a Hypergrid visitor),
    /// announces arrivals and departures in local chat, answers a few chat
    /// commands, and can tell estate managers when someone notable arrives.
    ///
    /// The texts and the per-region switches are edited by estate owners in
    /// the web portal and read from Robust (see ConciergeContentClient), so
    /// changing them needs no restart. Everything can also be set in the
    /// [Concierge] ini section; see OpenSimDefaults.ini.
    ///
    /// Not part of this module unless explicitly turned on: replacing the
    /// standard chat module, which the original OpenSim module could do to
    /// make say/shout region-wide (the [Chat] enabled = false mode).
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ConciergeModule")]
    public partial class ConciergeModule : ChatModule, ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly HttpClient s_brokerHttp = new HttpClient();

        // The well-known default the shipped ini used to carry; refusing it
        // stops an operator who just flips "enabled" from exposing a
        // password everyone can read in the repository.
        private const string InsecureDefaultPassword = "SECRET";

        private new List<IScene> m_scenes = new List<IScene>();
        private List<IScene> m_conciergedScenes = new List<IScene>();
        private readonly Dictionary<UUID, HashSet<UUID>> m_present = new Dictionary<UUID, HashSet<UUID>>();

        private bool m_replacingChatModule = false;

        private string m_whoami = "conferencier";
        private string m_gridName = string.Empty;
        private Regex m_regions = null;
        private string m_welcomes = null;
        private int m_conciergeChannel = 4242;
        private string m_announceEntering = "{displayname} enters {region} (now {people} in this region)";
        private string m_announceLeaving = "{displayname} leaves {region} (back to {people} in this region)";
        private bool m_announceArrivals = true;
        private int m_newResidentDays = 7;
        private string m_xmlRpcPassword = String.Empty;
        private string m_brokerURI = String.Empty;
        private int m_brokerUpdateTimeout = 300;
        private ConciergeContentClient m_content = new ConciergeContentClient(string.Empty, 60);

        internal new object m_syncy = new object();

        internal new bool m_enabled = false;

        #region ISharedRegionModule Members
        public override void Initialise(IConfigSource configSource)
        {
            IConfig config = configSource.Configs["Concierge"];

            if (config == null)
                return;

            if (!config.GetBoolean("enabled", false))
                return;

            m_enabled = true;

            // check whether ChatModule has been disabled: if yes,
            // then we'll "stand in"
            try
            {
                if (configSource.Configs["Chat"] == null)
                {
                    // if Chat module has not been configured it's
                    // enabled by default, so we are not going to
                    // replace it.
                    m_replacingChatModule = false;
                }
                else
                {
                    m_replacingChatModule  = !configSource.Configs["Chat"].GetBoolean("enabled", true);
                }
            }
            catch (Exception)
            {
                m_replacingChatModule = false;
            }

            m_log.InfoFormat("[Concierge] {0} ChatModule", m_replacingChatModule ? "replacing" : "not replacing");

            m_conciergeChannel = config.GetInt("concierge_channel", m_conciergeChannel);
            m_whoami = config.GetString("whoami", "conferencier");
            m_welcomes = config.GetString("welcomes", m_welcomes);
            m_announceEntering = config.GetString("announce_entering", m_announceEntering);
            m_announceLeaving = config.GetString("announce_leaving", m_announceLeaving);
            m_announceArrivals = config.GetBoolean("announce_arrivals", m_announceArrivals);
            m_newResidentDays = config.GetInt("new_resident_days", m_newResidentDays);
            m_xmlRpcPassword = config.GetString("password", m_xmlRpcPassword);
            m_brokerURI = config.GetString("broker", m_brokerURI);
            m_brokerUpdateTimeout = config.GetInt("broker_timeout", m_brokerUpdateTimeout);

            m_gridName = config.GetString("grid_name", string.Empty);
            if (string.IsNullOrWhiteSpace(m_gridName))
                m_gridName = configSource.Configs["GridInfo"]?.GetString("gridname", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(m_gridName))
                m_gridName = configSource.Configs["Const"]?.GetString("BaseHostname", string.Empty) ?? string.Empty;

            ParseNotifySettings(config);

            // Where Robust serves the per-region texts and switches that
            // estate owners set in the web portal: the same private service
            // address the region already uses to reach Robust.
            string serverUri = config.GetString("ServerURI", string.Empty);
            if (string.IsNullOrWhiteSpace(serverUri))
                serverUri = configSource.Configs["GridService"]?.GetString("GridServerURI", string.Empty) ?? string.Empty;
            m_content = new ConciergeContentClient(serverUri, config.GetInt("content_cache_seconds", 60));

            m_log.InfoFormat("[Concierge] reporting as \"{0}\" to our users; web-portal content {1}",
                m_whoami, m_content.IsConfigured ? "enabled" : "unavailable (no ServerURI / GridServerURI)");

            // calculate regions Regex
            if (m_regions == null)
            {
                string regions = config.GetString("regions", String.Empty);
                if (!String.IsNullOrEmpty(regions))
                {
                    m_regions = new Regex(@regions, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
            }
        }

        public override void AddRegion(Scene scene)
        {
            if (!m_enabled) return;

            lock (m_syncy)
            {
                if (!m_scenes.Contains(scene))
                {
                    m_scenes.Add(scene);

                    if (m_regions == null || m_regions.IsMatch(scene.RegionInfo.RegionName))
                        m_conciergedScenes.Add(scene);

                    // subscribe to NewClient events
                    scene.EventManager.OnNewClient += OnNewClient;

                    // subscribe to *Chat events
                    scene.EventManager.OnChatFromWorld += OnChatFromWorld;
                    if (!m_replacingChatModule)
                        scene.EventManager.OnChatFromClient += OnChatFromClient;
                    scene.EventManager.OnChatBroadcast += OnChatBroadcast;

                    // subscribe to agent change events
                    scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
                    scene.EventManager.OnMakeChildAgent += OnMakeChildAgent;
                }
            }
            m_log.InfoFormat("[Concierge]: initialized for {0}", scene.RegionInfo.RegionName);
        }

        public override void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;

            lock (m_syncy)
            {
                // unsubscribe from NewClient events
                scene.EventManager.OnNewClient -= OnNewClient;

                // unsubscribe from *Chat events
                scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
                if (!m_replacingChatModule)
                    scene.EventManager.OnChatFromClient -= OnChatFromClient;
                scene.EventManager.OnChatBroadcast -= OnChatBroadcast;

                // unsubscribe from agent change events
                scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
                scene.EventManager.OnMakeChildAgent -= OnMakeChildAgent;

                if (m_scenes.Contains(scene))
                {
                    m_scenes.Remove(scene);
                }

                if (m_conciergedScenes.Contains(scene))
                {
                    m_conciergedScenes.Remove(scene);
                }

                m_present.Remove(scene.RegionInfo.RegionID);
            }
            m_log.InfoFormat("[Concierge]: removed {0}", scene.RegionInfo.RegionName);
        }

        public override void PostInitialise()
        {
            if (!m_enabled)
                return;

            // The password-protected welcome upload is the legacy way to
            // edit a welcome text; the web portal replaces it. It stays
            // available only when an operator has set a real password - an
            // empty or the old shipped "SECRET" would let anyone overwrite
            // the welcome files.
            if (string.IsNullOrEmpty(m_welcomes))
                return;

            if (string.IsNullOrEmpty(m_xmlRpcPassword) ||
                    m_xmlRpcPassword.Equals(InsecureDefaultPassword, StringComparison.Ordinal))
            {
                m_log.Warn("[Concierge]: legacy XML-RPC welcome update is DISABLED - set [Concierge] password to a private value to enable it (welcome texts can be edited in the web portal instead)");
                return;
            }

            MainServer.Instance.AddXmlRPCHandler("concierge_update_welcome", XmlRpcUpdateWelcomeMethod, false);
        }

        public override void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            // With the standard chat module replaced, chat carries region-wide;
            // tell viewers so their chat-range handling matches.
            if (m_replacingChatModule)
            {
                ISimulatorFeaturesModule featuresModule = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                if (featuresModule != null)
                {
                    featuresModule.AddOpenSimExtraFeature("say-range", new OSDInteger(9999));
                    featuresModule.AddOpenSimExtraFeature("whisper-range", new OSDInteger(10));
                    featuresModule.AddOpenSimExtraFeature("shout-range", new OSDInteger(9999));
                }
            }
        }

        public override void Close()
        {
            if (m_enabled && !string.IsNullOrEmpty(m_welcomes))
                MainServer.Instance.RemoveXmlRPCHandler("concierge_update_welcome");
        }

        new public Type ReplaceableInterface
        {
            get { return null; }
        }

        public override string Name
        {
            get { return "ConciergeModule"; }
        }
        #endregion

        #region ISimChat Members
        public override void OnChatBroadcast(Object sender, OSChatMessage c)
        {
            if (m_replacingChatModule)
            {
                // distribute chat message to each and every avatar in
                // the region
                base.OnChatBroadcast(sender, c);
            }
        }

        public override void OnChatFromClient(object sender, OSChatMessage c)
        {
            if (c is null)
                return;

            HandleCommand(c);

            if (m_replacingChatModule && c.Scene is Scene scene)
            {
                // replacing ChatModule: need to redistribute
                // ChatFromClient to interested subscribers
                scene.EventManager.TriggerOnChatFromClient(sender, c);

                // when we are replacing ChatModule, we treat
                // OnChatFromClient like OnChatBroadcast for
                // concierged regions, effectively extending the
                // range of chat to cover the whole
                // region. however, we don't do this for whisper
                // (got to have some privacy)
                if (c.Type != ChatTypeEnum.Whisper && m_conciergedScenes.Contains(scene))
                {
                    base.OnChatBroadcast(sender, c);
                    return;
                }

                // redistribution will be done by base class
                base.OnChatFromClient(sender, c);
            }
        }

        public override void OnChatFromWorld(Object sender, OSChatMessage c)
        {
            if (m_replacingChatModule)
            {
                if (c.Type != ChatTypeEnum.Whisper && m_conciergedScenes.Contains(c.Scene))
                {
                    // same region-wide treatment as OnChatFromClient
                    base.OnChatBroadcast(sender, c);
                    return;
                }

                base.OnChatFromWorld(sender, c);
            }
        }
        #endregion

        public override void OnNewClient(IClientAPI client)
        {
            client.OnLogout += OnClientLoggedOut;

            if (m_replacingChatModule)
                client.OnChatFromClient += OnChatFromClient;
        }

        public void OnClientLoggedOut(IClientAPI client)
        {
            client.OnLogout -= OnClientLoggedOut;
            client.OnConnectionClosed -= OnClientLoggedOut;

            if (client.Scene is Scene scene && m_conciergedScenes.Contains(scene))
            {
                m_log.DebugFormat("[Concierge]: {0} logs off from {1}", client.Name, scene.RegionInfo.RegionName);
                HandleDeparture(scene, client.AgentId, client.Name);
            }
        }

        public void OnMakeRootAgent(ScenePresence agent)
        {
            Scene scene = agent.Scene;
            if (scene == null || !m_conciergedScenes.Contains(scene))
                return;

            // Root-agent events can repeat for one visit; greet once.
            if (!MarkPresent(scene, agent.UUID))
                return;

            m_log.DebugFormat("[Concierge]: {0} enters {1}", agent.Name, scene.RegionInfo.RegionName);

            // Off the region's event thread: the greeting looks up the
            // account and may read fresh content from Robust.
            Task.Run(() => HandleArrivalAsync(scene, agent));
        }

        public void OnMakeChildAgent(ScenePresence agent)
        {
            Scene scene = agent.Scene;
            if (scene == null || !m_conciergedScenes.Contains(scene))
                return;

            m_log.DebugFormat("[Concierge]: {0} leaves {1}", agent.Name, scene.RegionInfo.RegionName);
            HandleDeparture(scene, agent.UUID, agent.Name);
        }

        #region Arrival and departure

        private bool MarkPresent(Scene scene, UUID agentID)
        {
            lock (m_syncy)
            {
                UUID regionID = scene.RegionInfo.RegionID;
                if (!m_present.TryGetValue(regionID, out HashSet<UUID> set))
                {
                    set = new HashSet<UUID>();
                    m_present[regionID] = set;
                }

                return set.Add(agentID);
            }
        }

        private bool MarkGone(Scene scene, UUID agentID)
        {
            lock (m_syncy)
            {
                return m_present.TryGetValue(scene.RegionInfo.RegionID, out HashSet<UUID> set) && set.Remove(agentID);
            }
        }

        private async Task HandleArrivalAsync(Scene scene, ScenePresence agent)
        {
            try
            {
                ConciergeContent content = await m_content.GetAsync(scene.RegionInfo.RegionID).ConfigureAwait(false);
                if (content.Enabled == false)
                    return;

                ConciergeAudience audience = Classify(scene, agent);

                SendWelcome(scene, agent, audience, content);

                if (content.Announce ?? m_announceArrivals)
                    AnnounceToAgentsRegion(scene, ConciergeTemplate.Expand(m_announceEntering, Tokens(scene, agent.UUID, agent.Name, agent.Firstname, true)));

                if (content.Notify ?? m_notifyManagers)
                    NotifyManagers(scene, agent, audience);
                else
                    m_log.DebugFormat("[Concierge]: not notifying managers of {0} in {1}: notices are switched off for this region", agent.Name, scene.RegionInfo.RegionName);

                UpdateBroker(scene);
            }
            catch (Exception e)
            {
                m_log.Error(string.Format("[Concierge]: greeting {0} in {1} failed: {2}",
                    agent.Name, scene.RegionInfo.RegionName, e.Message), e);
            }
        }

        private void HandleDeparture(Scene scene, UUID agentID, string legacyName)
        {
            // Only announce a departure for someone we announced arriving:
            // a logout and a child-agent change can both fire for one exit.
            if (!MarkGone(scene, agentID))
                return;

            ConciergeContent content = m_content.Peek(scene.RegionInfo.RegionID);
            if (content.Enabled == false)
                return;

            if (content.Announce ?? m_announceArrivals)
            {
                string first = legacyName;
                int space = legacyName.IndexOf(' ');
                if (space > 0)
                    first = legacyName.Substring(0, space);

                AnnounceToAgentsRegion(scene, ConciergeTemplate.Expand(m_announceLeaving,
                    Tokens(scene, agentID, legacyName, first, true, RootCountExcluding(scene, agentID))));
            }

            UpdateBroker(scene);
        }

        private static string RootCountExcluding(Scene scene, UUID agentID)
        {
            int count = 0;
            scene.ForEachRootScenePresence(sp => { if (sp.UUID != agentID) count++; });
            return count.ToString();
        }

        /// <summary>Who an arriving avatar is, for choosing a greeting.</summary>
        internal ConciergeAudience Classify(Scene scene, ScenePresence agent)
        {
            IUserManagement um = scene.RequestModuleInterface<IUserManagement>();
            if (um != null && !um.IsLocalGridUser(agent.UUID))
                return ConciergeAudience.Visitor;

            UserAccount account = scene.UserAccountService?.GetUserAccount(scene.RegionInfo.ScopeID, agent.UUID);
            if (account == null)
                return ConciergeAudience.Resident;

            if (AccountMembershipHelper.GetMembershipType(account.UserFlags) == AccountMembershipHelper.TrialMember)
                return ConciergeAudience.Trial;

            if (m_newResidentDays > 0 && (Util.UnixTimeSinceEpoch() - account.Created) < m_newResidentDays * 86400L)
                return ConciergeAudience.New;

            return ConciergeAudience.Resident;
        }

        private string DisplayNameOf(Scene scene, UUID agentID, string fallback)
        {
            string name = scene.RequestModuleInterface<IDisplayNameModule>()?.GetDisplayName(agentID);
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        private string OwnerNameOf(Scene scene)
        {
            UUID owner = scene.RegionInfo.EstateSettings?.EstateOwner ?? UUID.Zero;
            if (owner.IsZero())
                return string.Empty;

            return scene.RequestModuleInterface<IUserManagement>()?.GetUserName(owner) ?? string.Empty;
        }

        /// <summary>
        /// Token lookup shared by welcomes, rules and announcements. The
        /// numbered tokens are the original ones: {0} name, {1} region, and
        /// {2} which is the concierge's name in a welcome but the visitor
        /// count in an announcement, exactly as the old module had it.
        /// </summary>
        private Func<string, string> Tokens(Scene scene, UUID agentID, string legacyName, string firstName,
            bool announcement, string countOverride = null)
        {
            string displayName = null;
            string count = countOverride ?? scene.GetRootAgentCount().ToString();

            return token =>
            {
                switch (token.ToLowerInvariant())
                {
                    case "0":
                    case "name":
                        return legacyName;
                    case "displayname":
                        return displayName ??= DisplayNameOf(scene, agentID, legacyName);
                    case "firstname":
                        return firstName;
                    case "1":
                    case "region":
                        return scene.RegionInfo.RegionName;
                    case "2":
                        return announcement ? count : m_whoami;
                    case "concierge":
                        return m_whoami;
                    case "count":
                        return count;
                    case "people":
                        // "1 person" / "3 people": the count with the right noun.
                        return count == "1" ? "1 person" : count + " people";
                    case "grid":
                    {
                        // The grid's own name as set in the web portal, else the ini value.
                        string grid = m_content.Peek(scene.RegionInfo.RegionID).Grid;
                        return string.IsNullOrEmpty(grid) ? m_gridName : grid;
                    }
                    case "estate":
                        return scene.RegionInfo.EstateSettings?.EstateName ?? string.Empty;
                    case "owner":
                        return OwnerNameOf(scene);
                    default:
                        return null;
                }
            };
        }

        #endregion

        #region Welcome

        protected void SendWelcome(Scene scene, ScenePresence agent, ConciergeAudience audience, ConciergeContent content)
        {
            string text = !string.IsNullOrWhiteSpace(content.Welcome) ? content.Welcome : ReadLegacyWelcome(scene);
            if (string.IsNullOrWhiteSpace(text))
            {
                m_log.DebugFormat("[Concierge]: no welcome message for region {0}", scene.RegionInfo.RegionName);
                return;
            }

            Func<string, string> tokens = Tokens(scene, agent.UUID, agent.Name, agent.Firstname, false);
            foreach (string line in ConciergeTemplate.SelectLines(text, audience))
                AnnounceToAgent(agent, ConciergeTemplate.Expand(line, tokens));
        }

        // The original flat-file welcomes: a file named for the region, else
        // one named DEFAULT, in the [Concierge] welcomes directory. (The old
        // code returned after checking only the first path, so DEFAULT was
        // never used.)
        private string ReadLegacyWelcome(Scene scene)
        {
            if (string.IsNullOrEmpty(m_welcomes))
                return string.Empty;

            string[] candidates =
            {
                Path.Combine(m_welcomes, SafeFileName(scene.RegionInfo.RegionName)),
                Path.Combine(m_welcomes, "DEFAULT")
            };

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    return File.ReadAllText(path);
                }
                catch (IOException ioe)
                {
                    m_log.ErrorFormat("[Concierge]: trouble reading welcome file {0} for region {1}: {2}",
                        path, scene.RegionInfo.RegionName, ioe.Message);
                }
            }

            return string.Empty;
        }

        private static string SafeFileName(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return name;
        }

        #endregion

        #region Chat output

        private static readonly Vector3 PosOfGod = new Vector3(128, 128, 9999);

        protected void AnnounceToAgentsRegion(IScene scene, string msg)
        {
            if (scene is Scene targetScene)
            {
                OSChatMessage c = new()
                {
                    Message = msg,
                    Type = ChatTypeEnum.Say,
                    Channel = 0,
                    Position = PosOfGod,
                    From = m_whoami,
                    Scene = scene
                };
                targetScene.EventManager.TriggerOnChatBroadcast(this, c);
            }
        }

        protected void AnnounceToAgent(ScenePresence agent, string msg)
        {
            agent.ControllingClient.SendChatMessage(
                msg, (byte) ChatTypeEnum.Say, PosOfGod, m_whoami, UUID.Zero, UUID.Zero,
                 (byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
        }

        #endregion

        #region Broker

        // Optionally report the attendee list to an external URL whenever
        // someone arrives or leaves (a web page of who is at an event).
        protected void UpdateBroker(Scene scene)
        {
            if (String.IsNullOrEmpty(m_brokerURI))
                return;

            string uri;
            try
            {
                uri = String.Format(m_brokerURI, scene.RegionInfo.RegionName, scene.RegionInfo.RegionID);
            }
            catch (FormatException)
            {
                m_log.ErrorFormat("[Concierge]: [Concierge] broker URL \"{0}\" is not a valid format string", m_brokerURI);
                return;
            }

            StringBuilder list = new StringBuilder();
            list.AppendFormat("<avatars count=\"{0}\" region_name=\"{1}\" region_uuid=\"{2}\" timestamp=\"{3}\">\n",
                scene.GetRootAgentCount(), SecurityElement.Escape(scene.RegionInfo.RegionName),
                scene.RegionInfo.RegionID, DateTime.UtcNow.ToString("s"));

            scene.ForEachRootScenePresence(sp =>
                list.AppendFormat("    <avatar name=\"{0}\" uuid=\"{1}\" />\n", SecurityElement.Escape(sp.Name), sp.UUID));

            list.Append("</avatars>");
            string payload = list.ToString();

            Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, m_brokerUpdateTimeout)));
                    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "text/xml")
                    };
                    request.Headers.UserAgent.ParseAdd("OpenSim.Concierge");

                    using HttpResponseMessage response = await s_brokerHttp.SendAsync(request, cts.Token).ConfigureAwait(false);
                    m_log.DebugFormat("[Concierge]: broker update to {0}: status {1}", uri, (int)response.StatusCode);
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[Concierge]: broker update to {0} failed: {1}", uri, e.Message);
                }
            });
        }

        #endregion

        #region Legacy XML-RPC welcome update

        private static void checkStringParameters(XmlRpcRequest request, string[] param)
        {
            Hashtable requestData = (Hashtable) request.Params[0];
            foreach (string p in param)
            {
                if (!requestData.Contains(p))
                    throw new Exception(String.Format("missing string parameter {0}", p));
                if (String.IsNullOrEmpty((string)requestData[p]))
                    throw new Exception(String.Format("parameter {0} is empty", p));
            }
        }

        public XmlRpcResponse XmlRpcUpdateWelcomeMethod(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            m_log.InfoFormat("[Concierge]: processing UpdateWelcome request from {0}", remoteClient?.Address);
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                checkStringParameters(request, new string[] { "password", "region", "welcome" });

                byte[] given = Encoding.UTF8.GetBytes((string)requestData["password"]);
                byte[] expected = Encoding.UTF8.GetBytes(m_xmlRpcPassword);
                if (!CryptographicOperations.FixedTimeEquals(given, expected))
                    throw new Exception("wrong password");

                if (String.IsNullOrEmpty(m_welcomes))
                    throw new Exception("welcome templates are not enabled, ask your OpenSim operator to set the \"welcomes\" option in the [Concierge] section of OpenSim.ini");

                string msg = (string)requestData["welcome"];

                string regionName = (string)requestData["region"];
                IScene scene = m_scenes.Find(delegate(IScene s) { return s.RegionInfo.RegionName == regionName; });
                if (scene == null)
                    throw new Exception(String.Format("unknown region \"{0}\"", regionName));

                if (!m_conciergedScenes.Contains(scene))
                    throw new Exception(String.Format("region \"{0}\" is not a concierged region.", regionName));

                string welcome = Path.Combine(m_welcomes, SafeFileName(regionName));
                if (File.Exists(welcome))
                {
                    m_log.InfoFormat("[Concierge]: UpdateWelcome: updating existing template \"{0}\"", welcome);
                    string welcomeBackup = String.Format("{0}~", welcome);
                    if (File.Exists(welcomeBackup))
                        File.Delete(welcomeBackup);
                    File.Move(welcome, welcomeBackup);
                }
                File.WriteAllText(welcome, msg);

                responseData["success"] = "true";
                response.Value = responseData;
            }
            catch (Exception e)
            {
                m_log.InfoFormat("[Concierge]: UpdateWelcome failed: {0}", e.Message);

                responseData["success"] = "false";
                responseData["error"] = e.Message;

                response.Value = responseData;
            }
            return response;
        }

        #endregion
    }
}
