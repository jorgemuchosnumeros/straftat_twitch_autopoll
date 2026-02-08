using HarmonyLib;

namespace straftat_twitch_autopoll.Patches;

[HarmonyPatch(typeof(MenuController), nameof(MenuController.OpenGame))]
public static class OpenGamePatch
{
	public static void Prefix(MenuController __instance)
	{
		var client = TwitchOAuthClient.Ensure(Plugin.Logger, () => Plugin.IsPredictionStarted);
		client.StartOAuth();
	}
}
