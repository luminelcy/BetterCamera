using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BetterCamera
{
    public static class FocusSlider
    {
        private const string SliderPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Body/Right/P_BetterCameraHandleObject1/Slider/Slider";

        private static Il2CppSystem.Object cachedDOFInstance;
        private static Il2CppSystem.Reflection.FieldInfo cachedFocusDistanceField;
        private static Il2CppSystem.Reflection.FieldInfo cachedValueField;

        private static Il2CppSystem.Object cachedSliderObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMaxValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMinValueField;
        private static Il2CppSystem.Reflection.MethodInfo cachedSetMethod;

        // 手柄同步的基准值。原生对焦（FocusClickedObjectController.SwitchFocus / 自动对焦）
        // 会直接改 DepthOfField 的焦点距离，绕过了本滑条，所以要每帧读回来对齐手柄。
        private static float _lastSyncedFocus = float.NaN;

        public static void Init(MelonLogger.Instance logger)
        {
            CacheDOFFields();
            CacheSliderFields();

            // 设置 max/min
            if (cachedSliderObj != null)
            {
                if (cachedMMaxValueField != null)
                    SetFloatField(cachedSliderObj, cachedMMaxValueField, 1.4f);
                if (cachedMMinValueField != null)
                    SetFloatField(cachedSliderObj, cachedMMinValueField, 0.01f);
            }

            // 设置初始值 0.7
            if (cachedSliderObj != null && cachedSetMethod != null)
            {
                var boxedVal = BoxFloat(0.7f);
                var boxedFalse = BoxBool(false);
                cachedSetMethod.Invoke(cachedSliderObj, new Il2CppSystem.Object[] { boxedVal, boxedFalse });
            }

            // 同步到 DepthOfField
            SetFocusDistance(0.7f);

            RegisterSlider();
        }

        private static void OnSliderChanged(float value)
        {
            SetFocusDistance(value);
        }

        private static void SetFocusDistance(float value)
        {
            ApplyFocusDistance(value);
            _lastSyncedFocus = value;   // 自己写的值记下来，免得下一帧同步又推回去
        }

        private static void ApplyFocusDistance(float value)
        {
            if (cachedDOFInstance == null || cachedFocusDistanceField == null) return;

            var focusDistanceObj = cachedFocusDistanceField.GetValue(cachedDOFInstance);
            if (focusDistanceObj == null) return;

            if (cachedValueField != null)
                SetFloatField(focusDistanceObj, cachedValueField, value);
        }

        /// <summary>读当前实际生效的焦点距离（原生对焦改的就是这个值）。取不到返回 NaN。</summary>
        private static float GetFocusDistance()
        {
            if (cachedDOFInstance == null || cachedFocusDistanceField == null || cachedValueField == null)
                return float.NaN;

            var focusDistanceObj = cachedFocusDistanceField.GetValue(cachedDOFInstance);
            if (focusDistanceObj == null) return float.NaN;

            var raw = cachedValueField.GetValue(focusDistanceObj);
            if (raw == null) return float.NaN;

            return UnboxFloat(raw);
        }

        /// <summary>
        /// 每帧把实际焦点距离同步到滑条手柄。
        ///
        /// 为什么需要：原生的对焦模式按钮走 FocusClickedObjectController.SwitchFocus()
        /// （还有自动对焦 CalculateFocusDistance()），它们直接改 DepthOfField 的
        /// 焦点距离，本滑条完全不知情。不刷新手柄的话，画面已经变焦而手柄还停在原处，
        /// 用户会以为滑条坏了。
        /// </summary>
        public static void SyncFromNative()
        {
            if (cachedSliderObj == null || cachedSetMethod == null) return;

            float cur = GetFocusDistance();
            if (float.IsNaN(cur)) return;

            if (float.IsNaN(_lastSyncedFocus))
            {
                _lastSyncedFocus = cur;
                SetSliderQuiet(cur);
                return;
            }

            if (Mathf.Abs(cur - _lastSyncedFocus) < 0.001f) return;

            _lastSyncedFocus = cur;
            SetSliderQuiet(cur);
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

        private static void CacheDOFFields()
        {
            var dofType = FindType("UnityEngine.Rendering.Universal.DepthOfField");
            if (dofType == null) return;

            var controllerType = FindType("Il2CppProject.HomeScene.RoomScene.RoomSnapScene.SwitchCameraFocusModeButtonObject.FocusClickedObjectController");
            if (controllerType == null) return;

            var getCompDef = GetGenericGetComponent();
            if (getCompDef == null) return;
            var getControllerComp = getCompDef.MakeGenericMethod(controllerType);

            var volumeGo = GameObject.Find("SceneContext/Volume");
            if (volumeGo == null)
                volumeGo = GameObject.Find("SceneContext/Systems/FocusCameraSwitcher");
            if (volumeGo == null) return;

            var controllerComp = getControllerComp.Invoke(volumeGo, null);
            if (controllerComp == null) return;

            var pointerProp = controllerComp.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) return;

            var ptr = (System.IntPtr)pointerProp.GetValue(controllerComp);
            var il2cppControllerObj = new Il2CppSystem.Object(ptr);
            var il2cppControllerType = Il2CppType.From(controllerType);

            var depthOfFieldField = FindIl2CppField(il2cppControllerType, "_depthOfField");
            if (depthOfFieldField == null) return;

            var dofValue = depthOfFieldField.GetValue(il2cppControllerObj);
            if (dofValue == null) return;

            cachedDOFInstance = dofValue;

            var il2cppDofType = Il2CppType.From(dofType);
            cachedFocusDistanceField = FindIl2CppField(il2cppDofType, "focusDistance");

            if (cachedFocusDistanceField != null)
            {
                var minFloatParamType = FindType("UnityEngine.Rendering.MinFloatParameter");
                if (minFloatParamType != null)
                {
                    var il2cppMinFloatType = Il2CppType.From(minFloatParamType);
                    cachedValueField = FindIl2CppField(il2cppMinFloatType, "m_Value");
                }
            }
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
            cachedMMaxValueField = FindIl2CppField(il2cppSliderType, "m_MaxValue");
            cachedMMinValueField = FindIl2CppField(il2cppSliderType, "m_MinValue");
            cachedSetMethod = FindIl2CppMethod(il2cppSliderType, "Set");
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

        private static float UnboxFloat(Il2CppSystem.Object obj)
        {
            var unboxPtr = IL2CPP.il2cpp_object_unbox(obj.Pointer);
            return Marshal.PtrToStructure<float>(unboxPtr);
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
