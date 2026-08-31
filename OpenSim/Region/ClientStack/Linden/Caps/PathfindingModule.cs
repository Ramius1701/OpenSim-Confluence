using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api.Implementation;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.LindenCaps
{
    // Backs the real viewer's Pathfinding floaters (llfloaterpathfindingconsole/
    // linksets/characters.cpp, llpathfindingmanager.cpp). Ported/added this session as
    // part of the ongoing cherry-picked-code-vs-viewer audit - the LSL scripting side
    // (llCreateCharacter/llNavigateTo/etc in LSL_Api.cs) already existed, but none of
    // the caps backing the floater UI did.
    //
    // Real navmesh generation/baking is NOT implemented - the actual binary format the
    // viewer's LLPathingLib parses out of RetrieveNavMeshSrc's response is defined by
    // that closed-source Linden Lab library, which isn't present in any locally
    // available viewer checkout and has no public specification (confirmed via
    // web search; even WhiteCore-Dev's own RetrieveNavMeshSrc is an empty stub that
    // leaves the client in an error state - see PROJECT_LOG for the full trace).
    // NavMeshGenerationStatus and RetrieveNavMeshSrc ARE both registered here, correctly
    // implementing everything that IS plain LLSD (status/version reporting, the rebake
    // trigger), so isPathfindingEnabledForRegion()'s presence-gate opens and every other
    // cap in this module actually gets exercised by the viewer - RetrieveNavMeshSrc
    // itself just honestly reports "no mesh data" rather than fabricating bytes with no
    // way to verify they mean anything to LLPathingLib.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "PathfindingModule")]
    public class PathfindingModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene = null;
        private bool m_Enabled = false;

        // Region-wide terrain walkability. Kept in-memory only for this first pass
        // (matches the same not-persisted-across-restart tradeoff already accepted for
        // Combat2's character nav state) - resets to the "fully walkable" default on
        // every restart rather than losing region config permanently.
        private int m_terrainCategory = SceneObjectPart.PathfindingCategoryInclude;
        private int m_terrainA = SceneObjectPart.PathfindingWalkabilityDefault;
        private int m_terrainB = SceneObjectPart.PathfindingWalkabilityDefault;
        private int m_terrainC = SceneObjectPart.PathfindingWalkabilityDefault;
        private int m_terrainD = SceneObjectPart.PathfindingWalkabilityDefault;

        // NavMeshGenerationStatus/RetrieveNavMeshSrc: real navmesh baking + the exact
        // binary format the viewer's LLPathingLib (closed-source, not present in any
        // locally-available viewer checkout, no public spec) expects is not implemented -
        // see PROJECT_LOG. This still reports a valid, well-formed "complete, nothing to
        // show" status so isPathfindingEnabledForRegion()'s presence-gate opens (which is
        // what unblocks every OTHER cap in this module), and the navmesh-retrieval POST
        // correctly reports "no data" rather than a malformed response - the viewer
        // handles that as a clean error state (kNavMeshRequestError), not a crash.
        private int m_navMeshVersion = 1;

        #region ISharedRegionModule

        public void Initialise(IConfigSource source)
        {
            IConfig cnf = source.Configs["Pathfinding"];
            if (cnf == null)
                return;

            m_Enabled = cnf.GetString("Enabled", "false") == "true";
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;
        }

        public void RemoveRegion(Scene scene) { }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void PostInitialise() { }

        public void Close() { }

        public string Name { get { return "PathfindingModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        #endregion

        public void RegisterCaps(UUID agent, Caps caps)
        {
            caps.RegisterSimpleHandler("AgentState",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleAgentState(agent, req, resp)));

            caps.RegisterSimpleHandler("CharacterProperties",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleCharacterProperties(req, resp)));

            caps.RegisterSimpleHandler("TerrainNavMeshProperties",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleTerrainNavMeshProperties(agent, req, resp)));

            caps.RegisterSimpleHandler("RegionObjects",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleRegionObjects(agent, req, resp)));

            caps.RegisterSimpleHandler("ObjectNavMeshProperties",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleObjectNavMeshProperties(agent, req, resp)));

            caps.RegisterSimpleHandler("NavMeshGenerationStatus",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleNavMeshGenerationStatus(agent, req, resp)));

            caps.RegisterSimpleHandler("RetrieveNavMeshSrc",
                new SimpleStreamHandler($"/{UUID.Random()}", (req, resp) => HandleRetrieveNavMeshSrc(req, resp)));
        }

        #region NavMeshGenerationStatus / RetrieveNavMeshSrc

        private void HandleNavMeshGenerationStatus(UUID agentID, IOSHttpRequest request, IOSHttpResponse response)
        {
            switch (request.HttpMethod)
            {
                case "GET":
                    WriteResponse(response, BuildNavMeshStatusResponse());
                    return;
                case "POST":
                    // The rebake button's {"command":"rebuild"} - the viewer only checks
                    // the transport-level HTTP status on this call, not the body, so a
                    // bumped version (making the next GET/status-check look "changed") is
                    // all that is needed here.
                    if (m_scene.Permissions.CanIssueEstateCommand(agentID, false))
                        m_navMeshVersion++;
                    else
                        response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                default:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
            }
        }

        private OSDMap BuildNavMeshStatusResponse()
        {
            return new OSDMap
            {
                ["version"] = OSD.FromInteger(m_navMeshVersion),
                ["status"] = OSD.FromString("complete")
            };
        }

        private void HandleRetrieveNavMeshSrc(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // No navmesh_data key: real baking + LLPathingLib-compatible serialization
            // isn't implemented (see PROJECT_LOG). The viewer treats a missing
            // navmesh_data field as a clean error state, not a crash.
            OSDMap map = new OSDMap { ["navmesh_version"] = OSD.FromInteger(m_navMeshVersion) };

            WriteResponse(response, map);
        }

        #endregion

        #region AgentState

        private void HandleAgentState(UUID agentID, IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            bool canModifyNavmesh = m_scene.Permissions.CanIssueEstateCommand(agentID, false);

            OSDMap map = new OSDMap { ["can_modify_navmesh"] = OSD.FromBoolean(canModifyNavmesh) };

            WriteResponse(response, map);
        }

        #endregion

        #region CharacterProperties

        private void HandleCharacterProperties(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            OSDMap map = new OSDMap();

            foreach (LSL_Api.PathfindingCharacterInfo info in LSL_Api.GetActivePathfindingCharacters(m_scene))
            {
                SceneObjectPart root = m_scene.GetSceneObjectPart(info.RootID);
                if (root == null)
                    continue;

                OSDMap item = new OSDMap
                {
                    ["cpu_time"] = OSD.FromReal(0.0),
                    ["horizontal"] = OSD.FromBoolean(false),
                    ["length"] = OSD.FromReal(info.Length),
                    ["radius"] = OSD.FromReal(info.Radius)
                };
                AddObjectFields(item, root);

                map[info.RootID.ToString()] = item;
            }

            WriteResponse(response, map);
        }

        #endregion

        #region TerrainNavMeshProperties

        private void HandleTerrainNavMeshProperties(UUID agentID, IOSHttpRequest request, IOSHttpResponse response)
        {
            switch (request.HttpMethod)
            {
                case "GET":
                    WriteResponse(response, BuildTerrainResponse());
                    return;
                case "PUT":
                    HandleSetTerrainNavMeshProperties(agentID, request, response);
                    return;
                default:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
            }
        }

        private OSDMap BuildTerrainResponse()
        {
            return new OSDMap
            {
                ["navmesh_category"] = OSD.FromInteger(m_terrainCategory),
                ["A"] = OSD.FromInteger(m_terrainA),
                ["B"] = OSD.FromInteger(m_terrainB),
                ["C"] = OSD.FromInteger(m_terrainC),
                ["D"] = OSD.FromInteger(m_terrainD)
            };
        }

        private void HandleSetTerrainNavMeshProperties(UUID agentID, IOSHttpRequest request, IOSHttpResponse response)
        {
            if (!m_scene.Permissions.CanIssueEstateCommand(agentID, false))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            OSDMap map = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);

            // Terrain has no "phantom" concept - only navmesh_category and the A/B/C/D
            // walkability coefficients are ever altered, matching
            // LLPathfindingLinkset::encodeAlteredFields for the terrain (isTerrain())
            // branch, which skips the phantom field entirely.
            if (map.ContainsKey("navmesh_category"))
                m_terrainCategory = map["navmesh_category"].AsInteger();
            if (map.ContainsKey("A"))
                m_terrainA = Math.Clamp(map["A"].AsInteger(), 0, 100);
            if (map.ContainsKey("B"))
                m_terrainB = Math.Clamp(map["B"].AsInteger(), 0, 100);
            if (map.ContainsKey("C"))
                m_terrainC = Math.Clamp(map["C"].AsInteger(), 0, 100);
            if (map.ContainsKey("D"))
                m_terrainD = Math.Clamp(map["D"].AsInteger(), 0, 100);

            WriteResponse(response, BuildTerrainResponse());
        }

        #endregion

        #region RegionObjects / ObjectNavMeshProperties

        private void HandleRegionObjects(UUID agentID, IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            OSDMap map = new OSDMap();

            foreach (SceneObjectGroup group in m_scene.GetSceneObjectGroups())
            {
                if (group.IsAttachment)
                    continue;

                map[group.UUID.ToString()] = BuildLinksetItem(group, agentID);
            }

            WriteResponse(response, map);
        }

        private void HandleObjectNavMeshProperties(UUID agentID, IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "PUT")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            OSDMap requestMap = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);

            OSDMap responseMap = new OSDMap();

            foreach (string key in requestMap.Keys)
            {
                if (!UUID.TryParse(key, out UUID linksetID))
                    continue;

                SceneObjectGroup group = m_scene.GetSceneObjectGroup(linksetID);
                if (group == null)
                    continue;

                if (!m_scene.Permissions.CanEditObject(group.UUID, agentID))
                    continue;

                if (!(requestMap[key] is OSDMap fields))
                    continue;

                ApplyLinksetFields(group, fields);

                responseMap[key] = BuildLinksetItem(group, agentID);
            }

            WriteResponse(response, responseMap);
        }

        // Mirrors LLPathfindingLinkset::encodeAlteredFields' partial-patch shape - only
        // the fields the resident actually changed are present, everything else is
        // left as-is.
        private void ApplyLinksetFields(SceneObjectGroup group, OSDMap fields)
        {
            if (fields.ContainsKey("phantom"))
                group.ScriptSetPhantomStatus(fields["phantom"].AsBoolean());

            if (fields.ContainsKey("navmesh_category"))
                group.RootPart.SetPathfindingCategory(fields["navmesh_category"].AsInteger());

            int? a = fields.ContainsKey("A") ? fields["A"].AsInteger() : (int?)null;
            int? b = fields.ContainsKey("B") ? fields["B"].AsInteger() : (int?)null;
            int? c = fields.ContainsKey("C") ? fields["C"].AsInteger() : (int?)null;
            int? d = fields.ContainsKey("D") ? fields["D"].AsInteger() : (int?)null;
            if (a.HasValue || b.HasValue || c.HasValue || d.HasValue)
                group.RootPart.SetPathfindingWalkability(a, b, c, d);
        }

        private OSDMap BuildLinksetItem(SceneObjectGroup group, UUID agentID)
        {
            SceneObjectPart root = group.RootPart;

            root.GetPathfindingWalkability(out int a, out int b, out int c, out int d);

            OSDMap item = new OSDMap
            {
                ["landimpact"] = OSD.FromInteger(group.PrimCount),
                ["modifiable"] = OSD.FromBoolean(m_scene.Permissions.CanEditObject(group.UUID, agentID)),
                ["is_scripted"] = OSD.FromBoolean(group.ContainsScripts()),
                ["phantom"] = OSD.FromBoolean(group.IsPhantom),
                ["navmesh_category"] = OSD.FromInteger(root.GetPathfindingCategory()),
                ["can_be_volume"] = OSD.FromBoolean(!group.UsesPhysics),
                ["A"] = OSD.FromInteger(a),
                ["B"] = OSD.FromInteger(b),
                ["C"] = OSD.FromInteger(c),
                ["D"] = OSD.FromInteger(d)
            };
            AddObjectFields(item, root);

            return item;
        }

        #endregion

        private static void AddObjectFields(OSDMap item, SceneObjectPart root)
        {
            SceneObjectGroup group = root.ParentGroup;

            item["name"] = OSD.FromString(root.Name ?? string.Empty);
            item["description"] = OSD.FromString(root.Description ?? string.Empty);
            item["owner"] = OSD.FromUUID(group.OwnerID);
            item["owner_is_group"] = OSD.FromBoolean(group.OwnerID.Equals(group.GroupID) && group.GroupID.IsNotZero());
            item["position"] = new OSDArray
            {
                OSD.FromReal(group.AbsolutePosition.X),
                OSD.FromReal(group.AbsolutePosition.Y),
                OSD.FromReal(group.AbsolutePosition.Z)
            };
        }

        private static void WriteResponse(IOSHttpResponse response, OSDMap map)
        {
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(map);
            response.ContentType = "application/llsd+xml";
            response.StatusCode = (int)HttpStatusCode.OK;
        }
    }
}
