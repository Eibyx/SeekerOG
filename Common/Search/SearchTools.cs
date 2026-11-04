using Soulseek;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Seeker
{
    /// <summary>A named, reusable filter written in the SeekerOG advanced syntax.</summary>
    public sealed class FilterProfile
    {
        public string Name { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pure helpers for SeekerOG search tools: favourite searches, filter profiles, duplicate hiding
    /// and CSV export. Persistence is done by the Android layer (string serialisation helpers here).
    /// </summary>
    public static class SearchTools
    {
        public static List<string> FavoriteSearches = new List<string>();
        public static List<FilterProfile> FilterProfiles = new List<FilterProfile>();
        public static bool HideDuplicates = false;

        private const char RecordSep = '\u001e';
        private const char UnitSep = '\u001f';

        public static string SerializeList(IEnumerable<string> items) => string.Join(RecordSep.ToString(), items.Where(i => !string.IsNullOrEmpty(i)));

        public static List<string> DeserializeList(string? data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return new List<string>();
            }
            return data!.Split(RecordSep).Where(s => s.Length > 0).ToList();
        }

        public static string SerializeProfiles(IEnumerable<FilterProfile> profiles) =>
            SerializeList(profiles.Select(p => p.Name.Replace(UnitSep, ' ') + UnitSep + p.Filter.Replace(UnitSep, ' ')));

        public static List<FilterProfile> DeserializeProfiles(string? data)
        {
            var list = new List<FilterProfile>();
            foreach (var rec in DeserializeList(data))
            {
                int i = rec.IndexOf(UnitSep);
                if (i <= 0)
                {
                    continue;
                }
                list.Add(new FilterProfile { Name = rec.Substring(0, i), Filter = rec.Substring(i + 1) });
            }
            return list;
        }

        public static void ToggleFavorite(string search)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return;
            }
            search = search.Trim();
            int idx = FavoriteSearches.FindIndex(s => string.Equals(s, search, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                FavoriteSearches.RemoveAt(idx);
            }
            else
            {
                FavoriteSearches.Insert(0, search);
            }
        }

        public static bool IsFavorite(string search) =>
            !string.IsNullOrWhiteSpace(search) && FavoriteSearches.Any(s => string.Equals(s, search.Trim(), StringComparison.OrdinalIgnoreCase));

        public static void SaveProfile(string name, string filter)
        {
            name = name.Trim();
            if (name.Length == 0)
            {
                return;
            }
            FilterProfiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            FilterProfiles.Insert(0, new FilterProfile { Name = name, Filter = filter });
        }

        /// <summary>
        /// Identity used to detect the same folder shared by several users: folder name + number of files
        /// + total size. The first occurrence (best according to the current sort order) is kept.
        /// </summary>
        public static string DuplicateKey(SearchResponse s, bool hideLocked)
        {
            var files = hideLocked ? s.Files : s.Files.Concat(s.LockedFiles);
            File? first = files.FirstOrDefault();
            if (first == null)
            {
                return s.Username;
            }
            string folder = SimpleHelpers.GetFolderNameFromFile(first.Filename).ToString().ToLowerInvariant();
            long total = 0;
            int count = 0;
            foreach (var f in files)
            {
                total += f.Size;
                count++;
            }
            if (count == 1)
            {
                return folder + "|" + SimpleHelpers.GetFileNameFromFile(first.Filename).ToString().ToLowerInvariant() + "|" + total;
            }
            return folder + "|" + count + "|" + total;
        }

        public static List<SearchResponse> RemoveDuplicates(IEnumerable<SearchResponse> responses, bool hideLocked)
        {
            var seen = new HashSet<string>();
            var result = new List<SearchResponse>();
            foreach (var r in responses)
            {
                if (seen.Add(DuplicateKey(r, hideLocked)))
                {
                    result.Add(r);
                }
            }
            return result;
        }

        private static string Csv(string? v)
        {
            v ??= string.Empty;
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            {
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            }
            return v;
        }

        private static string Opt(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "unknown";

        /// <summary>CSV export of results. Missing metadata is written as "unknown" (never invented).</summary>
        public static string ToCsv(IEnumerable<SearchResponse> responses, bool hideLocked)
        {
            var sb = new StringBuilder();
            sb.AppendLine("username,free_slot,upload_speed_bytes_per_s,queue_length,path,size_bytes,extension,bitrate_kbps,sample_rate_hz,bit_depth,duration_s,vbr,locked");
            foreach (var r in responses)
            {
                IEnumerable<(File f, bool locked)> files = r.Files.Select(f => (f, false));
                if (!hideLocked)
                {
                    files = files.Concat(r.LockedFiles.Select(f => (f, true)));
                }
                foreach (var (f, locked) in files)
                {
                    sb.Append(Csv(r.Username)).Append(',')
                      .Append(r.HasFreeUploadSlot ? "yes" : "no").Append(',')
                      .Append(r.UploadSpeed.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(r.QueueLength.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(Csv(f.Filename)).Append(',')
                      .Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(Csv(AdvancedQuery.ExtensionOf(f.Filename))).Append(',')
                      .Append(Opt(f.BitRate)).Append(',')
                      .Append(Opt(f.SampleRate)).Append(',')
                      .Append(Opt(f.BitDepth)).Append(',')
                      .Append(Opt(f.Length)).Append(',')
                      .Append(f.IsVariableBitRate.HasValue ? (f.IsVariableBitRate.Value ? "yes" : "no") : "unknown").Append(',')
                      .Append(locked ? "yes" : "no")
                      .AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Builds a filter string in the advanced syntax from graphical choices (used by the filter dialog).
        /// </summary>
        public static string BuildFilter(IEnumerable<string> formats, bool lossless, bool hires, int minBitrate, int maxBitrate,
            int minSampleRate, int minBitDepth, double minSizeMB, double maxSizeMB, int minDurationS, int maxDurationS,
            bool freeSlotOnly, int maxQueue, int minSpeedKBs, string excludeWords, string extra)
        {
            var parts = new List<string>();
            var fmts = formats.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().ToLowerInvariant()).ToList();
            if (fmts.Count > 0) parts.Add("format:" + string.Join(",", fmts));
            if (lossless) parts.Add("lossless");
            if (hires) parts.Add("hires");
            if (minBitrate > 0) parts.Add("bitrate>=" + minBitrate);
            if (maxBitrate > 0) parts.Add("bitrate<=" + maxBitrate);
            if (minSampleRate > 0) parts.Add("samplerate>=" + minSampleRate);
            if (minBitDepth > 0) parts.Add("bits>=" + minBitDepth);
            if (minSizeMB > 0) parts.Add("size>=" + minSizeMB.ToString(CultureInfo.InvariantCulture) + "MB");
            if (maxSizeMB > 0) parts.Add("size<=" + maxSizeMB.ToString(CultureInfo.InvariantCulture) + "MB");
            if (minDurationS > 0) parts.Add("dur>=" + minDurationS);
            if (maxDurationS > 0) parts.Add("dur<=" + maxDurationS);
            if (freeSlotOnly) parts.Add("free:yes");
            if (maxQueue >= 0) parts.Add("queue<=" + maxQueue);
            if (minSpeedKBs > 0) parts.Add("speed>=" + minSpeedKBs + "kb");
            foreach (var w in (excludeWords ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                parts.Add("-" + w.TrimStart('-'));
            }
            if (!string.IsNullOrWhiteSpace(extra)) parts.Add(extra.Trim());
            return string.Join(" ", parts);
        }
    }
}
