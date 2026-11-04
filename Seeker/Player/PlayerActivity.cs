using Android.Animation;
using Android.App;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using Common;
using Google.Android.Material.Dialog;
using Seeker.Helpers;
using Seeker.Library;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Seeker.Player
{
    /// <summary>
    /// SeekerOG Now Playing (redesign inspired by Namida's player UX, re-implemented from scratch:
    /// Namida is under a proprietary EULA, no code or assets were copied).
    ///  * artwork-driven colours: blurred artwork backdrop, tinted play button / progress / toggles
    ///  * artwork card that breathes with playback (smaller when paused), swipe to skip, tap to toggle
    ///  * transport row "position | prev | play | next | duration", press feedback on the play button
    ///  * secondary actions with a clear active dot, quality pills, "up next" card, queue / audio info sheets
    /// </summary>
    [Activity(Label = "SeekerOG", Exported = false, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
    public class PlayerActivity : ThemeableActivity
    {
        private ImageView art = null!, backdrop = null!, upNextArt = null!;
        private View artCard = null!, artFrame = null!, tint = null!, upNext = null!;
        private TextView title = null!, artist = null!, pos = null!, dur = null!, source = null!, upNextTitle = null!;
        private SeekBar seek = null!;
        private ImageButton toggle = null!, fav = null!;
        private LinearLayout qualityRow = null!;
        private View actShuffle = null!, actRepeat = null!;
        private Handler handler = null!;
        private bool userSeeking;
        private LibraryTrack? shownTrack;
        private Bitmap? shownArt;
        private bool? shownPlaying;
        private ArtPalette palette = new ArtPalette();
        private Color currentAccent = ArtPalette.DefaultAccent;
        private Color currentDeep = Color.ParseColor("#0A101C");
        private ValueAnimator? colorAnimator;
        private readonly Action tick;
        private GestureDetector? artGestures;

        public PlayerActivity()
        {
            tick = Tick;
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.seekerog_player);
            handler = new Handler(Looper.MainLooper!);

            // always light content on the artwork backdrop
            var insetsController = WindowCompat.GetInsetsController(Window!, Window!.DecorView);
            insetsController.AppearanceLightStatusBars = false;
            insetsController.AppearanceLightNavigationBars = false;
            var content = FindViewById<View>(Resource.Id.og_player_content)!;
            ViewCompat.SetOnApplyWindowInsetsListener(content, new PaddingInsetsListener());

            art = FindViewById<ImageView>(Resource.Id.og_player_art)!;
            artCard = FindViewById<View>(Resource.Id.og_player_art_card)!;
            artFrame = FindViewById<View>(Resource.Id.og_player_art_frame)!;
            backdrop = FindViewById<ImageView>(Resource.Id.og_player_backdrop)!;
            tint = FindViewById<View>(Resource.Id.og_player_tint)!;
            title = FindViewById<TextView>(Resource.Id.og_player_title)!;
            artist = FindViewById<TextView>(Resource.Id.og_player_artist)!;
            source = FindViewById<TextView>(Resource.Id.og_player_source)!;
            pos = FindViewById<TextView>(Resource.Id.og_player_pos)!;
            dur = FindViewById<TextView>(Resource.Id.og_player_dur)!;
            seek = FindViewById<SeekBar>(Resource.Id.og_player_seek)!;
            toggle = FindViewById<ImageButton>(Resource.Id.og_player_toggle)!;
            fav = FindViewById<ImageButton>(Resource.Id.og_player_fav)!;
            qualityRow = FindViewById<LinearLayout>(Resource.Id.og_player_quality_row)!;
            upNext = FindViewById<View>(Resource.Id.og_player_upnext)!;
            upNextArt = FindViewById<ImageView>(Resource.Id.og_upnext_art)!;
            upNextTitle = FindViewById<TextView>(Resource.Id.og_upnext_title)!;
            title.Selected = true; // marquee

            if (OperatingSystem.IsAndroidVersionAtLeast(31) && PreferencesState.GlassBlur && !PreferencesState.ReduceVisualEffects)
            {
                backdrop.SetRenderEffect(RenderEffect.CreateBlurEffect(60f, 60f, Shader.TileMode.Clamp!));
            }

            // keep the artwork square inside the space left by the other rows
            artFrame.LayoutChange += (s, e) =>
            {
                int avail = Math.Min(artFrame.Width - artFrame.PaddingLeft - artFrame.PaddingRight, artFrame.Height - artFrame.PaddingTop - artFrame.PaddingBottom);
                if (avail > 0 && (artCard.LayoutParameters!.Width != avail || artCard.LayoutParameters.Height != avail))
                {
                    var lp = (FrameLayout.LayoutParams)artCard.LayoutParameters;
                    lp.Width = avail;
                    lp.Height = avail;
                    lp.Gravity = GravityFlags.Center;
                    artCard.Post(() => artCard.LayoutParameters = lp);
                }
            };

            FindViewById<ImageButton>(Resource.Id.og_player_close)!.Click += (s, e) => Close();
            FindViewById<ImageButton>(Resource.Id.og_player_more)!.Click += (s, e) => ShowMore();
            toggle.Click += (s, e) =>
            {
                PressFeedback(toggle);
                PlaybackService.Instance?.Toggle();
            };
            FindViewById<ImageButton>(Resource.Id.og_player_next)!.Click += (s, e) => { MiniPlayerController.Pulse((View)s!); PlaybackService.Instance?.Next(true); };
            FindViewById<ImageButton>(Resource.Id.og_player_prev)!.Click += (s, e) => { MiniPlayerController.Pulse((View)s!); PlaybackService.Instance?.Previous(); };
            fav.Click += (s, e) =>
            {
                var t = PlaybackService.Current;
                if (t == null) return;
                LibraryStore.ToggleFavorite(t.Id);
                MiniPlayerController.Pulse(fav);
                RenderFavorite(t);
            };

            actShuffle = SetupAction(Resource.Id.og_act_shuffle, Resource.Drawable.og_shuffle, Resource.String.seekerog_player_shuffle, () => PlaybackService.SetShuffle(!PlaybackService.Shuffle));
            actRepeat = SetupAction(Resource.Id.og_act_repeat, Resource.Drawable.og_repeat, Resource.String.seekerog_player_repeat, PlaybackService.CycleRepeat);
            SetupAction(Resource.Id.og_act_queue, Resource.Drawable.og_queue, Resource.String.seekerog_queue, () => QueueSheet.Show(this));
            SetupAction(Resource.Id.og_act_eq, Resource.Drawable.og_equalizer, Resource.String.seekerog_equalizer, () => EqualizerDialog.Show(this));
            SetupAction(Resource.Id.og_act_info, Resource.Drawable.og_info, Resource.String.seekerog_audio_quality, () => AudioInfoSheet.Show(this));
            upNext.Click += (s, e) => QueueSheet.Show(this);

            seek.ProgressChanged += (s, e) =>
            {
                if (e.FromUser) pos.Text = LibraryRepository.FormatPosition(e.Progress);
            };
            seek.StartTrackingTouch += (s, e) => userSeeking = true;
            seek.StopTrackingTouch += (s, e) =>
            {
                userSeeking = false;
                PlaybackService.Instance?.SeekTo(seek.Progress);
            };

            artGestures = new GestureDetector(this, new ArtGestureListener(this));
            artCard.Touch += (s, e) =>
            {
                e.Handled = artGestures.OnTouchEvent(e.Event!);
            };

            ApplyColors(currentAccent, currentDeep, ArtPalette.DefaultAccent);
        }

        private View SetupAction(int id, int icon, int description, Action onClick)
        {
            var v = FindViewById<View>(id)!;
            v.FindViewById<ImageView>(Resource.Id.og_action_icon)!.SetImageResource(icon);
            v.ContentDescription = GetString(description);
            v.Click += (s, e) =>
            {
                MiniPlayerController.Pulse(v);
                onClick();
            };
            return v;
        }

        private void Close()
        {
            Finish();
            if (GlassAppearance.AnimationsEnabled)
            {
#pragma warning disable CA1422
                OverridePendingTransition(Resource.Animation.og_stay, Resource.Animation.og_slide_down);
#pragma warning restore CA1422
            }
        }

        public override void OnBackPressed()
        {
            Close();
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

        protected override void OnDestroy()
        {
            colorAnimator?.Cancel();
            base.OnDestroy();
        }

        private void OnStateChanged(object? sender, EventArgs e) => RunOnUiThread(Render);

        private void Tick()
        {
            var svc = PlaybackService.Instance;
            if (svc != null && !userSeeking)
            {
                int d = svc.DurationMs;
                seek.Max = Math.Max(1, d);
                seek.Progress = svc.PositionMs;
                pos.Text = LibraryRepository.FormatPosition(svc.PositionMs);
                dur.Text = LibraryRepository.FormatDuration(d);
            }
            handler.PostDelayed(tick, 500);
        }

        // ------------------------------------------------------------------ rendering

        private void Render()
        {
            var t = PlaybackService.Current;
            if (t == null)
            {
                title.Text = GetString(Resource.String.seekerog_nothing_playing);
                artist.Text = string.Empty;
                source.Text = string.Empty;
                art.SetImageResource(Resource.Drawable.og_music_note);
                upNext.Visibility = ViewStates.Gone;
                return;
            }
            source.Text = string.IsNullOrEmpty(PlaybackService.QueueSource) ? GetString(Resource.String.seekerog_nav_library) : PlaybackService.QueueSource;
            bool playing = PlaybackService.IsPlaying;
            if (shownPlaying != playing)
            {
                shownPlaying = playing;
                toggle.SetImageResource(playing ? Resource.Drawable.og_pause : Resource.Drawable.og_play);
                toggle.ContentDescription = GetString(playing ? Resource.String.seekerog_player_pause : Resource.String.seekerog_player_play);
                // the artwork "breathes": full size while playing, slightly smaller when paused
                float target = playing ? 1f : 0.9f;
                if (GlassAppearance.AnimationsEnabled)
                {
                    artCard.Animate()!.ScaleX(target).ScaleY(target).SetDuration(380).SetInterpolator(new Android.Views.Animations.OvershootInterpolator(1.1f)).Start();
                }
                else
                {
                    artCard.ScaleX = target;
                    artCard.ScaleY = target;
                }
            }
            SetActive(actShuffle, PlaybackService.Shuffle);
            SetActive(actRepeat, PlaybackService.Repeat != RepeatMode.Off);
            actRepeat.FindViewById<ImageView>(Resource.Id.og_action_icon)!.SetImageResource(PlaybackService.Repeat == RepeatMode.One ? Resource.Drawable.og_repeat_one : Resource.Drawable.og_repeat);
            RenderFavorite(t);
            RenderUpNext();

            if (shownTrack != t)
            {
                bool first = shownTrack == null;
                shownTrack = t;
                title.Text = t.Title;
                artist.Text = string.Join(" · ", new[] { t.Artist, t.Album }.Where(x => !string.IsNullOrEmpty(x)));
                if (!first && GlassAppearance.AnimationsEnabled)
                {
                    foreach (var v in new View[] { title, artist })
                    {
                        v.Alpha = 0f;
                        v.TranslationY = 12f;
                        v.Animate()!.Alpha(1f).TranslationY(0f).SetDuration(260).Start();
                    }
                }
                qualityRow.RemoveAllViews();
                var ctx = ApplicationContext!;
                Task.Run(() =>
                {
                    var q = ArtworkLoader.Probe(ctx, t);
                    RunOnUiThread(() =>
                    {
                        if (shownTrack == t) RenderQuality(q);
                    });
                });
            }
            var bmp = PlaybackService.CurrentArt;
            if (!ReferenceEquals(bmp, shownArt))
            {
                shownArt = bmp;
                RenderArtwork(bmp);
            }
        }

        private void RenderFavorite(LibraryTrack t)
        {
            LibraryStore.EnsureLoaded();
            bool isFav = LibraryStore.Favorites.Contains(t.Id);
            fav.SetImageResource(isFav ? Resource.Drawable.og_favorite : Resource.Drawable.og_favorite_border);
            fav.ImageTintList = ColorStateList.ValueOf(isFav ? currentAccent : Color.White);
        }

        private void RenderUpNext()
        {
            var next = PlaybackService.UpNext;
            if (next == null)
            {
                upNext.Visibility = ViewStates.Gone;
                return;
            }
            upNext.Visibility = ViewStates.Visible;
            upNextTitle.Text = next.Title + (string.IsNullOrEmpty(next.Artist) ? string.Empty : " · " + next.Artist);
            ThumbnailLoader.Bind(upNextArt, next, (int)(80 * Resources!.DisplayMetrics!.Density), Resource.Drawable.og_music_note);
        }

        private void RenderArtwork(Bitmap? bmp)
        {
            if (bmp != null)
            {
                art.SetImageBitmap(bmp);
                art.SetPadding(0, 0, 0, 0);
                art.ImageTintList = null;
            }
            else
            {
                art.SetImageResource(Resource.Drawable.og_music_note);
                int p = (int)(90 * Resources!.DisplayMetrics!.Density);
                art.SetPadding(p, p, p, p);
                art.ImageTintList = ColorStateList.ValueOf(Color.Argb(200, 255, 255, 255));
            }
            if (GlassAppearance.AnimationsEnabled)
            {
                art.Alpha = 0.3f;
                art.Animate()!.Alpha(1f).SetDuration(300).Start();
            }
            var backdropBmp = ArtPalette.BackdropFrom(bmp);
            backdrop.SetImageBitmap(backdropBmp);
            if (GlassAppearance.AnimationsEnabled)
            {
                backdrop.Alpha = 0f;
                backdrop.Animate()!.Alpha(backdropBmp == null ? 0f : 0.85f).SetDuration(450).Start();
            }
            else
            {
                backdrop.Alpha = backdropBmp == null ? 0f : 0.85f;
            }
            // colours computed off the UI thread from a tiny copy
            var source = bmp;
            Task.Run(() =>
            {
                var p = ArtPalette.FromBitmap(source);
                RunOnUiThread(() => AnimateTo(p));
            });
        }

        private void AnimateTo(ArtPalette p)
        {
            palette = p;
            colorAnimator?.Cancel();
            var fromAccent = currentAccent;
            var fromDeep = currentDeep;
            if (!GlassAppearance.AnimationsEnabled)
            {
                ApplyColors(p.Accent, p.Deep, p.OnAccent);
                return;
            }
            colorAnimator = ValueAnimator.OfFloat(0f, 1f);
            colorAnimator.SetDuration(500);
            colorAnimator.Update += (s, e) =>
            {
                float f = (float)e.Animation.AnimatedValue!;
                ApplyColors(Blend(fromAccent, p.Accent, f), Blend(fromDeep, p.Deep, f), p.OnAccent);
            };
            colorAnimator.Start();
        }

        private static Color Blend(Color a, Color b, float f) =>
            Color.Argb(255, (int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));

        private void ApplyColors(Color accent, Color deep, Color onAccent)
        {
            currentAccent = accent;
            currentDeep = deep;
            toggle.BackgroundTintList = ColorStateList.ValueOf(accent);
            toggle.ImageTintList = ColorStateList.ValueOf(onAccent);
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                // coloured glow under the play button (Namida-like tinted shadow)
                toggle.SetOutlineSpotShadowColor(accent);
                toggle.SetOutlineAmbientShadowColor(accent);
                artCard.SetOutlineSpotShadowColor(accent);
            }
            seek.ProgressTintList = ColorStateList.ValueOf(Color.White);
            seek.ThumbTintList = ColorStateList.ValueOf(Color.White);
            tint.SetBackgroundColor(ArtPalette.WithAlpha(deep, 0xB3));
            for (int i = 0; i < qualityRow.ChildCount; i++)
            {
                var pill = qualityRow.GetChildAt(i);
                if (pill?.Tag?.ToString() == "accent")
                {
                    pill.Background = AccentPill(accent, Resources!.DisplayMetrics!.Density);
                }
            }
            SetActive(actShuffle, PlaybackService.Shuffle);
            SetActive(actRepeat, PlaybackService.Repeat != RepeatMode.Off);
            var t = PlaybackService.Current;
            if (t != null) RenderFavorite(t);
        }

        private void SetActive(View action, bool active)
        {
            var icon = action.FindViewById<ImageView>(Resource.Id.og_action_icon)!;
            var dot = action.FindViewById<ImageView>(Resource.Id.og_action_dot)!;
            var c = active ? Lighten(currentAccent) : Color.Argb(150, 255, 255, 255);
            icon.ImageTintList = ColorStateList.ValueOf(c);
            dot.ImageTintList = ColorStateList.ValueOf(Lighten(currentAccent));
            dot.Visibility = active ? ViewStates.Visible : ViewStates.Invisible;
        }

        /// <summary>Makes the accent readable on the dark backdrop.</summary>
        private static Color Lighten(Color c)
        {
            float[] hsv = new float[3];
            Color.RGBToHSV(c.R, c.G, c.B, hsv);
            hsv[2] = Math.Max(hsv[2], 0.9f);
            hsv[1] = Math.Min(hsv[1], 0.6f);
            return Color.HSVToColor(hsv);
        }

        private void RenderQuality(ArtworkLoader.AudioQuality q)
        {
            qualityRow.RemoveAllViews();
            string unknown = GetString(Resource.String.seekerog_unknown);
            if (q.IsHiRes) AddPill("Hi-Res", true);
            else if (q.IsLossless) AddPill(GetString(Resource.String.seekerog_lossless), true);
            AddPill(q.Extension.Length > 0 ? q.Extension : unknown, false);
            if (q.SampleRateHz > 0) AddPill(q.SampleRateText(unknown), false);
            if (q.BitDepth.Length > 0) AddPill(q.BitDepth, false);
            if (q.BitrateKbps > 0) AddPill(q.BitrateText(unknown), false);
            if (q.Channels > 0) AddPill(q.ChannelsText(unknown), false);
        }

        private void AddPill(string text, bool accent)
        {
            var tv = new TextView(this)
            {
                Text = text,
                Gravity = GravityFlags.Center,
            };
            tv.SetTextColor(Color.White);
            tv.TextSize = 12;
            tv.SetTypeface(tv.Typeface, TypefaceStyle.Bold);
            float d = Resources!.DisplayMetrics!.Density;
            tv.SetPadding((int)(12 * d), 0, (int)(12 * d), 0);
            if (accent)
            {
                tv.Tag = "accent";
                tv.Background = AccentPill(currentAccent, d);
            }
            else
            {
                tv.SetBackgroundResource(Resource.Drawable.og_glass_dark_surface);
            }
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, (int)(28 * d));
            lp.MarginEnd = (int)(6 * d);
            tv.LayoutParameters = lp;
            tv.Click += (s, e) => AudioInfoSheet.Show(this);
            qualityRow.AddView(tv);
            if (GlassAppearance.AnimationsEnabled)
            {
                tv.Alpha = 0f;
                tv.Animate()!.Alpha(1f).SetStartDelay(qualityRow.ChildCount * 40).SetDuration(200).Start();
            }
        }

        private static Android.Graphics.Drawables.GradientDrawable AccentPill(Color accent, float density)
        {
            var g = new Android.Graphics.Drawables.GradientDrawable();
            g.SetColor(Lighten(accent).ToArgb());
            g.SetCornerRadius(14 * density);
            g.Alpha = 0xE6;
            return g;
        }

        private void PressFeedback(View v)
        {
            if (!GlassAppearance.AnimationsEnabled) return;
            v.Animate()!.ScaleX(0.92f).ScaleY(0.92f).SetDuration(90).WithEndAction(new Java.Lang.Runnable(() =>
                v.Animate()!.ScaleX(1f).ScaleY(1f).SetInterpolator(new Android.Views.Animations.OvershootInterpolator(2f)).SetDuration(260).Start())).Start();
        }

        private void ShowMore()
        {
            var t = PlaybackService.Current;
            if (t == null) return;
            string[] items =
            {
                GetString(Resource.String.seekerog_add_to_playlist),
                GetString(Resource.String.seekerog_audio_quality),
                GetString(Resource.String.seekerog_equalizer),
                GetString(Resource.String.seekerog_nav_library),
                GetString(Resource.String.seekerog_stop_playback),
            };
            var dialog = new MaterialAlertDialogBuilder(this)
                .SetTitle(t.Title)
                .SetItems(items, (s, e) =>
                {
                    switch (e.Which)
                    {
                        case 0: LibraryActivity.PromptAddToPlaylist(this, t); break;
                        case 1: AudioInfoSheet.Show(this); break;
                        case 2: EqualizerDialog.Show(this); break;
                        case 3: StartActivity(new Android.Content.Intent(this, typeof(LibraryActivity))); break;
                        case 4: PlaybackService.Instance?.StopAll(); Close(); break;
                    }
                })
                .Create();
            dialog.Show();
            GlassAppearance.ApplyBlurBehind(dialog.Window!);
        }

        private class ArtGestureListener : GestureDetector.SimpleOnGestureListener
        {
            private readonly PlayerActivity a;
            public ArtGestureListener(PlayerActivity a) { this.a = a; }
            public override bool OnDown(MotionEvent e) => true;
            public override bool OnSingleTapUp(MotionEvent e)
            {
                a.PressFeedback(a.toggle);
                PlaybackService.Instance?.Toggle();
                return true;
            }
            public override bool OnFling(MotionEvent? e1, MotionEvent e2, float velocityX, float velocityY)
            {
                if (Math.Abs(velocityX) < Math.Abs(velocityY) || Math.Abs(velocityX) < 600)
                {
                    return false;
                }
                bool next = velocityX < 0;
                if (GlassAppearance.AnimationsEnabled)
                {
                    float w = a.artCard.Width;
                    a.artCard.Animate()!.TranslationX(next ? -w * 0.25f : w * 0.25f).Alpha(0.4f).SetDuration(120).WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        a.artCard.TranslationX = next ? w * 0.2f : -w * 0.2f;
                        a.artCard.Animate()!.TranslationX(0f).Alpha(1f).SetDuration(220).Start();
                    })).Start();
                }
                if (next) PlaybackService.Instance?.Next(true);
                else PlaybackService.Instance?.Previous();
                return true;
            }
        }

        private class PaddingInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
        {
            private int baseTop = -1, baseBottom;
            public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
            {
                if (baseTop < 0)
                {
                    baseTop = v.PaddingTop;
                    baseBottom = v.PaddingBottom;
                }
                var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
                v.SetPadding(v.PaddingLeft, baseTop + bars.Top, v.PaddingRight, baseBottom + bars.Bottom);
                return insets;
            }
        }
    }
}
