using System;
using System.Net.Http;
using System.Text;
using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Services.StoreService.Gloebit
{
    // Robust-native Gloebit REST client for the Store feature. Deliberately
    // independent of addon-modules/Gloebit/GloebitMoneyModule - that
    // integration is entirely region-Scene-bound (its OAuth base URL comes
    // from an arbitrary live Scene, its HTTP handlers are registered on the
    // region's own MainServer, and its user IM-based authorize flow needs a
    // live IClientAPI), none of which exists here. This client only
    // implements the three calls the Store checkout flow actually needs:
    // build the authorize redirect, exchange the OAuth2 code, and submit a
    // transact request. Reuses the same GLBKey/GLBSecret/GLBEnvironment
    // values already configured for the grid's region-side Gloebit
    // integration (see [Gloebit] in Robust.HG.ini) so a resident's Gloebit
    // account is the same real account either way - but tracks
    // authorization/transaction state in this feature's own tables
    // (store_gloebit_auth/store_gloebit_transactions), not the region
    // module's GloebitUsers/GloebitTransactions, to avoid any dependency on
    // that addon's assembly or schema.
    public class GloebitClient
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string m_key;
        private readonly string m_secret;
        private readonly string m_apiUrl;
        private readonly string m_callbackBaseUri;

        public GloebitClient(string key, string secret, string apiUrl, string callbackBaseUri)
        {
            m_key = key;
            m_secret = secret;
            m_apiUrl = apiUrl.EndsWith("/") ? apiUrl : apiUrl + "/";
            m_callbackBaseUri = callbackBaseUri.TrimEnd('/');
        }

        private string AuthCompleteCallbackUrl(UUID avatarId)
        {
            return m_callbackBaseUri + "/store/gloebit/auth_complete?agentId=" + avatarId;
        }

        public Uri BuildAuthorizeUri(UUID avatarId, string userName)
        {
            string url = m_apiUrl + "oauth2/authorize"
                + "?client_id=" + Uri.EscapeDataString(m_key)
                + "&scope=" + Uri.EscapeDataString("balance transact")
                + "&redirect_uri=" + Uri.EscapeDataString(AuthCompleteCallbackUrl(avatarId))
                + "&response_type=code"
                + "&user=" + Uri.EscapeDataString(userName ?? string.Empty)
                + "&uid=" + Uri.EscapeDataString(avatarId.ToString());
            return new Uri(url);
        }

        // Exchanges an OAuth2 "code" (from the auth_complete callback query
        // string) for a bearer access token. Synchronous - callers are
        // already on a request-handling thread with no async pipeline of
        // their own in this codebase's WebInterface connector.
        public bool ExchangeAccessToken(UUID avatarId, string code, out string accessToken, out string gloebitId, out string error)
        {
            accessToken = null;
            gloebitId = null;
            error = null;

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);

                    var body = new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "client_id", m_key },
                        { "client_secret", m_secret },
                        { "code", code },
                        { "grant_type", "authorization_code" },
                        { "scope", "balance transact" },
                        { "redirect_uri", AuthCompleteCallbackUrl(avatarId) }
                    };

                    HttpResponseMessage result = client.PostAsync(m_apiUrl + "oauth2/access-token", new FormUrlEncodedContent(body))
                        .GetAwaiter().GetResult();
                    string responseText = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!result.IsSuccessStatusCode)
                    {
                        error = "Gloebit responded with HTTP " + (int)result.StatusCode + ": " + responseText;
                        return false;
                    }

                    OSDMap response = OSDParser.DeserializeJson(responseText) as OSDMap;
                    if (response == null || !response.ContainsKey("access_token"))
                    {
                        error = "Gloebit token exchange response did not include an access token.";
                        return false;
                    }

                    accessToken = response["access_token"].AsString();
                    gloebitId = response.ContainsKey("app_user_id") ? response["app_user_id"].AsString() : string.Empty;
                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.Error("[GLOEBIT CLIENT]: Access token exchange failed", e);
                error = e.Message;
                return false;
            }
        }

        // Submits a "charge this avatar N Gloebits" request. Returns true
        // only if Gloebit accepted/queued the request - actual success is
        // reported later, asynchronously, via the /store/gloebit/transaction
        // webhook (see the plan's Gloebit integration section - this is not
        // optional, Gloebit's queue processor calls back to that URL to
        // drive the local enact/consume/cancel lifecycle).
        public bool Transact(UUID transactionId, UUID payerAvatarId, string accessToken, string gloebitId, int amount, string description, string userName, out string error)
        {
            error = null;

            try
            {
                OSDMap body = new OSDMap
                {
                    ["version"] = 1,
                    ["application-key"] = m_key,
                    ["request-created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["username-on-application"] = userName ?? string.Empty,
                    ["transaction-id"] = transactionId.ToString(),
                    ["buyer-id-on-application"] = payerAvatarId.ToString(),
                    ["app-user-id"] = gloebitId ?? string.Empty,
                    ["gloebit-balance-change"] = amount,
                    ["asset-code"] = description ?? string.Empty,
                    ["asset-quantity"] = 1,
                    ["asset-enact-hold-url"] = BuildTransactionCallbackUrl(transactionId, "enact"),
                    ["asset-consume-hold-url"] = BuildTransactionCallbackUrl(transactionId, "consume"),
                    ["asset-cancel-hold-url"] = BuildTransactionCallbackUrl(transactionId, "cancel")
                };

                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + accessToken);

                    StringContent content = new StringContent(OSDParser.SerializeJsonString(body), Encoding.UTF8, "application/json");
                    HttpResponseMessage result = client.PostAsync(m_apiUrl + "v2/transact", content).GetAwaiter().GetResult();
                    string responseText = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!result.IsSuccessStatusCode)
                    {
                        error = "Gloebit responded with HTTP " + (int)result.StatusCode + ": " + responseText;
                        return false;
                    }

                    OSDMap response = OSDParser.DeserializeJson(responseText) as OSDMap;
                    bool success = response != null && response.ContainsKey("success") && response["success"].AsBoolean();
                    if (!success)
                    {
                        error = response != null && response.ContainsKey("reason") ? response["reason"].AsString() : "Gloebit rejected the transaction.";
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                m_log.Error("[GLOEBIT CLIENT]: Transact submission failed", e);
                error = e.Message;
                return false;
            }
        }

        private string BuildTransactionCallbackUrl(UUID transactionId, string state)
        {
            return m_callbackBaseUri + "/store/gloebit/transaction?id=" + transactionId + "&state=" + state;
        }
    }
}
