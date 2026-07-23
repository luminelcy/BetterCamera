using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using System.Reflection;

namespace BetterCamera
{
    public static class DutchReset
    {
        private const string ButtonPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Footer/Center/P_BetterCameraShowRoomStates/CommonButton/Button";

        public static void Init(MelonLogger.Instance logger)
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

            System.Action callback = ResetDutch;

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

        private static void ResetDutch()
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
            var il2cppObj = new Il2CppSystem.Object(ptr);

            var il2cppCameraType = Il2CppType.From(cameraType);
            var mLensField = FindIl2CppField(il2cppCameraType, "m_Lens");
            if (mLensField == null) return;

            var lensValue = mLensField.GetValue(il2cppObj);
            if (lensValue == null) return;

            var lensSettingsType = FindType("Il2CppCinemachine.LensSettings");
            if (lensSettingsType == null) return;

            var il2cppLensType = Il2CppType.From(lensSettingsType);
            var dutchField = FindIl2CppField(il2cppLensType, "Dutch");
            if (dutchField == null) return;

            // 将 0f 装箱为 Il2CppSystem.Object
            var corlib = IL2CPP.il2cpp_get_corlib();
            var singleClass = IL2CPP.il2cpp_class_from_name(corlib, "System", "Single");
            var zeroBytes = System.BitConverter.GetBytes(0f);
            var zeroPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(zeroBytes.Length);
            System.Runtime.InteropServices.Marshal.Copy(zeroBytes, 0, zeroPtr, zeroBytes.Length);
            var boxedPtr = IL2CPP.il2cpp_value_box(singleClass, zeroPtr);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(zeroPtr);
            var boxedZero = new Il2CppSystem.Object(boxedPtr);

            dutchField.SetValue(lensValue, boxedZero);
            mLensField.SetValue(il2cppObj, lensValue);
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
