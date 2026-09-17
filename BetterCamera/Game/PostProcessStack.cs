using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Game
{
    /// <summary>
    /// 读"后处理参数**实际生效的值**"。
    ///
    /// 为什么需要它（2026-09-17 玩家实测 + 探针确证）：URP 是**按优先级混合所有已加载 Volume** 的，
    /// 同一个参数只取优先级最高的那个 `overrideState == true` 的实例；都没 override 时用组件默认值。
    /// 而本 mod 的 ColorAdjust 面板只绑了 `BaseVolume` 上的 ColorAdjustments ——
    /// 那个实例的 saturation 是 `ov=False, v=0`（不参与混合），真正生效的是**房间环境 Volume**
    /// 上的 `ov=True, v=15`。于是滑条显示 0、画面是 15：玩家一拖滑条，我们的 override 把 +15 顶掉，
    /// 画面从 15 掉到 0 附近 —— 表现就是"一拉就跳变"。
    ///
    /// 所以：读值要按优先级逐层找"第一个 override 的那个"，而不是只看自己绑的那一层。
    /// 写值仍写我们自己的实例（见 ColorAdjustSliders.Apply），这样从真实值起步、平滑叠加。
    ///
    /// ⚠️ 没被任何层 override 时返回 **0**（URP 对 ColorAdjustments 的 contrast/hueShift/saturation
    /// 的组件默认值就是 0）—— 注意**不能**拿某一层 profile 里那个 `v=15` 当默认：
    /// `ov=False` 时那个数值根本不参与混合（探针日志里 Room1_Night 的 `contrast(ov=False,v=15)` 就是这种）。
    /// </summary>
    internal static class PostProcessStack
    {
        private const string VolumeTypeName = "UnityEngine.Rendering.Volume";
        private const string ColorAdjustmentsKind = "ColorAdjustments";

        /// <summary>
        /// 场景里可能有 ColorAdjustments 的几个 Volume。
        ///
        /// 优先级不写死 —— 排序时读 Volume 自己的 `priority` 字段（从高到低）。
        /// 这个清单是从运行中的游戏里数出来的：拍照场景 2 个（EffectVolume / BaseVolume）
        /// + 房间场景 2 个（SceneContext/Volume、P_RoomEnvironmentObject/Volume，后者带着房间的调色）。
        /// 房间那两个只在房间场景加载时存在，Find 不到就跳过。
        /// </summary>
        private static readonly string[] VolumePaths =
        {
            "SceneContext/System/P_RoomSnapPostProcessObject/EffectVolume",
            "SceneContext/System/P_RoomSnapPostProcessObject/BaseVolume",
            "SceneContext/Volume",
            "SceneContext/P_RoomEnvironmentObject/Volume",
        };

        /// <summary>
        /// 某个 ColorAdjustments 参数**实际生效**的值。逐层按优先级找第一个 override 的；
        /// 全程没找到就返回 0（组件默认值）。<paramref name="overridden"/> 说明它是被某一层覆盖的。
        /// </summary>
        public static float Effective(string paramName, out bool overridden)
        {
            overridden = false;

            var volumes = new List<Il2CppSystem.Object>();
            foreach (var path in VolumePaths)
            {
                try
                {
                    var volume = NativeRefs.FindComponent(path, VolumeTypeName);
                    if (volume != null) volumes.Add(volume);
                }
                catch { /* 单条路径失败不该影响其它层 */ }
            }

            // 优先级高的先看：第一个 override 的就是生效值
            volumes.Sort((a, b) => PriorityOf(b).CompareTo(PriorityOf(a)));

            foreach (var volume in volumes)
            {
                var param = FindParameter(volume, paramName);
                if (param == null) continue;

                if (!Bool("get_overrideState", param)) continue;

                overridden = true;
                return Float("get_value", param);
            }

            return 0f;
        }

        // ---- 内部 ----

        private static float PriorityOf(Il2CppSystem.Object volume)
        {
            try
            {
                // `priority` 在 Volume 上是**字段**（用方法名去读会静默拿不到 —— 探针第一版就栽在这）
                var field = Il2CppReflection.FindIl2CppField(volume.GetIl2CppType(), "priority");
                var raw = field?.GetValue(volume);
                return raw == null ? 0f : Il2CppReflection.UnboxFloat(raw);
            }
            catch { return 0f; }
        }

        /// <summary>在这个 Volume 的 profile 里找 ColorAdjustments，再取它上面名为 paramName 的参数对象。</summary>
        private static Il2CppSystem.Object FindParameter(Il2CppSystem.Object volume, string paramName)
        {
            try
            {
                var type = volume.GetIl2CppType();
                var profile = Il2CppReflection.FindIl2CppField(type, "sharedProfile")?.GetValue(volume)
                              ?? Il2CppReflection.FindIl2CppField(type, "profile")?.GetValue(volume);
                if (profile == null) return null;

                var list = Il2CppReflection.FindIl2CppField(profile.GetIl2CppType(), "components")?.GetValue(profile);
                if (list == null) return null;

                int count = Int("get_Count", list);
                var getItem = Il2CppReflection.FindIl2CppMethod(list.GetIl2CppType(), "get_Item");
                if (getItem == null) return null;

                for (int i = 0; i < count && i < 24; i++)
                {
                    var component = getItem.Invoke(list, new Il2CppSystem.Object[] { Il2CppReflection.BoxInt(i) });
                    if (component == null) continue;
                    if (!IsKind(component, ColorAdjustmentsKind)) continue;

                    return Il2CppReflection.FindIl2CppField(component.GetIl2CppType(), paramName)?.GetValue(component);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 读后处理栈失败（" + paramName + "）: " + e.GetType().Name);
            }

            return null;
        }

        /// <summary>
        /// 认出这是不是某个后处理组件 —— 按**字段指纹**，不碰原生类名 API
        /// （`il2cpp_class_get_name` 在这套 Il2CppInterop 上的托管签名对不上）。
        /// </summary>
        private static bool IsKind(Il2CppSystem.Object component, string kind)
        {
            try
            {
                bool saturation = false, hueShift = false;
                var fields = component.GetIl2CppType().GetFields(
                    Il2CppSystem.Reflection.BindingFlags.Instance |
                    Il2CppSystem.Reflection.BindingFlags.Public |
                    Il2CppSystem.Reflection.BindingFlags.NonPublic);

                for (int i = 0; i < fields.Length; i++)
                {
                    var n = fields[i]?.Name;
                    if (n == "saturation") saturation = true;
                    else if (n == "hueShift") hueShift = true;
                }

                return kind == ColorAdjustmentsKind && saturation && hueShift;
            }
            catch { return false; }
        }

        private static int Int(string method, Il2CppSystem.Object target)
        {
            var m = Il2CppReflection.FindIl2CppMethod(target.GetIl2CppType(), method);
            var raw = m?.Invoke(target, System.Array.Empty<Il2CppSystem.Object>());
            return raw == null ? 0 : Il2CppReflection.UnboxInt(raw);
        }

        private static float Float(string method, Il2CppSystem.Object target)
        {
            var m = Il2CppReflection.FindIl2CppMethod(target.GetIl2CppType(), method);
            var raw = m?.Invoke(target, System.Array.Empty<Il2CppSystem.Object>());
            return raw == null ? 0f : Il2CppReflection.UnboxFloat(raw);
        }

        private static bool Bool(string method, Il2CppSystem.Object target)
        {
            var m = Il2CppReflection.FindIl2CppMethod(target.GetIl2CppType(), method);
            var raw = m?.Invoke(target, System.Array.Empty<Il2CppSystem.Object>());
            return raw != null && raw.Unbox<bool>();
        }
    }
}
