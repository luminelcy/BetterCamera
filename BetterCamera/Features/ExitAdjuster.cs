using System;
using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 点返回按钮离开拍照场景时，把相机恢复到一个合理的状态：
    /// Dutch = 0、NearClipPlane = 0.1、FOV 收回原生范围（&gt;80→80，&lt;40→40）。
    ///
    /// ⚠️ 顺序有讲究。RoomCameraController.UpdateFOV 是
    /// 「整块读 m_Lens → 只改 FieldOfView → 整块写回」，
    /// 所以必须先落盘 Dutch / NearClipPlane，再改 FOV —— 反过来会被它的整块写回覆盖掉
    /// （它读到的是修改前的旧值）。
    /// </summary>
    public static class ExitAdjuster
    {
        private const string LensFieldFov = "FieldOfView";
        private const string LensFieldDutch = "Dutch";
        private const string LensFieldNearClip = "NearClipPlane";

        private const float ResetDutch = 0f;
        private const float ResetNearClip = 0.1f;
        private const float FovMin = 40f;
        private const float FovMax = 80f;

        private static Il2CppSystem.Object _camera;

        public static void Init()
        {
            _camera = NativeRefs.FindCamera(GamePaths.DefaultVirtualCamera);

            var button = NativeRefs.FindButton(GamePaths.RoomSnapBackButton);
            if (button != null)
                UnityEventBridge.AddClickListener(button, OnQuit);
        }

        private static void OnQuit()
        {
            // 挂在返回按钮的 onClick 上。这里必须自己兜住异常：Invoke 是遍历监听者
            // 调用的，我们抛出会**打断排在后面的监听者** —— 如果游戏的退场处理恰好
            // 排在我们后面，那就是"按了返回没反应"。LensKit 那几处反射读在 _camera
            // 失效时会抛，所以这不是理论风险。
            try
            {
                if (_camera == null) return;

                // ① 先落盘 Dutch / NearClipPlane。
                //    LensKit 每次都是完整的「读整块 → 改一个字段 → 写回」，
                //    所以两次 EditFloat 会正确叠加。
                LensKit.EditFloat(_camera, LensFieldDutch, ResetDutch);
                LensKit.EditFloat(_camera, LensFieldNearClip, ResetNearClip);

                // ② 再把 FOV 收回原生范围 —— 走原生变量而不是直写 m_Lens，
                //    这样滑条、响应式变量、相机三者保持同步。
                float fov = LensKit.ReadFloat(_camera, LensFieldFov);
                if (float.IsNaN(fov)) return;

                float clamped = fov;
                if (clamped > FovMax) clamped = FovMax;
                else if (clamped < FovMin) clamped = FovMin;

                if (clamped != fov)
                    NativeFovChannel.Set(clamped);
            }
            catch (Exception e)
            {
                CallbackGuard.Warn("ExitAdjuster.OnQuit", e);
            }
        }
    }
}
