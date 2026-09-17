using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppProject.HomeScene.RoomScene.RoomSnapScene;
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
    /// （同步、非状态机、每次必经），把写死的 9:16 换成玩家选的比例。
    ///
    /// 它只在那两条 `==1 / ==2` 分支上会被调到，所以原生化改造之后还要有人把我们的键
    /// 引到那条分支去 —— 那是 <see cref="CaptureSizeNativeBridge"/> 的活（它把
    /// `TakePhotoWithCaptureSizeAsync` 的**参数**换成 Portrait，状态不动）。
    ///
    /// **这个分工是踩过坑才定下来的**：早先的版本是在出片那一刻偷偷把 captureSize
    /// 改成 Portrait，而游戏状态和取景框还停在别处 —— 三者对不上，整条拍照流程会卡死，
    /// 表现是快门和退出按钮一起失效（hover 还有，就是按下去没反应）。
    ///
    /// 为什么不直接替换裁切矩形：那条路的参数是 `Nullable&lt;Rect&gt;`，用 Harmony 的
    /// ref 参数去接很容易类型匹配不上；而走比例这条路，矩形由游戏自己算，坐标系天然是对的。
    ///
    /// 另一刀是 UI 侧的：`CurrentCaptureSize` 变化时记住/清掉"当前选中的自定义预设"
    /// （出片比例与 resize 都要用它）。开关视觉不归我们管 —— 游戏自己会刷。
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

            // 【临时诊断，定位完删】no-ratio ⇒ 这个钩子的两刀（状态变化回调 + 出片比例注入）都不挂。
            // 复核发现：它是唯一**不受任何探针开关控制**的嫌疑块，四轮里全程生效 —— 没有这面开关，
            // 二分就永远分不清"卡死是它还是拍照尺寸那两块"。同样需要重启进程才生效。
            if (ProbeFlags.Has("no-ratio"))
            {
//                 MelonLogger.Msg("[probe] no-ratio：不挂拍照比例钩子（状态变化回调与出片比例注入都停）");
                _applied = true;
                return true;
            }

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

                // ⛔ **默认不挂出片比例注入**（2026-09-18）。
                //
                // 已定位：这一个补丁只要挂着就会让游戏坏 ——
                //     only-capture + no-clones + no-dict + no-ratio          → 正常（两个都不挂）
                //     only-capture + no-clones + no-dict + no-selection-hook → 卡（只挂这一个）
                // 这是最干净环境下、只动一个变量的对比，结论可靠。
                //
                // 它不是参数名的问题（改成位置名 `__0` 之后照样坏），而是"挂这个方法"本身：
                // 绑到的是 `Project_IScreenshotTaker_TakeScreenshotWithAspectRatioAsync` ——
                // 一个**显式接口实现（private）**，和前面几个编译器生成的小 lambda 同类，
                // 都是给 il2cpp 挂补丁很容易出事的地方。
                //
                // 代价：出片不再按玩家选的比例裁切，会退回游戏自己的 9:16（只有原生化那条
                // 分支能裁）。也就是**自定义比例的"出片"暂时不生效**，但游戏不再坏。
                // 正确的注入方式（换挂点 / 换机制）要另外找 —— 加 ratio-inject 开关可以把它开回来复现。
                if (ProbeFlags.Has("ratio-inject"))
                    Patch(Targets[0], prefix: nameof(AspectRatioPrefix));
                // else：出片比例注入默认不挂（见上面说明）。成功路径不播报。

                // 【临时诊断，定位完删】no-selection-hook ⇒ 只不挂"状态变化回调"这一个。
                //
                // 2026-09-18 已在最干净环境下定位到：元凶是 CaptureSizeRatioHook
                //（only-capture + no-clones + no-dict + no-ratio → 一切正常）。
                // 本钩子只有两个补丁，而触发条件是「换比例就卡」——
                //   0 = TakeScreenshotWithAspectRatioAsync   只在**出片**时跑
                //   1 = CaptureSizeMenuObjectView.<InitView>b__8_1   **CurrentCaptureSize 一变就跑** ← 就是它
                // 而且 b__8_1 也是**编译器生成的私有 lambda**，和前面排除掉的那几个同类。
                if (!ProbeFlags.Has("no-selection-hook"))
                    Patch(Targets[1], postfix: nameof(SelectionPostfix));
                // else：no-selection-hook 时不挂状态变化回调。成功路径不播报。

                _applied = true;

                // 成功路径不播报（排查时用的那几行已注释保留在 git 历史/注释里）。
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

        /// <summary>
        /// 把游戏写死的 9:16 换成玩家选的比例。
        ///
        /// ⚠️ 这一刀切在**出片流程内部**（TakeScreenshotWithAspectRatioAsync 由
        /// TakePhotoWithCaptureSizeAsync 调用，而后者是 LifeCycleSequenceAsync 那个
        /// Forget() 掉的循环里的一步）。从这里抛出去的异常会让整条场景流程静默停住 ——
        /// 快门和返回键一起失效，而日志一片干净。所以整个函数体兜住：
        /// 出错就退回"不改"，让游戏用它自己的 9:16。
        /// </summary>
        public static void AspectRatioPrefix(ref float __0)
        {
            // ⚠️ 参数名必须是 `__0`（按位置），**不能**写成 `ref float aspectRatio`。
            //
            // Harmony 默认按**参数名**和目标方法配对，而 il2cpp 方法在原生侧根本没有
            // 参数名 —— 名字对不上时它会按错误的布局取值/回写，踩的正是调用者的栈。
            // 这正是 2026-09-18 定位到的故障：在最干净的配置下（只有比例菜单、
            // 其余功能与克隆体全关）对比
            //     no-ratio（两个补丁都不挂）              → 正常
            //     no-selection-hook（只挂这一个）          → 卡
            // 唯一变量就是本方法，所以它就是元凶。
            try
            {
                if (CaptureSizePresets.SelectedRatio is float ratio)
                    __0 = ratio;
            }
            catch (Exception e)
            {
                WarnOnce("ratio", "替换出片比例失败（本次按游戏自己的比例出片）", e);
            }
        }

        /// <summary>
        /// 游戏的 CurrentCaptureSize 变成 captureSize 了（这个 lambda 就是"状态变了 → 按 key 刷全表"）。
        ///
        /// ⚠️ 它的语义**不是**"玩家点了原生选项" —— 原生化改造之后我们自己的键也会从这条路过，
        /// 所以只能按值分流（自定义键 = 记住选中的预设，原生键 = 清掉）。见 CaptureSizePresets.OnSizeChanged。
        /// </summary>
        public static void SelectionPostfix(CaptureSize __0)
        {
            // 同样跑在游戏的观察者链里（CurrentCaptureSize 变化 → 刷全表开关），
            // 异常会顺链打断订阅 —— 见 FilterMenuVisibilityHook 记的那个前科。
            // 参数同样用位置名 `__0`（Harmony 按名配对，而 il2cpp 没有参数名）。
            try
            {
                CaptureSizePresets.OnSizeChanged(__0);
            }
            catch (Exception e)
            {
                WarnOnce("selection", "记忆选中的拍照比例失败", e);
            }
        }

        /// <summary>已报过的站点。补丁体会随每次状态变化被调用，不设限会刷屏。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>同一个站点只报一次。</summary>
        private static void WarnOnce(string site, string message, Exception e)
        {
            if (!Reported.Add(site)) return;
            MelonLogger.Warning("[BetterCamera] " + message + ": " + e.GetType().Name + ": " + e.Message);
        }

        private static void Patch(MethodInfo target, string prefix = null, string postfix = null)
        {
            _harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(CaptureSizeRatioHook), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(CaptureSizeRatioHook), postfix));
        }

        // ================= 找目标 =================

        private const int RequiredTargetCount = 2;

        /// <summary>每个槽位期望的名字，只在找不到时用来报错。</summary>
        private static readonly string[] SlotNames =
        {
            "TakeScreenshotWithAspectRatioAsync（异步入口桩，收 float）",
            "CaptureSizeMenuObjectView 上收 CaptureSize 的那个 lambda（状态变化 → 刷开关）",
        };

        /// <summary>每个槽位实际找到了谁（"类型.方法"），找不到是 null。报错与**成功**时都会打。</summary>
        private static readonly string[] FoundIn = new string[RequiredTargetCount];

        /// <summary>
        /// 槽位 0 的**全部**候选（只为日志）。
        ///
        /// 槽位 0 是按"方法名后缀"认的，而至少有两个方法满足：接口声明
        /// `IScreenshotTaker.TakeScreenshotWithAspectRatioAsync` 和它的显式实现
        /// （Il2CppInterop 命名成 `Project_IScreenshotTaker_TakeScreenshotWithAspectRatioAsync`）。
        /// 我们只绑第一个 —— 如果绑到了接口声明上（没有方法体、永远不触发），
        /// 出片比例就会静默失效，而这**只有在日志里看到绑的是谁**才能发现。
        /// </summary>
        private static readonly List<string> Slot0Matches = new List<string>();

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
        /// 一次遍历把两个目标都找出来，按固定次序放进 Targets：
        ///   0 = TakeScreenshotWithAspectRatioAsync（异步入口桩，收 float）
        ///   1 = CaptureSizeMenuObjectView 的 &lt;InitView&gt;b__8_1（收 CaptureSize）= 状态变化回调
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
            Slot0Matches.Clear();

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
                        if (slot < 0) continue;

                        string who = (t.FullName ?? t.Name) + "." + m.Name;

                        // 候选要**先记后筛**：槽位 0 的重复匹配本来就会被丢掉，
                        // 但"丢掉了哪些"正是排查绑错方法的关键信息。
                        if (slot == 0) Slot0Matches.Add(who);

                        if ((found & (1 << slot)) != 0) continue;

                        hits[slot] = m;
                        FoundIn[slot] = who;
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
