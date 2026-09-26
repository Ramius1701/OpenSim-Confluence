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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
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
    //
    // Manual config above is optional, not required - by design. Robust's
    // own SimulationServiceConnector (and the other service-to-service
    // callers this class gates) call a region's *public* ServerURI for
    // ordinary login/teleport traffic, not loopback - most small/hobbyist
    // grids run every process on one machine behind NAT with no purely
    // internal path at all, so a loopback-only default would silently
    // 403 every login the first time this gate is enabled (found and
    // fixed live on Casperia, 2026-09-23 - see PROJECT_LOG.md). Every
    // grid owner already sets [Const] BaseHostname (the one truly
    // required setting for any grid), so its resolved address(es) are
    // auto-trusted below, together with this machine's own local network
    // identity (its interfaces' addresses and default gateways) to cover
    // whatever a home router's NAT hairpin/loopback rewrites a same-box
    // call's source to. This needs zero new configuration from any grid
    // owner to work correctly out of the box.
    public class ControlPlaneAccess
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // A refusal is worth telling the operator about - a silent 403 is
        // exactly what made the first live deploy of this gate take real
        // digging to diagnose (2026-09-23) - but these endpoints sit on
        // internet-reachable ports, so an unauthenticated scanner must not
        // be able to flood the log. One line per source address + endpoint
        // family per minute.
        private const long RefusalLogIntervalMs = 60_000;
        private const int MaxRefusalLogKeys = 1024;
        private static readonly ConcurrentDictionary<string, long> s_lastRefusalLogged = new ConcurrentDictionary<string, long>();
        private static int s_startupLogged;

        private readonly HashSet<IPAddress> m_trustedHosts = new HashSet<IPAddress>();

        public ControlPlaneAccess(IConfigSource config)
        {
            AddTrustedAddress(IPAddress.Loopback);
            AddTrustedAddress(IPAddress.IPv6Loopback);

            AddLocalMachineAddresses();

            string baseHostname = config?.Configs["Const"]?.GetString("BaseHostname", string.Empty);
            if (!string.IsNullOrWhiteSpace(baseHostname))
                AddTrustedHost(baseHostname.Trim());

            string hosts = GetConfiguredHosts(config);
            foreach (string host in hosts.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                AddTrustedHost(host.Trim());

            // Many connectors each build their own instance - say this
            // once per process, not once per connector.
            if (Interlocked.Exchange(ref s_startupLogged, 1) == 0)
            {
                m_log.InfoFormat("[CONTROL PLANE ACCESS]: {0} trusted control-plane addresses (loopback, this machine's interfaces and gateways, [Const] BaseHostname, ControlPlaneTrustedHosts). Control-plane requests from any other address are refused.",
                    m_trustedHosts.Count);
                m_log.DebugFormat("[CONTROL PLANE ACCESS]: Trusted addresses: {0}", string.Join(", ", m_trustedHosts));
            }
        }

        private static void LogRefusal(string key, string message)
        {
            long now = Environment.TickCount64;
            if (s_lastRefusalLogged.TryGetValue(key, out long last) && now - last < RefusalLogIntervalMs)
                return;

            if (s_lastRefusalLogged.Count >= MaxRefusalLogKeys)
                s_lastRefusalLogged.Clear();

            s_lastRefusalLogged[key] = now;
            m_log.Warn(message);
        }

        private static string EndpointFamily(string uriPath)
        {
            if (string.IsNullOrEmpty(uriPath))
                return string.Empty;

            int slash = uriPath.IndexOf('/', 1);
            return slash > 0 ? uriPath.Substring(0, slash) : uriPath;
        }

        // Trusts this machine's own identity on its local network(s) -
        // every address any of its interfaces actually holds, plus each
        // interface's own default gateway. Covers the common cases where
        // a home/small-office router's NAT hairpin either preserves the
        // original LAN sender's address or rewrites it to the gateway's
        // own address when a box calls back into its own public-facing
        // hostname. Best-effort: a machine with no usable NICs (unlikely)
        // just falls back to whatever BaseHostname/manual config adds.
        private void AddLocalMachineAddresses()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;

                    IPInterfaceProperties props;
                    try
                    {
                        props = nic.GetIPProperties();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                        AddTrustedAddress(addr.Address);

                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                        AddTrustedAddress(gw.Address);
                }
            }
            catch
            {
                // Best-effort discovery only - never let this stop the
                // process from starting.
            }
        }

        // Reject the in-world-script HTTP marker header on top of the
        // address check, so a script running on one of this grid's own
        // (therefore trusted-IP) regions can't be used to reach these
        // endpoints from "inside" - the disclosure calls this out
        // explicitly for every one of these handlers.
        public bool Authorize(IOSHttpRequest request, IOSHttpResponse response, HttpStatusCode blockedStatus = HttpStatusCode.Forbidden)
        {
            IPEndPoint remote = request.RemoteIPEndPoint;
            string family = EndpointFamily(request.UriPath);

            if (request.Headers["X-SecondLife-Shard"] != null)
            {
                LogRefusal("script|" + remote + "|" + family,
                    string.Format("[CONTROL PLANE ACCESS]: Refusing {0} {1} from {2}: in-world script HTTP requests are never allowed on control-plane endpoints.",
                        request.HttpMethod, family, remote));
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return false;
            }

            if (IsTrustedAddress(remote.Address))
                return true;

            LogRefusal("addr|" + remote.Address + "|" + family,
                string.Format("[CONTROL PLANE ACCESS]: Refusing {0} {1} from {2}: source address is not a trusted control-plane host. If this is one of your own servers (a region, or Robust), add its address to ControlPlaneTrustedHosts.",
                    request.HttpMethod, family, remote.Address));
            response.StatusCode = (int)blockedStatus;
            return false;
        }

        public bool IsTrustedAddress(IPAddress address)
        {
            address = NormalizeAddress(address);
            return IPAddress.IsLoopback(address) || m_trustedHosts.Contains(address);
        }

        // Privileged IM dialogs (a god's forced/silent teleport, and the
        // Confluence-specific dialog 250 - see IsPrivilegedInstantMessageDialog)
        // carry no authentication of their own beyond the sender's claimed
        // caller identity - only ever trust one of these when it actually
        // arrives from another of this grid's own trusted hosts, otherwise
        // any caller could force-teleport or otherwise abuse a target
        // resident by simply crafting the right IM dialog byte. Ordinary
        // IMs (chat, friendship offers, etc.) aren't gated at all here -
        // this only protects the small set of dialogs that carry real
        // privileged side effects.
        public bool AuthorizePrivilegedInstantMessage(byte dialog, IPEndPoint remoteClient)
        {
            if (!IsPrivilegedInstantMessageDialog(dialog))
                return true;

            if (remoteClient != null && IsTrustedAddress(remoteClient.Address))
                return true;

            LogRefusal("im|" + remoteClient?.Address + "|" + dialog,
                string.Format("[CONTROL PLANE ACCESS]: Refusing privileged instant message (dialog {0}) from {1}: source address is not a trusted control-plane host.",
                    dialog, remoteClient?.Address.ToString() ?? "unknown"));
            return false;
        }

        public static bool IsPrivilegedInstantMessageDialog(byte dialog)
        {
            return dialog == 250 || dialog == (byte)InstantMessageDialog.GodLikeRequestTeleport;
        }

        // Deliberately no AuthorizeJsonRpc here: the disclosure's profile
        // JSON-RPC gate would need to apply only to the two read methods
        // that expose email/private prefs (AvatarNotesRequest,
        // UserPreferencesRequest), not the classifieds/picks/notes/
        // properties/interests/preferences/appdata UPDATE methods - those
        // are called directly by residents' own viewers against the
        // region's public port (confirmed via
        // LocalUserProfilesServiceConnector.cs's MainServer.Instance
        // registration), so gating them to trusted-hosts-only would break
        // ordinary profile editing grid-wide. Left as an open item
        // pending a narrower fix - see PROJECT_LOG.md, 2026-09-23.

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
