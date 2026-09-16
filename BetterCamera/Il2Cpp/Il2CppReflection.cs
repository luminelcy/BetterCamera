using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BetterCamera.Il2Cpp
{
    /// <summary>
    /// il2cpp 反射工具。全项目唯一一份 —— 之前这套代码在 8 个文件里逐字复制，
    /// 合计 610 行（约占全项目 24%），改一个 bug 要改 8 遍。
    ///
    /// 说明：这些 helper 存在的原因是「按字符串路径拿到的组件没有编译期类型可用」。
    /// 凡是能用强类型的地方（例如 NativeFovChannel 里的 RoomCameraController）就不该用这里。
    /// 目标是让这里尽量小。
    /// </summary>
    public static class Il2CppReflection
    {
        // ================= 类型查找 =================

        /// <summary>按完整名在所有已加载程序集里找托管类型（如 "Il2CppProject.NoArrowMovableSlider"）。</summary>
        public static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>拿到 <c>GameObject.GetComponent&lt;T&gt;()</c> 的泛型方法定义，配合 MakeGenericMethod 做运行时类型化取组件。</summary>
        public static MethodInfo GetGenericGetComponent()
        {
            foreach (var m in typeof(GameObject).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    return m;
            }
            return null;
        }

        // ================= 成员查找（沿继承链） =================

        public static Il2CppSystem.Reflection.FieldInfo FindIl2CppField(Il2CppSystem.Type type, string fieldName)
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

        public static Il2CppSystem.Reflection.MethodInfo FindIl2CppMethod(Il2CppSystem.Type type, string methodName)
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

        // ================= 装箱 / 拆箱 =================
        // il2cpp 的反射 API 只认装箱后的 Il2CppSystem.Object，所以基本类型进出都要过这里。

        public static Il2CppSystem.Object BoxFloat(float value)
        {
            var corlib = IL2CPP.il2cpp_get_corlib();
            var singleClass = IL2CPP.il2cpp_class_from_name(corlib, "System", "Single");
            var bytes = BitConverter.GetBytes(value);
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var boxedPtr = IL2CPP.il2cpp_value_box(singleClass, ptr);
            Marshal.FreeHGlobal(ptr);
            return new Il2CppSystem.Object(boxedPtr);
        }

        public static Il2CppSystem.Object BoxBool(bool value)
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

        public static Il2CppSystem.Object BoxInt(int value)
        {
            var corlib = IL2CPP.il2cpp_get_corlib();
            var int32Class = IL2CPP.il2cpp_class_from_name(corlib, "System", "Int32");
            var bytes = BitConverter.GetBytes(value);
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var boxedPtr = IL2CPP.il2cpp_value_box(int32Class, ptr);
            Marshal.FreeHGlobal(ptr);
            return new Il2CppSystem.Object(boxedPtr);
        }

        public static float UnboxFloat(Il2CppSystem.Object obj)
        {
            var unboxPtr = IL2CPP.il2cpp_object_unbox(obj.Pointer);
            return Marshal.PtrToStructure<float>(unboxPtr);
        }

        public static int UnboxInt(Il2CppSystem.Object obj)
        {
            var unboxPtr = IL2CPP.il2cpp_object_unbox(obj.Pointer);
            return Marshal.PtrToStructure<int>(unboxPtr);
        }

        // ================= 字段读写 =================

        public static void SetFloatField(Il2CppSystem.Object target, Il2CppSystem.Reflection.FieldInfo field, float value)
        {
            field.SetValue(target, BoxFloat(value));
        }

        public static void SetIntField(Il2CppSystem.Object target, Il2CppSystem.Reflection.FieldInfo field, int value)
        {
            field.SetValue(target, BoxInt(value));
        }

        /// <summary>读一个 float 字段，取不到返回 NaN。</summary>
        public static float GetFloatField(Il2CppSystem.Object target, Il2CppSystem.Reflection.FieldInfo field)
        {
            if (target == null || field == null) return float.NaN;
            var raw = field.GetValue(target);
            if (raw == null) return float.NaN;
            return UnboxFloat(raw);
        }
    }
}
