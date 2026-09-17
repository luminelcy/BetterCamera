using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using BetterCamera.Il2Cpp;

namespace BetterCamera
{
    /// <summary>
    /// 把「被 Forget() 吞掉的异步异常」捞出来 —— 这类异常默认是不可见的。
    ///
    /// 【为什么需要它】拍照场景的整个流程是一个 `Forget()` 掉的 UniTask 循环
    /// （见 RequestDropProbeHook 的说明）。`Forget()` 的语义就是"我不关心结果"，
    /// 所以循环里任何一步抛异常都不会中断游戏，也不会打断 UI —— 它只是让那条流程
    /// **悄无声息地停在那里**，表现就是快门和返回键一起失效、而日志干干净净。
    ///
    /// 这条链条目前只是"高度可疑、未证实"：如果它是真的，异常本体应该在
    /// UniTaskScheduler 里露过面；如果探针什么都不打，那就反过来证明循环不是被异常
    /// 打死的，而是**卡在某个等待里**——两种结论的修法完全不同，所以这面开关很值。
    ///
    /// 【挂哪儿】`UniTaskScheduler.PublishUnobservedTaskException(Exception)`：
    /// UniTask 所有"没有观察者"的异常最终都汇聚到这一个静态方法（默认实现是
    /// Debug.LogException）。挂它比挂 `AsyncUniTaskMethodBuilder.SetException` 好 ——
    /// 后者是**每一个** async 方法抛异常都要经过的地方（含被正常处理的），噪音太大。
    ///
    /// 【为什么用 ToString()】Exception 的 ToString() 自带 `类型: 消息\n堆栈`，
    /// 一次调用把认人需要的全拿到了，不用逐字段反射读 Message/StackTrace。
    ///
    /// ⚠️ 已知的判读陷阱：`PropagateOperationCanceledException` 为 false 时
    /// OperationCanceledException 会被丢掉；而"取消"在本项目里大量出现
    /// （取消令牌满天飞），所以真出现了 OCE 也不要当成 bug。
    /// </summary>
    internal static class UniTaskExceptionProbeHook
    {
        private const string HarmonyId = "BetterCamera.UniTaskExceptionProbe";

        private const string SchedulerTypeName = "UniTaskScheduler";
        private const string PublishMethodName = "PublishUnobservedTaskException";

        /// <summary>日志里异常的正文上限 —— 堆栈可能很长，但我们只需要头部。</summary>
        private const int MaxTextLength = 1200;

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static bool _applied;

        /// <summary>同一个异常只报一次 —— 循环每帧重试的话会刷屏。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        public static void ResetReported() => Reported.Clear();

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断，定位完删】no-uniexc ⇒ 不挂（做对照用）
            if (ProbeFlags.Has("no-uniexc"))
            {
                MelonLogger.Msg("[probe] no-uniexc：本次不挂异步异常探针");
                _applied = true;
                return true;
            }

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[uniexc] 找不到 UniTaskScheduler." + PublishMethodName
                                        + "，被吞掉的异步异常捞不出来");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var method in Targets)
                {
                    string key = Il2CppReflection.MethodKey(method);
                    if (!Patched.Add(key)) continue;

                    _harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(UniTaskExceptionProbeHook), nameof(Prefix)));
                    patched++;
                }

                _applied = true;
                MelonLogger.Msg("[uniexc] 异步异常探针已挂 " + patched + " 个");
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[uniexc] 挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫遍所有 Il2Cpp* 程序集，收所有叫 UniTaskScheduler 的类型的单参数静态发布方法。
        ///
        /// 不用 typeof：同名类型可能存在于多份代理程序集里，编译期只能拿到其中一份，
        /// 补丁就打在了没人经过的那份上（本项目栽过多次）。
        /// GetTypes() 在大程序集上会抛 ReflectionTypeLoadException，必须从异常里取
        /// 已加载的那部分，否则恰好会跳过目标所在的大程序集。
        /// </summary>
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
                    if (type == null || type.Name != SchedulerTypeName) continue;

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BF); } catch { continue; }

                    foreach (var m in methods)
                    {
                        if (m.Name != PublishMethodName) continue;
                        if (m.GetParameters().Length != 1) continue;
                        Targets.Add(m);
                    }
                }
            }
        }

        /// <summary>
        /// `__args` 而不是具名参数：il2cpp 侧没有参数名，Harmony 按名匹配会绑不上。
        /// 收 object[] 对本项目一贯可靠（同一个坑见 DotweenTargetTraceHook）。
        ///
        /// 返回 true = 照常执行 —— 我们只读，绝不改变游戏行为。
        /// </summary>
        public static void Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1) return;
                if (!(__args[0] is Il2CppObjectBase exBase)) return;

                string text = Describe(exBase);
                if (string.IsNullOrEmpty(text)) return;
                if (!Reported.Add(text)) return;

                MelonLogger.Error("[uniexc] 未观察的异步异常（游戏自己看不见这条，"
                                  + "它只会让某条流程静默停住）:\n" + text);
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>
        /// 读异常的文本形式。走原生指针重建一个带类型的包装再 ToString()：
        /// 代理对象的 .NET 类型只是声明类型，直接对它 ToString 会丢掉真实类型名。
        /// </summary>
        private static string Describe(Il2CppObjectBase exBase)
        {
            try
            {
                var raw = new Il2CppSystem.Object(exBase.Pointer);
                string text = raw.ToString();
                if (string.IsNullOrEmpty(text)) return "(读不到异常正文)";

                return text.Length > MaxTextLength
                    ? text.Substring(0, MaxTextLength) + "\n…（已截断）"
                    : text;
            }
            catch (Exception e)
            {
                return "(读异常失败: " + e.GetType().Name + ": " + e.Message + ")";
            }
        }
    }
}
