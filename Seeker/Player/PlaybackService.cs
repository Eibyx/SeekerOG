using Common;
using Seeker.Helpers;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Seeker.Library;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Seeker.Player
{
    public enum RepeatMode { Off = 0, All = 1, One = 2 }

    /// <summary>
    /// SeekerOG local music player.
    ///  * Android MediaPlayer (platform decoders: MP3, FLAC, WAV, AAC/M4A, OGG Vorbis, Opus, and ALAC on
    ///    devices whose platform supports it). Unsupported files are reported and skipped.
    ///  * Foreground service of type mediaPlayback + MediaSession: lock screen / notification controls,
    ///    Bluetooth and headset buttons, audio focus, pause when headphones are unplugged.
    ///  * Completely independent from the Soulseek transfer services (own process thread, no shared locks),
    ///    so playback never blocks P2P transfers.
    /// The audio is decoded and mixed by Android; SeekerOG does not claim bit-perfect output.
    /// </summary>
    [Service(Name = "com.eibyx.seekerog.PlaybackService", Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaPlayback)]
    public class PlaybackService : Service, AudioManager.IOnAudioFocusChangeListener
    {
        public const string ActionToggle = "com.eibyx.seekerog.player.TOGGLE";
        public const string ActionNext = "com.eibyx.seekerog.player.NEXT";
        public const string ActionPrev = "com.eibyx.seekerog.player.PREV";
        public const string ActionStop = "com.eibyx.seekerog.player.STOP";
        public const string ActionStart = "com.eibyx.seekerog.player.START";
        private const string ChannelId = "seekerog_playback";
        private const int NotificationId = 0x5E0;

        public static PlaybackService? Instance { get; private set; }

        // ---- queue state (static so activities can render it without binding) ----
        public static List<LibraryTrack> Queue { get; private set; } = new List<LibraryTrack>();
        public static int Index { get; private set; } = -1;
        public static bool Shuffle { get; private set; }
        public static RepeatMode Repeat { get; private set; } = RepeatMode.Off;
        public static event EventHandler? StateChanged;
        public static LibraryTrack? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;
        public static bool IsPlaying => Instance?.player?.IsPlaying ?? false;
        public static int AudioSessionId => Instance?.player?.AudioSessionId ?? 0;

        private static List<int> shuffleOrder = new List<int>();
        private static readonly Random rng = new Random();

        private MediaPlayer? player;
        private MediaSession? session;
        private AudioManager? audioManager;
        private AudioFocusRequestClass? focusRequest;
        private bool prepared;
        private bool resumeOnFocusGain;
        private int consecutiveErrors;
        private Bitmap? currentArt;
        private NoisyReceiver? noisyReceiver;
        private Handler? handler;

        public static void PlayQueue(Context context, IList<LibraryTrack> tracks, int startIndex)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return;
            }
            Queue = tracks.ToList();
            Index = Math.Max(0, Math.Min(startIndex, Queue.Count - 1));
            RebuildShuffle();
            var intent = new Intent(context, typeof(PlaybackService));
            intent.SetAction(ActionStart);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                context.StartForegroundService(intent);
            }
            else
            {
                context.StartService(intent);
            }
            if (Instance != null)
            {
                Instance.LoadCurrent(true);
            }
        }

        public static void AddToQueue(LibraryTrack t)
        {
            Queue.Add(t);
            RebuildShuffle();
            RaiseChanged();
        }

        public static void SetShuffle(bool on)
        {
            Shuffle = on;
            RebuildShuffle();
            RaiseChanged();
        }

        public static void CycleRepeat()
        {
            Repeat = Repeat == RepeatMode.Off ? RepeatMode.All : Repeat == RepeatMode.All ? RepeatMode.One : RepeatMode.Off;
            RaiseChanged();
        }

        public static void JumpTo(int index)
        {
            if (index < 0 || index >= Queue.Count)
            {
                return;
            }
            Index = index;
            Instance?.LoadCurrent(true);
        }

        private static void RebuildShuffle()
        {
            shuffleOrder = Enumerable.Range(0, Queue.Count).OrderBy(_ => rng.Next()).ToList();
        }

        private static void RaiseChanged()
        {
            try
            {
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Logger.Debug("player listener failed " + e.Message);
            }
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            handler = new Handler(Looper.MainLooper!);
            audioManager = (AudioManager?)GetSystemService(AudioService);
            CreateChannel();
            session = new MediaSession(this, "SeekerOG");
            session.SetCallback(new SessionCallback(this));
            var launch = new Intent(this, typeof(PlayerActivity));
            session.SetSessionActivity(PendingIntent.GetActivity(this, 1, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
            session.Active = true;
            noisyReceiver = new NoisyReceiver(this);
            RegisterReceiver(noisyReceiver, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            // Must call StartForeground promptly after StartForegroundService.
            StartForegroundCompat(BuildNotification());
            switch (intent?.Action)
            {
                case ActionStart:
                    LoadCurrent(true);
                    break;
                case ActionToggle:
                    Toggle();
                    break;
                case ActionNext:
                    Next(true);
                    break;
                case ActionPrev:
                    Previous();
                    break;
                case ActionStop:
                    StopAll();
                    break;
            }
            return StartCommandResult.NotSticky;
        }

        private void StartForegroundCompat(Notification n)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    StartForeground(NotificationId, n, Android.Content.PM.ForegroundService.TypeMediaPlayback);
                }
                else
                {
                    StartForeground(NotificationId, n);
                }
            }
            catch (Exception e)
            {
                Logger.Debug("player StartForeground failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- playback

        private void LoadCurrent(bool playWhenReady)
        {
            var track = Current;
            if (track == null)
            {
                return;
            }
            ReleasePlayer();
            prepared = false;
            currentArt = null;
            try
            {
                player = new MediaPlayer();
                player.SetAudioAttributes(new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Media)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!);
                player.SetWakeMode(this, WakeLockFlags.Partial);
                player.SetDataSource(this, Android.Net.Uri.Parse(track.Uri)!);
                player.Prepared += (s, e) =>
                {
                    prepared = true;
                    consecutiveErrors = 0;
                    if (playWhenReady && RequestFocus())
                    {
                        player?.Start();
                    }
                    UpdateSessionAndNotification();
                };
                player.Completion += (s, e) => OnTrackCompleted();
                player.Error += (s, e) =>
                {
                    e.Handled = true;
                    consecutiveErrors++;
                    Logger.Debug($"player error what={e.What} extra={e.Extra}");
                    SeekerApplication.Toaster?.ShowToast(GetString(Resource.String.seekerog_player_unsupported) + " " + track.DisplayName, Android.Widget.ToastLength.Short);
                    if (consecutiveErrors < 3 && Queue.Count > 1)
                    {
                        handler?.Post(() => Next(false));
                    }
                    else
                    {
                        UpdateSessionAndNotification();
                    }
                };
                player.PrepareAsync();
            }
            catch (Exception e)
            {
                Logger.Debug("player load failed: " + e.Message);
                SeekerApplication.Toaster?.ShowToast(GetString(Resource.String.seekerog_player_unsupported) + " " + track.DisplayName, Android.Widget.ToastLength.Short);
            }
            LoadArtAsync(track);
            UpdateSessionAndNotification();
        }

        private void LoadArtAsync(LibraryTrack track)
        {
            Task.Run(() =>
            {
                Bitmap? bmp = ArtworkLoader.LoadEmbedded(this, track, 512);
                handler?.Post(() =>
                {
                    if (Current == track)
                    {
                        currentArt = bmp;
                        UpdateSessionAndNotification();
                    }
                });
            });
        }

        public static Bitmap? CurrentArt => Instance?.currentArt;

        private void OnTrackCompleted()
        {
            if (Repeat == RepeatMode.One)
            {
                player?.SeekTo(0);
                player?.Start();
                return;
            }
            Next(false);
        }

        public void Toggle()
        {
            if (player == null)
            {
                LoadCurrent(true);
                return;
            }
            if (player.IsPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void Play()
        {
            if (player == null || !prepared)
            {
                LoadCurrent(true);
                return;
            }
            if (RequestFocus())
            {
                player.Start();
            }
            UpdateSessionAndNotification();
        }

        public void Pause()
        {
            if (player != null && prepared && player.IsPlaying)
            {
                player.Pause();
            }
            UpdateSessionAndNotification();
        }

        public void SeekTo(int ms)
        {
            if (player != null && prepared)
            {
                player.SeekTo(ms);
                UpdateSessionAndNotification();
            }
        }

        public int PositionMs => player != null && prepared ? player.CurrentPosition : 0;
        public int DurationMs => player != null && prepared ? player.Duration : (int)(Current?.DurationMs ?? 0);

        public void Next(bool userInitiated)
        {
            if (Queue.Count == 0)
            {
                return;
            }
            int pos = Shuffle ? shuffleOrder.IndexOf(Index) : Index;
            pos++;
            if (pos >= Queue.Count)
            {
                if (Repeat == RepeatMode.Off && !userInitiated)
                {
                    Pause();
                    player?.SeekTo(0);
                    return;
                }
                pos = 0;
            }
            Index = Shuffle && shuffleOrder.Count == Queue.Count ? shuffleOrder[pos] : pos;
            LoadCurrent(true);
        }

        public void Previous()
        {
            if (Queue.Count == 0)
            {
                return;
            }
            if (PositionMs > 3000)
            {
                SeekTo(0);
                return;
            }
            int pos = Shuffle ? shuffleOrder.IndexOf(Index) : Index;
            pos = pos <= 0 ? Queue.Count - 1 : pos - 1;
            Index = Shuffle && shuffleOrder.Count == Queue.Count ? shuffleOrder[pos] : pos;
            LoadCurrent(true);
        }

        public void StopAll()
        {
            ReleasePlayer();
            AbandonFocus();
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                StopForeground(StopForegroundFlags.Remove);
            }
            else
            {
#pragma warning disable CA1422
                StopForeground(true);
#pragma warning restore CA1422
            }
            StopSelf();
            RaiseChanged();
        }

        private void ReleasePlayer()
        {
            try
            {
                player?.Reset();
                player?.Release();
            }
            catch (Exception)
            {
            }
            player = null;
            prepared = false;
        }

        // ---------------------------------------------------------------- audio focus

        private bool RequestFocus()
        {
            if (audioManager == null)
            {
                return true;
            }
            AudioFocusRequest result;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                    .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
                    .SetOnAudioFocusChangeListener(this)!
                    .SetWillPauseWhenDucked(false)!
                    .Build();
                result = audioManager.RequestAudioFocus(focusRequest!);
            }
            else
            {
#pragma warning disable CA1422
                result = audioManager.RequestAudioFocus(this, Stream.Music, AudioFocus.Gain);
#pragma warning restore CA1422
            }
            return result == AudioFocusRequest.Granted;
        }

        private void AbandonFocus()
        {
            if (audioManager == null)
            {
                return;
            }
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                if (focusRequest != null)
                {
                    audioManager.AbandonAudioFocusRequest(focusRequest);
                }
            }
            else
            {
#pragma warning disable CA1422
                audioManager.AbandonAudioFocus(this);
#pragma warning restore CA1422
            }
        }

        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            switch (focusChange)
            {
                case AudioFocus.Loss:
                    resumeOnFocusGain = false;
                    Pause();
                    break;
                case AudioFocus.LossTransient:
                    resumeOnFocusGain = player?.IsPlaying ?? false;
                    Pause();
                    break;
                case AudioFocus.LossTransientCanDuck:
                    player?.SetVolume(0.2f, 0.2f);
                    break;
                case AudioFocus.Gain:
                    player?.SetVolume(1f, 1f);
                    if (resumeOnFocusGain)
                    {
                        resumeOnFocusGain = false;
                        Play();
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- session + notification

        private void UpdateSessionAndNotification()
        {
            var track = Current;
            if (session != null)
            {
                long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause |
                               PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionSeekTo | PlaybackState.ActionStop;
                var state = new PlaybackState.Builder()
                    .SetActions(actions)!
                    .SetState(IsPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused, PositionMs, 1f)!
                    .Build();
                session.SetPlaybackState(state);
                if (track != null)
                {
                    var meta = new MediaMetadata.Builder()
                        .PutString(MediaMetadata.MetadataKeyTitle, track.Title)!
                        .PutString(MediaMetadata.MetadataKeyArtist, track.Artist)!
                        .PutString(MediaMetadata.MetadataKeyAlbum, track.Album)!
                        .PutLong(MediaMetadata.MetadataKeyDuration, DurationMs)!;
                    if (currentArt != null)
                    {
                        meta.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, currentArt);
                    }
                    session.SetMetadata(meta.Build());
                }
            }
            try
            {
                var nm = (NotificationManager?)GetSystemService(NotificationService);
                nm?.Notify(NotificationId, BuildNotification());
            }
            catch (Exception e)
            {
                Logger.Debug("player notification failed " + e.Message);
            }
            RaiseChanged();
        }

        private void CreateChannel()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                return;
            }
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            var channel = new NotificationChannel(ChannelId, GetString(Resource.String.seekerog_player_channel), NotificationImportance.Low);
            channel.SetShowBadge(false);
            nm?.CreateNotificationChannel(channel);
        }

        private PendingIntent ServiceIntent(string action, int code)
        {
            var i = new Intent(this, typeof(PlaybackService));
            i.SetAction(action);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                return PendingIntent.GetForegroundService(this, code, i, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
            }
            return PendingIntent.GetService(this, code, i, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        }

        private Notification BuildNotification()
        {
            var track = Current;
            Notification.Builder b;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                b = new Notification.Builder(this, ChannelId);
            }
            else
            {
#pragma warning disable CA1422
                b = new Notification.Builder(this);
#pragma warning restore CA1422
            }
            var open = PendingIntent.GetActivity(this, 2, new Intent(this, typeof(PlayerActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            b.SetSmallIcon(Resource.Drawable.og_music_note)
             .SetContentTitle(track?.Title ?? GetString(Resource.String.app_name))
             .SetContentText(track == null ? string.Empty : (string.IsNullOrEmpty(track.Artist) ? track.Album : track.Artist + " · " + track.Album))
             .SetContentIntent(open)
             .SetDeleteIntent(ServiceIntent(ActionStop, 13))
             .SetOngoing(IsPlaying)
             .SetShowWhen(false)
             .SetVisibility(NotificationVisibility.Public);
            if (currentArt != null)
            {
                b.SetLargeIcon(currentArt);
            }
            b.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, Resource.Drawable.og_prev), GetString(Resource.String.seekerog_player_prev), ServiceIntent(ActionPrev, 10)).Build());
            b.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, IsPlaying ? Resource.Drawable.og_pause : Resource.Drawable.og_play),
                GetString(IsPlaying ? Resource.String.seekerog_player_pause : Resource.String.seekerog_player_play), ServiceIntent(ActionToggle, 11)).Build());
            b.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, Resource.Drawable.og_next), GetString(Resource.String.seekerog_player_next), ServiceIntent(ActionNext, 12)).Build());
            var style = new Notification.MediaStyle();
            if (session != null)
            {
                style.SetMediaSession(session.SessionToken);
            }
            style.SetShowActionsInCompactView(0, 1, 2);
            b.SetStyle(style);
            return b.Build()!;
        }

        public override void OnDestroy()
        {
            try
            {
                if (noisyReceiver != null)
                {
                    UnregisterReceiver(noisyReceiver);
                }
            }
            catch (Exception)
            {
            }
            ReleasePlayer();
            AbandonFocus();
            session?.Release();
            session = null;
            Instance = null;
            RaiseChanged();
            base.OnDestroy();
        }

        private class SessionCallback : MediaSession.Callback
        {
            private readonly PlaybackService svc;
            public SessionCallback(PlaybackService svc) { this.svc = svc; }
            public override void OnPlay() => svc.Play();
            public override void OnPause() => svc.Pause();
            public override void OnSkipToNext() => svc.Next(true);
            public override void OnSkipToPrevious() => svc.Previous();
            public override void OnStop() => svc.StopAll();
            public override void OnSeekTo(long pos) => svc.SeekTo((int)pos);
        }

        private class NoisyReceiver : BroadcastReceiver
        {
            private readonly PlaybackService svc;
            public NoisyReceiver(PlaybackService svc) { this.svc = svc; }
            public override void OnReceive(Context? context, Intent? intent)
            {
                if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
                {
                    svc.Pause(); // headphones unplugged / Bluetooth disconnected
                }
            }
        }
    }
}
