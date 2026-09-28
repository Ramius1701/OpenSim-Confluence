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
using System.Threading;

using Nini.Config;
using log4net;

using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.MapImage
{
    public class MapGetServiceConnector : ServiceConnector
    {
        //private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private IMapImageService m_MapService;

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

            object[] args = [config];
            m_MapService = ServerUtils.LoadPlugin<IMapImageService>(gridService, args);

            server.AddStreamHandler(new MapServerGetHandler(m_MapService));
        }
    }

    class MapServerGetHandler : BaseStreamHandler
    {
        public static readonly object ev = new();

        //private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IMapImageService m_MapService;

        public MapServerGetHandler(IMapImageService service) :
                base("GET", "/map")
        {
            m_MapService = service;
        }

        protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if(!Monitor.TryEnter(ev, 5000))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                httpResponse.AddHeader("Retry-After", "10");
                return [];
            }

            // Every exit below used to be a bare return with a single
            // Monitor.Exit(ev) only on the success path - any exception,
            // or either of the early bad-input returns, leaked the lock
            // forever, wedging every subsequent map-tile request behind
            // TryEnter's 5s timeout (a real, disclosed single-request DoS,
            // see PROJECT_LOG.md 2026-09-23). One try/finally now covers
            // every path, including the Confluence-specific early return
            // added by the path-construction bug fix below.
            //
            // 2026-09-28 upstream sync: adopted origin/master's cleaner
            // scopeID validation (UUID.TryParse + a proper 404) in place
            // of the old try/catch-around-`new UUID(...)` that returned a
            // mysterious `new byte[9]` on a bad scope - same intent, this
            // is just correct instead of a magic value. The path-
            // construction bug fix below is Confluence-only and not
            // present upstream; kept.
            try
            {
                UUID scopeID = UUID.Zero;

                // This will be map/tilefile.ext, but on multitenancy it will be
                // map/scope/teilefile.ext
                path = path.Trim('/');
                string[] bits = path.Split(['/']);
                if (bits.Length > 2)
                {
                    if (string.IsNullOrEmpty(bits[1]) || !UUID.TryParse(bits[1], out scopeID))
                    {
                        httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                        httpResponse.ContentType = "text/plain";
                        return [];
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
                    return [];
                }

                byte[] result = m_MapService.GetMapTile(path, scopeID, out string format);
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
                return result;
            }
            finally
            {
                Monitor.Exit(ev);
            }
        }
    }
}
