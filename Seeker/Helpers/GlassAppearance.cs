using Seeker.Helpers;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using Common;
using System;

namespace Seeker
{
    /// <summary>
    /// SeekerOG "Glass" appearance helpers.
    ///
    /// Design notes:
    ///  * Translucent surfaces are plain colours with alpha (cheap, no offscreen buffers), so they are
    ///    safe to use on long lists.
    ///  * Real blur is only used where the platform composites it for us: the window behind dialogs and
    ///    bottom sheets on Android 12+ (cross-window blur), and only when the device reports it as
    ///    enabled (it is disabled e.g. in battery saver or on low-end GPUs). Older devices fall back to
    ///    a dim + translucent surface.
    ///  * "Reduce visual effects" switches to solid surfaces and disables blur and animations.
    /// </summary>
    public static class GlassAppearance
    {
        public const int BlurRadiusPx = 48;

        public static bool IsGlassTheme(Context c)
        {
            bool night = c.Resources.Configuration.UiMode.HasFlag(UiMode.NightYes);
            return night
                ? (PreferencesState.NightModeVariant == NightThemeType.Glass || PreferencesState.NightModeVariant == NightThemeType.AmoledGlass)
                : PreferencesState.DayModeVariant == DayThemeType.Glass;
        }

        public static bool EffectsReduced => PreferencesState.ReduceVisualEffects;

        public static bool AnimationsEnabled => PreferencesState.UiAnimations && !PreferencesState.ReduceVisualEffects;

        /// <summary>Applies accent, intensity and reduced-effects overlays after Activity.SetTheme.</summary>
        public static void ApplyOverlays(Activity a)
        {
            try
            {
                int accent = AccentStyle(PreferencesState.AccentColor);
                if (accent != 0)
                {
                    a.Theme.ApplyStyle(accent, true);
                }
                if (PreferencesState.ReduceVisualEffects)
                {
                    a.Theme.ApplyStyle(Resource.Style.SeekerOGGlass_Reduced, true);
                }
                else if (IsGlassTheme(a))
                {
                    bool night = a.Resources.Configuration.UiMode.HasFlag(UiMode.NightYes);
                    switch (PreferencesState.GlassIntensity)
                    {
                        case GlassIntensityType.Subtle:
                            a.Theme.ApplyStyle(Resource.Style.SeekerOGGlass_Subtle, true);
                            break;
                        case GlassIntensityType.Intense:
                            a.Theme.ApplyStyle(night ? Resource.Style.SeekerOGGlass_Intense_Dark : Resource.Style.SeekerOGGlass_Intense_Light, true);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Debug("GlassAppearance.ApplyOverlays failed: " + e.Message);
            }
        }

        public static int AccentStyle(AccentColorType accent) => accent switch
        {
            AccentColorType.Ocean => Resource.Style.SeekerOGAccent_Ocean,
            AccentColorType.Violet => Resource.Style.SeekerOGAccent_Violet,
            AccentColorType.Emerald => Resource.Style.SeekerOGAccent_Emerald,
            AccentColorType.Sunset => Resource.Style.SeekerOGAccent_Sunset,
            AccentColorType.Rose => Resource.Style.SeekerOGAccent_Rose,
            AccentColorType.Graphite => Resource.Style.SeekerOGAccent_Graphite,
            _ => 0,
        };

        /// <summary>
        /// Real background blur behind a dialog / bottom sheet window (Android 12+, when the
        /// compositor supports it). No-op elsewhere.
        /// </summary>
        public static void ApplyBlurBehind(Window window)
        {
            if (window == null || !PreferencesState.GlassBlur || PreferencesState.ReduceVisualEffects)
            {
                return;
            }
            if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                return;
            }
            try
            {
                var wm = window.Context.GetSystemService(Context.WindowService) as IWindowManager;
                if (wm == null || !wm.IsCrossWindowBlurEnabled)
                {
                    return;
                }
                window.AddFlags(WindowManagerFlags.BlurBehind);
                var attrs = window.Attributes;
                attrs.BlurBehindRadius = BlurRadiusPx;
                window.Attributes = attrs;
            }
            catch (Exception e)
            {
                Logger.Debug("blur behind not available: " + e.Message);
            }
        }

        /// <summary>Wraps a base context with the user text scale (Settings &gt; Appearance &gt; Text size).</summary>
        public static Context WrapTextScale(Context baseContext)
        {
            int percent = PreferencesState.TextScalePercent;
            if (percent == 100 || percent < 70 || percent > 160)
            {
                return baseContext;
            }
            var config = new Configuration(baseContext.Resources.Configuration);
            config.FontScale = config.FontScale * (percent / 100f);
            return baseContext.CreateConfigurationContext(config);
        }

        public static void ResetToDefaults()
        {
            PreferencesState.AccentColor = AccentColorType.ThemeDefault;
            PreferencesState.GlassIntensity = GlassIntensityType.Balanced;
            PreferencesState.GlassBlur = true;
            PreferencesState.ReduceVisualEffects = false;
            PreferencesState.UiAnimations = true;
            PreferencesState.TextScalePercent = 100;
        }

        public static void Save()
        {
            PreferencesManager.SaveInt(KeyConsts.M_OG_AccentColor, (int)PreferencesState.AccentColor);
            PreferencesManager.SaveInt(KeyConsts.M_OG_GlassIntensity, (int)PreferencesState.GlassIntensity);
            PreferencesManager.SaveBoolean(KeyConsts.M_OG_GlassBlur, PreferencesState.GlassBlur);
            PreferencesManager.SaveBoolean(KeyConsts.M_OG_ReduceEffects, PreferencesState.ReduceVisualEffects);
            PreferencesManager.SaveBoolean(KeyConsts.M_OG_Animations, PreferencesState.UiAnimations);
            PreferencesManager.SaveInt(KeyConsts.M_OG_TextScale, PreferencesState.TextScalePercent);
        }
    }
}
