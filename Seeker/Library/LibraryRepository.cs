using Common;
using Seeker.Helpers;
using Android.Content;
using Android.Database;
using Android.Provider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Seeker.Library
{
    /// <summary>A local audio file as indexed by Android's MediaStore (no file is copied or duplicated).</summary>
    public sealed class LibraryTrack
    {
        public long Id;
        public string Uri = string.Empty;
        public string Title = string.Empty;
        public string Artist = string.Empty;
        public string Album = string.Empty;
        public long AlbumId;
        public long DurationMs;
        public long Size;
        public string MimeType = string.Empty;
        public string Folder = string.Empty;
        public string DisplayName = string.Empty;
        public int BitrateBps; // MediaStore.BITRATE (Android 11+), 0 = unknown
        public long DateAddedSec;

        public string Extension
        {
            get
            {
                int dot = DisplayName.LastIndexOf('.');
                return dot >= 0 ? DisplayName.Substring(dot + 1).ToUpperInvariant() : string.Empty;
            }
        }
    }

    /// <summary>
    /// Reads the device music library through MediaStore (works with Scoped Storage, needs
    /// READ_MEDIA_AUDIO on Android 13+ or READ_EXTERNAL_STORAGE before). The scan runs off the UI thread.
    /// </summary>
    public static class LibraryRepository
    {
        private static readonly object Lock = new object();
        private static List<LibraryTrack> tracks = new List<LibraryTrack>();
        private static Task? scanTask;

        public static IReadOnlyList<LibraryTrack> Tracks
        {
            get { lock (Lock) { return tracks; } }
        }

        public static bool HasScanned { get; private set; }

        public static Task ScanAsync(Context context, bool force = false)
        {
            lock (Lock)
            {
                if (scanTask != null && !scanTask.IsCompleted)
                {
                    return scanTask;
                }
                if (HasScanned && !force)
                {
                    return Task.CompletedTask;
                }
                var appContext = context.ApplicationContext;
                scanTask = Task.Run(() =>
                {
                    var result = Query(appContext);
                    lock (Lock)
                    {
                        tracks = result;
                        HasScanned = true;
                    }
                });
                return scanTask;
            }
        }

        private static string GetString(ICursor c, int idx) => idx >= 0 && !c.IsNull(idx) ? (c.GetString(idx) ?? string.Empty) : string.Empty;
        private static long GetLong(ICursor c, int idx) => idx >= 0 && !c.IsNull(idx) ? c.GetLong(idx) : 0;

        private static List<LibraryTrack> Query(Context context)
        {
            var list = new List<LibraryTrack>();
            var collection = MediaStore.Audio.Media.ExternalContentUri;
            var projection = new List<string>
            {
                IBaseColumns.Id,
                MediaStore.IMediaColumns.Title,
                MediaStore.Audio.IAudioColumns.Artist,
                MediaStore.Audio.IAudioColumns.Album,
                MediaStore.Audio.IAudioColumns.AlbumId,
                MediaStore.IMediaColumns.Duration,
                MediaStore.IMediaColumns.Size,
                MediaStore.IMediaColumns.MimeType,
                MediaStore.IMediaColumns.DisplayName,
                MediaStore.IMediaColumns.DateAdded,
            };
            bool hasRelativePath = OperatingSystem.IsAndroidVersionAtLeast(29);
            if (hasRelativePath)
            {
                projection.Add(MediaStore.IMediaColumns.RelativePath);
            }
            else
            {
#pragma warning disable CA1422
                projection.Add(MediaStore.IMediaColumns.Data);
#pragma warning restore CA1422
            }
            bool hasBitrate = OperatingSystem.IsAndroidVersionAtLeast(30);
            if (hasBitrate)
            {
                projection.Add(MediaStore.IMediaColumns.Bitrate);
            }
            string selection = MediaStore.Audio.IAudioColumns.IsMusic + " != 0";
            try
            {
                using var cursor = context.ContentResolver.Query(collection, projection.ToArray(), selection, null, MediaStore.IMediaColumns.Title + " COLLATE NOCASE ASC");
                if (cursor == null)
                {
                    return list;
                }
                int iId = cursor.GetColumnIndex(IBaseColumns.Id);
                int iTitle = cursor.GetColumnIndex(MediaStore.IMediaColumns.Title);
                int iArtist = cursor.GetColumnIndex(MediaStore.Audio.IAudioColumns.Artist);
                int iAlbum = cursor.GetColumnIndex(MediaStore.Audio.IAudioColumns.Album);
                int iAlbumId = cursor.GetColumnIndex(MediaStore.Audio.IAudioColumns.AlbumId);
                int iDur = cursor.GetColumnIndex(MediaStore.IMediaColumns.Duration);
                int iSize = cursor.GetColumnIndex(MediaStore.IMediaColumns.Size);
                int iMime = cursor.GetColumnIndex(MediaStore.IMediaColumns.MimeType);
                int iName = cursor.GetColumnIndex(MediaStore.IMediaColumns.DisplayName);
                int iAdded = cursor.GetColumnIndex(MediaStore.IMediaColumns.DateAdded);
#pragma warning disable CA1422
                int iPath = hasRelativePath ? cursor.GetColumnIndex(MediaStore.IMediaColumns.RelativePath) : cursor.GetColumnIndex(MediaStore.IMediaColumns.Data);
#pragma warning restore CA1422
                int iBitrate = hasBitrate ? cursor.GetColumnIndex(MediaStore.IMediaColumns.Bitrate) : -1;
                while (cursor.MoveToNext())
                {
                    long id = GetLong(cursor, iId);
                    string path = GetString(cursor, iPath);
                    string folder;
                    if (hasRelativePath)
                    {
                        folder = path.TrimEnd('/');
                    }
                    else
                    {
                        int slash = path.LastIndexOf('/');
                        folder = slash > 0 ? path.Substring(0, slash) : path;
                    }
                    var t = new LibraryTrack
                    {
                        Id = id,
                        Uri = ContentUris.WithAppendedId(collection, id).ToString(),
                        Title = GetString(cursor, iTitle),
                        Artist = GetString(cursor, iArtist),
                        Album = GetString(cursor, iAlbum),
                        AlbumId = GetLong(cursor, iAlbumId),
                        DurationMs = GetLong(cursor, iDur),
                        Size = GetLong(cursor, iSize),
                        MimeType = GetString(cursor, iMime),
                        DisplayName = GetString(cursor, iName),
                        DateAddedSec = GetLong(cursor, iAdded),
                        Folder = folder,
                        BitrateBps = (int)GetLong(cursor, iBitrate),
                    };
                    if (string.IsNullOrEmpty(t.Artist) || t.Artist == "<unknown>")
                    {
                        t.Artist = string.Empty;
                    }
                    if (string.IsNullOrEmpty(t.Title))
                    {
                        t.Title = t.DisplayName;
                    }
                    list.Add(t);
                }
            }
            catch (Exception e)
            {
                Logger.Debug("Library query failed: " + e.Message);
            }
            return list;
        }

        public static string FolderLeaf(string folder)
        {
            var trimmed = folder.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        /// <summary>
        /// True if the track lives in the configured SeekerOG download folder (matched by folder name,
        /// since a SAF tree URI does not expose an absolute path).
        /// </summary>
        public static bool IsInDownloadFolder(LibraryTrack t, string downloadFolderName)
        {
            if (string.IsNullOrEmpty(downloadFolderName))
            {
                return false;
            }
            return t.Folder.IndexOf("/" + downloadFolderName, StringComparison.OrdinalIgnoreCase) >= 0
                || t.Folder.StartsWith(downloadFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Playback position: 0 is a real position ("0:00"), unlike an unknown duration.</summary>
        public static string FormatPosition(long ms) => ms <= 0 ? "0:00" : FormatDuration(ms);

        public static string FormatDuration(long ms)
        {
            if (ms <= 0) return "--:--";
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
        }
    }
}
