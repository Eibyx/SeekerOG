using Seeker.Helpers;
using Common;
using System.Collections.Generic;

namespace Seeker.Settings.Rows
{
    /// <summary>SeekerOG appearance pickers (accent colour, glass intensity, text size).</summary>
    internal static class SettingPickersGlass
    {
        public static string AccentLabel(Android.Content.Context ctx, AccentColorType a) => a switch
        {
            AccentColorType.Ocean => ctx.GetString(Resource.String.seekerog_accent_ocean),
            AccentColorType.Violet => ctx.GetString(Resource.String.seekerog_accent_violet),
            AccentColorType.Emerald => ctx.GetString(Resource.String.seekerog_accent_emerald),
            AccentColorType.Sunset => ctx.GetString(Resource.String.seekerog_accent_sunset),
            AccentColorType.Rose => ctx.GetString(Resource.String.seekerog_accent_rose),
            AccentColorType.Graphite => ctx.GetString(Resource.String.seekerog_accent_graphite),
            _ => ctx.GetString(Resource.String.seekerog_accent_theme),
        };

        public static string IntensityLabel(Android.Content.Context ctx, GlassIntensityType g) => g switch
        {
            GlassIntensityType.Subtle => ctx.GetString(Resource.String.seekerog_glass_subtle),
            GlassIntensityType.Intense => ctx.GetString(Resource.String.seekerog_glass_intense),
            _ => ctx.GetString(Resource.String.seekerog_glass_balanced),
        };

        public static string TextScaleLabel(int percent) => percent + "%";

        private static void Recreate(ISettingsHost host)
        {
            GlassAppearance.Save();
            UiHelpers.SetActivityTheme(host.Activity);
            SeekerApplication.RecreateActivies();
        }

        public static void PickAccent(ISettingsHost host, ValueRow row)
        {
            var ctx = host.Activity;
            var options = new List<(string label, AccentColorType value)>();
            foreach (AccentColorType a in new[] { AccentColorType.ThemeDefault, AccentColorType.Ocean, AccentColorType.Violet, AccentColorType.Emerald, AccentColorType.Sunset, AccentColorType.Rose, AccentColorType.Graphite })
            {
                options.Add((AccentLabel(ctx, a), a));
            }
            OptionPickerBottomSheet.ShowOptions(host, row, options, () => PreferencesState.AccentColor, v =>
            {
                if (PreferencesState.AccentColor == v) return;
                PreferencesState.AccentColor = v;
                Recreate(host);
            });
        }

        public static void PickIntensity(ISettingsHost host, ValueRow row)
        {
            var ctx = host.Activity;
            var options = new List<(string label, GlassIntensityType value)>
            {
                (IntensityLabel(ctx, GlassIntensityType.Subtle), GlassIntensityType.Subtle),
                (IntensityLabel(ctx, GlassIntensityType.Balanced), GlassIntensityType.Balanced),
                (IntensityLabel(ctx, GlassIntensityType.Intense), GlassIntensityType.Intense),
            };
            OptionPickerBottomSheet.ShowOptions(host, row, options, () => PreferencesState.GlassIntensity, v =>
            {
                if (PreferencesState.GlassIntensity == v) return;
                PreferencesState.GlassIntensity = v;
                Recreate(host);
            });
        }

        public static void PickTextScale(ISettingsHost host, ValueRow row)
        {
            var options = new List<(string label, int value)>();
            foreach (int p in new[] { 85, 100, 115, 130 })
            {
                options.Add((TextScaleLabel(p), p));
            }
            OptionPickerBottomSheet.ShowOptions(host, row, options, () => PreferencesState.TextScalePercent, v =>
            {
                if (PreferencesState.TextScalePercent == v) return;
                PreferencesState.TextScalePercent = v;
                Recreate(host);
            });
        }

        public static void AddRows(List<SettingRow> rows)
        {
            rows.Add(new ValueRow
            {
                Id = "appearance.og_accent",
                TitleRes = Resource.String.seekerog_accent_color,
                IconRes = Resource.Drawable.palette_28dp,
                ValueProvider = ctx => AccentLabel(ctx, PreferencesState.AccentColor),
                OnClick = (h, r) => PickAccent(h, r),
            });
            rows.Add(new ValueRow
            {
                Id = "appearance.og_intensity",
                TitleRes = Resource.String.seekerog_glass_intensity,
                IconRes = Resource.Drawable.palette_28dp,
                ValueProvider = ctx => IntensityLabel(ctx, PreferencesState.GlassIntensity),
                OnClick = (h, r) => PickIntensity(h, r),
            });
            rows.Add(new ToggleRow
            {
                Id = "appearance.og_blur",
                TitleRes = Resource.String.seekerog_glass_blur,
                SubtitleRes = Resource.String.seekerog_glass_blur_subtitle,
                IconRes = Resource.Drawable.palette_28dp,
                Getter = () => PreferencesState.GlassBlur,
                Setter = v => { PreferencesState.GlassBlur = v; GlassAppearance.Save(); },
            });
            rows.Add(new ToggleRow
            {
                Id = "appearance.og_animations",
                TitleRes = Resource.String.seekerog_animations,
                IconRes = Resource.Drawable.palette_28dp,
                Getter = () => PreferencesState.UiAnimations,
                Setter = v => { PreferencesState.UiAnimations = v; GlassAppearance.Save(); },
            });
            rows.Add(new ToggleRow
            {
                Id = "appearance.og_reduce",
                TitleRes = Resource.String.seekerog_reduce_effects,
                SubtitleRes = Resource.String.seekerog_reduce_effects_subtitle,
                IconRes = Resource.Drawable.palette_28dp,
                Getter = () => PreferencesState.ReduceVisualEffects,
                Setter = v =>
                {
                    PreferencesState.ReduceVisualEffects = v;
                    GlassAppearance.Save();
                    SeekerApplication.RecreateActivies();
                },
            });
            rows.Add(new ValueRow
            {
                Id = "appearance.og_textscale",
                TitleRes = Resource.String.seekerog_text_size,
                IconRes = Resource.Drawable.palette_28dp,
                ValueProvider = ctx => TextScaleLabel(PreferencesState.TextScalePercent),
                OnClick = (h, r) => PickTextScale(h, r),
            });
            rows.Add(new ButtonRow
            {
                Id = "appearance.og_reset",
                TitleRes = Resource.String.seekerog_reset_appearance,
                IconRes = Resource.Drawable.palette_28dp,
                OnClick = (h, r) =>
                {
                    GlassAppearance.ResetToDefaults();
                    PreferencesState.DayModeVariant = DayThemeType.Glass;
                    PreferencesState.NightModeVariant = NightThemeType.Glass;
                    PreferencesManager.SaveDayModeVariant();
                    PreferencesManager.SaveNightModeVariant();
                    Recreate(h);
                },
            });
        }
    }
}
