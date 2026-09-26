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
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    /// <summary>
    /// What Robust says about one region's concierge: the texts the estate
    /// owner set in the web portal and the per-region switches. A flag left
    /// null means "nobody set it - use the module's own default".
    /// </summary>
    public sealed class ConciergeContent
    {
        public string Welcome = string.Empty;
        public string Rules = string.Empty;
        public string Grid = string.Empty;
        public bool? Enabled;
        public bool? Announce;
        public bool? Notify;
    }

    /// <summary>
    /// Reads per-region concierge content from Robust and caches it, so an
    /// estate owner's edit in the web portal reaches the region within the
    /// cache lifetime, with no restart. Never throws: if Robust cannot be
    /// reached the last good copy (or an empty one) is used and the region
    /// carries on with its ini defaults.
    /// </summary>
    internal sealed class ConciergeContentClient
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly HttpClient s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // After a failed fetch, wait this long before asking again so a
        // stopped Robust cannot turn every arrival into a slow request.
        private const int RetryAfterFailureMs = 15_000;
        private const long WarnIntervalMs = 300_000;

        private sealed class Entry
        {
            public ConciergeContent Content = new ConciergeContent();
            public long ValidUntilMs;
        }

        private readonly string m_baseUri;
        private readonly int m_ttlMs;
        private readonly ConcurrentDictionary<UUID, Entry> m_cache = new ConcurrentDictionary<UUID, Entry>();
        private long m_lastWarnMs;

        public ConciergeContentClient(string baseUri, int cacheSeconds)
        {
            m_baseUri = (baseUri ?? string.Empty).Trim().TrimEnd('/');
            m_ttlMs = Math.Max(5, cacheSeconds) * 1000;
        }

        public bool IsConfigured => m_baseUri.Length > 0;

        /// <summary>Last known content for a region; never null, never blocks.</summary>
        public ConciergeContent Peek(UUID regionID)
        {
            return m_cache.TryGetValue(regionID, out Entry e) ? e.Content : new ConciergeContent();
        }

        public async Task<ConciergeContent> GetAsync(UUID regionID)
        {
            if (!IsConfigured)
                return new ConciergeContent();

            long now = Environment.TickCount64;
            if (m_cache.TryGetValue(regionID, out Entry cached) && now < cached.ValidUntilMs)
                return cached.Content;

            Entry entry = cached ?? new Entry();
            try
            {
                string json = await s_http.GetStringAsync(m_baseUri + "/concierge/" + regionID).ConfigureAwait(false);
                entry = new Entry { Content = Parse(json), ValidUntilMs = Environment.TickCount64 + m_ttlMs };
            }
            catch (Exception e)
            {
                entry.ValidUntilMs = Environment.TickCount64 + RetryAfterFailureMs;
                Warn(e.Message);
            }

            m_cache[regionID] = entry;
            return entry.Content;
        }

        public static ConciergeContent Parse(string json)
        {
            var content = new ConciergeContent();
            if (string.IsNullOrWhiteSpace(json))
                return content;

            if (OSDParser.DeserializeJson(json) is not OSDMap map)
                return content;

            content.Welcome = Str(map, "welcome");
            content.Rules = Str(map, "rules");
            content.Grid = Str(map, "grid");
            content.Enabled = Flag(map, "enabled");
            content.Announce = Flag(map, "announce");
            content.Notify = Flag(map, "notify");
            return content;
        }

        private static string Str(OSDMap map, string key)
        {
            return map.TryGetValue(key, out OSD v) && v is not null ? v.AsString() : string.Empty;
        }

        private static bool? Flag(OSDMap map, string key)
        {
            if (!map.TryGetValue(key, out OSD v) || v is null)
                return null;

            string s = v.AsString();
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;
            return null;
        }

        private void Warn(string reason)
        {
            long now = Environment.TickCount64;
            if (now - m_lastWarnMs < WarnIntervalMs && m_lastWarnMs != 0)
                return;

            m_lastWarnMs = now;
            m_log.WarnFormat("[Concierge]: could not read region content from {0} ({1}); using the last known copy and ini defaults",
                m_baseUri, reason);
        }
    }
}
