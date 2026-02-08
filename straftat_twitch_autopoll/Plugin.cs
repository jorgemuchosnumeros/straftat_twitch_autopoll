using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using ComputerysModdingUtilities;
using HarmonyLib;
using UnityEngine;
using TMPro;


[assembly: StraftatMod(true)]
namespace straftat_twitch_autopoll;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]

public class Plugin : BaseUnityPlugin
{
    internal new static ManualLogSource Logger;
    
    public static bool IsPredictionStarted;
    public static TwitchPredictionsClient PredictionsClient;

    public const int PredictionWindowSeconds = 120;
    public const string PredictionTitle = "Match Winner";

    public static void LogInfo(string message, bool writeOffline = false, string color = "white")
    {
        Logger.LogInfo(message);
        if (writeOffline)
            PauseManager.Instance.WriteOfflineLog($"<color={color}><b>{message}</b></color>");
    }

    public static void LogWarning(string message, bool writeOffline = false, string color = "yellow")
    {
        Logger.LogWarning(message);
        if (writeOffline)
            PauseManager.Instance.WriteOfflineLog($"<color={color}><b>{message}</b></color>");
    }

    public static void LogError(string message, bool writeOffline = false, string color = "red")
    {
        Logger.LogError(message);
        if (writeOffline)
            PauseManager.Instance.WriteOfflineLog($"<color={color}><b>ERROR: {message}</b></color>");
    }
    
    
    private void Awake()
    {
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        
        new Harmony("straftat_twitch_autopoll").PatchAll();
        PredictionsClient = new TwitchPredictionsClient(
            TwitchOAuthClient.Ensure(Logger, () => IsPredictionStarted),
            Logger,
            string.Empty,
            PredictionWindowSeconds,
            PredictionTitle);
    }

    private void OnApplicationQuit()
    {
        Logger.LogError("OnApplicationQuit");
        
        if (IsPredictionStarted)
            CancelPredict();
    }

    public static void StartPredict(Dictionary<int, List<string>> teamsById)
    {
        try
        {
            if (!IsPredictionStarted)
            {
                var oauth = TwitchOAuthClient.Ensure(Logger, () => IsPredictionStarted);
                if (!oauth.IsAffiliateOrPartner)
                {
                    LogWarning("Predictions disabled: Twitch account is not affiliate/partner.", true);
                    return;
                }

                LogInfo("Starting prediction for");
                LogInfo("Prediction started for:", true, "green");
                int optionIndex = 0;
                foreach (var kvp in teamsById)
                {
                    LogInfo($"Team {kvp.Key}: {string.Join(", ", kvp.Value)}");
                    LogInfo($"Option {optionIndex}: {string.Join(", ", kvp.Value)}", true);
                    optionIndex++;
                }

                var orderedOutcomes = teamsById
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => new TwitchPredictionOutcomeSpec
                    {
                        teamId = kvp.Key,
                        title = TrimOutcomeTitle(string.Join(", ", kvp.Value))
                    })
                    .ToList();
                if (orderedOutcomes.Count < 2)
                {
                    LogWarning("Prediction requires at least 2 outcomes. Skipping prediction.", true);
                    return;
                }
                PredictionsClient.CreatePrediction(orderedOutcomes);

                IsPredictionStarted = true;
            }
            else
            {
                LogError("Prediction already started!");
                throw new InvalidOperationException("Prediction already started");
            }
        }
        catch (Exception ex)
        {
            LogError($"StartPredict failed: {ex}", true);
        }
    }

    private static string TrimOutcomeTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
            return string.Empty;
        return title.Length > 24 ? title.Substring(0, 24) + "." : title;
    }

    public static void CancelPredict()
    {
        if (IsPredictionStarted)
        {
            LogInfo("Stopping and refunding prediction", true, "blue");
            
            //TODO: Twitch Stop and Refund Predict Call
            PredictionsClient.CancelPrediction();
            
            IsPredictionStarted = false;
        }
    }

    public static void SendResults(int teamId, List<string> winners)
    {
        LogInfo("Concluding predict results:", true, "yellow");
        LogInfo($"Team: {teamId} Winners: {string.Join(", ", winners)}", true);
        
        //TODO: Twitch Concluding Predict Call
        PredictionsClient.ResolvePrediction(teamId);

        IsPredictionStarted = false;
    }
}
