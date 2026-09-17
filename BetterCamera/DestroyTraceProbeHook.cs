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
    /// 【诊断】记录"谁被 UnityEngine.Object.Destroy 了"。
    ///
    /// 【为什么查这个】2026-09-17 已经把卡死的机制钉死到一句话：
    ///
    ///     RoomSnapSceneInstaller.Initialize() 里 Forget() 的 StartSequence 任务
    ///     **当场就是 canceled 状态**（堆栈里是 UniTask+CanceledResultSource.GetResult）
    ///     ⇒ 场景流程从未运行 ⇒ 快门和返回键的请求永远没人接
    ///
    /// 而 `StartSequence` 收到的取消令牌来自 `MonoBehaviour.get_destroyCancellationToken()`
    /// （反编译确认，能看到 Unity 内部符号 `OnCancellationTokenCreated_Injected`）——
    /// 这个令牌**只有一种情况会取消：那个 MonoBehaviour 被销毁**。而且一旦取消就永久取消。
    ///
    /// 所以问题收敛成一句：**谁销毁了 RoomSnapSceneInstaller 所在的对象？**
    /// 已知它挂在 `SceneContext` 上（2026-09-17 用游戏内的桥查过，只此一份、当时还活着）。
    ///
    /// 本探针就是回答这一句的：把每一次 Destroy 的对象名打出来。
    /// 判读：
    ///   * 日志里出现 `SceneContext`（或它的父对象）被 Destroy → 直接命中
    ///   * 只出现本 mod 的克隆体 / P_ZoomHandleObject → 那不是元凶，换方向
    ///
    /// 【限流】每个对象名只报一次。Destroy 在游戏里调用极频繁（UI 对象随时销毁），
    /// 不限流会淹掉一切；而"某个名字被销毁过"这个事实报一次就够。
    /// </summary>
    internal static class DestroyTraceProbeHook
    {
        private const string HarmonyId = "BetterCamera.DestroyTrace";

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static bool _applied;

        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>
        /// 最多打这么多条。
        ///
        /// ⚠️ `Object.Destroy` 是**引擎级热路径**，而 `GetObjectName` 每次都要
        /// WrapAsManaged + FindType 遍历全部程序集 —— 很贵。上一个探针
        /// （CancellationTraceProbeHook）就是挂在同类热路径上把游戏搞坏的，
        /// 所以这里给自己加个硬上限，宁可少记也不拖垮游戏。
        /// </summary>
        private const int MaxEmit = 300;
        private static int _emitted;

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断，定位完删】no-destroy ⇒ 不挂
            if (ProbeFlags.Has("no-destroy"))
            {
                MelonLogger.Msg("[probe] no-destroy：本次不挂销毁追踪探针");
                _applied = true;
                return true;
            }

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[destroy] 找不到 UnityEngine.Object.Destroy，销毁追踪探针不生效");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var m in Targets)
                {
                    string key = Il2CppReflection.MethodKey(m);
                    if (!Patched.Add(key)) continue;

                    _harmony.Patch(m, prefix: new HarmonyMethod(typeof(DestroyTraceProbeHook), nameof(Prefix)));
                    patched++;
                }

                _applied = true;
                MelonLogger.Msg("[destroy] 销毁追踪探针已挂 " + patched + " 个");
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[destroy] 挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫遍所有程序集找 `UnityEngine.Object` 上的静态 Destroy / DestroyImmediate
        /// （参数个数 1 的那些重载）。
        ///
        /// ⚠️ **不能按 "Il2Cpp" 前缀过滤程序集名** —— 这条第一版就是这么写的，
        /// 结果日志里直接报"找不到 UnityEngine.Object.Destroy"：Unity 引擎自己的代理
        /// 程序集叫 `UnityEngine.CoreModule` / `UnityEngine.PhysicsModule` 之类，
        /// **不带 Il2Cpp 前缀**，被那个过滤器整个排除了。
        /// （同一个坑本项目的 MonoKernelGuardHook 注释里也记过：Assembly-CSharp 系列
        /// 也是真正的代理程序集。）
        /// 所以这里扫全部程序集，靠 FullName 精确匹配避免误收。
        ///
        /// GetTypes() 在大程序集上会抛 ReflectionTypeLoadException，
        /// 必须从异常里取已加载的那部分，否则恰好会跳过目标所在的大程序集。
        /// </summary>
        private static void CollectTargets()
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null || type.FullName != "UnityEngine.Object") continue;

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BF); } catch { continue; }

                    foreach (var m in methods)
                    {
                        if (m.Name != "Destroy" && m.Name != "DestroyImmediate") continue;

                        // 只要收一个参数的重载 —— 多参数的那些是别的语义，
                        // 我们只关心"销毁这个对象"。
                        if (m.GetParameters().Length != 1) continue;
                        Targets.Add(m);
                    }
                }
            }
        }

        /// <summary>
        /// 返回 true = 照常执行 —— 只读，绝不改变游戏行为。
        /// </summary>
        public static void Prefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1) return;
                if (_emitted >= MaxEmit) return;          // 廉价判断放最前面
                if (!(__args[0] is Il2CppObjectBase objBase)) return;

                var raw = new Il2CppSystem.Object(objBase.Pointer);
                string name = Il2CppReflection.GetObjectName(raw);
                if (string.IsNullOrEmpty(name)) name = "(名字读不到)";
                if (!Reported.Add(name)) return;
                _emitted++;

                var type = raw.GetIl2CppType();
                MelonLogger.Msg("[destroy] 销毁 \"" + name + "\"  类型="
                                + (type?.Name ?? "?") + "  帧=" + UnityEngine.Time.frameCount);
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }
    }
}
