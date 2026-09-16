using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Game
{
    /// <summary>
    /// 按路径拿原生组件。
    ///
    /// 之前这段「Find 对象 → MakeGenericMethod 取组件 → 反射读 Pointer →
    /// new Il2CppSystem.Object(ptr)」的 18 行模板在 5 个文件里逐字重复，差异只有类型名。
    /// 这里参数化成三个入口。
    ///
    /// 为什么最后要转成裸 Il2CppSystem.Object 而不是保留强类型包装：
    /// 组件类型是运行时按字符串查出来的（如 NoArrowMovableSlider），编译期没有对应的
    /// 托管类型可写，只能退化成指针包装。凡是有强类型可用的地方（见 NativeFovChannel）
    /// 就不该用这里。
    /// </summary>
    public static class NativeRefs
    {
        /// <summary>找对象 → 取指定类型的组件 → 转成裸指针包装。任一步失败返回 null。</summary>
        public static Il2CppSystem.Object FindComponent(string path, string componentTypeName)
        {
            var go = GameObject.Find(path);
            if (go == null) return null;

            var type = Il2CppReflection.FindType(componentTypeName);
            if (type == null) return null;

            var getCompDef = Il2CppReflection.GetGenericGetComponent();
            if (getCompDef == null) return null;

            var component = getCompDef.MakeGenericMethod(type).Invoke(go, null);
            return ToRawObject(component);
        }

        // ---- 具体类型：调用方用这几个，不用记类型名 ----

        public static Il2CppSystem.Object FindSlider(string path)
            => FindComponent(path, "Il2CppProject.NoArrowMovableSlider");

        public static Il2CppSystem.Object FindCamera(string path)
            => FindComponent(path, "Il2CppCinemachine.CinemachineVirtualCamera");

        public static Il2CppSystem.Object FindVolume(string path)
            => FindComponent(path, "UnityEngine.Rendering.Volume");

        public static Il2CppSystem.Object FindButton(string path)
            => FindComponent(path, "UnityEngine.UI.Button");

        /// <summary>
        /// 某个托管类型对应的 Il2CppSystem.Type，供后续字段/方法查找用。
        /// 例：<c>NativeRefs.TypeOf("UnityEngine.UI.Slider")</c>
        /// </summary>
        public static Il2CppSystem.Type TypeOf(string managedTypeName)
        {
            var t = Il2CppReflection.FindType(managedTypeName);
            return t == null ? null : Il2CppType.From(t);
        }

        /// <summary>
        /// 把 Il2CppInterop 的组件包装对象降级成裸指针包装。
        ///
        /// MakeGenericMethod 走的是纯 .NET 反射，拿到的是托管包装；
        /// 而 il2cpp 的字段/方法反射 API 只吃 Il2CppSystem.Object，所以要拆出 Pointer。
        /// </summary>
        private static Il2CppSystem.Object ToRawObject(object componentWrapper)
        {
            if (componentWrapper == null) return null;

            var pointerProp = componentWrapper.GetType().GetProperty("Pointer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pointerProp == null) return null;

            var ptr = (IntPtr)pointerProp.GetValue(componentWrapper);
            return new Il2CppSystem.Object(ptr);
        }
    }
}
