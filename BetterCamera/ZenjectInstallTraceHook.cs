using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using BetterCamera.Il2Cpp;

namespace BetterCamera
{
    /// <summary>
    /// 【临时诊断，定位完就删】打出每次 Zenject MonoKernel 启动时的类型名。
    ///
    /// 为什么查这个：Player.log 里有 12 条
    ///     NullReferenceException  at Zenject.MonoKernel.Start ()
    /// 而且**只有一帧**（它是被 Unity 的 player loop 调的，栈到这儿就断了），
    /// 光看日志认不出是哪个安装器。
    ///
    /// MonoKernel.Start 是 Zenject 安装器启动的地方。它抛异常 = 那个 MonoInstaller
    /// 没装完 = 它负责的绑定全是空的 → 依赖它的 Presenter / UI 直接不工作。
    /// 「部分按钮无法使用」完全可以由这个造成。
    ///
    /// 判读：紧跟 NRE 之前那一行打的类型名，就是失败的安装器
    /// （顺序对应 —— 前缀在同一次 Start 的最前面跑）。
    ///
    /// ⚠️ **不用 typeof 取目标**。上一个诊断就是那么栽的：同名类型在多个程序集里都有
    /// （Assembly-CSharp / Il2CppBehaviourAssemblyDefinition / Il2CppViewAssemblyDefinition），
    /// 编译期只能拿到其中一个，补丁就打在了没人经过的那份上 —— 绑上了，但一次没触发。
    /// 这里改成运行时扫遍所有 Il2Cpp* 程序集，把找到的每一个 MonoKernel.Start 都打上。
    /// （本项目 CaptureSizeRatioHook.CollectTargets 就是这个套路。）
    /// </summary>
    internal static class ZenjectInstallTraceHook
    {
        private const string HarmonyId = "BetterCamera.ZenjectInstallTrace";

        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static HarmonyLib.Harmony _harmony;

        public static bool Apply()
        {
            // 【临时诊断，定位完删】no-kernel-guard ⇒ 不挂（和 MonoKernelGuardHook 共用一个开关，
            // 因为它们挂在同一个方法 MonoKernel.Start 上 —— 要判断"是不是我们踩坏了这个方法的
            // 调用约定"，必须两个一起关。见 MonoKernelGuardHook.Apply 里的完整说明。）
            if (ProbeFlags.Has("no-kernel-guard"))
            {
                MelonLogger.Msg("[probe] no-kernel-guard：不挂 Zenject 安装追踪探针（对照实验）");
                return true;
            }

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[zj] 找不到 MonoKernel.Start，诊断不生效");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var method in Targets)
                {
                    string key = (method.DeclaringType != null ? method.DeclaringType.FullName : "?")
                                 + "." + method.Name;
                    if (!Patched.Add(key)) continue;

                    _harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(ZenjectInstallTraceHook), nameof(Prefix)));
                    patched++;
                }

                MelonLogger.Msg("[zj] 已挂上 " + patched + " 个 MonoKernel.Start（共扫到 "
                                + Targets.Count + " 个候补）");
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[zj] 挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫遍所有 Il2Cpp* 程序集，收所有叫 MonoKernel 的类型的无参 Start。
        ///
        /// GetTypes() 在大程序集上会抛 ReflectionTypeLoadException（部分类型依赖缺失），
        /// 必须从异常里取已加载的那部分，否则恰好会跳过目标所在的大程序集。
        /// </summary>
        private static void CollectTargets()
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name ?? "";
                if (!asmName.StartsWith("Il2Cpp")) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null || type.Name != "MonoKernel") continue;

                    MethodInfo start;
                    try { start = type.GetMethod("Start", BF, null, Type.EmptyTypes, null); }
                    catch { continue; }

                    if (start != null) Targets.Add(start);
                }
            }
        }

        /// <summary>
        /// __instance 收成 object 而不是 MonoBehaviour：本项目引用的是 stub 版 UnityEngine
        /// （UnityDependencies 下的），那个 MonoBehaviour 不是 il2cpp 代理，和代理类型的
        /// 继承关系也对不上（Harmony 会拒绝绑定）。object 对任何托管对象都成立。
        /// </summary>
        public static void Prefix(object __instance)
        {
            try
            {
                MelonLogger.Msg("[zj] Start " + Describe(__instance)
                                + "  f=" + UnityEngine.Time.frameCount);
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>
        /// 认这个安装器是谁。
        ///
        /// **不能用 <c>__instance.GetType().Name</c>** —— 实测 9 次全打成 "MonoKernel"：
        /// Il2CppInterop 代理对象的 .NET 类型是**声明类型**（我们打的是基类 MonoKernel.Start），
        /// 它不反映真实的原生子类。所以要绕到原生指针上去问。
        ///
        /// 路径：代理 → Il2CppObjectBase.Pointer（裸指针）→ 包成 Il2CppSystem.Object
        /// → 按 UnityEngine.Object 重建一个**带类型**的包装 → 读 name。
        /// 安装器是 MonoBehaviour，它的 name 就是所在对象名，够用来认人。
        /// （WrapAsManaged 是 TmpKit 里已经在用的套路。）
        /// </summary>
        private static string Describe(object instance)
        {
            if (instance == null) return "(null)";

            var objBase = instance as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            if (objBase == null) return instance.GetType().Name;

            var raw = new Il2CppSystem.Object(objBase.Pointer);
            var wrapped = Il2CppReflection.WrapAsManaged(raw, "UnityEngine.Object");
            var name = wrapped != null
                ? wrapped.GetType().GetProperty("name")?.GetValue(wrapped) as string
                : null;

            return string.IsNullOrEmpty(name) ? "(读不到名字)" : name;
        }
    }
}
