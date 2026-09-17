using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using BetterCamera.Il2Cpp;

namespace BetterCamera
{
    /// <summary>
    /// 【临时诊断，定位完就删】把 DOTween 那条「目标丢了 / 启动失败」的补间**在动哪个对象**打出来。
    ///
    /// 要查的是这个：有本 mod 时 Player.log 里有 9 条
    ///     DOTWEEN ► Target or field is missing/null … at UnityEngine.CanvasGroup.set_alpha
    ///     DOTWEEN ► Tween startup failed (NULL target/property …)
    /// 没本 mod 时 0 条（2026-09-17 两次真机 A/B，行程不同但同一条入口日志都出现）。
    /// 现在缺的是「那个目标是谁」—— 日志里只有 setter 的签名，没有对象。
    ///
    /// 判读：警告里 DOTween 会把出事的 Tween 传进来，它的 <c>target</c> 就是被动的那个
    /// CanvasGroup。打出来的名字能一眼认出是哪边：
    ///   P_BetterCamera… / P_SwitchCameraFocusModeButtonObject1 / P_BCCaptureSizeOption_… → 本 mod 的克隆体
    ///   别的原生名字 → 原生 UI（那就要看是谁把它的引用搞空了）
    /// 名字为空/读不到 = 对象已销毁（MissingReference），同样是好线索。
    ///
    /// 挂的是 <c>DG.Tweening.Core.Debugger.LogWarning(object, Tween)</c>：两类警告都从这儿出，
    /// 一处覆盖。它是静态、非内联（里头有 Debug.Break）的方法，能绑住。
    /// </summary>
    internal static class DotweenTargetTraceHook
    {
        private const string HarmonyId = "BetterCamera.DotweenTargetTrace";

        /// <summary>扫所有 Il2Cpp* 程序集，按这两条认目标 —— 不写死命名空间，前缀各家不一样。</summary>
        private const string DebuggerTypeName = "Debugger";
        private const string DebuggerNamespacePart = "DG.Tweening";
        private const string LogWarningName = "LogWarning";

        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static HarmonyLib.Harmony _harmony;
        private static bool _applied;

        /// <summary>同一条（补间 + 目标）只报一次，避免每帧刷屏。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        public static bool Apply()
        {
            if (_applied) return true;

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[dttrace] 找不到 DG.Tweening.Core.Debugger.LogWarning，诊断不生效");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var method in Targets)
                {
                    string key = (method.DeclaringType != null ? method.DeclaringType.FullName : "?")
                                 + "." + method.Name + "/" + method.GetParameters().Length;
                    if (!Patched.Add(key)) continue;

                    _harmony.Patch(method,
                        postfix: new HarmonyMethod(typeof(DotweenTargetTraceHook), nameof(Postfix)));
                    patched++;
                }

                _applied = true;
                MelonLogger.Msg("[dttrace] 已挂上 " + patched + " 个 DOTween 警告点");
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[dttrace] 挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static void CollectTargets()
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

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
                    if (type == null || type.Name != DebuggerTypeName) continue;
                    if ((type.Namespace ?? type.FullName ?? "").IndexOf(DebuggerNamespacePart, StringComparison.Ordinal) < 0)
                        continue;

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BF); } catch { continue; }

                    foreach (var m in methods)
                    {
                        // 只要收 (message, Tween) 的那个重载 —— DOTween 出错时才带 Tween
                        if (m.Name != LogWarningName) continue;
                        if (m.GetParameters().Length != 2) continue;
                        Targets.Add(m);
                    }
                }
            }
        }

        /// <summary>
        /// __args 而不是具名参数：参数类型是 il2cpp 代理（Il2CppSystem.Object / Il2CppDG.Tweening.Tween），
        /// 收成 object 数组最稳（同一个坑见 MonoKernelGuardHook 里 __instance 收 object 的说明）。
        /// </summary>
        public static void Postfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                if (!(__args[1] is Il2CppObjectBase tween)) return;   // 没带 Tween 的警告（别的用途），不掺和

                string what = Describe(__args[0]);
                string target = DescribeTarget(tween);

                if (!Reported.Add(what + " | " + target)) return;
                MelonLogger.Msg("[dttrace] DOTween 目标出事: " + target
                                + "  ← " + what);
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>警告正文（前 90 字），用来区分「目标丢了」和「启动失败」。</summary>
        private static string Describe(object arg)
        {
            if (!(arg is Il2CppObjectBase raw)) return "(无正文)";

            try
            {
                var text = raw.TryCast<Il2CppSystem.String>()?.ToString();
                if (string.IsNullOrEmpty(text)) return "(正文读不到)";
                return text.Length > 90 ? text.Substring(0, 90) : text;
            }
            catch { return "(正文读不到)"; }
        }

        /// <summary>
        /// 补间在动谁：名字 + stringId + id。名字读不到通常就是对象已销毁。
        ///
        /// 类型从 <c>GetType()</c> 拿 —— 参数声明就是 Tween，代理就是按声明类型编组的。
        /// 方法查找要 Il2CppSystem.Type，调用目标要 Il2CppSystem.Object（裸指针包装），
        /// 两者不能混用（见 Il2CppReflection 里各处注释）。
        /// </summary>
        private static string DescribeTarget(Il2CppObjectBase tween)
        {
            try
            {
                var raw = new Il2CppSystem.Object(tween.Pointer);
                var type = Il2CppType.From(tween.GetType());
                var none = System.Array.Empty<Il2CppSystem.Object>();

                var target = Il2CppReflection.FindIl2CppMethod(type, "get_target")?.Invoke(raw, none);
                string name = Il2CppReflection.GetObjectName(target) ?? "(目标为空或已销毁)";

                var stringId = Il2CppReflection.FindIl2CppMethod(type, "get_stringId")?.Invoke(raw, none)?.ToString();

                var id = Il2CppReflection.FindIl2CppMethod(type, "get_id")?.Invoke(raw, none);

                return "\"" + name + "\""
                       + (string.IsNullOrEmpty(stringId) ? "" : " stringId=" + stringId)
                       + " tweenId=" + (id == null ? -1 : Il2CppReflection.UnboxInt(id));
            }
            catch (Exception e)
            {
                return "(读目标失败: " + e.GetType().Name + ": " + e.Message + ")";
            }
        }
    }
}
