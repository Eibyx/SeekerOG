using Common;
using Seeker.Helpers;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Seeker.Library;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Seeker.Player
{
    /// <summary>Album art and technical audio information read from the file itself (never guessed).</summary>
    public static class ArtworkLoader
    {
        private static readonly object CacheLock = new object();
        private static readonly LinkedList<(string key, Bitmap bmp)> cache = new LinkedList<(string, Bitmap)>();
        private const int CacheSize = 24;

        public static Bitmap? LoadEmbedded(Context context, LibraryTrack track, int maxPx)
        {
            string key = track.Uri + "@" + maxPx;
            lock (CacheLock)
            {
                foreach (var e in cache)
                {
                    if (e.key == key) return e.bmp;
                }
            }
            Bitmap? result = null;
            var mmr = new MediaMetadataRetriever();
            try
            {
                mmr.SetDataSource(context, Android.Net.Uri.Parse(track.Uri));
                byte[]? pic = mmr.GetEmbeddedPicture();
                if (pic != null && pic.Length > 0)
                {
                    var opts = new BitmapFactory.Options { InJustDecodeBounds = true };
                    BitmapFactory.DecodeByteArray(pic, 0, pic.Length, opts);
                    int sample = 1;
                    while (opts.OutWidth / (sample * 2) >= maxPx && opts.OutHeight / (sample * 2) >= maxPx)
                    {
                        sample *= 2;
                    }
                    var decode = new BitmapFactory.Options { InSampleSize = sample };
                    result = BitmapFactory.DecodeByteArray(pic, 0, pic.Length, decode);
                }
            }
            catch (Exception e)
            {
                Logger.Debug("art load failed: " + e.Message);
            }
            finally
            {
                try { mmr.Release(); } catch (Exception) { }
            }
            if (result != null)
            {
                lock (CacheLock)
                {
                    cache.AddFirst((key, result));
                    while (cache.Count > CacheSize) cache.RemoveLast();
                }
            }
            return result;
        }

        /// <summary>
        /// Human readable technical info. Values that the container / decoder does not expose are shown as unknown.
        /// </summary>
        public static string DescribeQuality(Context context, LibraryTrack track)
        {
            string unknown = context.GetString(Resource.String.seekerog_unknown);
            string codec = track.Extension.Length > 0 ? track.Extension : unknown;
            string sampleRate = unknown, channels = unknown, bitrate = unknown, bits = unknown;
            if (track.BitrateBps > 0)
            {
                bitrate = (track.BitrateBps / 1000) + " kbps";
            }
            var extractor = new MediaExtractor();
            try
            {
                extractor.SetDataSource(context, Android.Net.Uri.Parse(track.Uri)!, null);
                for (int i = 0; i < extractor.TrackCount; i++)
                {
                    var f = extractor.GetTrackFormat(i);
                    string mime = f.GetString(MediaFormat.KeyMime) ?? string.Empty;
                    if (!mime.StartsWith("audio/", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    codec = codec + " (" + mime.Substring(6) + ")";
                    if (f.ContainsKey(MediaFormat.KeySampleRate))
                    {
                        sampleRate = (f.GetInteger(MediaFormat.KeySampleRate) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";
                    }
                    if (f.ContainsKey(MediaFormat.KeyChannelCount))
                    {
                        channels = f.GetInteger(MediaFormat.KeyChannelCount).ToString(CultureInfo.InvariantCulture);
                    }
                    if (bitrate == unknown && f.ContainsKey(MediaFormat.KeyBitRate))
                    {
                        bitrate = (f.GetInteger(MediaFormat.KeyBitRate) / 1000) + " kbps";
                    }
                    if (f.ContainsKey("bits-per-sample"))
                    {
                        bits = f.GetInteger("bits-per-sample") + " bit";
                    }
                    else if (OperatingSystem.IsAndroidVersionAtLeast(24) && f.ContainsKey(MediaFormat.KeyPcmEncoding))
                    {
                        int enc = f.GetInteger(MediaFormat.KeyPcmEncoding);
                        bits = enc switch { 2 => "16 bit", 3 => "8 bit", 4 => "32 bit float", 21 => "24 bit", 22 => "32 bit", _ => unknown };
                    }
                    break;
                }
            }
            catch (Exception e)
            {
                Logger.Debug("quality probe failed: " + e.Message);
            }
            finally
            {
                try { extractor.Release(); } catch (Exception) { }
            }
            if (bitrate == unknown && track.DurationMs > 0 && track.Size > 0)
            {
                // average over the whole file (container overhead and artwork included) - labelled as approximate
                bitrate = "~" + (long)(track.Size * 8 / (track.DurationMs / 1000.0) / 1000) + " kbps";
            }
            return string.Format(context.GetString(Resource.String.seekerog_quality_line), codec, bitrate, sampleRate, bits, channels);
        }

        /// <summary>Describes the current audio output route (speaker, Bluetooth, wired, USB DAC).</summary>
        public static string DescribeOutput(Context context)
        {
            try
            {
                var am = (AudioManager?)context.GetSystemService(Context.AudioService);
                if (am == null) return context.GetString(Resource.String.seekerog_unknown);
                var devices = am.GetDevices(GetDevicesTargets.Outputs);
                AudioDeviceInfo? best = null;
                int bestRank = -1;
                foreach (var d in devices ?? Array.Empty<AudioDeviceInfo>())
                {
                    int rank = d.Type switch
                    {
                        AudioDeviceType.UsbDevice or AudioDeviceType.UsbHeadset or AudioDeviceType.UsbAccessory => 5,
                        AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset or AudioDeviceType.LineAnalog or AudioDeviceType.LineDigital => 4,
                        AudioDeviceType.BluetoothA2dp => 3,
                        AudioDeviceType.Hdmi => 2,
                        AudioDeviceType.BuiltinSpeaker => 1,
                        _ => 0,
                    };
                    if (rank > bestRank)
                    {
                        bestRank = rank;
                        best = d;
                    }
                }
                if (best == null) return context.GetString(Resource.String.seekerog_unknown);
                string kind = best.Type switch
                {
                    AudioDeviceType.UsbDevice or AudioDeviceType.UsbHeadset or AudioDeviceType.UsbAccessory => "USB (DAC)",
                    AudioDeviceType.WiredHeadphones or AudioDeviceType.WiredHeadset => context.GetString(Resource.String.seekerog_output_wired),
                    AudioDeviceType.BluetoothA2dp => "Bluetooth",
                    AudioDeviceType.Hdmi => "HDMI",
                    AudioDeviceType.BuiltinSpeaker => context.GetString(Resource.String.seekerog_output_speaker),
                    _ => best.Type.ToString(),
                };
                var rates = best.GetSampleRates();
                string rateInfo = rates != null && rates.Length > 0 ? " · " + string.Join("/", Array.ConvertAll(rates, r => (r / 1000.0).ToString("0.#", CultureInfo.InvariantCulture))) + " kHz" : string.Empty;
                string name = best.ProductName?.ToString() ?? string.Empty;
                return kind + (name.Length > 0 ? " (" + name + ")" : string.Empty) + rateInfo;
            }
            catch (Exception e)
            {
                Logger.Debug("output probe failed " + e.Message);
                return context.GetString(Resource.String.seekerog_unknown);
            }
        }
    }
}
