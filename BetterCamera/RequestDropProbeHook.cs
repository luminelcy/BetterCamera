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
    /// 把「玩家按了键、游戏流程却没接住」这件事变成**一行日志**。
    ///
    /// 【要解决的诊断盲区】卡死时的表现是：悬停正常、点击音效正常、我们自己的监听也跑，
    /// 但快门不出片、返回键不退出，而且**日志里一条异常都没有**。整个排查过程卡在
    /// "没有任何可观察量"上 —— 你无法区分"按键没送到"和"送到了但流程停了"。
    ///
    /// 【病根（反编译确认）】拍照场景所有"游戏自己的流程"都由同一个 Forget() 掉的 UniTask
    /// 循环驱动（RoomSnapSceneSequence.StartSequence → LifeCycleSequenceAsync）。快门和返回
    /// 键在这个循环里各是一个 Process，等的都是 `IRequestConsumer<T>.WaitRequestAndConsumeAsync`。
    /// 而它们的入队端是：
    ///
    ///     RequestHandler&lt;TRequest&gt;.PushRequest(TRequest value)     VA 0x183DC47A0
    ///         if (_isWaitingRequest)          // ← 只有这一个条件
    ///             _requestValue = value;      // 否则**静默丢弃**：不抛、不报、无栈
    ///
    /// 也就是说：只要那个循环不在"停在等请求"的状态，玩家按下的每一个键都会被悄悄扔掉。
    /// 这正好解释"两个按钮一起失效 + 零异常日志"。
    ///
    /// 【本探针做什么】挂 PushRequest 的前缀，读 `_isWaitingRequest`：
    ///   true  → 有人在等，一切正常，什么都不做（正常路径零日志）
    ///   false → 这个请求会被丢弃 ⇒ 打一条 Error，带上**真实的泛型实例类型**
    ///
    /// 这一条应该常驻，不是临时诊断：正常游戏里玩家按键时循环必然在等，`_isWaitingRequest`
    /// 一定是 true；是 false 就意味着流程已经死了，这正是玩家（和排查的人）最需要看到的信号。
    ///
    /// 【为什么读 `__instance` 的真实类型而不是补丁目标名】这个方法的多个泛型实例化在
    /// 二进制里**共享同一段原生代码**（dump 里 RequestHandler&lt;CloseRequest&gt; /
    /// &lt;PhotoCaptureRequest&gt; / &lt;UndoRequest&gt; … 60 多个都列在同一个 RVA 下），
    /// 所以补丁实际会拦到所有 `RequestHandler&lt;T&gt;`。日志里的"请求类型"必须从
    /// `__instance` 的 il2cpp 真实类型上读，不能信 `__originalMethod`
    /// （那个永远是补丁注册时用的那一个，与本次调用无关）。
    ///
    /// 【字段不能按偏移写死】`_isWaitingRequest` 的偏移取决于 `Nullable&lt;TRequest&gt;` 的
    /// 大小 ⇒ 每个 T 都可能不同。一律走字段反射。
    /// </summary>
    internal static class RequestDropProbeHook
    {
        private const string HarmonyId = "BetterCamera.RequestDropProbe";

        /// <summary>RequestHandler&lt;TRequest&gt; 的托管代理全名（反引号 1 = 一个泛型参数）。</summary>
        private const string HandlerOpenTypeName = "Il2CppTanitakaTech.UnityProcessManager.RequestHandler`1";

        /// <summary>要观测的 TRequest。返回键推的就是它（见 CommonBackButtonObjectPresenter）。</summary>
        private const string UndoRequestTypeName = "Il2CppProject.UndoRequest";

        /// <summary>
        /// 显式接口实现，Il2CppInterop 会把名字改成
        /// `TanitakaTech_UnityProcessManager_IRequestPusher_TRequest__PushRequest` 之类，
        /// 所以按**名字片段 + 参数个数**认，不按全名。
        /// </summary>
        private const string MethodNamePart = "PushRequest";

        private const string WaitingFieldName = "_isWaitingRequest";

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static bool _applied;

        /// <summary>同一个请求类型只报一次 —— 玩家连按十次返回键不该刷十行。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>
        /// 只对这几个请求报警。
        ///
        /// **必须白名单。** PushRequest 是通用机制，游戏自己就有大量"即发即忘"的请求 ——
        /// 实测场景刚加载完（帧 491）就会推一个没人在等的 `ChangeHomeSceneUIDisplayRequest`。
        /// 不筛的话日志立刻被这种误报刷满，真正该看的那条反而被埋掉，而且会让人误以为
        /// "游戏又卡住了"。留在名单里的三个，正好是拍照场景里玩家能按的三个键。
        /// </summary>
        private static readonly string[] InterestingNames =
        {
            "UndoRequest",          // 返回键 —— 卡死时最该看的就是它
            "PhotoCaptureRequest",  // 快门
            "MovieRecordRequest",   // 录像
        };

        /// <summary>【临时诊断】req-all ⇒ 取消白名单，所有请求都报（排查别的键失灵时用）。</summary>
        private const string AllFlag = "req-all";

        /// <summary>按真实类型缓存字段解析结果（PushRequest 每次按键都会跑）。</summary>
        private static readonly Dictionary<string, Il2CppSystem.Reflection.FieldInfo> WaitingFields =
            new Dictionary<string, Il2CppSystem.Reflection.FieldInfo>();

        /// <summary>进场景时清掉限流，让每种请求在每一局里都有资格再报一次。</summary>
        public static void ResetReported() => Reported.Clear();

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断，定位完删】no-req ⇒ 不挂这个探针（做"完全不插手请求系统"的对照）
            if (ProbeFlags.Has("no-req"))
            {
                MelonLogger.Msg("[probe] no-req：本次不挂请求丢弃探针");
                _applied = true;
                return true;
            }

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[req] 找不到 RequestHandler<" + UndoRequestTypeName
                                        + ">.PushRequest，请求丢弃探针不生效"
                                        + "（卡死时就分不清「按键没送到」和「流程停了」）");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var method in Targets)
                {
                    string key = Il2CppReflection.MethodKey(method);
                    if (!Patched.Add(key)) continue;

                    _harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(RequestDropProbeHook), nameof(Prefix)));
                    patched++;
                }

                _applied = true;

                // 成功也要报 —— 这个项目反复栽在"补丁挂上了但一次不触发、而且什么都不打"上
                MelonLogger.Msg("[req] 请求丢弃探针已挂 " + patched + " 个："
                                + string.Join("、", DescribeTargets()));
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[req] 请求丢弃探针挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static IEnumerable<string> DescribeTargets()
        {
            foreach (var m in Targets)
                yield return (m.DeclaringType?.Name ?? "?") + "." + m.Name;
        }

        /// <summary>
        /// 闭合出 `RequestHandler&lt;UndoRequest&gt;`，在上面找那个收一个参数的 PushRequest。
        ///
        /// 为什么自己 MakeGenericType 而不是扫所有已加载类型：Il2CppInterop 只为泛型类型
        /// 生成**开放泛型定义**，运行时用的闭合类型是 MakeGenericType 出来的，
        /// 程序集里并不存在 `RequestHandler_UndoRequest` 这种类型 —— 扫是扫不到的。
        /// </summary>
        private static void CollectTargets()
        {
            var open = Il2CppReflection.FindType(HandlerOpenTypeName);
            if (open == null)
            {
                MelonLogger.Warning("[req] 找不到 " + HandlerOpenTypeName + "（程序集还没加载？）");
                return;
            }

            var undo = Il2CppReflection.FindType(UndoRequestTypeName);
            if (undo == null)
            {
                MelonLogger.Warning("[req] 找不到 " + UndoRequestTypeName);
                return;
            }

            Type closed;
            try { closed = open.MakeGenericType(undo); }
            catch (Exception e)
            {
                MelonLogger.Warning("[req] 闭合 " + HandlerOpenTypeName + " 失败: "
                                    + e.GetType().Name + ": " + e.Message);
                return;
            }

            foreach (var m in closed.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name.IndexOf(MethodNamePart, StringComparison.Ordinal) < 0) continue;
                if (m.GetParameters().Length != 1) continue;

                Targets.Add(m);
            }
        }

        /// <summary>
        /// 返回 true = 照常执行（我们只读不写，永远不改变游戏行为）。
        /// </summary>
        public static void Prefix(object __instance)
        {
            try
            {
                if (!(__instance is Il2CppObjectBase objBase)) return;

                var raw = new Il2CppSystem.Object(objBase.Pointer);
                var type = raw.GetIl2CppType();
                if (type == null) return;

                string typeName = type.FullName ?? type.Name ?? "?";
                if (!IsInteresting(typeName)) return;

                if (!WaitingFields.TryGetValue(typeName, out var field))
                {
                    field = Il2CppReflection.FindIl2CppField(type, WaitingFieldName);
                    WaitingFields[typeName] = field;   // null 也缓存：省得每次都遍历字段
                }
                if (field == null) return;

                var boxed = field.GetValue(raw);
                bool waiting = boxed != null && boxed.Unbox<bool>();
                if (waiting) return;                   // 正常：有人在等，零日志

                if (!Reported.Add(typeName)) return;

                MelonLogger.Error("[req] " + typeName + " 被丢弃 —— 没有任何人在等它，"
                                  + "这次按键不会有任何反应（游戏自己的流程已经停了）。"
                                  + "帧=" + UnityEngine.Time.frameCount);
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>
        /// 这个请求是不是「玩家按了键、游戏该接住」的那一类。
        ///
        /// 认的是**完整类型名里含不含关键词**：il2cpp 给的 FullName 形如
        /// `TanitakaTech.UnityProcessManager.RequestHandler`1[[Project.UndoRequest, DomainAssemblyDefinition, …]]`，
        /// 泛型实参直接写在名字里，所以子串匹配就够，不用去解泛型。
        /// </summary>
        private static bool IsInteresting(string typeName)
        {
            if (ProbeFlags.Has(AllFlag)) return true;

            foreach (var name in InterestingNames)
                if (typeName.IndexOf(name, StringComparison.Ordinal) >= 0) return true;

            return false;
        }
    }
}
