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
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

using Nini.Config;
using log4net;

using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenMetaverse;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Server.Handlers.MapImage
{
    public class MapGetServiceConnector : ServiceConnector
    {
        //private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IMapImageService m_MapService;
        private IGridService m_GridService;

        private string m_ConfigName = "MapImageService";

        public MapGetServiceConnector(IConfigSource config, IHttpServer server, string configName) :
            base(config, server, configName)
        {
            IConfig serverConfig = config.Configs[m_ConfigName];
            if (serverConfig == null)
                throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

            string gridService = serverConfig.GetString("LocalServiceModule", string.Empty);

            if (string.IsNullOrWhiteSpace(gridService))
                throw new Exception("No LocalServiceModule in config file");

            object[] args = new object[] { config };
            m_MapService = ServerUtils.LoadPlugin<IMapImageService>(gridService, args);

            // Same optional "[MapImageService] GridService" liveness-check
            // key MapAddServerConnector.cs already uses on the upload side
            // (GetRegionByPosition, anti-spoofing) - reused here so a
            // deregistered region's own last-generated tile image (never
            // deleted, per this grid's asset-retention policy) stops being
            // served once nothing is actually registered at that
            // coordinate, instead of persisting on the World Map forever.
            string gridServiceForCheck = serverConfig.GetString("GridService", string.Empty);
            if (!string.IsNullOrWhiteSpace(gridServiceForCheck))
                m_GridService = ServerUtils.LoadPlugin<IGridService>(gridServiceForCheck, args);

            server.AddStreamHandler(new MapServerGetHandler(m_MapService, m_GridService));
        }
    }

    class MapServerGetHandler : BaseStreamHandler
    {
        public static readonly object ev = new object();

        //private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Zoom level 1 tiles map 1:1 to a real region's own grid position -
        // that's the only shape GetRegionByPosition below can meaningfully
        // check. Higher zoom levels are pre-rendered composite/overview
        // mosaics covering multiple regions at a different coordinate
        // scale entirely - checking those against a single region's
        // position was a real bug found live (2026-09-13): it rejected
        // still-online regions' own composite tiles, not just offline
        // ones. Only ever validate zoom level 1.
        private static readonly Regex TileNameRegex = new Regex(@"^map-(\d+)-(\d+)-(\d+)-", RegexOptions.Compiled);

        private IMapImageService m_MapService;
        private IGridService m_GridService;

        public MapServerGetHandler(IMapImageService service, IGridService gridService) :
                base("GET", "/map")
        {
            m_MapService = service;
            m_GridService = gridService;
        }

        protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if(!Monitor.TryEnter(ev, 5000))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                httpResponse.AddHeader("Retry-After", "10");
                return Array.Empty<byte>();
            }

            byte[] result = Array.Empty<byte>();
            string format = string.Empty;

            //UUID scopeID = new UUID("07f8d88e-cd5e-4239-a0ed-843f75d09992");
            UUID scopeID = UUID.Zero;

            // This will be map/tilefile.ext, but on multitenancy it will be
            // map/scope/teilefile.ext
            path = path.Trim('/');
            string[] bits = path.Split(new char[] {'/'});
            if (bits.Length > 2)
            {
                try
                {
                    scopeID = new UUID(bits[1]);
                }
                catch
                {
                    return new byte[9];
                }
                path = bits[2];
                path = path.Trim('/');
            }
            // BUG FIX: the common no-scope case (bits.Length == 2, e.g. the
            // real request path "map/map-1-1000-1000-objects.jpg") never
            // reduced path down to just the filename - it stayed as the
            // full "map/map-1-1000-1000-objects.jpg" string, which
            // MapImageService.GetMapTile then Path.Combine'd onto the tile
            // storage folder, producing a bogus nested "map/" subdirectory
            // that never existed on disk (confirmed: real tiles sit directly
            // in maptiles/<scopeID>/map-1-X-Y-objects.jpg, one level up from
            // where this bug was looking). Every tile request silently
            // failed and fell back to the generic water-tile placeholder
            // (or 404'd if that wasn't configured) - not a routing/config
            // problem, a real path-construction bug in this handler.
            else if (bits.Length == 2)
            {
                path = bits[1];
            }

            if(path.Length == 0)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                httpResponse.ContentType = "text/plain";
                return Array.Empty<byte>();
            }

            if (m_GridService != null)
            {
                Match m = TileNameRegex.Match(path);
                if (m.Success
                        && int.TryParse(m.Groups[1].Value, out int tileZoom) && tileZoom == 1
                        && int.TryParse(m.Groups[2].Value, out int tileX)
                        && int.TryParse(m.Groups[3].Value, out int tileY))
                {
                    GridRegion r = m_GridService.GetRegionByPosition(scopeID,
                            (int)Util.RegionToWorldLoc((uint)tileX), (int)Util.RegionToWorldLoc((uint)tileY));
                    if (r == null)
                    {
                        Monitor.Exit(ev);
                        httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                        httpResponse.ContentType = "text/plain";
                        return Array.Empty<byte>();
                    }
                }
            }

            result = m_MapService.GetMapTile(path, scopeID, out format);
            if (result.Length > 0)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.OK;
                if (format.Equals(".png"))
                    httpResponse.ContentType = "image/png";
                else if (format.Equals(".jpg") || format.Equals(".jpeg"))
                    httpResponse.ContentType = "image/jpeg";
            }
            else
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                httpResponse.ContentType = "text/plain";
            }

            Monitor.Exit(ev);

            return result;
        }
    }
}
