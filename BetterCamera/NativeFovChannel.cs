using System;
using UnityEngine;
using Il2CppProject;
using Il2CppProject.HomeScene.RoomScene;
using Il2CppTanitakaTech.StateVariable;

namespace BetterCamera
{
    /// <summary>
    /// 通往原生 RoomCameraFOV 响应式变量的唯一入口。
    ///
    /// 改造前 mod 各处直接写相机的 m_Lens.FieldOfView，绕过游戏自己的状态，
    /// 结果是滚轮/键盘一按 FOV 就跳回旧值、滑条手柄也不跟随。
    ///
    /// 改造后统一走这里：写变量的值，由游戏自己的
    ///     RoomCameraController 的 observer → UpdateFOV → 写 m_Lens + 发布 lensSettingsSetter
    /// 去落实。这样滚轮、键盘、滑条、退出重置全部作用于同一个值，互不打架。
    ///
    /// 配合 FovClampHook（把原生钳制边界 40/80 换成 mod 的 20/120），
    /// 变量才能真正接受扩宽后的范围。
    /// </summary>
    public static class NativeFovChannel
    {
        public const string CameraPath =
            "SceneContext/P_RoomCameraObject/VirtualCameras/DefaultVirtualCamera";

        private static RoomCameraController _ctrl;
        private static IVariableSetter<RoomCameraFOV> _setter;

        public static bool IsReady => _setter != null;
        public static RoomCameraController Controller => _ctrl;

        /// <summary>
        /// 懒加载。_roomCameraFOVSetter 是 Zenject 注入的，场景初始化时可能还没就绪
        /// （实测约 0.5 秒），所以调用方要能接受 false 并稍后重试。
        /// </summary>
        public static bool Ensure()
        {
            if (_ctrl == null)
            {
                var camObj = GameObject.Find(CameraPath);
                if (camObj == null) return false;
                _ctrl = camObj.GetComponentInParent<RoomCameraController>();
                if (_ctrl == null) return false;
            }

            if (_setter == null)
            {
                try { _setter = _ctrl._roomCameraFOVSetter; } catch { }
            }

            return _setter != null;
        }

        /// <summary>
        /// 写原生 FOV 变量。会走游戏自己的 UpdateFOV（改 m_Lens 并发布 lensSettingsSetter），
        /// 所以相机、响应式变量、原生 UI 三者保持一致。
        /// </summary>
        public static bool Set(float fov)
        {
            if (!Ensure()) return false;
            try
            {
                _setter.Set(new RoomCameraFOV(fov));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 读相机当前 FOV。m_Lens 是游戏 UpdateFOV 的落点，反映的是变量被钳制后的实际值，
        /// 因此它同时也是"手柄该停在哪儿"的依据。
        /// </summary>
        public static bool TryGetCurrent(out float fov)
        {
            fov = 0f;
            if (!Ensure()) return false;
            try
            {
                var cam = _ctrl._defaultVirtualCamera;
                if (cam == null) return false;
                fov = cam.m_Lens.FieldOfView;
                return true;
            }
            catch { return false; }
        }

        /// <summary>场景卸载时清掉缓存，避免持有已销毁对象的引用。</summary>
        public static void Reset()
        {
            _ctrl = null;
            _setter = null;
        }
    }
}
