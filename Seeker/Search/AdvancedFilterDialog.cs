using Seeker.Helpers;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using Common;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Seeker
{
    /// <summary>
    /// SeekerOG graphical filter builder. It produces text in the advanced syntax and puts it in the
    /// result filter box, so typed and graphical filters are the same thing (and can be saved as profiles).
    /// All filtering is local: nothing here changes what is sent to the Soulseek server.
    /// </summary>
    public static class AdvancedFilterDialog
    {
        public static readonly string[] Formats = { "flac", "mp3", "wav", "aac", "m4a", "ogg", "opus", "alac", "wma", "aiff", "dsd", "ape", "zip", "rar" };
        private static readonly int[] BitratePresets = { 128, 192, 256, 320 };
        private static readonly int[] SampleRates = { 0, 44100, 48000, 88200, 96000, 176400, 192000 };
        private static readonly int[] BitDepths = { 0, 16, 24, 32 };

        private static int ParseInt(EditText e, int fallback = 0)
        {
            return int.TryParse(e.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static double ParseDouble(EditText e)
        {
            string t = (e.Text ?? string.Empty).Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        private static int ParseDurationField(EditText e)
        {
            string t = (e.Text ?? string.Empty).Trim();
            if (t.Length == 0) return 0;
            return AdvancedQuery.ParseDuration(t, out double v) ? (int)v : 0;
        }

        public static void Show(Activity activity, string currentFilter, Action<string> apply)
        {
            var view = LayoutInflater.From(activity).Inflate(Resource.Layout.seekerog_advanced_filter, null);
            var formatGroup = view.FindViewById<ChipGroup>(Resource.Id.og_format_group);
            var bitrateGroup = view.FindViewById<ChipGroup>(Resource.Id.og_bitrate_presets);
            var customFormats = view.FindViewById<EditText>(Resource.Id.og_custom_formats);
            var lossless = view.FindViewById<CheckBox>(Resource.Id.og_lossless);
            var hires = view.FindViewById<CheckBox>(Resource.Id.og_hires);
            var minBr = view.FindViewById<EditText>(Resource.Id.og_min_bitrate);
            var maxBr = view.FindViewById<EditText>(Resource.Id.og_max_bitrate);
            var sr = view.FindViewById<Spinner>(Resource.Id.og_samplerate);
            var bd = view.FindViewById<Spinner>(Resource.Id.og_bitdepth);
            var minSize = view.FindViewById<EditText>(Resource.Id.og_min_size);
            var maxSize = view.FindViewById<EditText>(Resource.Id.og_max_size);
            var minDur = view.FindViewById<EditText>(Resource.Id.og_min_dur);
            var maxDur = view.FindViewById<EditText>(Resource.Id.og_max_dur);
            var free = view.FindViewById<CheckBox>(Resource.Id.og_free_slot);
            var maxQueue = view.FindViewById<EditText>(Resource.Id.og_max_queue);
            var minSpeed = view.FindViewById<EditText>(Resource.Id.og_min_speed);
            var exclude = view.FindViewById<EditText>(Resource.Id.og_exclude);
            var extra = view.FindViewById<EditText>(Resource.Id.og_extra);
            var preview = view.FindViewById<TextView>(Resource.Id.og_preview);

            var formatChips = new List<Chip>();
            foreach (var f in Formats)
            {
                var chip = new Chip(activity) { Text = f.ToUpperInvariant(), Checkable = true, Tag = f };
                formatGroup.AddView(chip);
                formatChips.Add(chip);
            }
            foreach (var b in BitratePresets)
            {
                var chip = new Chip(activity) { Text = "MP3 " + b, Checkable = true, Tag = b };
                bitrateGroup.AddView(chip);
                chip.CheckedChange += (s, e) =>
                {
                    if (e.IsChecked)
                    {
                        minBr.Text = b.ToString(CultureInfo.InvariantCulture);
                        var mp3 = formatChips.First(c => (string)c.Tag == "mp3");
                        mp3.Checked = true;
                    }
                };
            }
            string unknown = activity.GetString(Resource.String.seekerog_f_any);
            sr.Adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleSpinnerDropDownItem,
                SampleRates.Select(r => r == 0 ? unknown : (r / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz+").ToList());
            bd.Adapter = new ArrayAdapter<string>(activity, Android.Resource.Layout.SimpleSpinnerDropDownItem,
                BitDepths.Select(d => d == 0 ? unknown : d + " bit+").ToList());

            // Pre-fill from the current filter (best effort; anything not representable stays in "extra").
            var current = AdvancedQuery.Parse(currentFilter ?? string.Empty);
            foreach (var chip in formatChips)
            {
                string tag = (string)chip.Tag;
                if (tag == "dsd") chip.Checked = current.Formats.Contains("dsf");
                else chip.Checked = current.Formats.Contains(tag);
            }
            lossless.Checked = current.LosslessOnly;
            hires.Checked = current.HiResOnly;
            free.Checked = current.FreeSlot == true;
            extra.Text = string.Join(" ", current.Words.Concat(current.Phrases.Select(p => "\"" + p + "\"")));
            exclude.Text = string.Join(" ", current.ExcludedWords);
            foreach (var c in current.BitRate)
            {
                if (c.Op == AdvancedQuery.Op.Ge || c.Op == AdvancedQuery.Op.Gt) minBr.Text = ((int)c.Value).ToString(CultureInfo.InvariantCulture);
                if (c.Op == AdvancedQuery.Op.Le || c.Op == AdvancedQuery.Op.Lt) maxBr.Text = ((int)c.Value).ToString(CultureInfo.InvariantCulture);
            }
            foreach (var c in current.Size)
            {
                string mb = (c.Value / (1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);
                if (c.Op == AdvancedQuery.Op.Ge || c.Op == AdvancedQuery.Op.Gt) minSize.Text = mb;
                if (c.Op == AdvancedQuery.Op.Le || c.Op == AdvancedQuery.Op.Lt) maxSize.Text = mb;
            }
            foreach (var c in current.QueueLength)
            {
                maxQueue.Text = ((int)c.Value).ToString(CultureInfo.InvariantCulture);
            }
            if (current.SampleRate.Count > 0)
            {
                int idx = Array.IndexOf(SampleRates, (int)current.SampleRate[0].Value);
                if (idx > 0) sr.SetSelection(idx);
            }
            if (current.BitDepth.Count > 0)
            {
                int idx = Array.IndexOf(BitDepths, (int)current.BitDepth[0].Value);
                if (idx > 0) bd.SetSelection(idx);
            }

            Func<string> build = () =>
            {
                var formats = formatChips.Where(c => c.Checked).Select(c => (string)c.Tag).ToList();
                formats.AddRange((customFormats.Text ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.TrimStart('.')));
                return SearchTools.BuildFilter(formats, lossless.Checked, hires.Checked, ParseInt(minBr), ParseInt(maxBr),
                    SampleRates[Math.Max(0, sr.SelectedItemPosition)], BitDepths[Math.Max(0, bd.SelectedItemPosition)],
                    ParseDouble(minSize), ParseDouble(maxSize), ParseDurationField(minDur), ParseDurationField(maxDur),
                    free.Checked, string.IsNullOrWhiteSpace(maxQueue.Text) ? -1 : ParseInt(maxQueue, -1), ParseInt(minSpeed),
                    exclude.Text ?? string.Empty, extra.Text ?? string.Empty);
            };
            Action refreshPreview = () => preview.Text = build();
            foreach (var c in formatChips) c.CheckedChange += (s, e) => refreshPreview();
            foreach (var cb in new[] { lossless, hires, free }) cb.CheckedChange += (s, e) => refreshPreview();
            foreach (var et in new[] { customFormats, minBr, maxBr, minSize, maxSize, minDur, maxDur, maxQueue, minSpeed, exclude, extra }) et.TextChanged += (s, e) => refreshPreview();
            sr.ItemSelected += (s, e) => refreshPreview();
            bd.ItemSelected += (s, e) => refreshPreview();
            refreshPreview();

            var dialog = new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_advanced_filters)
                .SetView(view)
                .SetPositiveButton(Resource.String.seekerog_apply, (s, e) => apply(build()))
                .SetNeutralButton(Resource.String.seekerog_save_profile, (s, e) => PromptSaveProfile(activity, build()))
                .SetNegativeButton(Resource.String.seekerog_clear, (s, e) => apply(string.Empty))
                .Create();
            dialog.Show();
            GlassAppearance.ApplyBlurBehind(dialog.Window);
        }

        public static void PromptSaveProfile(Activity activity, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.seekerog_profile_empty), ToastLength.Short);
                return;
            }
            var input = new EditText(activity) { Hint = activity.GetString(Resource.String.seekerog_profile_name) };
            input.SetSingleLine(true);
            var container = new FrameLayout(activity);
            int pad = (int)(20 * activity.Resources.DisplayMetrics.Density);
            container.SetPadding(pad, pad / 2, pad, 0);
            container.AddView(input);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_save_profile)
                .SetView(container)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) =>
                {
                    SearchTools.SaveProfile(input.Text ?? string.Empty, filter);
                    SeekerOGStore.SaveSearchTools();
                })
                .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => { })
                .Show();
        }

        public static void ShowProfiles(Activity activity, Action<string> apply)
        {
            if (SearchTools.FilterProfiles.Count == 0)
            {
                SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.seekerog_no_profiles), ToastLength.Short);
                return;
            }
            var names = SearchTools.FilterProfiles.Select(p => p.Name + "  ·  " + p.Filter).ToArray();
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_filter_profiles)
                .SetItems(names, (s, e) => apply(SearchTools.FilterProfiles[e.Which].Filter))
                .SetNeutralButton(Resource.String.seekerog_delete_profiles, (s, e) =>
                {
                    var checkedItems = new bool[names.Length];
                    new MaterialAlertDialogBuilder(activity)
                        .SetTitle(Resource.String.seekerog_delete_profiles)
                        .SetMultiChoiceItems(names, checkedItems, (s2, e2) => checkedItems[e2.Which] = e2.IsChecked)
                        .SetPositiveButton(Android.Resource.String.Ok, (s2, e2) =>
                        {
                            for (int i = checkedItems.Length - 1; i >= 0; i--)
                            {
                                if (checkedItems[i]) SearchTools.FilterProfiles.RemoveAt(i);
                            }
                            SeekerOGStore.SaveSearchTools();
                        })
                        .SetNegativeButton(Android.Resource.String.Cancel, (s2, e2) => { })
                        .Show();
                })
                .Show();
        }

        public static void ShowSyntaxHelp(Activity activity)
        {
            var tv = new TextView(activity);
            int pad = (int)(20 * activity.Resources.DisplayMetrics.Density);
            tv.SetPadding(pad, pad / 2, pad, 0);
            tv.SetTextIsSelectable(true);
            tv.Text = activity.GetString(Resource.String.seekerog_syntax_help);
            var sv = new ScrollView(activity);
            sv.AddView(tv);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_syntax_help_title)
                .SetView(sv)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) => { })
                .Show();
        }
    }
}
