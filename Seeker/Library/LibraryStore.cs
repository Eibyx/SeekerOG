using Seeker.Helpers;
using Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Seeker.Library
{
    /// <summary>Local playlists and favourite tracks (stored as MediaStore ids in SharedPreferences).</summary>
    public static class LibraryStore
    {
        public static Dictionary<string, List<long>> Playlists = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        public static HashSet<long> Favorites = new HashSet<long>();
        private static bool loaded;

        public static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;
            var prefs = SeekerState.SharedPreferences;
            if (prefs == null)
            {
                return;
            }
            try
            {
                foreach (var rec in SearchTools.DeserializeList(prefs.GetString(KeyConsts.M_OG_Playlists, string.Empty)))
                {
                    int sep = rec.IndexOf('\u001f');
                    if (sep <= 0) continue;
                    string name = rec.Substring(0, sep);
                    var ids = rec.Substring(sep + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => long.TryParse(x, out long v) ? v : -1).Where(v => v >= 0).ToList();
                    Playlists[name] = ids;
                }
                foreach (var x in (prefs.GetString(KeyConsts.M_OG_FavoriteTracks, string.Empty) ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(x, out long v)) Favorites.Add(v);
                }
            }
            catch (Exception e)
            {
                Logger.Debug("LibraryStore load failed " + e.Message);
            }
        }

        public static void Save()
        {
            var prefs = SeekerState.SharedPreferences;
            if (prefs == null)
            {
                return;
            }
            lock (PreferencesManager.SharedPrefLock)
            {
                var editor = prefs.Edit();
                editor.PutString(KeyConsts.M_OG_Playlists, SearchTools.SerializeList(Playlists.Select(p => p.Key.Replace('\u001f', ' ') + '\u001f' + string.Join(",", p.Value))));
                editor.PutString(KeyConsts.M_OG_FavoriteTracks, string.Join(",", Favorites));
                editor.Apply();
            }
        }

        public static bool ToggleFavorite(long id)
        {
            EnsureLoaded();
            bool now;
            if (!Favorites.Remove(id))
            {
                Favorites.Add(id);
                now = true;
            }
            else
            {
                now = false;
            }
            Save();
            return now;
        }

        public static void AddToPlaylist(string name, long id)
        {
            EnsureLoaded();
            if (!Playlists.TryGetValue(name, out var list))
            {
                list = new List<long>();
                Playlists[name] = list;
            }
            if (!list.Contains(id))
            {
                list.Add(id);
            }
            Save();
        }
    }
}
