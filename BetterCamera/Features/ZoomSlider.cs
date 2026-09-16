using System;
using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 缩放滑条：本 mod 对 FOV 的扩宽入口（20–120，原生只有 40–80）。
    ///
    /// 双向：
    ///   拖滑条        → 写游戏的 RoomCameraFOV 响应式变量，由游戏自己的 UpdateFOV 落到相机
    ///   滚轮/键盘/退出 → SyncFromNative 把手柄跟着挪
    ///
    /// 扩宽范围本身靠 FovClampPatch 把原生钳制边界换掉；
    /// 没有那个补丁的话，写进去的值会被夹回 40/80。
    /// </summary>
    public static class ZoomSlider
    {
        private static Il2CppSystem.Object _slider;
        private static float _lastSynced = float.NaN;

        public static void Init()
        {
            NativeFovChannel.Ensure();

            _slider = NativeRefs.FindSlider(GamePaths.BcZoomSlider);
            if (_slider == null) return;

            SliderKit.SetRange(_slider, min: 20f, max: 120f);

            // 必须刷新：Slider.Set 开头的 `if (m_Value == newValue) return;` 会吞掉同值写入，
            // 光改范围手柄不会重算，会停在旧范围算出的位置上
            SliderKit.RefreshVisuals(_slider);

            UnityEventBridge.AddFloatListener(_slider, OnChanged);
        }

        /// <summary>
        /// 每帧把原生 FOV 同步到滑条手柄。滚轮、键盘、退出重置改的都是同一个值，
        /// 所以这里读到变化就说明是外部改的。
        /// </summary>
        public static void SyncFromNative()
        {
            if (_slider == null) return;
            if (!NativeFovChannel.TryGetCurrent(out float fov)) return;

            // 首帧先对齐一次，之后只响应变化
            if (float.IsNaN(_lastSynced))
            {
                _lastSynced = fov;
                SliderKit.SetValueQuiet(_slider, fov);
                return;
            }

            if (Mathf.Abs(fov - _lastSynced) < 0.01f) return;

            _lastSynced = fov;
            SliderKit.SetValueQuiet(_slider, fov);
        }

        private static void OnChanged(float value)
        {
            // 不直接写相机 m_Lens —— 写游戏的响应式变量，由它的 UpdateFOV 去改相机。
            // 这样滚轮/键盘/滑条作用于同一个值，不会互相打架。
            if (NativeFovChannel.Set(value))
                _lastSynced = value;   // 自己写的值记下来，免得下一帧同步又推回去
        }
    }
}
