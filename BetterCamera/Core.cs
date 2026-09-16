using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(BetterCamera.Core), "BetterCamera", "1.1.0", "kasa", null)]
[assembly: MelonGame("gogh Japan", "gogh")]
[assembly: MelonPriority(99)]

namespace BetterCamera
{
    public class Core : MelonMod
    {
        private const string TargetScene = "S_RoomSnapScene";

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != TargetScene)
                return;

            // 摘掉原生的 FOV 钳制（只在拍照场景内生效，见 FovClampHook）
            FovClampHook.Apply(LoggerInstance);

            CameraInitHandle.Init(LoggerInstance);
            FX.FXUIHandle.Init(LoggerInstance);
            FX.BaseSlider.Init(LoggerInstance);
            FX.EffectSlider.Init(LoggerInstance);
            SliderHandle.Init(LoggerInstance);
            ZoomSlider.Init(LoggerInstance);
            DutchReset.Init(LoggerInstance);
            DutchSlider.Init(LoggerInstance);
            FocusSlider.Init(LoggerInstance);
            QuitHandle.Init(LoggerInstance);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName != TargetScene)
                return;

            // 离开拍照场景立刻还原原生钳制，把影响面限制在这个场景内
            FovClampHook.Remove(LoggerInstance);

            // 相机对象随场景销毁，清掉缓存引用免得指着已销毁对象
            NativeFovChannel.Reset();
        }

        public override void OnApplicationQuit()
        {
            // 兜底：万一场景卸载回调没走到，别把补丁留在这个进程里
            FovClampHook.Remove(LoggerInstance);
        }

        public override void OnUpdate()
        {
            // 每帧把原生改动同步到对应的滑条手柄上。
            // 缩放：滚轮/键盘改 FOV 时手柄要跟着走。
            ZoomSlider.SyncFromNative();

            // 对焦：原生对焦模式按钮 / 自动对焦改焦点距离时手柄要跟着走。
            FocusSlider.SyncFromNative();
        }
    }
}
