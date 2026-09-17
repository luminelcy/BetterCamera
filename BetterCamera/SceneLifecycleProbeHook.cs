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
    /// 【诊断】问清楚两件事：拍照场景的 installer 挂在哪个 GameObject 上、场景流程是什么时候被启动的。
    ///
    /// 【为什么需要它】2026-09-17 的日志抓到了卡死的真凶：
    ///
    ///     System.OperationCanceledException  ← 被 Forget() 吞掉
    ///       at RoomSnapSceneSequence.StartSequence(CancellationToken)
    ///       at ConcurrentProcess.LoopProcessAsync(CancellationToken)
    ///
    /// 顺着反编译找到了那个 token 的出处 —— `RoomSnapSceneInstaller.Initialize()`
    /// （VA 0x1857580A0，`IInitializable` 的 Slot 9）在最后两步：
    ///
    ///     v16 = this.get_destroyCancellationToken();      // ← MonoBehaviour 的销毁令牌
    ///     this.StartSequence(sequence, v16).Forget();     // ← 整个场景流程
    ///
    /// （`sub_186054D80` → `sub_187084C40` 里能直接读到 Unity 内部符号
    ///  `UnityEngine.MonoBehaviour::OnCancellationTokenCreated_Injected`，所以这个判断是确定的。）
    ///
    /// 也就是说：**StartSequence 抛 OCE = installer 所在的那个 GameObject 被销毁了。**
    /// 而 `Initialize()` 每个场景只跑一次、`StartSequence` 只被调用一次、游戏里没有任何地方
    /// 会重启它 —— 所以一次取消就等于整个拍照场景永久瘫痪（快门和返回键全部静默失效）。
    ///
    /// 【本探针做什么】把这个"谁"打出来：
    ///   1. `RoomSnapSceneInstaller.Initialize()` 的前缀 —— 打它所在 GameObject 的名字。
    ///      这一步是**决定性**的：知道了对象名，就能和本 mod 唯一会销毁对象的地方
    ///      （SliderHandle 那句 `Destroy(original)`，以及几处销毁克隆体子对象）对上号。
    ///   2. `RoomSnapSceneSequence.StartSequence` 的前缀 —— 打帧号，用来把"流程启动"和
    ///      "流程被取消"两个时刻对齐（日志里已经有 OCE 的时刻）。
    ///
    /// 判读：
    ///   * installer 的对象名如果是本 mod 碰过的（P_ZoomHandleObject / 我们的克隆体 / 它们的父级）
    ///     → 我们销毁它 = 直接病因
    ///   * 如果是游戏自己的对象 → 要查是谁销毁的（可能是游戏自身逻辑，我们只是改变了时序）
    /// </summary>
    internal static class SceneLifecycleProbeHook
    {
        private const string HarmonyId = "BetterCamera.SceneLifecycleProbe";

        private const string InstallerTypeName = "RoomSnapSceneInstaller";
        private const string InstallerMethodName = "Initialize";

        private const string SequenceTypeName = "RoomSnapSceneSequence";
        private const string SequenceMethodName = "StartSequence";

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<MethodInfo> InstallerTargets = new List<MethodInfo>();
        private static readonly List<MethodInfo> SequenceTargets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static bool _applied;

        /// <summary>只报第一次 —— installer 的 Initialize 每场景一次，不需要限流，但重复调用本身是线索。</summary>
        private static int _startSequenceCalls;

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断，定位完删】no-lifecycle ⇒ 不挂
            if (ProbeFlags.Has("no-lifecycle"))
            {
                MelonLogger.Msg("[probe] no-lifecycle：本次不挂场景生命周期探针");
                _applied = true;
                return true;
            }

            try
            {
                CollectTargets();

                if (InstallerTargets.Count == 0 && SequenceTargets.Count == 0)
                {
                    MelonLogger.Warning("[lifecycle] 找不到 RoomSnapSceneInstaller.Initialize / "
                                        + "RoomSnapSceneSequence.StartSequence，场景生命周期探针不生效");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var m in InstallerTargets)
                    patched += Patch(m, nameof(InstallerPrefix));
                foreach (var m in SequenceTargets)
                    patched += Patch(m, nameof(StartSequencePrefix));

                _applied = true;
                _startSequenceCalls = 0;

                MelonLogger.Msg("[lifecycle] 场景生命周期探针已挂 " + patched + " 个："
                                + "installer×" + InstallerTargets.Count
                                + " StartSequence×" + SequenceTargets.Count);
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[lifecycle] 挂不上: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static int Patch(MethodInfo method, string prefixName)
        {
            string key = Il2CppReflection.MethodKey(method);
            if (!Patched.Add(key)) return 0;

            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(SceneLifecycleProbeHook), prefixName));
            return 1;
        }

        /// <summary>
        /// 扫遍所有 Il2Cpp* 程序集，按**类型名 + 方法名**收目标。
        ///
        /// 不写死命名空间、也不用 typeof：同名类型可能存在于多份代理程序集里，
        /// 编译期只能拿到其中一份（本项目栽过多次）。
        /// GetTypes() 在大程序集上会抛 ReflectionTypeLoadException，必须从异常里取已加载的部分。
        /// </summary>
        private static void CollectTargets()
        {
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
                    if (type == null) continue;

                    if (type.Name == InstallerTypeName)
                        Add(InstallerTargets, type, InstallerMethodName, 0);
                    else if (type.Name == SequenceTypeName)
                        Add(SequenceTargets, type, SequenceMethodName, 1);
                }
            }
        }

        /// <summary>
        /// 按**名字片段** + 参数个数收方法。
        ///
        /// 必须用片段：`Initialize` 在这里是**显式接口实现**，Il2CppInterop 会把它改名成
        /// 类似 `Zenject_IInitializable_Initialize` 的样子（2026-09-17 实测：按全名
        /// "Initialize" 匹配直接是 0 个 —— 探针静默失效了一轮）。
        /// </summary>
        private static void Add(List<MethodInfo> list, Type type, string namePart, int argCount)
        {
            MethodInfo[] methods;
            try { methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
            catch { return; }

            foreach (var m in methods)
            {
                if (m.Name.IndexOf(namePart, StringComparison.Ordinal) < 0) continue;
                if (m.GetParameters().Length != argCount) continue;
                list.Add(m);
            }
        }

        /// <summary>
        /// `RoomSnapSceneInstaller.Initialize()` —— 打出这个 installer 挂在哪个对象上。
        ///
        /// 这是整个诊断里信息量最大的一行：有了对象名，就能判断它是不是被本 mod 销毁的。
        /// </summary>
        public static void InstallerPrefix(object __instance)
        {
            try
            {
                if (__instance is Il2CppObjectBase objBase) _installerPtr = objBase.Pointer;

                var name = Describe(__instance);
                MelonLogger.Msg("[lifecycle] RoomSnapSceneInstaller.Initialize —— 所在对象 \""
                                + name + "\"  帧=" + UnityEngine.Time.frameCount);
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>installer 的原生指针。`InstallerPrefix` 存下来，`StartSequencePrefix` 用。</summary>
        private static IntPtr _installerPtr;

        /// <summary>
        /// 直接从**原生内存**按偏移读 `MonoBehaviour.m_CancellationTokenSource`。
        ///
        /// ⚠️ **为什么不走 il2cpp 字段反射**：本项目引用的是 stub 版 UnityEngine，
        /// `Il2CppReflection.FindIl2CppField` 拿到的字段偏移可能是 0 或错的 ——
        /// 那会读到一个看起来"正常"的垃圾值，**比读不到更糟**：2026-09-17 就被它骗过一次
        ///（读出来"令牌源=正常"，而同一次 Initialize 里 StartSequence 收到的令牌明明是
        /// 「★已取消★」，两者不可能同时为真）。
        ///
        /// 偏移 0x18 来自 dump.cs 的字段表（`UnityEngine.MonoBehaviour`：
        /// `m_CachedPtr` @0x10、`m_CancellationTokenSource` @0x18），
        /// 直接按字长读指针，没有解释空间。
        ///
        /// 判读：
        ///   未创建(null) —— `destroyCancellationToken` 的 getter 是"为 null 才新建"，
        ///                  所以此刻令牌**不可能**已经取消 ⇒ 说明取消源另有出处
        ///   已创建       —— 说明在我们之前就有人访问过这个令牌源，
        ///                  那"它被取消"就发生在 getter 创建之后、StartSequence 取用之前
        /// </summary>
        private static string ReadTokenSourceField()
        {
            if (_installerPtr == IntPtr.Zero) return "(没有 installer 指针)";

            try
            {
                IntPtr cts = Marshal.ReadIntPtr(_installerPtr, 0x18);
                return cts == IntPtr.Zero
                    ? "未创建(null)"
                    : "已创建(0x" + cts.ToInt64().ToString("X") + ")";
            }
            catch (Exception e)
            {
                return "(读失败 " + e.GetType().Name + ")";
            }
        }

        /// <summary>
        /// （已删除）原来的 `DescribeTokenSource` 走的是 il2cpp 字段反射读
        /// `m_CancellationTokenSource`。它给出的读数是**假的** —— 见 ReadTokenSourceField
        /// 的说明：本项目引用 stub 版 UnityEngine，反射拿到的偏移不可靠，
        /// 会返回一个看起来"正常"的垃圾值，比读不到更糟。改用原生偏移直读。
        /// </summary>

        /// <summary>
        /// `RoomSnapSceneSequence.StartSequence(CancellationToken)` —— 场景流程的启动点。
        ///
        /// 记帧号是为了和日志里的 OCE 时刻对齐。**调用次数本身就是线索**：
        /// 正常情况下每个场景一次；如果看到多次，说明有地方在重启流程。
        /// </summary>
        public static void StartSequencePrefix(object[] __args)
        {
            try
            {
                _startSequenceCalls++;

                // 令牌状态是这一行里最重要的信息：**如果进来时就已经取消**，那场景流程
                // 一启动就死，后面什么都不用查了 —— 病因在上游（installer 的
                // destroyCancellationToken 在调用前就被烧掉了）。
                string token = __args != null && __args.Length > 0
                    ? DescribeToken(__args[0])
                    : "(拿不到参数)";

                // installer 的指针要打出来：`[cancel]` 探针记录的是"谁被 RaiseCancellation 了"，
                // 两边对比指针就能判断"这个 installer 的令牌是不是被那条路径取消的"。
                MelonLogger.Msg("[lifecycle] StartSequence 第 " + _startSequenceCalls
                                + " 次被调用  帧=" + UnityEngine.Time.frameCount
                                + "  令牌=" + token
                                + "  installer=0x"
                                + (_installerPtr == IntPtr.Zero ? "?" : _installerPtr.ToInt64().ToString("X"))
                                + "  令牌源=" + ReadTokenSourceField());
            }
            catch
            {
                // 诊断代码不许把游戏搞坏
            }
        }

        /// <summary>
        /// 读装箱的 `CancellationToken.IsCancellationRequested`。
        ///
        /// 参数是值类型，il2cpp 会装箱；用真实类型找 getter 再调，别去猜字段布局。
        /// </summary>
        private static string DescribeToken(object arg)
        {
            if (!(arg is Il2CppObjectBase objBase)) return "(不是 il2cpp 对象)";

            try
            {
                var raw = new Il2CppSystem.Object(objBase.Pointer);
                var getter = Il2CppReflection.FindIl2CppMethod(
                    raw.GetIl2CppType(), "get_IsCancellationRequested");
                if (getter == null) return "(读不到令牌状态)";

                var v = getter.Invoke(raw, System.Array.Empty<Il2CppSystem.Object>());
                return v != null && v.Unbox<bool>() ? "★已取消★" : "正常";
            }
            catch (Exception e)
            {
                return "(读令牌失败: " + e.GetType().Name + ")";
            }
        }

        /// <summary>
        /// 读对象名。代理对象的 .NET 类型只是声明类型，所以要绕到原生指针上重建一个
        /// 带类型的包装（和 ZenjectInstallTraceHook 同一套路）。
        /// </summary>
        private static string Describe(object instance)
        {
            if (instance == null) return "(null)";

            if (!(instance is Il2CppObjectBase objBase)) return instance.GetType().Name;

            var raw = new Il2CppSystem.Object(objBase.Pointer);
            return Il2CppReflection.GetObjectName(raw) ?? "(读不到名字)";
        }
    }
}
