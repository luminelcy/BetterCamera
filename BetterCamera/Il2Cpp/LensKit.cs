using Il2CppInterop.Runtime;
using MelonLoader;

namespace BetterCamera.Il2Cpp
{
    /// <summary>
    /// 改 Cinemachine 相机的 LensSettings（FieldOfView / Dutch / NearClipPlane …）。
    ///
    /// LensSettings 是**结构体**，il2cpp 反射拿到的 `m_Lens` 是装箱后的副本，
    /// 所以必须「读出整块 → 改一个字段 → 整块写回」。只改副本不写回是静默失效，
    /// 没有任何报错 —— 这个坑在 4 个文件里各踩过一次，这里收成一处。
    /// </summary>
    public static class LensKit
    {
        private const string CameraTypeName = "Il2CppCinemachine.CinemachineVirtualCamera";
        private const string LensTypeName = "Il2CppCinemachine.LensSettings";

        private static Il2CppSystem.Reflection.FieldInfo _fLens;
        private static Il2CppSystem.Type _lensType;
        private static bool _ready;

        /// <summary>
        /// 解析一次并缓存。`_ready` 是 latch —— **失败不会重试**，所以失败必须报出来。
        ///
        /// 为什么不能只静默 return：依赖这条链的是 DutchSlider、DutchReset、
        /// NearClipAdjuster、ExitAdjuster 四处，解析不出来时它们的 EditFloat / ReadFloat
        /// 全部返回 false / NaN，而且**调用点全都丢弃返回值** —— 表现是"点了没反应"，
        /// 日志里零线索。ReadFloat 返回 NaN 还会让 SyncFromNative 的比值比较出怪结果。
        ///
        /// 注意日志只打在这里：EditFloat / ReadFloat 返回 false 有合法原因
        /// （相机已销毁之类），在那里打会刷屏。而这里因为 latch 只跑一次。
        /// </summary>
        private static void Ensure()
        {
            if (_ready) return;
            _ready = true;

            var camType = Game.NativeRefs.TypeOf(CameraTypeName);
            if (camType != null)
                _fLens = Il2CppReflection.FindIl2CppField(camType, "m_Lens");

            _lensType = Game.NativeRefs.TypeOf(LensTypeName);

            if (_fLens == null || _lensType == null)
                MelonLogger.Error("[BetterCamera] LensKit 解析失败（m_Lens=" + (_fLens != null)
                                  + " lensType=" + (_lensType != null) + "）—— "
                                  + "Dutch / NearClip / 退出复位这几处改相机镜头参数都不会生效");
        }

        /// <summary>
        /// 改相机 LensSettings 里的一个 float 字段。
        /// </summary>
        /// <param name="camera">NativeRefs.FindCamera 拿到的裸指针包装</param>
        /// <param name="lensFieldName">FieldOfView / Dutch / NearClipPlane / FarClipPlane …</param>
        public static bool EditFloat(Il2CppSystem.Object camera, string lensFieldName, float value)
        {
            if (camera == null) return false;
            Ensure();
            if (_fLens == null || _lensType == null) return false;

            var lens = _fLens.GetValue(camera);
            if (lens == null) return false;

            var field = Il2CppReflection.FindIl2CppField(_lensType, lensFieldName);
            if (field == null) return false;

            Il2CppReflection.SetFloatField(lens, field, value);
            _fLens.SetValue(camera, lens);   // ← 关键：整块写回，否则改动丢失
            return true;
        }

        /// <summary>读 LensSettings 里的一个 float 字段，取不到返回 NaN。</summary>
        public static float ReadFloat(Il2CppSystem.Object camera, string lensFieldName)
        {
            if (camera == null) return float.NaN;
            Ensure();
            if (_fLens == null || _lensType == null) return float.NaN;

            var lens = _fLens.GetValue(camera);
            if (lens == null) return float.NaN;

            var field = Il2CppReflection.FindIl2CppField(_lensType, lensFieldName);
            return Il2CppReflection.GetFloatField(lens, field);
        }
    }
}
