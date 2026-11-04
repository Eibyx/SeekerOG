using Common;
using Seeker.Helpers;
using Android.App;
using Android.Media.Audiofx;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Dialog;
using System;

namespace Seeker.Player
{
    /// <summary>System equalizer attached to the player's audio session (only affects SeekerOG playback).</summary>
    public static class EqualizerDialog
    {
        private static Equalizer? equalizer;
        private static int attachedSession;

        public static void Show(Activity activity)
        {
            int sessionId = PlaybackService.AudioSessionId;
            if (sessionId == 0)
            {
                SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.seekerog_eq_start_playback), ToastLength.Short);
                return;
            }
            try
            {
                if (equalizer == null || attachedSession != sessionId)
                {
                    equalizer?.Release();
                    equalizer = new Equalizer(0, sessionId);
                    attachedSession = sessionId;
                }
            }
            catch (Exception e)
            {
                SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.seekerog_eq_unavailable) + " " + e.Message, ToastLength.Long);
                return;
            }
            var eq = equalizer;
            float density = activity.Resources.DisplayMetrics.Density;
            var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            root.SetPadding((int)(20 * density), (int)(8 * density), (int)(20 * density), 0);

            var enable = new Switch(activity) { Text = activity.GetString(Resource.String.seekerog_eq_enabled), Checked = eq.Enabled };
            root.AddView(enable);

            short presets = eq.NumberOfPresets;
            var presetNames = new string[presets + 1];
            presetNames[0] = activity.GetString(Resource.String.seekerog_eq_custom);
            for (short i = 0; i < presets; i++)
            {
                presetNames[i + 1] = eq.GetPresetName(i);
            }
            var spinner = new Spinner(activity);
            spinner.Adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleSpinnerDropDownItem, presetNames);
            root.AddView(spinner);

            short bands = eq.NumberOfBands;
            var range = eq.GetBandLevelRange();
            short min = range != null && range.Length > 1 ? range[0] : (short)-1500;
            short max = range != null && range.Length > 1 ? range[1] : (short)1500;
            var bars = new SeekBar[bands];
            for (short b = 0; b < bands; b++)
            {
                short band = b;
                int hz = eq.GetCenterFreq(band) / 1000;
                var label = new TextView(activity) { Text = hz >= 1000 ? (hz / 1000.0).ToString("0.#") + " kHz" : hz + " Hz" };
                root.AddView(label);
                var bar = new SeekBar(activity) { Max = max - min, Progress = eq.GetBandLevel(band) - min };
                bar.ProgressChanged += (s, e) =>
                {
                    if (e.FromUser)
                    {
                        try { eq.SetBandLevel(band, (short)(e.Progress + min)); } catch (Exception) { }
                    }
                };
                bars[b] = bar;
                root.AddView(bar);
            }
            enable.CheckedChange += (s, e) => { try { eq.SetEnabled(e.IsChecked); } catch (Exception) { } };
            spinner.ItemSelected += (s, e) =>
            {
                if (e.Position <= 0) return;
                try
                {
                    eq.UsePreset((short)(e.Position - 1));
                    for (short b = 0; b < bands; b++) bars[b].Progress = eq.GetBandLevel(b) - min;
                }
                catch (Exception) { }
            };
            var scroll = new ScrollView(activity);
            scroll.AddView(root);
            var dialog = new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_equalizer)
                .SetView(scroll)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) => { })
                .Create();
            dialog.Show();
            GlassAppearance.ApplyBlurBehind(dialog.Window);
        }
    }
}
