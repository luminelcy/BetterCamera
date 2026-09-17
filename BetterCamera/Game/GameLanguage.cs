using BetterCamera.Il2Cpp;
using MelonLoader;

namespace BetterCamera.Game
{
    /// <summary>
    /// 读游戏当前的语言码（"zh-Hans" / "en" 这种 BCP-47 形式）。
    ///
    /// 游戏用的是 Unity Localization 包，取法就是：
    ///     LocalizationSettings.SelectedLocale   （静态属性）
    ///       → Locale.Identifier
    ///         → LocaleIdentifier.Code
    ///
    /// 为什么不订阅 SelectedLocaleChanged：那是个静态事件，挂在 il2cpp 侧要转成托管委托，
    /// 得额外绕 DelegateSupport 并反射拼出 Action&lt;Locale&gt;；而语言切换本来就只有
    /// 设置界面那一下。改成调用方隔一会儿问一次，变了再重写文字
    /// （见 ColorAdjustSliders.SyncLanguage）。
    ///
    /// 取不到一律返回 null，调用方按"未知语言"处理 —— 文字那边会回落到英文。
    /// </summary>
    public static class GameLanguage
    {
        private static bool _resolved;
        private static Il2CppSystem.Reflection.MethodInfo _getSelectedLocale;
        private static Il2CppSystem.Reflection.MethodInfo _getIdentifier;
        private static Il2CppSystem.Reflection.MethodInfo _getCode;

        /// <summary>当前语言码；拿不到返回 null。</summary>
        public static string Current()
        {
            Resolve();

            if (_getSelectedLocale == null) return null;

            try
            {
                // 静态属性，实例参数传 null
                var locale = _getSelectedLocale.Invoke(null, null);
                if (locale == null) return null;

                // LocaleIdentifier 是 struct，反射调用时它是装箱的 —— il2cpp 支持在装箱值上
                // 调实例方法，所以这里不用手动拆箱
                var identifier = _getIdentifier?.Invoke(locale, null);
                if (identifier == null) return null;

                return _getCode?.Invoke(identifier, null)?.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// 解析一次并缓存。`_resolved` 是 latch —— **失败不会重试**，所以失败要报出来。
        ///
        /// 级别是警告不是错误：这条链断了只是文字回落到英文，mod 功能照常。
        /// 而且当前 ColorAdjustSliders 走的是写死的 FixedLanguage，这套链路是休眠的。
        ///
        /// 和 SliderKit / LensKit 一样，latch 让这段只跑一次，所以不会刷屏。
        /// </summary>
        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _getSelectedLocale = Il2CppReflection.FindIl2CppStaticMethod(
                NativeRefs.TypeOf("UnityEngine.Localization.LocalizationSettings"), "get_SelectedLocale");

            _getIdentifier = Il2CppReflection.FindIl2CppMethod(
                NativeRefs.TypeOf("UnityEngine.Localization.Locale"), "get_Identifier");

            _getCode = Il2CppReflection.FindIl2CppMethod(
                NativeRefs.TypeOf("UnityEngine.Localization.LocaleIdentifier"), "get_Code");

            if (_getSelectedLocale == null || _getIdentifier == null || _getCode == null)
                MelonLogger.Warning("[BetterCamera] 取不到 Unity Localization 的语言查询方法，"
                                    + "界面文字会一直用英文（其余功能不受影响）");
        }
    }
}
