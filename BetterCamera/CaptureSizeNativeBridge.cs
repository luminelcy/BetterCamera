using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppProject.HomeScene.RoomScene.RoomSnapScene;
using BetterCamera.Features;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera
{
    /// <summary>
    /// 让本 mod 的 4 个自定义比例变成**游戏自己的拍照尺寸**（原生化改造）。
    ///
    /// 【改造前】4 个选项是"借路口的外挂"：不写 CurrentCaptureSize（状态推成 Portrait），
    /// 比例靠 mod 自己注入出片入口桩 + 自己调 UpdateCropArea，开关视觉全靠自己维护。
    /// 代价：第一次切换要"推 Portrait 再同帧接管"（9:16 掠影那套补丁）、反复回放 Timeline
    /// （DOTween CanvasGroup 警告）、每次切换都 Kill 游戏正在跑的补间。
    ///
    /// 【改造后】4 个选项**就是游戏的选项**：往 CaptureSizeMenuObjectView 的
    /// `_captureSizeSwitchButtons`（RotaryHeart 的 SerializableDictionaryBase&lt;CaptureSize, CommonSwitchButtonBehaviour&gt;）
    /// 里插 4 个条目，键是 (CaptureSize)3~6（枚举底层 int，游戏只按整数值比大小）。
    /// 于是：点击 → 游戏自己合并的流 → Presenter 的 CurrentCaptureSizeSetter.Set(我们的键)
    ///       → 游戏自己刷开关视觉（按 key == 当前值刷全表，原生三项自动按灭）
    /// mod 只保留"比例"这一处注入（出片 + 取景框）。
    ///
    /// 【比例从哪来】游戏把「CaptureSize → 比例」写死成了内联常量，且**有三份**
    /// （取景框状态变化 / 取景框窗口 resize / 出片状态机），我们的键在三处都会掉进 else
    /// = "不裁切"。做法统一成一条原则：**不替游戏做事，只把它的入参/常量换成玩家的比例**。
    ///   1) 取景框状态变化：`CaptureSizeCropObjectView.&lt;InitView&gt;b__10_0` 的前缀只把**入参**
    ///      改写成 Portrait ⇒ 原方法照常执行、走它自己的 `==1` 分支，由**游戏自己**调 UpdateCropArea；
    ///      比例再由 `UpdateCropArea`/`UpdateCropAreaImmediately` 的 `ref float` 前缀换掉。
    ///      （上一版是"跳过原方法、我们自己调 UpdateCropArea(>1 的比例)"—— 实测玩家只用原生比例
    ///      反复切换从不卡死、切我们的比例就卡死，所以退回"引它走熟路"。）
    ///   2) 取景框窗口 resize：游戏那个 `_needsResizeUpdate` 是死字段（没人写），所以由我们的
    ///      屏幕尺寸跟踪接管，调"不带动画"那条 —— 同样只在这条路上替它做（游戏那条路根本不跑）。
    ///   3) 出片：`RoomSnapSceneSequence.TakePhotoWithCaptureSizeAsync` 的**入口桩**，把我们的键
    ///      改成 Portrait（只改参数、不动状态），这样出片照旧走"按比例裁切"分支，
    ///      再由 CaptureSizeRatioHook.AspectRatioPrefix 把写死的 9:16 换成玩家的比例。
    ///
    /// 【插入时机：晚插，不跟游戏抢】
    /// 原本的设计是挂 `StartLifeCycle` 的前缀，抢在游戏建点击 Merge 流（订阅时快照）之前插进去。
    /// **实机证明抢不到**：场景那批 Zenject 初始化跑在 MelonLoader 的场景回调之前
    ///（证据：后装的 trace 钩子只看到我们克隆体的 MonoKernel.Start，场景自己的一个都没看到），
    /// 而我们的克隆体是场景回调里才建的。所以改成：克隆体一建好就直接插（CaptureSizePresets.Init 里调）。
    /// 晚插仍然管用 —— 游戏的开关视觉刷新（`&lt;InitView&gt;b__8_1`）是**每次状态变化遍历字典现算**的，
    /// 不是快照；只有"点击入流"那一份拿不到，由我们自己的点击监听补上（CaptureSizePresets.PushKey）。
    ///
    /// 【键类型陷阱】不能从"开放泛型/声明类型"上抓字典方法：那样拿到的键参数类型是装箱 Int32，
    /// 而游戏这份字典的键是 CaptureSize 枚举（滤镜菜单当年就栽在这上面）。这里改成
    /// **加程序集引用走强类型**（csproj 里引 RotaryHeart 那个代理程序集），闭合泛型由我们自己
    /// MakeGenericType —— 而且插完**逐键读回校验**（ContainsKey）：本项目有"invoke 不抛但没生效"
    /// 的前科，只有读回才验得出来是"插进去了"还是"选项点了没反应"。
    /// </summary>
    internal static class CaptureSizeNativeBridge
    {
        private const string HarmonyId = "BetterCamera.CaptureSizeNative";

        /// <summary>菜单 View 的完整代理类型名 —— 只用于"按路径找组件"。</summary>
        private const string MenuViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeMenuObject.CaptureSizeMenuObjectView";

        /// <summary>取景框 View（CollectTargets 按**完整名**匹配，避免按短名误收别的同名类）。</summary>
        private const string CropViewTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.CaptureSizeCropObject.CaptureSizeCropObjectView";

        /// <summary>拍照场景的流程类（出片入口桩在它身上）。</summary>
        private const string SequenceTypeName =
            "Il2CppProject.HomeScene.RoomScene.RoomSnapScene.RoomSnapSceneSequence";

        private const string DictFieldName = "_captureSizeSwitchButtons";
        private const string CropAreaImmediateMethodName = "UpdateCropAreaImmediately";

        private static HarmonyLib.Harmony _harmony;
        private static bool _applied;

        // 收集到的目标（同名类型每一份都收 —— 见 CollectTargets 的说明）
        private static readonly List<MethodInfo> CropOnChangedTargets = new List<MethodInfo>();
        private static readonly List<MethodInfo> CropResizeTargets = new List<MethodInfo>();
        private static readonly List<MethodInfo> CropRatioTargets = new List<MethodInfo>();
        private static readonly List<MethodInfo> TakePhotoTargets = new List<MethodInfo>();

        /// <summary>
        /// 日志限流：同一个站点只报一次。
        ///
        /// ⚠️ 每次进拍照场景要清一次（Core 调 <see cref="ResetReported"/>）—— 否则"第一次失败、
        /// 之后永远安静"，正是排查时最缺的信息。
        /// </summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>进场景时清掉限流记录，让每个站点在每一局里都有资格再报一次。</summary>
        public static void ResetReported() => Reported.Clear();

        /// <summary>
        /// 离开拍照场景时清掉**跨场景的输入状态**。
        ///
        /// 补丁本身是类型级的、跨场景不用重挂（挂一次就一直有效，也不该摘 —— 摘了再挂
        /// 只会多一层风险）。真正的坑是这两个状态：
        ///
        ///   _pendingCropKey —— "这一刀该用哪个比例"。不清零的话，上一个场景残留的键
        ///     会去改下一个场景**第一次** UpdateCropArea 的比例，而那个键在新场景里
        ///     已经毫无意义（取景框会按一个玩家没选过的比例裁切）。
        ///   _lastScreenW/H —— 上次下发取景框时的屏幕尺寸。不清零会让新场景的第一次
        ///     resize 判断被跳过。
        ///
        /// 由 Core.OnSceneWasUnloaded 调（和 CaptureSizePresets.Reset 一起）。
        /// </summary>
        public static void ResetSceneState()
        {
            _pendingCropKey = null;
            _lastScreenW = 0;
            _lastScreenH = 0;
        }

        /// <summary>
        /// 上一次按比例下发取景框时的屏幕尺寸。resize 补丁靠它判断"画布尺寸真的变了"——
        /// 游戏那个 `_needsResizeUpdate` 是死字段（没人写），不能当门闩用。
        /// </summary>
        private static int _lastScreenW, _lastScreenH;

        /// <summary>
        /// 【临时诊断，定位完删】"这一刀该用哪个比例"的键 —— 由 <see cref="CropOnSizeChangedPrefix"/> 记下、
        /// <see cref="CropRatioPrefix"/> 读走。卡死的时候日志里这几行能指出停在哪一步。
        /// </summary>
        private static CaptureSize? _pendingCropKey;

        // ---- 注册 ----

        public static bool Apply()
        {
            if (_applied) return true;

            // 【临时诊断】no-patches ⇒ 一个补丁都不挂（做"mod 已加载但完全不插手"的基准测试）。
            // 需要重启进程才生效（`_applied` 会缓存）。
            if (ProbeFlags.Has("no-patches"))
            {
//                 MelonLogger.Msg("[probe] no-patches：本次不挂任何原生化补丁（基准测试）");
                _applied = true;
                return true;
            }

            try
            {
                CollectTargets();

                // 被 no-*-hook 开关故意摘掉的那几组本来就是空的，不能因此判定"拦截点没找全"。
                // 每一个 no-*-hook 都必须在这里登记，否则摘掉之后 Apply 会误报失败并整个不做原生化。
                bool needCropOnChanged = !ProbeFlags.Has("no-crop-hook");
                bool needCropRatio = !ProbeFlags.Has("no-cropsub-hook");
                bool needCropResize = !ProbeFlags.Has("no-resize-hook");
                bool needTakePhoto = !ProbeFlags.Has("no-output-hook");

                if ((needCropOnChanged && CropOnChangedTargets.Count == 0)
                    || (needCropRatio && CropRatioTargets.Count == 0)
                    || (needCropResize && CropResizeTargets.Count == 0)
                    || (needTakePhoto && TakePhotoTargets.Count == 0))
                {
                    // 报错要能区分三种原因：程序集还没加载 / 类型或方法名变了 / 签名不匹配。
                    // 只报数字的话，一次实机日志看不出是哪一种。
                    MelonLogger.Error("[BetterCamera] 原生化需要的拦截点没找全（已扫 "
                                      + AppDomain.CurrentDomain.GetAssemblies().Length + " 个程序集）："
                                      + "取景框状态变化=" + (CropOnChangedTargets.Count > 0 ? "OK" : "缺 " + CropViewTypeName + " 上收 CaptureSize 的 void 方法")
                                      + "；resize=" + (CropResizeTargets.Count > 0 ? "OK" : "缺 " + CropViewTypeName + ".LateUpdate")
                                      + "；出片=" + (TakePhotoTargets.Count > 0 ? "OK" : "缺 " + SequenceTypeName + ".TakePhotoWithCaptureSizeAsync(5 参)")
                                      + "。本次不做原生化");
                    return false;
                }

                _harmony ??= new HarmonyLib.Harmony(HarmonyId);

                int patched = 0;
                patched += PatchAll(CropOnChangedTargets, nameof(CropOnSizeChangedPrefix));
                patched += PatchAll(CropResizeTargets, nameof(CropOnResizePrefix));
                patched += PatchAll(CropRatioTargets, nameof(CropRatioPrefix));
                patched += PatchAll(TakePhotoTargets, nameof(TakePhotoArgPrefix));

                _applied = true;

                // ⚠️ 这一行是必需的，不是噪音：Apply 成功原本是**静默**的，于是"补丁挂上了但一次不触发"
                // 在日志里完全看不出来 —— 2026-09-17 就栽过这一下。
//                 MelonLogger.Msg("[BetterCamera] 原生化补丁已挂 " + patched + " 个："
//                                 + "取景框×" + CropOnChangedTargets.Count
//                                 + " resize×" + CropResizeTargets.Count
//                                 + " 比例×" + CropRatioTargets.Count
//                                 + " 出片×" + TakePhotoTargets.Count);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[BetterCamera] 原生化补丁失败: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static int PatchAll(List<MethodInfo> targets, string prefixName)
        {
            int n = 0;
            foreach (var method in targets)
            {
                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(CaptureSizeNativeBridge), prefixName));
                n++;
            }
            return n;
        }

        /// <summary>
        /// 扫**所有**已加载程序集，按**完整类型名**收目标，每一份同名类型都收。
        ///
        /// 【为什么不用 Il2CppReflection.FindType】它只返回第一个匹配（哪一份取决于程序集加载顺序），
        /// 而游戏类型可能同时存在于 **Il2Cpp*AssemblyDefinition 与 `Assembly-CSharp`** 两份代理里 ——
        /// 实测确认存在这种同 FullName 的 Project 类型（例如 `…MoveToJoinMultiRoomButtonObjectPresenter`）。
        /// 补丁一旦打在没人经过的那份上就是"绑上了但一次不触发"，而且完全静默。
        ///
        /// 【为什么不按短名 + StartsWith("Il2Cpp") 过滤】那是**错的**：`Assembly-CSharp.dll` /
        /// `Assembly-CSharp-firstpass.dll` 也是真正的游戏代理程序集（分别定义 443 / 17 个游戏类型），
        /// 被那个过滤器整个排除了。所以这里扫全部程序集、并且用**完整名**匹配避免误收。
        ///
        /// （更正一条旧注释：本项目的注释说"同名类型在多份程序集里都有"是**不准确**的 ——
        /// 2026-09-17 用元数据扫描器枚举过全部 85 个代理程序集，多数 grep 命中都是 TypeRef 引用。
        /// 但"扫遍所有程序集 + 按完整名匹配"本身仍然是对的做法，因为它对两种情形都成立。）
        ///
        /// GetTypes() 在大程序集上会抛 ReflectionTypeLoadException（部分类型依赖缺失），
        /// 必须从异常里取已加载的那部分，否则恰好会跳过目标所在的大程序集。
        /// </summary>
        private static void CollectTargets()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null || type.FullName == null) continue;

                    if (type.FullName == CropViewTypeName)
                    {
                        // 状态变化那个是编译器生成的 lambda，不能按名字认 → 按签名（见 FindByCaptureSizeArg）
                        //
                        // 【临时诊断】no-crop-hook ⇒ 单独摘掉这一个补丁。
                        // 它是"切比例"路径上两个候选 lambda 补丁之一（另一个在 CaptureSizeRatioHook：
                        // 菜单 View 的 <InitView>b__8_1）。已证明破坏源是"挂补丁"本身而不是补丁内容
                        //（no-croparg 让前缀整个提前返回、补丁仍然挂着，照样坏），
                        // 所以要在这里**不收集目标**才是真正的排除。
                        if (!ProbeFlags.Has("no-crop-hook"))
                            Add(CropOnChangedTargets, FindByCaptureSizeArg(type));
                        // 【临时诊断】no-resize-hook ⇒ 真的不挂 LateUpdate。
                        if (!ProbeFlags.Has("no-resize-hook"))
                            Add(CropResizeTargets, FindInstance(type, "LateUpdate", 0));

                        // 两个"写比例"的入口：把游戏写死的 9:16 换成玩家选的比例
                        //
                        // 【临时诊断】no-cropsub-hook ⇒ 真的不挂这两个。
                        // "切比例"必然经过 UpdateCropArea，所以它是下一个嫌疑；
                        // 而且它同样是 ref 值类型参数（ref float）。
                        if (!ProbeFlags.Has("no-cropsub-hook"))
                        {
                            Add(CropRatioTargets, FindInstance(type, "UpdateCropArea", 1));
                            Add(CropRatioTargets, FindInstance(type, "UpdateCropAreaImmediately", 1));
                        }
                    }
                    else if (type.FullName == SequenceTypeName)
                    {
                        // 【临时诊断】no-output-hook ⇒ 真的不挂出片入口。
                        if (!ProbeFlags.Has("no-output-hook"))
                            Add(TakePhotoTargets, FindInstance(type, "TakePhotoWithCaptureSizeAsync", 5));
                    }
                }
            }
        }

        private static void Add(List<MethodInfo> list, MethodInfo method)
        {
            if (method == null) return;

            string key = Il2CppReflection.MethodKey(method);   // 键里带程序集，见那边的说明
            foreach (var existing in list)
                if (Il2CppReflection.MethodKey(existing) == key) return;

            list.Add(method);
        }

        /// <summary>
        /// 找"收一个 CaptureSize 的那个方法"= View 订阅 OnCaptureSizeChanged 时挂的回调。
        ///
        /// **不能按名字认**：编译器生成的 lambda 在托管侧被 Il2CppInterop 改名
        /// （`&lt;InitView&gt;b__10_0` → `_InitView_b__10_0`：尖括号去掉、前缀补下划线），
        /// 字面名在托管侧根本不存在 —— 按名字找永远返回 null，而且是静默的。
        ///
        /// **也不能只要求"一个 CaptureSize 参数"**：实际有两处满足 ——
        /// `_InitView_b__10_0(captureSize)` 和**枚举字段的 setter** `set__currentCaptureSize(value)`。
        /// 只按参数类型筛就是靠元数据顺序**侥幸**命中，顺序一变就会静默挂到 setter 上（然后绑不上）。
        /// 所以三条约束一起用：只看本类型自己的方法（DeclaredOnly）、排除 get_/set_、要求返回 void。
        /// </summary>
        private static MethodInfo FindByCaptureSizeArg(Type type)
        {
            if (type == null) return null;

            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public
                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (var m in type.GetMethods(BF))
            {
                if (m.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
                if (m.Name.StartsWith("set_", StringComparison.Ordinal)) continue;
                if (m.ReturnType != typeof(void)) continue;

                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.Name == "CaptureSize")
                {
                    // 把命中名记一次：将来一次无声的"功能消失"可以靠这行定位
                    WarnOnce("cropmethod", "取景框状态变化的拦截点 = " + type.Name + "." + m.Name, null);
                    return m;
                }
            }

            return null;
        }

        private static MethodInfo FindInstance(Type type, string name, int argCount)
        {
            if (type == null) return null;

            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != name) continue;
                if (m.GetParameters().Length != argCount) continue;
                return m;
            }
            return null;
        }

        // ---- 1) 插入：我们的克隆体建好之后，把它们塞进游戏的开关字典 ----

        /// <summary>
        /// 把 4 个预设注册进游戏的 `_captureSizeSwitchButtons`。由 CaptureSizePresets.Init 在
        /// **克隆体建好之后**调用。
        ///
        /// 【为什么不是"赶在 StartLifeCycle 之前插"】原设计是挂 StartLifeCycle 的 prefix，
        /// 想抢在游戏建 Merge 流（那次订阅是快照）之前把条目放进去。实机证明**抢不到**：
        /// 场景那批 Zenject 初始化（含 StartLifeCycle）跑在 MelonLoader 的场景回调**之前**
        ///（证据：后装的 trace 钩子只看到我们自己克隆体的 MonoKernel.Start，一个场景 kernel 都没看到），
        /// 而我们的克隆体是场景回调里才建的。所以改为：**晚插**。
        ///
        /// 【晚插为什么仍然有用】游戏的开关视觉刷新（`&lt;InitView&gt;b__8_1`）是
        /// **每次状态变化时遍历字典现算**的，不是快照 —— 所以只要条目在玩家点击之前进了字典，
        /// 我们那 4 个开关就会被游戏自己点亮/按灭。唯一拿不到的是"点击入流"（那份 Merge 是快照），
        /// 那部分由我们自己的点击监听补上（见 CaptureSizePresets 的 PushKey）。
        /// </summary>
        public static void RegisterPresets()
        {
            // ⛔ **默认不插字典**（2026-09-18 决定）。
            //
            // 实机二分已经确认：把这 4 个条目插进游戏的 `_captureSizeSwitchButtons`
            // 就是**快门和返回键一起失效**的原因 ——
            //   · 完全不插（no-dict）           → 快门 / 返回键一切正常，自定义比例能拍
            //   · 插进去（无论键怎么装箱）      → 快门、返回键、开关选中态全部失效
            // 而且"装箱成 System.Int32 而非 CaptureSize"这个真 bug 修掉之后**依然坏**，
            // 说明坏的原因不是键类型，是"往这个字典里塞东西"这件事本身。
            //
            // 权衡：这个改造的收益只是「开关的选中态由游戏维护」，
            // 代价却是核心拍照流程瘫痪 —— 不值。所以默认关掉。
            //
            // 以后要接着查这个坑（或者哪天找到正确的插入方式），加 `dict-insert` 再开。
            // 那 4 个开关的选中态目前不由任何人维护，是已知的、可接受的缺口。
            if (!ProbeFlags.Has("dict-insert")) return;

            if (ProbeFlags.Has("no-dict")) return;   // 【临时诊断】

            try
            {
                var view = NativeRefs.FindComponent(GamePaths.CaptureSizeMenu, MenuViewTypeName);
                if (view == null)
                {
                    WarnOnce("view", "按路径找不到拍照尺寸菜单的 View，自定义比例不会出现在开关视觉里"
                                     + "（点击与比例仍然工作）", null);
                    return;
                }

                InsertInto(view);
            }
            catch (Exception e)
            {
                WarnOnce("insert", "注册自定义拍照尺寸失败（自定义比例会被当成不裁切）", e);
            }
        }

        private static void InsertInto(Il2CppSystem.Object view)
        {
            var dict = Il2CppReflection.FindIl2CppField(view.GetIl2CppType(), DictFieldName)?.GetValue(view);
            if (dict == null)
            {
                WarnOnce("dict", "拿不到拍照尺寸菜单的开关字典", null);
                return;
            }

            var closedType = DictionaryType();
            if (closedType != null)
            {
                // 【诊断】比对自己 MakeGenericType 出来的闭合类型 和 字典实例的**真实** il2cpp 类型。
                //
                // 2026-09-18 已经确认：**把条目插进这个字典就是快门失效的元凶**
                //（纯功能全开 → 快门坏；只关 no-dict → 快门恢复正常，其余一切照旧）。
                //
                // 最可能的机理是：两种途径造出来的泛型实例**名字相同但不是同一个实例**，
                // 于是插进去的是**坏条目** —— 游戏遍历字典时读到类型不对的东西，
                // 连锁破坏它自己的状态机。（滤镜菜单当年栽过一模一样的坑，
                // 见 FilterMenuVisibilityHook 顶部那段注释。）
                try
                {
//                     MelonLogger.Msg("[dict] 自己造的闭合类型 = " + closedType.FullName);
//                     MelonLogger.Msg("[dict] 字典的真实类型   = "
//                                     + (dict.GetIl2CppType()?.FullName ?? "?"));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[dict] 读字典真实类型失败: " + e.GetType().Name);
                }
            }
            if (closedType == null)
            {
                MelonLogger.Error("[BetterCamera] 解析不出开关字典的闭合泛型类型，自定义比例不做原生化");
                return;
            }

            var setItem = closedType.GetMethod("set_Item",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (setItem == null)
            {
                MelonLogger.Error("[BetterCamera] 开关字典上没有 set_Item，自定义比例不做原生化");
                return;
            }

            // 键类型检查：走到这一步 set_Item 的键参数**必然**是 CaptureSize（闭合泛型是我们自己 Make 的），
            // 所以它不是防线、只是把"我们造出来的类型对不对"显式写出来 —— 真正的防线是下面的读回校验。
            // （滤镜菜单当年的坏条目就是"键类型悄悄变成装箱 Int32"，那种事只有读回才验得出来。）
            var keyType = setItem.GetParameters()[0].ParameterType;
            if (keyType != typeof(CaptureSize))
            {
                MelonLogger.Error("[BetterCamera] 闭合字典的键类型不是 CaptureSize（是 " + keyType.Name
                                  + "），拒绝插入 —— 这是滤镜菜单踩过的坏条目陷阱");
                return;
            }

            // 字典实例本身是泛型基类包装，调用前要按闭合类型重新包一次指针
            var proxy = closedType.GetConstructor(new[] { typeof(IntPtr) });
            if (proxy == null)
            {
                MelonLogger.Error("[BetterCamera] 拿不到字典闭合类型的指针构造器，自定义比例不做原生化");
                return;
            }

            object target;
            try { target = proxy.Invoke(new object[] { dict.Pointer }); }
            catch (Exception e)
            {
                WarnOnce("proxy", "按闭合类型包装开关字典失败", e);
                return;
            }

            // 每次都插（set_Item 幂等）。**不要**在外面加"这个 View 插过了"的标记：
            // 标记要么早于插入结果落下、要么用截断的指针当身份，任何一种都会造成
            // "标记说插过了、实际没插、日志里什么都没有"的静默永久失效（三个 agent 独立指出过）。
            int attempted = 0;
            foreach (var preset in CaptureSizePresets.All)
            {
                var toggle = preset.Toggle;
                if (toggle == null)
                {
                    WarnOnce("toggle" + preset.Label,
                             "预设 " + preset.Label + " 的开关还没建好（克隆体没拿到？），这一个不会被注册", null);
                    continue;
                }

                var value = Il2CppReflection.WrapAsManaged(toggle, CaptureSizePresets.ToggleTypeName);
                if (value == null)
                {
                    WarnOnce("wrap" + preset.Label, "包装 " + preset.Label + " 的开关失败", null);
                    continue;
                }

                try
                {
                    // ⚠️ 键必须用 BoxEnum 按**枚举自己的类型**装箱。
                    //
                    // 原先是 `new object[] { preset.Key, value }` —— 托管侧装箱，Il2CppInterop
                    // 最终把它编组成 System.Int32，而这个字典要的键是 CaptureSize 枚举。
                    // 两者在托管侧都"是一个整数"，il2cpp 侧却是两个类型 ⇒ 塞进去就是**坏条目**：
                    // 游戏遍历字典时类型对不上号，连锁破坏它自己的状态机。
                    // 2026-09-18 实测：只把这一步关掉（no-dict），快门就恢复正常。
                    //
                    // 注意读回校验也一直用的是同一个错装箱，所以它验不出来 ——
                    // "invoke 不抛"和"读回能查到"都证明不了键类型是对的。
                    setItem.Invoke(target, new object[]
                    {
                        Il2CppReflection.BoxEnum(keyType, (int)preset.Key),
                        value
                    });
                    attempted++;
                }
                catch (Exception e)
                {
                    WarnOnce("set" + preset.Label, "插入 " + preset.Label + " 到开关字典失败", e);
                }
            }

            VerifyInsertion(closedType, target, attempted, keyType);
        }

        /// <summary>
        /// 读回校验：**invoke 不抛不等于真的进去了**（本项目有"MethodInfo.Invoke 静默失效"的前科，
        /// 滤镜菜单那个坏条目也是这么来的）。逐键 ContainsKey，缺一个就响亮报错 ——
        /// 否则玩家的表现只是"这几个选项点了没反应"，日志里一点线索都没有。
        /// </summary>
        private static void VerifyInsertion(Type closedType, object dict, int attempted,
                                            Type keyType)
        {
            var containsKey = closedType.GetMethod("ContainsKey",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (containsKey == null)
            {
                MelonLogger.Warning("[BetterCamera] 字典上没有 ContainsKey，无法读回校验"
                                    + "（插了 " + attempted + " 个，是否生效只能靠实机点一下看）");
                return;
            }

            var missing = new List<string>();
            foreach (var preset in CaptureSizePresets.All)
            {
                try
                {
                    // 和插入用**同一个**装箱方式。两边一致才有意义 ——
                    // 之前"读回通过"其实是因为两边都用同一个错装箱，验了个寂寞。
                    var has = containsKey.Invoke(dict, new object[]
                    {
                        Il2CppReflection.BoxEnum(keyType, (int)preset.Key)
                    });
                    if (!(has is bool ok && ok)) missing.Add(preset.Label);
                }
                catch (Exception e)
                {
                    WarnOnce("verify" + preset.Label, "读回校验 " + preset.Label + " 时抛异常", e);
                    missing.Add(preset.Label);
                }
            }

            // 成功路径不播报（只保留失败上报）
            if (missing.Count != 0)
                MelonLogger.Error("[BetterCamera] 自定义比例注册不完整，字典里读不到：" + string.Join("、", missing)
                                  + " —— 这几个选项点了不会有反应（其余功能不受影响）");
        }

        /// <summary>闭合的字典类型：SerializableDictionaryBase&lt;CaptureSize, CommonSwitchButtonBehaviour&gt;。</summary>
        private static Type DictionaryType()
        {
            var open = Il2CppReflection.FindType(
                "Il2CppRotaryHeart.Lib.SerializableDictionary.SerializableDictionaryBase`2");
            var toggle = Il2CppReflection.FindType(CaptureSizePresets.ToggleTypeName);
            if (open == null || toggle == null) return null;

            try { return open.MakeGenericType(typeof(CaptureSize), toggle); }
            catch (Exception e)
            {
                WarnOnce("generic", "构造字典闭合泛型失败", e);
                return null;
            }
        }

        // ---- 2) 取景框：状态变化 ----

        /// <summary>
        /// `CaptureSizeCropObjectView.&lt;InitView&gt;b__10_0(CaptureSize)` 的前缀 —— **只改参数，不跳过原方法**。
        ///
        /// 原方法：`_currentCaptureSize = size; if (size == 1 || size == 2) UpdateCropArea(内联常量); else SetAsDefault();`
        ///
        /// 【为什么不再"跳过原方法、自己调 UpdateCropArea"】那是上一版的写法，实测有代价：
        /// 玩家只用原生比例反复切换**从不卡死**，切我们的比例就会卡死（2026-09-17 实测锁定）。
        /// 与其替游戏做它自己的事（还喂了它从没见过的 >1 横构图比例，而原生永远是 <1 的竖屏比例），
        /// 不如把它引到它自己那条熟路上：把入参改写成 Portrait ⇒ 原方法走 `==1` 分支、
        /// 由**游戏自己的代码**调用 UpdateCropArea。比例由 <see cref="CropRatioPrefix"/> 替换。
        /// 这样"取景框这一刀"和玩家点原生 Portrait 时是**同一条指令序列**，只是数值不同。
        ///
        /// 状态侧的影响：`_currentCaptureSize` 会被原方法写成 Portrait（入参被我们改过）。
        /// 真实状态仍在游戏的 CurrentCaptureSize 变量里（= 我们的键），resize 那条路由
        /// 我们的 resize 前缀按 SelectedRatio 接管（见 CropOnResizePrefix），不受这个字段影响。
        /// </summary>
        public static void CropOnSizeChangedPrefix(ref CaptureSize __0)
        {
            // ⚠️ 这个前缀跑在游戏 **CurrentCaptureSize 的观察者链**里（b__10_0）。
            // 从这条链里抛出去的异常会顺链把订阅打断 —— 正是本项目在滤镜菜单上栽过的
            // 「点一次就卡住」。所以整个函数体兜住：出错就退回"什么都不改"，
            // 让原方法按游戏的入参照常跑（退化成没有这个补丁）。
            try
            {
                // ⚠️ 先把"当前这一刀"记下来（**包括原生键**）：只在是我们的键时才改写入参，
                // 但键必须无条件更新 —— 否则"先点自定义、再点原生"时残留的旧键会继续生效，
                // 取景框把原生 Portrait 的 9:16 又改回旧的自定义比例（选项与框子不一致）。
                _pendingCropKey = __0;

                if (ProbeFlags.Has("no-croparg")) return;   // 【临时诊断】
                if (CaptureSizePresets.RatioForKey(__0) == null) return;

                __0 = CaptureSize.Portrait;

//                 MelonLogger.Msg("[crop] 键 " + (int)_pendingCropKey + " → 入参改写成 Portrait（原方法照常执行）");
            }
            catch (Exception e)
            {
                WarnOnce("croparg", "改写取景框入参失败（本次不插手，交回游戏）", e);
            }
        }

        /// <summary>
        /// `CaptureSizeCropObjectView.UpdateCropArea(float aspectRatio)` / `UpdateCropAreaImmediately(float)` 的前缀。
        ///
        /// 游戏走 `==1` 分支时传的是写死的 9:16（0.5625f）；刚才那一刀是我们的键时换成玩家的比例。
        /// 判据用**上面那个键**（<see cref="_pendingCropKey"/>）：不用 mod 侧的选中状态，
        /// 免得两条消费链的先后顺序把"选项"和"框子"拆成两回事。
        /// </summary>
        public static void CropRatioPrefix(ref float __0)
        {
            // ⚠️ `__0` 而不是 `ref float aspectRatio`：Harmony 按参数**名**配对，
            // 而 il2cpp 方法在原生侧没有参数名 —— 名字对不上就可能按错布局取值/回写，
            // 踩调用者的栈。2026-09-18 就是靠这条线索定位到 AspectRatioPrefix 的
            //（见那边的说明），所以全项目的 ref 补丁统一改用位置参数。
            try
            {
                if (ProbeFlags.Has("no-cropsub")) return;   // 【临时诊断】
                if (_pendingCropKey == null) return;

                if (CaptureSizePresets.RatioForKey(_pendingCropKey.Value) is float ratio)
                {
                    __0 = ratio;
//                     MelonLogger.Msg("[crop] 比例换成 " + ratio + "（键 " + (int)_pendingCropKey.Value + "）");
                }
            }
            catch (Exception e)
            {
                WarnOnce("cropsub", "替换取景框比例失败（本次用游戏自己的值）", e);
            }
        }

        // ---- 3) 取景框：窗口 resize ----

        /// <summary>
        /// `CaptureSizeCropObjectView.LateUpdate()` 的前缀。
        ///
        /// LateUpdate 里那份 CaptureSize→比例 switch 和上面是同一套写死常量，我们的键会掉进 else
        /// （resize 之后裁切框被撑回整幅画布）。所以：选中自定义比例期间，resize 由我们按比例重设。
        ///
        /// 每帧都会被调用，所以只做最省事的两步判断：没有待处理的 resize 就直接放行。
        /// </summary>
        public static bool CropOnResizePrefix(object __instance)
        {
            // 这个前缀每帧都被调，而且返回 false 会**跳过游戏自己的 LateUpdate** ——
            // 一旦我们半途出错又跳过原方法，就是"我们没做、游戏也没做"，取景框会停在错的状态。
            // 所以出错一律 return true：退化成"没有这个补丁"，由游戏自己接管。
            try
            {
                if (ProbeFlags.Has("no-resize")) return true;   // 【临时诊断：单独给个开关】

                float? ratio = CaptureSizePresets.SelectedRatio;
                if (ratio == null) return true;                 // 没选自定义比例：游戏自己管

                // ⚠️ 不要读游戏的 `_needsResizeUpdate` 当门闩 —— 全二进制里**没有任何地方写它**
                //（反编译 + xref 双向确认），恒为 false，那样这个补丁会永远放行 = 静默失效。
                // 改成盯屏幕尺寸：窗口一变就自己按比例重设一次（原生那条 else 会把框撑回整幅画布）。
                int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
                if (w == _lastScreenW && h == _lastScreenH) return true;

                _lastScreenW = w;
                _lastScreenH = h;

                CaptureSizePresets.ApplyCropAreaImmediately(ratio.Value);

                // 跳过原方法（它会按 _currentCaptureSize 掉进 else，把框撑回整幅画布）
                return false;
            }
            catch (Exception e)
            {
                WarnOnce("resize", "窗口缩放后按自定义比例重设取景框失败（本次交回游戏自己处理）", e);
                return true;
            }
        }

        // ---- 4) 出片：把我们的键改写成 Portrait（只改参数，不动游戏状态） ----

        /// <summary>
        /// `RoomSnapSceneSequence.TakePhotoWithCaptureSizeAsync(CaptureSize, bool, bool, bool, CancellationToken)` 的入口桩前缀。
        ///
        /// 出片状态机里也是同一套写死常量：==1 → 0.5625f、==2 → 0.562063f、其它 → 不裁切。
        /// 我们的键会走 else，那样连 CaptureSizeRatioHook 的比例补丁都不会被调到（它打在只有
        /// 那两条分支才经过的 TakeScreenshotWithAspectRatioAsync 上）。
        ///
        /// 做法：只把**参数**换成 Portrait —— 状态不动（菜单选中的仍是我们的键、取景框不受影响），
        /// 出片照旧走"按比例裁切"分支，再由 AspectRatioPrefix 换成玩家的比例。
        /// 这与改造前"把状态推成 Portrait"的效果等价，但不再有状态污染和 9:16 中间态。
        /// </summary>
        public static void TakePhotoArgPrefix(ref CaptureSize __0)
        {
            // ⚠️ 这一刀切在**出片状态机自己的帧里**（TakePhotoWithCaptureSizeAsync 是从
            // LifeCycleSequenceAsync 那个 Forget() 掉的循环里调的）。从这里抛出去的异常会让
            // 整条场景流程静默停住 —— 快门和返回键一起失效，而日志一片干净。必须兜住。
            try
            {
                if (ProbeFlags.Has("no-output")) return;

                // 【诊断】出片时游戏实际传进来的尺寸值。
                //
                // 2026-09-18 的现场是「用自定义选项拍出来还是 Default（不裁切）」，
                // 所以要看的就是这里收到的是不是我们的键（3~6）：
                //   收到 3~6 → 补丁生效了，问题在下游的比例替换
                //   收到 0   → 游戏压根没把玩家的选择传到出片这一步，问题在选择链上游
//                 MelonLogger.Msg("[output] 出片入口收到 CaptureSize=" + (int)__0
//                                 + "  当前选中比例=" + (CaptureSizePresets.SelectedRatio?.ToString() ?? "无"));

                if (CaptureSizePresets.RatioForKey(__0) == null) return;

                __0 = CaptureSize.Portrait;
            }
            catch (Exception e)
            {
                WarnOnce("output", "改写拍片尺寸入参失败（本次按游戏自己的值出片）", e);
            }
        }

        // ---- 小工具 ----

        private static void WarnOnce(string site, string message, Exception e)
        {
            if (!Reported.Add(site)) return;
            MelonLogger.Warning("[BetterCamera] " + message
                                + (e == null ? "" : ": " + e.GetType().Name + ": " + e.Message));
        }
    }
}
