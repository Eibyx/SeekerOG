using Soulseek;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Seeker
{
    /// <summary>
    /// SeekerOG advanced search syntax (inspired by Nicotine+ result filters).
    ///
    /// A query string is split in two parts:
    ///  * <see cref="ServerQuery"/> - the plain words, "-exclusions" and the words of quoted phrases.
    ///    This is the only part that is sent to the official Soulseek server, which only understands
    ///    space separated words and "-excluded" words (no new protocol features are assumed).
    ///  * Local predicates - everything else ("format:flac", "bits:24", "size&lt;100MB", "re:/.../", ...)
    ///    which is evaluated on this device against the search responses that were received.
    ///
    /// Metadata that the peer did not send (bit depth, sample rate, duration, bitrate are all optional
    /// attributes in the protocol) is treated as unknown: a filter that requires such a value does not
    /// match a file with an unknown value (we never invent metadata).
    ///
    /// Examples:
    ///   formato:flac bits:24 frecuencia:96000 -live
    ///   artista:"Daft Punk" formato:flac
    ///   extension:mp3 bitrate:320 tamaño&lt;100MB
    ///   format:flac,alac hires free:yes queue&lt;=10 dur:3:30~5
    /// </summary>
    public sealed class AdvancedQuery
    {
        public const int MaxRegexLength = 256;
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(25);

        public enum Op { Eq, Gt, Ge, Lt, Le, Approx }

        public readonly struct NumericCondition
        {
            public readonly Op Op;
            public readonly double Value;
            public readonly double Tolerance;

            public NumericCondition(Op op, double value, double tolerance = 0)
            {
                Op = op;
                Value = value;
                Tolerance = tolerance;
            }

            public bool Matches(double actual)
            {
                switch (Op)
                {
                    case Op.Eq: return Math.Abs(actual - Value) < 0.0001;
                    case Op.Gt: return actual > Value;
                    case Op.Ge: return actual >= Value;
                    case Op.Lt: return actual < Value;
                    case Op.Le: return actual <= Value;
                    case Op.Approx: return Math.Abs(actual - Value) <= Tolerance;
                    default: return true;
                }
            }

            public override string ToString()
            {
                string op = Op switch { Op.Eq => "=", Op.Gt => ">", Op.Ge => ">=", Op.Lt => "<", Op.Le => "<=", _ => "~" };
                return Op == Op.Approx ? $"{Value}~{Tolerance}" : $"{op}{Value}";
            }
        }

        /// <summary>The original, unmodified text.</summary>
        public string Original { get; private set; } = string.Empty;

        /// <summary>Text that is safe to send to the Soulseek server (words and -exclusions only).</summary>
        public string ServerQuery { get; private set; } = string.Empty;

        /// <summary>Plain words (must all be contained in the folder or file name).</summary>
        public List<string> Words { get; } = new List<string>();
        /// <summary>Words that must not be contained (from "-word").</summary>
        public List<string> ExcludedWords { get; } = new List<string>();
        /// <summary>Exact phrases (from "quoted text").</summary>
        public List<string> Phrases { get; } = new List<string>();
        public List<string> ExcludedPhrases { get; } = new List<string>();
        /// <summary>Partial word matches (from "word*" or "*word").</summary>
        public List<string> PartialWords { get; } = new List<string>();

        /// <summary>Allowed extensions without dot (lower case). Empty = any.</summary>
        public HashSet<string> Formats { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedFormats { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool LosslessOnly { get; private set; }
        public bool LossyOnly { get; private set; }
        public bool HiResOnly { get; private set; }

        public List<NumericCondition> BitRate { get; } = new List<NumericCondition>();      // kbps
        public List<NumericCondition> SampleRate { get; } = new List<NumericCondition>();   // Hz
        public List<NumericCondition> BitDepth { get; } = new List<NumericCondition>();     // bits
        public List<NumericCondition> Size { get; } = new List<NumericCondition>();         // bytes
        public List<NumericCondition> Duration { get; } = new List<NumericCondition>();     // seconds
        public List<NumericCondition> UploadSpeed { get; } = new List<NumericCondition>();  // bytes / s
        public List<NumericCondition> QueueLength { get; } = new List<NumericCondition>();
        public List<NumericCondition> FileCount { get; } = new List<NumericCondition>();

        public bool? Vbr { get; private set; }
        public bool? FreeSlot { get; private set; }
        public bool? FavoriteUser { get; private set; }
        public bool? AlreadyDownloaded { get; private set; }

        public List<string> Users { get; } = new List<string>();
        public List<string> ExcludedUsers { get; } = new List<string>();
        public List<string> FolderContains { get; } = new List<string>();
        public List<string> FileNameContains { get; } = new List<string>();
        /// <summary>
        /// artist:/album:/title: terms. The Soulseek protocol does not carry ID3 tags in search results,
        /// so these are matched against the path (folder + file name), which is how most shares are organised.
        /// </summary>
        public List<string> PathTerms { get; } = new List<string>();

        public Regex? PathRegex { get; private set; }
        public string? RegexError { get; private set; }

        /// <summary>Tokens that could not be understood (shown to the user as warnings).</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Hooks provided by the Android layer (user list, transfer history).</summary>
        public static Func<string, bool>? IsFavoriteUserProvider;
        public static Func<string, string, bool>? IsAlreadyDownloadedProvider;

        public bool HasFileLevelPredicates =>
            Formats.Count > 0 || ExcludedFormats.Count > 0 || LosslessOnly || LossyOnly || HiResOnly ||
            BitRate.Count > 0 || SampleRate.Count > 0 || BitDepth.Count > 0 || Size.Count > 0 || Duration.Count > 0 ||
            Vbr.HasValue || FileNameContains.Count > 0 || PathRegex != null || AlreadyDownloaded.HasValue;

        public bool HasResponseLevelPredicates =>
            UploadSpeed.Count > 0 || QueueLength.Count > 0 || FileCount.Count > 0 || FreeSlot.HasValue || FavoriteUser.HasValue ||
            Users.Count > 0 || ExcludedUsers.Count > 0 || FolderContains.Count > 0;

        public bool HasTextPredicates =>
            Words.Count > 0 || ExcludedWords.Count > 0 || Phrases.Count > 0 || ExcludedPhrases.Count > 0 || PartialWords.Count > 0 || PathTerms.Count > 0;

        /// <summary>True if anything beyond what the server already evaluates must be checked locally.</summary>
        public bool HasLocalFilters =>
            HasFileLevelPredicates || HasResponseLevelPredicates || Phrases.Count > 0 || ExcludedPhrases.Count > 0 || PartialWords.Count > 0 || PathTerms.Count > 0;

        public bool IsEmpty => !HasLocalFilters && !HasTextPredicates;

        private static readonly Dictionary<string, string> KeyAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // format / extension
            { "format", "format" }, { "formato", "format" }, { "fmt", "format" }, { "type", "format" }, { "tipo", "format" },
            { "ext", "format" }, { "extension", "format" }, { "extensión", "format" }, { "extencion", "format" },
            // quality
            { "bitrate", "bitrate" }, { "br", "bitrate" }, { "kbps", "bitrate" }, { "mbr", "bitrate" }, { "minbitrate", "bitrate" },
            { "samplerate", "samplerate" }, { "sr", "samplerate" }, { "hz", "samplerate" }, { "khz", "samplerate" },
            { "freq", "samplerate" }, { "frecuencia", "samplerate" }, { "frequency", "samplerate" }, { "muestreo", "samplerate" },
            { "bits", "bits" }, { "bitdepth", "bits" }, { "depth", "bits" }, { "profundidad", "bits" },
            // size / duration
            { "size", "size" }, { "tamaño", "size" }, { "tamano", "size" }, { "tam", "size" }, { "sz", "size" },
            { "mfs", "size" }, { "minfilesize", "size" },
            { "duration", "duration" }, { "duracion", "duration" }, { "duración", "duration" }, { "dur", "duration" },
            { "length", "duration" }, { "len", "duration" }, { "time", "duration" }, { "tiempo", "duration" },
            // availability
            { "speed", "speed" }, { "velocidad", "speed" }, { "vel", "speed" },
            { "queue", "queue" }, { "cola", "queue" }, { "q", "queue" },
            { "free", "free" }, { "slot", "free" }, { "slots", "free" }, { "libre", "free" }, { "plaza", "free" }, { "plazas", "free" },
            { "files", "files" }, { "archivos", "files" }, { "mfif", "files" }, { "minfilesinfolder", "files" },
            { "fav", "fav" }, { "favorite", "fav" }, { "favorito", "fav" }, { "favoritos", "fav" },
            { "downloaded", "downloaded" }, { "descargado", "downloaded" }, { "dl", "downloaded" },
            { "vbr", "vbr" },
            // text
            { "user", "user" }, { "usuario", "user" }, { "u", "user" },
            { "folder", "folder" }, { "carpeta", "folder" }, { "dir", "folder" },
            { "file", "file" }, { "archivo", "file" }, { "name", "file" }, { "nombre", "file" }, { "filename", "file" },
            { "artist", "path" }, { "artista", "path" }, { "album", "path" }, { "álbum", "path" },
            { "title", "path" }, { "titulo", "path" }, { "título", "path" }, { "path", "path" }, { "ruta", "path" },
            { "re", "regex" }, { "regex", "regex" }, { "regexp", "regex" },
        };

        private static readonly Dictionary<string, string> FlagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "lossless", "lossless" }, { "sinperdida", "lossless" }, { "sinpérdida", "lossless" },
            { "lossy", "lossy" }, { "conperdida", "lossy" }, { "conpérdida", "lossy" },
            { "hires", "hires" }, { "hi-res", "hires" },
            { "isvbr", "vbr" }, { "iscbr", "cbr" },
            { "freeslot", "free" },
        };

        /// <summary>Format groups usable inside format: (e.g. format:lossless).</summary>
        public static readonly Dictionary<string, string[]> FormatGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "dsd", new[] { "dsf", "dff" } },
            { "aiff", new[] { "aiff", "aif" } },
            { "alac", new[] { "m4a", "alac" } }, // ALAC is usually stored in .m4a; codec is not reported by the protocol.
            { "aac", new[] { "aac", "m4a" } },
            { "ogg", new[] { "ogg", "oga" } },
            { "zip", new[] { "zip" } },
            { "rar", new[] { "rar" } },
        };

        public static readonly HashSet<string> LosslessExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "flac", "wav", "aiff", "aif", "alac", "ape", "wv", "dsf", "dff", "tta", "tak"
        };

        public static AdvancedQuery Parse(string? text)
        {
            var q = new AdvancedQuery();
            q.Original = text ?? string.Empty;
            var serverParts = new List<string>();
            foreach (string rawToken in Tokenize(q.Original))
            {
                q.ParseToken(rawToken, serverParts);
            }
            q.ServerQuery = string.Join(" ", serverParts);
            return q;
        }

        /// <summary>Splits on whitespace, keeping "quoted phrases" (and key:"quoted values") together.</summary>
        internal static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            bool inRegex = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' && !inRegex)
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    continue;
                }
                // re:/.../ may contain spaces
                if (c == '/' && !inQuotes)
                {
                    string soFar = sb.ToString();
                    if (!inRegex && (soFar.EndsWith("re:", StringComparison.OrdinalIgnoreCase) || soFar.EndsWith("regex:", StringComparison.OrdinalIgnoreCase) || soFar.EndsWith("regexp:", StringComparison.OrdinalIgnoreCase)))
                    {
                        inRegex = true;
                        sb.Append(c);
                        continue;
                    }
                    if (inRegex && (i == 0 || text[i - 1] != '\\'))
                    {
                        inRegex = false;
                        sb.Append(c);
                        continue;
                    }
                }
                if (char.IsWhiteSpace(c) && !inQuotes && !inRegex)
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }
            return tokens;
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                return s.Substring(1, s.Length - 2);
            }
            return s.Trim('"');
        }

        private void ParseToken(string token, List<string> serverParts)
        {
            if (token.Length == 0 || token == "-")
            {
                return;
            }

            bool negated = token[0] == '-' && token.Length > 1;
            string body = negated ? token.Substring(1) : token;

            // quoted phrase
            if (body.StartsWith("\"", StringComparison.Ordinal))
            {
                string phrase = Unquote(body).Trim();
                if (phrase.Length == 0)
                {
                    return;
                }
                if (negated)
                {
                    ExcludedPhrases.Add(phrase);
                    // a single excluded word can be passed to the server; phrases cannot.
                    if (!phrase.Contains(' '))
                    {
                        serverParts.Add("-" + phrase);
                    }
                }
                else
                {
                    Phrases.Add(phrase);
                    serverParts.AddRange(phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                }
                return;
            }

            // flags without value
            if (!negated && FlagAliases.TryGetValue(body, out string? flag))
            {
                ApplyFlag(flag);
                return;
            }

            // key[op]value
            int opIndex = IndexOfOperator(body, out string op);
            if (opIndex > 0)
            {
                string key = body.Substring(0, opIndex);
                string value = Unquote(body.Substring(opIndex + op.Length));
                if (KeyAliases.TryGetValue(key, out string? canonical))
                {
                    if (value.Length == 0)
                    {
                        Warnings.Add(token);
                        return;
                    }
                    if (!ApplyKeyValue(canonical, op, value, negated, serverParts))
                    {
                        Warnings.Add(token);
                    }
                    return;
                }
            }

            // plain word (possibly with wildcard)
            if (negated)
            {
                ExcludedWords.Add(body);
                serverParts.Add("-" + body);
                return;
            }
            if (body.Contains('*'))
            {
                string stripped = body.Trim('*');
                if (stripped.Length > 0)
                {
                    PartialWords.Add(stripped);
                    // The server supports a leading wildcard "*ord"; keep it as is (it matches only
                    // words ending with the string). A trailing "*" is not supported, send the word.
                    serverParts.Add(body.StartsWith("*", StringComparison.Ordinal) && !body.EndsWith("*", StringComparison.Ordinal) ? body : stripped);
                }
                return;
            }
            Words.Add(body);
            serverParts.Add(body);
        }

        private static int IndexOfOperator(string body, out string op)
        {
            op = string.Empty;
            for (int i = 1; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '"')
                {
                    return -1;
                }
                if (c == ':' || c == '=' || c == '<' || c == '>' || c == '~')
                {
                    if ((c == '<' || c == '>') && i + 1 < body.Length && body[i + 1] == '=')
                    {
                        op = body.Substring(i, 2);
                    }
                    else if (c == ':' && i + 1 < body.Length && (body[i + 1] == '<' || body[i + 1] == '>' || body[i + 1] == '='))
                    {
                        // key:>=value
                        op = (i + 2 < body.Length && body[i + 2] == '=' && body[i + 1] != '=') ? body.Substring(i, 3) : body.Substring(i, 2);
                    }
                    else
                    {
                        op = c.ToString();
                    }
                    return i;
                }
            }
            return -1;
        }

        private void ApplyFlag(string flag)
        {
            switch (flag)
            {
                case "lossless": LosslessOnly = true; break;
                case "lossy": LossyOnly = true; break;
                case "hires": HiResOnly = true; break;
                case "vbr": Vbr = true; break;
                case "cbr": Vbr = false; break;
                case "free": FreeSlot = true; break;
            }
        }

        private static bool? ParseBool(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "1": case "y": case "yes": case "si": case "sí": case "true": case "on": case "s":
                    return true;
                case "0": case "n": case "no": case "false": case "off":
                    return false;
                default:
                    return null;
            }
        }

        private bool ApplyKeyValue(string key, string op, string value, bool negated, List<string> serverParts)
        {
            switch (key)
            {
                case "format":
                {
                    foreach (string raw in value.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string f = raw.Trim().TrimStart('.').ToLowerInvariant();
                        if (f == "lossless") { if (negated) LossyOnly = true; else LosslessOnly = true; continue; }
                        if (f == "lossy") { if (negated) LosslessOnly = true; else LossyOnly = true; continue; }
                        if (f == "hires" || f == "hi-res") { HiResOnly = !negated; continue; }
                        var target = negated ? ExcludedFormats : Formats;
                        if (FormatGroups.TryGetValue(f, out string[]? group))
                        {
                            foreach (var g in group) target.Add(g);
                        }
                        else
                        {
                            target.Add(f);
                        }
                    }
                    return true;
                }
                case "bitrate":
                    return AddNumeric(BitRate, op, value, ParseBitRate, defaultOp: Op.Ge);
                case "samplerate":
                    return AddNumeric(SampleRate, op, value, ParseSampleRate, defaultOp: Op.Ge);
                case "bits":
                    return AddNumeric(BitDepth, op, value, ParsePlainNumber, defaultOp: Op.Ge);
                case "size":
                    return AddNumeric(Size, op, value, ParseSize, defaultOp: Op.Ge);
                case "duration":
                    return AddNumeric(Duration, op, value, ParseDuration, defaultOp: Op.Approx, defaultTolerance: 5);
                case "speed":
                    return AddNumeric(UploadSpeed, op, value, ParseSpeed, defaultOp: Op.Ge);
                case "queue":
                    return AddNumeric(QueueLength, op, value, ParsePlainNumber, defaultOp: Op.Le);
                case "files":
                    return AddNumeric(FileCount, op, value, ParsePlainNumber, defaultOp: Op.Ge);
                case "free":
                {
                    bool? b = ParseBool(value);
                    if (b == null) return false;
                    FreeSlot = negated ? !b : b;
                    return true;
                }
                case "fav":
                {
                    bool? b = ParseBool(value);
                    if (b == null) return false;
                    FavoriteUser = negated ? !b : b;
                    return true;
                }
                case "downloaded":
                {
                    bool? b = ParseBool(value);
                    if (b == null) return false;
                    AlreadyDownloaded = negated ? !b : b;
                    return true;
                }
                case "vbr":
                {
                    bool? b = ParseBool(value);
                    if (b == null) return false;
                    Vbr = negated ? !b : b;
                    return true;
                }
                case "user":
                    (negated ? ExcludedUsers : Users).Add(value);
                    return true;
                case "folder":
                    if (negated) ExcludedPhrases.Add(value); else FolderContains.Add(value);
                    return true;
                case "file":
                    if (negated) ExcludedPhrases.Add(value); else FileNameContains.Add(value);
                    return true;
                case "path":
                    if (negated)
                    {
                        ExcludedPhrases.Add(value);
                    }
                    else
                    {
                        PathTerms.Add(value);
                        // artist / album names are useful for the server too (as plain words).
                        serverParts.AddRange(value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                    return true;
                case "regex":
                    return SetRegex(value);
            }
            return false;
        }

        private bool SetRegex(string value)
        {
            string pattern = value;
            RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
            if (pattern.Length >= 2 && pattern[0] == '/')
            {
                int last = pattern.LastIndexOf('/');
                if (last > 0)
                {
                    string flags = pattern.Substring(last + 1);
                    pattern = pattern.Substring(1, last - 1);
                    if (flags.Contains('c')) options &= ~RegexOptions.IgnoreCase; // case sensitive
                }
            }
            if (pattern.Length == 0)
            {
                return false;
            }
            if (pattern.Length > MaxRegexLength)
            {
                RegexError = "regex too long";
                return false;
            }
            try
            {
                // Timeout protects the UI against catastrophic backtracking patterns.
                PathRegex = new Regex(pattern, options, RegexTimeout);
                return true;
            }
            catch (ArgumentException e)
            {
                RegexError = e.Message;
                return false;
            }
        }

        private delegate bool ValueParser(string s, out double v);

        private static bool AddNumeric(List<NumericCondition> list, string op, string value, ValueParser parser, Op defaultOp, double defaultTolerance = 0)
        {
            // ranges: 100-200, 3:00-4:00 (duration), 100MB-1GB
            int dash = FindRangeDash(value);
            if (dash > 0)
            {
                if (parser(value.Substring(0, dash), out double lo) && parser(value.Substring(dash + 1), out double hi))
                {
                    list.Add(new NumericCondition(Op.Ge, Math.Min(lo, hi)));
                    list.Add(new NumericCondition(Op.Le, Math.Max(lo, hi)));
                    return true;
                }
                return false;
            }

            double tolerance = defaultTolerance;
            int tilde = value.IndexOf('~');
            bool approx = false;
            if (op == "~")
            {
                approx = true;
            }
            if (tilde > 0)
            {
                string tol = value.Substring(tilde + 1);
                value = value.Substring(0, tilde);
                if (!parser(tol, out tolerance))
                {
                    return false;
                }
                approx = true;
            }

            if (!parser(value, out double v))
            {
                return false;
            }

            Op parsedOp;
            switch (op.Replace(":", ""))
            {
                case ">": parsedOp = Op.Gt; break;
                case ">=": parsedOp = Op.Ge; break;
                case "<": parsedOp = Op.Lt; break;
                case "<=": parsedOp = Op.Le; break;
                case "=": parsedOp = Op.Eq; break;
                case "~": parsedOp = Op.Approx; break;
                default: parsedOp = defaultOp; break;
            }
            if (approx)
            {
                parsedOp = Op.Approx;
            }
            if (parsedOp == Op.Eq && defaultOp == Op.Approx)
            {
                parsedOp = Op.Approx;
            }
            list.Add(new NumericCondition(parsedOp, v, tolerance));
            return true;
        }

        private static int FindRangeDash(string value)
        {
            for (int i = 1; i < value.Length - 1; i++)
            {
                if (value[i] == '-' && (char.IsDigit(value[i - 1]) || char.IsLetter(value[i - 1])) && char.IsDigit(value[i + 1]))
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool TryParseDouble(string s, out double v)
        {
            return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static bool ParsePlainNumber(string s, out double v)
        {
            return TryParseDouble(s.Trim(), out v);
        }

        private static bool ParseBitRate(string s, out double v)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("kbps")) s = s.Substring(0, s.Length - 4);
            else if (s.EndsWith("k")) s = s.Substring(0, s.Length - 1);
            return TryParseDouble(s, out v);
        }

        /// <summary>Accepts 44100, 44.1, 44.1k, 44.1khz, 96khz, 192000hz. Result in Hz.</summary>
        public static bool ParseSampleRate(string s, out double v)
        {
            s = s.Trim().ToLowerInvariant();
            bool khz = false;
            if (s.EndsWith("khz")) { khz = true; s = s.Substring(0, s.Length - 3); }
            else if (s.EndsWith("hz")) { s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("k")) { khz = true; s = s.Substring(0, s.Length - 1); }
            if (!TryParseDouble(s, out v))
            {
                return false;
            }
            if (khz || v < 1000)
            {
                v *= 1000;
            }
            v = Math.Round(v);
            return true;
        }

        /// <summary>Accepts 100, 100MB, 1.5GB, 700k, 1024B. Default unit MB. Result in bytes.</summary>
        public static bool ParseSize(string s, out double v)
        {
            s = s.Trim().ToLowerInvariant();
            double mult = 1024 * 1024;
            string[] units = { "gib", "gb", "g", "mib", "mb", "m", "kib", "kb", "k", "b" };
            double[] mults = { 1L << 30, 1L << 30, 1L << 30, 1 << 20, 1 << 20, 1 << 20, 1 << 10, 1 << 10, 1 << 10, 1 };
            for (int i = 0; i < units.Length; i++)
            {
                if (s.EndsWith(units[i]))
                {
                    mult = mults[i];
                    s = s.Substring(0, s.Length - units[i].Length);
                    break;
                }
            }
            if (!TryParseDouble(s, out v))
            {
                return false;
            }
            v *= mult;
            return true;
        }

        /// <summary>Accepts 240, 4:00, 1:02:03, 3m30s, 3m, 45s, 1h. Result in seconds.</summary>
        public static bool ParseDuration(string s, out double v)
        {
            s = s.Trim().ToLowerInvariant();
            v = 0;
            if (s.Length == 0)
            {
                return false;
            }
            if (s.Contains(':'))
            {
                var parts = s.Split(':');
                double total = 0;
                foreach (var p in parts)
                {
                    if (!TryParseDouble(p, out double part))
                    {
                        return false;
                    }
                    total = total * 60 + part;
                }
                v = total;
                return true;
            }
            if (s.Contains('h') || s.Contains('m') || s.EndsWith("s"))
            {
                double total = 0;
                var num = new StringBuilder();
                foreach (char c in s)
                {
                    if (char.IsDigit(c) || c == '.' || c == ',')
                    {
                        num.Append(c);
                        continue;
                    }
                    if (num.Length == 0 || !TryParseDouble(num.ToString(), out double n))
                    {
                        return false;
                    }
                    num.Clear();
                    switch (c)
                    {
                        case 'h': total += n * 3600; break;
                        case 'm': total += n * 60; break;
                        case 's': total += n; break;
                        default: return false;
                    }
                }
                if (num.Length > 0)
                {
                    if (!TryParseDouble(num.ToString(), out double rest)) return false;
                    total += rest;
                }
                v = total;
                return true;
            }
            return TryParseDouble(s, out v);
        }

        /// <summary>Accepts 500 (KB/s), 500kb, 2mb, 2MB/s. Result in bytes/s (the unit the protocol uses).</summary>
        public static bool ParseSpeed(string s, out double v)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("/s")) s = s.Substring(0, s.Length - 2);
            if (s.EndsWith("ps")) s = s.Substring(0, s.Length - 2);
            double mult = 1024;
            if (s.EndsWith("mb") || s.EndsWith("m")) { mult = 1024 * 1024; s = s.TrimEnd('b').TrimEnd('m'); }
            else if (s.EndsWith("kb") || s.EndsWith("k")) { mult = 1024; s = s.TrimEnd('b').TrimEnd('k'); }
            else if (s.EndsWith("b")) { mult = 1; s = s.TrimEnd('b'); }
            if (!TryParseDouble(s, out v))
            {
                return false;
            }
            v *= mult;
            return true;
        }

        // ------------------------------------------------------------------ matching

        internal static string ExtensionOf(string filename)
        {
            int slash = Math.Max(filename.LastIndexOf('\\'), filename.LastIndexOf('/'));
            int dot = filename.LastIndexOf('.');
            if (dot <= slash || dot == filename.Length - 1)
            {
                return string.Empty;
            }
            return filename.Substring(dot + 1).ToLowerInvariant();
        }

        private static bool ContainsCI(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool AllMatch(List<NumericCondition> conditions, double? value)
        {
            if (conditions.Count == 0)
            {
                return true;
            }
            if (!value.HasValue)
            {
                return false; // unknown metadata never satisfies a quality filter
            }
            foreach (var c in conditions)
            {
                if (!c.Matches(value.Value))
                {
                    return false;
                }
            }
            return true;
        }

        public bool FileMatches(string username, File f)
        {
            string ext = ExtensionOf(f.Filename);
            if (Formats.Count > 0 && !Formats.Contains(ext))
            {
                return false;
            }
            if (ExcludedFormats.Count > 0 && ExcludedFormats.Contains(ext))
            {
                return false;
            }
            if (LosslessOnly && !LosslessExtensions.Contains(ext))
            {
                return false;
            }
            if (LossyOnly && (LosslessExtensions.Contains(ext) || !SimpleHelpersAudio.IsAudioExtension(ext)))
            {
                return false;
            }
            if (HiResOnly)
            {
                // Hi-Res = more than CD quality. Requires the peer to report depth or sample rate.
                bool hires = (f.BitDepth.HasValue && f.BitDepth.Value > 16) || (f.SampleRate.HasValue && f.SampleRate.Value > 48000);
                if (!hires)
                {
                    return false;
                }
            }
            if (!AllMatch(BitRate, f.BitRate) || !AllMatch(SampleRate, f.SampleRate) || !AllMatch(BitDepth, f.BitDepth) ||
                !AllMatch(Size, f.Size) || !AllMatch(Duration, f.Length))
            {
                return false;
            }
            if (Vbr.HasValue && f.IsVariableBitRate != Vbr.Value)
            {
                return false;
            }
            if (FileNameContains.Count > 0)
            {
                string name = SimpleHelpers.GetFileNameFromFile(f.Filename).ToString();
                foreach (var n in FileNameContains)
                {
                    if (!ContainsCI(name, n))
                    {
                        return false;
                    }
                }
            }
            if (PathRegex != null)
            {
                try
                {
                    if (!PathRegex.IsMatch(f.Filename))
                    {
                        return false;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }
            if (AlreadyDownloaded.HasValue && IsAlreadyDownloadedProvider != null)
            {
                if (IsAlreadyDownloadedProvider(username, f.Filename) != AlreadyDownloaded.Value)
                {
                    return false;
                }
            }
            // text predicates on the full path
            foreach (var w in Words)
            {
                if (!ContainsCI(f.Filename, w)) return false;
            }
            foreach (var w in PartialWords)
            {
                if (!ContainsCI(f.Filename, w)) return false;
            }
            foreach (var p in Phrases)
            {
                if (!ContainsCI(f.Filename, p)) return false;
            }
            foreach (var p in PathTerms)
            {
                if (!ContainsCI(f.Filename, p)) return false;
            }
            return true;
        }

        public bool ResponseMatches(SearchResponse s, bool hideLocked)
        {
            if (FreeSlot.HasValue && s.HasFreeUploadSlot != FreeSlot.Value)
            {
                return false;
            }
            if (!AllMatch(UploadSpeed, s.UploadSpeed) || !AllMatch(QueueLength, s.QueueLength))
            {
                return false;
            }
            if (FileCount.Count > 0 && !AllMatch(FileCount, hideLocked ? s.FileCount : s.FileCount + s.LockedFileCount))
            {
                return false;
            }
            if (Users.Count > 0 && !Users.Any(u => ContainsCI(s.Username, u)))
            {
                return false;
            }
            if (ExcludedUsers.Count > 0 && ExcludedUsers.Any(u => string.Equals(s.Username, u, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            if (FavoriteUser.HasValue && IsFavoriteUserProvider != null && IsFavoriteUserProvider(s.Username) != FavoriteUser.Value)
            {
                return false;
            }

            IEnumerable<File> files = hideLocked ? s.Files : s.Files.Concat(s.LockedFiles);

            // Exclusions apply to every file in the response (same semantics as Seeker's "-word").
            if (ExcludedWords.Count > 0 || ExcludedPhrases.Count > 0)
            {
                foreach (var f in files)
                {
                    foreach (var w in ExcludedWords)
                    {
                        if (ContainsCI(f.Filename, w)) return false;
                    }
                    foreach (var w in ExcludedPhrases)
                    {
                        if (ContainsCI(f.Filename, w)) return false;
                    }
                }
            }

            if (FolderContains.Count > 0)
            {
                File? first = files.FirstOrDefault();
                if (first == null)
                {
                    return false;
                }
                string folder = SimpleHelpers.GetDirectoryRequestFolderName(first.Filename);
                foreach (var fc in FolderContains)
                {
                    if (!ContainsCI(folder, fc)) return false;
                }
            }

            bool needsFile = HasFileLevelPredicates || Words.Count > 0 || PartialWords.Count > 0 || Phrases.Count > 0 || PathTerms.Count > 0;
            if (!needsFile)
            {
                return true;
            }
            foreach (var f in files)
            {
                if (FileMatches(s.Username, f))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Applies the file level predicates to a single response and returns only matching files,
        /// used to restrict a folder download to the files the user asked for (e.g. only .flac).
        /// </summary>
        public IEnumerable<File> MatchingFiles(SearchResponse s, bool hideLocked)
        {
            IEnumerable<File> files = hideLocked ? s.Files : s.Files.Concat(s.LockedFiles);
            return files.Where(f => FileMatches(s.Username, f));
        }

        /// <summary>Human readable description of the local filters (for chips / summaries).</summary>
        public List<string> Describe()
        {
            var parts = new List<string>();
            if (Formats.Count > 0) parts.Add("format: " + string.Join(", ", Formats));
            if (ExcludedFormats.Count > 0) parts.Add("not: " + string.Join(", ", ExcludedFormats));
            if (LosslessOnly) parts.Add("lossless");
            if (LossyOnly) parts.Add("lossy");
            if (HiResOnly) parts.Add("hi-res");
            foreach (var c in BitRate) parts.Add($"bitrate {c} kbps");
            foreach (var c in SampleRate) parts.Add($"sample rate {c} Hz");
            foreach (var c in BitDepth) parts.Add($"bits {c}");
            foreach (var c in Size) parts.Add($"size {c} B");
            foreach (var c in Duration) parts.Add($"duration {c} s");
            foreach (var c in UploadSpeed) parts.Add($"speed {c} B/s");
            foreach (var c in QueueLength) parts.Add($"queue {c}");
            foreach (var c in FileCount) parts.Add($"files {c}");
            if (FreeSlot.HasValue) parts.Add(FreeSlot.Value ? "free slot" : "no free slot");
            if (Vbr.HasValue) parts.Add(Vbr.Value ? "vbr" : "cbr");
            if (FavoriteUser.HasValue) parts.Add(FavoriteUser.Value ? "favorite users" : "non favorite users");
            if (AlreadyDownloaded.HasValue) parts.Add(AlreadyDownloaded.Value ? "already downloaded" : "not downloaded");
            foreach (var u in Users) parts.Add("user: " + u);
            foreach (var u in ExcludedUsers) parts.Add("not user: " + u);
            foreach (var p in Phrases) parts.Add("\"" + p + "\"");
            foreach (var p in ExcludedPhrases) parts.Add("-\"" + p + "\"");
            foreach (var p in PathTerms) parts.Add("path: " + p);
            foreach (var p in FolderContains) parts.Add("folder: " + p);
            foreach (var p in FileNameContains) parts.Add("file: " + p);
            if (PathRegex != null) parts.Add("regex: " + PathRegex);
            return parts;
        }
    }

    internal static class SimpleHelpersAudio
    {
        private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "flac", "wav", "aac", "m4a", "ogg", "oga", "opus", "alac", "wma", "aiff", "aif", "ape", "wv", "dsf", "dff", "mpc", "tta", "tak", "mp2", "amr"
        };

        public static bool IsAudioExtension(string ext) => AudioExtensions.Contains(ext);
    }
}
