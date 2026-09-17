using System;
using System.Collections.Generic;
using System.Reflection;
using BetterCamera.Game;
using MelonLoader;
using BetterCamera.Il2Cpp;

namespace BetterCamera
{
    /// <summary>
    /// 【临时诊断，定位完连同 Core/csproj 的注册一起删】把拍照场景里的后处理栈整个打出来。
    ///
    /// 为什么要它：玩家报告"ColorAdjust 滑条显示的 0 和游戏实际的观感对不上"。
    /// URP 是**按优先级混合**所有已加载 Volume 的（同一参数只取优先级最高的那个 override），
    /// 而我们只绑了 BaseVolume 上的 ColorAdjustments —— 如果决定画面的是另一个 Volume 上的实例，
    /// 那"滑条 = 0"和"画面饱和度 ≠ 0"就自然是两回事。静态看不出来，必须把栈读出来。
    ///
    /// 输出形状（每个 Volume 一行 + 每个参数一行）：
    ///   [colorstack] Volume "BaseVolume" prio=100 weight=1 enabled=True profile="RoomSnapSceneBaseVolumeProfile"
    ///   [colorstack]   ColorAdjustments: saturation(ov=False,v=0) contrast(ov=False,v=0) …
    ///   [colorstack]   WhiteBalance: temperature(ov=True,v=20) …
    /// 判读：**第一个 ov=True 的参数就是决定画面的那个**（优先级高的排在前面）。
    /// </summary>
    internal static class ColorStackProbe
    {
        private const string VolumeTypeName = "UnityEngine.Rendering.Volume";

        /// <summary>只打这些组件 —— 后处理的观感基本都由它们决定。</summary>
        private static readonly string[] Interesting = { "ColorAdjustments", "WhiteBalance", "ColorLookup", "Tonemapping", "LiftGammaGain", "ShadowsMidtonesHighlights" };

        private static bool _done;

        /// <summary>
        /// 要读的 Volume。按**优先级从高到低**排 —— 同一个参数只取优先级最高的那个 override，
        /// 所以"第一个 ov=True 的参数"就是最终决定画面的那个。
        /// （这四条是从运行中的桥里数出来的：拍照场景 2 条 + 仍在加载的房间场景 2 条。）
        /// </summary>
        private static readonly string[] VolumePaths =
        {
            "SceneContext/System/P_RoomSnapPostProcessObject/EffectVolume",
            "SceneContext/System/P_RoomSnapPostProcessObject/BaseVolume",
            "SceneContext/Volume",
            "SceneContext/P_RoomEnvironmentObject/Volume",
        };

        public static void Dump()
        {
            if (_done) return;      // 每个进程一次就够：值不会自己变（我们没在写）
            _done = true;

            foreach (var path in VolumePaths)
            {
                try
                {
                    // 房间里那两个只在房间场景加载时存在；拍照场景里 Find 不到就跳过
                    var volume = NativeRefs.FindComponent(path, VolumeTypeName);
                    if (volume == null) continue;

                    DumpVolume(volume);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[colorstack] 读 " + path + " 失败: " + e.GetType().Name);
                }
            }
        }

        private static void DumpVolume(Il2CppSystem.Object volume)
        {
            try
            {
                var type = volume.GetIl2CppType();
                var name = NameOf(volume);
                var profile = Il2CppReflection.FindIl2CppField(type, "sharedProfile")?.GetValue(volume)
                              ?? Il2CppReflection.FindIl2CppField(type, "m_InternalProfile")?.GetValue(volume)
                              ?? Il2CppReflection.FindIl2CppField(type, "profile")?.GetValue(volume);

                MelonLogger.Msg("[colorstack] Volume \"" + name + "\""
                                + " prio=" + Int("get_priority", volume)
                                + " weight=" + Float("get_weight", volume)
                                + " profile=" + (profile == null ? "(null)" : NameOf(profile)));

                if (profile == null) return;

                var list = Il2CppReflection.FindIl2CppField(profile.GetIl2CppType(), "components")?.GetValue(profile);
                if (list == null) return;

                int count = Int("get_Count", list);
                var getItem = Il2CppReflection.FindIl2CppMethod(list.GetIl2CppType(), "get_Item");
                if (getItem == null) return;

                for (int i = 0; i < count && i < 24; i++)
                {
                    var component = getItem.Invoke(list, new Il2CppSystem.Object[] { Il2CppReflection.BoxInt(i) });
                    if (component == null) continue;

                    string kind = KindOf(component);
                    if (kind == null) continue;

                    MelonLogger.Msg("[colorstack]   " + kind + ": " + DescribeParams(component));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[colorstack] 读某个 Volume 失败: " + e.GetType().Name);
            }
        }

        /// <summary>把组件上所有「参数类」字段读成 name(ov=?,v=?) 的形式。</summary>
        private static string DescribeParams(Il2CppSystem.Object component)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var type = component.GetIl2CppType();

                var fields = type.GetFields(
                    Il2CppSystem.Reflection.BindingFlags.Instance |
                    Il2CppSystem.Reflection.BindingFlags.Public |
                    Il2CppSystem.Reflection.BindingFlags.NonPublic);

                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field == null) continue;

                    var fieldTypeName = field.FieldType == null ? "" : field.FieldType.Name;
                    if (fieldTypeName.IndexOf("Parameter", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var param = field.GetValue(component);
                    if (param == null) continue;

                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(field.Name)
                      .Append("(ov=").Append(Bool("get_overrideState", param))
                      .Append(",v=").Append(Float("get_value", param))
                      .Append(')');
                }

                return sb.Length == 0 ? "(没有参数类字段)" : sb.ToString();
            }
            catch (Exception e)
            {
                return "(读参数失败: " + e.GetType().Name + ")";
            }
        }

        // ---- 小工具 ----

        /// <summary>
        /// 认出这是哪个后处理组件 —— **按字段指纹**，不碰原生类名 API
        /// （`il2cpp_class_get_name` 在这套 Il2CppInterop 上的托管签名对不上，试过不通）。
        /// </summary>
        private static string KindOf(Il2CppSystem.Object component)
        {
            try
            {
                var type = component.GetIl2CppType();
                bool saturation = false, hueShift = false, temperature = false, texture = false, tonemapping = false;

                var fields = type.GetFields(
                    Il2CppSystem.Reflection.BindingFlags.Instance |
                    Il2CppSystem.Reflection.BindingFlags.Public |
                    Il2CppSystem.Reflection.BindingFlags.NonPublic);

                for (int i = 0; i < fields.Length; i++)
                {
                    var n = fields[i]?.Name;
                    if (n == null) continue;
                    if (n == "saturation") saturation = true;
                    else if (n == "hueShift") hueShift = true;
                    else if (n == "temperature") temperature = true;
                    else if (n == "texture") texture = true;
                    else if (n == "mode") tonemapping = true;      // Tonemapping 的独有字段
                }

                if (saturation && hueShift) return "ColorAdjustments";
                if (temperature) return "WhiteBalance";
                if (texture) return "ColorLookup";
                if (tonemapping) return "Tonemapping";
                return null;
            }
            catch { return null; }
        }

        private static string NameOf(Il2CppSystem.Object obj)
        {
            try
            {
                var wrapped = Il2CppReflection.WrapAsManaged(obj, "UnityEngine.Object");
                var name = wrapped?.GetType().GetProperty("name")?.GetValue(wrapped) as string;
                return string.IsNullOrEmpty(name) ? "(无名)" : name;
            }
            catch { return "(无名)"; }
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
            return raw == null ? float.NaN : Il2CppReflection.UnboxFloat(raw);
        }

        private static bool Bool(string method, Il2CppSystem.Object target)
        {
            var m = Il2CppReflection.FindIl2CppMethod(target.GetIl2CppType(), method);
            var raw = m?.Invoke(target, System.Array.Empty<Il2CppSystem.Object>());
            return raw != null && raw.Unbox<bool>();
        }
    }
}
