using MelonLoader;
using UnityEngine;
using BetterCamera.Features;

[assembly: MelonInfo(typeof(BetterCamera.Core), "BetterCamera", "1.1.0", "kasa", null)]
[assembly: MelonGame("gogh Japan", "gogh")]
[assembly: MelonPriority(99)]

namespace BetterCamera
{
    public class Core : MelonMod
    {
        private const string TargetScene = "S_RoomSnapScene";

        /// <summary>
        /// 每帧同步是否该跑。
        ///
        /// 不只是省开销：场景卸载后各模块的静态缓存（cachedSliderObj、cachedDOFInstance
        /// 等）指向的是已销毁的 il2cpp 对象，继续拿它们做反射读取就是操作悬垂指针。
        /// 尤其 FocusSlider.SyncFromNative 会直接读 DOF，必须挡住。
        /// </summary>
        private static bool _inTargetScene;

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != TargetScene)
                return;

            _inTargetScene = true;

            // 摘掉原生的 FOV 钳制（只在拍照场景内生效，见 FovClampHook）
            FovClampHook.Apply();

            // 接管滤镜菜单的显隐，让本 mod 的 ColorAdjust 页能跟着标签切换
            // （必须在 ColorAdjustSliders.Init 之前，它建好面板就要登记过来）
            FilterMenuVisibilityHook.Apply();

            // 把自定义拍照比例接到出片链路上。必须在 CaptureSizePresets.Init 之前 ——
            // 补丁要先就位，玩家点新选项时才有东西接住
            CaptureSizeRatioHook.Apply();

            // ⚠️ 顺序是有依赖的：
            //   FXUIHandle / SliderHandle 会 Instantiate 出下面各滑条要去找的 UI 对象，
            //   所以它们必须排在最前面。普通玩家看不到这层依赖，改动时留意。
            FX.FXUIHandle.Init(LoggerInstance);
            SliderHandle.Init(LoggerInstance);

            ZoomSlider.Init();
            FxSlider.Init();
            DutchSlider.Init();
            DutchReset.Init();

            NearClipAdjuster.Init();
            ExitAdjuster.Init();
            FocusSlider.Init();
            ColorAdjustSliders.Init();
            CaptureSizePresets.Init();
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName != TargetScene)
                return;

            // 先关门禁再拆补丁 —— 顺序反了的话，这一帧还有可能拿旧缓存去同步
            _inTargetScene = false;

            // 离开拍照场景立刻还原原生钳制，把影响面限制在这个场景内
            FovClampHook.Remove();
            FilterMenuVisibilityHook.Remove();
            CaptureSizeRatioHook.Remove();

            // 缓存里是随场景销毁的对象，留着就是悬垂指针
            CaptureSizePresets.Reset();

            // 相机对象随场景销毁，清掉缓存引用免得指着已销毁对象
            NativeFovChannel.Reset();
        }

        public override void OnApplicationQuit()
        {
            // 兜底：万一场景卸载回调没走到，别把补丁留在这个进程里
            _inTargetScene = false;
            FovClampHook.Remove();
            FilterMenuVisibilityHook.Remove();
            CaptureSizeRatioHook.Remove();
        }

        public override void OnUpdate()
        {
            if (!_inTargetScene)
                return;

            // 各自把原生侧的改动同步到滑条手柄。
            // 共同点：这些值都可能被 mod 之外的东西改（滚轮、键盘、原生按钮、
            // 退出重置），手柄不跟上用户就会以为滑条坏了。
            ZoomSlider.SyncFromNative();    // 滚轮 / 键盘改 FOV
            DutchSlider.SyncFromNative();   // 重置按钮改 Dutch
            FocusSlider.SyncFromNative();   // 原生对焦模式 / 自动对焦
        }
    }
}
