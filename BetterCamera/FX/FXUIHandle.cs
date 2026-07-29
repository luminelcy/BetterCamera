using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace BetterCamera.FX
{
    public static class FXUIHandle
    {
        private const string SourceSliderPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_FilterMenuObject/Body/ControlsLayout/ExposureAndTemperatureLayout/SlidersLayout/ExposureSliderLayout/SliderLayout";

        private const string BaseSliderTargetPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_FilterMenuObject/Body/ControlsLayout/FiltersListLayout";

        private const string EffectSliderTargetPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_FilterMenuObject/Body/ControlsLayout/SpecialEffectsListLayout";

        private const string SpecialEffectsListObjectPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_FilterMenuObject/Body/ControlsLayout/SpecialEffectsListLayout/P_EffectsListObject/ScrollRect/Viewport";

        private const string FiltersListObjectPath =
            "SceneContext/CommonCanvas/UIPartsGroup/Header/P_FilterMenuObject/Body/ControlsLayout/FiltersListLayout/P_FiltersListObject/ScrollRect/Viewport";

        public static void Init(MelonLogger.Instance logger)
        {
            var sourceSlider = GameObject.Find(SourceSliderPath);
            if (sourceSlider == null)
            {
                logger.Error($"FXUIHandle: Source slider not found at path: {SourceSliderPath}");
                return;
            }

            

            var baseTarget = GameObject.Find(BaseSliderTargetPath);
            if (baseTarget != null)
            {
                var baseSlider = UnityEngine.Object.Instantiate(sourceSlider, baseTarget.transform);
                baseSlider.name = "BaseSlider";
                baseSlider.SetActive(true);
                baseSlider.transform.SetAsFirstSibling();
                baseSlider.transform.localPosition = new Vector3(0f, 126f, 0f);

            }

            var effectTarget = GameObject.Find(EffectSliderTargetPath);
            if (effectTarget != null)
            {
                var effectSlider = UnityEngine.Object.Instantiate(sourceSlider, effectTarget.transform);
                effectSlider.name = "EffectSlider";
                effectSlider.SetActive(true);
                effectSlider.transform.SetAsFirstSibling();
                effectSlider.transform.localPosition = new Vector3(0f, 126f, 0f);

            }

            var effectsListObj = GameObject.Find(SpecialEffectsListObjectPath);
            if (effectsListObj != null)
            {
                effectsListObj.transform.localPosition = new Vector3(-200f, 110f, 0f);
                var rt = effectsListObj.GetComponent<RectTransform>();
                if (rt != null) rt.offsetMin = new Vector2(0f, -260f);
            }

            var filtersListObj = GameObject.Find(FiltersListObjectPath);
            if (filtersListObj != null)
            {
                filtersListObj.transform.localPosition = new Vector3(-200f, 110f, 0f);
                var rt = filtersListObj.GetComponent<RectTransform>();
                if (rt != null) rt.offsetMin = new Vector2(0f, -260f);
            }
        }
    }
}
