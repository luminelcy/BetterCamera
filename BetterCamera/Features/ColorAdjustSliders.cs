using System;
using MelonLoader;
using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 把 ColorAdjustments 的参数接成滑条，放进滤镜菜单。
    ///
    /// 为什么能做：URP 的 VolumeProfile 里本来就挂着 ColorAdjustments 组件（和 ColorLookup、
    /// WhiteBalance 并列）。游戏的后处理响应式变量一共四个 —— FilterId / Exposure /
    /// Temperature / EffectId，对应 ColorLookup、ColorAdjustments.postExposure、
    /// WhiteBalance、EffectVolume；也就是说 ColorAdjustments 里**只有 postExposure 被游戏用了**，
    /// 剩下三个（hueShift / saturation / contrast）在整个 game assembly 里只出现过声明，
    /// 完全空闲，是纯 mod 扩展空间。
    ///
    /// ⚠️ 所以这里**没有曝光滑条**：游戏自己的「曝光」写的就是同一个
    /// ColorAdjustments.postExposure（同一个 BaseVolume），再做一个就是重复的，
    /// 两边还会互相覆盖。要调曝光用原生那个标签页。
    ///
    /// UI 完全复用原生结构：克隆 ExposureAndTemperatureLayout 拿到同样的容器样式，
    /// 再把里面的滑条单元（标签 + 滑条）克隆成四条。位置和外观与其他面板一致。
    ///
    /// 注意参数必须 overrideState = true 才会生效 —— URP 只应用被 override 的参数。
    ///
    /// 标签页的显隐不在这里管，交给 FilterMenuVisibilityHook（它 patch 了原生的
    /// SetFilterMenuObjectVisible，在游戏自己的切换逻辑之后补一刀）。这里只负责把面板
    /// 和标签按钮建出来、登记过去。
    /// </summary>
    public static class ColorAdjustSliders
    {
        private const string ComponentTypeName = "UnityEngine.Rendering.Universal.ColorAdjustments";
        private const string SlidersLayoutName = "SlidersLayout";
        private const string SliderUnitName = "ExposureSliderLayout";
        private const string ResetButtonPrefix = "P_Reset";
        private const string SliderLeafPath = "SliderLayout/P_PostProcessExposureSliderObject/Slider/Slider";

        private sealed class Knob
        {
            public string CloneName;    // 克隆体名字（UI 契约）
            public string Param;        // ColorAdjustments 上的字段名
            public LabelKey Key;        // 显示给玩家的名字（按当前语言查表）
            public float Min, Max;
            public Il2CppSystem.Object Slider;
            public Il2CppSystem.Object Parameter;
            public Il2CppSystem.Object LabelTmp;
        }

        private static readonly Knob[] Knobs =
        {
            new Knob { CloneName = "P_BCColorAdjustHue",        Param = "hueShift",     Key = LabelKey.Hue,        Min = -180f, Max = 180f },
            new Knob { CloneName = "P_BCColorAdjustSaturation", Param = "saturation",   Key = LabelKey.Saturation, Min = -100f, Max = 100f },
            new Knob { CloneName = "P_BCColorAdjustContrast",   Param = "contrast",     Key = LabelKey.Contrast,   Min = -100f, Max = 100f },
        };

        private const string LabelNodeName = "CommonLocalizeText";

        /// <summary>
        /// 注意前缀：Il2CppInterop 会给会和 .NET 撞名的命名空间加 Il2Cpp（Project → Il2CppProject
        /// 也是同一回事），TMPro 就在这个名单里。写 "TMPro.TextMeshProUGUI" 是查不到的，
        /// FindType 返回 null，然后一切静默失效。
        /// </summary>
        private const string TmpTypeName = "Il2CppTMPro.TextMeshProUGUI";
        private const string LocalizeStringEventTypeName =
            "UnityEngine.Localization.Components.LocalizeStringEvent";
        private const string TabViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.FilterMenuTabButtonObject.FilterMenuTabButtonObjectView";
        private const string TabInstallerTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.FilterMenuTabButtonObject.FilterMenuTabButtonObjectInstaller";
        private const string ResetViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.ResetExposureAndTemperatureButtonObject.ResetExposureAndTemperatureButtonObjectView";
        private static Il2CppSystem.Type _paramType;

        /// <summary>标签页按钮上的 TMP（文字挂在 FilterMenuTabButtonObjectView._buttonText 上）。</summary>
        private static Il2CppSystem.Object _tabLabel;

        /// <summary>
        /// 文字用哪个语言，固定中文。
        ///
        /// 跟随游戏语言的那套（GameLanguage + 每 30 帧扫一次）暂时停用 —— 要恢复的话：
        ///   1. 这里改回 <c>RefreshLabels(GameLanguage.Current())</c>
        ///   2. Core.OnUpdate 里恢复 <c>ColorAdjustSliders.SyncLanguage()</c>
        ///   3. 把 SyncLanguage 和那个帧计数加回来
        /// </summary>
        private const string FixedLanguage = "zh-Hans";

        public static void Init()
        {
            if (!CacheTarget()) return;

            var container = BuildUi();
            if (container == null) return;

            var tabView = BuildTab();

            // 登记之后面板立刻被隐藏，往后显隐全部由原生标签切换机制驱动。
            // tabView 可能为 null（标签没建起来），那时面板就一直藏着 —— 但不影响其余功能。
            FilterMenuVisibilityHook.Register(container, tabView);

            // 容器建好了但拿不到滑条的，单独跳过；能接的先接上
            int wired = 0;
            foreach (var k in Knobs)
            {
                if (k.Slider == null) continue;
                if (Wire(k)) wired++;
            }

            if (wired > 0)
            {
                // 全部归零，避免一上来画面就变了
                foreach (var k in Knobs)
                    if (k.Parameter != null) Apply(k, 0f);
            }

            // 标签一个都找不到就是结构变了 —— 这种情况必须报出来，
            // 因为"每条标签都显示着从模板带过来的文字"看着像正常，很容易没人发现
            if (Knobs[0].LabelTmp == null)
                MelonLogger.Warning("[BetterCamera] 找不到滑条标签的 TMP，ColorAdjust 的文字不会被替换");

            RefreshLabels(FixedLanguage);
        }

        /// <summary>找 BaseVolume profile 里那个 ColorAdjustments 实例，并缓存参数写入方法。</summary>
        private static bool CacheTarget()
        {
            var volumeType = NativeRefs.TypeOf("UnityEngine.Rendering.Volume");
            var profileType = NativeRefs.TypeOf("UnityEngine.Rendering.VolumeProfile");
            var compType = NativeRefs.TypeOf(ComponentTypeName);
            _paramType = NativeRefs.TypeOf("UnityEngine.Rendering.FloatParameter")
                      ?? NativeRefs.TypeOf("UnityEngine.Rendering.ClampedFloatParameter");
            if (volumeType == null || profileType == null || compType == null || _paramType == null) return false;

            var volume = NativeRefs.FindVolume(GamePaths.BaseVolume);
            if (volume == null) return false;

            // profile 是属性，getter 首次访问时会建运行时副本，改它不污染游戏资源
            var profile = Il2CppReflection.FindIl2CppMethod(volumeType, "get_profile")?.Invoke(volume, null);
            if (profile == null) return false;

            var comps = Il2CppReflection.FindIl2CppField(profileType, "components")?.GetValue(profile);
            if (comps == null) return false;

            var listType = comps.GetIl2CppType();
            var getCount = Il2CppReflection.FindIl2CppMethod(listType, "get_Count");
            var getItem = Il2CppReflection.FindIl2CppMethod(listType, "get_Item");
            if (getCount == null || getItem == null) return false;

            int count = Il2CppReflection.UnboxInt(getCount.Invoke(comps, null));
            for (int i = 0; i < count; i++)
            {
                var elem = getItem.Invoke(comps, new Il2CppSystem.Object[] { Il2CppReflection.BoxInt(i) });
                if (elem == null) continue;
                if (elem.GetIl2CppType()?.Name != "ColorAdjustments") continue;

                foreach (var k in Knobs)
                {
                    var f = Il2CppReflection.FindIl2CppField(compType, k.Param);
                    k.Parameter = f?.GetValue(elem);
                }
                return true;
            }

            MelonLogger.Error("[BetterCamera] BaseVolume 的 profile 里没有 ColorAdjustments");
            return false;
        }

        /// <summary>克隆原生容器和滑条单元，摆进 ControlsLayout。返回新面板，失败返回 null。</summary>
        private static GameObject BuildUi()
        {
            var template = GameObject.Find(GamePaths.ExposureAndTemperatureLayout);
            var parent = GameObject.Find(GamePaths.ControlsLayout);
            if (template == null || parent == null)
            {
                MelonLogger.Warning("[BetterCamera] 滤镜菜单结构变了，ColorAdjust 面板未创建");
                return null;
            }

            var container = UnityEngine.Object.Instantiate(template, parent.transform);
            container.name = GamePaths.NameColorAdjustLayout;

            WireResetButton(container);

            var slidersLayout = container.transform.Find(SlidersLayoutName);
            if (slidersLayout == null) { MelonLogger.Warning("[BetterCamera] 克隆体里没有 SlidersLayout"); return null; }

            // 原模板里有两个滑条单元（曝光 / 色温）。只留一个当模板，克隆出我们需要的条数。
            Transform unit = slidersLayout.Find(SliderUnitName);
            if (unit == null && slidersLayout.childCount > 0)
                unit = slidersLayout.GetChild(0);
            if (unit == null) { MelonLogger.Warning("[BetterCamera] 找不到滑条单元模板"); return null; }

            // 只留第一个，其余（色温）删掉
            for (int i = slidersLayout.childCount - 1; i >= 0; i--)
            {
                var c = slidersLayout.GetChild(i);
                if (c != unit) UnityEngine.Object.Destroy(c.gameObject);
            }

            unit.name = Knobs[0].CloneName;
            Knobs[0].LabelTmp = FindLabel(unit);
            Knobs[0].Slider = FindSliderIn(unit);

            for (int i = 1; i < Knobs.Length; i++)
            {
                var clone = UnityEngine.Object.Instantiate(unit, slidersLayout);
                clone.name = Knobs[i].CloneName;
                Knobs[i].LabelTmp = FindLabel(clone);
                Knobs[i].Slider = FindSliderIn(clone);
            }

            // 文字由本 mod 自己写，先把面板里克隆来的本地化事件让开（见 SilenceLocalization）
            SilenceLocalization(container.transform);

            return container;
        }

        /// <summary>
        /// 找到滑条单元里的标签 TMP 并记下来，文字统一交给 RefreshLabels 写。
        ///
        /// TMP 不在 CommonLocalizeText 它自己身上，而在它的子节点上（实测叫 "Text (TMP)"）。
        /// 早先直接对 CommonLocalizeText 取 TextMeshProUGUI，拿到的是 null，写入静默失效 ——
        /// 结果是四条标签一直显示克隆时从模板带过来的文字，全都写着"曝光"。
        /// 这里改成按组件在子节点里找，不写死子节点名（名字是随版本变的，而这个节点存在的
        /// 意义就是"里面有个 TMP"）。
        /// </summary>
        private static Il2CppSystem.Object FindLabel(Transform unit)
        {
            var label = unit.Find(LabelNodeName);
            if (label == null) return null;

            for (int i = 0; i < label.childCount; i++)
            {
                var tmp = NativeRefs.FindComponent(label.GetChild(i), TmpTypeName);
                if (tmp != null) return tmp;
            }
            return null;
        }

        /// <summary>
        /// 关掉子树里所有的 LocalizeStringEvent。
        ///
        /// 本 mod 的面板和标签按钮都是克隆来的，这些节点上的本地化事件指向的是模板
        /// （曝光/色温面板）的 key —— 它们会把我们写进去的文字按游戏自己的 key 覆盖回去。
        /// 文字既然由本 mod 自己管，就得先把它们让开。
        ///
        /// 只关字符串事件，不动 LocalizeTmpFontEvent —— 字体该跟着语言走，那正是我们要的。
        /// </summary>
        private static void SilenceLocalization(Transform node)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);

                var localizer = NativeRefs.FindComponent(child, LocalizeStringEventTypeName);
                if (localizer != null)
                {
                    try
                    {
                        Il2CppReflection.FindIl2CppMethod(localizer.GetIl2CppType(), "set_enabled")
                            ?.Invoke(localizer, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(false) });
                    }
                    catch { }
                }

                SilenceLocalization(child);
            }
        }

        /// <summary>按给定语言码写全部文字（滑条标签 + 标签页按钮）。</summary>
        private static void RefreshLabels(string language)
        {
            foreach (var k in Knobs)
                SetTmpText(k.LabelTmp, ColorAdjustLabels.Get(k.Key, language));

            SetTmpText(_tabLabel, ColorAdjustLabels.Get(LabelKey.ColorTab, language));
        }

        /// <summary>
        /// 写 TextMeshProUGUI 的文字。
        ///
        /// set_text 声明在基类 TMP_Text 上，不是 TextMeshProUGUI 自己 —— 这里显式从 TMP_Text
        /// 上取，不依赖"反射会沿继承链找到继承成员"这条没验证过的假设。
        ///
        /// 找不到必须报出来：早先这里写的是 <c>FindIl2CppMethod(...)?.Invoke(...)</c>，
        /// 找不到就静默跳过，于是"文字一直写不进去"这件事从头到尾没有任何迹象。
        /// </summary>
        private static void SetTmpText(Il2CppSystem.Object tmp, string text)
        {
            if (tmp == null) return;

            try
            {
                // 走 Il2CppInterop 生成的托管包装：参数是普通 C# string，编组由它负责。
                // 直接走 il2cpp 反射传手工装箱的字符串实测无效（见 Il2CppReflection.WrapAsManaged）。
                var wrapper = Il2CppReflection.WrapAsManaged(tmp, TmpTypeName);
                var textProperty = wrapper?.GetType().GetProperty("text");

                if (textProperty != null && textProperty.CanWrite)
                {
                    textProperty.SetValue(wrapper, text);

                    // 回读确认。单看这行像是多余的，但"写进去了吗"这件事早先没有任何迹象：
                    // 写入走的是静默路径，失败时既不抛异常也不留痕，表现只是标签显示着
                    // 克隆时从模板带过来的文字 —— 看着完全正常。
                    if ((textProperty.GetValue(wrapper) as string) != text)
                        MelonLogger.Warning("[BetterCamera] 文字写入没有生效，ColorAdjust 的标签可能不对");

                    return;
                }

                MelonLogger.Warning("[BetterCamera] 拿不到 TMP 的 text 属性，ColorAdjust 的文字写不进去");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 设置文字失败: " + e.Message);
            }
        }

        private static Il2CppSystem.Object FindSliderIn(Transform unit)
        {
            var go = unit.Find(SliderLeafPath);
            return go == null ? null : NativeRefs.FindComponent(go, "Il2CppProject.NoArrowMovableSlider");
        }

        /// <summary>
        /// 克隆一个原生标签按钮，接到本 mod 的页上。返回它的 View（供 FilterMenuVisibilityHook
        /// 做选中态配色），失败返回 null。
        ///
        /// 只借外观，不借逻辑：
        ///   - View 留着 —— 用游戏自己的 SetSelected 上选中/未选中配色
        ///   - 点击是**追加**到原生 Button 上的（见 TakeButton），不是把游戏的监听清掉
        ///     再换成我们的。上一版正是栽在 RemoveAllListeners 上：它同时把
        ///     CommonButtonBehaviour 自己的监听抹了，把按钮的内部状态搞坏
        /// </summary>
        private static Il2CppSystem.Object BuildTab()
        {
            var template = GameObject.Find(GamePaths.NativeTabButtonTemplate);
            var parent = GameObject.Find(GamePaths.TabButtonsLayout);
            if (template == null || parent == null)
            {
                MelonLogger.Warning("[BetterCamera] 找不到标签按钮模板，ColorAdjust 页没有入口");
                return null;
            }

            var tab = UnityEngine.Object.Instantiate(template, parent.transform);
            tab.name = GamePaths.NameColorAdjustTabButton;

            SetTabType(tab);

            var view = NativeRefs.FindComponent(tab.transform, TabViewTypeName);
            if (view == null) { MelonLogger.Warning("[BetterCamera] 克隆体上没有 FilterMenuTabButtonObjectView"); return null; }

            // 标签文字挂在 View._buttonText 上；具体写什么由 RefreshLabels 按当前语言决定
            _tabLabel = Il2CppReflection.FindIl2CppField(view.GetIl2CppType(), "_buttonText")?.GetValue(view);
            SilenceLocalization(tab.transform);

            var button = TakeButton(view);
            if (button == null) { MelonLogger.Warning("[BetterCamera] 拿不到标签按钮的 Button，ColorAdjust 页没有入口"); return null; }

            UnityEventBridge.AddClickListener(button, FilterMenuVisibilityHook.ShowColorAdjust);
            return view;
        }

        /// <summary>
        /// 把面板里那个原生复位按钮接管过来，用来重置本面板的四条参数。
        ///
        /// 图标和外观直接沿用原生的（面板是克隆 ExposureAndTemperatureLayout 来的，复位按钮
        /// 本来就跟着一起被复制了）—— 就是 DutchReset 那种"借图标"的做法，只不过这里连借
        /// 都不用，现成的。
        ///
        /// 注意克隆体同样带着自己的 Zenject 上下文，所以原生那个"重置曝光/色温"的行为可能
        /// 也还挂着。这一条没有确认过（上下文到底会不会初始化，见 SetTabType 的说明），
        /// 目前的写法是两边并存 —— 我们的监听是追加的，不挤掉谁。
        /// </summary>
        private static void WireResetButton(GameObject container)
        {
            for (int i = 0; i < container.transform.childCount; i++)
            {
                var child = container.transform.GetChild(i);
                if (!child.name.StartsWith(ResetButtonPrefix, StringComparison.Ordinal)) continue;

                var view = NativeRefs.FindComponent(child, ResetViewTypeName);
                var behaviour = view == null
                    ? null
                    : Il2CppReflection.FindIl2CppField(view.GetIl2CppType(), "_button")?.GetValue(view);

                // 和标签按钮一样，Button 的位置不猜：从 CommonButtonBehaviour._button 拿
                var button = behaviour == null
                    ? null
                    : Il2CppReflection.FindIl2CppField(behaviour.GetIl2CppType(), "_button")?.GetValue(behaviour);

                if (button == null)
                {
                    MelonLogger.Warning("[BetterCamera] 复位按钮结构不对，ColorAdjust 面板没有重置入口");
                    return;
                }

                UnityEventBridge.AddClickListener(button, ResetAll);
                return;
            }
        }

        /// <summary>四条参数全部归零，滑条手柄一起回到中点。</summary>
        private static void ResetAll()
        {
            foreach (var k in Knobs)
            {
                if (k.Parameter != null) Apply(k, 0f);
                // sendCallback = false：值是我们自己写的，别再折回来触发一次 Apply
                if (k.Slider != null) SliderKit.SetValueQuiet(k.Slider, 0f);
            }
        }

        /// <summary>
        /// 改掉克隆体 Installer 上序列化的 <c>_filterMenuType</c>，让它代表本 mod 的标签页。
        ///
        /// 克隆体不是一个空壳：它带着自己的 Zenject 三件套（GameObjectContext + Installer +
        /// DefaultGameObjectKernel），和原生标签按钮在结构上完全一样。Installer 上序列化的
        /// _filterMenuType 就是「这个按钮代表哪一页」，随模板一起被复制成了「曝光·色温」——
        /// 于是它自己的 Presenter 会一直把它按曝光页的选中态来点亮，这也是它看着"卡在
        /// hover"的由来之一。
        ///
        /// 改成 3 之后，如果它的 Zenject 上下文会初始化，点它就走完整原生链路：
        /// Presenter 写 CurrentFilterMenuType=3 → 游戏自己去显示本页、熄灭另外三个。
        /// 这是一层保险，不是唯一依赖 —— 上下文到底会不会跑我没有确认过，所以
        /// 点击监听仍然自己挂着（见 BuildTab），两条路同时成立也不会互相打架。
        ///
        /// 必须赶在上下文初始化之前写。克隆体此时还是未激活状态（滤镜菜单关着），
        /// Awake 没跑过，所以紧接着 Instantiate 写就是安全的；万一不是，_presenter
        /// 会有值，那时候补写已经晚了，宁可报出来也不要静默失效。
        /// </summary>
        private static void SetTabType(GameObject tab)
        {
            var installer = NativeRefs.FindComponent(tab.transform, TabInstallerTypeName);
            if (installer == null)
            {
                MelonLogger.Warning("[BetterCamera] 克隆体上没有标签 Installer，ColorAdjust 页的选中态可能不对");
                return;
            }

            var type = installer.GetIl2CppType();
            if (Il2CppReflection.FindIl2CppField(type, "_presenter")?.GetValue(installer) != null)
            {
                MelonLogger.Warning("[BetterCamera] 标签 Installer 已经初始化过了，改不到它的 FilterMenuType");
                return;
            }

            var field = Il2CppReflection.FindIl2CppField(type, "_filterMenuType");
            if (field == null)
            {
                MelonLogger.Warning("[BetterCamera] 标签 Installer 上没有 _filterMenuType");
                return;
            }

            // 装箱的 Int32 写进 4 字节的枚举字段 —— 和 SliderKit.SetDirection 写 m_Direction 同一个路子
            Il2CppReflection.SetIntField(installer, field, FilterMenuVisibilityHook.ColorAdjustMenuType);
        }

        /// <summary>
        /// 取出标签按钮实际点击的那个 Button。返回 null 表示结构不对，调用方应该放弃建这个标签。
        ///
        /// 不按路径猜 Button 挂在哪个子节点 —— 从 CommonButtonBehaviour._button 字段拿，
        /// 那是游戏自己存的引用。也就是说不去动克隆体上的 CommonButtonBehaviour：
        /// 它那个会把音效播放器（Zenject 注入，克隆体上永远是 null）拖进来的点击链，
        /// 因为克隆体没有 Presenter、没人订阅它的 Observable，走不起来。
        /// 我们的监听是追加到同一个 Button 上，不会把谁挤掉。
        /// </summary>
        private static Il2CppSystem.Object TakeButton(Il2CppSystem.Object view)
        {
            var behaviour = Il2CppReflection
                .FindIl2CppField(view.GetIl2CppType(), "_commonButtonBehaviour")?.GetValue(view);
            if (behaviour == null) return null;

            return Il2CppReflection
                .FindIl2CppField(behaviour.GetIl2CppType(), "_button")?.GetValue(behaviour);
        }

        private static bool Wire(Knob k)
        {
            SliderKit.SetRange(k.Slider, k.Min, k.Max);
            SliderKit.RefreshVisuals(k.Slider);
            SliderKit.SetValueQuiet(k.Slider, 0f);

            return UnityEventBridge.AddFloatListener(k.Slider, v => Apply(k, v));
        }

        /// <summary>写参数值。必须先打开 overrideState，否则 URP 不会应用这个参数。</summary>
        private static void Apply(Knob k, float value)
        {
            if (k.Parameter == null || _paramType == null) return;
            try
            {
                Il2CppReflection.FindIl2CppMethod(_paramType, "set_overrideState")
                    ?.Invoke(k.Parameter, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(true) });
                Il2CppReflection.FindIl2CppMethod(_paramType, "set_value")
                    ?.Invoke(k.Parameter, new Il2CppSystem.Object[] { Il2CppReflection.BoxFloat(value) });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] ColorAdjust 写入失败: " + e.Message);
            }
        }
    }
}
