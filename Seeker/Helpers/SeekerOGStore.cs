using Seeker.Helpers;
using Common;

namespace Seeker
{
    /// <summary>Persistence for SeekerOG-specific data (favourite searches, filter profiles, toggles).</summary>
    public static class SeekerOGStore
    {
        private const string HideDuplicatesKey = "SeekerOG_HideDuplicates";

        public static void RestoreSearchTools(Android.Content.ISharedPreferences prefs)
        {
            try
            {
                SearchTools.FavoriteSearches = SearchTools.DeserializeList(prefs.GetString(KeyConsts.M_OG_FavoriteSearches, string.Empty));
                SearchTools.FilterProfiles = SearchTools.DeserializeProfiles(prefs.GetString(KeyConsts.M_OG_SavedFilters, string.Empty));
                SearchTools.HideDuplicates = prefs.GetBoolean(HideDuplicatesKey, false);
                if (SearchTools.FilterProfiles.Count == 0)
                {
                    // Useful starting profiles (users can delete them).
                    SearchTools.FilterProfiles.Add(new FilterProfile { Name = "Lossless", Filter = "lossless" });
                    SearchTools.FilterProfiles.Add(new FilterProfile { Name = "Hi-Res", Filter = "format:flac,alac,wav,aiff,dsd hires" });
                    SearchTools.FilterProfiles.Add(new FilterProfile { Name = "MP3 320", Filter = "format:mp3 bitrate>=320" });
                    SearchTools.FilterProfiles.Add(new FilterProfile { Name = "Fast & free", Filter = "free:yes queue<=5" });
                }
            }
            catch (System.Exception e)
            {
                Logger.Debug("RestoreSearchTools failed " + e.Message);
            }
        }

        public static void SaveSearchTools()
        {
            var prefs = SeekerState.SharedPreferences;
            if (prefs == null)
            {
                return;
            }
            lock (PreferencesManager.SharedPrefLock)
            {
                var editor = prefs.Edit();
                editor.PutString(KeyConsts.M_OG_FavoriteSearches, SearchTools.SerializeList(SearchTools.FavoriteSearches));
                editor.PutString(KeyConsts.M_OG_SavedFilters, SearchTools.SerializeProfiles(SearchTools.FilterProfiles));
                editor.PutBoolean(HideDuplicatesKey, SearchTools.HideDuplicates);
                editor.Apply();
            }
        }
    }
}
