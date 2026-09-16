using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppProject;
using Il2CppProject.HomeScene.RoomScene;
using Il2CppTanitakaTech.StateVariable;

namespace BetterCamera
{
    /// <summary>
    /// 摘掉原生的 FOV 钳制。
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
    /// </summary>
    public static class FovClampHook
    {
        private const string HarmonyId = "BetterCamera.FovClamp";

        private static HarmonyLib.Harmony _harmony;
        private static MethodInfo _target;
        private static bool _applied;

        public static bool IsApplied => _applied;

        public static bool Apply(MelonLogger.Instance logger)
        {
            if (_applied) return true;

            try
            {
                // 直接按名字特征在托管代理里找，不依赖 RoomCameraController 实例 ——
                // 它的 _roomCameraFOVSetter 要等 Zenject 注入（实测 0.5 秒），
                // 而补丁应该在场景初始化时就打上。
                _target = FindManagedMethod(logger);
                if (_target == null)
                {
                    logger.Error("[hook] 托管侧找不到钳制方法，无法 patch");
                    return false;
                }

                logger.Msg("[hook] 目标 = " + _target.DeclaringType?.FullName + "." + _target.Name);

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);
                _harmony.Patch(_target, prefix: new HarmonyMethod(typeof(FovClampHook), nameof(Prefix)));
                _applied = true;
                logger.Msg("[hook] 补丁已打上（FOV 上下界 40/80 已解除）");
                return true;
            }
            catch (Exception e)
            {
                logger.Error("[hook] Apply 失败: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        public static void Remove(MelonLogger.Instance logger)
        {
            if (!_applied || _target == null) return;
            try
            {
                _harmony?.Unpatch(_target, HarmonyPatchType.All, HarmonyId);
                logger.Msg("[hook] OK 补丁已撤销");
            }
            catch (Exception e)
            {
                logger.Error("[hook] Unpatch 失败: " + e.GetType().Name + ": " + e.Message);
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
        /// 注意不要简单地「去掉钳制」—— 实测那样做时滚轮路径会把 FOV 一路推到 128 以上，
        /// 再滚就更大，投影会坏掉。原生这段代码本来就是在保护相机。
        /// 正确做法是把上下界换成 mod 的扩宽范围，其余语义（&lt;=0 兜底）保持不变。
        ///
        /// 返回 false = 跳过原方法。
        /// </summary>
        public static bool Prefix(ref RoomCameraFOV s, ref RoomCameraFOV __result)
        {
            float v = s.Value;
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
        private static MethodInfo FindManagedMethod(MelonLogger.Instance logger)
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
                catch (Exception e) { logger.Msg("[hook]   " + an + " GetTypes 失败: " + e.GetType().Name); continue; }
                if (types == null) continue;

                int ok = 0, total = 0;
                foreach (var t in types)
                {
                    if (t == null) continue;
                    total++;
                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BF); } catch { continue; }
                    ok++;

                    foreach (var m in ms)
                    {
                        if (m.Name.IndexOf("CreateVariable", StringComparison.Ordinal) < 0) continue;
                        if (!m.ReturnType.Name.Contains("RoomCameraFOV")) continue;
                        logger.Msg("[hook]   命中 " + t.FullName + "." + m.Name);
                        return m;
                    }
                }
                logger.Msg("[hook]   " + an + ": 可用类型 " + ok + "/" + total);
            }
            return null;
        }
    }
}
