using Seeker.Browse;
using Android.Content;
using Android.Views;
using Android.Widget;
using Common;
using Google.Android.Material.Chip;
using Google.Android.Material.ProgressIndicator;
using Seeker.Helpers;
using Soulseek;
using System;
using System.Linq;

namespace Seeker
{
    /// <summary>
    /// SeekerOG Glass home: prominent search, quick actions, live transfer summary, recent and
    /// favourite searches, favourite users and saved filter profiles. Pure UI over existing services
    /// (no network logic here).
    /// </summary>
    public class HomeDashboard
    {
        private readonly View root;
        private readonly EditText search;
        private readonly ChipGroup recent, favorites, users, profiles;
        private readonly View recentLabel, favLabel, usersLabel, profilesLabel;
        private readonly TextView active, queued, done, failed;
        private readonly LinearProgressIndicator progress;

        public HomeDashboard(View container)
        {
            root = container.FindViewById<View>(Resource.Id.og_home_dashboard);
            var ctx = root.Context;
            search = root.FindViewById<EditText>(Resource.Id.og_home_search);
            search.EditorAction += (s, e) =>
            {
                e.Handled = false;
                if (e.ActionId == Android.Views.InputMethods.ImeAction.Search || e.ActionId == Android.Views.InputMethods.ImeAction.Done ||
                    (e.Event != null && e.Event.KeyCode == Keycode.Enter && e.Event.Action == KeyEventActions.Down))
                {
                    string term = search.Text?.Trim() ?? string.Empty;
                    if (term.Length > 0)
                    {
                        UiHelpers.HideSoftKeyboard(search);
                        search.Text = string.Empty;
                        SeekerState.MainActivityRef?.SearchFromHome(term);
                    }
                    e.Handled = true;
                }
            };

            SetupAction(Resource.Id.og_qa_browse, Resource.Drawable.og_explore, Resource.String.browse_tab, () => SeekerState.MainActivityRef?.NavigateToPage(3));
            SetupAction(Resource.Id.og_qa_library, Resource.Drawable.ic_og_library, Resource.String.seekerog_nav_library, () => Start(typeof(Library.LibraryActivity)));
            SetupAction(Resource.Id.og_qa_messages, Resource.Drawable.og_chat, Resource.String.messages, () => Start(typeof(MessagesActivity)));
            SetupAction(Resource.Id.og_qa_rooms, Resource.Drawable.og_rooms, Resource.String.chatrooms, () => Start(typeof(ChatroomActivity)));
            SetupAction(Resource.Id.og_qa_users, Resource.Drawable.og_person, Resource.String.target_user_list, () => Start(typeof(UserListActivity)));
            SetupAction(Resource.Id.og_qa_filters, Resource.Drawable.og_tune, Resource.String.seekerog_filter_profiles, () =>
            {
                var act = SeekerState.MainActivityRef;
                if (act != null) AdvancedFilterDialog.ShowProfiles(act, f => act.ApplyFilterFromHome(f));
            });

            root.FindViewById<View>(Resource.Id.og_home_transfers).Click += (s, e) => SeekerState.MainActivityRef?.NavigateToPage(2);
            active = Stat(Resource.Id.og_stat_active, Resource.String.seekerog_stat_active);
            queued = Stat(Resource.Id.og_stat_queued, Resource.String.seekerog_stat_queued);
            done = Stat(Resource.Id.og_stat_done, Resource.String.seekerog_stat_done);
            failed = Stat(Resource.Id.og_stat_failed, Resource.String.seekerog_stat_failed);
            progress = root.FindViewById<LinearProgressIndicator>(Resource.Id.og_home_progress);

            recent = root.FindViewById<ChipGroup>(Resource.Id.og_home_recent);
            favorites = root.FindViewById<ChipGroup>(Resource.Id.og_home_favorites);
            users = root.FindViewById<ChipGroup>(Resource.Id.og_home_users);
            profiles = root.FindViewById<ChipGroup>(Resource.Id.og_home_profiles);
            recentLabel = root.FindViewById<View>(Resource.Id.og_home_recent_label);
            favLabel = root.FindViewById<View>(Resource.Id.og_home_fav_label);
            usersLabel = root.FindViewById<View>(Resource.Id.og_home_users_label);
            profilesLabel = root.FindViewById<View>(Resource.Id.og_home_profiles_label);
        }

        private static void Start(Type activity)
        {
            var main = SeekerState.MainActivityRef;
            main?.StartActivity(new Intent(main, activity));
        }

        private void SetupAction(int id, int icon, int label, Action onClick)
        {
            var v = root.FindViewById<View>(id);
            v.FindViewById<ImageView>(Resource.Id.og_qa_icon).SetImageResource(icon);
            v.FindViewById<TextView>(Resource.Id.og_qa_label).SetText(label);
            v.Click += (s, e) => onClick();
        }

        private TextView Stat(int id, int label)
        {
            var v = root.FindViewById<View>(id);
            v.FindViewById<TextView>(Resource.Id.og_stat_label).SetText(label);
            return v.FindViewById<TextView>(Resource.Id.og_stat_value);
        }

        private Chip MakeChip(string text, Action onClick, Action onLongClick = null)
        {
            var chip = new Chip(root.Context) { Text = text };
            chip.SetEnsureMinTouchTargetSize(true);
            chip.Click += (s, e) => onClick();
            if (onLongClick != null)
            {
                chip.LongClick += (s, e) => { onLongClick(); e.Handled = true; };
            }
            return chip;
        }

        public void Refresh()
        {
            try
            {
                RefreshTransfers();
                RefreshChips();
            }
            catch (Exception e)
            {
                Logger.Debug("HomeDashboard refresh failed: " + e.Message);
            }
        }

        private void RefreshTransfers()
        {
            int a = 0, q = 0, d = 0, f = 0;
            long bytesTotal = 0, bytesDone = 0;
            var mgr = TransferItems.TransferItemManagerDL;
            if (mgr?.AllTransferItems != null)
            {
                lock (mgr.AllTransferItems)
                {
                    foreach (var t in mgr.AllTransferItems)
                    {
                        var st = t.State;
                        if (st.HasFlag(TransferStates.Succeeded)) d++;
                        else if (st.HasFlag(TransferStates.Errored) || st.HasFlag(TransferStates.Rejected) || st.HasFlag(TransferStates.TimedOut) || t.Failed) f++;
                        else if (st.HasFlag(TransferStates.InProgress) || st.HasFlag(TransferStates.Initializing))
                        {
                            a++;
                            bytesTotal += t.Size;
                            bytesDone += t.Size * Math.Max(0, Math.Min(100, t.Progress)) / 100;
                        }
                        else if (st.HasFlag(TransferStates.Queued) || st.HasFlag(TransferStates.Requested)) q++;
                    }
                }
            }
            active.Text = a.ToString();
            queued.Text = q.ToString();
            done.Text = d.ToString();
            failed.Text = f.ToString();
            progress.Visibility = a > 0 ? ViewStates.Visible : ViewStates.Gone;
            progress.Progress = bytesTotal > 0 ? (int)(bytesDone * 100 / bytesTotal) : 0;
        }

        private void RefreshChips()
        {
            recent.RemoveAllViews();
            foreach (var term in (PreferencesState.SearchHistory ?? new System.Collections.Generic.List<string>()).Take(8))
            {
                string t = term;
                recent.AddView(MakeChip(t, () => SeekerState.MainActivityRef?.SearchFromHome(t)));
            }
            Show(recent, recentLabel);

            favorites.RemoveAllViews();
            foreach (var term in SearchTools.FavoriteSearches.Take(12))
            {
                string t = term;
                favorites.AddView(MakeChip("★ " + t, () => SeekerState.MainActivityRef?.SearchFromHome(t), () =>
                {
                    SearchTools.ToggleFavorite(t);
                    SeekerOGStore.SaveSearchTools();
                    RefreshChips();
                }));
            }
            Show(favorites, favLabel);

            users.RemoveAllViews();
            var list = CommonState.UserList;
            if (list != null)
            {
                System.Collections.Generic.List<UserListItem> friends;
                lock (list)
                {
                    friends = list.Where(u => u.Role == Seeker.UserRole.Friend).Take(12).ToList();
                }
                foreach (var u in friends)
                {
                    string name = u.Username;
                    bool online = u.UserStatus != null && u.UserStatus.Presence != UserPresence.Offline;
                    users.AddView(MakeChip((online ? "● " : "○ ") + name, () =>
                    {
                        BrowseService.RequestFilesApi(name, null);
                        SeekerState.MainActivityRef?.NavigateToPage(3);
                    }));
                }
            }
            Show(users, usersLabel);

            profiles.RemoveAllViews();
            foreach (var p in SearchTools.FilterProfiles.Take(10))
            {
                string filter = p.Filter;
                profiles.AddView(MakeChip(p.Name, () => SeekerState.MainActivityRef?.ApplyFilterFromHome(filter)));
            }
            Show(profiles, profilesLabel);
        }

        private static void Show(ViewGroup group, View label)
        {
            var vis = group.ChildCount > 0 ? ViewStates.Visible : ViewStates.Gone;
            group.Visibility = vis;
            label.Visibility = vis;
        }
    }
}
