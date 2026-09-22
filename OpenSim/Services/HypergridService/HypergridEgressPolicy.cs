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
using System.Net;
using System.Net.Sockets;

namespace OpenSim.Services.HypergridService
{
    // Hypergrid travel and verification callbacks both take a caller-
    // supplied destination/HomeURI and make an outbound HTTP call to it
    // (UserAgentServiceConnector, GatekeeperService's verification
    // callback) - a caller who controls that URL can point it at this
    // grid's own internal-only services (loopback, LAN, link-local,
    // cloud-metadata addresses) and use this grid as an SSRF proxy into
    // its own network. Genuine Hypergrid travel is always to a real,
    // publicly-routable grid by definition, so blocking non-routable
    // targets costs nothing for legitimate use. The one exception is a
    // target that resolves back to THIS grid's own gatekeeper - always
    // allowed, since that's simply "stay home," not an egress at all.
    public static class HypergridEgressPolicy
    {
        public static bool IsAllowedTarget(string targetUri, string localGatewayUri = null)
        {
            if (UserAgentService.IsLocalGridURI(localGatewayUri, targetUri))
                return true;

            if (!Uri.TryCreate(targetUri, UriKind.Absolute, out Uri uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
            }
            catch
            {
                return false;
            }

            if (addresses.Length == 0)
                return false;

            foreach (IPAddress address in addresses)
            {
                if (!IsAllowedAddress(address))
                    return false;
            }

            return true;
        }

        public static bool IsAllowedAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return false;

            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
                return IsAllowedIPv4(bytes);

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return IsAllowedIPv6(bytes);

            return false;
        }

        private static bool IsAllowedIPv4(byte[] bytes)
        {
            // 0.0.0.0/8, 10.0.0.0/8, 127.0.0.0/8
            if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127)
                return false;
            // 100.64.0.0/10 (shared/CGNAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return false;
            // 169.254.0.0/16 (link-local, includes 169.254.169.254 cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254)
                return false;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return false;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return false;
            // 198.18.0.0/15 (benchmarking)
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                return false;
            // 224.0.0.0/4 (multicast) and above (reserved)
            if (bytes[0] >= 224)
                return false;

            return true;
        }

        private static bool IsAllowedIPv6(byte[] bytes)
        {
            // fe80::/10 (link-local)
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                return false;
            // fc00::/7 (unique local)
            if ((bytes[0] & 0xfe) == 0xfc)
                return false;
            // ff00::/8 (multicast)
            if (bytes[0] == 0xff)
                return false;

            return true;
        }
    }
}
