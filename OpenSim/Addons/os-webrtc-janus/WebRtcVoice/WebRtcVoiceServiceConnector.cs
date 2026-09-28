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
using System.Reflection;
using System.Threading;

using OpenSim.Framework;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using log4net;
using Nini.Config;

namespace osWebRtcVoice
{
    // Class that provides the local IWebRtcVoiceService interface to the JsonRPC Robust
    //     server. This is used by the region servers to talk to the Robust server.
    public class WebRtcVoiceServiceConnector : IWebRtcVoiceService
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[WEBRTC VOICE SERVICE CONNECTOR]";
        private readonly bool m_Enabled = false;
        private readonly bool m_MessageDetails = false;
        private IConfigSource m_Config;

        string m_serverURI = "http://localhost:8080";

        public WebRtcVoiceServiceConnector(IConfigSource pConfig)
        {
            m_Config = pConfig;
            IConfig moduleConfig = m_Config.Configs["WebRtcVoice"];

            if (moduleConfig is not null)
            {
                m_Enabled = moduleConfig.GetBoolean("Enabled", false);
                if (m_Enabled)
                {
                    m_serverURI = moduleConfig.GetString("WebRtcVoiceServerURI", string.Empty);
                    if (string.IsNullOrWhiteSpace(m_serverURI))
                    {
                        m_log.Error($"{LogHeader} WebRtcVoiceServiceConnector enabled but no WebRtcVoiceServerURI specified");
                        m_Enabled = false;
                        return;
                    }

                    m_MessageDetails = moduleConfig.GetBoolean("MessageDetails", false);

                    m_log.Info($"{LogHeader} WebRtcVoiceServiceConnector enabled");
                }
            }
        }

        // Create a local viewer session. This gets a local viewer session ID that is
        //    later changed when the ProvisionVoiceAccountRequest response is returned
        //    so that the viewer session ID is the same here as from the WebRTC service.
        public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            m_log.Debug($"{LogHeader} CreateViewerSession");
            return new VoiceViewerSession(this, pSceneID, pUserID);
        }

        public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            m_log.Debug($"{LogHeader} ProvisionVoiceAccountRequest without ViewerSession. uID={pUserID}, sID={pSceneID}");
            return null;
        }

        // Received a ProvisionVoiceAccountRequest from a viewer. Forward it to the WebRTC service.
        public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            m_log.Debug($"{LogHeader} ProvisionVoiceAccountRequest. uID={pUserID}, sID={pSceneID}");
            OSDMap req = new()
            {
                { "request", pRequest },
                { "userID", pUserID.ToString() },
                { "scene", pSceneID.ToString() }
            };
            OSDMap resp = JsonRpcRequest("provision_voice_account_request", m_serverURI, req);

            // Kludge to sync the viewer session number in our IVoiceViewerSession with the one from the WebRTC service.
            if (resp.TryGetString("viewer_session", out string otherViewerSessionId))
            {
                m_log.Debug(
                    $"{LogHeader} ProvisionVoiceAccountRequest: syncing viewSessionID. old={pVSession.ViewerSessionID}, new={otherViewerSessionId}");
                VoiceViewerSession.UpdateViewerSessionId(pVSession, otherViewerSessionId);
            }

            return resp;
        }

        public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            m_log.Debug($"{LogHeader} VoiceSignalingRequest without ViewerSession. uID={pUserID}, sID={pSceneID}");
            return null;
        }

        public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pSceneID)
        {
            m_log.Debug($"{LogHeader} VoiceSignalingRequest. uID={pUserID}, sID={pSceneID}");
            OSDMap req = new()
            {
                { "request", pRequest },
                { "userID", pUserID.ToString() },
                { "scene", pSceneID.ToString() }
            };
            return JsonRpcRequest("voice_signaling_request", m_serverURI, req);
        }

        // Firestorm's WebRTC voice client does not retry a failed ProvisionVoiceAccountRequest
        // on its own: one failed attempt permanently fails voice for that region until the
        // viewer relogs or teleports away and back (confirmed live, 2026-09-28 -- a single
        // "connection actively refused" against an otherwise healthy, 29-minutes-stable
        // ConfluenceVoice was enough to leave a viewer with no voice in Sandbox while a second
        // viewer connected normally seconds later). Retrying a genuine transport failure here,
        // cheaply and fast, means the viewer often never sees it happen at all.
        private const int MaxJsonRpcAttempts = 3;
        private const int JsonRpcRetryDelayMs = 300;

        public OSDMap JsonRpcRequest(string method, string uri, OSDMap pParams)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return null;

            OSDMap lastError = null;
            for (int attempt = 1; attempt <= MaxJsonRpcAttempts; attempt++)
            {
                (OSDMap result, OSDMap transportError) = TryJsonRpcRequest(method, uri, pParams);
                if (result is not null)
                    return result;

                lastError = transportError;
                if (attempt < MaxJsonRpcAttempts)
                {
                    m_log.Warn(
                        $"{LogHeader}: JsonRpc request '{method}' to {uri} failed (attempt {attempt}/{MaxJsonRpcAttempts}), " +
                        $"retrying: {OSDParser.SerializeJsonString(transportError)}");
                    Thread.Sleep(JsonRpcRetryDelayMs);
                }
            }

            m_log.Error($"{LogHeader}: JsonRpc request '{method}' to {uri} failed after {MaxJsonRpcAttempts} attempts");
            return lastError;
        }

        // One attempt at the JSON-RPC round trip.
        //
        // Returns (result, null) on success -- including a genuine application-level
        // {"error": ...} response from the WebRTC voice service itself. That is NOT retried:
        // retrying an at-capacity rejection or a real validation error fixes nothing and can
        // only cause confusing duplicate side effects (e.g. a second session created for the
        // same logout).
        //
        // Returns (null, errorMap) only when the round trip itself did not complete -- a thrown
        // exception, an empty response, or PostToService's own failure shape (it reports a
        // connection failure as a normal-looking map rather than throwing, which is why the
        // "invalid response" branch below is the one that actually caught the live incident
        // this retry exists for, not the catch block). That is exactly the case worth retrying.
        private (OSDMap, OSDMap) TryJsonRpcRequest(string method, string uri, OSDMap pParams)
        {
            string jsonId = UUID.Random().ToString();
            OSDMap request = new()
            {
                { "jsonrpc", OSD.FromString("2.0") },
                { "id", OSD.FromString(jsonId) },
                { "method", OSD.FromString(method) },
                { "params", pParams }
            };

            OSDMap outerResponse;
            try
            {
                if (m_MessageDetails) m_log.Debug($"{LogHeader}: request: {request}");

                outerResponse = WebUtil.PostToService(uri, request, 10000, true);

                if (m_MessageDetails) m_log.Debug($"{LogHeader}: response: {outerResponse}");
            }
            catch (Exception e)
            {
                m_log.Debug($"{LogHeader}: request: {request}");
                return (null, new OSDMap() { { "error", OSD.FromString(e.Message) } });
            }

            if (outerResponse is null || outerResponse.Count == 0)
            {
                return (null, new OSDMap()
                {
                    { "error", $"JsonRpc request '{method}' to {uri} returned an empty response" }
                });
            }

            if (!outerResponse.TryGetOSDMap("_Result", out OSDMap response))
            {
                return (null, new OSDMap()
                {
                    { "error", $"JsonRpc request '{method}' to {uri} returned an invalid response: {OSDParser.SerializeJsonString(outerResponse)}" }
                });
            }

            if (response.TryGetValue("error", out OSD osdtmp))
            {
                string errm = $"JsonRpc request '{method}' to {uri} returned an error: {OSDParser.SerializeJsonString(osdtmp)}";
                m_log.Error(errm);
                return (new OSDMap() { { "error", errm } }, null);
            }

            if (!response.TryGetOSDMap("result", out OSDMap resultmap))
            {
                return (null, new OSDMap()
                {
                    { "error", $"JsonRpc request '{method}' to {uri} returned result as non-OSDMap: {OSDParser.SerializeJsonString(outerResponse)}" }
                });
            }

            return (resultmap, null);
        }
    }
}