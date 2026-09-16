using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections;
using Il2CppProject;
using Il2CppProject.HomeScene.RoomScene;
using Il2CppTanitakaTech.StateVariable;

namespace BetterCamera
{
    public static class ZoomSlider
    {
        private const string SliderPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Body/Right/P_BetterCameraHandleObject0/Slider/Slider";

        // 手柄同步用的基准值。原生 FOV 的读写统一走 NativeFovChannel。
        private static float _lastSyncedFov = float.NaN;

        private static Il2CppSystem.Object cachedSliderObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedMValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMaxValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMinValueField;
        private static Il2CppSystem.Reflection.MethodInfo cachedSetMethod;
        private static Il2CppSystem.Reflection.MethodInfo cachedUpdateVisualsMethod;

        public static void Init(MelonLogger.Instance logger)
        {
            NativeFovChannel.Ensure();
            CacheSliderFields();

            // 滑条 UI 的上下限保持 20-120（这是本 mod 相对原生 40-80 的扩宽）
            if (cachedSliderObj != null)
            {
                if (cachedMMaxValueField != null)
                    SetFloatField(cachedSliderObj, cachedMMaxValueField, 120f);
                if (cachedMMinValueField != null)
                    SetFloatField(cachedSliderObj, cachedMMinValueField, 20f);

                // 改完范围必须刷新手柄，否则它还停在旧范围算出的位置上
                RefreshSliderVisuals();
            }

            RegisterSlider();
        }

        /// <summary>
        /// 每帧把原生 FOV 同步到滑条手柄。滚轮和键盘走的是游戏的 ZoomCamera →
        /// 写同一个 RoomCameraFOV 变量 → UpdateFOV 改相机 m_Lens，所以这里读到
        /// m_Lens 的变化就说明外部改了 FOV，把滑条跟着挪过去。
        /// </summary>
        public static void SyncFromNative()
        {
            if (cachedSliderObj == null || cachedSetMethod == null) return;
            if (!NativeFovChannel.TryGetCurrent(out float fov)) return;

            if (float.IsNaN(_lastSyncedFov))
            {
                // 首次拿到值：对齐一次，之后只在外部改动时才动滑条
                _lastSyncedFov = fov;
                SetSliderQuiet(fov);
                return;
            }

            if (Mathf.Abs(fov - _lastSyncedFov) < 0.01f) return;

            _lastSyncedFov = fov;
            SetSliderQuiet(fov);
        }

        /// <summary>写滑条值但不触发回调（sendCallback=false），避免和设备回环。</summary>
        private static void SetSliderQuiet(float value)
        {
            if (cachedSliderObj == null || cachedSetMethod == null) return;
            try
            {
                var boxedVal = BoxFloat(value);
                var boxedFalse = BoxBool(false);
                cachedSetMethod.Invoke(cachedSliderObj, new Il2CppSystem.Object[] { boxedVal, boxedFalse });
            }
            catch { }
        }

        private static void OnSliderChanged(float value)
        {
            // 不再直接写 m_Lens —— 写游戏的响应式变量，
            // 由它自己的 UpdateFOV 去改相机，这样滚轮/键盘/滑条三者同步
            if (NativeFovChannel.Set(value))
                _lastSyncedFov = value;
        }

        private static void CacheSliderFields()
        {
            var sliderObj = GameObject.Find(SliderPath);
            if (sliderObj == null) return;

            var sliderType = FindType("Il2CppProject.NoArrowMovableSlider");
            if (sliderType == null) return;

            var getCompDef = GetGenericGetComponent();
            if (getCompDef == null) return;

            var sliderComp = getCompDef.MakeGenericMethod(sliderType).Invoke(sliderObj, null);
            if (sliderComp == null) return;

            var pointerProp = sliderComp.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) return;

            var ptr = (System.IntPtr)pointerProp.GetValue(sliderComp);
            cachedSliderObj = new Il2CppSystem.Object(ptr);

            var baseSliderType = FindType("UnityEngine.UI.Slider");
            if (baseSliderType == null) return;

            var il2cppSliderType = Il2CppType.From(baseSliderType);
            cachedMValueField = FindIl2CppField(il2cppSliderType, "m_Value");
            cachedMMaxValueField = FindIl2CppField(il2cppSliderType, "m_MaxValue");
            cachedMMinValueField = FindIl2CppField(il2cppSliderType, "m_MinValue");
            cachedSetMethod = FindIl2CppMethod(il2cppSliderType, "Set");
            cachedUpdateVisualsMethod = FindIl2CppMethod(il2cppSliderType, "UpdateVisuals");
        }

        /// <summary>
        /// 强制按当前 min/max 重算手柄位置。
        ///
        /// 光写 m_MinValue / m_MaxValue 字段是不会刷新视觉的 —— 手柄位置只在
        /// Slider.UpdateVisuals() 里算。而 Slider.Set() 开头是
        ///     if (m_Value == newValue) return;
        /// 所以「改完范围再 Set 同一个值」也会被这行挡掉，视觉仍然是按旧范围算的。
        ///
        /// 具体症状：克隆原生滑条时它的范围是 [40,80] 且值顶在 80（手柄 100%），
        /// 我们把范围改成 [20,120] 后不去刷新，手柄就停在 100%，
        /// 而按新范围 80 应该在 60% 处。
        /// </summary>
        private static void RefreshSliderVisuals()
        {
            if (cachedSliderObj == null || cachedUpdateVisualsMethod == null) return;
            try { cachedUpdateVisualsMethod.Invoke(cachedSliderObj, null); }
            catch { }
        }

        private static void ConfigureSlider(float maxValue, float minValue, float value)
        {
            if (cachedSliderObj == null) return;

            // 先设置值（与 DutchSlider.ResetSliderValue 一致）
            if (cachedSetMethod != null)
            {
                var boxedVal = BoxFloat(value);
                var boxedFalse = BoxBool(false);

                cachedSetMethod.Invoke(cachedSliderObj, new Il2CppSystem.Object[] { boxedVal, boxedFalse });
            }

            // 再设置 max/min
            if (cachedMMaxValueField != null)
                SetFloatField(cachedSliderObj, cachedMMaxValueField, maxValue);
            if (cachedMMinValueField != null)
                SetFloatField(cachedSliderObj, cachedMMinValueField, minValue);
        }

        private static void RegisterSlider()
        {
            if (cachedSliderObj == null) return;

            var baseSliderType = FindType("UnityEngine.UI.Slider");
            if (baseSliderType == null) return;

            var il2cppSliderType = Il2CppType.From(baseSliderType);
            var onValueChangedField = FindIl2CppField(il2cppSliderType, "m_OnValueChanged");
            if (onValueChangedField == null) return;

            var eventValue = onValueChangedField.GetValue(cachedSliderObj);
            if (eventValue == null) return;

            var sliderEventType = FindType("UnityEngine.Events.UnityEvent`1");
            if (sliderEventType == null) return;
            var sliderEventFloat = sliderEventType.MakeGenericType(typeof(float));
            var addListenerIl2Cpp = FindIl2CppMethod(Il2CppType.From(sliderEventFloat), "AddListener");
            if (addListenerIl2Cpp == null) return;

            System.Action<float> callback = OnSliderChanged;

            var unityActionGeneric = FindType("UnityEngine.Events.UnityAction`1");
            if (unityActionGeneric == null) return;
            var unityActionFloat = unityActionGeneric.MakeGenericType(typeof(float));

            var dsType = FindType("Il2CppInterop.Runtime.DelegateSupport");
            if (dsType == null) return;
            var convertDelegate = dsType.GetMethod("ConvertDelegate");
            if (convertDelegate == null) return;

            var generic = convertDelegate.MakeGenericMethod(unityActionFloat);
            var delegateInstance = generic.Invoke(null, new object[] { callback });

            addListenerIl2Cpp.Invoke(eventValue, new Il2CppSystem.Object[] { (Il2CppSystem.Object)delegateInstance });
        }

        private static void SetFloatField(Il2CppSystem.Object target, Il2CppSystem.Reflection.FieldInfo field, float value)
        {
            field.SetValue(target, BoxFloat(value));
        }

        private static float UnboxFloat(Il2CppSystem.Object obj)
        {
            var unboxPtr = IL2CPP.il2cpp_object_unbox(obj.Pointer);
            return Marshal.PtrToStructure<float>(unboxPtr);
        }

        private static Il2CppSystem.Object BoxFloat(float value)
        {
            var corlib = IL2CPP.il2cpp_get_corlib();
            var singleClass = IL2CPP.il2cpp_class_from_name(corlib, "System", "Single");
            var bytes = System.BitConverter.GetBytes(value);
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var boxedPtr = IL2CPP.il2cpp_value_box(singleClass, ptr);
            Marshal.FreeHGlobal(ptr);
            return new Il2CppSystem.Object(boxedPtr);
        }

        private static Il2CppSystem.Object BoxBool(bool value)
        {
            var corlib = IL2CPP.il2cpp_get_corlib();
            var boolClass = IL2CPP.il2cpp_class_from_name(corlib, "System", "Boolean");
            var bytes = new byte[] { (byte)(value ? 1 : 0) };
            var ptr = Marshal.AllocHGlobal(1);
            Marshal.Copy(bytes, 0, ptr, 1);
            var boxedPtr = IL2CPP.il2cpp_value_box(boolClass, ptr);
            Marshal.FreeHGlobal(ptr);
            return new Il2CppSystem.Object(boxedPtr);
        }

        private static Il2CppSystem.Reflection.FieldInfo FindIl2CppField(Il2CppSystem.Type type, string fieldName)
        {
            var current = type;
            while (current != null)
            {
                var fields = current.GetFields(
                    Il2CppSystem.Reflection.BindingFlags.Instance |
                    Il2CppSystem.Reflection.BindingFlags.Public |
                    Il2CppSystem.Reflection.BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].Name == fieldName)
                        return fields[i];
                }
                current = current.BaseType;
            }
            return null;
        }

        private static Il2CppSystem.Reflection.MethodInfo FindIl2CppMethod(Il2CppSystem.Type type, string methodName)
        {
            var current = type;
            while (current != null)
            {
                var methods = current.GetMethods(
                    Il2CppSystem.Reflection.BindingFlags.Instance |
                    Il2CppSystem.Reflection.BindingFlags.Public |
                    Il2CppSystem.Reflection.BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == methodName)
                        return methods[i];
                }
                current = current.BaseType;
            }
            return null;
        }

        private static System.Type FindType(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private static MethodInfo GetGenericGetComponent()
        {
            foreach (var m in typeof(GameObject).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    return m;
            }
            return null;
        }
    }
}
