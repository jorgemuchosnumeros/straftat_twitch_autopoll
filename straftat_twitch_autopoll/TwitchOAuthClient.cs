using System;
using System.Collections;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace straftat_twitch_autopoll;

public class TwitchOAuthClient
{
    public const string TwitchClientId = "t1tug446aluj8yvplzs9n0mhw3xo5n";
    public const string TwitchApplicationScopes = "channel:read:predictions channel:manage:predictions";

    public string AccessToken;
    public string RefreshToken;
    public DateTimeOffset AccessTokenExpiresAt = DateTimeOffset.MinValue;
    public string BroadcasterId;
    public string BroadcasterType;
    public bool IsAffiliateOrPartner;

    private MonoBehaviour host;
    private readonly ManualLogSource logger;
    private readonly Func<bool> isPredictionActive;
    private Coroutine refreshTokenCoroutine;
    private bool refreshInProgress;
    private bool oauthStarted;

    public static TwitchOAuthClient Instance;
    private static OAuthCoroutineHost sharedHost;

    public static TwitchOAuthClient Ensure(ManualLogSource logger = null, Func<bool> isPredictionActive = null)
    {
        if (Instance != null)
        {
            if (Instance.host == null)
                Instance.host = EnsureHost();
            return Instance;
        }

        var ensuredHost = EnsureHost();

        var logSource = logger ?? BepInEx.Logging.Logger.CreateLogSource("TwitchOAuth");
        var predictionActive = isPredictionActive ?? (() => false);
        Instance = new TwitchOAuthClient(ensuredHost, logSource, predictionActive);
        return Instance;
    }

    private static OAuthCoroutineHost EnsureHost()
    {
        if (sharedHost == null)
        {
            var go = new GameObject("TwitchOAuthHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            sharedHost = go.AddComponent<OAuthCoroutineHost>();
        }

        return sharedHost;
    }

    public TwitchOAuthClient(MonoBehaviour host, ManualLogSource logger, Func<bool> isPredictionActive)
    {
        this.host = host;
        this.logger = logger;
        this.isPredictionActive = isPredictionActive;
    }

    public void StartOAuth()
    {
        if (host == null)
            host = EnsureHost();

        if (oauthStarted)
            return;
        oauthStarted = true;
        host.StartCoroutine(RequestDeviceCode());
    }

    private IEnumerator RequestDeviceCode()
    {
        WWWForm form = new WWWForm();
        form.AddField("client_id", TwitchClientId);
        form.AddField("scopes", TwitchApplicationScopes);

        Plugin.LogInfo("Attempting Twitch OAuth", true, "purple");

        using UnityWebRequest req = UnityWebRequest.Post("https://id.twitch.tv/oauth2/device", form);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Plugin.LogInfo("Opening Twitch OAuth Browser", true, "purple");
            Plugin.LogInfo(req.downloadHandler.text);

            var twitchDcfStartResponse = JsonConvert.DeserializeObject<TwitchDcfStartResponse>(req.downloadHandler.text);
            Application.OpenURL(twitchDcfStartResponse.verification_uri);

            host.StartCoroutine(OAuthWatchdog(twitchDcfStartResponse.interval, twitchDcfStartResponse.device_code));
        }
        else
        {
            Plugin.LogError(req.error, true);
        }
    }

    private IEnumerator OAuthWatchdog(int intervalSeconds, string deviceCode)
    {
        int pollInterval = intervalSeconds;

        while (true)
        {
            WWWForm form = new WWWForm();
            form.AddField("client_id", TwitchClientId);
            form.AddField("device_code", deviceCode);
            form.AddField("grant_type", "urn:ietf:params:oauth:grant-type:device_code");

            using UnityWebRequest req = UnityWebRequest.Post("https://id.twitch.tv/oauth2/token", form);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var twitchTokenResponse = JsonConvert.DeserializeObject<TwitchTokenResponse>(req.downloadHandler.text);
                AccessToken = twitchTokenResponse.access_token;
                RefreshToken = twitchTokenResponse.refresh_token;
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(twitchTokenResponse.expires_in);
                
                Plugin.LogInfo("Twitch OAuth device flow completed!", true, "green");

                host.StartCoroutine(ValidateTokenAndStoreUser());
                StartTokenRefreshLoop();
                yield break;
            }

            var errorText = req.downloadHandler != null ? req.downloadHandler.text : string.Empty;
            var errorResponse = JsonConvert.DeserializeObject<TwitchOAuthErrorResponse>(errorText);
            var errorCode = errorResponse != null ? errorResponse.error : string.Empty;
            if (string.IsNullOrWhiteSpace(errorCode) && !string.IsNullOrWhiteSpace(errorText))
            {
                if (errorText.Contains("authorization_pending"))
                    errorCode = "authorization_pending";
                else if (errorText.Contains("access_denied"))
                    errorCode = "access_denied";
                else if (errorText.Contains("expired_token"))
                    errorCode = "expired_token";
            }

            if (errorCode == "authorization_pending")
            {
                var warningString = "Twitch OAuth device flow: authorization pending request";
                Plugin.LogWarning(warningString, true);
            }
            else if (errorCode == "access_denied" || errorCode == "expired_token")
            {
                var errorString = $"Twitch OAuth device flow failed: {errorCode} {errorResponse?.message}";
                Plugin.LogError(errorString, true);
                oauthStarted = false;
                yield break;
            }
            else
            {
                var errorString = $"Twitch OAuth device flow error: {errorCode} {errorResponse?.message}";
                Plugin.LogError(errorString, true);
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }

    private void StartTokenRefreshLoop()
    {
        if (refreshTokenCoroutine != null)
            host.StopCoroutine(refreshTokenCoroutine);
        refreshTokenCoroutine = host.StartCoroutine(RefreshTokenLoop());
    }

    private IEnumerator RefreshTokenLoop()
    {
        while (true)
        {
            if (string.IsNullOrWhiteSpace(RefreshToken))
            {
                yield return new WaitForSeconds(10f);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var secondsUntilExpiry = (AccessTokenExpiresAt - now).TotalSeconds;
            if (secondsUntilExpiry <= 1800)
            {
                if (isPredictionActive())
                {
                    Plugin.LogWarning("Access token near expiry but prediction in progress; delaying refresh.", true);
                    yield return new WaitForSeconds(30f);
                    continue;
                }

                if (!refreshInProgress)
                    yield return host.StartCoroutine(RefreshAccessToken());
            }

            var waitSeconds = Math.Max(30, secondsUntilExpiry - 1800);
            yield return new WaitForSeconds((float)waitSeconds);
        }
    }

    private IEnumerator RefreshAccessToken()
    {
        refreshInProgress = true;
        try
        {
            WWWForm form = new WWWForm();
            form.AddField("client_id", TwitchClientId);
            form.AddField("grant_type", "refresh_token");
            form.AddField("refresh_token", RefreshToken);

            using UnityWebRequest req = UnityWebRequest.Post("https://id.twitch.tv/oauth2/token", form);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var twitchTokenResponse = JsonConvert.DeserializeObject<TwitchTokenResponse>(req.downloadHandler.text);
                AccessToken = twitchTokenResponse.access_token;
                RefreshToken = twitchTokenResponse.refresh_token;
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(twitchTokenResponse.expires_in);
                Plugin.LogInfo("Twitch access token refreshed.", true);
                if (string.IsNullOrWhiteSpace(BroadcasterId))
                    host.StartCoroutine(ValidateTokenAndStoreUser());
                yield break;
            }

            var errorText = req.downloadHandler != null ? req.downloadHandler.text : string.Empty;
            var errorResponse = JsonConvert.DeserializeObject<TwitchOAuthErrorResponse>(errorText);
            Plugin.LogError($"Twitch token refresh failed: {errorResponse?.error} {errorResponse?.message}", true);
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private IEnumerator ValidateTokenAndStoreUser()
    {
        using UnityWebRequest req = UnityWebRequest.Get("https://id.twitch.tv/oauth2/validate");
        req.SetRequestHeader("Authorization", $"Bearer {AccessToken}");
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var response = JsonConvert.DeserializeObject<TwitchValidateResponse>(req.downloadHandler.text);
            if (response != null && !string.IsNullOrWhiteSpace(response.user_id))
            {
                BroadcasterId = response.user_id;
                Plugin.LogInfo($"Twitch OAuth validated. Broadcaster id: {BroadcasterId}", true);
                host.StartCoroutine(FetchBroadcasterType(BroadcasterId));
            }
            else
            {
                Plugin.LogWarning("Twitch OAuth validate succeeded but user_id was missing.", true);
            }
            yield break;
        }

        Plugin.LogError($"Twitch OAuth validate failed: {req.responseCode} {req.error}", true);
    }

    private IEnumerator FetchBroadcasterType(string userId)
    {
        using UnityWebRequest req = UnityWebRequest.Get($"https://api.twitch.tv/helix/users?id={userId}");
        req.SetRequestHeader("Authorization", $"Bearer {AccessToken}");
        req.SetRequestHeader("Client-Id", TwitchClientId);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var raw = req.downloadHandler.text;
            BroadcasterType = "ERROR_PARSING_BROADCASTER_TYPE";
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var match = Regex.Match(raw, "\"broadcaster_type\"\\s*:\\s*\"([^\"]*)\"");
                if (match.Success)
                    BroadcasterType = match.Groups[1].Value;
            }
            
            Plugin.Logger.LogInfo(BroadcasterType);
            
            IsAffiliateOrPartner = BroadcasterType != "";

            if (IsAffiliateOrPartner)
                Plugin.LogInfo($"Twitch broadcaster type: {BroadcasterType}", true);
            else
                Plugin.LogWarning("Twitch account is not affiliate/partner. Predictions will be disabled.", true);

            yield break;
        }

        Plugin.LogError($"Twitch get users failed: {req.responseCode} {req.error}", true);
    }
}

public class OAuthCoroutineHost : MonoBehaviour { }

[Serializable]
public class TwitchDcfStartResponse
{
    public string device_code;
    public string user_code;
    public string verification_uri;
    public int expires_in;
    public int interval;
}

[Serializable]
public class TwitchTokenResponse
{
    public string access_token;
    public int expires_in;
    public string refresh_token;
}

[Serializable]
public class TwitchOAuthErrorResponse
{
    public string error;
    public string message;
}

[Serializable]
public class TwitchValidateResponse
{
    public string client_id;
    public string login;
    public string[] scopes;
    public string user_id;
    public int expires_in;
}
