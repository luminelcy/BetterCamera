namespace BetterCamera.Features
{
    /// <summary>本 mod 新增 UI 上要显示的文字。取值顺序即下表的列序。</summary>
    public enum LabelKey
    {
        Hue = 0,
        Saturation = 1,
        Contrast = 2,
        ColorTab = 3,
    }

    /// <summary>
    /// ColorAdjust 面板的文字表。
    ///
    /// 为什么不走游戏自己的本地化：游戏的 key（LocalizeTextKey）是个包着字符串的 struct，
    /// 条目存在 Unity Localization 的 StringTable 里 —— 加新条目要在运行时改引擎的数据，
    /// 能做，但整条链都是反射且没法离线验证。这里用一张自发维护的小表，
    /// 代价是没写的语言回落到英文。
    ///
    /// 只做中英两种（zh 含简繁），其余语言一律英文。
    /// </summary>
    public static class ColorAdjustLabels
    {
        // 每张表的下标必须和 LabelKey 一一对应（Hue / Saturation / Contrast / ColorTab）
        private static readonly string[] English = { "Hue", "Saturation", "Contrast", "Color" };
        private static readonly string[] Simplified = { "色相", "饱和度", "对比度", "颜色" };
        private static readonly string[] Traditional = { "色相", "飽和度", "對比度", "顏色" };

        public static string Get(LabelKey key, string languageCode)
            => Pick(languageCode)[(int)key];

        /// <summary>
        /// 语言码是 BCP-47：简体 "zh-Hans"、繁体 "zh-Hant"、英文 "en"。
        /// 认不出的（含异常值）一律当英文。
        /// </summary>
        private static string[] Pick(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode)) return English;
            if (!languageCode.StartsWith("zh")) return English;

            return languageCode.Contains("Hant") ? Traditional : Simplified;
        }
    }
}
