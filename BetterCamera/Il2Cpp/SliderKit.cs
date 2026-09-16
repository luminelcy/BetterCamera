using Il2CppInterop.Runtime;

namespace BetterCamera.Il2Cpp
{
    /// <summary>
    /// 对原生 UnityEngine.UI.Slider 控件的常用操作。
    ///
    /// 4 个滑条文件之前各自缓存一遍 m_MinValue / m_MaxValue / m_Value / Set /
    /// UpdateVisuals 这同一批成员（都在 UnityEngine.UI.Slider 上，与具体滑条无关），
    /// 这里合并成一份并缓存。
    /// </summary>
    public static class SliderKit
    {
        private static Il2CppSystem.Reflection.FieldInfo _fMax, _fMin, _fValue;
        private static Il2CppSystem.Reflection.MethodInfo _mSet, _mUpdateVisuals;
        private static Il2CppSystem.Reflection.FieldInfo _fDirection;
        private static bool _ready;

        private static void Ensure()
        {
            if (_ready) return;
            _ready = true;

            var managed = Il2CppReflection.FindType("UnityEngine.UI.Slider");
            if (managed == null) return;
            var type = Il2CppType.From(managed);

            _fMax = Il2CppReflection.FindIl2CppField(type, "m_MaxValue");
            _fMin = Il2CppReflection.FindIl2CppField(type, "m_MinValue");
            _fValue = Il2CppReflection.FindIl2CppField(type, "m_Value");
            _fDirection = Il2CppReflection.FindIl2CppField(type, "m_Direction");
            _mSet = Il2CppReflection.FindIl2CppMethod(type, "Set");
            _mUpdateVisuals = Il2CppReflection.FindIl2CppMethod(type, "UpdateVisuals");
        }

        /// <summary>设上下限。</summary>
        public static void SetRange(Il2CppSystem.Object slider, float min, float max)
        {
            if (slider == null) return;
            Ensure();
            if (_fMin != null) Il2CppReflection.SetFloatField(slider, _fMin, min);
            if (_fMax != null) Il2CppReflection.SetFloatField(slider, _fMax, max);
        }

        /// <summary>
        /// 按当前 min/max 重算手柄位置。
        ///
        /// 改完 SetRange 之后**必须**调这个。原因：手柄位置只在 Slider.UpdateVisuals()
        /// 里算，而 Slider.Set() 开头是
        ///     if (m_Value == newValue) return;
        /// 所以「改完范围再 Set 同一个值」会被这行挡掉，视觉仍按旧范围显示。
        /// </summary>
        public static void RefreshVisuals(Il2CppSystem.Object slider)
        {
            if (slider == null) return;
            Ensure();
            if (_mUpdateVisuals == null) return;
            try { _mUpdateVisuals.Invoke(slider, null); } catch { }
        }

        /// <summary>写滑条值但不触发回调（sendCallback=false），避免和设备回环。</summary>
        public static bool SetValueQuiet(Il2CppSystem.Object slider, float value)
        {
            if (slider == null) return false;
            Ensure();
            if (_mSet == null) return false;
            try
            {
                _mSet.Invoke(slider, new Il2CppSystem.Object[]
                {
                    Il2CppReflection.BoxFloat(value),
                    Il2CppReflection.BoxBool(false),
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>设滑条方向（Slider.Direction 枚举，1 = RightToLeft）。</summary>
        public static bool SetDirection(Il2CppSystem.Object slider, int direction)
        {
            if (slider == null) return false;
            Ensure();
            if (_fDirection == null) return false;
            Il2CppReflection.SetIntField(slider, _fDirection, direction);
            return true;
        }
    }
}
