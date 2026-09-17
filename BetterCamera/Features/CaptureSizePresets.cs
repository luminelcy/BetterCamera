using System;
using MelonLoader;
using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;
using Il2CppProject.HomeScene.RoomScene.RoomSnapScene;

namespace BetterCamera.Features
{
    /// <summary>
    /// 给拍照尺寸菜单补几个常用的固定比例 —— 做法是让它们**成为游戏自己的选项**。
    ///
    /// 【原生化改造，2026-09-17】4 个预设各自占一个空闲的 <c>CaptureSize</c> 值（3~6），
    /// 由 <see cref="CaptureSizeNativeBridge"/> 插进游戏的开关字典
    /// `CaptureSizeMenuObjectView._captureSizeSwitchButtons`。于是：
    ///   点击 → 游戏自己合并的 OnCaptureSizeSwitchButtonClicked → Presenter 的
    ///   CurrentCaptureSizeSetter.Set(我们的键) → 游戏按「key == 当前值」刷全表开关视觉。
    /// mod 这边只剩两件事：这里建克隆体、<c>OnSizeChanged</c> 记住选中的是哪个预设。
    ///
    /// 【为什么比例还得由 mod 提供】游戏把「CaptureSize → 比例」写死成了内联常量，而且
    /// **有三份**（取景框状态变化 `CaptureSizeCropObjectView.&lt;InitView&gt;b__10_0`、取景框
    /// 窗口 resize 的 LateUpdate、出片状态机 `RoomSnapSceneSequence.&lt;TakePhotoWithCaptureSizeAsync&gt;`），
    /// 我们的键在三处都会掉进 else = "不裁切"。所以那三处各补了一刀（见 CaptureSizeNativeBridge）：
    /// 前两处由我们按比例下发取景框，第三处只把**参数**换成 Portrait，让出片照旧走
    /// "按比例裁切"分支，再由 <see cref="CaptureSizeRatioHook"/> 把写死的 9:16 换成玩家的比例。
    ///
    /// 【和改造前的区别】以前是"借 Portrait 路口"：不写状态、靠推 Portrait 让出片能裁，
    /// 代价是推的那一下游戏会把取景框动画到 9:16，得靠"同帧接管"+ 压制开关视觉那套补丁
    /// （9:16 掠影的根源）。现在状态就是我们的键，游戏一路自己走，那套补丁全部删掉了。
    /// </summary>
    public static class CaptureSizePresets
    {
        private const string OptionNodeName = "P_SettingsToggleSwitchButton";
        private const string LabelNodeName = "CommonLocalizeText";

        internal const string ToggleTypeName =
            "Il2CppCommon.Prefabs.CommonSwitchButton.CommonSwitchButtonBehaviour";
        private const string CropViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeCropObject.CaptureSizeCropObjectView";
        private const string MenuInstallerTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeMenuObject.CaptureSizeMenuObjectInstaller";
        private const string PresenterTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeMenuObject.CaptureSizeMenuObjectPresenter";

        internal sealed class Preset
        {
            public string Name;     // 克隆体名字（UI 契约）
            public string Label;    // 显示给玩家的文字 —— 比例名与语言无关，不用查表
            public float Ratio;     // 宽 / 高
            public CaptureSize Key; // 注册进游戏字典用的键（真的 CaptureSize 值，见下面数组的注释）
            public Il2CppSystem.Object Toggle;
        }

        // 键用 3~6：游戏只定义了 0/1/2（Default/Portrait/HoloModelink），3 以上空闲，
        // 而 CaptureSize 底层就是 int —— 游戏对它的比较、字典键、内联分支全按整数值走，
        // 所以塞新值进去和塞原生值进去在它眼里是一回事（见 CaptureSizeNativeBridge 的说明）。
        private static readonly Preset[] Presets =
        {
            new Preset { Name = "P_BCCaptureSizeOption_1x1", Label = "1:1", Ratio = 1f, Key = (CaptureSize)3 },
            new Preset { Name = "P_BCCaptureSizeOption_5x4", Label = "5:4", Ratio = 1.25f, Key = (CaptureSize)4 },
            new Preset { Name = "P_BCCaptureSizeOption_4x3", Label = "4:3", Ratio = 4f / 3f, Key = (CaptureSize)5 },
            new Preset { Name = "P_BCCaptureSizeOption_3x2", Label = "3:2", Ratio = 1.5f, Key = (CaptureSize)6 },
        };

        /// <summary>当前选中的自定义预设；null = 玩家在用原生选项（或不裁切）。</summary>
        private static Preset _selected;

        // 取景框那条写入口。方法信息是类型级的，跨场景不用重查；
        // 拿不到组件时自然也调不了，所以不必跟着 Reset 清。
        private static bool _cropMethodResolved;
        private static Il2CppSystem.Reflection.MethodInfo _cropMethod;

        /// <summary>给补丁用：当前该按哪个比例裁切；null = 不插手，交给游戏。</summary>
        public static float? SelectedRatio => _selected?.Ratio;

        public static void Init()
        {
            // 【临时诊断，定位完删】no-clones ⇒ 整个功能不建（菜单里只有原生三项）＝ 最干净的基准盘。
            // 只关补丁/字典是不够的：克隆体本身还在建、还在菜单里占位、还挂着我们的点击监听，
            // 那些都可能是卡死的一部分（玩家 2026-09-17 指出基准盘里还能看到自定义比例）。
            if (ProbeFlags.Has("no-clones"))
            {
//                 MelonLogger.Msg("[probe] no-clones：本次不创建自定义比例（菜单里只有原生三项）");
                return;
            }

            var template = GameObject.Find(GamePaths.CaptureSizeOptionTemplate);
            var parent = GameObject.Find(GamePaths.CaptureSizeMenuBody);
            if (template == null || parent == null)
            {
                MelonLogger.Warning("[BetterCamera] 找不到拍照尺寸菜单，比例预设未创建");
                return;
            }

            // 模板在 Body 里的次序是 BG / Title / Default / Portrait / Border / HoloModelink。
            // 插在 Portrait 后面（Border 之前），和原生预设排在一起。
            int insertAt = template.transform.GetSiblingIndex() + 1;

            foreach (var preset in Presets)
            {
                var option = UnityEngine.Object.Instantiate(template, parent.transform);
                option.name = preset.Name;
                option.transform.SetSiblingIndex(insertAt++);

                if (!BuildOption(option, preset))
                    continue;
            }

            // 克隆体建好之后把它们注册进游戏的开关字典：这样游戏自己的视觉刷新（每次状态变化
            // 遍历字典现算）会带上我们 —— 原生三项自动按灭、我们那个自动点亮。
            // 点击入流那份快照赶不上（见 BuildOption 的说明），由我们的点击监听补。
            CaptureSizeNativeBridge.RegisterPresets();
        }

        /// <summary>离开拍照场景时清掉缓存 —— 这些是随场景销毁的对象，留着就是悬垂指针。</summary>
        public static void Reset()
        {
            _selected = null;
            foreach (var p in Presets) p.Toggle = null;
        }

        /// <summary>
        /// 把玩家的选择交给**游戏自己的 Presenter**：调它那个"收 CaptureSize 的方法"（= 点击回调本体
        /// `&lt;StartLifeCycle&gt;b__28_7`，反编译确认它只有一句 `CurrentCaptureSizeSetter.Set(...)`）。
        ///
        /// 为什么不自己写 CurrentCaptureSize：那要凭空造一个 CurrentCaptureSize（传装箱的枚举），
        /// 是这个项目踩过两次的静默失效类型。走它的方法 = 状态由游戏自己的代码构造和写入，
        /// 写完之后游戏的 b__8_1 会把整本字典刷一遍（我们那 4 个开关因此在视觉上也跟着亮）。
        /// </summary>
        private static void PushKey(Preset preset)
        {
            // 【临时诊断，定位完删】"玩家点了哪个预设"这一步的轨迹
//             MelonLogger.Msg("[key] 推送 " + preset.Label + "（键 " + (int)preset.Key + "）");

            try
            {
                var installer = NativeRefs.FindComponent(GamePaths.CaptureSizeMenu, MenuInstallerTypeName);
                var presenter = installer == null
                    ? null
                    : Il2CppReflection.FindIl2CppField(installer.GetIl2CppType(), "_presenter")?.GetValue(installer);

                var wrapper = Il2CppReflection.WrapAsManaged(presenter, PresenterTypeName);
                if (wrapper == null)
                {
                    MelonLogger.Warning("[BetterCamera] 拿不到拍照尺寸菜单的 Presenter，" + preset.Label + " 切不过去");
                    return;
                }

                // 按签名找：Presenter 上只有那个处理点击的 lambda 收 CaptureSize
                foreach (var method in wrapper.GetType().GetMethods(
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType.Name != "CaptureSize") continue;

                    // 【临时诊断】探针开关：native-state ⇒ 推 Portrait（= 状态永远留在原生值域里）
                    var target = ProbeFlags.Has("native-state") ? CaptureSize.Portrait : preset.Key;
                    method.Invoke(wrapper, new object[] { target });
                    return;
                }

                MelonLogger.Warning("[BetterCamera] Presenter 上没有收 CaptureSize 的方法，" + preset.Label + " 切不过去");
            }
            catch (Exception e)
            {
                // 这个回调挂在 Button.onClick 上：异常冒出去会打断这一整次 UI 输入处理（见 CallbackGuard 的说明）
                MelonLogger.Warning("[BetterCamera] 切换拍照比例失败: " + e.Message);
            }
        }

        /// <summary>按钮位置不猜：从 CommonButtonBehaviour._button 拿游戏自己存的引用。</summary>
        private static Il2CppSystem.Object TakeButton(Il2CppSystem.Object toggle)
        {
            var behaviour = Il2CppReflection
                .FindIl2CppField(toggle.GetIl2CppType(), "_switchButton")?.GetValue(toggle);

            return behaviour == null
                ? null
                : Il2CppReflection.FindIl2CppField(behaviour.GetIl2CppType(), "_button")?.GetValue(behaviour);
        }

        /// <summary>给原生化桥用：全部预设（含它们的键、比例和克隆出来的开关）。</summary>
        internal static Preset[] All => Presets;

        /// <summary>键 → 比例。不是自定义预设的键返回 null —— 那意味着"这是游戏自己的尺寸，别插手"。</summary>
        internal static float? RatioForKey(CaptureSize key)
        {
            foreach (var p in Presets)
                if (p.Key == key) return p.Ratio;
            return null;
        }

        /// <summary>
        /// 游戏的 <c>CurrentCaptureSize</c> 变成 captureSize 了。
        ///
        /// 由 CaptureSizeRatioHook 打在 View 的 <c>&lt;InitView&gt;b__8_1</c> 上 ——
        /// ⚠️ 那个回调的语义是「状态变了，按 key == 新值 刷全表」，**不是**"玩家点了原生选项"。
        /// 改造前之所以把它当后者用，是因为我们自己从不写状态；现在我们的键会被游戏写进状态，
        /// 所以必须按值分流：
        ///   自定义键 → 记住选中的预设（出片比例与 resize 都要用它）
        ///   原生键   → 清掉选中状态（玩家回到原生尺寸）
        ///
        /// 开关视觉不用我们管：<c>b__8_1</c> 本体就是"遍历字典、按 key == 当前值 刷全表"，
        /// 原生那几项也会被一起按灭。
        /// </summary>
        internal static void OnSizeChanged(CaptureSize captureSize)
        {
            _selected = null;
            foreach (var p in Presets)
            {
                if (p.Key != captureSize) continue;
                _selected = p;
                break;
            }

            // 【临时诊断，定位完删】卡死时这一行是"最后一次点击走到哪儿"的起点
//             MelonLogger.Msg("[key] 状态变成 " + (int)captureSize
//                             + "（选中=" + (_selected == null ? "非自定义" : _selected.Label) + "）");
        }

        private static bool BuildOption(GameObject option, Preset preset)
        {
            // 克隆体上挂着 LocalizeStringEvent，会按模板的 key 把文字覆盖回去，先让它让开
            TmpKit.SilenceLocalization(option.transform);
            TmpKit.SetText(TmpKit.FindText(option.transform.Find(LabelNodeName)), preset.Label);

            var toggle = NativeRefs.FindComponent(option.transform.Find(OptionNodeName), ToggleTypeName);
            if (toggle == null)
            {
                MelonLogger.Warning("[BetterCamera] 克隆体上没有 CommonSwitchButtonBehaviour，比例预设 "
                                    + preset.Label + " 不可用");
                return false;
            }

            var button = TakeButton(toggle);
            if (button == null)
            {
                MelonLogger.Warning("[BetterCamera] 拿不到比例预设 " + preset.Label + " 的按钮");
                return false;
            }

            // ⚠️ 必须调**游戏自己的** CommonSwitchButtonBehaviour.InitBehaviour(bool)，不能只调 UpdateIsOn。
            //
            // 原因是晚插的副作用：游戏的 InitView 会给字典里每个条目调一次 InitBehaviour(key == Default)
            //（它把 `_wasInit` 置 true），而我们的条目是 InitView 跑完之后才插进去的 ⇒ 游戏不会再替我们初始化。
            // 于是 `_wasInit` 恒为 false ⇒ UpdateIsOn 的守卫 `if (_isOn != isOn || !_wasInit)` **恒真** ⇒
            // 每一次开关视觉刷新都会给这 4 个克隆体取消重建 CTS、重播一遍 timeline ——
            // 玩家看到的就是"点一个自定义预设，选中标识沿字典顺序把 3,4,5,6 跳一遍"（2026-09-17 实测）。
            //
            // InitBehaviour 内部会调 CommonButtonBehaviour.InitBehaviour()（点击→音效那条链）；
            // 克隆体的音效播放器没被注入，那一步由 ClickSoundGuardHook 挡掉，不会抛。
            if (!TryInitBehaviour(toggle))
                SetToggleVisual(toggle, false, force: true);   // 拿不到游戏的原方法就退回老的兜底

            // 点击由我们自己路由：那本字典的点击 Merge 流是**订阅时的快照**，我们插进去太晚
            //（场景那批 Zenject 初始化跑在 MelonLoader 场景回调之前），所以这份快照里没有我们 ——
            // 由这里把"我们的键"交给游戏自己的 Presenter 补上（状态仍由游戏写入）。
            UnityEventBridge.AddClickListener(button, () => PushKey(preset));

            preset.Toggle = toggle;
            return true;
        }

        /// <summary>
        /// 把取景框设到指定比例。
        ///
        /// 用游戏自己的 <c>UpdateCropArea</c>（带动画那条）—— 和原生选项走的是同一条路，
        /// 补间、布局、_isAnimating 都由游戏自己维护。
        ///
        /// 曾经改成过 <c>UpdateCropAreaImmediately</c>（不带动画那条），理由是"避免补间位移"，
        /// 但那是错的：它不 Kill 掉游戏那边正在跑的 sequence，只是关掉 fitter 直接写
        /// sizeDelta，于是和还在运行的补间抢同一个属性。实测每次切换比例后 2~3ms 就冒一个
        /// NullReferenceException，而且切换次数一多就永久复现 —— 退回游戏自己的路径。
        /// （那批 NRE 后来查明是按钮音效链的残留，但"和跑着的补间抢属性"这条理由本身仍然成立。）
        /// </summary>
        internal static void ApplyCropAreaImmediately(float ratio)
        {
            var view = NativeRefs.FindComponent(GamePaths.CaptureSizeCropObject, CropViewTypeName);
            if (view == null)
            {
                MelonLogger.Warning("[BetterCamera] 找不到取景框，比例预览不会更新");
                return;
            }

            if (!EnsureCropMethod(view)) return;

            try
            {
                // 【临时诊断，定位完删】下发前后各记一行取景框状态：
                // 卡死的时候，日志尾部这几行就是"我们最后一刀切在什么状态上"。
//                 MelonLogger.Msg("[tween] 下发前 " + CropStateLine());

                _cropMethod.Invoke(view, new Il2CppSystem.Object[] { Il2CppReflection.BoxFloat(ratio) });

//                 MelonLogger.Msg("[tween] 下发后 " + CropStateLine());
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 更新取景框失败: " + e.Message
                                    + "（下发时 " + CropStateLine() + "）");
            }
        }

        /// <summary>
        /// 【临时诊断，定位完删】取景框状态压成一行，给卡死现场用。
        ///
        /// 三个字段各有判读：
        ///   animating —— 我们这一刀是不是切在游戏动画中途
        ///   seq       —— 补间指针，变了说明这一刀把游戏的 sequence 换掉了
        ///   size      —— 游戏侧的 CurrentCaptureSize（和我们选的比例是否一致）
        /// </summary>
        public static string CropStateLine()
        {
            try
            {
                var view = NativeRefs.FindComponent(GamePaths.CaptureSizeCropObject, CropViewTypeName);
                if (view == null) return "取景框找不到";

                var type = view.GetIl2CppType();
                var animating = Il2CppReflection.FindIl2CppField(type, "_isAnimating")?.GetValue(view);
                var seq = Il2CppReflection.FindIl2CppField(type, "_sequence")?.GetValue(view);
                var size = Il2CppReflection.FindIl2CppField(type, "_currentCaptureSize")?.GetValue(view);

                return "animating=" + (animating == null ? "?" : animating.Unbox<bool>().ToString())
                       + " seq=0x" + (seq == null ? "0" : seq.Pointer.ToInt64().ToString("X"))
                       + " size=" + (size == null ? "?" : size.ToString());
            }
            catch (Exception e)
            {
                return "读取景框状态失败: " + e.GetType().Name;
            }
        }

        /// <summary>
        /// 解析取景框的写入口。
        ///
        /// 必须从**活着的组件**拿 Il2CppSystem.Type —— 方法信息本身是类型级的、跨场景不用重查，
        /// 但 FindIl2CppMethod 要的是 Il2CppSystem.Type，而 Il2CppReflection.FindType
        /// 给的是托管 Type，两者不通用。所以只在第一次拿到组件时解析一次。
        /// </summary>
        private static bool EnsureCropMethod(Il2CppSystem.Object view)
        {
            if (_cropMethodResolved) return _cropMethod != null;

            _cropMethodResolved = true;

            // 只留"不带动画"那条：带动画的那条现在由游戏自己的代码走
            //（我们只在 b__10_0 前缀里把入参改写成 Portrait，再由 CropRatioPrefix 换掉比例）
            _cropMethod = Il2CppReflection.FindIl2CppMethod(view.GetIl2CppType(), "UpdateCropAreaImmediately");
            if (_cropMethod == null)
                MelonLogger.Warning("[BetterCamera] 找不到取景框的 UpdateCropArea，比例预览不会更新");

            return _cropMethod != null;
        }

        // ---- 【临时诊断】DOTween 补间计数。定位完连同 ApplyCropArea 里那行一起删。 ----
        //
        // 要验的猜想：ApplyCropArea 调的 UpdateCropArea **每次都会 Kill 旧序列再新建一个**，
        // 如果 Kill 没把旧的释放干净，这几个计数会随切换次数**单调上涨**。而 DOTween 的
        // TweenManager 每帧要遍历所有活动补间 —— 攒到一定程度就可能把别的东西拖垮。
        //
        // 日志里已经出现过两类 DOTween 警告（"Target or field is missing/null"、
        // "This Tween has been killed and is no..."），说明死补间确实存在。
        //
        // 判读：切换十几次，看数字是**单调上涨**还是**稳定在某个小值**。
        // 前者=泄漏（接着查我们哪一刀没释放干净）；后者=排除 DOTween，换方向。
        private static Il2CppSystem.Type _dotweenType;
        private static bool _dotweenTypeResolved;

        // 这个计数只在 LogTweenCounts 的输出里用，而那段输出目前是注释状态
        //（诊断代码按约定保留、默认不播报）。字段留着，以后查补间泄漏直接放开即可。
#pragma warning disable CS0169
        private static int _tweenSample;
#pragma warning restore CS0169

        private static void LogTweenCounts()
        {
            try
            {
                if (!_dotweenTypeResolved)
                {
                    _dotweenTypeResolved = true;
                    _dotweenType = NativeRefs.TypeOf("Il2CppDG.Tweening.DOTween");
                    if (_dotweenType == null)
                        MelonLogger.Warning("[tween] 找不到 DOTween 类型，补间计数不可用");
                }

                if (_dotweenType == null) return;

//                 MelonLogger.Msg("[tween] #" + _tweenSample++
//                                 + " active=" + StaticInt("TotalActiveTweens")
//                                 + " sequences=" + StaticInt("TotalActiveSequences")
//                                 + " playing=" + StaticInt("TotalPlayingTweens"));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[tween] 读补间数失败: " + e.Message);
            }
        }

        private static int StaticInt(string name)
        {
            var method = Il2CppReflection.FindIl2CppStaticMethod(_dotweenType, name);
            return method == null ? -1 : Il2CppReflection.UnboxInt(method.Invoke(null, null));
        }

        /// <summary>
        /// 调游戏的 <c>CommonSwitchButtonBehaviour.InitBehaviour(bool)</c>（把克隆体变成"游戏初始化过"的开关：
        /// `_isOn = false`、`_wasInit = true`，从而 UpdateIsOn 的同值调用不再重播 timeline）。
        /// 找不到那个方法返回 false，调用方退回旧兜底。
        /// </summary>
        private static bool TryInitBehaviour(Il2CppSystem.Object toggle)
        {
            if (toggle == null) return false;

            try
            {
                var method = Il2CppReflection.FindIl2CppMethod(toggle.GetIl2CppType(), "InitBehaviour", 1);
                if (method == null) return false;

                method.Invoke(toggle, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(false) });
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 初始化克隆开关失败（退回 UpdateIsOn 兜底）: " + e.Message);
                return false;
            }
        }

        private static void InvokeBool(Il2CppSystem.Object target, string methodName, bool value)
        {
            if (target == null) return;

            try
            {
                Il2CppReflection.FindIl2CppMethod(target.GetIl2CppType(), methodName, 1)
                    ?.Invoke(target, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(value) });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 调 " + methodName + " 失败: " + e.Message);
            }
        }

        /// <summary>
        /// 设开关的视觉状态。**值没变就整个跳过**，这是必须的，不能图省事直接调
        /// UpdateIsOn 让它自己判断。
        ///
        /// 反汇编确认过：
        ///
        ///   UpdateIsOn 开头是  if ( _isOn != isOn || !_wasInit )
        ///   InitBehaviour 里才有  _wasInit = true
        ///   （而且只有对象在**激活层级**里时它才重播 timeline，否则走 SnapVisualToCurrentState）
        ///
        /// 原生化改造之后，`_wasInit` 不再恒为 false：我们的克隆体也会被游戏的 InitView
        /// 一起初始化（它遍历整本字典调 `InitBehaviour(key == Default)`）。但那次初始化
        /// 依赖"插入确实发生了"，所以 BuildOption 里仍然自己 force 一次做兜底 ——
        /// 幂等，最坏情况是多播一遍 off 动画。
        ///
        /// 日常的亮/灭**不归这里管**：状态一变，游戏的 b__8_1 会遍历字典按 key 刷全表。
        /// 这个方法现在只有 BuildOption 那一个调用点（force: true）。
        /// </summary>
        private static void SetToggleVisual(Il2CppSystem.Object toggle, bool isOn, bool force = false)
        {
            if (toggle == null) return;

            if (!force && ReadIsOn(toggle) is bool current && current == isOn) return;

            InvokeBool(toggle, "UpdateIsOn", isOn);
        }

        /// <summary>读 CommonSwitchButtonBehaviour.IsOn（就是 UpdateIsOn 比较的那个字段）。读不到返回 null。</summary>
        private static bool? ReadIsOn(Il2CppSystem.Object toggle)
        {
            try
            {
                var getter = Il2CppReflection.FindIl2CppMethod(toggle.GetIl2CppType(), "get_IsOn");
                return getter?.Invoke(toggle, System.Array.Empty<Il2CppSystem.Object>())?.Unbox<bool>();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 读开关状态失败: " + e.Message);
                return null;
            }
        }
    }
}
