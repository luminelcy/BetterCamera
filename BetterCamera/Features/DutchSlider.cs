using System;
using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// Dutch（相机侧倾）滑条，范围 -90 ~ 90，初始 0。
    ///
    /// 没有原生通道 —— 游戏自己永远不会改 Dutch，所以直写 m_Lens 是安全的，
    /// 不存在 FOV 那种"两边抢着写"的问题。
    ///
    /// 但仍然要做手柄同步：DutchReset 按钮会在外部改这个值，手柄得跟上。
    /// </summary>
    public static class DutchSlider
    {
        private const string LensFieldDutch = "Dutch";
        private const float MaxValue = 90f;
        private const float MinValue = -90f;
        private const float InitialValue = 0f;

        private static Il2CppSystem.Object _slider;
        private static Il2CppSystem.Object _camera;
        private static float _lastSynced = float.NaN;

        public static void Init()
        {
            _camera = NativeRefs.FindCamera(GamePaths.DefaultVirtualCamera);

            _slider = NativeRefs.FindSlider(GamePaths.BcDutchSlider);
            if (_slider == null) return;

            SliderKit.SetRange(_slider, MinValue, MaxValue);
            SliderKit.RefreshVisuals(_slider);
            SliderKit.SetValueQuiet(_slider, InitialValue);

            UnityEventBridge.AddFloatListener(_slider, OnChanged);

            // 对焦滑条的方向要反过来：两条滑条都是从同一份原生预制体克隆的，
            // 而语义方向相反，得单独摆正一个。
            var focus = NativeRefs.FindSlider(GamePaths.BcFocusSlider);
            if (focus != null)
                SliderKit.SetDirection(focus, 1);   // Slider.Direction.RightToLeft
        }

        /// <summary>每帧把 m_Lens.Dutch 同步到滑条手柄，反映任何外部改动。</summary>
        public static void SyncFromNative()
        {
            if (_slider == null || _camera == null) return;

            float dutch = LensKit.ReadFloat(_camera, LensFieldDutch);
            if (float.IsNaN(dutch)) return;

            if (float.IsNaN(_lastSynced))
            {
                _lastSynced = dutch;
                SliderKit.SetValueQuiet(_slider, dutch);
                return;
            }

            if (Mathf.Abs(dutch - _lastSynced) < 0.01f) return;

            _lastSynced = dutch;
            SliderKit.SetValueQuiet(_slider, dutch);
        }

        private static void OnChanged(float value)
        {
            // 挂在滑条的 onValueChanged 上；_camera 是 Init 时缓存、之后不刷新的
            // il2cpp 引用，失效后 EditFloat 里的反射读会抛。必须在这里吞掉，
            // 否则会打断 Unity 的输入处理（见 CallbackGuard 的说明）。
            try
            {
                if (LensKit.EditFloat(_camera, LensFieldDutch, value))
                    _lastSynced = value;
            }
            catch (Exception e)
            {
                CallbackGuard.Warn("DutchSlider.OnChanged", e);
            }
        }
    }
}
