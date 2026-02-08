using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace straftat_twitch_autopoll;

public class TwitchPredictionsClient
{
    private const string PredictionsUrl = "https://api.twitch.tv/helix/predictions";

    private readonly TwitchOAuthClient oauthClient;
    private readonly ManualLogSource logger;
    private string broadcasterId;
    private readonly int predictionWindowSeconds;
    private readonly string predictionTitle;
    private MonoBehaviour host;

    private string currentPredictionId;
    private readonly Dictionary<int, string> outcomeIdByTeamId = new();

    public TwitchPredictionsClient(
        TwitchOAuthClient oauthClient,
        ManualLogSource logger,
        string broadcasterId,
        int predictionWindowSeconds,
        string predictionTitle)
    {
        this.oauthClient = oauthClient;
        this.logger = logger;
        this.broadcasterId = broadcasterId;
        this.predictionWindowSeconds = predictionWindowSeconds;
        this.predictionTitle = predictionTitle;
        host = EnsureHost();
    }

    private static PredictionsCoroutineHost EnsureHost()
    {
        return PredictionsCoroutineHost.Ensure();
    }

    public Coroutine CreatePrediction(List<TwitchPredictionOutcomeSpec> outcomes)
    {
        if (host == null)
            host = EnsureHost();
        return host.StartCoroutine(CreatePredictionCoroutine(outcomes));
    }

    public Coroutine CancelPrediction()
    {
        if (host == null)
            host = EnsureHost();
        return host.StartCoroutine(EndPredictionCoroutine("CANCELED", null));
    }

    public Coroutine ResolvePrediction(int winningTeamId)
    {
        if (host == null)
            host = EnsureHost();
        return host.StartCoroutine(ResolvePredictionCoroutine(winningTeamId));
    }

    private IEnumerator CreatePredictionCoroutine(List<TwitchPredictionOutcomeSpec> outcomes)
    {
        if (!EnsureReady())
            yield break;

        if (outcomes == null || outcomes.Count == 0)
        {
            Plugin.LogError("Twitch prediction create failed: no outcomes provided.", true);
            yield break;
        }

        var outcomeRequests = outcomes
            .Select(kvp => new TwitchPredictionOutcomeRequest { title = kvp.title })
            .ToArray();
        Plugin.LogInfo($"Twitch prediction outcomes count: {outcomeRequests.Length}", true, "gray");

        var requestBody = new TwitchPredictionCreateRequest
        {
            broadcaster_id = broadcasterId,
            title = predictionTitle,
            outcomes = outcomeRequests,
            prediction_window = predictionWindowSeconds
        };

        var json = JsonConvert.SerializeObject(requestBody);
        using var req = BuildJsonRequest(PredictionsUrl, "POST", json);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var response = JsonConvert.DeserializeObject<TwitchPredictionResponse>(req.downloadHandler.text);
            var prediction = response != null && response.data != null ? response.data.FirstOrDefault() : null;
            if (prediction == null)
            {
                Plugin.LogError("Twitch prediction create failed: empty response data.", true);
                yield break;
            }

            currentPredictionId = prediction.id;
            outcomeIdByTeamId.Clear();
            foreach (var kvp in outcomes)
            {
                var outcome = prediction.outcomes.FirstOrDefault(o => o.title == kvp.title);
                if (outcome != null)
                    outcomeIdByTeamId[kvp.teamId] = outcome.id;
            }

            Plugin.LogInfo($"Twitch prediction created: {currentPredictionId}", true);
            yield break;
        }

        Plugin.LogError($"Twitch prediction create failed: {req.responseCode} {req.error}", true);
        if (req.downloadHandler != null && !string.IsNullOrWhiteSpace(req.downloadHandler.text))
            Plugin.LogError($"Twitch prediction error body: {req.downloadHandler.text}", true, "gray");
    }

    private IEnumerator ResolvePredictionCoroutine(int winningTeamId)
    {
        if (!EnsureReady())
            yield break;

        if (string.IsNullOrWhiteSpace(currentPredictionId))
        {
            Plugin.LogError("Twitch prediction resolve failed: no active prediction id.", true);
            yield break;
        }

        if (!outcomeIdByTeamId.TryGetValue(winningTeamId, out var winningOutcomeId))
        {
            Plugin.LogError($"Twitch prediction resolve failed: no outcome id for team {winningTeamId}.", true);
            yield break;
        }

        yield return EndPredictionCoroutine("RESOLVED", winningOutcomeId);
    }

    private IEnumerator EndPredictionCoroutine(string status, string winningOutcomeId)
    {
        if (!EnsureReady())
            yield break;

        if (string.IsNullOrWhiteSpace(currentPredictionId))
        {
            Plugin.LogError("Twitch prediction end failed: no active prediction id.", true);
            yield break;
        }

        var requestBody = new TwitchPredictionEndRequest
        {
            broadcaster_id = broadcasterId,
            id = currentPredictionId,
            status = status,
            winning_outcome_id = winningOutcomeId
        };

        var json = JsonConvert.SerializeObject(requestBody);
        using var req = BuildJsonRequest(PredictionsUrl, "PATCH", json);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Plugin.LogInfo($"Twitch prediction {status.ToLowerInvariant()}: {currentPredictionId}", true);
            if (status == "CANCELED" || status == "RESOLVED")
            {
                currentPredictionId = null;
                outcomeIdByTeamId.Clear();
            }
            yield break;
        }

        Plugin.LogError($"Twitch prediction {status.ToLowerInvariant()} failed: {req.responseCode} {req.error}", true);
    }

    private bool EnsureReady()
    {
        if (string.IsNullOrWhiteSpace(broadcasterId) || broadcasterId == "REPLACE_WITH_BROADCASTER_ID")
        {
            if (!string.IsNullOrWhiteSpace(oauthClient?.BroadcasterId))
            {
                broadcasterId = oauthClient.BroadcasterId;
            }
            else
            {
                Plugin.LogError("Twitch predictions: broadcaster id is not set.", true);
                return false;
            }
        }

        if (oauthClient == null || string.IsNullOrWhiteSpace(oauthClient.AccessToken))
        {
            Plugin.LogError("Twitch predictions: access token is missing.", true);
            return false;
        }

        if (!oauthClient.IsAffiliateOrPartner)
        {
            Plugin.LogWarning("Twitch predictions: broadcaster not affiliate/partner.", true);
            return false;
        }

        if (oauthClient.AccessTokenExpiresAt != DateTimeOffset.MinValue &&
            DateTimeOffset.UtcNow >= oauthClient.AccessTokenExpiresAt)
        {
            Plugin.LogError("Twitch predictions: access token is expired.", true);
            return false;
        }

        return true;
    }

    private UnityWebRequest BuildJsonRequest(string url, string method, string json)
    {
        var req = new UnityWebRequest(url, method);
        var bodyRaw = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Client-Id", TwitchOAuthClient.TwitchClientId);
        req.SetRequestHeader("Authorization", $"Bearer {oauthClient.AccessToken}");
        return req;
    }
}

public class PredictionsCoroutineHost : MonoBehaviour
{
    private static PredictionsCoroutineHost instance;

    public static PredictionsCoroutineHost Ensure()
    {
        if (instance != null)
            return instance;

        var go = new GameObject("TwitchPredictionsHost");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<PredictionsCoroutineHost>();
        return instance;
    }
}

[Serializable]
public class TwitchPredictionOutcomeRequest
{
    public string title;
}

public class TwitchPredictionCreateRequest
{
    public string broadcaster_id;
    public string title;
    public TwitchPredictionOutcomeRequest[] outcomes;
    public int prediction_window;
}

[Serializable]
public class TwitchPredictionEndRequest
{
    public string broadcaster_id;
    public string id;
    public string status;
    public string winning_outcome_id;
}

[Serializable]
public class TwitchPredictionResponse
{
    public List<TwitchPrediction> data;
}

[Serializable]
public class TwitchPrediction
{
    public string id;
    public List<TwitchPredictionOutcome> outcomes;
}

[Serializable]
public class TwitchPredictionOutcome
{
    public string id;
    public string title;
}

public class TwitchPredictionOutcomeSpec
{
    public int teamId;
    public string title;
}
