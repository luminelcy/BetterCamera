using Il2CppInterop.Runtime;

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

        private static void Ensure()
        {
            if (_ready) return;
            _ready = true;

            var camType = Game.NativeRefs.TypeOf(CameraTypeName);
            if (camType != null)
                _fLens = Il2CppReflection.FindIl2CppField(camType, "m_Lens");

            _lensType = Game.NativeRefs.TypeOf(LensTypeName);
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
