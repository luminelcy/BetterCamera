using System;
using MelonLoader;
using UnityEngine;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Game
{
    /// <summary>
    /// 写游戏 UI 上的 TextMeshPro 文字。
    ///
    /// 放在 Game/ 而不是 Il2Cpp/：它需要按类型名找组件，而查找在 NativeRefs 里，
    /// Il2Cpp/ 是它下面的一层，不该反向依赖。（SliderKit 在 Il2Cpp/ 是因为它只操作
    /// 传进来的对象，自己不找东西。）
    ///
    /// 下面三件事各自都踩过坑，所以合到一处 —— 之前它们埋在 ColorAdjustSliders 里，
    /// 第二个功能要用时才发现是 private：
    ///
    ///   1. TMP 组件不在 CommonLocalizeText 自己身上，在它的子节点上（实测叫 "Text (TMP)"）。
    ///      直接对 CommonLocalizeText 取 TextMeshProUGUI 会拿到 null，写入静默失效，
    ///      表现是标签一直显示克隆时从模板带过来的文字 —— 看着完全正常。
    ///   2. 那个节点上挂着 LocalizeStringEvent，会按游戏自己的 key 把文字覆盖回去，必须先关掉。
    ///      只关字符串事件，LocalizeTmpFontEvent 留着 —— 字体该跟着语言走，那正是我们要的。
    ///   3. 往 il2cpp 方法传 string 参数不能手工装箱，见 Il2CppReflection.WrapAsManaged。
    /// </summary>
    public static class TmpKit
    {
        /// <summary>
        /// 注意前缀：Il2CppInterop 会给会和 .NET 撞名的命名空间加 Il2Cpp（Project → Il2CppProject
        /// 也是同一回事），TMPro 在这个名单里。写 "TMPro.TextMeshProUGUI" 查不到，
        /// FindType 返回 null，然后一切静默失效。
        /// </summary>
        private const string TmpTypeName = "Il2CppTMPro.TextMeshProUGUI";

        private const string LocalizeStringEventTypeName =
            "UnityEngine.Localization.Components.LocalizeStringEvent";

        /// <summary>
        /// 在节点的子节点里找 TMP。
        ///
        /// 不写死子节点名：名字是随版本变的，而这个节点存在的意义就是"里面有个 TMP"。
        /// </summary>
        public static Il2CppSystem.Object FindText(Transform node)
        {
            if (node == null) return null;

            for (int i = 0; i < node.childCount; i++)
            {
                var tmp = NativeRefs.FindComponent(node.GetChild(i), TmpTypeName);
                if (tmp != null) return tmp;
            }
            return null;
        }

        /// <summary>
        /// 关掉子树里所有的 LocalizeStringEvent。
        ///
        /// 本 mod 的 UI 都是克隆来的，这些节点上的本地化事件指向的是模板的 key ——
        /// 它们会把我们写进去的文字覆盖回去。文字既然由本 mod 自己管，就得先把它们让开。
        /// </summary>
        public static void SilenceLocalization(Transform node)
        {
            if (node == null) return;

            for (int i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);

                var localizer = NativeRefs.FindComponent(child, LocalizeStringEventTypeName);
                if (localizer != null)
                {
                    try
                    {
                        Il2CppReflection.FindIl2CppMethod(localizer.GetIl2CppType(), "set_enabled")
                            ?.Invoke(localizer, new Il2CppSystem.Object[] { Il2CppReflection.BoxBool(false) });
                    }
                    catch { }
                }

                SilenceLocalization(child);
            }
        }

        /// <summary>
        /// 写文字。
        ///
        /// 走 Il2CppInterop 生成的托管包装：参数是普通 C# string，编组由它负责。
        /// 直接走 il2cpp 反射传手工装箱的字符串实测无效（见 Il2CppReflection.WrapAsManaged）。
        /// </summary>
        public static void SetText(Il2CppSystem.Object tmp, string text)
        {
            if (tmp == null) return;

            try
            {
                var wrapper = Il2CppReflection.WrapAsManaged(tmp, TmpTypeName);
                var textProperty = wrapper?.GetType().GetProperty("text");

                if (textProperty != null && textProperty.CanWrite)
                {
                    textProperty.SetValue(wrapper, text);
                    Verify(wrapper, textProperty, text);
                    return;
                }

                MelonLogger.Warning("[BetterCamera] 拿不到 TMP 的 text 属性，文字写不进去");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BetterCamera] 设置文字失败: " + e.Message);
            }
        }

        /// <summary>
        /// 回读确认。单看这步像是多余的，但"写进去了吗"这件事早先没有任何迹象：
        /// 写入走的是静默路径，失败时既不抛异常也不留痕，表现只是标签显示着
        /// 克隆时从模板带过来的文字 —— 看着完全正常。
        /// </summary>
        private static void Verify(object wrapper, System.Reflection.PropertyInfo textProperty, string expected)
        {
            if ((textProperty.GetValue(wrapper) as string) != expected)
                MelonLogger.Warning("[BetterCamera] 文字写入没有生效，界面上可能显示成别的");
        }
    }
}
