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
    /// 挡掉「克隆体上那个从没被注入过的 Zenject MonoKernel 在 Start 里抛空引用」。
    ///
    /// 【现象】每次进拍照场景固定 6 条
    ///     NullReferenceException
    ///       at Zenject.MonoKernel.Start ()
    /// 全部来自 SliderHandle 克隆出来的对象：
    ///     P_BetterCameraHandleObject0/1/2      ← 克隆 P_ZoomHandleObject
    ///     P_BetterCameraSwitchModeButton       ← 克隆 P_SwitchCameraFocusModeButtonObject
    ///     P_SwitchCameraFocusModeButtonObject1
    ///     P_BetterCameraShowRoomStates         ← 克隆 P_ShowRoomStatesButtonObject
    /// 同一个方法里克隆的 CommonButton（那是 P_*Object 的**子节点**）不报 —— 这就是线索。
    ///
    /// 【原因】游戏里每个 P_*Object 顶层 UI 都自带一个 Zenject GameObjectContext。
    /// 场景加载装配时，GameObjectContext.InstallBindings 会
    ///     Bind&lt;MonoKernel&gt;().To&lt;DefaultGameObjectKernel&gt;().FromNewComponentOn(gameObject)
    /// 把 kernel **运行时添加**到它自己那个 GameObject 上。我们 Instantiate 这种对象时，
    /// Unity 连这个 kernel 一起复制，可克隆体的 GameObjectContext 永远不会重新装配：
    /// 它的入口是 [Inject] Construct(DiContainer)，而 Zenject 只在场景加载时注入场景对象，
    /// 之后新建的对象没人管。于是克隆体上 kernel 那三个 [InjectLocal] 管理器全是 null，
    /// 而 MonoKernel.Start() 无条件走到 _initializableManager.Initialize() → 空引用。
    /// 克隆子节点（CommonButton / 滤镜菜单里那些布局和按钮）就没有 kernel，所以不报。
    ///
    /// 【为什么跳过是对的，而不是把问题扫到地毯下】
    ///   - 没被注入 = 这个 kernel 背后没有任何容器，没有任何 IInitializable / ITickable
    ///     需要它驱动，Initialize() 本来无事可做。Zenject 自己也承认这种状态：MonoKernel
    ///     的 Update / FixedUpdate / LateUpdate / OnDestroy 全都对管理器做了 null 判断，
    ///     OnDestroy 那里还写着 "_disposablesManager can be null if we get destroyed
    ///     before the Start event"。
    ///   - 换句话说这条 NRE 除了往日志里丢一堆栈，本来也弄不坏什么；修它主要是清噪音，
    ///     顺带让"场景初始化时的异常"重新变成一个值得看的信号。
    ///   - 判定条件只有「没被注入」一条：注入过的 kernel 一律照常执行，
    ///     不存在把正常流程改坏的可能。
    ///
    /// 【为什么不直接删掉克隆体上那三个 Zenject 组件】
    ///   那要改 6 个 Instantiate 点，以后每加一处克隆都得记得改一遍；这里一处覆盖全部。
    ///   （另外日志里从来没出现过 ColorAdjustSliders 那两条 Warning，说明克隆的标签
    ///   Installer 的 _presenter 一直是 null —— 克隆体上的 Zenject 组件确实是死的。
    ///   死的东西不碍事，没必要为了 NRE 去动 GameObject 结构。）
    ///
    /// 【作用域】只在拍照场景挂一次，之后不再摘 —— 它不影响任何被正常注入的 kernel，
    /// 留着比摘掉安全。
    /// </summary>
    internal static class MonoKernelGuardHook
    {
        private const string HarmonyId = "BetterCamera.MonoKernelGuard";

        /// <summary>MonoKernel 上那个 [InjectLocal] 管理器字段。它是 null 就说明这个 kernel 没被注入过。</summary>
        private const string ManagerFieldName = "_initializableManager";

        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static HarmonyLib.Harmony _harmony;
        private static bool _applied;

        /// <summary>读字段失败的站点，一个只报一次（这个函数会随每个 kernel 的 Start 被调用）。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        private static Il2CppSystem.Reflection.FieldInfo _managerField;

        public static bool Apply()
        {
            if (_applied) return true;

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[BetterCamera] 找不到 Zenject MonoKernel，克隆体的空引用挡不住");
                    return false;
                }

                // 拿不到字段就判断不了「有没有被注入」，补丁挂上去也没用 —— 直接不挂，
                // 让行为退回改动之前（有 NRE 但功能正常），比挂一个永远 return true 的假补丁诚实
                var kernelType = Targets[0].DeclaringType;
                _managerField = kernelType == null
                    ? null
                    : Il2CppReflection.FindIl2CppField(Il2CppType.From(kernelType), ManagerFieldName);
                if (_managerField == null)
                {
                    MelonLogger.Warning("[BetterCamera] MonoKernel 上找不到 " + ManagerFieldName
                                        + "，克隆体的空引用挡不住（只影响日志，不影响功能）");
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
                        prefix: new HarmonyMethod(typeof(MonoKernelGuardHook), nameof(Prefix)));
                    patched++;
                }

                _applied = true;
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] MonoKernel 守卫挂不上: "
                                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫遍所有 Il2Cpp* 程序集，收所有叫 MonoKernel 的类型的无参 Start。
        ///
        /// **不用 typeof 取目标** —— 同名类型在多个程序集里都有（Assembly-CSharp /
        /// Il2CppBehaviourAssemblyDefinition / Il2CppViewAssemblyDefinition），编译期只能拿到
        /// 其中一个，补丁就打在了没人经过的那份上：绑上了，但一次不触发。上一个诊断就栽在这里。
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
        /// （UnityDependencies 下的），那个 MonoBehaviour 不是 il2cpp 代理，和代理类型的继承
        /// 关系也对不上（Harmony 会拒绝绑定）。object 对任何托管对象都成立。
        ///
        /// 返回 false = 不执行原方法。
        /// </summary>
        public static bool Prefix(object __instance)
        {
            var objBase = __instance as Il2CppObjectBase;
            if (objBase == null) return true;   // 认不出来就别插手，走原逻辑

            bool injected;
            try
            {
                var raw = new Il2CppSystem.Object(objBase.Pointer);
                // 非 null ⇒ 这个 kernel 被某个容器注入过 ⇒ 一切照常
                injected = _managerField.GetValue(raw) != null;
            }
            catch (Exception e)
            {
                WarnOnce("read", "读 MonoKernel 管理器字段失败，这次照常执行原方法", e);
                return true;
            }

            return injected;   // 注入过的照常执行；没注入的跳过（原方法必抛空引用）
        }

        /// <summary>同一个站点只报一次 —— 这个函数会随每个 kernel 的 Start 被调用。</summary>
        private static void WarnOnce(string site, string message, Exception e)
        {
            if (!Reported.Add(site)) return;
            MelonLogger.Warning("[BetterCamera] " + message + ": " + e.GetType().Name + ": " + e.Message);
        }
    }
}
