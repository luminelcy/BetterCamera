using System;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 「重置 Dutch」按钮：把相机侧倾归零。
    ///
    /// 滑条手柄的归零不在这里做 —— DutchSlider 每帧从 m_Lens.Dutch 同步手柄位置，
    /// 所以这里改完相机，下一帧手柄自然跟过去。
    ///
    /// （原实现是直接调 DutchSlider.ResetSliderValue()，属于功能模块之间的反向依赖。
    /// 改成每帧同步后，这个依赖就没有存在的必要了，顺带也让手柄能跟随任何外部改动。）
    /// </summary>
    public static class DutchReset
    {
        private const string LensFieldDutch = "Dutch";

        private static Il2CppSystem.Object _camera;

        public static void Init()
        {
            _camera = NativeRefs.FindCamera(GamePaths.DefaultVirtualCamera);

            var button = NativeRefs.FindButton(GamePaths.BcShowRoomStatesButton);
            if (button != null)
                UnityEventBridge.AddClickListener(button, ResetDutch);
        }

        private static void ResetDutch()
        {
            // 挂在按钮 onClick 上，异常会打断同一条事件上排在后面的监听者 ——
            // 必须自己吞（见 CallbackGuard 的说明）。
            try
            {
                LensKit.EditFloat(_camera, LensFieldDutch, 0f);
            }
            catch (Exception e)
            {
                CallbackGuard.Warn("DutchReset.ResetDutch", e);
            }
        }
    }
}
