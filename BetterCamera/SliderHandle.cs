using MelonLoader;
using UnityEngine;
using BetterCamera.Game;

namespace BetterCamera
{
    public static class SliderHandle
    {
        private const string ZoomHandlePath =
            GamePaths.NativeZoomHandle;

        private const string FooterCenterPath =
            GamePaths.FooterCenter;

        private const string SwitchCameraPath =
            GamePaths.NativeSwitchCameraButton;

        private const string BodyRightPath =
            GamePaths.BodyRight;

        private const string ShowRoomStatesPath =
            GamePaths.NativeShowRoomStatesButton;

        private const string CommonButtonPath =
            GamePaths.NativeCommonButton;

        public static void Init(MelonLogger.Instance logger)
        {
            var original = GameObject.Find(ZoomHandlePath);

            // 这个守卫一度被注释掉。恢复它的理由：下面第一件事就是 original.transform，
            // 为空时抛 NRE，Init 从这里中断 —— 而 Core 的初始化是串行的，那会连带把
            // 后面所有步骤一起带走。路径一旦对不上（游戏改层级），整条链路全废。
            // 守卫本身就把原因写清楚了，日志里能直接看到是哪个路径找不到。
            if (original == null)
            {
                logger.Error($"Cannot find P_ZoomHandleObject at path: {ZoomHandlePath}");
                return;
            }

            var clone0 = UnityEngine.Object.Instantiate(original, original.transform.parent);
            clone0.name = GamePaths.NameBcZoomHandle;

            var clone1 = UnityEngine.Object.Instantiate(original, original.transform.parent);
            clone1.name = GamePaths.NameBcFocusHandle;
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
            clone2.name = GamePaths.NameBcDutchHandle;
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
                    cloneSwitch.name = GamePaths.NameBcSwitchModeButton;
                    cloneSwitch.transform.localPosition = new Vector3(-220f, 205f, 0f);
                    var iconZoomIn = cloneSwitch.transform.Find("CircleIconButton/IconZoomIn");
                    if (iconZoomIn != null) UnityEngine.Object.Destroy(iconZoomIn.gameObject);

                    var cloneSwitch1 = UnityEngine.Object.Instantiate(switchCamOriginal, bodyRight.transform);
                    cloneSwitch1.name = GamePaths.NameBcFocusModeButton1;
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
                    cloneShowRoom.name = GamePaths.NameBcShowRoomStates;
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
                    cloneCommonBtn.name = GamePaths.NameBcCommonButton;
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
            if (root == null) return null;

            Transform current = root.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null)
                {
                    // 只在路径确实找不到时打一行 —— 原来是逐级 + 列出全部子节点名，
                    // 那是开发期调试用的，对用户是噪音
                    logger.Warning($"[BetterCamera] 未找到 UI 路径: {path}");
                    return null;
                }
            }
            return current.gameObject;
        }
    }
}
