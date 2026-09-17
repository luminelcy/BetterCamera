using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace BetterCamera
{
    /// <summary>
    /// 【临时诊断，定位完整文件删】用文件当"远程开关"，方便二分定位卡死。
    ///
    /// 用法：在 `&lt;游戏根&gt;/Mods/BetterCamera-probe.txt` 里写开关名，一行一个：
    ///     native-state   —— 点我们的预设时，推给游戏的状态用 Portrait（而不是我们的键）
    ///     no-dict        —— 不把 4 个开关注册进游戏的字典
    ///     no-croparg     —— 不改写 b__10_0 的入参
    ///     no-cropsub     —— 不把游戏写死的 9:16 换成我们的比例
    ///     no-output      —— 不改写出片入口桩的参数
    /// 文件不存在 / 空 = 所有开关关闭 = 正常行为。
    ///
    /// 为什么用文件而不是重新编译：卡死现在是**确定性复现**，二分只要"改文件 + 重进场景"，
    /// 一轮就能排除一个嫌疑，比每次重编快得多。每个场景开始时会重新读一次。
    /// </summary>
    internal static class ProbeFlags
    {
        private const string FileName = "BetterCamera-probe.txt";

        private static readonly HashSet<string> On = new HashSet<string>();
        private static bool _loaded;
        private static string _path;

        /// <summary>重新读一次（进场景时调）。</summary>
        public static void Reload()
        {
            _loaded = false;
            On.Clear();
        }

        public static bool Has(string name)
        {
            if (!_loaded) Load();
            return On.Contains(name);
        }

        private static void Load()
        {
            _loaded = true;

            try
            {
                // 模组跑在游戏进程里，进程基目录就是游戏根
                var root = AppDomain.CurrentDomain.BaseDirectory ?? "";
                _path = Path.Combine(Path.Combine(root, "Mods"), FileName);
                if (!File.Exists(_path)) return;

                foreach (var raw in File.ReadAllLines(_path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    On.Add(line);
                }

//                 MelonLogger.Msg("[probe] 生效的开关: "
//                                 + (On.Count == 0 ? "（无）" : string.Join("、", On)));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[probe] 读 " + _path + " 失败: " + e.GetType().Name);
            }
        }
    }
}
