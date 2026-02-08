# Straftat Twitch Autopoll (STRAFTAT / BepInEx Mono)

Automate your Twitch match predictions, relieving most of the burden from your moderators.

## Installation (manual)
Assuming [Bepinex Mono](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_mono.html) is already installed, Unzip the release in `STRAFTAT/Bepinex/Plugins`

## Usage
Note: You will need a twitch account that is able to do predicts (Affiliate, Partner, etc.)

Start the game with the mod loaded, after the title screen the game will wait for you to complete the OAuth Device flow to grant the mod access to predicts.

After that, either host or join a game, during the first round the predict will start. You can cancel the predict returning to the main menu or closing the game.

Results will be uploaded when reaching the VictoryScene with the leaderboard.

Another Note: This mod hasnt been tested with MoreStrafts or edge cases where players at the start of the predict are not the same as at the end of the predict.

## Building

To build you will require a `straftat_twitch_autopoll/straftat_twitch_autopoll/libs` folder with the following assemblies from the game (`STRAFTAT/STRAFTAT_Data/Managed`) with `STRAFTAT` being the game root folder
- `Assembly-CSharp.dll`
- `ComputerysModdingUtilites.dll`
- `FishNet.Runtime.dll`
- `Newtonsoft.Json.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.JSONSerializeModule`
- `UnityEngine.UnityWebRequestModule`
- `UnityEngine.UnityWebRequestWWWModule`
- `Unity.TextMeshPro.dll`


You are welcome to repurpose this mod for other unity games with a similar system to this game.
