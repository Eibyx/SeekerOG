using NUnit.Framework;
using Seeker;
using Soulseek;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace UnitTestCommon
{
    [TestFixture]
    public class AdvancedQueryTests
    {
        private static File F(string name, long size = 10 * 1024 * 1024, int? bitrate = null, int? length = null, int? sampleRate = null, int? bitDepth = null, bool? vbr = null)
        {
            var attrs = new List<FileAttribute>();
            if (bitrate.HasValue) attrs.Add(new FileAttribute(FileAttributeType.BitRate, bitrate.Value));
            if (length.HasValue) attrs.Add(new FileAttribute(FileAttributeType.Length, length.Value));
            if (sampleRate.HasValue) attrs.Add(new FileAttribute(FileAttributeType.SampleRate, sampleRate.Value));
            if (bitDepth.HasValue) attrs.Add(new FileAttribute(FileAttributeType.BitDepth, bitDepth.Value));
            if (vbr.HasValue) attrs.Add(new FileAttribute(FileAttributeType.VariableBitRate, vbr.Value ? 1 : 0));
            return new File(1, name, size, "", attrs);
        }

        private static SearchResponse R(params File[] files) => new SearchResponse("alice", 1, true, 500 * 1024, 3, files);
        private static SearchResponse RU(string user, bool free, int speed, int queue, params File[] files) => new SearchResponse(user, 1, free, speed, queue, files);

        [Test]
        public void ServerQuery_KeepsOnlyWordsAndExclusions()
        {
            var q = AdvancedQuery.Parse("daft punk formato:flac bits:24 frecuencia:96000 -live");
            Assert.That(q.ServerQuery, Is.EqualTo("daft punk -live"));
            Assert.That(q.Formats, Is.EquivalentTo(new[] { "flac" }));
            Assert.That(q.BitDepth.Single().Value, Is.EqualTo(24));
            Assert.That(q.SampleRate.Single().Value, Is.EqualTo(96000));
            Assert.That(q.ExcludedWords, Is.EquivalentTo(new[] { "live" }));
            Assert.That(q.HasLocalFilters, Is.True);
            Assert.That(q.Warnings, Is.Empty);
        }

        [Test]
        public void QuotedArtist_IsPhraseAndServerWords()
        {
            var q = AdvancedQuery.Parse("artista:\"Daft Punk\" formato:flac");
            Assert.That(q.ServerQuery, Is.EqualTo("Daft Punk"));
            Assert.That(q.PathTerms, Is.EquivalentTo(new[] { "Daft Punk" }));
            var good = R(F(@"\\music\Daft Punk\Discovery\01 One More Time.flac"));
            var wrongFormat = R(F(@"\\music\Daft Punk\Discovery\01 One More Time.mp3"));
            var wrongArtist = R(F(@"\\music\Daft\Punk Discovery\01.flac"));
            Assert.That(q.ResponseMatches(good, true), Is.True);
            Assert.That(q.ResponseMatches(wrongFormat, true), Is.False);
            Assert.That(q.ResponseMatches(wrongArtist, true), Is.False);
        }

        [Test]
        public void ExtensionBitrateAndSizeComparison()
        {
            var q = AdvancedQuery.Parse("extension:mp3 bitrate:320 tamaño<100MB");
            Assert.That(q.ServerQuery, Is.EqualTo(string.Empty));
            Assert.That(q.ResponseMatches(R(F(@"\\a\b\song.mp3", 9 * 1024 * 1024, bitrate: 320)), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\a\b\song.mp3", 9 * 1024 * 1024, bitrate: 256)), true), Is.False);
            Assert.That(q.ResponseMatches(R(F(@"\\a\b\song.mp3", 200L * 1024 * 1024, bitrate: 320)), true), Is.False);
            // unknown bitrate never satisfies a bitrate filter
            Assert.That(q.ResponseMatches(R(F(@"\\a\b\song.mp3", 9 * 1024 * 1024)), true), Is.False);
        }

        [TestCase("44.1", 44100)]
        [TestCase("44.1k", 44100)]
        [TestCase("96khz", 96000)]
        [TestCase("176400", 176400)]
        [TestCase("192", 192000)]
        [TestCase("88,2", 88200)]
        public void SampleRateParsing(string text, double expected)
        {
            Assert.That(AdvancedQuery.ParseSampleRate(text, out double v), Is.True);
            Assert.That(v, Is.EqualTo(expected));
        }

        [TestCase("100", 100.0 * 1024 * 1024)]
        [TestCase("100MB", 100.0 * 1024 * 1024)]
        [TestCase("1.5GB", 1.5 * 1024 * 1024 * 1024)]
        [TestCase("700k", 700.0 * 1024)]
        [TestCase("12b", 12.0)]
        public void SizeParsing(string text, double expected)
        {
            Assert.That(AdvancedQuery.ParseSize(text, out double v), Is.True);
            Assert.That(v, Is.EqualTo(expected).Within(0.5));
        }

        [TestCase("240", 240)]
        [TestCase("4:00", 240)]
        [TestCase("1:02:03", 3723)]
        [TestCase("3m30s", 210)]
        [TestCase("1h", 3600)]
        [TestCase("45s", 45)]
        public void DurationParsing(string text, double expected)
        {
            Assert.That(AdvancedQuery.ParseDuration(text, out double v), Is.True);
            Assert.That(v, Is.EqualTo(expected));
        }

        [Test]
        public void DurationWithTolerance()
        {
            var q = AdvancedQuery.Parse("dur:3:30~5");
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.flac", length: 213)), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.flac", length: 220)), true), Is.False);
            var range = AdvancedQuery.Parse("duracion:3:00-4:00");
            Assert.That(range.ResponseMatches(R(F(@"\\a\x.flac", length: 200)), true), Is.True);
            Assert.That(range.ResponseMatches(R(F(@"\\a\x.flac", length: 260)), true), Is.False);
        }

        [Test]
        public void MultipleFormatsAreOred()
        {
            var q = AdvancedQuery.Parse("format:flac,mp3");
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.flac")), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.mp3")), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.ogg")), true), Is.False);
        }

        [Test]
        public void FormatGroups_DsdAndAlac()
        {
            var q = AdvancedQuery.Parse("format:dsd");
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.dsf")), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.dff")), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\a\x.flac")), true), Is.False);
        }

        [Test]
        public void LosslessAndHiResFlags()
        {
            var lossless = AdvancedQuery.Parse("lossless");
            Assert.That(lossless.ResponseMatches(R(F(@"\\a\x.flac")), true), Is.True);
            Assert.That(lossless.ResponseMatches(R(F(@"\\a\x.mp3")), true), Is.False);

            var hires = AdvancedQuery.Parse("hires");
            Assert.That(hires.ResponseMatches(R(F(@"\\a\x.flac", sampleRate: 96000, bitDepth: 24)), true), Is.True);
            Assert.That(hires.ResponseMatches(R(F(@"\\a\x.flac", sampleRate: 44100, bitDepth: 16)), true), Is.False);
            // unknown => not hi-res (never invent metadata)
            Assert.That(hires.ResponseMatches(R(F(@"\\a\x.flac")), true), Is.False);
        }

        [Test]
        public void AvailabilityFilters()
        {
            var q = AdvancedQuery.Parse("free:yes queue<=5 speed>=100kb");
            Assert.That(q.ResponseMatches(RU("bob", true, 200 * 1024, 2, F(@"\\a\x.mp3")), true), Is.True);
            Assert.That(q.ResponseMatches(RU("bob", false, 200 * 1024, 2, F(@"\\a\x.mp3")), true), Is.False);
            Assert.That(q.ResponseMatches(RU("bob", true, 200 * 1024, 20, F(@"\\a\x.mp3")), true), Is.False);
            Assert.That(q.ResponseMatches(RU("bob", true, 50 * 1024, 2, F(@"\\a\x.mp3")), true), Is.False);
        }

        [Test]
        public void UserFolderAndFileFilters()
        {
            var q = AdvancedQuery.Parse("user:bob carpeta:Discovery archivo:One");
            var good = RU("bob", true, 1, 0, F(@"\\music\Daft Punk\Discovery\01 One More Time.flac"));
            Assert.That(q.ResponseMatches(good, true), Is.True);
            Assert.That(q.ResponseMatches(RU("carol", true, 1, 0, F(@"\\music\Daft Punk\Discovery\01 One More Time.flac")), true), Is.False);
            Assert.That(q.ResponseMatches(RU("bob", true, 1, 0, F(@"\\music\Daft Punk\Homework\01 One.flac")), true), Is.False);
        }

        [Test]
        public void PhrasesAndExclusions()
        {
            var q = AdvancedQuery.Parse("\"one more time\" -live -\"radio edit\"");
            Assert.That(q.ServerQuery, Is.EqualTo("one more time -live"));
            Assert.That(q.ResponseMatches(R(F(@"\\m\One More Time.flac")), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\m\One More Time (Live).flac")), true), Is.False);
            Assert.That(q.ResponseMatches(R(F(@"\\m\One More Time (Radio Edit).flac")), true), Is.False);
            Assert.That(q.ResponseMatches(R(F(@"\\m\Time for one more.flac")), true), Is.False);
        }

        [Test]
        public void Regex_WithSpacesAndFlags()
        {
            var q = AdvancedQuery.Parse("re:/\\(19[89]\\d\\) remaster/");
            Assert.That(q.PathRegex, Is.Not.Null);
            Assert.That(q.ResponseMatches(R(F(@"\\m\Album (1985) Remaster\01.flac")), true), Is.True);
            Assert.That(q.ResponseMatches(R(F(@"\\m\Album (2005) Remaster\01.flac")), true), Is.False);
        }

        [Test]
        public void Regex_InvalidIsReportedNotThrown()
        {
            var q = AdvancedQuery.Parse("re:/([a-z/");
            Assert.That(q.PathRegex, Is.Null);
            Assert.That(q.Warnings, Is.Not.Empty);
        }

        [Test]
        public void Regex_CatastrophicBacktrackingIsBounded()
        {
            var q = AdvancedQuery.Parse("re:/^(a+)+$/");
            var evil = new string('a', 5000) + "!";
            var sw = Stopwatch.StartNew();
            bool result = q.ResponseMatches(R(F(@"\\m\" + evil + ".flac")), true);
            sw.Stop();
            Assert.That(result, Is.False);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000));
        }

        [Test]
        public void ComparisonOperatorVariants()
        {
            Assert.That(AdvancedQuery.Parse("bitrate>=256").BitRate.Single().Op, Is.EqualTo(AdvancedQuery.Op.Ge));
            Assert.That(AdvancedQuery.Parse("bitrate:>256").BitRate.Single().Op, Is.EqualTo(AdvancedQuery.Op.Gt));
            Assert.That(AdvancedQuery.Parse("bitrate:<=256").BitRate.Single().Op, Is.EqualTo(AdvancedQuery.Op.Le));
            Assert.That(AdvancedQuery.Parse("bitrate=320").BitRate.Single().Op, Is.EqualTo(AdvancedQuery.Op.Eq));
            var range = AdvancedQuery.Parse("bitrate:192-256");
            Assert.That(range.BitRate.Count, Is.EqualTo(2));
            Assert.That(range.ResponseMatches(R(F(@"\\a\x.mp3", bitrate: 224)), true), Is.True);
            Assert.That(range.ResponseMatches(R(F(@"\\a\x.mp3", bitrate: 320)), true), Is.False);
        }

        [Test]
        public void LegacySyntaxStillParsed()
        {
            var q = AdvancedQuery.Parse("mbr:256 isvbr");
            Assert.That(q.BitRate.Single().Value, Is.EqualTo(256));
            Assert.That(q.Vbr, Is.True);
        }

        [Test]
        public void UnknownKeyIsPlainWord()
        {
            var q = AdvancedQuery.Parse("track:05 beatles");
            Assert.That(q.ServerQuery, Is.EqualTo("track:05 beatles"));
            Assert.That(q.HasLocalFilters, Is.False);
        }

        [Test]
        public void BadValueProducesWarning()
        {
            var q = AdvancedQuery.Parse("bitrate:abc");
            Assert.That(q.Warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void PlainWordsOnly_HasNoLocalFilters()
        {
            var q = AdvancedQuery.Parse("aphex twin -remix");
            Assert.That(q.HasLocalFilters, Is.False);
            Assert.That(q.ServerQuery, Is.EqualTo("aphex twin -remix"));
        }

        [Test]
        public void AnyFileMatchingIsEnoughForFolderResponse()
        {
            var q = AdvancedQuery.Parse("format:flac bits:24");
            var folder = R(F(@"\\m\a\cover.jpg"), F(@"\\m\a\01.flac", bitDepth: 24, sampleRate: 96000));
            Assert.That(q.ResponseMatches(folder, true), Is.True);
            Assert.That(q.MatchingFiles(folder, true).Count(), Is.EqualTo(1));
        }

        [Test]
        public void TextFilter_UsesAdvancedWhenSyntaxPresent()
        {
            var tf = new TextFilter(supportsSpecialFlags: true);
            tf.Set("format:flac -live");
            Assert.That(tf.Advanced, Is.Not.Null);
            Assert.That(tf.WordsToInclude, Is.Empty);

            tf.Set("beatles -live");
            Assert.That(tf.Advanced, Is.Null);
            Assert.That(tf.WordsToInclude, Is.EquivalentTo(new[] { "beatles" }));
            Assert.That(tf.WordsToAvoid, Is.EquivalentTo(new[] { "live" }));
        }

        [Test]
        public void ManyResponses_FilterQuickly()
        {
            var q = AdvancedQuery.Parse("format:flac bitrate>=800 size<100MB -live re:/disc ?\\d/");
            var responses = new List<SearchResponse>();
            for (int i = 0; i < 20000; i++)
            {
                responses.Add(RU("u" + i, i % 2 == 0, 1000 * i, i % 7,
                    F($@"\\music\Artist {i}\Album {i} Disc {i % 3}\01 Track.flac", 30L * 1024 * 1024, bitrate: 700 + (i % 300)),
                    F($@"\\music\Artist {i}\Album {i} Disc {i % 3}\cover.jpg", 100000)));
            }
            var sw = Stopwatch.StartNew();
            int count = responses.Count(r => q.ResponseMatches(r, true));
            sw.Stop();
            Assert.That(count, Is.GreaterThan(0));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(3000));
        }
    }
}
