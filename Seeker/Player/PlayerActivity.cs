using Common;
using Seeker.Helpers;
using Android.App;
using Android.OS;
using Android.Widget;
using Google.Android.Material.Dialog;
using Seeker.Library;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Seeker.Player
{
    [Activity(Label = "SeekerOG", Exported = false, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
    public class PlayerActivity : ThemeableActivity
    {
        private ImageView art = null!;
        private TextView title = null!, artist = null!, pos = null!, dur = null!, quality = null!, output = null!;
        private SeekBar seek = null!;
        private ImageButton toggle = null!, shuffle = null!, repeat = null!, fav = null!;
        private Handler handler = null!;
        private bool userSeeking;
        private LibraryTrack? shownTrack;
        private readonly Action tick;

        public PlayerActivity()
        {
            tick = Tick;
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.seekerog_player);
            handler = new Handler(Looper.MainLooper!);
            var toolbar = FindViewById<Google.Android.Material.AppBar.MaterialToolbar>(Resource.Id.og_player_toolbar)!;
            toolbar.NavigationClick += (s, e) => Finish();
            art = FindViewById<ImageView>(Resource.Id.og_player_art)!;
            title = FindViewById<TextView>(Resource.Id.og_player_title)!;
            artist = FindViewById<TextView>(Resource.Id.og_player_artist)!;
            pos = FindViewById<TextView>(Resource.Id.og_player_pos)!;
            dur = FindViewById<TextView>(Resource.Id.og_player_dur)!;
            quality = FindViewById<TextView>(Resource.Id.og_player_quality)!;
            output = FindViewById<TextView>(Resource.Id.og_player_output)!;
            seek = FindViewById<SeekBar>(Resource.Id.og_player_seek)!;
            toggle = FindViewById<ImageButton>(Resource.Id.og_player_toggle)!;
            shuffle = FindViewById<ImageButton>(Resource.Id.og_player_shuffle)!;
            repeat = FindViewById<ImageButton>(Resource.Id.og_player_repeat)!;
            fav = FindViewById<ImageButton>(Resource.Id.og_player_fav)!;

            toggle.Click += (s, e) => PlaybackService.Instance?.Toggle();
            FindViewById<ImageButton>(Resource.Id.og_player_next)!.Click += (s, e) => PlaybackService.Instance?.Next(true);
            FindViewById<ImageButton>(Resource.Id.og_player_prev)!.Click += (s, e) => PlaybackService.Instance?.Previous();
            shuffle.Click += (s, e) => PlaybackService.SetShuffle(!PlaybackService.Shuffle);
            repeat.Click += (s, e) => PlaybackService.CycleRepeat();
            fav.Click += (s, e) =>
            {
                var t = PlaybackService.Current;
                if (t != null)
                {
                    LibraryStore.ToggleFavorite(t.Id);
                    Render();
                }
            };
            FindViewById<ImageButton>(Resource.Id.og_player_eq)!.Click += (s, e) => EqualizerDialog.Show(this);
            FindViewById<ImageButton>(Resource.Id.og_player_queue)!.Click += (s, e) => ShowQueue();
            FindViewById<ImageButton>(Resource.Id.og_player_addlist)!.Click += (s, e) =>
            {
                var t = PlaybackService.Current;
                if (t != null) LibraryActivity.PromptAddToPlaylist(this, t);
            };
            seek.ProgressChanged += (s, e) =>
            {
                if (e.FromUser) pos.Text = LibraryRepository.FormatDuration(e.Progress);
            };
            seek.StartTrackingTouch += (s, e) => userSeeking = true;
            seek.StopTrackingTouch += (s, e) =>
            {
                userSeeking = false;
                PlaybackService.Instance?.SeekTo(seek.Progress);
            };
        }

        protected override void OnResume()
        {
            base.OnResume();
            PlaybackService.StateChanged += OnStateChanged;
            Render();
            handler.Post(tick);
        }

        protected override void OnPause()
        {
            PlaybackService.StateChanged -= OnStateChanged;
            handler.RemoveCallbacks(tick);
            base.OnPause();
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            RunOnUiThread(Render);
        }

        private void Tick()
        {
            var svc = PlaybackService.Instance;
            if (svc != null && !userSeeking)
            {
                int d = svc.DurationMs;
                seek.Max = Math.Max(1, d);
                seek.Progress = svc.PositionMs;
                pos.Text = LibraryRepository.FormatDuration(svc.PositionMs);
                dur.Text = LibraryRepository.FormatDuration(d);
            }
            handler.PostDelayed(tick, 500);
        }

        private void Render()
        {
            var t = PlaybackService.Current;
            if (t == null)
            {
                title.Text = GetString(Resource.String.seekerog_nothing_playing);
                artist.Text = string.Empty;
                art.SetImageResource(Resource.Drawable.og_album);
                return;
            }
            title.Text = t.Title;
            artist.Text = string.Join(" · ", new[] { t.Artist, t.Album }.Where(x => !string.IsNullOrEmpty(x)));
            toggle.SetImageResource(PlaybackService.IsPlaying ? Resource.Drawable.og_pause : Resource.Drawable.og_play);
            shuffle.Alpha = PlaybackService.Shuffle ? 1f : 0.4f;
            repeat.SetImageResource(PlaybackService.Repeat == RepeatMode.One ? Resource.Drawable.og_repeat_one : Resource.Drawable.og_repeat);
            repeat.Alpha = PlaybackService.Repeat == RepeatMode.Off ? 0.4f : 1f;
            LibraryStore.EnsureLoaded();
            fav.SetImageResource(LibraryStore.Favorites.Contains(t.Id) ? Resource.Drawable.og_favorite : Resource.Drawable.og_favorite_border);
            var bmp = PlaybackService.CurrentArt;
            if (bmp != null)
            {
                art.SetImageBitmap(bmp);
                art.SetPadding(0, 0, 0, 0);
            }
            else
            {
                art.SetImageResource(Resource.Drawable.og_album);
                int p = (int)(70 * Resources!.DisplayMetrics!.Density);
                art.SetPadding(p, p, p, p);
            }
            if (shownTrack != t)
            {
                shownTrack = t;
                quality.Text = "…";
                var ctx = ApplicationContext!;
                Task.Run(() =>
                {
                    string q = ArtworkLoader.DescribeQuality(ctx, t);
                    string o = ArtworkLoader.DescribeOutput(ctx);
                    RunOnUiThread(() =>
                    {
                        if (shownTrack == t)
                        {
                            quality.Text = q;
                            output.Text = o;
                        }
                    });
                });
            }
        }

        private void ShowQueue()
        {
            var queue = PlaybackService.Queue;
            if (queue.Count == 0)
            {
                return;
            }
            var items = queue.Select((t, i) => (i == PlaybackService.Index ? "▶ " : "") + t.Title + (string.IsNullOrEmpty(t.Artist) ? "" : " — " + t.Artist)).ToArray();
            var dialog = new MaterialAlertDialogBuilder(this)
                .SetTitle(Resource.String.seekerog_queue)
                .SetItems(items, (s, e) => PlaybackService.JumpTo(e.Which))
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) => { })
                .Create();
            dialog.Show();
            GlassAppearance.ApplyBlurBehind(dialog.Window!);
        }
    }
}
