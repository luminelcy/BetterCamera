using System;
using Il2CppInterop.Runtime;

namespace BetterCamera.Il2Cpp
{
    /// <summary>
    /// 往原生 UnityEvent 上挂托管回调。
    ///
    /// 之前这段代码在 4 个滑条文件里逐字重复（每份 36 行）、在 2 个按钮文件里
    /// 也重复（每份 49 行，彼此只差回调方法名一行）。这里合成两个入口。
    ///
    /// 滑条与按钮的差异（不是抄错，是必须区分）：
    ///
    ///             滑条                        按钮
    ///   事件字段   Slider.m_OnValueChanged      Button.m_OnClick
    ///   事件类型   UnityEvent&lt;float&gt;             UnityEvent
    ///   委托类型   UnityAction&lt;float&gt;            UnityAction
    ///   回调签名   Action&lt;float&gt;                Action
    ///
    /// 这里的 Il2CppSystem.Object 是裸指针包装（NativeRefs 里 new 出来的），
    /// 不带类型信息，所以字段查找要指定声明类型。
    /// </summary>
    public static class UnityEventBridge
    {
        /// <summary>给滑条挂 onValueChanged 监听。所有本 mod 的滑条都派生自 UnityEngine.UI.Slider。</summary>
        public static bool AddFloatListener(Il2CppSystem.Object sliderObj, Action<float> callback)
        {
            if (sliderObj == null || callback == null) return false;

            var field = Il2CppReflection.FindIl2CppField(SliderType, "m_OnValueChanged");
            if (field == null) return false;

            var eventObj = field.GetValue(sliderObj);
            if (eventObj == null) return false;

            return Wire(eventObj, UnityEventFloatType, UnityActionFloatType, callback);
        }

        /// <summary>给按钮挂 onClick 监听。</summary>
        public static bool AddClickListener(Il2CppSystem.Object buttonObj, Action callback)
        {
            if (buttonObj == null || callback == null) return false;

            var field = Il2CppReflection.FindIl2CppField(ButtonType, "m_OnClick");
            if (field == null) return false;

            var eventObj = field.GetValue(buttonObj);
            if (eventObj == null) return false;

            return Wire(eventObj, UnityEventType, UnityActionType, callback);
        }

        /// <summary>两版共用的尾巴：找 AddListener → 把托管委托转成 il2cpp 委托 → 调用。</summary>
        private static bool Wire(Il2CppSystem.Object eventObj, Type eventType, Type actionType, object callback)
        {
            if (eventType == null || actionType == null) return false;

            var addListener = Il2CppReflection.FindIl2CppMethod(Il2CppType.From(eventType), "AddListener");
            if (addListener == null) return false;

            var convertDelegate = DelegateSupportType?.GetMethod("ConvertDelegate");
            if (convertDelegate == null) return false;

            var il2cppDelegate = convertDelegate
                .MakeGenericMethod(actionType)
                .Invoke(null, new object[] { callback });

            addListener.Invoke(eventObj, new Il2CppSystem.Object[] { (Il2CppSystem.Object)il2cppDelegate });
            return true;
        }

        // ---- 类型只解析一次（FindType 会遍历全部程序集；MakeGenericType 也有开销） ----
        //
        // 注意两类类型不能混：
        //   Il2CppSystem.Type  —— 给 il2cpp 反射 API（FindIl2CppField 等）
        //   System.Type       —— 给 .NET 的 MakeGenericType / MakeGenericMethod

        private static Il2CppSystem.Type _sliderType;
        private static Il2CppSystem.Type SliderType =>
            _sliderType ??= Il2CppSecure("UnityEngine.UI.Slider");

        private static Il2CppSystem.Type _buttonType;
        private static Il2CppSystem.Type ButtonType =>
            _buttonType ??= Il2CppSecure("UnityEngine.UI.Button");

        private static Type _unityEventFloat;
        private static Type UnityEventFloatType =>
            _unityEventFloat ??= Il2CppReflection.FindType("UnityEngine.Events.UnityEvent`1")?.MakeGenericType(typeof(float));

        private static Type _unityEvent;
        private static Type UnityEventType => _unityEvent ??= Il2CppReflection.FindType("UnityEngine.Events.UnityEvent");

        private static Type _unityActionFloat;
        private static Type UnityActionFloatType =>
            _unityActionFloat ??= Il2CppReflection.FindType("UnityEngine.Events.UnityAction`1")?.MakeGenericType(typeof(float));

        private static Type _unityAction;
        private static Type UnityActionType => _unityAction ??= Il2CppReflection.FindType("UnityEngine.Events.UnityAction");

        private static Type _delegateSupport;
        private static Type DelegateSupportType =>
            _delegateSupport ??= Il2CppReflection.FindType("Il2CppInterop.Runtime.DelegateSupport");

        /// <summary>按托管类型名拿到 il2cpp 侧的类型，失败返回 null（不抛）。</summary>
        private static Il2CppSystem.Type Il2CppSecure(string managedTypeName)
        {
            var t = Il2CppReflection.FindType(managedTypeName);
            return t == null ? null : Il2CppType.From(t);
        }
    }
}
