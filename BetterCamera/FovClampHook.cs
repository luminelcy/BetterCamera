using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppProject;

namespace BetterCamera
{
    /// <summary>
    /// 把原生 FOV 钳制的上下界从 40/80 换成 mod 的 20/120。
    ///
    /// 钳制的真身（反汇编确认，VA 0x1812EBAF0）：
    ///     RoomCameraFOV.&lt;&gt;c.&lt;CreateVariable&gt;b__8_0  —  编译器生成的 lambda
    ///     if (v &lt;= 0) v = 60;      // DEFAULT_FOV
    ///     if (v &lt;  40) return 40;  // MIN_FOV
    ///     if (v &gt;  80) return 80;  // MAX_FOV
    ///     return v;
    ///
    /// 这个 lambda 被 RoomCameraFOV.CreateVariable() 当作 HalfDependencyVariable 的
    /// ResultSelector 传进去，所以 Set() 存的是原始值、而 Observe() 给出的是夹过的值。
    /// RoomCameraController.UpdateFOV 本身不夹（反汇编确认），夹取完全发生在这里。
    ///
    /// 为什么用 Harmony 而不是替换 ResultSelector 字段：
    ///   Harmony 改的是函数入口的原生跳转，所以那个早在静态初始化时就建好、
    ///   并被 ResultObservable 闭包捕获的委托，依然会走到 hook 上。
    ///
    /// 只在 S_RoomSnapScene 里打补丁，离开场景还原，把影响面限制到最小。
    ///
    /// 正常路径不打任何日志 —— 这是给最终用户用的 mod，成功是默认状态，不需要播报。
    /// </summary>
    public static class FovClampHook
    {
        private const string HarmonyId = "BetterCamera.FovClamp";

        private static HarmonyLib.Harmony _harmony;
        private static MethodInfo _target;
        private static bool _applied;

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断，定位完删】no-fov ⇒ 不挂。
            //
            // 为什么它是现在最可疑的一个：
            //   · 它是本 mod 唯一一个**两个 ref 结构体参数**的补丁
            //     （`ref RoomCameraFOV s, ref RoomCameraFOV __result`），
            //     Harmony 对 il2cpp 值类型 ref 的编组只要差一点，踩的就是调用者的栈。
            //   · 它挂在 RoomCameraFOV 变量的 ResultSelector 上，**每次读写 FOV 都会跑**
            //    （滚轮 / 滑条 / 场景每帧的同步都过这条路径）—— 是全项目最热的补丁。
            //   · 用户 2026-09-17 贴的崩溃是
            //     `AccessViolationException: Attempted to read or write protected memory.
            //      This is often an indication that **other memory is corrupt**`
            //     —— 典型的"被别人踩坏"，而且**有概率**（时好时坏）。
            //   · 它此前**没有开关**，是唯一从未被单独排除过的补丁。
            if (ProbeFlags.Has("no-fov"))
            {
//                 MelonLogger.Msg("[probe] no-fov：不挂 FOV 钳制补丁（对照实验）");
                _applied = true;
                return true;
            }

            try
            {
                // 直接按名字特征在托管代理里找，不依赖 RoomCameraController 实例 ——
                // 它的 _roomCameraFOVSetter 要等 Zenject 注入（实测 0.5 秒），
                // 而补丁应该在场景初始化时就打上。
                _target = FindManagedMethod();
                if (_target == null)
                {
                    MelonLogger.Error("[BetterCamera] 找不到 FOV 钳制方法，扩宽视野范围将不生效（其余功能不受影响）");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);
                _harmony.Patch(_target, prefix: new HarmonyMethod(typeof(FovClampHook), nameof(Prefix)));
                _applied = true;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] FOV 钳制补丁失败: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        public static void Remove()
        {
            if (!_applied || _target == null) return;
            try
            {
                _harmony?.Unpatch(_target, HarmonyPatchType.All, HarmonyId);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] FOV 钳制补丁撤销失败: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _applied = false;
                _target = null;
            }
        }

        // mod 的扩宽范围，替换原生的 [40, 80]
        private const float MinFov = 20f;
        private const float MaxFov = 120f;

        /// <summary>
        /// 替换原生的钳制实现。
        ///
        /// 注意这不是简单地「去掉钳制」—— 实测完全取消边界后滚轮路径会把 FOV 一路推到
        /// 128 以上，再滚还会更大，投影会坏掉。原生这段代码本来就是在保护相机。
        /// 正确做法是把上下界换成 mod 的扩宽范围，其余语义（&lt;=0 兜底）保持不变。
        ///
        /// 返回 false = 跳过原方法。
        /// </summary>
        public static bool Prefix(ref RoomCameraFOV __0, ref RoomCameraFOV __result)
        {
            // ⚠️ 参数用位置名 `__0`：Harmony 按参数**名**配对，而 il2cpp 方法在原生侧
            // 没有参数名，名字对不上就可能按错布局取值/回写、踩调用者的栈。
            // 2026-09-18 靠这条线索定位到 CaptureSizeRatioHook.AspectRatioPrefix，
            // 全项目所有带参数的补丁统一改用位置名。
            float v = __0.Value;
            if (v <= 0f) v = 60f;                    // 原生兜底：FOV=0 会让投影退化
            if (v < MinFov) v = MinFov;
            if (v > MaxFov) v = MaxFov;
            __result = new RoomCameraFOV(v);
            return false;
        }

        /// <summary>
        /// 在 Il2CppInterop 生成的托管代理里找钳制 lambda。
        ///
        /// 特征：名字含 CreateVariable 的编译器生成方法，返回 RoomCameraFOV。
        /// 注意 GetTypes() 在大程序集上会抛 ReflectionTypeLoadException（部分类型依赖
        /// 缺失），必须从异常里取已加载的那部分，否则恰好会跳过目标所在的大程序集。
        /// </summary>
        private static MethodInfo FindManagedMethod()
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic |
                                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name ?? "";
                if (!an.StartsWith("Il2Cpp")) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null) continue;

                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BF); } catch { continue; }

                    foreach (var m in ms)
                    {
                        if (m.Name.IndexOf("CreateVariable", StringComparison.Ordinal) < 0) continue;
                        if (!m.ReturnType.Name.Contains("RoomCameraFOV")) continue;
                        return m;
                    }
                }
            }
            return null;
        }
    }
}
