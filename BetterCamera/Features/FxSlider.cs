using System;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 滤镜 / 特效强度滑条。
    ///
    /// 两者逻辑完全相同 —— 一条控制 BaseVolume、一条控制 EffectVolume，
    /// 原实现是两个文件（FX/BaseSlider.cs 与 FX/EffectSlider.cs），全文 diff 只差 3 行
    /// （两个路径常量的值）。这里合成一份，实例化两次。
    ///
    /// 强度是直接写 Volume.weight 的：游戏没有对应原生通道，也不会自己改它，
    /// 所以不存在冲突（跟 FOV 那条不同）。
    /// </summary>
    public static class FxSlider
    {
        private const float MinValue = 0f;
        private const float MaxValue = 1f;
        private const float InitialValue = 1f;

        private sealed class Channel
        {
            public string SliderPath;
            public string VolumePath;

            public Il2CppSystem.Object Slider;
            public Il2CppSystem.Object Volume;
            public Il2CppSystem.Reflection.FieldInfo WeightField;
        }

        private static readonly Channel Base = new Channel
        {
            SliderPath = GamePaths.BcFxBaseSliderNode,
            VolumePath = GamePaths.BaseVolume,
        };

        private static readonly Channel Effect = new Channel
        {
            SliderPath = GamePaths.BcFxEffectSliderNode,
            VolumePath = GamePaths.EffectVolume,
        };

        public static void Init()
        {
            InitChannel(Base, OnBaseChanged);
            InitChannel(Effect, OnEffectChanged);
        }

        private static void InitChannel(Channel ch, System.Action<float> onChanged)
        {
            ch.Slider = NativeRefs.FindSlider(ch.SliderPath);
            if (ch.Slider == null) return;

            SliderKit.SetRange(ch.Slider, MinValue, MaxValue);
            SliderKit.RefreshVisuals(ch.Slider);
            SliderKit.SetValueQuiet(ch.Slider, InitialValue);

            ch.Volume = NativeRefs.FindVolume(ch.VolumePath);
            if (ch.Volume != null)
                ch.WeightField = Il2CppReflection.FindIl2CppField(
                    NativeRefs.TypeOf("UnityEngine.Rendering.Volume"), "weight");

            UnityEventBridge.AddFloatListener(ch.Slider, onChanged);
        }

        // 两个静态回调而不是闭包 —— 和原实现一致，走的是已验证过的委托转换路径
        private static void OnBaseChanged(float value) => ApplyWeight(Base, value);
        private static void OnEffectChanged(float value) => ApplyWeight(Effect, value);

        private static void ApplyWeight(Channel ch, float value)
        {
            // 这个回调挂在滑条的 onValueChanged 上，而 ch.Volume 是场景初始化时缓存的
            // il2cpp 引用、之后不刷新 —— 它被销毁后再拖滑条，SetFloatField 会打在
            // 悬垂指针上。异常必须在这里吞掉（见 CallbackGuard 的说明）。
            try
            {
                if (ch.Volume == null || ch.WeightField == null) return;
                Il2CppReflection.SetFloatField(ch.Volume, ch.WeightField, value);
            }
            catch (Exception e)
            {
                CallbackGuard.Warn("FxSlider.ApplyWeight", e);
            }
        }
    }
}
