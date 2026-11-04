using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Seeker.Library;
using System;
using System.Linq;

namespace Seeker.Player
{
    /// <summary>
    /// Playback queue (Namida-inspired, re-implemented): artwork thumbnails, current track highlighted
    /// with a "now playing" overlay, drag the handle to reorder, swipe sideways to remove, tap to jump.
    /// </summary>
    public static class QueueSheet
    {
        public static void Show(Activity activity)
        {
            if (PlaybackService.Queue.Count == 0)
            {
                return;
            }
            var view = LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_queue_sheet, null)!;
            var list = view.FindViewById<RecyclerView>(Resource.Id.og_queue_list)!;
            var summary = view.FindViewById<TextView>(Resource.Id.og_queue_summary)!;
            var shuffle = view.FindViewById<Google.Android.Material.Button.MaterialButton>(Resource.Id.og_queue_shuffle)!;
            var adapter = new QueueAdapter(activity);
            var layout = new LinearLayoutManager(activity);
            list.SetLayoutManager(layout);
            list.SetAdapter(adapter);
            var touch = new ItemTouchHelper(new QueueTouchCallback(adapter));
            touch.AttachToRecyclerView(list);
            adapter.StartDrag = holder => touch.StartDrag(holder);

            void UpdateSummary()
            {
                long totalMs = PlaybackService.Queue.Sum(t => t.DurationMs);
                summary.Text = activity.GetString(Resource.String.seekerog_queue_summary, PlaybackService.Queue.Count, LibraryRepository.FormatDuration(totalMs));
                shuffle.Checkable = true;
                shuffle.Checked = PlaybackService.Shuffle;
                shuffle.Alpha = PlaybackService.Shuffle ? 1f : 0.7f;
            }
            UpdateSummary();
            shuffle.Click += (s, e) => { PlaybackService.SetShuffle(!PlaybackService.Shuffle); UpdateSummary(); };

            EventHandler onChanged = (s, e) => activity.RunOnUiThread(() =>
            {
                if (adapter.SuppressRefresh) return;
                adapter.NotifyDataSetChanged();
                UpdateSummary();
            });
            PlaybackService.StateChanged += onChanged;

            var dialog = GlassSheet.Create(activity, view, 0.85f, fixedHeight: true);
            dialog.DismissEvent += (s, e) => PlaybackService.StateChanged -= onChanged;
            dialog.Show();
            int current = Math.Max(0, PlaybackService.Index - 2);
            layout.ScrollToPositionWithOffset(current, 0);
        }

        private class Holder : RecyclerView.ViewHolder
        {
            public readonly View Surface;
            public readonly ImageView Handle, Art, Playing;
            public readonly TextView Title, Artist, Duration;
            public Holder(View v) : base(v)
            {
                Surface = v.FindViewById<View>(Resource.Id.og_q_surface)!;
                Handle = v.FindViewById<ImageView>(Resource.Id.og_q_handle)!;
                Art = v.FindViewById<ImageView>(Resource.Id.og_q_art)!;
                Playing = v.FindViewById<ImageView>(Resource.Id.og_q_playing)!;
                Title = v.FindViewById<TextView>(Resource.Id.og_q_title)!;
                Artist = v.FindViewById<TextView>(Resource.Id.og_q_artist)!;
                Duration = v.FindViewById<TextView>(Resource.Id.og_q_duration)!;
            }
        }

        private class QueueAdapter : RecyclerView.Adapter
        {
            private readonly Activity activity;
            private readonly int thumbPx;
            public Action<RecyclerView.ViewHolder>? StartDrag;
            public bool SuppressRefresh;

            public QueueAdapter(Activity activity)
            {
                this.activity = activity;
                thumbPx = (int)(96 * activity.Resources!.DisplayMetrics!.Density);
            }

            public override int ItemCount => PlaybackService.Queue.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
            {
                var v = LayoutInflater.From(activity)!.Inflate(Resource.Layout.seekerog_queue_row, parent, false)!;
                var h = new Holder(v);
                h.Surface.Click += (s, e) =>
                {
                    int pos = h.BindingAdapterPosition;
                    if (pos != RecyclerView.NoPosition) PlaybackService.JumpTo(pos);
                };
                h.Handle.Touch += (s, e) =>
                {
                    if (e.Event!.ActionMasked == MotionEventActions.Down)
                    {
                        StartDrag?.Invoke(h);
                    }
                    e.Handled = false;
                };
                return h;
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
            {
                var h = (Holder)holder;
                var t = PlaybackService.Queue[position];
                bool current = position == PlaybackService.Index;
                h.Title.Text = t.Title;
                h.Artist.Text = string.IsNullOrEmpty(t.Artist) ? t.Album : t.Artist;
                h.Duration.Text = LibraryRepository.FormatDuration(t.DurationMs);
                h.Title.SetTypeface(null, current ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                h.Playing.Visibility = current ? ViewStates.Visible : ViewStates.Gone;
                if (current) h.Surface.SetBackgroundResource(Resource.Drawable.og_queue_current);
                else h.Surface.SetBackgroundResource(Android.Resource.Color.Transparent);
                ThumbnailLoader.Bind(h.Art, t, thumbPx, Resource.Drawable.og_music_note);
            }
        }

        private class QueueTouchCallback : ItemTouchHelper.SimpleCallback
        {
            private readonly QueueAdapter adapter;

            public QueueTouchCallback(QueueAdapter adapter) : base(ItemTouchHelper.Up | ItemTouchHelper.Down, ItemTouchHelper.Left | ItemTouchHelper.Right)
            {
                this.adapter = adapter;
            }

            public override bool IsLongPressDragEnabled => true;

            public override bool OnMove(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder, RecyclerView.ViewHolder target)
            {
                int from = viewHolder.BindingAdapterPosition, to = target.BindingAdapterPosition;
                if (from < 0 || to < 0) return false;
                adapter.SuppressRefresh = true;
                PlaybackService.MoveQueueItem(from, to);
                adapter.SuppressRefresh = false;
                adapter.NotifyItemMoved(from, to);
                return true;
            }

            public override void OnSwiped(RecyclerView.ViewHolder viewHolder, int direction)
            {
                int pos = viewHolder.BindingAdapterPosition;
                if (pos < 0) return;
                adapter.SuppressRefresh = true;
                PlaybackService.RemoveFromQueue(pos);
                adapter.SuppressRefresh = false;
                adapter.NotifyItemRemoved(pos);
            }

            public override void ClearView(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder)
            {
                base.ClearView(recyclerView, viewHolder);
                viewHolder.ItemView.Alpha = 1f;
                // re-bind so the "current" highlight follows the moved items
                recyclerView.Post(() => adapter.NotifyDataSetChanged());
            }

            public override void OnSelectedChanged(RecyclerView.ViewHolder? viewHolder, int actionState)
            {
                base.OnSelectedChanged(viewHolder, actionState);
                if (viewHolder != null && actionState == ItemTouchHelper.ActionStateDrag)
                {
                    viewHolder.ItemView.Alpha = 0.85f;
                }
            }
        }
    }
}
