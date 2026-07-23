using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BetterCamera.Core), "BetterCamera", "1.0.0", "kasa", null)]
[assembly: MelonGame("gogh Japan", "gogh")]
[assembly: MelonPriority(99)]

namespace BetterCamera
{
    public class Core : MelonMod
    {
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != "S_RoomSnapScene")
                return;
            // LoggerInstance.Msg($"Scene {sceneName} with build index {buildIndex} has been loaded!");

            SliderHandle.Init(LoggerInstance);
            DutchReset.Init(LoggerInstance);
        }
    }
}