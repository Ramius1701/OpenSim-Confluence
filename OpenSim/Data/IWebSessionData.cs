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

using OpenSim.Framework;

namespace OpenSim.Data
{
    // Backing store for the built-in WebUI's login sessions
    // (WebInterfaceServiceConnector.cs). Previously in-memory only, a
    // deliberate, documented tradeoff at the time - but that meant every
    // Robust restart force-logged-out every open browser session with no
    // warning, confirmed a real, repeated annoyance during a single day
    // of heavy redeployment (2026-09-13). This lets a session survive a
    // restart: the connector keeps its own in-memory dictionary as a
    // write-through cache for the common case (no DB round-trip per
    // page load), falling back to this store on a cache miss - which
    // only really happens right after a fresh Robust start.
    //
    // WebSessionRecord itself lives in OpenSim.Framework, not here -
    // OpenSim.Data only references OpenSim.Framework (confirmed via its
    // own .csproj), not OpenSim.Services.Interfaces, so a type shared
    // between IWebSessionData here and IWebSessionService there has to
    // live somewhere both can reach.
    public interface IWebSessionData
    {
        WebSessionRecord Get(string token);
        bool Store(WebSessionRecord session);
        bool Delete(string token);
    }
}
