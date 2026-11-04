using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Common;
using Google.Android.Material.Chip;
using Google.Android.Material.Dialog;
using Seeker.Helpers;
using Seeker.Player;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Seeker.Library
{
    /// <summary>
    /// SeekerOG music library: songs, albums (grid), artists, folders, playlists, favourites and
    /// downloads of the audio already on the device (MediaStore). Files are played in place.
    /// Visual language matches the Now Playing screen: artwork thumbnails, glass cards, chips,
    /// detail header with Play / Shuffle, current track highlighted, shared mini player.
    /// </summary>
    [Activity(Label = "SeekerOG", Exported = false)]
    public class LibraryActivity : ThemeableActivity
    {
        private enum Mode { Songs = 0, Albums = 1, Artists = 2, Folders = 3, Playlists = 4, Favorites = 5, Downloads = 6 }
        private const int PermissionRequest = 7781;

        private Mode mode = Mode.Songs;
        private string? drillTitle;
        private string? drillKind;
        private List<LibraryTrack>? drillTracks;
        private string query = string.Empty;
        private LibraryAdapter adapter = null!;
        private RecyclerView list = null!;
        private GridLayoutManager layoutManager = null!;
        private TextView empty = null!, countText = null!, titleText = null!;
        private Handler handler = null!;
        private Action? pendingFilter;
        private MiniPlayerController miniPlayer = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.seekerog_library);
            handler = new Handler(Looper.MainLooper!);
            LibraryStore.EnsureLoaded();

            FindViewById<ImageButton>(Resource.Id.og_library_back)!.Click += (s, e) => GoBack();
            FindViewById<ImageButton>(Resource.Id.og_library_rescan)!.Click += (s, e) => Scan(true);
            empty = FindViewById<TextView>(Resource.Id.og_library_empty)!;
            countText = FindViewById<TextView>(Resource.Id.og_library_count)!;
            titleText = FindViewById<TextView>(Resource.Id.og_library_title)!;
            miniPlayer = new MiniPlayerController(this, Window!.DecorView);

            list = FindViewById<RecyclerView>(Resource.Id.og_library_list)!;
            adapter = new LibraryAdapter(this);
            layoutManager = new GridLayoutManager(this, 2);
            layoutManager.SetSpanSizeLookup(new SpanLookup(adapter));
            list.SetLayoutManager(layoutManager);
            list.SetItemViewCacheSize(12);
            list.SetAdapter(adapter);

            var tabs = FindViewById<ChipGroup>(Resource.Id.og_library_tabs)!;
            int[] titles = { Resource.String.seekerog_lib_songs, Resource.String.seekerog_lib_albums, Resource.String.seekerog_lib_artists, Resource.String.seekerog_lib_folders, Resource.String.seekerog_lib_playlists, Resource.String.seekerog_lib_favorites, Resource.String.seekerog_lib_downloads };
            int[] icons = { Resource.Drawable.og_music_note, Resource.Drawable.og_album, Resource.Drawable.og_person, Resource.Drawable.og_folder, Resource.Drawable.og_queue, Resource.Drawable.og_favorite, Resource.Drawable.og_download };
            for (int i = 0; i < titles.Length; i++)
            {
                var chip = (Chip)LayoutInflater.Inflate(Resource.Layout.seekerog_chip_choice, tabs, false)!;
                chip.SetText(titles[i]);
                chip.SetChipIconResource(icons[i]);
                chip.ChipIconVisible = true;
                chip.CheckedIconVisible = false;
                chip.Id = View.GenerateViewId();
                chip.Tag = i;
                int index = i;
                chip.CheckedChange += (s, e) =>
                {
                    if (!e.IsChecked) return;
                    mode = (Mode)index;
                    drillTitle = null;
                    drillTracks = null;
                    Refresh(scrollTop: true);
                };
                tabs.AddView(chip);
                if (i == 0) chip.Checked = true;
            }

            var search = FindViewById<EditText>(Resource.Id.og_library_search)!;
            search.TextChanged += (s, e) =>
            {
                query = e.Text?.ToString() ?? string.Empty;
                if (pendingFilter != null) handler.RemoveCallbacks(pendingFilter);
                pendingFilter = () => Refresh(scrollTop: false);
                handler.PostDelayed(pendingFilter, 180);
            };

            if (HasAudioPermission())
            {
                Scan(false);
            }
            else
            {
                RequestAudioPermission();
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            miniPlayer.Attach();
            PlaybackService.StateChanged += OnPlayerChanged;
            adapter.NotifyDataSetChanged(); // refresh "now playing" highlight
        }

        protected override void OnPause()
        {
            miniPlayer.Detach();
            PlaybackService.StateChanged -= OnPlayerChanged;
            base.OnPause();
        }

        private LibraryTrack? lastCurrent;
        private void OnPlayerChanged(object? s, EventArgs e) => RunOnUiThread(() =>
        {
            if (lastCurrent != PlaybackService.Current)
            {
                lastCurrent = PlaybackService.Current;
                adapter.NotifyDataSetChanged();
            }
        });

        private void GoBack()
        {
            if (drillTracks != null)
            {
                drillTitle = null;
                drillTracks = null;
                Refresh(scrollTop: true);
                return;
            }
            Finish();
        }

        public override void OnBackPressed()
        {
            if (drillTracks != null)
            {
                GoBack();
                return;
            }
#pragma warning disable CA1422
            base.OnBackPressed();
#pragma warning restore CA1422
        }

        // ---------------------------------------------------------------- permissions

        private string AudioPermission => OperatingSystem.IsAndroidVersionAtLeast(33) ? Manifest.Permission.ReadMediaAudio : Manifest.Permission.ReadExternalStorage;

        private bool HasAudioPermission() => ContextCompat.CheckSelfPermission(this, AudioPermission) == Permission.Granted;

        private void RequestAudioPermission()
        {
            empty.Visibility = ViewStates.Visible;
            empty.Text = GetString(Resource.String.seekerog_library_permission);
            ActivityCompat.RequestPermissions(this, new[] { AudioPermission }, PermissionRequest);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == PermissionRequest)
            {
                if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                {
                    Scan(false);
                }
                else
                {
                    empty.Visibility = ViewStates.Visible;
                    empty.Text = GetString(Resource.String.seekerog_library_permission_denied);
                }
            }
        }

        private void Scan(bool force)
        {
            empty.Visibility = ViewStates.Visible;
            empty.Text = GetString(Resource.String.seekerog_library_scanning);
            LibraryRepository.ScanAsync(this, force).ContinueWith(t => RunOnUiThread(() => Refresh(scrollTop: false)));
        }

        // ---------------------------------------------------------------- data

        private static string DownloadFolderName()
        {
            string? uri = PreferencesState.SaveDataDirectoryUri;
            if (string.IsNullOrEmpty(uri)) return string.Empty;
            string decoded = Android.Net.Uri.Decode(uri) ?? uri;
            decoded = decoded.TrimEnd('/');
            int idx = Math.Max(decoded.LastIndexOf('/'), decoded.LastIndexOf(':'));
            return idx >= 0 ? decoded.Substring(idx + 1) : decoded;
        }

        private bool Matches(LibraryTrack t)
        {
            if (query.Length == 0) return true;
            return t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private string Unknown => GetString(Resource.String.seekerog_unknown);

        private void Refresh(bool scrollTop)
        {
            if (!LibraryRepository.HasScanned)
            {
                return;
            }
            var all = LibraryRepository.Tracks;
            countText.Text = GetString(Resource.String.seekerog_lib_count, all.Count);
            var items = new List<LibraryAdapter.Item>();
            if (drillTracks != null)
            {
                var tracks = drillTracks.Where(Matches).ToList();
                items.Add(LibraryAdapter.Item.Header(drillKind ?? string.Empty, drillTitle ?? string.Empty, DescribeTracks(tracks), tracks));
                AddTracks(items, tracks, drillTitle ?? string.Empty);
            }
            else
            {
                switch (mode)
                {
                    case Mode.Songs:
                        AddTracks(items, all.Where(Matches).ToList(), GetString(Resource.String.seekerog_lib_songs), withShuffleHeader: true);
                        break;
                    case Mode.Favorites:
                    {
                        var tracks = all.Where(t => LibraryStore.Favorites.Contains(t.Id) && Matches(t)).ToList();
                        if (tracks.Count > 0) items.Add(LibraryAdapter.Item.Header(GetString(Resource.String.seekerog_lib_favorites), GetString(Resource.String.seekerog_lib_favorites), DescribeTracks(tracks), tracks));
                        AddTracks(items, tracks, GetString(Resource.String.seekerog_lib_favorites));
                        break;
                    }
                    case Mode.Downloads:
                    {
                        string dl = DownloadFolderName();
                        var tracks = all.Where(t => LibraryRepository.IsInDownloadFolder(t, dl) && Matches(t)).OrderByDescending(t => t.DateAddedSec).ToList();
                        if (tracks.Count > 0) items.Add(LibraryAdapter.Item.Header(GetString(Resource.String.seekerog_lib_downloads), GetString(Resource.String.seekerog_lib_downloads), DescribeTracks(tracks), tracks));
                        AddTracks(items, tracks, GetString(Resource.String.seekerog_lib_downloads));
                        break;
                    }
                    case Mode.Albums:
                        foreach (var g in all.Where(Matches).GroupBy(t => t.AlbumId).OrderBy(g => g.First().Album, StringComparer.OrdinalIgnoreCase))
                        {
                            var first = g.First();
                            var tracks = g.OrderBy(t => t.DisplayName).ToList();
                            string name = string.IsNullOrEmpty(first.Album) ? Unknown : first.Album;
                            items.Add(LibraryAdapter.Item.Album(name, string.IsNullOrEmpty(first.Artist) ? GetString(Resource.String.seekerog_lib_tracks_count, tracks.Count) : first.Artist, first,
                                () => Drill(GetString(Resource.String.seekerog_kind_album), name, tracks)));
                        }
                        break;
                    case Mode.Artists:
                        foreach (var g in all.Where(Matches).GroupBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            var tracks = g.OrderBy(t => t.Album).ThenBy(t => t.DisplayName).ToList();
                            string name = string.IsNullOrEmpty(g.Key) ? Unknown : g.Key;
                            int albums = tracks.Select(t => t.AlbumId).Distinct().Count();
                            items.Add(LibraryAdapter.Item.Row(LibraryAdapter.RowStyle.Initial, name,
                                GetString(Resource.String.seekerog_lib_albums_count, albums) + " · " + GetString(Resource.String.seekerog_lib_tracks_count, tracks.Count),
                                null, string.Empty, null, () => Drill(GetString(Resource.String.seekerog_kind_artist), name, tracks)));
                        }
                        break;
                    case Mode.Folders:
                        foreach (var g in all.Where(Matches).GroupBy(t => t.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            var tracks = g.OrderBy(t => t.DisplayName).ToList();
                            string name = LibraryRepository.FolderLeaf(g.Key);
                            items.Add(LibraryAdapter.Item.Row(LibraryAdapter.RowStyle.Icon, name, g.Key, null, tracks.Count.ToString(), Resource.Drawable.og_folder,
                                () => Drill(GetString(Resource.String.seekerog_kind_folder), name, tracks)));
                        }
                        break;
                    case Mode.Playlists:
                        var byId = all.ToDictionary(t => t.Id);
                        items.Add(LibraryAdapter.Item.Row(LibraryAdapter.RowStyle.Icon, GetString(Resource.String.seekerog_new_playlist), GetString(Resource.String.seekerog_new_playlist_hint), null, string.Empty, Resource.Drawable.og_playlist, () => PromptNewPlaylist()));
                        foreach (var p in LibraryStore.Playlists.OrderBy(p => p.Key))
                        {
                            var tracks = p.Value.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                            string name = p.Key;
                            var item = LibraryAdapter.Item.Row(LibraryAdapter.RowStyle.Art, name, GetString(Resource.String.seekerog_lib_tracks_count, tracks.Count), tracks.FirstOrDefault(), string.Empty, Resource.Drawable.og_queue,
                                () => Drill(GetString(Resource.String.seekerog_kind_playlist), name, tracks));
                            item.OnMore = () => ConfirmDeletePlaylist(name);
                            items.Add(item);
                        }
                        break;
                }
            }
            titleText.Text = drillTracks != null ? drillTitle : GetString(Resource.String.seekerog_nav_library);
            adapter.SetItems(items);
            if (scrollTop) list.ScrollToPosition(0);
            bool emptyList = items.Count == 0 || (items.Count == 1 && items[0].Type == LibraryAdapter.TypeHeader && drillTracks == null);
            empty.Visibility = emptyList ? ViewStates.Visible : ViewStates.Gone;
            if (emptyList)
            {
                empty.Text = GetString(all.Count == 0 ? Resource.String.seekerog_library_empty : Resource.String.seekerog_library_no_match);
            }
        }

        private string DescribeTracks(List<LibraryTrack> tracks)
        {
            long ms = tracks.Sum(t => t.DurationMs);
            return GetString(Resource.String.seekerog_lib_tracks_count, tracks.Count) + " · " + LibraryRepository.FormatDuration(ms);
        }

        private void AddTracks(List<LibraryAdapter.Item> items, List<LibraryTrack> tracks, string source, bool withShuffleHeader = false)
        {
            if (withShuffleHeader && tracks.Count > 0)
            {
                items.Add(LibraryAdapter.Item.Header(GetString(Resource.String.seekerog_nav_library), GetString(Resource.String.seekerog_lib_songs), DescribeTracks(tracks), tracks, compact: true));
            }
            for (int i = 0; i < tracks.Count; i++)
            {
                int index = i;
                var t = tracks[i];
                string sub = string.Join(" · ", new[] { t.Artist, t.Album }.Where(x => !string.IsNullOrEmpty(x)));
                var item = LibraryAdapter.Item.Row(LibraryAdapter.RowStyle.Art, t.Title, sub, t, LibraryRepository.FormatDuration(t.DurationMs), null, () =>
                {
                    PlaybackService.PlayQueue(this, tracks, index, source);
                    MiniPlayerController.OpenPlayer(this);
                });
                item.Badge = t.Extension;
                item.OnMore = () => ShowTrackMenu(t);
                items.Add(item);
            }
        }

        internal void PlayAll(List<LibraryTrack> tracks, bool shuffle, string source)
        {
            if (tracks.Count == 0) return;
            PlaybackService.SetShuffle(shuffle);
            int start = shuffle ? new Random().Next(tracks.Count) : 0;
            PlaybackService.PlayQueue(this, tracks, start, source);
            MiniPlayerController.OpenPlayer(this);
        }

        private void Drill(string kind, string title, List<LibraryTrack> tracks)
        {
            drillKind = kind;
            drillTitle = title;
            drillTracks = tracks;
            Refresh(scrollTop: true);
        }

        private void ShowTrackMenu(LibraryTrack t)
        {
            string[] items =
            {
                GetString(Resource.String.seekerog_play_next),
                GetString(Resource.String.seekerog_add_to_queue),
                GetString(Resource.String.seekerog_add_to_playlist),
                GetString(LibraryStore.Favorites.Contains(t.Id) ? Resource.String.seekerog_unfavorite : Resource.String.seekerog_favorite),
                GetString(Resource.String.seekerog_audio_quality),
            };
            var dialog = new MaterialAlertDialogBuilder(this)
                .SetTitle(t.Title)
                .SetItems(items, (s, e) =>
                {
                    switch (e.Which)
                    {
                        case 0: PlaybackService.PlayNext(this, t); Toast(Resource.String.seekerog_added); break;
                        case 1:
                            if (PlaybackService.Current == null) PlaybackService.PlayQueue(this, new List<LibraryTrack> { t }, 0);
                            else PlaybackService.AddToQueue(t);
                            Toast(Resource.String.seekerog_added);
                            break;
                        case 2: PromptAddToPlaylist(this, t); break;
                        case 3: LibraryStore.ToggleFavorite(t.Id); Refresh(scrollTop: false); break;
                        case 4: AudioInfoSheet.Show(this, t); break;
                    }
                })
                .Create();
            dialog.Show();
            GlassAppearance.ApplyBlurBehind(dialog.Window!);
        }

        private void Toast(int res) => SeekerApplication.Toaster.ShowToast(GetString(res), ToastLength.Short);

        public static void PromptAddToPlaylist(Activity activity, LibraryTrack t)
        {
            LibraryStore.EnsureLoaded();
            var names = LibraryStore.Playlists.Keys.OrderBy(k => k).ToList();
            var display = new List<string> { activity.GetString(Resource.String.seekerog_new_playlist) };
            display.AddRange(names);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_add_to_playlist)
                .SetItems(display.ToArray(), (s, e) =>
                {
                    if (e.Which == 0)
                    {
                        PromptName(activity, name => LibraryStore.AddToPlaylist(name, t.Id));
                    }
                    else
                    {
                        LibraryStore.AddToPlaylist(names[e.Which - 1], t.Id);
                        SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.seekerog_added), ToastLength.Short);
                    }
                })
                .Show();
        }

        private void PromptNewPlaylist()
        {
            PromptName(this, name =>
            {
                LibraryStore.EnsureLoaded();
                if (!LibraryStore.Playlists.ContainsKey(name))
                {
                    LibraryStore.Playlists[name] = new List<long>();
                    LibraryStore.Save();
                }
                Refresh(scrollTop: false);
            });
        }

        private void ConfirmDeletePlaylist(string name)
        {
            new MaterialAlertDialogBuilder(this)
                .SetTitle(name)
                .SetMessage(Resource.String.seekerog_delete_playlist_q)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) =>
                {
                    LibraryStore.Playlists.Remove(name);
                    LibraryStore.Save();
                    Refresh(scrollTop: false);
                })
                .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => { })
                .Show();
        }

        private static void PromptName(Activity activity, Action<string> onName)
        {
            var view = LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_text_prompt, null)!;
            var input = view.FindViewById<EditText>(Resource.Id.og_prompt_input)!;
            input.Hint = activity.GetString(Resource.String.seekerog_playlist_name);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_new_playlist)
                .SetView(view)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) =>
                {
                    string name = (input.Text ?? string.Empty).Trim();
                    if (name.Length > 0) onName(name);
                })
                .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => { })
                .Show();
        }

        private class SpanLookup : GridLayoutManager.SpanSizeLookup
        {
            private readonly LibraryAdapter adapter;
            public SpanLookup(LibraryAdapter adapter) { this.adapter = adapter; }
            public override int GetSpanSize(int position) => adapter.GetItemViewType(position) == LibraryAdapter.TypeAlbum ? 1 : 2;
        }
    }

    /// <summary>Virtualised multi-type list: detail header, rows (art / icon / initial), album cards.</summary>
    public class LibraryAdapter : RecyclerView.Adapter
    {
        public const int TypeHeader = 0, TypeRow = 1, TypeAlbum = 2;
        public enum RowStyle { Art, Icon, Initial }

        public sealed class Item
        {
            public int Type;
            public RowStyle Style;
            public string Title = string.Empty, Subtitle = string.Empty, Trailing = string.Empty, Badge = string.Empty, Kind = string.Empty;
            public LibraryTrack? Track;
            public int? Icon;
            public Action? OnClick;
            public Action? OnMore;
            public List<LibraryTrack>? Tracks;
            public bool Compact;

            public static Item Row(RowStyle style, string title, string subtitle, LibraryTrack? track, string trailing, int? icon, Action onClick) =>
                new Item { Type = TypeRow, Style = style, Title = title, Subtitle = subtitle, Track = track, Trailing = trailing, Icon = icon, OnClick = onClick };
            public static Item Album(string title, string subtitle, LibraryTrack art, Action onClick) =>
                new Item { Type = TypeAlbum, Title = title, Subtitle = subtitle, Track = art, OnClick = onClick };
            public static Item Header(string kind, string title, string subtitle, List<LibraryTrack> tracks, bool compact = false) =>
                new Item { Type = TypeHeader, Kind = kind, Title = title, Subtitle = subtitle, Tracks = tracks, Track = tracks.FirstOrDefault(), Compact = compact };
        }

        private static readonly Color[] AvatarColors =
        {
            Color.ParseColor("#1B7FB5"), Color.ParseColor("#6C4CE0"), Color.ParseColor("#0E8F6E"),
            Color.ParseColor("#D9622B"), Color.ParseColor("#C2366B"), Color.ParseColor("#46525F"),
        };

        private readonly LibraryActivity activity;
        private readonly int thumbPx, cardPx;
        private List<Item> items = new List<Item>();

        public LibraryAdapter(LibraryActivity activity)
        {
            this.activity = activity;
            float d = activity.Resources!.DisplayMetrics!.Density;
            thumbPx = (int)(100 * d);
            cardPx = (int)(200 * d);
        }

        public void SetItems(List<Item> newItems)
        {
            items = newItems;
            NotifyDataSetChanged();
        }

        public override int ItemCount => items.Count;
        public override int GetItemViewType(int position) => items[position].Type;

        private class RowHolder : RecyclerView.ViewHolder
        {
            public View Surface; public ImageView Art, Icon; public TextView Initial, Title, Subtitle, Trailing, Badge; public ImageButton More; public Item? Item;
            public RowHolder(View v) : base(v)
            {
                Surface = v.FindViewById<View>(Resource.Id.og_row_surface)!;
                Art = v.FindViewById<ImageView>(Resource.Id.og_row_art)!;
                Icon = v.FindViewById<ImageView>(Resource.Id.og_row_icon)!;
                Initial = v.FindViewById<TextView>(Resource.Id.og_row_initial)!;
                Title = v.FindViewById<TextView>(Resource.Id.og_row_title)!;
                Subtitle = v.FindViewById<TextView>(Resource.Id.og_row_subtitle)!;
                Trailing = v.FindViewById<TextView>(Resource.Id.og_row_trailing)!;
                Badge = v.FindViewById<TextView>(Resource.Id.og_row_badge)!;
                More = v.FindViewById<ImageButton>(Resource.Id.og_row_more)!;
                v.Click += (s, e) => Item?.OnClick?.Invoke();
                v.LongClick += (s, e) => { if (Item?.OnMore != null) { Item.OnMore(); e.Handled = true; } else e.Handled = false; };
                More.Click += (s, e) => Item?.OnMore?.Invoke();
            }
        }

        private class AlbumHolder : RecyclerView.ViewHolder
        {
            public ImageView Art; public TextView Title, Subtitle; public View ArtFrame; public Item? Item;
            public AlbumHolder(View v) : base(v)
            {
                Art = v.FindViewById<ImageView>(Resource.Id.og_album_art)!;
                ArtFrame = v.FindViewById<View>(Resource.Id.og_album_art_frame)!;
                Title = v.FindViewById<TextView>(Resource.Id.og_album_title)!;
                Subtitle = v.FindViewById<TextView>(Resource.Id.og_album_subtitle)!;
                v.Click += (s, e) => Item?.OnClick?.Invoke();
            }
        }

        private class HeaderHolder : RecyclerView.ViewHolder
        {
            public ImageView Art; public TextView Kind, Title, Subtitle; public View Play, Shuffle; public Item? Item;
            public HeaderHolder(View v, LibraryActivity a) : base(v)
            {
                Art = v.FindViewById<ImageView>(Resource.Id.og_hdr_art)!;
                Kind = v.FindViewById<TextView>(Resource.Id.og_hdr_kind)!;
                Title = v.FindViewById<TextView>(Resource.Id.og_hdr_title)!;
                Subtitle = v.FindViewById<TextView>(Resource.Id.og_hdr_subtitle)!;
                Play = v.FindViewById<View>(Resource.Id.og_hdr_play)!;
                Shuffle = v.FindViewById<View>(Resource.Id.og_hdr_shuffle)!;
                Play.Click += (s, e) => { if (Item?.Tracks != null) a.PlayAll(Item.Tracks, false, Item.Title); };
                Shuffle.Click += (s, e) => { if (Item?.Tracks != null) a.PlayAll(Item.Tracks, true, Item.Title); };
            }
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var inflater = LayoutInflater.From(activity)!;
            switch (viewType)
            {
                case TypeHeader: return new HeaderHolder(inflater.Inflate(Resource.Layout.seekerog_library_header, parent, false)!, activity);
                case TypeAlbum: return new AlbumHolder(inflater.Inflate(Resource.Layout.seekerog_album_card, parent, false)!);
                default: return new RowHolder(inflater.Inflate(Resource.Layout.seekerog_library_row, parent, false)!);
            }
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var item = items[position];
            switch (holder)
            {
                case HeaderHolder h:
                    h.Item = item;
                    h.Kind.Text = item.Kind;
                    h.Title.Text = item.Title;
                    h.Subtitle.Text = item.Subtitle;
                    h.Art.Visibility = item.Compact ? ViewStates.Gone : ViewStates.Visible;
                    h.Kind.Visibility = item.Compact ? ViewStates.Gone : ViewStates.Visible;
                    if (!item.Compact) ThumbnailLoader.Bind(h.Art, item.Track, cardPx, Resource.Drawable.og_album);
                    break;
                case AlbumHolder a:
                    a.Item = item;
                    a.Title.Text = item.Title;
                    a.Subtitle.Text = item.Subtitle;
                    int w = (activity.Resources!.DisplayMetrics!.WidthPixels - (int)(16 * activity.Resources.DisplayMetrics.Density)) / 2 - (int)(28 * activity.Resources.DisplayMetrics.Density);
                    if (w > 0 && a.ArtFrame.LayoutParameters!.Height != w)
                    {
                        a.ArtFrame.LayoutParameters.Height = w;
                        a.ArtFrame.RequestLayout();
                    }
                    ThumbnailLoader.Bind(a.Art, item.Track, cardPx, Resource.Drawable.og_album);
                    break;
                case RowHolder r:
                    r.Item = item;
                    r.Title.Text = item.Title;
                    r.Subtitle.Text = item.Subtitle;
                    r.Subtitle.Visibility = string.IsNullOrEmpty(item.Subtitle) ? ViewStates.Gone : ViewStates.Visible;
                    r.Trailing.Text = item.Trailing;
                    r.Badge.Text = item.Badge;
                    r.Badge.Visibility = string.IsNullOrEmpty(item.Badge) ? ViewStates.Gone : ViewStates.Visible;
                    r.More.Visibility = item.OnMore != null ? ViewStates.Visible : ViewStates.Gone;
                    bool playing = item.Track != null && item.Style == RowStyle.Art && PlaybackService.Current == item.Track;
                    r.Title.SetTypeface(null, playing ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                    if (playing) r.Surface.SetBackgroundResource(Resource.Drawable.og_queue_current);
                    else r.Surface.SetBackgroundResource(Resource.Drawable.og_row_ripple);
                    r.Initial.Visibility = ViewStates.Gone;
                    r.Icon.Visibility = ViewStates.Gone;
                    switch (item.Style)
                    {
                        case RowStyle.Art when item.Track != null:
                            r.Art.BackgroundTintList = null;
                            ThumbnailLoader.Bind(r.Art, item.Track, thumbPx, item.Icon ?? Resource.Drawable.og_music_note);
                            if (playing)
                            {
                                r.Icon.Visibility = ViewStates.Visible;
                                r.Icon.SetImageResource(Resource.Drawable.og_equalizer);
                                r.Icon.SetBackgroundResource(Resource.Drawable.og_now_playing_overlay);
                            }
                            break;
                        case RowStyle.Initial:
                            r.Art.Tag = null;
                            r.Art.SetImageDrawable(null);
                            r.Art.BackgroundTintList = ColorStateList.ValueOf(AvatarColors[Math.Abs(item.Title.GetHashCode()) % AvatarColors.Length]);
                            r.Initial.Visibility = ViewStates.Visible;
                            r.Initial.Text = item.Title.Length > 0 ? item.Title.Substring(0, 1).ToUpperInvariant() : "?";
                            break;
                        default:
                            r.Art.Tag = null;
                            r.Art.SetImageDrawable(null);
                            r.Art.BackgroundTintList = null;
                            r.Icon.Visibility = ViewStates.Visible;
                            r.Icon.Background = null;
                            r.Icon.SetImageResource(item.Icon ?? Resource.Drawable.og_music_note);
                            break;
                    }
                    break;
            }
        }
    }
}
