using System;
using System.Reflection;
using HarmonyLib;
using Il2CppCommon.Prefabs.CommonSwitchButton;
using MelonLoader;

namespace BetterCamera
{
    /// <summary>
    /// 挡掉「第一次切自定义比例时菜单闪一下竖屏 9:16」。
    ///
    /// 机制（反汇编 + 实机日志确认）：
    ///
    ///   RequestNativePortrait()  把 CurrentCaptureSize 推成 Portrait
    ///     └─ 游戏按「key == 当前值」重刷开关视觉 → UpdateIsOn(Portrait, true)   ← 点亮
    ///   RefreshToggleVisuals()   紧接着 → UpdateIsOn(Portrait, false)            ← 按灭
    ///
    /// 两次写入在**同一帧**，但点亮那一下启动了 _onOnTimeline，那一帧渲染出来就是
    /// Portrait 亮着的样子 —— 肉眼就是"跳一帧 9:16"。
    ///
    /// 为什么不能靠"按灭得快"：同一帧内动画已经起了，按灭只是紧接着又起一段 OFF 动画，
    /// 那一帧的画面还是 ON 的。所以必须从源头挡 —— 让那一次 UpdateIsOn **整个不执行**。
    ///
    /// 挡掉之后：CurrentCaptureSize 照样变成 Portrait（出片分支照旧正确，不会退回卡死那条路），
    /// 被挡掉的只是菜单上那一下视觉。而我们的 RefreshToggleVisuals 本来就要把 Portrait
    /// 按灭、把选中的预设点亮，所以最终画面是对的。
    ///
    /// 只在**本 mod 主动推状态**的那一瞬间挡一次（Arm/Disarm 夹住 RequestNativePortrait），
    /// 玩家自己去点原生 Portrait 选项时完全不受影响。
    /// </summary>
    internal static class PortraitToggleSuppressHook
    {
        private const string HarmonyId = "BetterCamera.PortraitToggleSuppress";

        private const string MethodName = "UpdateIsOn";

        /// <summary>竖屏那个原生选项的对象名，和 GamePaths.CaptureSizeOptionTemplate 的最后一段一致。</summary>
        private const string PortraitOptionName = "CaptureSizeOption_Portrait";

        private static HarmonyLib.Harmony _harmony;
        private static MethodInfo _target;

        public static bool Applied { get; private set; }

        /// <summary>
        /// 待命标志。Arm 之后、下一次对 Portrait 的“点亮”会被吃掉一次。
        /// 用 Arm/Disarm 夹住调用而不是让它长期挂着 —— 万一游戏那条刷新没发生，
        /// 长期挂着会误吃掉之后某次合法的点亮。
        /// </summary>
        private static bool _armed;

        public static bool Apply()
        {
            if (Applied) return true;

            try
            {
                _target = typeof(CommonSwitchButtonBehaviour).GetMethod(
                    MethodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool) },
                    null);

                if (_target == null)
                {
                    MelonLogger.Error("[BetterCamera] 找不到 CommonSwitchButtonBehaviour.UpdateIsOn，"
                                      + "第一次切比例时菜单仍会闪一下 9:16");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);
                _harmony.Patch(_target, prefix: new HarmonyMethod(typeof(PortraitToggleSuppressHook), nameof(Prefix)));
                Applied = true;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 挡跳变的补丁失败: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        public static void Remove()
        {
            if (!Applied || _target == null) return;

            try
            {
                _harmony?.Unpatch(_target, HarmonyPatchType.All, HarmonyId);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 挡跳变的补丁撤销失败: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Applied = false;
                _target = null;
                _armed = false;
            }
        }

        /// <summary>推状态之前叫一下，让紧接着那次"点亮 Portrait"被吃掉。</summary>
        public static void Arm() => _armed = true;

        /// <summary>推完立刻叫一下，别让它挂到别的地方去。</summary>
        public static void Disarm() => _armed = false;

        /// <summary>
        /// 返回 false = 跳过原方法，即这次点亮不发生。
        ///
        /// 参数名 isOn 必须和游戏侧一致（Harmony 按名字对参数）。
        /// </summary>
        public static bool Prefix(CommonSwitchButtonBehaviour __instance, bool isOn)
        {
            if (!_armed) return true;
            if (!isOn) return true;                    // 只挡"点亮"，按灭照常
            if (__instance == null) return true;

            // 只挡竖屏那一个 —— 同一次刷新里 Default / HoloModelink 也会被写成 false，
            // 那些是我们要的，不动。
            if (!IsPortraitOption(__instance)) return true;

            _armed = false;
            return false;
        }

        private static bool IsPortraitOption(CommonSwitchButtonBehaviour toggle)
        {
            var parent = toggle.transform.parent;
            return parent != null && parent.gameObject.name == PortraitOptionName;
        }
    }
}
