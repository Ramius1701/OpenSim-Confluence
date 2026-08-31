using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

using DirFindFlags = OpenMetaverse.DirectoryManager.DirFindFlags;

namespace OpenSim.Region.CoreModules.World.Search
{
    // Native grid search - see ISearchService/ISearchData for the Places/
    // Land design rationale (replaces the addon-modules OpenSimSearch's
    // dependency on an external XML-RPC search server, see PROJECT_LOG.md
    // Batch 14). Extended (task #53) to cover People/Events/Classifieds/
    // Groups too, after a direct comparison against OpenSimSearch's own
    // source (addon-modules/OpenSimSearch/Modules/SearchModule/
    // OpenSearch.cs) showed this module originally only wired Places/Land,
    // leaving the viewer's Directory floater's other tabs doing nothing -
    // WhiteCore-Dev's own native IDirectoryServiceConnector/
    // IGroupsServiceConnector confirmed the same category split (Places/
    // Events/Classifieds/Groups all real and queryable without a live
    // Scene) is achievable natively rather than via an addon-module.
    //
    // Loads each service directly via ServerUtils.LoadPlugin rather than
    // going through a separate Local*ServiceConnector class, deliberately -
    // this keeps the task to exactly one new [Extension]-tagged region
    // module given the confirmed, unresolved Mono.Addins discovery
    // unreliability for newly-added extension classes on this deployment.
    //
    // Activated the same way the OpenSimSearch addon already gates itself:
    // via [Search] Module = "<Name>". Setting it to "ConfluenceSearchModule"
    // both enables this module and disables OpenSimSearch (which disables
    // itself for any Module value other than "OpenSimSearch").
    //
    // Objects search deliberately NOT implemented - confirmed (both in this
    // codebase and in WhiteCore-Dev) that there is no real in-world object/
    // content indexing capability anywhere to query; the classic viewer's
    // per-prim "Include in search" checkbox exists but nothing ever reads
    // it back out. Map-item search integration (OnMapItemRequest - land-
    // for-sale/event pins shown directly on the World Map, a different
    // viewer feature from the Directory Search floater) is also deferred -
    // real substrate exists (ISearchService/IEventsService) but the
    // request/reply shape (pixel coordinates instead of query results)
    // needs its own pass rather than a rushed bolt-on here.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ConfluenceSearchModule")]
    public class ConfluenceSearchModule : ISharedRegionModule, ISearchModule
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<Scene> m_scenes = new List<Scene>();
        private ISearchService m_searchService = null;
        private IEventsService m_eventsService = null;
        private IUserProfilesService m_userProfilesService = null;
        private IGroupsSearchProvider m_groupsService = null;
        private bool m_enabled = false;

        // Classic Directory-floater Events results/detail-lookup use a
        // 32-bit eventID, but EventItem.ID (this grid's own Events feature,
        // task #31) is a UUID - there's no existing uint identity for an
        // event to reuse. Deriving a stable uint from the UUID's hash and
        // caching the reverse mapping (populated on every DirEventsQuery)
        // lets EventInfoRequest look the real event back up without
        // inventing a second, persisted ID scheme just for this.
        private readonly ConcurrentDictionary<uint, UUID> m_eventIdMap = new ConcurrentDictionary<uint, UUID>();

        public string Name => "ConfluenceSearchModule";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig searchConfig = source.Configs["Search"];
            if (searchConfig == null)
                return;

            if (searchConfig.GetString("Module", "OpenSimSearch") != Name)
                return;

            m_searchService = LoadService<ISearchService>(source, "SearchService");
            if (m_searchService == null)
            {
                m_log.Error("[CONFLUENCE SEARCH]: Can't load search service");
                return;
            }

            m_eventsService = LoadService<IEventsService>(source, "EventsService");
            m_groupsService = LoadService<IGroupsSearchProvider>(source, "GroupsSearchService");

            // IUserProfilesService's concrete implementation takes a 2-arg
            // (IConfigSource, string configName) constructor, not the 1-arg
            // shape every other service here uses - same special case
            // WebInterfaceServiceConnector already has to handle.
            IConfig profilesSection = source.Configs["UserProfilesService"];
            if (profilesSection != null)
            {
                string profilesDll = profilesSection.GetString("LocalServiceModule", string.Empty);
                if (!string.IsNullOrEmpty(profilesDll))
                {
                    try
                    {
                        m_userProfilesService = ServerUtils.LoadPlugin<IUserProfilesService>(profilesDll, new object[] { source, "UserProfilesService" });
                    }
                    catch (Exception e)
                    {
                        m_log.Error("[CONFLUENCE SEARCH]: Failed to load user profiles service", e);
                    }
                }
            }

            m_enabled = true;
            m_log.Info("[CONFLUENCE SEARCH]: Native search module is active");
        }

        private static T LoadService<T>(IConfigSource source, string sectionName) where T : class
        {
            IConfig serviceConfig = source.Configs[sectionName];
            if (serviceConfig == null)
            {
                m_log.WarnFormat("[CONFLUENCE SEARCH]: {0} section missing from configuration - that category will return no results", sectionName);
                return null;
            }

            string serviceDll = serviceConfig.GetString("LocalServiceModule", string.Empty);
            if (serviceDll == string.Empty)
            {
                m_log.WarnFormat("[CONFLUENCE SEARCH]: No LocalServiceModule named in section {0}", sectionName);
                return null;
            }

            try
            {
                return ServerUtils.LoadPlugin<T>(serviceDll, new object[] { source });
            }
            catch (Exception e)
            {
                m_log.Error("[CONFLUENCE SEARCH]: Failed to load " + sectionName, e);
                return null;
            }
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnNewClient += OnNewClient;
            scene.RegisterModuleInterface<ISearchModule>(this);

            lock (m_scenes)
                m_scenes.Add(scene);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.UnregisterModuleInterface<ISearchModule>(this);
            scene.EventManager.OnNewClient -= OnNewClient;

            lock (m_scenes)
                m_scenes.Remove(scene);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
        }

        public void Refresh()
        {
        }

        private void OnNewClient(IClientAPI client)
        {
            client.OnDirPlacesQuery += DirPlacesQuery;
            client.OnDirLandQuery += DirLandQuery;
            client.OnDirFindQuery += DirFindQuery;
            client.OnDirPopularQuery += DirPopularQuery;
            client.OnDirClassifiedQuery += DirClassifiedQuery;
            client.OnEventInfoRequest += EventInfoRequest;
            client.OnClassifiedInfoRequest += ClassifiedInfoRequest;
        }

        // 42 = Adult/unrestricted (Util.ConvertMaturityToAccessLevel's own
        // convention) - the classic Directory floater's own maturity
        // preference already gates what a viewer requests before it even
        // reaches here; decoding queryFlags' maturity bits to filter a
        // second time is a separate task, not attempted in this pass.
        private const int UnrestrictedAccess = 42;

        private void DirPlacesQuery(IClientAPI remoteClient, UUID queryID,
                string queryText, int queryFlags, int category, string simName,
                int queryStart)
        {
            List<LandSearchRecord> results = m_searchService.SearchPlaces(queryText, queryStart, 100, UnrestrictedAccess);
            m_log.InfoFormat("[CONFLUENCE SEARCH]: DirPlacesQuery text='{0}' flags={1} category={2} sim='{3}' -> {4} results",
                    queryText, queryFlags, category, simName, results.Count);

            DirPlacesReplyData[] data = new DirPlacesReplyData[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                LandSearchRecord r = results[i];
                data[i] = new DirPlacesReplyData
                {
                    parcelID = r.ParcelID,
                    name = r.Name,
                    forSale = r.ForSale,
                    auction = r.Auction,
                    dwell = r.Dwell
                };
            }

            remoteClient.SendDirPlacesReply(queryID, data);
        }

        // LimitByPrice/LimitByArea flag bits match OpenSim-Grid-Interface's
        // real helper/query.php (dir_land_query) exactly - the price/area
        // spinners in the viewer's Land Sales tab are only meant to apply
        // when their checkbox is actually ticked; the raw price/area values
        // are sent regardless of checkbox state, so passing them through
        // unconditionally (as this used to) silently over-filters results
        // whenever the checkboxes are unticked but the fields hold a
        // leftover/default value.
        private const uint LimitByPriceFlag = 0x100000;
        private const uint LimitByAreaFlag = 0x200000;

        private void DirLandQuery(IClientAPI remoteClient, UUID queryID,
                uint queryFlags, uint searchType, int price, int area,
                int queryStart)
        {
            int maxPrice = (queryFlags & LimitByPriceFlag) != 0 ? price : 0;
            int minArea = (queryFlags & LimitByAreaFlag) != 0 ? area : 0;

            List<LandSearchRecord> results = m_searchService.SearchLandForSale(maxPrice, minArea, queryStart, 100);
            m_log.InfoFormat("[CONFLUENCE SEARCH]: DirLandQuery flags={0} searchType={1} price={2} area={3} (effective maxPrice={4} minArea={5}) -> {6} results",
                    queryFlags, searchType, price, area, maxPrice, minArea, results.Count);

            // Stock viewer/server protocol, not our own invention: clicking
            // a Land Sales result sends a UDP ParcelInfoRequest carrying
            // whatever UUID we hand back as parcelID here. Stock
            // LandManagementModule.ClientOnParcelInfoRequest decodes that
            // UUID via Util.ParseFakeParcelID (region handle + local x/y
            // baked into the UUID bytes) - a real database parcel UUID
            // fails that decode and the server just drops the request
            // (logs "got no parcelinfo; not sending"), which is why the
            // viewer's detail pane and Teleport/Map buttons used to hang on
            // "Loading..." forever. Building the same fake ID
            // LandObject.cs's own LandData.FakeID uses (see
            // Util.BuildFakeParcelID's call sites) is the fix, not a guess.
            IGridService gridService = remoteClient.Scene is Scene scene ? scene.GridService : null;
            DirLandReplyData[] data = new DirLandReplyData[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                LandSearchRecord r = results[i];
                UUID replyParcelId = r.ParcelID;
                if (gridService != null && !string.IsNullOrEmpty(r.RegionName))
                {
                    OpenSim.Services.Interfaces.GridRegion region = gridService.GetRegionByName(remoteClient.ScopeId, r.RegionName);
                    if (region != null)
                    {
                        // LandingX/Y (the About Land "landing point") is only
                        // ever set if the owner actually clicked Set - most
                        // parcels never do. A landing point of exactly (0,0)
                        // is indistinguishable from "never set" here, and
                        // (0,0) is the region's own corner, not "the
                        // parcel" - using it made results look like they
                        // pointed at the wrong place. The parcel's real
                        // shape (its Bitmap blob) isn't fetched by this
                        // query, so a true guaranteed-inside-the-parcel
                        // point isn't available without decoding that - the
                        // region's center is a real, honest fallback (nowhere
                        // near as wrong as the corner) rather than a
                        // disguised guess at the parcel's actual shape.
                        uint localX = (uint)r.LandingX;
                        uint localY = (uint)r.LandingY;
                        if (localX == 0 && localY == 0)
                        {
                            localX = (uint)(region.RegionSizeX / 2);
                            localY = (uint)(region.RegionSizeY / 2);
                        }
                        replyParcelId = Util.BuildFakeParcelID(region.RegionHandle, localX, localY);
                    }
                }

                data[i] = new DirLandReplyData
                {
                    parcelID = replyParcelId,
                    name = r.Name,
                    auction = r.Auction,
                    forSale = r.ForSale,
                    salePrice = r.SalePrice,
                    actualArea = r.Area
                };
            }

            remoteClient.SendDirLandReply(queryID, data);
        }

        // Same protocol overload OpenSimSearch/WhiteCore-Dev both use: one
        // viewer packet (OnDirFindQuery) carries a People, Groups, or Events
        // (DateEvents) search, distinguished only by a flag bit. Groups was
        // missing here (m_groupsService was loaded in Initialise() but never
        // actually queried) - confirmed against the real viewer source
        // (llpaneldirgroups.cpp, still present and reachable in both the
        // official client and Firestorm) that the classic Directory
        // floater's Groups tab really does send DirFindQuery with
        // DFQ_GROUPS/DirFindFlags.Groups, not a separate message - and
        // BasicSearchModule.cs (vanilla OpenSim's own basic search) already
        // handles this same flag, so this was a real regression relative to
        // what stock OpenSim already did, not unsupported-by-design.
        private void DirFindQuery(IClientAPI remoteClient, UUID queryID,
                string queryText, uint queryFlags, int queryStart)
        {
            if (((DirFindFlags)queryFlags & DirFindFlags.DateEvents) == DirFindFlags.DateEvents)
            {
                DirEventsQuery(remoteClient, queryID, queryText, queryStart);
                return;
            }

            if (((DirFindFlags)queryFlags & DirFindFlags.Groups) == DirFindFlags.Groups)
            {
                DirGroupsQuery(remoteClient, queryID, queryText, queryStart);
                return;
            }

            if (((DirFindFlags)queryFlags & DirFindFlags.People) == DirFindFlags.People)
                DirPeopleQuery(remoteClient, queryID, queryText, queryStart);
        }

        private void DirGroupsQuery(IClientAPI remoteClient, UUID queryID, string queryText, int queryStart)
        {
            if (m_groupsService == null)
            {
                remoteClient.SendAlertMessage("Groups search is not enabled");
                remoteClient.SendDirGroupsReply(queryID, new DirGroupsReplyData[0]);
                return;
            }

            List<DirGroupsReplyData> results = m_groupsService.FindGroups(remoteClient.AgentId.ToString(), queryText);

            if (results.Count == 0)
            {
                remoteClient.SendDirGroupsReply(queryID, new DirGroupsReplyData[0]);
                return;
            }

            DirGroupsReplyData[] data = results.ToArray();

            // Same paging as BasicSearchModule's own Groups handling - a viewer page is
            // 100 results, and queryStart is the resident paging to the next page.
            if (queryStart > 0 && queryStart < data.Length)
            {
                int len = Math.Min(data.Length - queryStart, 101);
                DirGroupsReplyData[] page = new DirGroupsReplyData[len];
                Array.Copy(data, queryStart, page, 0, len);
                data = page;
            }
            else if (data.Length > 101)
            {
                DirGroupsReplyData[] page = new DirGroupsReplyData[101];
                Array.Copy(data, 0, page, 0, 101);
                data = page;
            }

            remoteClient.SendDirGroupsReply(queryID, data);
        }

        private void DirPeopleQuery(IClientAPI remoteClient, UUID queryID, string queryText, int queryStart)
        {
            IUserAccountService accounts = remoteClient.Scene is Scene scene ? scene.UserAccountService : null;
            if (accounts == null)
            {
                remoteClient.SendDirPeopleReply(queryID, new DirPeopleReplyData[0]);
                return;
            }

            List<UserAccount> results = accounts.GetUserAccounts(remoteClient.ScopeId, queryText);

            DirPeopleReplyData[] data = new DirPeopleReplyData[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                UserAccount a = results[i];
                data[i] = new DirPeopleReplyData
                {
                    agentID = a.PrincipalID,
                    firstName = a.FirstName,
                    lastName = a.LastName,
                    // Online status/active group tag aren't available from
                    // IUserAccountService alone (would need IPresenceService/
                    // IGroupsModule threaded through here too) - left at
                    // honest defaults rather than faking "online".
                    group = string.Empty,
                    online = false,
                    reputation = 0
                };
            }

            remoteClient.SendDirPeopleReply(queryID, data);
        }

        private void DirEventsQuery(IClientAPI remoteClient, UUID queryID, string queryText, int queryStart)
        {
            if (m_eventsService == null)
            {
                m_log.Warn("[CONFLUENCE SEARCH]: DirEventsQuery received but m_eventsService is null - EventsService section missing/failed to load");
                remoteClient.SendDirEventsReply(queryID, new DirEventsReplyData[0]);
                return;
            }

            // The viewer's Events tab sends a compound queryText like
            // "u|0|test" - dayToken|category|searchText, pipe-separated -
            // not plain text. This exact format and field order is ported
            // directly from OpenSim-Grid-Interface's real helper/query.php
            // (dir_events_query), the actual proven backend behind this tab
            // before this native module existed: `explode("|", $text)`,
            // pieces[0]=day token, pieces[1]=category, pieces[2]=search
            // text (empty when fewer than 3 pieces). Neither WhiteCore-
            // Dev's own DirEventsQuery nor the OpenSimSearch addon parse
            // this in C# - query.php did it server-side, in PHP, which is
            // why porting its exact logic here (not inventing new logic)
            // is the fix, not a guess.
            string[] pieces = (queryText ?? string.Empty).Split('|');
            string dayToken = pieces.Length > 0 ? pieces[0].Trim().ToLowerInvariant() : string.Empty;
            string eventsSearchText = pieces.Length >= 3 ? pieces[2] : string.Empty;

            // Real day-token semantics, taken directly from Firestorm's own
            // FSPanelSearchEvents::find()/setDay() (fsfloatersearch.cpp):
            // "u" means the "In-Progress & Upcoming" radio mode, sent as a
            // literal "u|category|text". The "Date" radio mode instead sends
            // mDay - a plain signed day offset from today (0=today,
            // 1=tomorrow, -1=yesterday, ...) - as the same field, and
            // setDay() computes that day's boundary in Pacific time
            // (utc_to_pacific_time), not UTC. This is not query.php's
            // format (that was PHP-side day math for a different, dead
            // backend) - it's what the real client on the wire actually
            // sends, read straight from source rather than guessed.
            List<EventItem> results;
            if (dayToken == "u" || dayToken == string.Empty)
            {
                results = m_eventsService.SearchEvents(eventsSearchText, queryStart, 100);
            }
            else if (int.TryParse(dayToken, out int dayOffset))
            {
                GetPacificDayBoundaryUnix(dayOffset, out int dayStartUnix, out int dayEndUnix);
                results = m_eventsService.SearchEventsByDay(eventsSearchText, dayStartUnix, dayEndUnix, queryStart, 100);
            }
            else
            {
                m_log.WarnFormat("[CONFLUENCE SEARCH]: DirEventsQuery day token '{0}' is neither 'u' nor a parseable day offset - falling back to upcoming-only", dayToken);
                results = m_eventsService.SearchEvents(eventsSearchText, queryStart, 100);
            }

            m_log.InfoFormat("[CONFLUENCE SEARCH]: DirEventsQuery text='{0}' (day='{1}' text='{2}') -> {3} results",
                    queryText, dayToken, eventsSearchText, results.Count);

            DirEventsReplyData[] data = new DirEventsReplyData[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                EventItem ev = results[i];
                uint eventId = EventUuidToUint(ev.ID);

                data[i] = new DirEventsReplyData
                {
                    ownerID = ev.CreatorId,
                    name = ev.Title,
                    eventID = eventId,
                    date = ev.EventDate.ToString("MM/dd/yyyy HH:mm"),
                    unixTime = Utils.DateTimeToUnixTime(ev.EventDate),
                    eventFlags = 0
                };
            }

            remoteClient.SendDirEventsReply(queryID, data);
        }

        // Cached lookup, not re-resolved per query - TimeZoneInfo.FindSystemTimeZoneById
        // is the slow part. Tries the IANA ID first (Linux/cross-platform),
        // then the Windows-only ID, matching how this codebase is actually
        // deployed (dev on Windows, same binaries can run on Linux).
        private static readonly TimeZoneInfo PacificTimeZone = ResolvePacificTimeZone();

        private static TimeZoneInfo ResolvePacificTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
            catch (TimeZoneNotFoundException) { }

            try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
            catch (TimeZoneNotFoundException) { }

            return TimeZoneInfo.Utc;
        }

        // Mirrors Firestorm's FSPanelSearchEvents::setDay exactly: take
        // "now" converted to Pacific time, add dayOffset whole days, and use
        // that Pacific-time day's midnight-to-midnight boundary - not a UTC
        // day boundary, since the viewer's Today/Yesterday/Tomorrow arrows
        // are all relative to Pacific "server time" the same way SL's real
        // event search always has been.
        private static void GetPacificDayBoundaryUnix(int dayOffset, out int dayStartUnix, out int dayEndUnix)
        {
            DateTime nowPacific = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTimeZone);
            DateTime targetDayStartPacific = nowPacific.Date.AddDays(dayOffset);
            DateTime targetDayEndPacific = targetDayStartPacific.AddDays(1);

            DateTime dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(targetDayStartPacific, DateTimeKind.Unspecified), PacificTimeZone);
            DateTime dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(targetDayEndPacific, DateTimeKind.Unspecified), PacificTimeZone);

            dayStartUnix = (int)Utils.DateTimeToUnixTime(dayStartUtc);
            dayEndUnix = (int)Utils.DateTimeToUnixTime(dayEndUtc);
        }

        private uint EventUuidToUint(UUID id)
        {
            uint candidate = unchecked((uint)id.GetHashCode());
            if (candidate == 0)
                candidate = 1;

            m_eventIdMap[candidate] = id;
            return candidate;
        }

        private void DirPopularQuery(IClientAPI remoteClient, UUID queryID, uint queryFlags)
        {
            // No dedicated popularity metric exists (no visit/traffic
            // tracking anywhere in this codebase) - reuses the exact same
            // real Dwell-ordered data SearchPlaces already returns for the
            // Places tab, rather than inventing a second metric. An empty
            // query still returns real (not fabricated) top-by-dwell parcels
            // because SearchPlaces' LIKE '%%' matches every listed parcel.
            List<LandSearchRecord> results = m_searchService.SearchPlaces(string.Empty, 0, 100, UnrestrictedAccess);

            DirPopularReplyData[] data = new DirPopularReplyData[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                LandSearchRecord r = results[i];
                data[i] = new DirPopularReplyData
                {
                    parcelID = r.ParcelID,
                    name = r.Name,
                    dwell = r.Dwell
                };
            }

            remoteClient.SendDirPopularReply(queryID, data);
        }

        private void DirClassifiedQuery(IClientAPI remoteClient, UUID queryID,
                string queryText, uint queryFlags, uint category, int queryStart)
        {
            if (m_userProfilesService == null)
            {
                m_log.Warn("[CONFLUENCE SEARCH]: DirClassifiedQuery received but m_userProfilesService is null - UserProfilesService failed to load");
                remoteClient.SendDirClassifiedReply(queryID, new DirClassifiedReplyData[0]);
                return;
            }

            List<UserClassifiedAdd> results = m_userProfilesService.SearchClassifieds(queryText, queryStart, 100);
            m_log.InfoFormat("[CONFLUENCE SEARCH]: DirClassifiedQuery text='{0}' -> {1} results", queryText, results.Count);

            DirClassifiedReplyData[] data = new DirClassifiedReplyData[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                UserClassifiedAdd ad = results[i];
                data[i] = new DirClassifiedReplyData
                {
                    classifiedID = ad.ClassifiedId,
                    name = ad.Name,
                    classifiedFlags = ad.Flags,
                    creationDate = (uint)ad.CreationDate,
                    expirationDate = (uint)ad.ExpirationDate,
                    price = ad.Price
                };
            }

            remoteClient.SendDirClassifiedReply(queryID, data);
        }

        private void EventInfoRequest(IClientAPI remoteClient, uint queryEventID)
        {
            if (m_eventsService == null || !m_eventIdMap.TryGetValue(queryEventID, out UUID eventUuid))
            {
                remoteClient.SendAgentAlertMessage("Couldn't find this event.", false);
                return;
            }

            EventItem ev = m_eventsService.Get(eventUuid);
            if (ev == null)
            {
                remoteClient.SendAgentAlertMessage("Couldn't find this event.", false);
                return;
            }

            EventData data = new EventData
            {
                eventID = queryEventID,
                creator = ev.CreatorId.ToString(),
                name = ev.Title,
                category = ev.Category,
                description = ev.Description,
                date = ev.EventDate.ToString("MM/dd/yyyy HH:mm"),
                dateUTC = Utils.DateTimeToUnixTime(ev.EventDate),
                duration = (uint)ev.DurationMinutes,
                cover = 0,
                amount = 0,
                simName = ev.Location,
                eventFlags = 0
            };

            // Same pattern as ClassifiedInfoRequest below (Vector3.TryParse
            // on a stored "x,y,z" global-position string) - and the exact
            // same field the real, proven OpenSimSearch addon's own
            // EventInfoRequest already used ("globalposition"), just under
            // this codebase's own EventItem.GlobalPos name. Leaves
            // data.globalPos at its zero default when no location was
            // captured, rather than guessing one - onClickTeleport/onClickMap
            // in the viewer are both no-ops on an exactly-zero global pos.
            Vector3.TryParse(ev.GlobalPos, out data.globalPos);

            remoteClient.SendEventInfoReply(data);
        }

        private void ClassifiedInfoRequest(UUID queryClassifiedID, IClientAPI remoteClient)
        {
            if (m_userProfilesService == null)
                return;

            UserClassifiedAdd ad = new UserClassifiedAdd { ClassifiedId = queryClassifiedID };
            string result = string.Empty;
            if (!m_userProfilesService.ClassifiedInfoRequest(ref ad, ref result))
                return;

            Vector3 globalPos = new Vector3();
            Vector3.TryParse(ad.GlobalPos, out globalPos);

            remoteClient.SendClassifiedInfoReply(
                    ad.ClassifiedId,
                    ad.CreatorId,
                    (uint)ad.CreationDate,
                    (uint)ad.ExpirationDate,
                    (uint)ad.Category,
                    ad.Name,
                    ad.Description,
                    ad.ParcelId,
                    (uint)ad.ParentEstate,
                    ad.SnapshotId,
                    ad.SimName,
                    globalPos,
                    ad.ParcelName,
                    ad.Flags,
                    ad.Price);
        }
    }
}
