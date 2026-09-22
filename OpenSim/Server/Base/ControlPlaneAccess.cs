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
using System.Net;
using Nini.Config;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.Base
{
    // Upstream OpenSim ships a real inter-server control plane - the
    // region agent-create/object-create/neighbour-hello endpoints, the
    // friends endpoint, and a handful of others - on the SAME public HTTP
    // ports it serves ordinary Hypergrid/viewer traffic on, with no caller
    // authentication of its own. Each of those is meant to be called only
    // by another of this grid's own servers (a neighbouring region, or
    // Robust itself); allowing them from anywhere else is what a real,
    // documented disclosure (see PROJECT_LOG.md, 2026-09-23) traced to
    // unauthenticated account takeover, session-credential harvesting, and
    // forged object injection, among others. This is the single gate every
    // one of those handlers should call before touching its request body -
    // refuse any caller that isn't one of this grid's own configured
    // server addresses (plus loopback), fail closed.
    //
    // Depends on HttpRequest.RemoteIPEndPoint actually reflecting the real
    // TCP peer rather than an unverified X-Forwarded-For claim - see that
    // property's own fix, applied first for exactly this reason.
    //
    // Configure via:
    //   [Network]
    //       ControlPlaneTrustedHosts = "region1.example.com, 203.0.113.5"
    // (also accepts [Security] for the same key, and the older
    // TrustedControlPlaneHosts spelling, matching whichever section an
    // operator finds more natural). Hostnames are resolved once, at
    // startup - if a trusted region's own address changes, this process
    // needs restarting to pick it up.
    public class ControlPlaneAccess
    {
        private readonly HashSet<IPAddress> m_trustedHosts = new HashSet<IPAddress>();

        public ControlPlaneAccess(IConfigSource config)
        {
            AddTrustedAddress(IPAddress.Loopback);
            AddTrustedAddress(IPAddress.IPv6Loopback);

            string hosts = GetConfiguredHosts(config);
            foreach (string host in hosts.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                AddTrustedHost(host.Trim());
        }

        // Reject the in-world-script HTTP marker header on top of the
        // address check, so a script running on one of this grid's own
        // (therefore trusted-IP) regions can't be used to reach these
        // endpoints from "inside" - the disclosure calls this out
        // explicitly for every one of these handlers.
        public bool Authorize(IOSHttpRequest request, IOSHttpResponse response, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
        {
            if (request.Headers["X-SecondLife-Shard"] != null)
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return false;
            }

            IPAddress address = NormalizeAddress(request.RemoteIPEndPoint.Address);
            if (IPAddress.IsLoopback(address) || m_trustedHosts.Contains(address))
                return true;

            response.StatusCode = (int)blockedStatus;
            return false;
        }

        private static string GetConfiguredHosts(IConfigSource config)
        {
            string[] sections = { "Network", "Security" };
            string[] keys = { "ControlPlaneTrustedHosts", "TrustedControlPlaneHosts" };

            foreach (string sectionName in sections)
            {
                IConfig section = config?.Configs[sectionName];
                if (section == null)
                    continue;

                foreach (string key in keys)
                {
                    string value = section.GetString(key, string.Empty);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return string.Empty;
        }

        private void AddTrustedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return;

            if (Uri.TryCreate(host, UriKind.Absolute, out Uri uri))
            {
                host = uri.Host;
            }
            else if (host[0] == '[')
            {
                int endBracket = host.IndexOf(']');
                if (endBracket > 0)
                    host = host.Substring(1, endBracket - 1);
            }
            else
            {
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon)
                    host = host.Substring(0, colon);
            }

            if (IPAddress.TryParse(host, out IPAddress address))
            {
                AddTrustedAddress(address);
                return;
            }

            try
            {
                foreach (IPAddress resolvedAddress in System.Net.Dns.GetHostAddresses(host))
                    AddTrustedAddress(resolvedAddress);
            }
            catch
            {
                // Unresolvable at startup - not fatal, just leaves this
                // one host untrusted until the config is corrected and the
                // process restarted.
            }
        }

        private void AddTrustedAddress(IPAddress address)
        {
            m_trustedHosts.Add(NormalizeAddress(address));
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                return address.MapToIPv4();

            return address;
        }
    }
}
