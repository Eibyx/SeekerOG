using Android.App;
using Android.Views;
using Android.Widget;
using Seeker.Library;
using System.Globalization;
using System.Threading.Tasks;

namespace Seeker.Player
{
    /// <summary>
    /// "Audio path" sheet: badges (Hi-Res / Lossless / Lossy), a 2-column grid of the real file facts,
    /// the current output route and the equalizer shortcut. Unknown values are labelled as such.
    /// </summary>
    public static class AudioInfoSheet
    {
        public static void Show(Activity activity, LibraryTrack? track = null)
        {
            track ??= PlaybackService.Current;
            if (track == null) return;
            var t = track;
            var view = LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_audio_info_sheet, null)!;
            view.FindViewById<TextView>(Resource.Id.og_ai_title)!.Text = t.Title;
            view.FindViewById<TextView>(Resource.Id.og_ai_file)!.Text = t.Folder + "/" + t.DisplayName;
            ThumbnailLoader.Bind(view.FindViewById<ImageView>(Resource.Id.og_ai_art)!, t, (int)(128 * activity.Resources!.DisplayMetrics!.Density), Resource.Drawable.og_music_note);
            var grid = view.FindViewById<GridLayout>(Resource.Id.og_ai_grid)!;
            var badges = view.FindViewById<LinearLayout>(Resource.Id.og_ai_badges)!;
            var output = view.FindViewById<TextView>(Resource.Id.og_ai_output)!;
            output.Text = "…";
            view.FindViewById<View>(Resource.Id.og_ai_eq)!.Click += (s, e) => EqualizerDialog.Show(activity);
            var dialog = GlassSheet.Create(activity, view, 0.9f);
            dialog.Show();

            var ctx = activity.ApplicationContext!;
            Task.Run(() =>
            {
                var q = ArtworkLoader.Probe(ctx, t);
                string route = ArtworkLoader.DescribeOutput(ctx);
                activity.RunOnUiThread(() =>
                {
                    string unknown = activity.GetString(Resource.String.seekerog_unknown);
                    output.Text = route;
                    if (q.IsHiRes) AddBadge(activity, badges, "Hi-Res", true);
                    AddBadge(activity, badges, activity.GetString(q.IsLossless ? Resource.String.seekerog_lossless : Resource.String.seekerog_lossy), q.IsLossless && !q.IsHiRes);
                    AddBadge(activity, badges, q.Extension.Length > 0 ? q.Extension : unknown, false);
                    AddTile(activity, grid, Resource.String.seekerog_q_codec, q.Codec.Length == 0 ? unknown : q.Codec == "raw" ? activity.GetString(Resource.String.seekerog_codec_pcm) : q.Codec.ToUpperInvariant());
                    AddTile(activity, grid, Resource.String.seekerog_q_bitrate, q.BitrateText(unknown));
                    AddTile(activity, grid, Resource.String.seekerog_q_samplerate, q.SampleRateText(unknown));
                    AddTile(activity, grid, Resource.String.seekerog_q_bitdepth, q.BitDepthText(unknown));
                    AddTile(activity, grid, Resource.String.seekerog_q_channels, q.ChannelsText(unknown));
                    AddTile(activity, grid, Resource.String.seekerog_q_duration, LibraryRepository.FormatDuration(t.DurationMs));
                    AddTile(activity, grid, Resource.String.seekerog_q_size, SimpleHelpers.GetHumanReadableSize(t.Size));
                    AddTile(activity, grid, Resource.String.seekerog_q_container, string.IsNullOrEmpty(t.MimeType) ? unknown : t.MimeType);
                });
            });
        }

        private static void AddBadge(Activity a, LinearLayout row, string text, bool accent)
        {
            var tv = new TextView(a) { Text = text, Gravity = GravityFlags.Center };
            tv.TextSize = 12;
            tv.SetTypeface(tv.Typeface, Android.Graphics.TypefaceStyle.Bold);
            float d = a.Resources!.DisplayMetrics!.Density;
            tv.SetPadding((int)(14 * d), 0, (int)(14 * d), 0);
            tv.SetBackgroundResource(accent ? Resource.Drawable.og_info_pill_accent : Resource.Drawable.og_info_pill);
            var attrs = a.Theme!.ObtainStyledAttributes(new[] { accent ? Resource.Attribute.glassOnAccent : Resource.Attribute.glassTextPrimary });
            tv.SetTextColor(attrs.GetColor(0, Android.Graphics.Color.White));
            attrs.Recycle();
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, (int)(30 * d)) { MarginEnd = (int)(6 * d) };
            row.AddView(tv, lp);
        }

        private static void AddTile(Activity a, GridLayout grid, int label, string value)
        {
            var tile = LayoutInflater.From(a)!.Inflate(Resource.Layout.seekerog_info_tile, grid, false)!;
            tile.FindViewById<TextView>(Resource.Id.og_tile_label)!.SetText(label);
            tile.FindViewById<TextView>(Resource.Id.og_tile_value)!.Text = value;
            var lp = new GridLayout.LayoutParams(GridLayout.InvokeSpec(GridLayout.Undefined), GridLayout.InvokeSpec(GridLayout.Undefined, 1f));
            lp.Width = 0;
            float d = a.Resources!.DisplayMetrics!.Density;
            lp.SetMargins((int)(4 * d), (int)(4 * d), (int)(4 * d), (int)(4 * d));
            grid.AddView(tile, lp);
        }
    }
}
