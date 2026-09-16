namespace BetterCamera.Game
{
    /// <summary>
    /// 游戏里所有被本 mod 触碰的 GameObject 路径，集中一处。
    ///
    /// 之前这些字面量散落在 10 个文件里（同一个相机路径就硬编码了 5 次），
    /// 而查找失败是**静默**的 —— 路径写错或游戏改了层级，各处 `if (obj == null) return;`
    /// 直接吞掉，没有任何报错，表现为"功能莫名其妙不见了"。
    /// 集中之后至少改一处、看一处。
    /// </summary>
    public static class GamePaths
    {
        // 反复出现的前缀，抽出来只为了下面读起来短一些；拼出来的字符串与原先逐字一致
        private const string Canvas = "SceneContext/CommonCanvas/UIPartsGroup";
        private const string FilterMenu = Canvas + "/Header/P_FilterMenuObject/Body/ControlsLayout";

        // ================= 相机 =================

        public const string CameraObject = "SceneContext/P_RoomCameraObject";
        public const string DefaultVirtualCamera = CameraObject + "/VirtualCameras/DefaultVirtualCamera";

        /// <summary>
        /// 挂着 FocusClickedObjectController 的对象。那个组件持有 DepthOfField 的引用，
        /// 是 mod 改焦点距离的唯一入口。
        ///
        /// 两个候选路径：游戏在不同版本里换过一次位置，先试主路径再退回备选。
        /// </summary>
        public const string FocusController = "SceneContext/Volume";
        public const string FocusControllerFallback = "SceneContext/Systems/FocusCameraSwitcher";

        // ================= 原生 UI：克隆源 =================
        // NativeUiFactory 从这里 Instantiate 出本 mod 自己的 UI

        public const string NativeZoomHandle = Canvas + "/Body/Right/P_ZoomHandleObject";
        public const string NativeSwitchCameraButton =
            Canvas + "/Header/RightIconBackground/P_SwitchCameraFocusModeButtonObject";
        public const string NativeShowRoomStatesButton =
            Canvas + "/Header/P_RoomTopRightMenuObject/Adjust/ViewGroup/P_ShowRoomStatesButtonObject";
        public const string NativeCommonButton =
            Canvas + "/Footer/Right/PoseAndRotateUI/P_SwitchRotateAvatarButtonObject/CommonButton";
        public const string NativeFxExposureSlider =
            FilterMenu + "/ExposureAndTemperatureLayout/SlidersLayout/ExposureSliderLayout/SliderLayout";

        // ================= 原生 UI：容器 =================

        public const string BodyRight = Canvas + "/Body/Right";
        public const string FooterCenter = Canvas + "/Footer/Center";
        public const string FiltersListLayout = FilterMenu + "/FiltersListLayout";
        public const string SpecialEffectsListLayout = FilterMenu + "/SpecialEffectsListLayout";
        public const string FiltersListViewport = FiltersListLayout + "/P_FiltersListObject/ScrollRect/Viewport";
        public const string SpecialEffectsListViewport =
            SpecialEffectsListLayout + "/P_EffectsListObject/ScrollRect/Viewport";

        // ================= 本 mod 克隆出的 UI =================
        //
        // ⚠️⚠️ 这些名字是 UI 契约，不是可以随便改的常量：
        //   - 生产方是 NativeUiFactory（它 Instantiate 后 .name = "P_BetterCamera…"）
        //   - 消费方靠 GameObject.Find(这些路径) 去拿它们
        //   - 名字一改，Find 返回 null，各处静默 return，表现为"功能消失"
        //   - 其中 P_BetterCameraSwitchModeButton / P_SwitchCameraFocusModeButtonObject1 /
        //     P_BetterCameraCommonButton 没有任何 C# 代码引用，但作者把它们当 UI 元素在用，
        //     同样不能删
        //
        // 同理，NativeUiFactory 里那些 localPosition / localEulerAngles / localScale /
        // 图标显隐也都是作者摆好的 UI 结果，重构时一律原样保留。

        // 克隆体的名字单独列出来：NativeUiFactory 用这些名字给 Instantiate 出来的对象命名，
        // 而消费者用下面的路径去找。两者由同一个常量拼出，改名就不可能只改一半。
        public const string NameBcZoomHandle = "P_BetterCameraHandleObject0";
        public const string NameBcFocusHandle = "P_BetterCameraHandleObject1";
        public const string NameBcDutchHandle = "P_BetterCameraHandleObject2";
        public const string NameBcShowRoomStates = "P_BetterCameraShowRoomStates";
        public const string NameBcFxBaseSlider = "BaseSlider";
        public const string NameBcFxEffectSlider = "EffectSlider";

        // 下面这三个克隆体没有任何 C# 逻辑引用，但作者把它们当 UI 元素在用。
        // 名字同样是契约 —— 不要删、不要改名。
        public const string NameBcSwitchModeButton = "P_BetterCameraSwitchModeButton";
        public const string NameBcFocusModeButton1 = "P_SwitchCameraFocusModeButtonObject1";
        public const string NameBcCommonButton = "P_BetterCameraCommonButton";

        public const string BcZoomHandle = BodyRight + "/" + NameBcZoomHandle;       // ZoomSlider
        public const string BcFocusHandle = BodyRight + "/" + NameBcFocusHandle;     // FocusSlider；DutchSlider 会改它的 m_Direction
        public const string BcDutchHandle = FooterCenter + "/" + NameBcDutchHandle;  // DutchSlider
        public const string BcShowRoomStates = FooterCenter + "/" + NameBcShowRoomStates; // DutchReset
        public const string BcFxBaseSlider = FiltersListLayout + "/" + NameBcFxBaseSlider;       // FxSlider（滤镜）
        public const string BcFxEffectSlider = SpecialEffectsListLayout + "/" + NameBcFxEffectSlider; // FxSlider（特效）

        // 滑条/按钮在克隆体里的子路径
        public const string SliderNode = "/Slider/Slider";
        public const string ButtonNode = "/CommonButton/Button";

        public const string BcZoomSlider = BcZoomHandle + SliderNode;
        public const string BcFocusSlider = BcFocusHandle + SliderNode;
        public const string BcDutchSlider = BcDutchHandle + SliderNode;
        public const string BcFxBaseSliderNode = BcFxBaseSlider + "/P_PostProcessExposureSliderObject" + SliderNode;
        public const string BcFxEffectSliderNode = BcFxEffectSlider + "/P_PostProcessExposureSliderObject" + SliderNode;
        public const string BcShowRoomStatesButton = BcShowRoomStates + ButtonNode;

        // ================= 游戏自己的按钮 =================

        public const string RoomSnapBackButton =
            Canvas + "/Header/RoomSnapSceneBackButtonObject/CommonBackButtonObject/CommonBackButton";

        // ================= 后处理 Volume =================

        public const string BaseVolume = "SceneContext/System/P_RoomSnapPostProcessObject/BaseVolume";
        public const string EffectVolume = "SceneContext/System/P_RoomSnapPostProcessObject/EffectVolume";
    }
}
