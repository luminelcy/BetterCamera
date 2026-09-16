using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;
using Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject;

namespace BetterCamera
{
    /// <summary>
    /// 让本 mod 的 ColorAdjust 页表现得像原生标签页。
    ///
    /// 原生链路（反汇编确认，VA 0x1874C39E0）：
    ///     点标签 → CurrentFilterMenuType 变化
    ///       → FilterMenuObjectView.&lt;InitView&gt;b__5_1
    ///         → SetFilterMenuObjectVisible(shownFilterMenuType)
    ///             foreach (kv in _filterMenuObjects)        // FilterMenuType → CanvasGroup
    ///                 kv.Value.alpha / interactable / SetActive = (kv.Key == shownFilterMenuType)
    ///
    /// 之前的做法是把「3 → 本 mod 面板」也塞进那个字典，让游戏自己切。那条路走不通：
    /// 字典的键类型是枚举 FilterMenuType，而从反射拿到的泛型 Add 拿到的是装箱的 Int32，
    /// 两者不是同一个类型。写进去之后字典就带着一个坏条目，上面那个 foreach 遍历到它
    /// 中途抛异常 —— 后面的面板永远收不起来，异常还会顺着 R3 观察者链把订阅打断，
    /// 表现就是「点一次标签就卡住」。
    ///
    /// 现在一个字节都不改游戏的字典，改成在它的显隐逻辑之后补一刀：
    ///   - 原生切页 → Postfix 把本 mod 的面板收起来
    ///   - 点本 mod 标签 → 自己把三个原生面板收起来，再显示自己
    ///
    /// 两边都只改 alpha / interactable / blocksRaycasts，**不碰 SetActive** —— 面板的
    /// 激活状态是游戏自己的状态机（原生实现里就在来回 SetActive），mod 混进去改会跟它
    /// 下一轮打架，而视觉与输入用 CanvasGroup 已经足够。
    ///
    /// 正常路径不打任何日志。
    /// </summary>
    public static class FilterMenuVisibilityHook
    {
        private const string HarmonyId = "BetterCamera.FilterMenuVisibility";

        private const string ViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.FilterMenuObjectView";
        private const string TabViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.FilterMenuObject.FilterMenuTabButtonObject.FilterMenuTabButtonObjectView";
        private const string CanvasGroupTypeName = "UnityEngine.CanvasGroup";

        /// <summary>FilterMenuType 原生只定义了 0/1/2，3 空闲，本 mod 的 ColorAdjust 页占它。</summary>
        public const int ColorAdjustMenuType = 3;

        private static HarmonyLib.Harmony _harmony;
        private static MethodInfo _target;
        private static bool _applied;

        private static GameObject _panel;
        private static Il2CppSystem.Object _tabView;

        /// <summary>也就是 FilterMenuType 的三个取值各对应的那一页 —— 本 mod 的页亮起时要把它们收起来。</summary>
        private static readonly string[] NativePanelPaths =
        {
            GamePaths.ExposureAndTemperatureLayout,
            GamePaths.FiltersListLayout,
            GamePaths.SpecialEffectsListLayout,
        };

        public static bool Apply()
        {
            if (_applied) return true;

            try
            {
                _target = FindTarget();
                if (_target == null)
                {
                    MelonLogger.Error("[BetterCamera] 找不到 FilterMenuObjectView.SetFilterMenuObjectVisible，" +
                                      "ColorAdjust 页将无法随标签切换显隐（其余功能不受影响）");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);
                _harmony.Patch(_target,
                    postfix: new HarmonyMethod(typeof(FilterMenuVisibilityHook), nameof(Postfix)));
                _applied = true;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 滤镜菜单显隐补丁失败: " + e.GetType().Name + ": " + e.Message);
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
                MelonLogger.Error("[BetterCamera] 滤镜菜单显隐补丁撤销失败: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _applied = false;
                _target = null;
                _panel = null;
                _tabView = null;
            }
        }

        /// <summary>
        /// 原生刚切完页。参数是 3 的情况原生不会发生（它的字典里没有本 mod 的面板），
        /// 留着这个判断只是让语义完整。
        ///
        /// 除了藏面板，还必须把本 mod 的标签也取消选中 —— 否则切到原生页之后，
        /// 那个标签会一直亮着，看起来像是"卡在 hover"。原生三个标签的选中态由游戏
        /// 自己按 CurrentFilterMenuType 维护，只有我们这个是编外的。
        /// </summary>
        public static void Postfix(FilterMenuType shownFilterMenuType)
        {
            try
            {
                if ((int)shownFilterMenuType == ColorAdjustMenuType) return;
                SetCanvasGroupVisible(_panel, false);
                SetViewSelected(_tabView, false);
            }
            catch { }
        }

        /// <summary>登记本 mod 的面板与标签按钮。面板立即隐藏，等玩家点标签才出现。</summary>
        public static void Register(GameObject panel, Il2CppSystem.Object tabView)
        {
            _panel = panel;
            _tabView = tabView;
            SetCanvasGroupVisible(_panel, false);

            // 克隆体带着模板 prefab 里序列化下来的配色，那是按"选中/hover"存的。
            // 游戏自己只会在 InitView 时给标签上色，而克隆体的 InitView 不一定跑到，
            // 于是第一次打开菜单时它一直显示那个配色 —— 看着就是"卡在 hover"，
            // 点一次之后我们的回调写过色才正常。这里开局先按未选中写一遍。
            SetViewSelected(_tabView, false);
        }

        /// <summary>玩家点了本 mod 的标签：收起三个原生面板，亮起本 mod 的页。</summary>
        public static void ShowColorAdjust()
        {
            try
            {
                foreach (var path in NativePanelPaths)
                    SetCanvasGroupVisible(GameObject.Find(path), false);

                SetCanvasGroupVisible(_panel, true);
                SetViewSelected(_tabView, true);
                DeselectNativeTabs();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 切到 ColorAdjust 页失败: " + e.Message);
            }
        }

        /// <summary>
        /// 本 mod 的页亮起时把原生三个标签的选中态灭掉 —— 否则当前选中的原生标签会
        /// 一直保持高亮，看起来像同时选中了两个。
        /// </summary>
        private static void DeselectNativeTabs()
        {
            var layout = GameObject.Find(GamePaths.TabButtonsLayout);
            if (layout == null) return;

            for (int i = 0; i < layout.transform.childCount; i++)
            {
                var child = layout.transform.GetChild(i);
                if (child.name == GamePaths.NameColorAdjustTabButton) continue;
                SetViewSelected(NativeRefs.FindComponent(child, TabViewTypeName), false);
            }
        }

        private static void SetViewSelected(Il2CppSystem.Object view, bool selected)
        {
            if (view == null) return;
            try
            {
                Il2CppReflection.FindIl2CppMethod(view.GetIl2CppType(), "SetSelected")
                    ?.Invoke(view, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(selected) });
            }
            catch { }
        }

        /// <summary>
        /// 按 CanvasGroup 显示/隐藏面板，和原生隐藏面板时的做法一致（只取其中不碰 SetActive 的部分）。
        /// 找不到 CanvasGroup 就什么都不做 —— 有些页可能本来就没有。
        /// </summary>
        private static void SetCanvasGroupVisible(GameObject go, bool visible)
        {
            if (go == null) return;

            var cg = NativeRefs.FindComponent(go.transform, CanvasGroupTypeName);
            if (cg == null) return;

            try
            {
                var t = cg.GetIl2CppType();
                Il2CppReflection.FindIl2CppMethod(t, "set_alpha")
                    ?.Invoke(cg, new Il2CppSystem.Object[] { Il2CppReflection.BoxFloat(visible ? 1f : 0f) });
                Il2CppReflection.FindIl2CppMethod(t, "set_interactable")
                    ?.Invoke(cg, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(visible) });
                Il2CppReflection.FindIl2CppMethod(t, "set_blocksRaycasts")
                    ?.Invoke(cg, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(visible) });
            }
            catch { }
        }

        private static MethodInfo FindTarget()
        {
            var type = Il2CppReflection.FindType(ViewTypeName);
            if (type == null) return null;

            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "SetFilterMenuObjectVisible") return m;
            }
            return null;
        }
    }
}
