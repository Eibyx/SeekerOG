using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using Seeker.Helpers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Seeker.Library
{
    /// <summary>
    /// Small artwork thumbnails for lists (songs, albums, queue, mini player).
    /// * Android 10+: MediaStore's own cached thumbnails (ContentResolver.LoadThumbnail).
    /// * Android 6-9: the album art provider, then the embedded picture.
    /// Bounded LRU cache (~120 x 128px) and at most 2 decodes in parallel, so fast scrolling
    /// never floods memory or the CPU. Views are re-checked by tag to avoid recycled-row flicker.
    /// </summary>
    public static class ThumbnailLoader
    {
        private const int MaxEntries = 120;
        private static readonly object Lock = new object();
        private static readonly LinkedList<string> order = new LinkedList<string>();
        private static readonly Dictionary<string, (Bitmap? bmp, LinkedListNode<string> node)> cache = new Dictionary<string, (Bitmap?, LinkedListNode<string>)>();
        private static readonly SemaphoreSlim decodeGate = new SemaphoreSlim(2);
        private static readonly Handler main = new Handler(Looper.MainLooper!);

        private static bool TryGet(string key, out Bitmap? bmp)
        {
            lock (Lock)
            {
                if (cache.TryGetValue(key, out var e))
                {
                    order.Remove(e.node);
                    order.AddFirst(e.node);
                    bmp = e.bmp;
                    return true;
                }
            }
            bmp = null;
            return false;
        }

        private static void Put(string key, Bitmap? bmp)
        {
            lock (Lock)
            {
                if (cache.ContainsKey(key))
                {
                    return;
                }
                var node = order.AddFirst(key);
                cache[key] = (bmp, node);
                while (order.Count > MaxEntries)
                {
                    var last = order.Last!;
                    order.RemoveLast();
                    cache.Remove(last.Value);
                }
            }
        }

        public static void Bind(ImageView view, LibraryTrack? track, int sizePx, int placeholderRes)
        {
            if (track == null)
            {
                view.Tag = null;
                view.SetImageResource(placeholderRes);
                return;
            }
            string key = (track.AlbumId != 0 ? "a" + track.AlbumId : track.Uri) + "@" + sizePx;
            view.Tag = key;
            if (TryGet(key, out var cached))
            {
                if (cached != null) view.SetImageBitmap(cached);
                else view.SetImageResource(placeholderRes);
                return;
            }
            view.SetImageResource(placeholderRes);
            var ctx = view.Context!.ApplicationContext!;
            Task.Run(async () =>
            {
                await decodeGate.WaitAsync().ConfigureAwait(false);
                Bitmap? bmp = null;
                try
                {
                    // the row may have been recycled while waiting
                    bool stillWanted = true;
                    var tcs = new TaskCompletionSource<bool>();
                    main.Post(() => tcs.TrySetResult(view.Tag?.ToString() == key));
                    stillWanted = await tcs.Task.ConfigureAwait(false);
                    if (!stillWanted)
                    {
                        return; // scrolled away: skip the decode entirely
                    }
                    bmp = Load(ctx, track, sizePx);
                }
                catch (Exception e)
                {
                    Logger.Debug("thumbnail failed: " + e.Message);
                }
                finally
                {
                    decodeGate.Release();
                }
                Put(key, bmp);
                main.Post(() =>
                {
                    if (view.Tag?.ToString() == key && bmp != null)
                    {
                        view.Alpha = 0f;
                        view.SetImageBitmap(bmp);
                        view.Animate()!.Alpha(1f).SetDuration(GlassAppearance.AnimationsEnabled ? 220 : 0).Start();
                    }
                });
            });
        }

        public static Bitmap? Load(Context ctx, LibraryTrack track, int sizePx)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                try
                {
                    return ctx.ContentResolver!.LoadThumbnail(Android.Net.Uri.Parse(track.Uri)!, new Android.Util.Size(sizePx, sizePx), null);
                }
                catch (Exception)
                {
                    // MediaStore has no cached thumbnail: fall back to the embedded picture
                }
                return Player.ArtworkLoader.LoadEmbedded(ctx, track, sizePx);
            }
            if (track.AlbumId != 0)
            {
                try
                {
                    var albumArt = ContentUris.WithAppendedId(Android.Net.Uri.Parse("content://media/external/audio/albumart")!, track.AlbumId);
                    using var stream = ctx.ContentResolver!.OpenInputStream(albumArt);
                    if (stream != null)
                    {
                        var opts = new BitmapFactory.Options { InSampleSize = 2 };
                        var b = BitmapFactory.DecodeStream(stream, null, opts);
                        if (b != null)
                        {
                            return Bitmap.CreateScaledBitmap(b, sizePx, sizePx, true);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return Player.ArtworkLoader.LoadEmbedded(ctx, track, sizePx);
        }
    }
}
