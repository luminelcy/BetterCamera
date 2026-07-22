using MelonLoader;
using UnityEngine;

namespace BetterCamera
{
    public static class SliderHandle
    {
        private const string ZoomHandlePath =
            "SceneContext/CommonCanvas/UIPartsGroup/Body/Right/P_ZoomHandleObject";

        private const string FooterCenterPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Footer/Center";

        private const string SwitchCameraPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/RightIconBackground/P_SwitchCameraFocusModeButtonObject";

        private const string BodyRightPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Body/Right";

        public static void Init(MelonLogger.Instance logger)
        {
            var original = GameObject.Find(ZoomHandlePath);
            if (original == null)
            {
                logger.Error($"Cannot find P_ZoomHandleObject at path: {ZoomHandlePath}");
                return;
            }

            var clone0 = UnityEngine.Object.Instantiate(original, original.transform.parent);
            clone0.name = "P_BetterCameraHandleObject0";

            var clone1 = UnityEngine.Object.Instantiate(original, original.transform.parent);
            clone1.name = "P_BetterCameraHandleObject1";
            var rt1 = clone1.GetComponent<RectTransform>();
            rt1.anchoredPosition = new Vector2(rt1.anchoredPosition.x - 65f, rt1.anchoredPosition.y);

            var zoomInIcon1 = clone1.transform.Find("ZoomInIcon");
            if (zoomInIcon1 != null) zoomInIcon1.localScale = Vector3.zero;
            var zoomOutIcon1 = clone1.transform.Find("ZoomOutIcon");
            if (zoomOutIcon1 != null) zoomOutIcon1.localScale = Vector3.zero;

            var footerCenter = GameObject.Find(FooterCenterPath);
            if (footerCenter == null)
            {
                logger.Error($"Cannot find Footer/Center at path: {FooterCenterPath}");
                return;
            }

            var clone2 = UnityEngine.Object.Instantiate(original, footerCenter.transform);
            clone2.name = "P_BetterCameraHandleObject2";
            clone2.transform.localPosition = new Vector3(0f, 80f, 0f);
            clone2.transform.localEulerAngles = new Vector3(0f, 0f, 270f);

            var zoomInIcon2 = clone2.transform.Find("ZoomInIcon");
            if (zoomInIcon2 != null) zoomInIcon2.localScale = Vector3.zero;
            var zoomOutIcon2 = clone2.transform.Find("ZoomOutIcon");
            if (zoomOutIcon2 != null) zoomOutIcon2.localScale = Vector3.zero;

            var handle2 = clone2.transform.Find("Slider/Slider/Handle Slide Area");
            if (handle2 != null)
            {
                var handleRt = handle2.GetComponent<RectTransform>();
                if (handleRt != null) handleRt.sizeDelta = new Vector2(578f, 0f);
            }
            var bar2 = clone2.transform.Find("Slider/Bar");
            if (bar2 != null) bar2.localScale = new Vector3(1f, 2.895f, 1f);

            var switchCamOriginal = GameObject.Find(SwitchCameraPath);
            if (switchCamOriginal != null)
            {
                var bodyRight = GameObject.Find(BodyRightPath);
                if (bodyRight != null)
                {
                    var cloneSwitch = UnityEngine.Object.Instantiate(switchCamOriginal, bodyRight.transform);
                    cloneSwitch.name = "P_BetterCameraSwitchModeButton";
                    cloneSwitch.transform.localPosition = new Vector3(-220f, 205f, 0f);
                    var iconZoomIn = cloneSwitch.transform.Find("CircleIconButton/IconZoomIn");
                    if (iconZoomIn != null) UnityEngine.Object.Destroy(iconZoomIn.gameObject);

                    var cloneSwitch1 = UnityEngine.Object.Instantiate(switchCamOriginal, bodyRight.transform);
                    cloneSwitch1.name = "P_SwitchCameraFocusModeButtonObject1";
                    cloneSwitch1.transform.localPosition = new Vector3(-220f, -201f, 0f);
                    var iconZoomOut = cloneSwitch1.transform.Find("CircleIconButton/IconZoomOut");
                    if (iconZoomOut != null) UnityEngine.Object.Destroy(iconZoomOut.gameObject);
                }
                else logger.Error($"Cannot find Body/Right at path: {BodyRightPath}");
            }
            else logger.Error($"Cannot find SwitchCamera at path: {SwitchCameraPath}");

            UnityEngine.Object.Destroy(original);

            logger.Msg("Successfully cloned P_ZoomHandleObject twice!");
        }
    }
}
