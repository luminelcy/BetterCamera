using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System.Linq;
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

        private static MelonLogger.Instance _logger;

        public static void Init(MelonLogger.Instance logger)
        {
            _logger = logger;
            CacheDOFFields();
            CacheSliderFields();

            logger.Msg($"FocusSlider: cachedDOFInstance={cachedDOFInstance != null}, cachedFocusDistanceField={cachedFocusDistanceField != null}, cachedValueField={cachedValueField != null}");
            logger.Msg($"FocusSlider: cachedSliderObj={cachedSliderObj != null}, cachedSetMethod={cachedSetMethod != null}");

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
            if (cachedDOFInstance == null || cachedFocusDistanceField == null) return;

            var focusDistanceObj = cachedFocusDistanceField.GetValue(cachedDOFInstance);
            if (focusDistanceObj == null) return;

            if (cachedValueField != null)
            {
                SetFloatField(focusDistanceObj, cachedValueField, value);
            }
        }

        private static void CacheDOFFields()
        {
            var dofType = FindType("UnityEngine.Rendering.Universal.DepthOfField");
            if (dofType == null) { _logger?.Msg("FocusSlider: DepthOfField type not found"); return; }
            _logger?.Msg($"FocusSlider: Found dofType: {dofType.FullName}");

            // DepthOfField(Clone) 在 FocusClickedObjectController._depthOfField 中
            var controllerType = FindType("Il2CppProject.HomeScene.RoomScene.RoomSnapScene.SwitchCameraFocusModeButtonObject.FocusClickedObjectController");
            if (controllerType == null) { _logger?.Msg("FocusSlider: FocusClickedObjectController type not found"); return; }
            _logger?.Msg($"FocusSlider: Found controllerType: {controllerType.FullName}");

            var getCompDef = GetGenericGetComponent();
            if (getCompDef == null) return;
            var getControllerComp = getCompDef.MakeGenericMethod(controllerType);

            // 在 SceneContext 下查找带 Volume 的 GameObject
            var volumeGo = GameObject.Find("SceneContext/Volume");
            if (volumeGo == null) { _logger?.Msg("FocusSlider: SceneContext/Volume not found"); return; }
            _logger?.Msg($"FocusSlider: Found Volume GameObject: {volumeGo.name}");

            var controllerComp = getControllerComp.Invoke(volumeGo, null);
            if (controllerComp == null) { _logger?.Msg("FocusSlider: FocusClickedObjectController not found on Volume GO"); return; }
            _logger?.Msg("FocusSlider: Found FocusClickedObjectController component");

            var pointerProp = controllerComp.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) { _logger?.Msg("FocusSlider: Pointer property not found"); return; }

            var ptr = (System.IntPtr)pointerProp.GetValue(controllerComp);
            var il2cppControllerObj = new Il2CppSystem.Object(ptr);
            var il2cppControllerType = Il2CppType.From(controllerType);

            // 获取 _depthOfField 字段
            var depthOfFieldField = FindIl2CppField(il2cppControllerType, "_depthOfField");
            if (depthOfFieldField == null) { _logger?.Msg("FocusSlider: _depthOfField field not found"); return; }

            var dofValue = depthOfFieldField.GetValue(il2cppControllerObj);
            if (dofValue == null) { _logger?.Msg("FocusSlider: _depthOfField is null"); return; }

            cachedDOFInstance = dofValue;
            _logger?.Msg("FocusSlider: Got DepthOfField from _depthOfField!");

            // 缓存 focusDistance 和 value 字段
            var il2cppDofType = Il2CppType.From(dofType);
            cachedFocusDistanceField = FindIl2CppField(il2cppDofType, "focusDistance");
            _logger?.Msg($"FocusSlider: cachedFocusDistanceField={cachedFocusDistanceField != null}");

            if (cachedFocusDistanceField != null)
            {
                var minFloatParamType = FindType("UnityEngine.Rendering.MinFloatParameter");
                if (minFloatParamType != null)
                {
                    var il2cppMinFloatType = Il2CppType.From(minFloatParamType);
                    cachedValueField = FindIl2CppField(il2cppMinFloatType, "m_Value");
                    _logger?.Msg($"FocusSlider: cachedValueField={cachedValueField != null}");
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
