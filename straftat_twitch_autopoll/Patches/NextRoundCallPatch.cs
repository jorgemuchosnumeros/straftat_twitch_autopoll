using System.Linq;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace straftat_twitch_autopoll.Patches;

[HarmonyPatch(typeof(PlayerManager), "WaitForRoundStartCoroutineStart")]
public static class NextRoundCallPatch
{
	public static void Prefix(PlayerManager __instance)
	{
		if (SceneManager.GetActiveScene().name == "VictoryScene")
		{
			var scores = ClientInstance.playerInstances.ToDictionary(clientInstance =>
				clientInstance.Value, clientInstance => ScoreManager.Instance.GetPoints(ScoreManager.Instance.GetTeamId(clientInstance.Value.PlayerId)));

			var highestScore = scores.OrderByDescending(x => x.Value).First().Value;
			var winners = scores.Where(
				x => x.Value >= highestScore).ToDictionary(
				x => x.Key, x => x.Value);

			var winnerTeam = ScoreManager.Instance.GetTeamId(winners.First().Key.PlayerId);

			Plugin.SendResults(winnerTeam, winners.Keys.Select(x => x.PlayerName).ToList());
		}
		else if (!Plugin.IsPredictionStarted)
		{
			var teams = ClientInstance.playerInstances.Values
				.GroupBy(x => ScoreManager.Instance.GetTeamId(x.PlayerId))
				.ToDictionary(g => g.Key, g => g.Select(x => x.PlayerName).ToList());
			Plugin.StartPredict(teams);
		}
	}
}
