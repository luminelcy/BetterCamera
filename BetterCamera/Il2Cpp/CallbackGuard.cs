using System;
using System.Collections.Generic;
using MelonLoader;

namespace BetterCamera.Il2Cpp
{
    /// <summary>
    /// 挂在 UnityEvent（onClick / onValueChanged）上的回调的兜底。
    ///
    /// **为什么必须兜**：这些回调的异常会顺着 `UnityEvent.Invoke` 往外冒，后果有两层 ——
    ///
    ///   1. 打断**同一条事件上其它监听者**：Invoke 是遍历监听者列表调用，
    ///      中途抛出，排在后面的那个就永远轮不到。
    ///   2. 打断**这一次 UI 输入处理**：异常继续冒到 UI 事件链，Unity 记一条日志，
    ///      那一帧剩下的 UI 事件被跳过。
    ///
    /// 作者在 `CaptureSizePresets.GuardedSelect` 上踩过并把这个现象写在了注释里
    /// （"点了这个之后别的按钮也不响应"），但只在那一处包了 —— 其余几个回调
    /// （FxSlider / DutchSlider / ExitAdjuster / DutchReset）都是裸的。
    ///
    /// **为什么不做在 UnityEventBridge 里统一包**：那需要把注册的回调换成闭包，
    /// 而 FxSlider.cs 的注释写明作者刻意用静态方法、避开闭包，理由是
    /// `DelegateSupport.ConvertDelegate` 对闭包这条路没验证过。换闭包有让回调
    /// **整个失效**的风险，不值得为一个兜底去赌。所以退一步：各回调自己在函数体
    /// 内部包，委托本身仍然是静态方法，注册方式一个字不变。
    ///
    /// 用法（注意 catch 里只调 Warn，别再抛）：
    ///
    ///     try { ...原函数体... }
    ///     catch (Exception e) { CallbackGuard.Warn("XxxYyy.OnChanged", e); }
    ///
    /// 正常路径零开销、零日志。
    /// </summary>
    internal static class CallbackGuard
    {
        /// <summary>已经报过的站名。滑条回调失败是每次拖动都复现的，不设限会刷屏。</summary>
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>记一条警告，同一个站名只记一次。</summary>
        public static void Warn(string site, Exception e)
        {
            if (!Reported.Add(site)) return;

            MelonLogger.Warning("[BetterCamera] " + site + " 的回调抛异常（已吞掉，"
                                + "避免打断 Unity 的输入处理）: " + e.GetType().Name + ": " + e.Message);
        }
    }
}
