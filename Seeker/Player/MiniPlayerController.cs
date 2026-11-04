using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.ProgressIndicator;
using Seeker.Library;
using System;

namespace Seeker.Player
{
    /// <summary>
    /// Binds the shared mini player card (seekerog_mini_player.xml) to the playback state.
    /// Used by the main screen (above the bottom bar) and the library. Call Attach() in OnResume
    /// and Detach() in OnPause so no listener outlives the activity.
    /// </summary>
    public sealed class MiniPlayerController
    {
        private readonly Activity activity;
        private readonly View root;
        private readonly ImageView art;
        private readonly TextView title, artist;
        private readonly ImageButton toggle;
        private readonly LinearProgressIndicator progress;
        private readonly Handler handler = new Handler(Looper.MainLooper!);
        private readonly Action tick;
        private LibraryTrack? shown;
        private bool attached;

        public MiniPlayerController(Activity activity, View container)
        {
            this.activity = activity;
            root = container.FindViewById<View>(Resource.Id.og_mini_root)!;
            art = root.FindViewById<ImageView>(Resource.Id.og_mini_art)!;
            title = root.FindViewById<TextView>(Resource.Id.og_mini_title)!;
            artist = root.FindViewById<TextView>(Resource.Id.og_mini_artist)!;
            toggle = root.FindViewById<ImageButton>(Resource.Id.og_mini_toggle)!;
            progress = root.FindViewById<LinearProgressIndicator>(Resource.Id.og_mini_progress)!;
            tick = Tick;
            root.Click += (s, e) => OpenPlayer(activity);
            toggle.Click += (s, e) =>
            {
                Pulse(toggle);
                PlaybackService.Instance?.Toggle();
            };
            root.FindViewById<ImageButton>(Resource.Id.og_mini_next)!.Click += (s, e) => PlaybackService.Instance?.Next(true);
        }

        public static void OpenPlayer(Activity activity)
        {
            activity.StartActivity(new Intent(activity, typeof(PlayerActivity)));
            if (GlassAppearance.AnimationsEnabled)
            {
#pragma warning disable CA1422
                activity.OverridePendingTransition(Resource.Animation.og_slide_up, Resource.Animation.og_stay);
#pragma warning restore CA1422
            }
        }

        internal static void Pulse(View v)
        {
            if (!GlassAppearance.AnimationsEnabled) return;
            v.Animate()!.ScaleX(0.88f).ScaleY(0.88f).SetDuration(90).WithEndAction(new Java.Lang.Runnable(() =>
                v.Animate()!.ScaleX(1f).ScaleY(1f).SetDuration(160).Start())).Start();
        }

        public void Attach()
        {
            if (attached) return;
            attached = true;
            PlaybackService.StateChanged += OnChanged;
            Render();
            handler.Post(tick);
        }

        public void Detach()
        {
            if (!attached) return;
            attached = false;
            PlaybackService.StateChanged -= OnChanged;
            handler.RemoveCallbacks(tick);
        }

        private void OnChanged(object? s, EventArgs e) => activity.RunOnUiThread(Render);

        private void Tick()
        {
            var svc = PlaybackService.Instance;
            if (svc != null && root.Visibility == ViewStates.Visible)
            {
                int d = svc.DurationMs;
                progress.Progress = d > 0 ? (int)(1000L * svc.PositionMs / d) : 0;
            }
            if (attached)
            {
                handler.PostDelayed(tick, 1000);
            }
        }

        private void Render()
        {
            var t = PlaybackService.Current;
            if (t == null)
            {
                if (root.Visibility != ViewStates.Gone)
                {
                    root.Visibility = ViewStates.Gone;
                }
                shown = null;
                return;
            }
            if (root.Visibility != ViewStates.Visible)
            {
                root.Visibility = ViewStates.Visible;
                if (GlassAppearance.AnimationsEnabled)
                {
                    root.TranslationY = 40f;
                    root.Alpha = 0f;
                    root.Animate()!.TranslationY(0f).Alpha(1f).SetDuration(260).Start();
                }
            }
            title.Text = t.Title;
            artist.Text = string.IsNullOrEmpty(t.Artist) ? t.Album : t.Artist;
            toggle.SetImageResource(PlaybackService.IsPlaying ? Resource.Drawable.og_pause : Resource.Drawable.og_play);
            if (shown != t)
            {
                shown = t;
                int px = (int)(96 * activity.Resources!.DisplayMetrics!.Density);
                ThumbnailLoader.Bind(art, t, px, Resource.Drawable.og_music_note);
            }
        }
    }
}
