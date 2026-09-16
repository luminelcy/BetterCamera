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
    /// 为什么能做：URP 的 VolumeProfile 里本来就挂着 ColorAdjustments 组件
    /// （和 ColorLookup、WhiteBalance 并列），但游戏只把 ColorLookup→FilterId、
    /// WhiteBalance→Temperature 做成了控件，ColorAdjustments 的五个参数一个都没用。
    /// 这几个参数完全空闲，是纯 mod 扩展空间。
    ///
    /// UI 完全复用原生结构：克隆 ExposureAndTemperatureLayout 拿到同样的容器样式，
    /// 再把里面的滑条单元（标签 + 滑条）克隆成四条。位置和外观与其他面板一致。
    ///
    /// 注意参数必须 overrideState = true 才会生效 —— URP 只应用被 override 的参数。
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
            public string Label;        // 显示给玩家的名字
            public float Min, Max;
            public Il2CppSystem.Object Slider;
            public Il2CppSystem.Object Parameter;
        }

        private static readonly Knob[] Knobs =
        {
            new Knob { CloneName = "P_BCColorAdjustHue",        Param = "hueShift",     Label = "Hue",        Min = -180f, Max = 180f },
            new Knob { CloneName = "P_BCColorAdjustSaturation", Param = "saturation",   Label = "Saturation", Min = -100f, Max = 100f },
            new Knob { CloneName = "P_BCColorAdjustContrast",   Param = "contrast",     Label = "Contrast",   Min = -100f, Max = 100f },
            new Knob { CloneName = "P_BCColorAdjustExposure",   Param = "postExposure", Label = "Exposure",   Min = -3f,   Max = 3f   },
        };

        private const string LabelNodeName = "CommonLocalizeText";
        private const string TmpTypeName = "TMPro.TextMeshProUGUI";
        /// <summary>标签页按钮上的文字。</summary>
        private const string TabLabel = "Color";

        private static Il2CppSystem.Type _paramType;

        /// <summary>FilterMenuType 只用了 0/1/2（Filter / ExposureAndColorTemperature / Effects），3 是空闲的。</summary>
        private const int MyMenuTypeValue = 3;

        public static void Init()
        {
            if (!CacheTarget()) return;

            var container = BuildUi();
            if (container == null) return;

            // 标签按钮暂时停用。
            //
            // 症状：点一次就卡死游戏，且面板没被正常显示/隐藏。
            // 原因：往 _filterMenuObjects 字典注册时，键用的是装箱的 Int32，
            // 而字典的键类型是枚举 FilterMenuType —— 装箱类型对不上，
            // Add 走的很可能是 IDictionary.Add(object,object) 那个非泛型重载，
            // 于是存进去的键类型错误，游戏按枚举查不到，内部状态被打乱。
            //
            // 要修得先把键装箱成真正的 FilterMenuType（il2cpp 侧按枚举类装箱），
            // 并且确认拿到的是泛型 Add 而不是非泛型那个。
            // 在此之前只保留面板（默认隐藏），不再创建标签按钮。
            // BuildTab(container);

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

            // 默认隐藏。
            // 原生面板的显隐由 FilterMenuObjectView.SetFilterMenuObjectVisible 通过 CanvasGroup 控制，
            // 而我们的面板不在它的显隐体系内（见 BuildTab 的说明），不主动藏起来就会一直叠在
            // 当前标签的上面。
            HideContainer(container);

            // 克隆出来的重置按钮会带着游戏的 onClick，点下去会去重置曝光/色温。
            // 跟本面板无关，去掉免得误导。
            for (int i = container.transform.childCount - 1; i >= 0; i--)
            {
                var c = container.transform.GetChild(i);
                if (c.name.StartsWith(ResetButtonPrefix, StringComparison.Ordinal))
                    UnityEngine.Object.Destroy(c.gameObject);
            }

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
            SetLabelText(unit, Knobs[0].Label);
            Knobs[0].Slider = FindSliderIn(unit);

            for (int i = 1; i < Knobs.Length; i++)
            {
                var clone = UnityEngine.Object.Instantiate(unit, slidersLayout);
                clone.name = Knobs[i].CloneName;
                SetLabelText(clone, Knobs[i].Label);
                Knobs[i].Slider = FindSliderIn(clone);
            }

            return container;
        }

        /// <summary>
        /// 直接改 TextMeshProUGUI 的文字。
        ///
        /// 为什么不走游戏自己的本地化：CommonLocalizeTextBehaviour 用的是
        /// LocalizeTextKey 枚举，mod 加不了新 key。直接写 TMP 省事，
        /// 代价是不随语言切换 —— 这几个名字用英文就够。
        /// </summary>
        private static void SetLabelText(Transform unit, string text)
        {
            var label = unit.Find(LabelNodeName);
            if (label == null) return;
            SetTmpText(NativeRefs.FindComponent(FullPath(label), TmpTypeName), text);
        }

        /// <summary>标签按钮的文字挂在 FilterMenuTabButtonObjectView._buttonText 上。</summary>
        private static void SetTabText(string tabPath, string text)
        {
            var view = NativeRefs.FindComponent(tabPath,
                "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.FilterMenuTabButtonObject.FilterMenuTabButtonObjectView");
            if (view == null) return;

            var tmp = Il2CppReflection.FindIl2CppField(view.GetIl2CppType(), "_buttonText")?.GetValue(view);
            SetTmpText(tmp, text);
        }

        private static void SetTmpText(Il2CppSystem.Object tmp, string text)
        {
            if (tmp == null) return;
            try
            {
                Il2CppReflection.FindIl2CppMethod(tmp.GetIl2CppType(), "set_text")
                    ?.Invoke(tmp, new Il2CppSystem.Object[] { Il2CppReflection.BoxString(text) });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 设置文字失败: " + e.Message);
            }
        }

        /// <summary>按 CanvasGroup 藏起面板 —— 和原生隐藏面板时的做法一致。</summary>
        private static void HideContainer(GameObject container)
        {
            var cg = NativeRefs.FindComponent(FullPath(container.transform), "UnityEngine.CanvasGroup");
            if (cg == null) return;

            try
            {
                var t = cg.GetIl2CppType();
                Il2CppReflection.FindIl2CppMethod(t, "set_alpha")
                    ?.Invoke(cg, new Il2CppSystem.Object[] { Il2CppReflection.BoxFloat(0f) });
                Il2CppReflection.FindIl2CppMethod(t, "set_interactable")
                    ?.Invoke(cg, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(false) });
                Il2CppReflection.FindIl2CppMethod(t, "set_blocksRaycasts")
                    ?.Invoke(cg, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(false) });
            }
            catch { }
        }

        private static Il2CppSystem.Object FindSliderIn(Transform unit)
        {
            var go = unit.Find(SliderLeafPath);
            if (go == null) return null;
            return NativeRefs.FindComponent(FullPath(go), "Il2CppProject.NoArrowMovableSlider");
        }

        /// <summary>把 Transform 还原成 GameObject.Find 能用的层级路径。</summary>
        private static string FullPath(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            var cur = t.parent;
            while (cur != null) { sb.Insert(0, cur.name + "/"); cur = cur.parent; }
            return sb.ToString();
        }

        /// <summary>
        /// 建一个真正属于本 mod 的标签页。
        ///
        /// 原生的切换链路是：
        ///     点标签 → 设 CurrentFilterMenuType → Presenter 观察者触发
        ///       → View.SetFilterMenuObjectVisible(type)
        ///         → 查 _filterMenuObjects 字典 → 只显示命中的那个面板
        ///
        /// 所以我们把「新枚举值 → 本面板的 CanvasGroup」也塞进那个字典，再克隆一个标签按钮，
        /// 点击时直接调 SetFilterMenuObjectVisible。于是：
        ///   - 点我们的标签：走游戏自己的机制显示本面板、隐藏其他
        ///   - 点原生标签：游戏自己的机制同样会隐藏本面板（我们的条目就在它查的那个字典里）
        /// 完全不用自己写显示/隐藏逻辑，也不会和游戏打架。
        ///
        /// FilterMenuType 只用了 0/1/2，这里占 3。
        /// </summary>
        private static void BuildTab(GameObject container)
        {
            var view = NativeRefs.FindComponent(GamePaths.FilterMenuObject,
                "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.FilterMenuObjectView");
            if (view == null) { MelonLogger.Warning("[BetterCamera] 拿不到 FilterMenuObjectView，标签未建"); return; }

            var viewType = view.GetIl2CppType();
            var setVisible = Il2CppReflection.FindIl2CppMethod(viewType, "SetFilterMenuObjectVisible");
            if (setVisible == null) { MelonLogger.Warning("[BetterCamera] 找不到 SetFilterMenuObjectVisible"); return; }

            // ① 把新条目塞进「标签类型 → CanvasGroup」的字典
            var dict = Il2CppReflection.FindIl2CppField(viewType, "_filterMenuObjects")?.GetValue(view);
            if (dict == null) { MelonLogger.Warning("[BetterCamera] 拿不到 _filterMenuObjects"); return; }

            var canvasGroup = NativeRefs.FindComponent(FullPath(container.transform), "UnityEngine.CanvasGroup");
            if (canvasGroup == null) { MelonLogger.Warning("[BetterCamera] 面板没有 CanvasGroup"); return; }

            var addMethod = FindMethodByParamCount(dict.GetIl2CppType(), "Add", 2);
            if (addMethod == null) { MelonLogger.Warning("[BetterCamera] 字典上没有 Add(K,V)"); return; }

            try
            {
                addMethod.Invoke(dict, new Il2CppSystem.Object[]
                {
                    Il2CppReflection.BoxInt(MyMenuTypeValue),
                    canvasGroup,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 注册标签页失败（枚举键可能没转换成功）: " + e.Message);
                return;
            }

            // ② 克隆标签按钮
            var template = GameObject.Find(GamePaths.NativeTabButtonTemplate);
            var tabParent = GameObject.Find(GamePaths.TabButtonsLayout);
            if (template == null || tabParent == null) { MelonLogger.Warning("[BetterCamera] 找不到标签按钮模板"); return; }

            var tab = UnityEngine.Object.Instantiate(template, tabParent.transform);
            tab.name = GamePaths.NameColorAdjustTabButton;
            var tabPath = FullPath(tab.transform);
            SetTabText(tabPath, TabLabel);

            // 克隆来的按钮带着游戏的 onClick（点下去会切到「曝光/色温」），清掉换成我们的
            ClearClickListeners(tabPath);

            var button = NativeRefs.FindComponent(tabPath, "UnityEngine.UI.Button");
            if (button != null)
                UnityEventBridge.AddClickListener(button, () => ShowTab(setVisible, view));
        }

        private static void ShowTab(Il2CppSystem.Reflection.MethodInfo setVisible, Il2CppSystem.Object view)
        {
            try
            {
                setVisible.Invoke(view, new Il2CppSystem.Object[] { Il2CppReflection.BoxInt(MyMenuTypeValue) });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 切到 ColorAdjust 页失败: " + e.Message);
            }
        }

        private static void ClearClickListeners(string buttonGoPath)
        {
            try
            {
                var button = NativeRefs.FindComponent(buttonGoPath, "UnityEngine.UI.Button");
                if (button == null) return;

                var evt = Il2CppReflection
                    .FindIl2CppField(NativeRefs.TypeOf("UnityEngine.UI.Button"), "m_OnClick")
                    ?.GetValue(button);
                if (evt == null) return;

                Il2CppReflection.FindIl2CppMethod(evt.GetIl2CppType(), "RemoveAllListeners")?.Invoke(evt, null);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 清理标签按钮监听失败: " + e.Message);
            }
        }

        /// <summary>按名字 + 参数个数找方法 —— 反射里同名重载很常见（如 Add 有 IDictionary 的和泛型的）。</summary>
        private static Il2CppSystem.Reflection.MethodInfo FindMethodByParamCount(
            Il2CppSystem.Type type, string name, int paramCount)
        {
            var methods = type.GetMethods(
                Il2CppSystem.Reflection.BindingFlags.Instance |
                Il2CppSystem.Reflection.BindingFlags.Public |
                Il2CppSystem.Reflection.BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != name) continue;
                try { if (methods[i].GetParameters().Length == paramCount) return methods[i]; }
                catch { }
            }
            return null;
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
