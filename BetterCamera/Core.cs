using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using BetterCamera.Features;

[assembly: MelonInfo(typeof(BetterCamera.Core), "BetterCamera", "1.2.b3", "kasa", null)]
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

        /// <summary>
        /// 【诊断】纯只读的探针必须在**游戏自己的场景初始化之前**就位。
        ///
        /// ⚠️ 2026-09-17 才发现的一个观测盲区：游戏的 Zenject 接管
        /// （MonoKernel.Start → InitializableManager.Initialize → 各 installer）跑在
        /// MelonLoader 的**场景回调之前**。所以挂在 OnSceneWasInitialized 里的补丁，
        /// **第一次进拍照场景时全都还没生效** —— 日志里那个 `StartSequence 第 1 次`
        /// 其实是第二次进场景，第一次全程没被观测到。而我们要查的恰恰是"第一次是怎么坏的"。
        ///
        /// 这里挂的四个都是**只读前缀**（只打日志，永远 return true / 不改任何东西），
        /// 所以提前挂没有风险；真正会改变游戏行为的 hook 仍然留在
        /// OnSceneWasInitialized 里，避免在非拍照场景里生效。
        /// </summary>
        /// <summary>
        /// 诊断探针是否启用。**默认关闭**，只有探针文件里写了 `probes-on` 才开。
        ///
        /// ⚠️ 2026-09-17 深夜改成默认关。
        ///
        /// 这一整套探针挂在**游戏自己的关键方法**上：`RoomSnapSceneSequence.StartSequence`、
        /// `RoomSnapSceneInstaller.Initialize`、`MonoKernel.Start`、`UniTaskScheduler`、
        /// `RequestHandler.PushRequest` —— 而它们要诊断的症状本身是
        /// **AccessViolation（读写受保护内存）**，落点就在 `StartSequence`。
        ///
        /// 排查过程里反复出现"加了探针就多一种坏法"（`[cancel]` 那一版甚至让主界面
        /// 都加载不完整）。也就是说探针本身成了变量，测出来的东西不能证明任何事。
        ///
        /// 现在默认一个都不挂：**默认那份跑出来的行为，才是玩家真正会遇到的行为。**
        /// 要诊断时在 `Mods/BetterCamera-probe.txt` 里写 `probes-on` 再重启进程。
        /// </summary>
        private static bool _probesEnabled;


        public override void OnLateInitializeMelon()
        {
            // 探针开关在这一刻读一次，之后改文件要**重启进程**才生效（Apply 有 _applied 闩）
            ProbeFlags.Reload();

            _probesEnabled = ProbeFlags.Has("probes-on");
            if (!_probesEnabled)
            {
//                 MelonLogger.Msg("[BetterCamera] 诊断探针未启用（默认关闭）。"
//                                 + "要开就在 Mods/BetterCamera-probe.txt 里写 probes-on，然后重启进程。");
                return;
            }

//             MelonLogger.Msg("[BetterCamera] probes-on：开始挂诊断探针");

            Step("RequestDropProbeHook", () => RequestDropProbeHook.Apply());
            Step("UniTaskExceptionProbeHook", () => UniTaskExceptionProbeHook.Apply());
            Step("SceneLifecycleProbeHook", () => SceneLifecycleProbeHook.Apply());
            // ⛔ DestroyTraceProbeHook 已下线（2026-09-17 22:50）。
            // 它挂在 UnityEngine.Object.Destroy 上 —— **引擎级热路径**，而 GetObjectName
            // 每次都要遍历全部程序集找类型。它已经完成了使命（证明本 mod 没有销毁
            // SceneContext / RoomSnapSceneInstaller），没必要继续担这个风险。
            // Step("DestroyTraceProbeHook", () => DestroyTraceProbeHook.Apply());

            // ⚠️ 这一版是**重写后**的极简版（2026-09-17 22:57）：只挂 RaiseCancellation，
            // prefix 里只读帧号 + 原生指针，不做任何反射、不访问对象。
            // 上一版同时挂了 get_destroyCancellationToken 的 postfix，把游戏搞坏了
            //（连主界面都加载不完整）—— 详见文件里的说明。
            Step("CancellationTraceProbeHook", () => CancellationTraceProbeHook.Apply());

            // ⛔ 上一版 CancellationTraceProbeHook 的下线记录（2026-09-17 22:44）。
            //
            // 挂上它之后游戏**连主界面都加载不完整**了 —— 比它要诊断的那个 bug 严重得多。
            // 它是那一轮唯一的新增，所以就是它。最可疑的是给
            // `UnityEngine.MonoBehaviour.get_destroyCancellationToken`（一个返回结构体的
            // 属性 getter）挂 postfix + `ref Il2CppSystem.Threading.CancellationToken __result`：
            // 这类"值类型 ref 参数在 il2cpp 上的编组"没验证过，而它是引擎级热路径。
            //
            // 教训：**只读探针不等于安全探针**。挂在引擎自己的方法上（MonoBehaviour、
            // Object）和挂在游戏自己的方法上，风险完全不是一个量级 —— 前者是全进程公用的。
            // 后续如果再要查取消来源，必须挂游戏自己的方法，或者只在启动早期单次读取。
            // Step("CancellationTraceProbeHook", () => CancellationTraceProbeHook.Apply());

            // 这两个原本也挂在场景回调里，于是**游戏自己的 kernel 一个都抓不到** ——
            // SceneContext 的 MonoKernel.Start 跑在 MelonLoader 场景回调之前，
            // 实测第一次进拍照场景时只看到我们自己的克隆体（"[zj] Start P_BetterCamera…"），
            // 游戏那几十个一个都没有。提前挂上才看得到全貌。
            Step("ZenjectInstallTraceHook", () => ZenjectInstallTraceHook.Apply());
            Step("DotweenTargetTraceHook", () => DotweenTargetTraceHook.Apply());

//             MelonLogger.Msg("[BetterCamera] 只读诊断探针已在启动时挂好（改探针文件需重启进程才生效）");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != TargetScene)
                return;

            _inTargetScene = true;

            // 换场景了，上一条报过的失败重新有资格报一次
            _reported.Clear();

            // 同理：各 hook 内部的限流记录也要清，否则"第一次失败、之后永远安静"
            CaptureSizeNativeBridge.ResetReported();

            // 【临时诊断，定位完删】重新读一次探针开关文件（改文件 + 重进场景即可二分定位卡死）
            ProbeFlags.Reload();

            // 四个只读诊断探针（[req] / [uniexc] / [lifecycle] / [destroy]）已经在
            // OnLateInitializeMelon 里挂好了 —— 必须早于游戏的 Zenject 接管，
            // 否则第一次进拍照场景会整个落在观测盲区里（见那里的说明）。

            // 自定义拍照比例 —— **默认关闭**（2026-09-18）。
            //
            // 它在拍照场景里会导致「切换比例时按钮失效」，一直没找到干净的修法：
            // 出片比例注入那个补丁只要挂着就坏（已单独定位），把它下线之后**仍然**会坏，
            // 说明这一块里还有别的挂点在破坏游戏。与其继续在真机上一轮轮排除，
            // 先整块停用、把稳定的版本交出去；要接着查就加 `capture-size` 开关。
            //
            // 停用之后拍照尺寸菜单里只有游戏原生的三项，玩家选它们一切照旧 ——
            // 这个开关影响的是"本 mod 加的那四个比例"。
            bool captureSizeEnabled = ProbeFlags.Has("capture-size");
            if (!captureSizeEnabled)
//                 MelonLogger.Msg("[BetterCamera] 自定义拍照比例功能未启用（默认关闭）");

            // 摘掉原生的 FOV 钳制（只在拍照场景内生效，见 FovClampHook）
            Step("FovClampHook", () => FovClampHook.Apply());

            // 接管滤镜菜单的显隐，让本 mod 的 ColorAdjust 页能跟着标签切换
            // （必须在 ColorAdjustSliders.Init 之前，它建好面板就要登记过来）
            Step("FilterMenuVisibilityHook", () => FilterMenuVisibilityHook.Apply());

            if (captureSizeEnabled)
            {
                // 把自定义拍照比例接到出片链路上。必须在 CaptureSizePresets.Init 之前 ——
                // 补丁要先就位，玩家点新选项时才有东西接住
                Step("CaptureSizeRatioHook", () => CaptureSizeRatioHook.Apply());

                // 把 4 个自定义比例注册成**游戏自己的**拍照尺寸：插进开关字典，并给三处写死
                // 0/1/2 的分支各补一刀（取景框状态变化 / resize / 出片）。
                Step("CaptureSizeNativeBridge", () => CaptureSizeNativeBridge.Apply());
            }

            // 挡掉克隆体上那个没被注入过的 Zenject kernel 在 Start 里抛的空引用。
            // 必须排在下面那些 Instantiate 之前 —— 克隆体一激活 Unity 就会调它的 Start。
            Step("MonoKernelGuardHook", () => MonoKernelGuardHook.Apply());

            // 挡掉克隆体按钮被点击时那条「点击 → 音效」链抛的空引用（音效播放器同样是
            // 注入不到才为 null）。和上面一样，得赶在克隆体存在之前就位 ——
            // 游戏自己在菜单刷新时会去调克隆体的 InitBehaviour。
            Step("ClickSoundGuardHook", () => ClickSoundGuardHook.Apply());

            // （ZenjectInstallTraceHook / DotweenTargetTraceHook 已移到 OnLateInitializeMelon
            //   —— 它们原来挂在这里，导致游戏自己的 kernel 一个都抓不到。）

            // ⚠️ 顺序是有依赖的：
            //   FXUIHandle / SliderHandle 会 Instantiate 出下面各滑条要去找的 UI 对象，
            //   所以它们必须排在最前面。普通玩家看不到这层依赖，改动时留意。
            //
            // 顺序照旧，但每一步都单独兜住 —— 上面那条依赖意味着 Step 名不要随意重排。
            // ⚠️ 顺序是有依赖的：
            //   FXUIHandle / SliderHandle 会 Instantiate 出下面各滑条要去找的 UI 对象，
            //   所以它们必须排在最前面。普通玩家看不到这层依赖，改动时留意。
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

            // 4 个自定义比例选项的克隆体。默认不建（见上面 captureSizeEnabled 的说明）
            if (captureSizeEnabled)
                Step("CaptureSizePresets", CaptureSizePresets.Init);

            // 【临时诊断】把所有 Volume 的后处理参数值与 override 状态打一遍。
            // 同样归 probes-on 管 —— 它要遍历场景里所有 Volume，也属于"会碰游戏对象"的那一类。
            if (_probesEnabled)
                Step("ColorStackProbe", ColorStackProbe.Dump);
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

            // 取景框那两个跨场景状态（_pendingCropKey / _lastScreenW,H）也要清 ——
            // 残留的键会去改下一个场景第一次 UpdateCropArea 的比例，而那个键在新场景里
            // 已经毫无意义（取景框按玩家没选过的比例裁切）
            CaptureSizeNativeBridge.ResetSceneState();

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
            //
            // 也走 Step：一个同步失败不该把另外两个也带下水（它们互不依赖）。
            // Step 对同名步骤只报一次，所以这里不会每帧刷屏。
            Step("ZoomSlider.SyncFromNative", ZoomSlider.SyncFromNative);    // 滚轮 / 键盘改 FOV
            Step("DutchSlider.SyncFromNative", DutchSlider.SyncFromNative);  // 重置按钮改 Dutch
            Step("FocusSlider.SyncFromNative", FocusSlider.SyncFromNative);  // 原生对焦模式 / 自动对焦
        }
    }
}
