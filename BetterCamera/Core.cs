using MelonLoader;

[assembly: MelonInfo(typeof(BetterCamera.Core), "BetterCamera", "1.0.0", "Author", null)]
[assembly: MelonGame(null, null)]

namespace BetterCamera
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initialized.");
        }
    }
}