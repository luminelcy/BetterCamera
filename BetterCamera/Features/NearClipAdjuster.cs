using BetterCamera.Game;
using BetterCamera.Il2Cpp;

namespace BetterCamera.Features
{
    /// <summary>
    /// 把房间相机的近裁剪面改小，让玩家能贴近物体拍摄。
    ///
    /// 原生值较大（贴近就会被裁掉），改成 0.01。这是纯 mod 扩展 ——
    /// 游戏没有对应的原生通道，也不会自己改这个值，所以不存在冲突。
    /// </summary>
    public static class NearClipAdjuster
    {
        private const string LensField = "NearClipPlane";
        private const float NearClip = 0.01f;

        public static void Init()
        {
            var cam = NativeRefs.FindCamera(GamePaths.DefaultVirtualCamera);
            if (cam == null) return;

            // 已经是目标值就不重复做结构体拷贝
            float current = LensKit.ReadFloat(cam, LensField);
            if (!float.IsNaN(current) && current <= NearClip) return;

            LensKit.EditFloat(cam, LensField, NearClip);
        }
    }
}
