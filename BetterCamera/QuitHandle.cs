using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BetterCamera
{
    public static class QuitHandle
    {
        private const string ButtonPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/RoomSnapSceneBackButtonObject/CommonBackButtonObject/CommonBackButton";

        private static Il2CppSystem.Object cachedCameraObj;
        private static Il2CppSystem.Reflection.FieldInfo cachedMLensField;

        private static Il2CppSystem.Reflection.FieldInfo cachedDutchField;
        private static Il2CppSystem.Reflection.FieldInfo cachedFieldOfViewField;
        private static Il2CppSystem.Reflection.FieldInfo cachedNearClipPlaneField;

        public static void Init(MelonLogger.Instance logger)
        {
            CacheCameraFields();
            RegisterButton();
        }

        public static void OnQuit()
        {
            float currentFov = float.NaN;

            if (cachedCameraObj != null && cachedMLensField != null)
            {
                var lensValue = cachedMLensField.GetValue(cachedCameraObj);
                if (lensValue != null)
                {
                    // Dutch = 0
                    if (cachedDutchField != null)
                        cachedDutchField.SetValue(lensValue, BoxFloat(0f));

                    // 只读出 FOV 备后面用，这里不写它 —— 见下方说明
                    if (cachedFieldOfViewField != null)
                    {
                        var fovObj = cachedFieldOfViewField.GetValue(lensValue);
                        if (fovObj != null)
                            currentFov = UnboxFloat(fovObj);
                    }

                    // NearClipPlane = 0.1
                    if (cachedNearClipPlaneField != null)
                        cachedNearClipPlaneField.SetValue(lensValue, BoxFloat(0.1f));

                    // 先写回 Dutch / NearClipPlane
                    cachedMLensField.SetValue(cachedCameraObj, lensValue);
                }
            }

            // FOV 收回原生范围（>80 → 80，<40 → 40，区间内不动）—— 走原生变量而不是直写 m_Lens。
            //
            // 顺序很关键：游戏 UpdateFOV 是「整块读 m_Lens → 只改 FieldOfView → 整块写回」，
            // 所以必须先把 Dutch / NearClipPlane 落盘，再调 Set()。反过来会被 UpdateFOV
            // 的整块写回覆盖掉（它读到的是改之前的旧值）。
            if (!float.IsNaN(currentFov))
            {
                float clamped = currentFov;
                if (clamped > 80f) clamped = 80f;
                else if (clamped < 40f) clamped = 40f;

                if (clamped != currentFov)
                    NativeFovChannel.Set(clamped);
            }
        }

        private static void RegisterButton()
        {
            var buttonObj = GameObject.Find(ButtonPath);
            if (buttonObj == null) return;

            var buttonType = FindType("UnityEngine.UI.Button");
            if (buttonType == null) return;

            var getCompDef = GetGenericGetComponent();
            if (getCompDef == null) return;

            var button = getCompDef.MakeGenericMethod(buttonType).Invoke(buttonObj, null);
            if (button == null) return;

            var il2cppType = Il2CppType.From(buttonType);
            var field = FindIl2CppField(il2cppType, "m_OnClick");
            if (field == null) return;

            var pointerProp = button.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) return;

            var ptr = (System.IntPtr)pointerProp.GetValue(button);
            var il2cppObj = new Il2CppSystem.Object(ptr);
            var onClickValue = field.GetValue(il2cppObj);
            if (onClickValue == null) return;

            var unityEventType = FindType("UnityEngine.Events.UnityEvent");
            if (unityEventType == null) return;

            var addListenerIl2Cpp = FindIl2CppMethod(Il2CppType.From(unityEventType), "AddListener");
            if (addListenerIl2Cpp == null) return;

            System.Action callback = OnQuit;

            var unityActionType = FindType("UnityEngine.Events.UnityAction");
            if (unityActionType == null) return;

            var dsType = FindType("Il2CppInterop.Runtime.DelegateSupport");
            if (dsType == null) return;

            var convertDelegate = dsType.GetMethod("ConvertDelegate");
            if (convertDelegate == null) return;

            var generic = convertDelegate.MakeGenericMethod(unityActionType);
            var delegateInstance = generic.Invoke(null, new object[] { callback });

            addListenerIl2Cpp.Invoke(onClickValue, new Il2CppSystem.Object[] { (Il2CppSystem.Object)delegateInstance });
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

            var il2cppLensType = Il2CppType.From(lensSettingsType);
            cachedDutchField = FindIl2CppField(il2cppLensType, "Dutch");
            cachedFieldOfViewField = FindIl2CppField(il2cppLensType, "FieldOfView");
            cachedNearClipPlaneField = FindIl2CppField(il2cppLensType, "NearClipPlane");
        }

        private static float UnboxFloat(Il2CppSystem.Object boxed)
        {
            var ptr = IL2CPP.il2cpp_object_unbox(boxed.Pointer);
            return System.BitConverter.ToSingle(
                new byte[]
                {
                    Marshal.ReadByte(ptr),
                    Marshal.ReadByte(ptr, 1),
                    Marshal.ReadByte(ptr, 2),
                    Marshal.ReadByte(ptr, 3)
                }, 0);
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
