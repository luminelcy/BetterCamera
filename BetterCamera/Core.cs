using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using BetterCamera.Features;

[assembly: MelonInfo(typeof(BetterCamera.Core), "BetterCamera", "1.2.b2", "kasa", null)]
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

        /// <summary>已经报过失败的步骤名。同一个步骤只报一次，避免每帧步骤刷屏。</summary>
        private static readonly HashSet<string> _reported = new HashSet<string>();

        /// <summary>
        /// 跑一步，失败就记一笔然后继续下一步。
        ///
        /// 为什么必须隔离：下面那些步骤是**串行**的，任何一步抛出都会让后面所有步骤不执行 ——
        /// 一个「某个 UI 对象找不到」级别的局部问题，会被放大成「整个 mod 所有功能消失」。
        /// 这个放大器比任何单个步骤本身的 bug 都危险（作者踩过：一个日志钩子抛异常，
        /// 排在最后的 CaptureSizePresets.Init 没跑，比例菜单连同所有组件一起不见了）。
        ///
        /// 同一个步骤名只报一次：OnUpdate 里那几步是每帧跑的，失败会每帧抛。
        /// </summary>
        private static void Step(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (_reported.Add(name))
                    MelonLogger.Error("[BetterCamera] 步骤 " + name + " 失败，其余步骤继续: "
                                      + e.GetType().Name + ": " + e.Message);
            }
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != TargetScene)
                return;

            _inTargetScene = true;

            // 换场景了，上一条报过的失败重新有资格报一次
            _reported.Clear();

            // 摘掉原生的 FOV 钳制（只在拍照场景内生效，见 FovClampHook）
            Step("FovClampHook", () => FovClampHook.Apply());

            // 接管滤镜菜单的显隐，让本 mod 的 ColorAdjust 页能跟着标签切换
            // （必须在 ColorAdjustSliders.Init 之前，它建好面板就要登记过来）
            Step("FilterMenuVisibilityHook", () => FilterMenuVisibilityHook.Apply());

            // 把自定义拍照比例接到出片链路上。必须在 CaptureSizePresets.Init 之前 ——
            // 补丁要先就位，玩家点新选项时才有东西接住
            Step("CaptureSizeRatioHook", () => CaptureSizeRatioHook.Apply());

            // 挡掉"第一次切比例时菜单闪一下竖屏 9:16"（见那边的注释）。同样要在
            // 玩家能点到菜单之前就位。
            Step("PortraitToggleSuppressHook", () => PortraitToggleSuppressHook.Apply());

            // ⚠️ 顺序是有依赖的：
            //   FXUIHandle / SliderHandle 会 Instantiate 出下面各滑条要去找的 UI 对象，
            //   所以它们必须排在最前面。普通玩家看不到这层依赖，改动时留意。
            //
            // 顺序照旧，但每一步都单独兜住 —— 上面那条依赖意味着 Step 名不要随意重排。
            Step("FXUIHandle", () => FX.FXUIHandle.Init(LoggerInstance));
            Step("SliderHandle", () => SliderHandle.Init(LoggerInstance));

            Step("ZoomSlider", ZoomSlider.Init);
            Step("FxSlider", FxSlider.Init);
            Step("DutchSlider", DutchSlider.Init);
            Step("DutchReset", DutchReset.Init);

            Step("NearClipAdjuster", NearClipAdjuster.Init);
            Step("ExitAdjuster", ExitAdjuster.Init);
            Step("FocusSlider", FocusSlider.Init);
            Step("ColorAdjustSliders", ColorAdjustSliders.Init);
            Step("CaptureSizePresets", CaptureSizePresets.Init);
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
            PortraitToggleSuppressHook.Remove();

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
            PortraitToggleSuppressHook.Remove();
        }

        public override void OnUpdate()
        {
            if (!_inTargetScene)
                return;

            // 各自把原生侧的改动同步到滑条手柄。
            // 共同点：这些值都可能被 mod 之外的东西改（滚轮、键盘、原生按钮、
            // 退出重置），手柄不跟上用户就会以为滑条坏了。
            //
            // 也走 Step：一个同步失败不该把另外两个也带下水（它们互不依赖）。
            // Step 对同名步骤只报一次，所以这里不会每帧刷屏。
            Step("ZoomSlider.SyncFromNative", ZoomSlider.SyncFromNative);    // 滚轮 / 键盘改 FOV
            Step("DutchSlider.SyncFromNative", DutchSlider.SyncFromNative);  // 重置按钮改 Dutch
            Step("FocusSlider.SyncFromNative", FocusSlider.SyncFromNative);  // 原生对焦模式 / 自动对焦
        }
    }
}
