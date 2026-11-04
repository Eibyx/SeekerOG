using Common;
using Seeker.Helpers;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Dialog;
using Google.Android.Material.Tabs;
using Seeker.Player;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Seeker.Library
{
    /// <summary>
    /// SeekerOG music library: songs, albums, artists, folders, playlists and favourites of the audio
    /// already on the device (MediaStore). Files are played in place, never copied.
    /// </summary>
    [Activity(Label = "SeekerOG", Exported = false)]
    public class LibraryActivity : ThemeableActivity
    {
        private enum Mode { Songs = 0, Albums = 1, Artists = 2, Folders = 3, Playlists = 4, Favorites = 5, Downloads = 6 }
        private const int PermissionRequest = 7781;

        private Mode mode = Mode.Songs;
        private string? drillTitle; // when inside an album / artist / folder / playlist
        private List<LibraryTrack>? drillTracks;
        private string query = string.Empty;
        private LibraryAdapter adapter = null!;
        private TextView empty = null!;
        private View crumb = null!;
        private TextView crumbText = null!;
        private View miniPlayer = null!;
        private TextView miniTitle = null!;
        private ImageButton miniToggle = null!;
        private Handler handler = null!;
        private Action? pendingFilter;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.seekerog_library);
            handler = new Handler(Looper.MainLooper!);
            LibraryStore.EnsureLoaded();
            var toolbar = FindViewById<Google.Android.Material.AppBar.MaterialToolbar>(Resource.Id.og_library_toolbar)!;
            toolbar.NavigationClick += (s, e) => OnBackPressedCompat();
            toolbar.InflateMenu(Resource.Menu.seekerog_library_menu);
            toolbar.MenuItemClick += (s, e) =>
            {
                if (e.Item.ItemId == Resource.Id.og_library_rescan)
                {
                    Scan(true);
                }
            };

            empty = FindViewById<TextView>(Resource.Id.og_library_empty)!;
            crumb = FindViewById<View>(Resource.Id.og_library_crumb)!;
            crumbText = FindViewById<TextView>(Resource.Id.og_library_crumb_text)!;
            FindViewById<Button>(Resource.Id.og_library_play_all)!.Click += (s, e) =>
            {
                var tracks = CurrentTrackList();
                if (tracks.Count > 0)
                {
                    PlaybackService.PlayQueue(this, tracks, 0);
                    StartActivity(new Intent(this, typeof(PlayerActivity)));
                }
            };
            miniPlayer = FindViewById<View>(Resource.Id.og_mini_player)!;
            miniTitle = FindViewById<TextView>(Resource.Id.og_mini_title)!;
            miniToggle = FindViewById<ImageButton>(Resource.Id.og_mini_toggle)!;
            miniPlayer.Click += (s, e) => StartActivity(new Intent(this, typeof(PlayerActivity)));
            miniToggle.Click += (s, e) => PlaybackService.Instance?.Toggle();

            var list = FindViewById<RecyclerView>(Resource.Id.og_library_list)!;
            list.SetLayoutManager(new LinearLayoutManager(this));
            list.HasFixedSize = true;
            adapter = new LibraryAdapter(this);
            list.SetAdapter(adapter);

            var tabs = FindViewById<TabLayout>(Resource.Id.og_library_tabs)!;
            int[] titles = { Resource.String.seekerog_lib_songs, Resource.String.seekerog_lib_albums, Resource.String.seekerog_lib_artists, Resource.String.seekerog_lib_folders, Resource.String.seekerog_lib_playlists, Resource.String.seekerog_lib_favorites, Resource.String.seekerog_lib_downloads };
            foreach (var t in titles)
            {
                tabs.AddTab(tabs.NewTab().SetText(t));
            }
            tabs.TabSelected += (s, e) =>
            {
                mode = (Mode)e.Tab!.Position;
                drillTitle = null;
                drillTracks = null;
                Refresh();
            };

            var search = FindViewById<EditText>(Resource.Id.og_library_search)!;
            search.TextChanged += (s, e) =>
            {
                query = e.Text?.ToString() ?? string.Empty;
                // debounce: avoid re-filtering thousands of rows on every keystroke
                if (pendingFilter != null) handler.RemoveCallbacks(pendingFilter);
                pendingFilter = Refresh;
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
            PlaybackService.StateChanged += OnPlayerChanged;
            UpdateMiniPlayer();
        }

        protected override void OnPause()
        {
            PlaybackService.StateChanged -= OnPlayerChanged;
            base.OnPause();
        }

        private void OnPlayerChanged(object? s, EventArgs e) => RunOnUiThread(UpdateMiniPlayer);

        private void UpdateMiniPlayer()
        {
            var t = PlaybackService.Current;
            miniPlayer.Visibility = t == null ? ViewStates.Gone : ViewStates.Visible;
            if (t != null)
            {
                miniTitle.Text = t.Title + (string.IsNullOrEmpty(t.Artist) ? string.Empty : " — " + t.Artist);
                miniToggle.SetImageResource(PlaybackService.IsPlaying ? Resource.Drawable.og_pause : Resource.Drawable.og_play);
            }
        }

        private void OnBackPressedCompat()
        {
            if (drillTitle != null)
            {
                drillTitle = null;
                drillTracks = null;
                Refresh();
                return;
            }
            Finish();
        }

        public override void OnBackPressed()
        {
            if (drillTitle != null)
            {
                OnBackPressedCompat();
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
            LibraryRepository.ScanAsync(this, force).ContinueWith(t => RunOnUiThread(Refresh));
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

        private List<LibraryTrack> CurrentTrackList()
        {
            if (drillTracks != null) return drillTracks.Where(Matches).ToList();
            var all = LibraryRepository.Tracks;
            switch (mode)
            {
                case Mode.Favorites:
                    return all.Where(t => LibraryStore.Favorites.Contains(t.Id) && Matches(t)).ToList();
                case Mode.Downloads:
                    string dl = DownloadFolderName();
                    return all.Where(t => LibraryRepository.IsInDownloadFolder(t, dl) && Matches(t)).OrderByDescending(t => t.DateAddedSec).ToList();
                default:
                    return all.Where(Matches).ToList();
            }
        }

        private void Refresh()
        {
            if (!LibraryRepository.HasScanned)
            {
                return;
            }
            var rows = new List<LibraryAdapter.Row>();
            crumb.Visibility = ViewStates.Gone;
            if (drillTracks != null)
            {
                crumb.Visibility = ViewStates.Visible;
                crumbText.Text = drillTitle;
                AddTrackRows(rows, CurrentTrackList());
            }
            else
            {
                var all = LibraryRepository.Tracks;
                switch (mode)
                {
                    case Mode.Songs:
                    case Mode.Favorites:
                    case Mode.Downloads:
                        if (mode != Mode.Songs) crumb.Visibility = ViewStates.Visible;
                        crumbText.Text = mode == Mode.Favorites ? GetString(Resource.String.seekerog_lib_favorites) : mode == Mode.Downloads ? GetString(Resource.String.seekerog_lib_downloads) : string.Empty;
                        AddTrackRows(rows, CurrentTrackList());
                        break;
                    case Mode.Albums:
                        foreach (var g in all.Where(Matches).GroupBy(t => t.AlbumId).OrderBy(g => g.First().Album, StringComparer.OrdinalIgnoreCase))
                        {
                            var first = g.First();
                            var tracks = g.ToList();
                            rows.Add(new LibraryAdapter.Row(Resource.Drawable.og_album, string.IsNullOrEmpty(first.Album) ? GetString(Resource.String.seekerog_unknown) : first.Album,
                                first.Artist, tracks.Count.ToString(), () => Drill(first.Album, tracks)));
                        }
                        break;
                    case Mode.Artists:
                        foreach (var g in all.Where(Matches).GroupBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            var tracks = g.OrderBy(t => t.Album).ToList();
                            string name = string.IsNullOrEmpty(g.Key) ? GetString(Resource.String.seekerog_unknown) : g.Key;
                            int albums = tracks.Select(t => t.AlbumId).Distinct().Count();
                            rows.Add(new LibraryAdapter.Row(Resource.Drawable.og_person, name,
                                GetString(Resource.String.seekerog_lib_albums_count, albums), tracks.Count.ToString(), () => Drill(name, tracks)));
                        }
                        break;
                    case Mode.Folders:
                        foreach (var g in all.Where(Matches).GroupBy(t => t.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            var tracks = g.OrderBy(t => t.DisplayName).ToList();
                            rows.Add(new LibraryAdapter.Row(Resource.Drawable.og_folder, LibraryRepository.FolderLeaf(g.Key), g.Key, tracks.Count.ToString(), () => Drill(LibraryRepository.FolderLeaf(g.Key), tracks)));
                        }
                        break;
                    case Mode.Playlists:
                        var byId = all.ToDictionary(t => t.Id);
                        rows.Add(new LibraryAdapter.Row(Resource.Drawable.og_playlist, GetString(Resource.String.seekerog_new_playlist), string.Empty, "+", () => PromptNewPlaylist()));
                        foreach (var p in LibraryStore.Playlists.OrderBy(p => p.Key))
                        {
                            var tracks = p.Value.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                            string name = p.Key;
                            rows.Add(new LibraryAdapter.Row(Resource.Drawable.og_queue, name, string.Empty, tracks.Count.ToString(), () => Drill(name, tracks),
                                () => ConfirmDeletePlaylist(name)));
                        }
                        break;
                }
            }
            adapter.SetRows(rows);
            empty.Visibility = rows.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            if (rows.Count == 0)
            {
                empty.Text = GetString(LibraryRepository.Tracks.Count == 0 ? Resource.String.seekerog_library_empty : Resource.String.seekerog_library_no_match);
            }
        }

        private void AddTrackRows(List<LibraryAdapter.Row> rows, List<LibraryTrack> tracks)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                int index = i;
                var t = tracks[i];
                string sub = string.Join(" · ", new[] { t.Artist, t.Album }.Where(x => !string.IsNullOrEmpty(x)));
                string trailing = t.Extension + "  " + LibraryRepository.FormatDuration(t.DurationMs);
                rows.Add(new LibraryAdapter.Row(LibraryStore.Favorites.Contains(t.Id) ? Resource.Drawable.og_favorite : Resource.Drawable.og_music_note, t.Title, sub, trailing,
                    () =>
                    {
                        PlaybackService.PlayQueue(this, tracks, index);
                        StartActivity(new Intent(this, typeof(PlayerActivity)));
                    },
                    () => ShowTrackMenu(t)));
            }
        }

        private void Drill(string title, List<LibraryTrack> tracks)
        {
            drillTitle = title;
            drillTracks = tracks;
            Refresh();
        }

        private void ShowTrackMenu(LibraryTrack t)
        {
            string[] items =
            {
                GetString(Resource.String.seekerog_add_to_queue),
                GetString(Resource.String.seekerog_add_to_playlist),
                GetString(LibraryStore.Favorites.Contains(t.Id) ? Resource.String.seekerog_unfavorite : Resource.String.seekerog_favorite),
                GetString(Resource.String.seekerog_track_info),
            };
            new MaterialAlertDialogBuilder(this)
                .SetTitle(t.Title)
                .SetItems(items, (s, e) =>
                {
                    switch (e.Which)
                    {
                        case 0:
                            if (PlaybackService.Current == null) PlaybackService.PlayQueue(this, new List<LibraryTrack> { t }, 0);
                            else PlaybackService.AddToQueue(t);
                            break;
                        case 1: PromptAddToPlaylist(this, t); break;
                        case 2: LibraryStore.ToggleFavorite(t.Id); Refresh(); break;
                        case 3: ShowInfo(t); break;
                    }
                })
                .Show();
        }

        private void ShowInfo(LibraryTrack t)
        {
            var ctx = ApplicationContext!;
            System.Threading.Tasks.Task.Run(() =>
            {
                string q = ArtworkLoader.DescribeQuality(ctx, t);
                RunOnUiThread(() => new MaterialAlertDialogBuilder(this)
                    .SetTitle(t.Title)
                    .SetMessage($"{t.Artist}\n{t.Album}\n{t.Folder}/{t.DisplayName}\n{SimpleHelpers.GetHumanReadableSize(t.Size)} · {LibraryRepository.FormatDuration(t.DurationMs)}\n\n{q}")
                    .SetPositiveButton(Android.Resource.String.Ok, (s, e) => { })
                    .Show());
            });
        }

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
                Refresh();
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
                    Refresh();
                })
                .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => { })
                .Show();
        }

        private static void PromptName(Activity activity, Action<string> onName)
        {
            var input = new EditText(activity) { Hint = activity.GetString(Resource.String.seekerog_playlist_name) };
            input.SetSingleLine(true);
            var container = new FrameLayout(activity);
            int pad = (int)(20 * activity.Resources!.DisplayMetrics!.Density);
            container.SetPadding(pad, pad / 2, pad, 0);
            container.AddView(input);
            new MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.seekerog_new_playlist)
                .SetView(container)
                .SetPositiveButton(Android.Resource.String.Ok, (s, e) =>
                {
                    string name = (input.Text ?? string.Empty).Trim();
                    if (name.Length > 0) onName(name);
                })
                .SetNegativeButton(Android.Resource.String.Cancel, (s, e) => { })
                .Show();
        }
    }

    /// <summary>Virtualised list (RecyclerView) of simple rows; only visible rows are bound.</summary>
    public class LibraryAdapter : RecyclerView.Adapter
    {
        public sealed class Row
        {
            public int Icon;
            public string Title, Subtitle, Trailing;
            public Action OnClick;
            public Action? OnLongClick;
            public Row(int icon, string title, string subtitle, string trailing, Action onClick, Action? onLongClick = null)
            {
                Icon = icon; Title = title; Subtitle = subtitle; Trailing = trailing; OnClick = onClick; OnLongClick = onLongClick;
            }
        }

        private class Holder : RecyclerView.ViewHolder
        {
            public ImageView Icon;
            public TextView Title, Subtitle, Trailing;
            public Row? Row;
            public Holder(View v) : base(v)
            {
                Icon = v.FindViewById<ImageView>(Resource.Id.og_row_icon)!;
                Title = v.FindViewById<TextView>(Resource.Id.og_row_title)!;
                Subtitle = v.FindViewById<TextView>(Resource.Id.og_row_subtitle)!;
                Trailing = v.FindViewById<TextView>(Resource.Id.og_row_trailing)!;
                v.Click += (s, e) => Row?.OnClick();
                v.LongClick += (s, e) =>
                {
                    if (Row?.OnLongClick != null)
                    {
                        Row.OnLongClick();
                        e.Handled = true;
                    }
                    else
                    {
                        e.Handled = false;
                    }
                };
            }
        }

        private readonly Context context;
        private List<Row> rows = new List<Row>();

        public LibraryAdapter(Context context)
        {
            this.context = context;
        }

        public void SetRows(List<Row> newRows)
        {
            rows = newRows;
            NotifyDataSetChanged();
        }

        public override int ItemCount => rows.Count;

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var h = (Holder)holder;
            var r = rows[position];
            h.Row = r;
            h.Icon.SetImageResource(r.Icon);
            h.Title.Text = r.Title;
            h.Subtitle.Text = r.Subtitle;
            h.Subtitle.Visibility = string.IsNullOrEmpty(r.Subtitle) ? ViewStates.Gone : ViewStates.Visible;
            h.Trailing.Text = r.Trailing;
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var v = LayoutInflater.From(context)!.Inflate(Resource.Layout.seekerog_library_row, parent, false)!;
            return new Holder(v);
        }
    }
}
