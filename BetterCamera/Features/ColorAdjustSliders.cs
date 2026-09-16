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
            public float Min, Max;
            public Il2CppSystem.Object Slider;
            public Il2CppSystem.Object Parameter;
        }

        private static readonly Knob[] Knobs =
        {
            new Knob { CloneName = "P_BCColorAdjustHue",        Param = "hueShift",     Min = -180f, Max = 180f },
            new Knob { CloneName = "P_BCColorAdjustSaturation", Param = "saturation",   Min = -100f, Max = 100f },
            new Knob { CloneName = "P_BCColorAdjustContrast",   Param = "contrast",     Min = -100f, Max = 100f },
            new Knob { CloneName = "P_BCColorAdjustExposure",   Param = "postExposure", Min = -3f,   Max = 3f   },
        };

        private static Il2CppSystem.Type _paramType;

        public static void Init()
        {
            if (!CacheTarget()) return;

            BuildUi();

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

        /// <summary>克隆原生容器和滑条单元，摆进 ControlsLayout。</summary>
        private static void BuildUi()
        {
            var template = GameObject.Find(GamePaths.ExposureAndTemperatureLayout);
            var parent = GameObject.Find(GamePaths.ControlsLayout);
            if (template == null || parent == null)
            {
                MelonLogger.Warning("[BetterCamera] 滤镜菜单结构变了，ColorAdjust 面板未创建");
                return;
            }

            var container = UnityEngine.Object.Instantiate(template, parent.transform);
            container.name = GamePaths.NameColorAdjustLayout;

            // 克隆出来的重置按钮会带着游戏的 onClick，点下去会去重置曝光/色温。
            // 跟本面板无关，去掉免得误导。
            for (int i = container.transform.childCount - 1; i >= 0; i--)
            {
                var c = container.transform.GetChild(i);
                if (c.name.StartsWith(ResetButtonPrefix, StringComparison.Ordinal))
                    UnityEngine.Object.Destroy(c.gameObject);
            }

            var slidersLayout = container.transform.Find(SlidersLayoutName);
            if (slidersLayout == null) { MelonLogger.Warning("[BetterCamera] 克隆体里没有 SlidersLayout"); return; }

            // 原模板里有两个滑条单元（曝光 / 色温）。只留一个当模板，克隆出我们需要的条数。
            Transform unit = slidersLayout.Find(SliderUnitName);
            if (unit == null && slidersLayout.childCount > 0)
                unit = slidersLayout.GetChild(0);
            if (unit == null) { MelonLogger.Warning("[BetterCamera] 找不到滑条单元模板"); return; }

            // 只留第一个，其余（色温）删掉
            for (int i = slidersLayout.childCount - 1; i >= 0; i--)
            {
                var c = slidersLayout.GetChild(i);
                if (c != unit) UnityEngine.Object.Destroy(c.gameObject);
            }

            unit.name = Knobs[0].CloneName;
            Knobs[0].Slider = FindSliderIn(unit);

            for (int i = 1; i < Knobs.Length; i++)
            {
                var clone = UnityEngine.Object.Instantiate(unit, slidersLayout);
                clone.name = Knobs[i].CloneName;
                Knobs[i].Slider = FindSliderIn(clone);
            }
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
