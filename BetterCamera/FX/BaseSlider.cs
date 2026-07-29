using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BetterCamera.FX
{
    public static class BaseSlider
    {
        private const string SliderPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_FilterMenuObject/Body/ControlsLayout/FiltersListLayout/BaseSlider/P_PostProcessExposureSliderObject/Slider/Slider";

        private const string VolumePath =
            "SceneContext/System/P_RoomSnapPostProcessObject/BaseVolume";

        private static Il2CppSystem.Object cachedSliderObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedMValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMaxValueField;
        private static Il2CppSystem.Reflection.FieldInfo cachedMMinValueField;
        private static Il2CppSystem.Reflection.MethodInfo cachedSetMethod;

        private static Il2CppSystem.Object cachedVolumeObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedWeightField;

        public static void Init(MelonLogger.Instance logger)
        {
            CacheSliderFields();
            CacheVolumeFields();
            ConfigureSlider(1f, 0f, 1f);
            RegisterSlider();
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

        private static void CacheVolumeFields()
        {
            var volumeObj = GameObject.Find(VolumePath);
            if (volumeObj == null) return;

            var volumeType = FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null) return;

            var getCompDef = GetGenericGetComponent();
            if (getCompDef == null) return;

            var volumeComp = getCompDef.MakeGenericMethod(volumeType).Invoke(volumeObj, null);
            if (volumeComp == null) return;

            var pointerProp = volumeComp.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) return;

            var ptr = (System.IntPtr)pointerProp.GetValue(volumeComp);
            cachedVolumeObj = new Il2CppSystem.Object(ptr);

            var il2cppVolumeType = Il2CppType.From(volumeType);
            cachedWeightField = FindIl2CppField(il2cppVolumeType, "weight");
        }

        private static void OnSliderChanged(float value)
        {
            if (cachedVolumeObj == null || cachedWeightField == null) return;
            SetFloatField(cachedVolumeObj, cachedWeightField, value);
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

        private static void ConfigureSlider(float maxValue, float minValue, float value)
        {
            if (cachedSliderObj == null) return;

            if (cachedMMaxValueField != null)
                SetFloatField(cachedSliderObj, cachedMMaxValueField, maxValue);
            if (cachedMMinValueField != null)
                SetFloatField(cachedSliderObj, cachedMMinValueField, minValue);

            if (cachedSetMethod != null)
            {
                var boxedVal = BoxFloat(value);
                var boxedFalse = BoxBool(false);
                cachedSetMethod.Invoke(cachedSliderObj, new Il2CppSystem.Object[] { boxedVal, boxedFalse });
            }
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
