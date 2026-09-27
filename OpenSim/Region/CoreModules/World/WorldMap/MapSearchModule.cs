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
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.World.WorldMap
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MapSearchModule")]
    public class MapSearchModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        IGridService m_gridservice = null;
        UUID m_stupidScope = UUID.Zero;

        #region ISharedRegionModule Members
        public void Initialise(IConfigSource source)
        {
        }

        public void AddRegion(Scene scene)
        {
            scene.EventManager.OnNewClient += OnNewClient;
        }

        public void RegionLoaded(Scene scene)
        {
            if (m_gridservice == null)
            {
                m_gridservice = scene.GridService;
                m_stupidScope = scene.RegionInfo.ScopeID;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnNewClient -= OnNewClient;
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            m_gridservice = null;
        }

        public string Name
        {
            get { return "MapSearchModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        private void OnNewClient(IClientAPI client)
        {
            client.OnMapNameRequest += OnMapNameRequestHandler;
        }

        private void OnMapNameRequestHandler(IClientAPI remoteClient, string mapName, uint flags)
        {
            if (m_gridservice == null)
                return;

            try
            {
                List<MapBlockData> blocks = new List<MapBlockData>();
                if (mapName.Length < 3 || (mapName.EndsWith("#") && mapName.Length < 4))
                {
                    // final block, closing the search result
                    AddFinalBlock(blocks, mapName);

                    // flags are agent flags sent from the viewer.
                    // they have different values depending on different viewers, apparently
                    remoteClient.SendMapBlock(blocks, flags);
                    remoteClient.SendAlertMessage("Use a search string with at least 3 characters");
                    return;
                }

                // TEMPORARY diagnostic logging (2026-09-28) - investigating the in-world "Find"
                // flash-then-vanish report. Safe/log-only: no behavior change. Remove once resolved.
                m_log.DebugFormat("[MAPSEARCHMODULE]: MAP NAME=({0}) flags={1}", mapName, flags);
                string mapNameOrig = mapName;
                int indx = mapName.IndexOfAny(new char[] {'.', '!','+','|',':','%'});
                bool needOriginalName = indx >= 0;

                // try to fetch from GridServer
                List<GridRegion> regionInfos = m_gridservice.GetRegionsByName(m_stupidScope, mapName, 20);

                if (!remoteClient.IsActive)
                    return;

                m_log.DebugFormat("[MAPSEARCHMODULE]: search {0} returned {1} regions", mapName, regionInfos == null ? -1 : regionInfos.Count);

                if (regionInfos != null && regionInfos.Count > 0)
                {
                    foreach (GridRegion info in regionInfos)
                    {
                        MapBlockData block = new MapBlockData();
                        MapBlockFromGridRegion(block, info, flags);

                        if (needOriginalName && flags == 2 &&  regionInfos.Count == 1)
                            block.Name = mapNameOrig;
                        blocks.Add(block);
                    }
                }

                // Real fix (2026-09-28): only close the result list with a synthetic "does not
                // exist" (Access=NonExistent) block when nothing was actually found. Sending it
                // unconditionally - even alongside a real match - is what vanilla OpenSim always
                // did here, but Firestorm-family viewers (confirmed against their own source,
                // llworldmapmessage.cpp/llworldmap.cpp/llfloaterworldmap.cpp) treat ANY block with
                // Access=255 in a reply as a failed lookup: it sets found_null_sim, which clears
                // LLFloaterWorldMap's mCompletingRegionName, which makes every later reply for the
                // same search (the viewer sends several near-identical MapNameRequest packets per
                // click) get silently dropped by updateSims' own "already done" guard. Only the
                // first reply ever did anything, which is exactly the "flashes then vanishes"
                // symptom. The closing block still has a real purpose - telling an older viewer
                // "stop waiting, this is everything" - so it's kept for the genuine zero-match
                // case, just no longer sent alongside a real result.
                if (regionInfos == null || regionInfos.Count == 0)
                    AddFinalBlock(blocks, mapNameOrig);

                // TEMPORARY diagnostic logging (2026-09-28): log exactly what is being sent back,
                // in order - this is the one thing the earlier investigation couldn't see at all.
                foreach (MapBlockData b in blocks)
                    m_log.DebugFormat("[MAPSEARCHMODULE]: sending block Name='{0}' X={1} Y={2} Access={3} MapImageId={4}",
                        b.Name, b.X, b.Y, b.Access, b.MapImageId);

                // flags are agent flags sent from the viewer.
                // they have different values depending on different viewers, apparently
                remoteClient.SendMapBlock(blocks, flags);

                // send extra user messages for V3
                // because the UI is very confusing
                // while we don't fix the hard-coded urls
                if (flags == 2)
                {
                    if (regionInfos == null || regionInfos.Count == 0)
                        remoteClient.SendAgentAlertMessage("No regions found with that name.", true);
                }
            }
            catch (Exception e)
            {
                // TEMPORARY (2026-09-28): this was a bare catch{} that silently swallowed every
                // exception with zero logging - kept safe (still never lets an exception escape
                // to the caller) but now actually visible if something is throwing here.
                m_log.WarnFormat("[MAPSEARCHMODULE]: OnMapNameRequestHandler threw for '{0}': {1}", mapName, e);
            }
        }

        private static void MapBlockFromGridRegion(MapBlockData block, GridRegion r, uint flag)
        {
            if (r == null)
            {
                block.Access = (byte)SimAccess.NonExistent;
                block.MapImageId = UUID.Zero;
                return;
            }

            block.Access = r.Access;
            switch (flag)
            {
                case 0:
                    block.MapImageId = r.TerrainImage;
                    break;
                case 2:
                    block.MapImageId = r.ParcelImage;
                    break;
                default:
                    block.MapImageId = UUID.Zero;
                    break;
            }
            block.Name = r.RegionName;
            block.X = (ushort)(r.RegionLocX / Constants.RegionSize);
            block.Y = (ushort)(r.RegionLocY / Constants.RegionSize);
            block.SizeX = (ushort)r.RegionSizeX;
            block.SizeY = (ushort)r.RegionSizeY;
        }

        private void AddFinalBlock(List<MapBlockData> blocks,string name)
        {
                // final block, closing the search result
                MapBlockData data = new MapBlockData()
                {
                    Agents = 0,
                    Access = (byte)SimAccess.NonExistent,
                    MapImageId = UUID.Zero,
                    Name = name,
                    RegionFlags = 0,
                    WaterHeight = 0, // not used
                    X = 0,
                    Y = 0
                };
                blocks.Add(data);
        }
    }
}
