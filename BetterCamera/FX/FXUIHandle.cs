using MelonLoader;
using UnityEngine;
using BetterCamera.Game;
using UnityEngine.UI;

namespace BetterCamera.FX
{
    public static class FXUIHandle
    {
        private const string SourceSliderPath =
            GamePaths.NativeFxExposureSlider;

        private const string BaseSliderTargetPath =
            GamePaths.FiltersListLayout;

        private const string EffectSliderTargetPath =
            GamePaths.SpecialEffectsListLayout;

        private const string SpecialEffectsListObjectPath =
            GamePaths.SpecialEffectsListViewport;

        private const string FiltersListObjectPath =
            GamePaths.FiltersListViewport;

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
                baseSlider.name = GamePaths.NameBcFxBaseSlider;
                baseSlider.SetActive(true);
                baseSlider.transform.SetAsLastSibling();
                baseSlider.transform.localPosition = new Vector3(0f, 126f, 0f);

            }

            var effectTarget = GameObject.Find(EffectSliderTargetPath);
            if (effectTarget != null)
            {
                var effectSlider = UnityEngine.Object.Instantiate(sourceSlider, effectTarget.transform);
                effectSlider.name = GamePaths.NameBcFxEffectSlider;
                effectSlider.SetActive(true);
                effectSlider.transform.SetAsLastSibling();
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
