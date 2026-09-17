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
    /// 挡掉「克隆体上的按钮被点击时抛空引用」。和 MonoKernelGuardHook 是同一个毛病的两处发作点：
    /// 那个是克隆体上的 Zenject kernel 没被注入，这个是克隆体上的按钮**音效播放器**没被注入。
    ///
    /// 【现象】点一次本 mod 的按钮就是一条（时间上 1:1，比例预设最明显 —— 它每点一次都会
    /// 在 ApplyCropArea 里打一行 [tween]，两条日志紧挨着）：
    ///     NullReferenceException
    ///       at Common.Prefabs.CommonButton.CommonButtonBehaviour.PlaySoundAsync (CancellationToken)
    ///       at CommonButtonBehaviour.<InitBehaviour>b__5_0 (R3.Unit, CancellationToken)
    ///
    /// 【原因】反汇编确认（CommonButtonBehaviour 的字段：_button@0x20、_clickSound@0x28、
    /// _soundEffectPlayer@0x30），PlaySoundAsync 的状态机里两条路完全不对称：
    ///   - _clickSound（[SerializeField]，Instantiate 会连它一起复制）**有空判断**：
    ///     为空就直接 SetResult 返回 —— 游戏自己就给"这个按钮没音效"留了路。
    ///   - _soundEffectPlayer（[Inject]，由克隆体上那个 GameObjectContext 注入）**没有判断**，
    ///     拿到就往接口上打 PlayAsync。克隆体的上下文永远不装配（见 MonoKernelGuardHook），
    ///     它恒为 null ⇒ 空引用就抛在这一步，PlayAsync 根本来不及执行。
    ///   也就是说：这个音效**从来没响过**，日志里的栈是它唯一的产物。
    ///
    /// 【做法】InitBehaviour 的前缀里判断这个按钮有没有被注入过；没有就不挂那条订阅。
    /// 拆开看 InitBehaviour 只做一件事 —— 把「点击 → PlaySoundAsync」这条链挂到 _button 上
    /// （取 _button、造 observer、SubscribeAwait、丢弃返回的 IDisposable），
    /// 按钮的视觉、交互、我们追加的点击监听都不经过它。跳过它 = 去掉一个响不了的音效订阅，
    /// 玩家可见行为不变，日志里少一堆栈。
    ///
    /// 【为什么不把 _soundEffectPlayer 从原生模板抄进克隆体】那样按钮能真的出声，但要把一个
    /// 接口类型的引用写进 il2cpp 对象的字段里（本项目在这类写入上踩过静默失效），赌注只是
    /// 一个点击音效 —— 不值得。真想要声音，改的是这里，不是别处。
    ///
    /// 【为什么是一处补丁而不是在每个 Instantiate 点写字段】InitBehaviour 是**游戏自己**在
    /// 菜单刷新时对我们的克隆体调的，时机和对象都不由我们决定；而且克隆点有十来个
    /// （以后还会加）。一处补丁覆盖全部，含以后新增的克隆点。判据只有「有没有被注入过」一条，
    /// 注入过的按钮一律照常执行，不存在把正常音效改坏的可能。
    /// </summary>
    internal static class ClickSoundGuardHook
    {
        private const string HarmonyId = "BetterCamera.ClickSoundGuard";

        /// <summary>目标类型/方法按**名字**找 —— 同名类型可能在多个 Il2Cpp* 程序集里都有，见 CollectTargets。</summary>
        private const string BehaviourTypeName = "CommonButtonBehaviour";
        private const string InitMethodName = "InitBehaviour";

        /// <summary>那个 [Inject] 的音效播放器字段。它是 null 就说明这个按钮没被注入过。</summary>
        private const string PlayerFieldName = "_soundEffectPlayer";

        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static readonly HashSet<string> Patched = new HashSet<string>();
        private static HarmonyLib.Harmony _harmony;
        private static bool _applied;

        private static Il2CppSystem.Reflection.FieldInfo _playerField;

        /// <summary>挡下来的按钮只报第一个 —— InitBehaviour 会随每个按钮被调用。</summary>
        private static bool _reported;

        /// <summary>读字段失败这类站点，一个只报一次。</summary>
        private static readonly HashSet<string> ReportedSites = new HashSet<string>();

        public static bool Apply()
        {
            if (_applied) return true;

            try
            {
                CollectTargets();
                if (Targets.Count == 0)
                {
                    MelonLogger.Warning("[BetterCamera] 找不到 CommonButtonBehaviour.InitBehaviour，克隆体按钮的空引用挡不住");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                foreach (var method in Targets)
                {
                    var behaviourType = method.DeclaringType;
                    if (behaviourType == null) continue;

                    // 字段在每个候补上各解析一次：同名类在别的程序集里可能也有，字段得从
                    // **实际被打补丁的那个类**上取。任取一份即可 —— 同名同定义的类字段偏移相同，
                    // 而 il2cpp 读字段只按偏移走。
                    var field = Il2CppReflection.FindIl2CppField(Il2CppType.From(behaviourType), PlayerFieldName);
                    if (field == null) continue;

                    string key = Il2CppReflection.MethodKey(method);
                    if (!Patched.Add(key)) continue;

                    _playerField ??= field;

                    _harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(ClickSoundGuardHook), nameof(Prefix)));
                    patched++;
                }

                // 拿不到字段就判断不了「有没有被注入」，补丁挂了也没用 —— 直接不挂，
                // 让行为退回改动之前（有 NRE 但功能正常），比挂一个永远 return true 的假补丁诚实
                if (patched == 0 || _playerField == null)
                {
                    MelonLogger.Warning("[BetterCamera] CommonButtonBehaviour 上拿不到 " + PlayerFieldName
                                        + "，克隆体按钮的空引用挡不住（只影响日志，不影响功能）");
                    return false;
                }

                _applied = true;
                return patched > 0;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 点击音效守卫挂不上: "
                                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 扫遍所有 Il2Cpp* 程序集，收所有叫 CommonButtonBehaviour 的类型的无参 InitBehaviour。
        ///
        /// **不用 typeof 取目标** —— 同名类型在多个程序集里都有（Assembly-CSharp /
        /// Il2CppBehaviourAssemblyDefinition / Il2CppViewAssemblyDefinition），编译期只能拿到
        /// 其中一个，补丁就打在了没人经过的那份上：绑上了，但一次不触发。
        /// 和 MonoKernelGuardHook 出自同一处教训。
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
                    if (type == null || type.Name != BehaviourTypeName) continue;

                    MethodInfo init;
                    try { init = type.GetMethod(InitMethodName, BF, null, Type.EmptyTypes, null); }
                    catch { continue; }

                    if (init != null) Targets.Add(init);
                }
            }
        }

        /// <summary>
        /// __instance 收成 object 而不是 MonoBehaviour：本项目引用的是 stub 版 UnityEngine
        /// （UnityDependencies 下的），那个 MonoBehaviour 不是 il2cpp 代理，和代理类型的继承
        /// 关系也对不上（Harmony 会拒绝绑定）。object 对任何托管对象都成立。
        ///
        /// 返回 false = 不执行原方法 = 那条「点击 → 音效」的订阅不挂。
        /// </summary>
        public static bool Prefix(object __instance)
        {
            var objBase = __instance as Il2CppObjectBase;
            if (objBase == null) return true;   // 认不出来就别插手，走原逻辑

            bool injected;
            string name = null;
            try
            {
                var raw = new Il2CppSystem.Object(objBase.Pointer);
                // 非 null ⇒ 这个按钮被注入过（原生按钮）⇒ 一切照常
                injected = _playerField.GetValue(raw) != null;

                if (!injected && !_reported) name = Il2CppReflection.GetObjectName(raw);
            }
            catch (Exception e)
            {
                WarnOnce("read", "读按钮的音效播放器字段失败，这次照常执行原方法", e);
                return true;
            }

            if (injected) return true;

            if (!_reported)
            {
                _reported = true;
                MelonLogger.Msg("[BetterCamera] 按钮没有音效播放器（克隆体注入不到），已跳过它的点击音效订阅: "
                                + (name ?? "(读不到名字)"));
            }

            return false;
        }

        /// <summary>同一个站点只报一次 —— InitBehaviour 会随每个按钮被调用。</summary>
        private static void WarnOnce(string site, string message, Exception e)
        {
            if (!ReportedSites.Add(site)) return;
            MelonLogger.Warning("[BetterCamera] " + message + ": " + e.GetType().Name + ": " + e.Message);
        }
    }
}
