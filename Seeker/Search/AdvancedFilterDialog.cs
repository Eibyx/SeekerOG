using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using Common;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Seeker.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Seeker
{
    /// <summary>
    /// SeekerOG advanced filters (v2, glass bottom sheet). Graphical builder for the advanced syntax:
    /// what it produces is exactly the text users could type in the filter box, so typed filters,
    /// graphical filters and saved profiles stay the same thing. All filtering is local; nothing here
    /// changes what is sent to the Soulseek server.
    ///
    /// UX: sections as glass cards with an icon, subtitle and "active" counter; chips for formats and
    /// quality presets (single-selection chips replace the old spinners); rounded fields with units;
    /// a live summary (human readable chips + generated syntax) and the number of matching results on
    /// the primary button; sticky action bar (save profile / clear / apply).
    /// </summary>
    public static class AdvancedFilterDialog
    {
        public static readonly string[] Formats = { "flac", "mp3", "wav", "aac", "m4a", "ogg", "opus", "alac", "wma", "aiff", "dsd", "ape", "zip", "rar" };
        private static readonly int[] BitratePresets = { 128, 192, 256, 320 };
        private static readonly int[] SampleRates = { 0, 44100, 48000, 88200, 96000, 176400, 192000 };
        private static readonly int[] BitDepths = { 0, 16, 24, 32 };

        private sealed class Controls
        {
            public List<Chip> FormatChips = new List<Chip>();
            public List<Chip> BitrateChips = new List<Chip>();
            public List<Chip> SampleChips = new List<Chip>();
            public List<Chip> DepthChips = new List<Chip>();
            public EditText CustomFormats = null!, MinBr = null!, MaxBr = null!, MinSize = null!, MaxSize = null!, MinDur = null!, MaxDur = null!, MaxQueue = null!, MinSpeed = null!, Exclude = null!, Extra = null!;
            public Chip Lossless = null!, HiRes = null!;
            public MaterialSwitch Free = null!;
            public bool Populating;
        }

        private static string Txt(EditText e) => (e.Text ?? string.Empty).Trim();

        private static int ParseInt(EditText e, int fallback = 0) =>
            int.TryParse(Txt(e), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static double ParseDouble(EditText e) =>
            double.TryParse(Txt(e).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private static int ParseDurationField(EditText e)
        {
            string t = Txt(e);
            return t.Length > 0 && AdvancedQuery.ParseDuration(t, out double v) ? (int)v : 0;
        }

        private static int SelectedIndex(List<Chip> chips) => Math.Max(0, chips.FindIndex(c => c.Checked));

        private static string Build(Controls c)
        {
            var formats = c.FormatChips.Where(x => x.Checked).Select(x => x.Tag!.ToString()!).ToList();
            formats.AddRange(Txt(c.CustomFormats).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.TrimStart('.')));
            return SearchTools.BuildFilter(formats, c.Lossless.Checked, c.HiRes.Checked, ParseInt(c.MinBr), ParseInt(c.MaxBr),
                SampleRates[SelectedIndex(c.SampleChips)], BitDepths[SelectedIndex(c.DepthChips)],
                ParseDouble(c.MinSize), ParseDouble(c.MaxSize), ParseDurationField(c.MinDur), ParseDurationField(c.MaxDur),
                c.Free.Checked, Txt(c.MaxQueue).Length == 0 ? -1 : ParseInt(c.MaxQueue, -1), ParseInt(c.MinSpeed),
                Txt(c.Exclude), Txt(c.Extra));
        }

        private static Chip NewChip(Activity a, ViewGroup parent, string text, object tag)
        {
            var chip = (Chip)LayoutInflater.From(a)!.Inflate(Resource.Layout.seekerog_chip_choice, parent, false)!;
            chip.Text = text;
            chip.Tag = new Java.Lang.String(tag.ToString());
            chip.Id = View.GenerateViewId();
            parent.AddView(chip);
            return chip;
        }

        private static void SetupSection(View root, int includeId, int icon, int title, int subtitle)
        {
            var sec = root.FindViewById<View>(includeId)!;
            sec.FindViewById<ImageView>(Resource.Id.og_sec_icon)!.SetImageResource(icon);
            sec.FindViewById<TextView>(Resource.Id.og_sec_title)!.SetText(title);
            sec.FindViewById<TextView>(Resource.Id.og_sec_subtitle)!.SetText(subtitle);
        }

        private static void SetSectionCount(View root, int includeId, int count)
        {
            var pill = root.FindViewById<View>(includeId)!.FindViewById<TextView>(Resource.Id.og_sec_count)!;
            pill.Text = count.ToString(CultureInfo.InvariantCulture);
            pill.Visibility = count > 0 ? ViewStates.Visible : ViewStates.Gone;
        }

        /// <param name="countMatches">Optional: returns how many current results match a filter (run off the UI thread).</param>
        public static void Show(Activity activity, string currentFilter, Action<string> apply, Func<string, int>? countMatches = null)
        {
            var view = LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_advanced_filter, null)!;
            var c = new Controls
            {
                CustomFormats = view.FindViewById<EditText>(Resource.Id.og_custom_formats)!,
                MinBr = view.FindViewById<EditText>(Resource.Id.og_min_bitrate)!,
                MaxBr = view.FindViewById<EditText>(Resource.Id.og_max_bitrate)!,
                MinSize = view.FindViewById<EditText>(Resource.Id.og_min_size)!,
                MaxSize = view.FindViewById<EditText>(Resource.Id.og_max_size)!,
                MinDur = view.FindViewById<EditText>(Resource.Id.og_min_dur)!,
                MaxDur = view.FindViewById<EditText>(Resource.Id.og_max_dur)!,
                MaxQueue = view.FindViewById<EditText>(Resource.Id.og_max_queue)!,
                MinSpeed = view.FindViewById<EditText>(Resource.Id.og_min_speed)!,
                Exclude = view.FindViewById<EditText>(Resource.Id.og_exclude)!,
                Extra = view.FindViewById<EditText>(Resource.Id.og_extra)!,
                Lossless = view.FindViewById<Chip>(Resource.Id.og_lossless)!,
                HiRes = view.FindViewById<Chip>(Resource.Id.og_hires)!,
                Free = view.FindViewById<MaterialSwitch>(Resource.Id.og_free_slot)!,
            };

            SetupSection(view, Resource.Id.og_f_sec_formats, Resource.Drawable.og_music_note, Resource.String.seekerog_f_formats, Resource.String.seekerog_f_formats_sub);
            SetupSection(view, Resource.Id.og_f_sec_quality, Resource.Drawable.og_hd, Resource.String.seekerog_f_quality, Resource.String.seekerog_f_quality_sub);
            SetupSection(view, Resource.Id.og_f_sec_size, Resource.Drawable.og_straighten, Resource.String.seekerog_f_size_duration, Resource.String.seekerog_f_size_sub);
            SetupSection(view, Resource.Id.og_f_sec_avail, Resource.Drawable.og_speed, Resource.String.seekerog_f_availability, Resource.String.seekerog_f_avail_sub);
            SetupSection(view, Resource.Id.og_f_sec_text, Resource.Drawable.og_text, Resource.String.seekerog_f_text, Resource.String.seekerog_f_text_sub);
            SetupSection(view, Resource.Id.og_f_sec_summary, Resource.Drawable.og_summary, Resource.String.seekerog_f_summary, Resource.String.seekerog_f_summary_sub);

            var formatGroup = view.FindViewById<ChipGroup>(Resource.Id.og_format_group)!;
            foreach (var f in Formats)
            {
                c.FormatChips.Add(NewChip(activity, formatGroup, f.ToUpperInvariant(), f));
            }
            var bitrateGroup = view.FindViewById<ChipGroup>(Resource.Id.og_bitrate_presets)!;
            foreach (var b in BitratePresets)
            {
                var chip = NewChip(activity, bitrateGroup, b + "+", b);
                c.BitrateChips.Add(chip);
                int value = b;
                chip.CheckedChange += (s, e) =>
                {
                    if (c.Populating) return;
                    if (e.IsChecked) c.MinBr.Text = value.ToString(CultureInfo.InvariantCulture);
                    else if (Txt(c.MinBr) == value.ToString(CultureInfo.InvariantCulture)) c.MinBr.Text = string.Empty;
                };
            }
            string any = activity.GetString(Resource.String.seekerog_f_any);
            var srGroup = view.FindViewById<ChipGroup>(Resource.Id.og_samplerate_group)!;
            foreach (var r in SampleRates)
            {
                c.SampleChips.Add(NewChip(activity, srGroup, r == 0 ? any : (r / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz", r));
            }
            var bdGroup = view.FindViewById<ChipGroup>(Resource.Id.og_bitdepth_group)!;
            foreach (var d in BitDepths)
            {
                c.DepthChips.Add(NewChip(activity, bdGroup, d == 0 ? any : d + "-bit", d));
            }

            var preview = view.FindViewById<TextView>(Resource.Id.og_preview)!;
            var summaryChips = view.FindViewById<ChipGroup>(Resource.Id.og_f_summary_chips)!;
            var activeText = view.FindViewById<TextView>(Resource.Id.og_f_active)!;
            var applyButton = view.FindViewById<MaterialButton>(Resource.Id.og_f_apply)!;
            var handler = new Handler(Looper.MainLooper!);
            CancellationTokenSource? countCts = null;
            Action? pendingCount = null;

            Action refresh = () =>
            {
                if (c.Populating) return;
                string filter = Build(c);
                preview.Text = filter.Length == 0 ? activity.GetString(Resource.String.seekerog_f_no_filters) : filter;
                var parsed = AdvancedQuery.Parse(filter);
                var parts = parsed.Describe();
                foreach (var w in parsed.Words) parts.Add("+" + w);
                foreach (var w in parsed.ExcludedWords) parts.Add("−" + w);
                summaryChips.RemoveAllViews();
                foreach (var p in parts)
                {
                    var chip = new Chip(activity, null, Resource.Attribute.chipStyle) { Text = p, Clickable = false, Checkable = false };
                    chip.SetTextAppearance(Resource.Style.TextAppearance_Material3_LabelMedium);
                    chip.ChipBackgroundColor = Android.Content.Res.ColorStateList.ValueOf(ThemeColor(activity, Resource.Attribute.glassFieldColor));
                    chip.ChipStrokeWidth = 0;
                    chip.SetEnsureMinTouchTargetSize(false);
                    summaryChips.AddView(chip);
                }
                activeText.Text = parts.Count == 0
                    ? activity.GetString(Resource.String.seekerog_f_none_active)
                    : activity.GetString(Resource.String.seekerog_f_n_active, parts.Count);

                SetSectionCount(view, Resource.Id.og_f_sec_formats, c.FormatChips.Count(x => x.Checked) + (Txt(c.CustomFormats).Length > 0 ? 1 : 0));
                SetSectionCount(view, Resource.Id.og_f_sec_quality, (c.Lossless.Checked ? 1 : 0) + (c.HiRes.Checked ? 1 : 0) + (Txt(c.MinBr).Length > 0 ? 1 : 0) + (Txt(c.MaxBr).Length > 0 ? 1 : 0) + (SelectedIndex(c.SampleChips) > 0 ? 1 : 0) + (SelectedIndex(c.DepthChips) > 0 ? 1 : 0));
                SetSectionCount(view, Resource.Id.og_f_sec_size, new[] { c.MinSize, c.MaxSize, c.MinDur, c.MaxDur }.Count(e => Txt(e).Length > 0));
                SetSectionCount(view, Resource.Id.og_f_sec_avail, (c.Free.Checked ? 1 : 0) + new[] { c.MaxQueue, c.MinSpeed }.Count(e => Txt(e).Length > 0));
                SetSectionCount(view, Resource.Id.og_f_sec_text, new[] { c.Exclude, c.Extra }.Count(e => Txt(e).Length > 0));
                SetSectionCount(view, Resource.Id.og_f_sec_summary, parts.Count);

                // live count of matching results, debounced and cancellable (never blocks typing)
                if (countMatches != null)
                {
                    if (pendingCount != null) handler.RemoveCallbacks(pendingCount);
                    pendingCount = () =>
                    {
                        countCts?.Cancel();
                        var cts = new CancellationTokenSource();
                        countCts = cts;
                        Task.Run(() =>
                        {
                            int n = countMatches(filter);
                            if (!cts.IsCancellationRequested)
                            {
                                handler.Post(() => applyButton.Text = activity.GetString(Resource.String.seekerog_apply_n, n));
                            }
                        });
                    };
                    handler.PostDelayed(pendingCount, 220);
                }
            };

            foreach (var chip in c.FormatChips.Concat(c.BitrateChips).Concat(c.SampleChips).Concat(c.DepthChips).Append(c.Lossless).Append(c.HiRes))
            {
                chip.CheckedChange += (s, e) => refresh();
            }
            c.Free.CheckedChange += (s, e) => refresh();
            foreach (var et in new[] { c.CustomFormats, c.MinBr, c.MaxBr, c.MinSize, c.MaxSize, c.MinDur, c.MaxDur, c.MaxQueue, c.MinSpeed, c.Exclude, c.Extra })
            {
                et.TextChanged += (s, e) => refresh();
            }

            // saved profiles: one tap loads them into the form
            var profilesGroup = view.FindViewById<ChipGroup>(Resource.Id.og_f_profiles)!;
            void RenderProfiles()
            {
                profilesGroup.RemoveAllViews();
                foreach (var p in SearchTools.FilterProfiles)
                {
                    var chip = (Chip)LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_chip_action, profilesGroup, false)!;
                    chip.Text = p.Name;
                    chip.SetChipIconResource(Resource.Drawable.og_tune);
                    string filter = p.Filter;
                    chip.Click += (s, e) =>
                    {
                        Populate(c, filter);
                        refresh();
                    };
                    profilesGroup.AddView(chip);
                }
                view.FindViewById<View>(Resource.Id.og_f_profiles_scroll)!.Visibility = SearchTools.FilterProfiles.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
            }
            RenderProfiles();

            Populate(c, currentFilter ?? string.Empty);
            refresh();

            var dialog = GlassSheet.Create(activity, view, 0.92f, fixedHeight: true);
            view.FindViewById<View>(Resource.Id.og_f_help)!.Click += (s, e) => ShowSyntaxHelp(activity);
            view.FindViewById<View>(Resource.Id.og_f_save)!.Click += (s, e) => PromptSaveProfile(activity, Build(c), RenderProfiles);
            view.FindViewById<View>(Resource.Id.og_f_clear)!.Click += (s, e) =>
            {
                Populate(c, string.Empty);
                refresh();
            };
            applyButton.Click += (s, e) =>
            {
                countCts?.Cancel();
                apply(Build(c));
                dialog.Dismiss();
            };
            dialog.DismissEvent += (s, e) =>
            {
                countCts?.Cancel();
                if (pendingCount != null) handler.RemoveCallbacks(pendingCount);
            };
            dialog.Show();
        }

        private static Android.Graphics.Color ThemeColor(Activity a, int attr)
        {
            var ta = a.Theme!.ObtainStyledAttributes(new[] { attr });
            var color = ta.GetColor(0, Android.Graphics.Color.Gray);
            ta.Recycle();
            return color;
        }

        /// <summary>Loads a filter string into the controls (best effort; the rest goes to "extra").</summary>
        private static void Populate(Controls c, string filter)
        {
            c.Populating = true;
            try
            {
                var q = AdvancedQuery.Parse(filter);
                foreach (var chip in c.FormatChips)
                {
                    string tag = chip.Tag!.ToString()!;
                    chip.Checked = tag == "dsd" ? q.Formats.Contains("dsf") : tag == "aiff" ? q.Formats.Contains("aiff") : q.Formats.Contains(tag);
                }
                var known = new HashSet<string>(Formats.Concat(new[] { "dsf", "dff", "aif", "oga" }), StringComparer.OrdinalIgnoreCase);
                if (q.Formats.Contains("alac")) known.Remove("m4a"); // the alac group expands to m4a+alac
                c.CustomFormats.Text = string.Join(" ", q.Formats.Where(f => !known.Contains(f)));
                c.Lossless.Checked = q.LosslessOnly;
                c.HiRes.Checked = q.HiResOnly;
                c.Free.Checked = q.FreeSlot == true;
                c.MinBr.Text = c.MaxBr.Text = c.MinSize.Text = c.MaxSize.Text = c.MinDur.Text = c.MaxDur.Text = c.MaxQueue.Text = c.MinSpeed.Text = string.Empty;
                foreach (var cond in q.BitRate)
                {
                    string v = ((int)cond.Value).ToString(CultureInfo.InvariantCulture);
                    if (cond.Op == AdvancedQuery.Op.Ge || cond.Op == AdvancedQuery.Op.Gt || cond.Op == AdvancedQuery.Op.Eq) c.MinBr.Text = v;
                    if (cond.Op == AdvancedQuery.Op.Le || cond.Op == AdvancedQuery.Op.Lt) c.MaxBr.Text = v;
                }
                foreach (var chip in c.BitrateChips)
                {
                    chip.Checked = chip.Tag!.ToString() == Txt(c.MinBr);
                }
                foreach (var cond in q.Size)
                {
                    string mb = (cond.Value / (1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);
                    if (cond.Op == AdvancedQuery.Op.Ge || cond.Op == AdvancedQuery.Op.Gt) c.MinSize.Text = mb;
                    if (cond.Op == AdvancedQuery.Op.Le || cond.Op == AdvancedQuery.Op.Lt) c.MaxSize.Text = mb;
                }
                foreach (var cond in q.Duration)
                {
                    string d = Library.LibraryRepository.FormatDuration((long)(cond.Value * 1000));
                    if (cond.Op == AdvancedQuery.Op.Ge || cond.Op == AdvancedQuery.Op.Gt) c.MinDur.Text = d;
                    if (cond.Op == AdvancedQuery.Op.Le || cond.Op == AdvancedQuery.Op.Lt) c.MaxDur.Text = d;
                }
                foreach (var cond in q.QueueLength) c.MaxQueue.Text = ((int)cond.Value).ToString(CultureInfo.InvariantCulture);
                foreach (var cond in q.UploadSpeed) c.MinSpeed.Text = ((int)(cond.Value / 1024)).ToString(CultureInfo.InvariantCulture);
                int sr = q.SampleRate.Count > 0 ? Array.IndexOf(SampleRates, (int)q.SampleRate[0].Value) : 0;
                for (int i = 0; i < c.SampleChips.Count; i++) c.SampleChips[i].Checked = i == Math.Max(0, sr);
                int bd = q.BitDepth.Count > 0 ? Array.IndexOf(BitDepths, (int)q.BitDepth[0].Value) : 0;
                for (int i = 0; i < c.DepthChips.Count; i++) c.DepthChips[i].Checked = i == Math.Max(0, bd);
                c.Exclude.Text = string.Join(" ", q.ExcludedWords);
                c.Extra.Text = string.Join(" ", q.Words.Concat(q.Phrases.Select(p => "\"" + p + "\"")).Concat(q.PartialWords.Select(p => p + "*")));
            }
            finally
            {
                c.Populating = false;
            }
        }

        public static void PromptSaveProfile(Activity activity, string filter, Action? onSaved = null)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.seekerog_profile_empty), ToastLength.Short);
                return;
            }
            var view = LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_text_prompt, null)!;
            var input = view.FindViewById<EditText>(Resource.Id.og_prompt_input)!;
            input.Hint = activity.GetString(Resource.String.seekerog_profile_name);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_save_profile)
                .SetView(view)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) =>
                {
                    SearchTools.SaveProfile(input.Text ?? string.Empty, filter);
                    SeekerOGStore.SaveSearchTools();
                    onSaved?.Invoke();
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
            int pad = (int)(22 * activity.Resources!.DisplayMetrics!.Density);
            tv.SetPadding(pad, pad / 2, pad, 0);
            tv.SetTextIsSelectable(true);
            tv.Text = activity.GetString(Resource.String.seekerog_syntax_help);
            tv.SetLineSpacing(0, 1.15f);
            var sv = new ScrollView(activity);
            sv.AddView(tv);
            var dialog = new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_syntax_help_title)
                .SetView(sv)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) => { })
                .Create();
            dialog.Show();
            GlassAppearance.ApplyBlurBehind(dialog.Window!);
        }
    }
}
