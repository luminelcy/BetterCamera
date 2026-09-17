using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using BetterCamera.Il2Cpp;

namespace BetterCamera
{
    /// <summary>
    /// 【诊断】记录每一次 `MonoBehaviour.RaiseCancellation()` —— Unity 通知
    /// "该取消 destroyCancellationToken 了"的唯一入口。
    ///
    /// 【为什么查这个】2026-09-17 的读数已经把问题逼到死角：
    ///
    ///     [lifecycle] RoomSnapSceneInstaller.Initialize —— 所在对象 "SceneContext"  帧=539
    ///     [lifecycle] StartSequence 第 1 次被调用  帧=539  令牌=★已取消★
    ///                         installer的令牌源=已创建(0x2DA9BCF6000)
    ///
    /// `destroyCancellationToken` 的 getter 规则是"**为 null 才新建**、否则直接返回已有的"
    ///（反编译确认）。读数是"已创建"，就意味着**在我们之前**已经有人访问过这个令牌源，
    /// 把它建了出来 —— 而它现在是取消状态。
    ///
    /// 而 `SceneContext` / installer 是帧 539 前后**刚新建**的对象：
    /// 一个刚建出来的对象，令牌源不该已经被创建过、更不该已经取消。
    ///
    /// 所以问题只剩一句：**谁碰过它？**
    ///
    /// 【⚠️ 这个探针的写法是被上一版教出来的】
    /// 第一版同时挂了 `RaiseCancellation` 和 `get_destroyCancellationToken` 的 postfix，
    /// 结果**游戏连主界面都加载不完整** —— 比它要诊断的 bug 严重得多。
    /// 教训：**引擎自己的方法（MonoBehaviour / Object）是全进程公用的热路径**，
    /// 挂在上面和挂在游戏自己的方法上，风险完全不是一个量级；而且给一个返回结构体的
    /// 属性 getter 挂 `ref __result` 这种值类型编组，本项目从没验证过。
    ///
    /// 所以这一版：
    ///   * **只挂 `RaiseCancellation`**，不碰任何 getter
    ///   * prefix 里**只做两件事**：读帧号、读对象指针。不读名字、不做反射、
    ///     不访问对象本身（对象此刻可能正在销毁）—— 整段只有常数级开销
    ///   * 有硬上限，宁可少记也不拖垮游戏
    ///
    /// 【判读】拿这里打出的指针跟 `[lifecycle] StartSequence … installer的令牌源=…`
    /// 里那个指针对比：
    ///   * 有同一个指针、且帧号 **早于** installer 的 Initialize → 找到了取消时刻，
    ///     接着看那一帧前后发生了什么（谁销毁/复用了这个对象）
    ///   * 完全没有这个指针 → 取消不走 RaiseCancellation，那是一条全新的线索
    /// </summary>
    internal static class CancellationTraceProbeHook
    {
        private const string HarmonyId = "BetterCamera.CancellationTrace";

        /// <summary>
        /// ⚠️ **必须是全名。** 更早一版这里写的是 "MonoBehaviour"，一个类型都没匹配上
        /// （`type.FullName != "MonoBehaviour"`），探针白挂一轮。
        /// </summary>
        private const string BehaviourTypeName = "UnityEngine.MonoBehaviour";
        private const string MethodNamePart = "RaiseCancellation";

        /// <summary>最多打这么多条。RaiseCancellation 在游戏中调用频繁，必须封顶。</summary>
        private const int MaxEmit = 200;

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static bool _applied;

        private static int _emitted;

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断，定位完删】no-cancel ⇒ 不挂
            if (ProbeFlags.Has("no-cancel"))
            {
                MelonLogger.Msg("[probe] no-cancel：本次不挂取消追踪探针");
                _applied = true;
                return true;
            }

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[cancel] 找不到 " + BehaviourTypeName + "." + MethodNamePart
                                        + "，取消追踪探针不生效");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var m in Targets)
                {
                    if (!Patched.Add(Il2CppReflection.MethodKey(m))) continue;
                    _harmony.Patch(m, prefix: new HarmonyMethod(typeof(CancellationTraceProbeHook), nameof(RaisePrefix)));
                    patched++;
                }

                _applied = true;
                _emitted = 0;
                MelonLogger.Msg("[cancel] 取消追踪探针已挂 " + patched + " 个（只挂 RaiseCancellation，只记帧号+指针）");
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[cancel] 挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫遍所有程序集找 `UnityEngine.MonoBehaviour.RaiseCancellation`（无参）。
        ///
        /// ⚠️ **不能按 "Il2Cpp" 前缀过滤程序集名** —— Unity 引擎自己的代理程序集叫
        /// `UnityEngine.CoreModule` 之类，不带那个前缀，会被整个排除掉
        /// （DestroyTraceProbeHook 第一版就是这么哑的）。所以扫全部程序集，靠 FullName 匹配。
        /// </summary>
        private static void CollectTargets()
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null || type.FullName != BehaviourTypeName) continue;

                    MethodInfo[] methods;
                    try { methods = type.GetMethods(BF); } catch { continue; }

                    foreach (var m in methods)
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if (m.Name.IndexOf(MethodNamePart, StringComparison.Ordinal) < 0) continue;
                        Targets.Add(m);
                    }
                }
            }
        }

        /// <summary>
        /// 返回 true = 照常执行。
        ///
        /// **整段只读帧号和原生指针。** 不读对象名、不做反射、不访问对象 ——
        /// 这个方法是在对象销毁过程中被调用的，此刻碰它比不碰危险得多
        /// （上一版就是因为多挂了 getter 而把游戏搞坏）。
        /// </summary>
        public static void RaisePrefix(object __instance)
        {
            try
            {
                if (_emitted >= MaxEmit) return;
                if (!(__instance is Il2CppObjectBase objBase)) return;

                _emitted++;
                MelonLogger.Msg("[cancel] RaiseCancellation  帧=" + UnityEngine.Time.frameCount
                                + "  ptr=0x" + objBase.Pointer.ToInt64().ToString("X")
                                + "  cts=" + ReadCtsPointer(objBase.Pointer));
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>
        /// 读这个 MonoBehaviour 自己的 `m_CancellationTokenSource` **指针值**（原生偏移 0x18）。
        ///
        /// 【为什么需要它】`[lifecycle]` 已经读出：installer 的令牌源是"已创建 + 已取消"，
        /// 但 installer 自己的 `RaiseCancellation` **从来没被调用过**（把 11 条 `[cancel]`
        /// 的指针逐一和 installer 的指针对比，一个都不匹配）。
        ///
        /// 这两件事不可能同时为真 —— Unity 的 `OnCancellationTokenCreated` 是**按对象注册**的，
        /// 每个 MonoBehaviour 的 CTS 只由它自己销毁时取消。所以唯一自洽的解释是：
        /// **installer 的 `m_CancellationTokenSource` 指向的是别的对象的 CTS**
        ///（这正是 `Instantiate` 浅拷贝 native 运行期字段的经典症状）。
        ///
        /// 有了这一列，就能在 `[cancel]` 的若干条里找出**真正持有那个 CTS 的对象** ——
        /// 那一个就是元凶，接下来只要查"谁销毁/复制了它"。
        ///
        /// 只读一段固定偏移的内存，不访问对象本身（此刻它正在销毁），是这里最安全的操作。
        /// </summary>
        private static string ReadCtsPointer(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero) return "?";

            try
            {
                IntPtr cts = Marshal.ReadIntPtr(objPtr, 0x18);
                return cts == IntPtr.Zero ? "null" : "0x" + cts.ToInt64().ToString("X");
            }
            catch
            {
                return "(读失败)";
            }
        }
    }
}
