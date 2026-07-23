using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BetterCamera
{
    public static class CameraInitHandle
    {
        public static void Init(MelonLogger.Instance logger)
        {
            SetNearClipPlane();
        }

        private static void SetNearClipPlane()
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
            var il2cppCameraObj = new Il2CppSystem.Object(ptr);

            var il2cppCameraType = Il2CppType.From(cameraType);
            var mLensField = FindIl2CppField(il2cppCameraType, "m_Lens");
            if (mLensField == null) return;

            var lensValue = mLensField.GetValue(il2cppCameraObj);
            if (lensValue == null) return;

            var lensSettingsType = FindType("Il2CppCinemachine.LensSettings");
            if (lensSettingsType == null) return;

            var nearClipField = FindIl2CppField(Il2CppType.From(lensSettingsType), "NearClipPlane");
            if (nearClipField == null) return;

            // NearClipPlane = 0.01
            nearClipField.SetValue(lensValue, BoxFloat(0.01f));

            // 写回 m_Lens
            mLensField.SetValue(il2cppCameraObj, lensValue);
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
