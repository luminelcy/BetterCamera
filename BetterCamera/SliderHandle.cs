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

        private const string ShowRoomStatesPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_RoomTopRightMenuObject/Adjust/ViewGroup/P_ShowRoomStatesButtonObject";

        private const string CommonButtonPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Footer/Right/PoseAndRotateUI/P_SwitchRotateAvatarButtonObject/CommonButton";

        public static void Init(MelonLogger.Instance logger)
        {
            var original = GameObject.Find(ZoomHandlePath);
            // if (original == null)
            // {
            //     logger.Error($"Cannot find P_ZoomHandleObject at path: {ZoomHandlePath}");
            //     return;
            // }

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

            var showRoomStatesOriginal = GameObject.Find(ShowRoomStatesPath);
            if (showRoomStatesOriginal == null)
                showRoomStatesOriginal = FindInactiveByPath(ShowRoomStatesPath, logger);
            
            if (showRoomStatesOriginal != null)
            {
                var footerCenter2 = GameObject.Find(FooterCenterPath);
                if (footerCenter2 != null)
                {
                    var cloneShowRoom = UnityEngine.Object.Instantiate(showRoomStatesOriginal, footerCenter2.transform);
                    cloneShowRoom.name = "P_BetterCameraShowRoomStates";
                    cloneShowRoom.SetActive(true);
                    cloneShowRoom.transform.localPosition = new Vector3(0f, 150f, 0f);
                    cloneShowRoom.transform.localScale = new Vector3(0.9f, 0.9f, 0.9f);
                    var hoveredTips = cloneShowRoom.transform.Find("P_HoveredTipsObject");
                    if (hoveredTips != null) UnityEngine.Object.Destroy(hoveredTips.gameObject);
                }
                else logger.Error($"Cannot find Footer/Center at path: {FooterCenterPath}");
            }

            var commonButtonOriginal = GameObject.Find(CommonButtonPath);
            if (commonButtonOriginal != null)
            {
                var footerCenter3 = GameObject.Find(FooterCenterPath);
                if (footerCenter3 != null)
                {
                    var cloneCommonBtn = UnityEngine.Object.Instantiate(commonButtonOriginal, footerCenter3.transform);
                    cloneCommonBtn.name = "P_BetterCameraCommonButton";
                    cloneCommonBtn.transform.localPosition = new Vector3(0f, 150f, 0f);
                    cloneCommonBtn.transform.localScale = new Vector3(2f, 2f, 2f);
                    cloneCommonBtn.transform.localEulerAngles = new Vector3(0f, 180f, 90f);
                }
                else logger.Error($"Cannot find Footer/Center at path: {FooterCenterPath}");
            }
            else logger.Error($"Cannot find CommonButton at path: {CommonButtonPath}");

            UnityEngine.Object.Destroy(original);

            // logger.Msg("Successfully cloned P_ZoomHandleObject twice!");
        }

        private static GameObject FindInactiveByPath(string path, MelonLogger.Instance logger)
        {
            var parts = path.Split('/');
            var root = GameObject.Find(parts[0]);
            if (root == null) { logger.Msg($"FindInactiveByPath: root '{parts[0]}' not found"); return null; }

            Transform current = root.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null)
                {
                    logger.Msg($"FindInactiveByPath: '{parts[i]}' not found under '{string.Join("/", parts, 0, i)}'");
                    // 列出实际的子对象名称帮助调试
                    var parent = root.transform;
                    for (int j = 1; j < i; j++) parent = parent.Find(parts[j]);
                    var children = new System.Collections.Generic.List<string>();
                    for (int c = 0; c < parent.childCount; c++)
                        children.Add(parent.GetChild(c).name);
                    logger.Msg($"FindInactiveByPath: children of '{string.Join("/", parts, 0, i)}': [{string.Join(", ", children)}]");
                    return null;
                }
            }
            return current.gameObject;
        }
    }
}
