using System;
using MelonLoader;
using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;
using Il2CppProject.HomeScene.RoomScene.RoomSnapScene;

namespace BetterCamera.Features
{
    /// <summary>
    /// 给拍照尺寸菜单补几个常用的固定比例。
    ///
    /// 为什么不能像 ColorAdjust 那样"改一个枚举值就完事"：游戏把「CaptureSize → 比例」
    /// 写死成了三个内联常量（`RoomSnapSceneSequence.&lt;TakePhotoWithCaptureSizeAsync&gt;d__159.MoveNext`），
    /// 加新枚举值只会落到 else = 不裁切分支。所以比例得由 mod 自己在出片链路上替换
    /// （见 CaptureSizeRatioHook），这里只负责 UI + "当前选中的是哪个自定义比例"。
    ///
    /// 选项**不进**游戏的 `_captureSizeSwitchButtons` 字典：那个字典运行中加不进去
    /// （`OnCaptureSizeSwitchButtonClicked` 每次访问都重建 Merge 流，而 Presenter 只在
    /// StartLifeCycle 里订阅一次），而且往里塞条目正是滤镜菜单那边踩过的坑。
    /// 新选项的点击和开关视觉都由这里自己管。
    ///
    /// 价格：因为不写 CurrentCaptureSize，游戏自己的状态会停在原来那个原生选项上。
    /// 没有副作用 —— 观察这个变量的只有这个菜单和取景框，两处都由本 mod 接管了。
    /// </summary>
    public static class CaptureSizePresets
    {
        private const string OptionNodeName = "P_SettingsToggleSwitchButton";
        private const string LabelNodeName = "CommonLocalizeText";

        /// <summary>原生选项的命名前缀。我们的叫 P_BCCaptureSizeOption_*，不会误伤。</summary>
        private const string NativeOptionPrefix = "CaptureSizeOption_";

        private const string ToggleTypeName =
            "Il2CppCommon.Prefabs.CommonSwitchButton.CommonSwitchButtonBehaviour";
        private const string CropViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeCropObject.CaptureSizeCropObjectView";
        private const string MenuInstallerTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeMenuObject.CaptureSizeMenuObjectInstaller";
        private const string PresenterTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeMenuObject.CaptureSizeMenuObjectPresenter";

        private sealed class Preset
        {
            public string Name;     // 克隆体名字（UI 契约）
            public string Label;    // 显示给玩家的文字 —— 比例名与语言无关，不用查表
            public float Ratio;     // 宽 / 高
            public Il2CppSystem.Object Toggle;
        }

        private static readonly Preset[] Presets =
        {
            new Preset { Name = "P_BCCaptureSizeOption_1x1", Label = "1:1", Ratio = 1f },
            new Preset { Name = "P_BCCaptureSizeOption_5x4", Label = "5:4", Ratio = 1.25f },
            new Preset { Name = "P_BCCaptureSizeOption_4x3", Label = "4:3", Ratio = 4f / 3f },
            new Preset { Name = "P_BCCaptureSizeOption_3x2", Label = "3:2", Ratio = 1.5f },
        };

        /// <summary>当前选中的自定义预设；null = 玩家在用原生选项（或不裁切）。</summary>
        private static Preset _selected;

        // 取景框那条写入口。方法信息是类型级的，跨场景不用重查；
        // 拿不到组件时自然也调不了，所以不必跟着 Reset 清。
        private static bool _cropMethodResolved;
        private static Il2CppSystem.Reflection.MethodInfo _cropMethod;

        /// <summary>
        /// 游戏状态是否已经被本 mod 推到 Portrait 了。
        ///
        /// 这个闸门是「选中跳一帧 9:16」的正解。机制是：
        ///
        ///   RequestNativePortrait() 把 CurrentCaptureSize 推成 Portrait
        ///     └─ 游戏按「key == 当前值」重刷一遍开关视觉 → **Portrait 被点亮**
        ///   RefreshToggleVisuals() 紧接着又把它按灭
        ///
        /// 两次写入在同一帧，但点亮那一下启动了一段 timeline（_onOnTimeline），
        /// 那一帧渲染出来就是 Portrait 亮着的样子 —— 肉眼就是"跳一帧 9:16"。
        ///
        /// 而**第二次之后切换，CurrentCaptureSize 本来就是 Portrait**：再推一次
        /// 纯属多余，却每次都让游戏重刷一遍开关。所以推过就不再推，只在玩家中途
        /// 去点了原生选项（见 OnNativeSizeChosen）之后才需要重新推。
        /// </summary>
        private static bool _droveToPortrait;

        /// <summary>给补丁用：当前该按哪个比例裁切；null = 不插手，交给游戏。</summary>
        public static float? SelectedRatio => _selected?.Ratio;

        public static void Init()
        {
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
        }

        /// <summary>离开拍照场景时清掉缓存 —— 这些是随场景销毁的对象，留着就是悬垂指针。</summary>
        public static void Reset()
        {
            _selected = null;
            _droveToPortrait = false;
            foreach (var p in Presets) p.Toggle = null;
        }

        /// <summary>
        /// 原生选项被选中了（由补丁在游戏自己的开关刷新之后调）。
        /// 这是"玩家离开了自定义预设"的唯一信号 —— 我们没写 CurrentCaptureSize，
        /// 所以原生那条路不会有任何别的通知。
        /// </summary>
        public static void OnNativeSizeChosen()
        {
            // 玩家回到原生选项了，游戏状态不再是我们推的 Portrait —— 下次选预设要重新推
            _droveToPortrait = false;

            if (_selected == null) return;
            _selected = null;
            RefreshToggleVisuals();
        }

        /// <summary>
        /// 菜单重新初始化后把我们选中的开关重新点亮。
        /// 游戏在 InitView 里会把每个开关刷成「key == Default」，我们的不在字典里刷不到，
        /// 但选中的那个也会被别人的刷新带歪，所以得补一次。
        /// </summary>
        public static void OnMenuReinitialized() => RefreshToggleVisuals();

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

            // ⚠️ 刻意**不**调 CommonSwitchButtonBehaviour.InitBehaviour。
            // 它内部会去初始化 CommonButtonBehaviour 的「点击 → 音效」那条链，
            // 而音效播放器是 Zenject 注入的、克隆体上永远是 null —— 点下去就是空引用。
            // 视觉初始化改用 UpdateIsOn（`if (_isOn != isOn || !_wasInit)` 决定了它
            // 在没初始化过时也会走完整流程），不走那条链。
            SetToggleVisual(toggle, false, force: true);

            // 回调包一层：异常顺着 Button.onClick.Invoke 冒出去的话，
            // EventSystem 这一次输入处理会被整个打断，表现是"点了这个之后别的按钮也不响应"
            UnityEventBridge.AddClickListener(button, () => GuardedSelect(preset));

            preset.Toggle = toggle;
            return true;
        }

        /// <summary>
        /// 包一层 try/catch。这个回调挂在 UnityEvent 上，异常会顺着
        /// `Button.onClick.Invoke` 往 Unity 的输入处理里冒 —— 那会把**这一整次点击处理**
        /// 打断（onClick 的监听者循环、以及 EventSystem 随后的收尾），
        /// 表现是"点了这个之后别的按钮也不响应了"，而且日志里未必有线索。
        /// 宁可这里哑掉也不能外溢。
        /// </summary>
        private static void GuardedSelect(Preset preset)
        {
            try
            {
                Select(preset);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 切换拍照比例失败: " + e.Message);
            }
        }

        private static void Select(Preset preset)
        {
            // 先让游戏自己切到 Portrait，再把比例纠正成我们的。
            //
            // 顺序和手法都不能换：游戏那套是「CurrentCaptureSize（状态） →
            // 取景框（b__10_0）→ 出片时按 captureSize 分支」三者联动的。早先的版本
            // 只是在出片那一刻偷偷把 captureSize 改成 Portrait，游戏状态和取景框却还
            // 停在别处 —— 三者对不上，整条拍照流程会卡死（快门和退出按钮一起失效）。
            // 只在游戏状态还不是 Portrait 时才推。推过就不再推 —— 重复推会让游戏
            // 重刷一遍开关视觉、把 Portrait 点亮，那就是"跳一帧 9:16"（见 _droveToPortrait）
            if (!_droveToPortrait)
            {
                // 夹住：推状态会让游戏顺手点亮它自己的 Portrait 开关，那一帧就是
                // "跳一下 9:16"。Arm 让紧接着那次点亮被吃掉，推完立刻 Disarm ——
                // 游戏那条刷新是同步的（同一帧内完成），所以这个窗口是精确的。
                PortraitToggleSuppressHook.Arm();
                RequestNativePortrait();
                PortraitToggleSuppressHook.Disarm();

                _droveToPortrait = true;
            }

            _selected = preset;

            RefreshToggleVisuals();
            ApplyCropArea(preset.Ratio);
        }

        /// <summary>
        /// 借游戏自己的 Presenter 把 CurrentCaptureSize 设成 Portrait。
        ///
        /// 走它的理由：这样 CurrentCaptureSize 是由**游戏自己的代码**构造和写入的，
        /// 我们不用去凭空造一个 CurrentCaptureSize（那要传装箱的枚举，是这个项目里
        /// 踩过两次的静默失效类型）。
        ///
        /// 为什么是 Portrait 而不是别的：出片链路只对 1/2 这两个值走"按比例裁切"分支，
        /// 其它值一律不裁切。选 1 是因为它是竖构图 —— 和我们的预设同为"非全屏"语义，
        /// 而且它自己的比例会被下面的 aspect 补丁换掉，选哪个都只是借个路口。
        /// </summary>
        private static void RequestNativePortrait()
        {
            var installer = NativeRefs.FindComponent(GamePaths.CaptureSizeMenu, MenuInstallerTypeName);
            var presenter = installer == null
                ? null
                : Il2CppReflection.FindIl2CppField(installer.GetIl2CppType(), "_presenter")?.GetValue(installer);

            var wrapper = Il2CppReflection.WrapAsManaged(presenter, PresenterTypeName);
            if (wrapper == null)
            {
                MelonLogger.Warning("[BetterCamera] 拿不到拍照尺寸菜单的 Presenter，比例可能不生效");
                return;
            }

            try
            {
                // 按签名找：Presenter 上只有那个处理点击的 lambda 收 CaptureSize
                foreach (var method in wrapper.GetType().GetMethods(
                             System.Reflection.BindingFlags.Instance |
                             System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType.Name != "CaptureSize") continue;

                    method.Invoke(wrapper, new object[] { CaptureSize.Portrait });
                    return;
                }

                MelonLogger.Warning("[BetterCamera] Presenter 上没有收 CaptureSize 的方法，比例可能不生效");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 切换原生尺寸失败: " + e.Message);
            }
        }

        /// <summary>
        /// 只让当前选中的那个开着。
        ///
        /// 我们那四个好办；原生那三个才是麻烦：本 mod 不写 CurrentCaptureSize，
        /// 所以游戏那套「key == 当前值」的刷新根本不会因为我们而被触发，
        /// 结果是旧的新的同时亮着。够不到字典，但够得到 GameObject ——
        /// Body 下面就是 CaptureSizeOption_*（我们的叫 P_BCCaptureSizeOption_*，前缀不同不会误伤）。
        ///
        /// 只在"选中的是我们的预设"时才去关原生 —— 点原生选项时游戏自己会刷对，
        /// 那时去关反而会把它刚点亮的那个按灭。
        /// </summary>
        private static void RefreshToggleVisuals()
        {
            foreach (var p in Presets)
                SetToggleVisual(p.Toggle, ReferenceEquals(p, _selected));

            if (_selected == null) return;

            var body = GameObject.Find(GamePaths.CaptureSizeMenuBody);
            if (body == null) return;

            for (int i = 0; i < body.transform.childCount; i++)
            {
                var child = body.transform.GetChild(i);
                if (!child.name.StartsWith(NativeOptionPrefix, StringComparison.Ordinal)) continue;

                SetToggleVisual(NativeRefs.FindComponent(child.Find(OptionNodeName), ToggleTypeName), false);
            }
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
        /// </summary>
        private static void ApplyCropArea(float ratio)
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
                _cropMethod.Invoke(view, new Il2CppSystem.Object[] { Il2CppReflection.BoxFloat(ratio) });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 更新取景框失败: " + e.Message);
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

            _cropMethod = Il2CppReflection.FindIl2CppMethod(view.GetIl2CppType(), "UpdateCropArea");
            if (_cropMethod == null)
                MelonLogger.Warning("[BetterCamera] 找不到取景框的 UpdateCropArea，比例预览不会更新");

            return _cropMethod != null;
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

        private static void InvokeBool(Il2CppSystem.Object target, string methodName, bool value)
        {
            if (target == null) return;

            try
            {
                Il2CppReflection.FindIl2CppMethod(target.GetIl2CppType(), methodName)
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
        ///
        /// 而本 mod 刻意**不**调 InitBehaviour（见 BuildOption 的说明：它会去初始化
        /// 「点击 → 音效」那条链，而音效播放器是 Zenject 注入的、克隆体上永远是 null）。
        /// 后果就是**我们克隆出来的那 4 个开关 `_wasInit` 恒为 false**，那个守卫对它们
        /// 恒真 —— 同值调用也会取消并重建 _beforeSelectCTS、重播一遍 _onOnTimeline /
        /// _onOffTimeline。重申逻辑每帧调一次的话，就是每帧播一遍动画，
        /// 反而把"选中跳变"做得更明显。
        ///
        /// 原生那 3 个开关 _wasInit 是 true，同值本来就是 no-op；统一走这里更稳妥。
        /// </summary>
        /// <param name="force">
        /// 第一次初始化视觉时要 true —— 那时 _isOn 还是默认的 false，靠读值判断会误跳过，
        /// 而那次调用真正的作用是让 UpdateIsOn 走一遍完整流程把视觉建起来。
        /// </param>
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
