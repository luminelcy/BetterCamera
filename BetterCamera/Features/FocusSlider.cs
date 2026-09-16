using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 对焦距离滑条。范围 0.01 ~ 1.4，初始 0.7。
    ///
    /// 取值链是两级：
    ///     FocusClickedObjectController._depthOfField
    ///       → DepthOfField.focusDistance            (MinFloatParameter)
    ///         → 它的 m_Value / value
    ///
    /// 写入走原生的 value 属性（set_value）而不是直写 m_Value 字段 ——
    /// 游戏自己的 SwitchFocus 走的就是 set_value，两边同一条路才不会互相打架。
    /// set_value 内部是 `m_Value = Mathf.Max(value, min)`，所以 Init 里会把
    /// 这个实例的 min 从游戏原生的 0.1 换成 mod 的 0.01，否则 0.01~0.1 这段无效。
    /// </summary>
    public static class FocusSlider
    {
        private const string ControllerTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.SwitchCameraFocusModeButtonObject.FocusClickedObjectController";
        private const string DofTypeName = "UnityEngine.Rendering.Universal.DepthOfField";
        private const string ParamTypeName = "UnityEngine.Rendering.MinFloatParameter";

        private const float MinValue = 0.01f;
        private const float MaxValue = 1.4f;
        private const float InitialValue = 0.7f;

        /// <summary>mod 允许的焦点距离下限，用来替换游戏原生的 0.1。</summary>
        private const float MinFocusDistance = MinValue;

        private static Il2CppSystem.Object _slider;
        private static Il2CppSystem.Object _focusDistance;          // MinFloatParameter 实例
        private static Il2CppSystem.Reflection.FieldInfo _valueField;
        private static Il2CppSystem.Reflection.MethodInfo _setValue;
        private static float _lastSynced = float.NaN;

        public static void Init()
        {
            if (!CacheFocusTarget()) return;

            _slider = NativeRefs.FindSlider(GamePaths.BcFocusSlider);
            if (_slider == null) return;

            SliderKit.SetRange(_slider, MinValue, MaxValue);
            SliderKit.RefreshVisuals(_slider);
            SliderKit.SetValueQuiet(_slider, InitialValue);

            Apply(InitialValue);

            UnityEventBridge.AddFloatListener(_slider, OnChanged);
        }

        /// <summary>
        /// 定位 DepthOfField.focusDistance，并把它实例上的 min 换掉。
        ///
        /// 为什么要换 min：MinFloatParameter.set_value 实现是一行
        ///     m_Value = Mathf.Max(value, min);
        /// 游戏给 focusDistance 的 min 是 0.1，不改的话 mod 想写 0.01 会被钳住。
        /// min 是实例字段，只影响这一个 focusDistance —— 同一个组件上的
        /// gaussianStart / gaussianEnd 以及游戏里其他所有 MinFloatParameter 都不受影响。
        ///
        /// 只在进场景时设一次；玩家碰不到这个字段，Volume 系统也不会重新序列化它。
        /// </summary>
        private static bool CacheFocusTarget()
        {
            var controllerType = Il2CppReflection.FindType(ControllerTypeName);
            var dofType = Il2CppReflection.FindType(DofTypeName);
            var paramType = Il2CppReflection.FindType(ParamTypeName);
            if (controllerType == null || dofType == null || paramType == null) return false;

            var controller = FindFocusController(controllerType);
            if (controller == null) return false;

            var dofField = Il2CppReflection.FindIl2CppField(
                Il2CppInterop.Runtime.Il2CppType.From(controllerType), "_depthOfField");
            if (dofField == null) return false;

            var dof = dofField.GetValue(controller);
            if (dof == null) return false;

            var focusDistanceField = Il2CppReflection.FindIl2CppField(
                Il2CppInterop.Runtime.Il2CppType.From(dofType), "focusDistance");
            if (focusDistanceField == null) return false;

            _focusDistance = focusDistanceField.GetValue(dof);
            if (_focusDistance == null) return false;

            var il2cppParamType = Il2CppInterop.Runtime.Il2CppType.From(paramType);
            _setValue = Il2CppReflection.FindIl2CppMethod(il2cppParamType, "set_value");
            _valueField = Il2CppReflection.FindIl2CppField(il2cppParamType, "m_Value");

            var minField = Il2CppReflection.FindIl2CppField(il2cppParamType, "min");
            if (minField != null)
                Il2CppReflection.SetFloatField(_focusDistance, minField, MinFocusDistance);

            return _setValue != null;
        }

        /// <summary>两个候选路径依次尝试（游戏换过一次挂载位置）。</summary>
        private static Il2CppSystem.Object FindFocusController(System.Type controllerType)
        {
            return NativeRefs.FindComponent(GamePaths.FocusController, ControllerTypeName)
                ?? NativeRefs.FindComponent(GamePaths.FocusControllerFallback, ControllerTypeName);
        }

        /// <summary>
        /// 每帧把实际焦点距离同步到滑条手柄。
        /// 原生的对焦模式按钮和自动对焦会直接改这个值，mod 不知情，不刷新手柄
        /// 就会出现"画面已经变焦、手柄还停在原处"。
        /// </summary>
        public static void SyncFromNative()
        {
            if (_slider == null) return;

            float cur = Current();
            if (float.IsNaN(cur)) return;

            if (float.IsNaN(_lastSynced))
            {
                _lastSynced = cur;
                SliderKit.SetValueQuiet(_slider, cur);
                return;
            }

            if (Mathf.Abs(cur - _lastSynced) < 0.001f) return;

            _lastSynced = cur;
            SliderKit.SetValueQuiet(_slider, cur);
        }

        private static void OnChanged(float value) => Apply(value);

        private static void Apply(float value)
        {
            if (_focusDistance == null || _setValue == null) return;
            try
            {
                _setValue.Invoke(_focusDistance, new Il2CppSystem.Object[]
                {
                    Il2CppReflection.BoxFloat(value),
                });
                _lastSynced = value;   // 自己写的值记下来，免得下一帧同步又推回去
            }
            catch { }
        }

        /// <summary>
        /// 读当前焦点距离。读 m_Value 即可 —— MinFloatParameter.get_value 的实现
        /// 就是 `return m_Value`，两者是同一块存储，读取不做钳制。
        /// </summary>
        private static float Current()
        {
            return Il2CppReflection.GetFloatField(_focusDistance, _valueField);
        }
    }
}
