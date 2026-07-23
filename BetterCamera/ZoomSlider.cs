using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections;

namespace BetterCamera
{
    public static class ZoomSlider
    {
        private const string SliderPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Body/Right/P_BetterCameraHandleObject0/Slider/Slider";

        private static Il2CppSystem.Object cachedCameraObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedMLensField;
        private static Il2CppSystem.Reflection.FieldInfo cachedFOVField;

        private static Il2CppSystem.Object cachedSliderObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedMValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMaxValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMinValueField;
        private static Il2CppSystem.Reflection.MethodInfo cachedSetMethod;

        public static void Init(MelonLogger.Instance logger)
        {
            CacheCameraFields();
            CacheSliderFields();

            // 设置 max/min
            if (cachedSliderObj != null)
            {
                if (cachedMMaxValueField != null)
                    SetFloatField(cachedSliderObj, cachedMMaxValueField, 120f);
                if (cachedMMinValueField != null)
                    SetFloatField(cachedSliderObj, cachedMMinValueField, 20f);
            }

            RegisterSlider();
        }

        public static IEnumerator DelayedSetSliderValue()
        {
            // 等待 0.5 秒，确保 UI 布局完成
            yield return new WaitForSeconds(0.7f);

            // 读取当前 FOV
            float currentFOV = 60f;
            if (cachedCameraObj != null && cachedMLensField != null && cachedFOVField != null)
            {
                var lensValue = cachedMLensField.GetValue(cachedCameraObj);
                if (lensValue != null)
                {
                    var fovIl2cpp = cachedFOVField.GetValue(lensValue);
                    if (fovIl2cpp != null)
                        currentFOV = UnboxFloat(fovIl2cpp);
                }
            }

            // 设置 Slider 值和把手位置
            if (cachedSliderObj == null || cachedSetMethod == null) yield break;

            var boxedVal = BoxFloat(currentFOV);
            var boxedFalse = BoxBool(false);
            cachedSetMethod.Invoke(cachedSliderObj, new Il2CppSystem.Object[] { boxedVal, boxedFalse });
            RegisterSlider();
        }

        public static void ResetSliderValue()
        {
            if (cachedCameraObj == null || cachedMLensField == null || cachedFOVField == null) return;

            var lensValue = cachedMLensField.GetValue(cachedCameraObj);
            if (lensValue == null) return;

            var fovIl2cpp = cachedFOVField.GetValue(lensValue);
            if (fovIl2cpp == null) return;

            ConfigureSlider(120f, 20f, UnboxFloat(fovIl2cpp));
        }

        private static void OnSliderChanged(float value)
        {
            if (cachedCameraObj == null || cachedMLensField == null || cachedFOVField == null) return;

            var lensValue = cachedMLensField.GetValue(cachedCameraObj);
            if (lensValue == null) return;

            SetFOV(lensValue, value);
        }

        private static void CacheCameraFields()
        {
            var cameraObj = GameObject.Find("SceneContext/P_RoomCameraObject/VirtualCameras/DefaultVirtualCamera");
            if (cameraObj == null) return;

            var cameraType = FindType("Il2CppCinemachine.CinemachineVirtualCamera");
            if (cameraType == null) return;

            var getCompDef = GetGenericGetComponent();
            if (getCompDef == null) return;

            var cameraComp = getCompDef.MakeGenericMethod(cameraType).Invoke(cameraObj, null);
            if (cameraComp == null) return;

            var pointerProp = cameraComp.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) return;

            var ptr = (System.IntPtr)pointerProp.GetValue(cameraComp);
            cachedCameraObj = new Il2CppSystem.Object(ptr);

            var il2cppCameraType = Il2CppType.From(cameraType);
            cachedMLensField = FindIl2CppField(il2cppCameraType, "m_Lens");

            var lensSettingsType = FindType("Il2CppCinemachine.LensSettings");
            if (lensSettingsType == null) return;
            cachedFOVField = FindIl2CppField(Il2CppType.From(lensSettingsType), "FieldOfView");
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

        private static void SetFOV(Il2CppSystem.Object lensValue, float value)
        {
            SetFloatField(lensValue, cachedFOVField, value);
            cachedMLensField.SetValue(cachedCameraObj, lensValue);
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
