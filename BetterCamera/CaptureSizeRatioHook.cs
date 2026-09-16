using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using BetterCamera.Features;

namespace BetterCamera
{
    /// <summary>
    /// 把本 mod 的自定义拍照比例接到游戏的出片链路上。
    ///
    /// 游戏把「CaptureSize → 比例」写死成了三个内联常量
    /// （`RoomSnapSceneSequence.&lt;TakePhotoWithCaptureSizeAsync&gt;d__159.MoveNext`）：
    ///     ==1 → TakePhotoWithAspectRatioAsync(0.5625f)      // 9:16
    ///     ==2 → TakePhotoWithAspectRatioAsync(0x3F0FE339f)
    ///     其它 → TakePhotoAsync(null)                        // 不裁切
    /// 加枚举值不会生效，所以只能把比例插进去。
    ///
    /// 出片那一刀打在 `ScreenshotTaker.TakeScreenshotWithAspectRatioAsync` 的**入口桩**上
    /// （同步、非状态机、每次必经），把写死的 9:16 换成玩家选的比例。它外面那层
    /// `TakePhotoWithCaptureSizeAsync` 不动 —— 让游戏走它自己的 Portrait 分支，
    /// "当前尺寸"由 CaptureSizePresets 通过游戏自己的 Presenter 去切。
    ///
    /// **这个分工是踩过坑才定下来的**：早先的版本是在出片那一刻偷偷把 captureSize
    /// 改成 Portrait，而游戏状态和取景框还停在别处 —— 三者对不上，整条拍照流程会卡死，
    /// 表现是快门和退出按钮一起失效（hover 还有，就是按下去没反应）。
    ///
    /// 为什么不直接替换裁切矩形：那条路的参数是 `Nullable&lt;Rect&gt;`，用 Harmony 的
    /// ref 参数去接很容易类型匹配不上；而走比例这条路，矩形由游戏自己算，坐标系天然是对的。
    ///
    /// 另外两刀是 UI 侧的：原生选项被选中时清掉 mod 状态、菜单重开时恢复开关视觉。
    ///
    /// ⚠️ 已知边界：出片那刀替换的是全局截图服务的比例，只要 mod 状态还在就生效。
    /// 状态在离开拍照场景时清（Core 调 Reset），所以跨场景不会外溢；但拍照场景内
    /// 若有别的按比例截图（贴纸/工坊缩略图之类）会一并被改。目前没观察到这种用法。
    ///
    /// 正常路径不打任何日志。
    /// </summary>
    public static class CaptureSizeRatioHook
    {
        private const string HarmonyId = "BetterCamera.CaptureSizeRatio";

        private static HarmonyLib.Harmony _harmony;
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();
        private static bool _applied;

        public static bool Apply()
        {
            if (_applied) return true;

            try
            {
                Targets.Clear();
                CollectTargets();

                if (Targets.Count < RequiredTargetCount)
                {
                    MelonLogger.Error("[BetterCamera] 自定义拍照比例的目标方法没找全（" + Targets.Count +
                                      "/" + RequiredTargetCount + "），该功能将不生效（其余功能不受影响）");
                    MelonLogger.Error("[BetterCamera] 已找到：" + DescribeSlots(true));
                    MelonLogger.Error("[BetterCamera] 没找到：" + DescribeSlots(false));
                    Targets.Clear();
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                Patch(Targets[0], prefix: nameof(AspectRatioPrefix));
                Patch(Targets[1], postfix: nameof(NativeChosenPostfix));
                Patch(Targets[2], postfix: nameof(MenuReinitializedPostfix));

                _applied = true;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 自定义拍照比例补丁失败: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        public static void Remove()
        {
            if (!_applied) return;

            try
            {
                foreach (var t in Targets)
                    _harmony?.Unpatch(t, HarmonyPatchType.All, HarmonyId);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 自定义拍照比例补丁撤销失败: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _applied = false;
                Targets.Clear();
            }
        }

        // ================= 补丁体 =================

        /// <summary>把游戏写死的 9:16 换成玩家选的比例。</summary>
        public static void AspectRatioPrefix(ref float aspectRatio)
        {
            if (CaptureSizePresets.SelectedRatio is float ratio)
                aspectRatio = ratio;
        }

        /// <summary>原生选项被点了 —— 清掉 mod 状态，别再插手。</summary>
        public static void NativeChosenPostfix() => CaptureSizePresets.OnNativeSizeChosen();

        /// <summary>菜单重开会把开关刷一遍，补一次我们自己的选中态。</summary>
        public static void MenuReinitializedPostfix() => CaptureSizePresets.OnMenuReinitialized();

        private static void Patch(MethodInfo target, string prefix = null, string postfix = null)
        {
            _harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(CaptureSizeRatioHook), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(CaptureSizeRatioHook), postfix));
        }

        // ================= 找目标 =================

        private const int RequiredTargetCount = 3;

        /// <summary>每个槽位期望的名字，只在找不到时用来报错。</summary>
        private static readonly string[] SlotNames =
        {
            "TakeScreenshotWithAspectRatioAsync（异步入口桩，收 float）",
            "CaptureSizeMenuObjectView 上收 CaptureSize 的那个 lambda",
            "CaptureSizeMenuObjectView.InitView",
        };

        /// <summary>每个槽位实际找到了谁（"类型.方法"），找不到是 null。只为报错用。</summary>
        private static readonly string[] FoundIn = new string[RequiredTargetCount];

        private static string DescribeSlots(bool wantFound)
        {
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < RequiredTargetCount; i++)
            {
                bool found = FoundIn[i] != null;
                if (found != wantFound) continue;

                if (sb.Length > 0) sb.Append("；");
                sb.Append(i).Append(") ").Append(found ? FoundIn[i] : SlotNames[i]);
            }

            return sb.Length == 0 ? "（无）" : sb.ToString();
        }

        /// <summary>
        /// 一次遍历把四个目标都找出来，按固定次序放进 Targets：
        ///   0 = TakePhotoWithCaptureSizeAsync（异步入口桩，收 CaptureSize）
        ///   1 = TakeScreenshotWithAspectRatioAsync（异步入口桩，收 float）
        ///   2 = CaptureSizeMenuObjectView 的 &lt;InitView&gt;b__8_1（收 CaptureSize）
        ///   3 = CaptureSizeMenuObjectView.InitView
        ///
        /// 按名字特征扫，不依赖具体命名空间和嵌套层级 —— 编译器生成的 lambda 挂在
        /// `&lt;&gt;c__DisplayClass` 这类嵌套类型上，按声明类型是找不到的。
        /// 注意 GetTypes() 在大程序集上会抛 ReflectionTypeLoadException，必须从异常里取
        /// 已加载的部分，否则恰好会跳过目标所在的大程序集。
        /// </summary>
        private static void CollectTargets()
        {
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic |
                                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            int found = 0;
            MethodInfo[] hits = new MethodInfo[RequiredTargetCount];
            Array.Clear(FoundIn, 0, FoundIn.Length);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name ?? "";
                if (!name.StartsWith("Il2Cpp")) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null) continue;

                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BF); } catch { continue; }

                    foreach (var m in methods)
                    {
                        int slot = Classify(m, t.FullName);
                        if (slot < 0 || (found & (1 << slot)) != 0) continue;

                        hits[slot] = m;
                        FoundIn[slot] = (t.FullName ?? t.Name) + "." + m.Name;
                        found |= 1 << slot;
                    }
                }
            }

            foreach (var h in hits)
                if (h != null) Targets.Add(h);
        }

        /// <summary>认出目标方法，返回它该占的槽位；不是目标返回 -1。</summary>
        private static int Classify(MethodInfo method, string declaringTypeName)
        {
            string n = method.Name;

            if (n.EndsWith("TakeScreenshotWithAspectRatioAsync", StringComparison.Ordinal)) return 0;

            if (declaringTypeName == null || declaringTypeName.IndexOf("CaptureSizeMenuObjectView", StringComparison.Ordinal) < 0)
                return -1;

            if (n == "InitView") return 2;

            // 编译器生成的 lambda **不能按名字认**：Il2CppInterop 会把名字里的 <> 和 . 换成 _
            // （实测相邻的那个接口实现被命名成 Project_IScreenshotTaker_TakeScreenshotWithAspectRatioAsync），
            // 所以 "<InitView>b__8_1" 在托管侧压根不是这个名字。
            // 按签名认：View 的 lambda 里只有它收 CaptureSize（另外两个收 bool）。
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.Name == "CaptureSize")
                return 1;

            return -1;
        }
    }
}
