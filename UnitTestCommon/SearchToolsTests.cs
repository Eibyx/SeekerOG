using NUnit.Framework;
using Seeker;
using Soulseek;
using System.Collections.Generic;
using System.Linq;

namespace UnitTestCommon
{
    [TestFixture]
    public class SearchToolsTests
    {
        private static File F(string name, long size, int? bitrate = null)
        {
            var attrs = new List<FileAttribute>();
            if (bitrate.HasValue) attrs.Add(new FileAttribute(FileAttributeType.BitRate, bitrate.Value));
            return new File(1, name, size, "", attrs);
        }

        [Test]
        public void Profiles_RoundTrip()
        {
            var profiles = new List<FilterProfile> { new FilterProfile { Name = "Hi-Res", Filter = "format:flac bits:24" }, new FilterProfile { Name = "MP3 320", Filter = "format:mp3 bitrate:320" } };
            var back = SearchTools.DeserializeProfiles(SearchTools.SerializeProfiles(profiles));
            Assert.That(back.Select(p => p.Name), Is.EqualTo(new[] { "Hi-Res", "MP3 320" }));
            Assert.That(back[0].Filter, Is.EqualTo("format:flac bits:24"));
        }

        [Test]
        public void List_RoundTrip_HandlesEmpty()
        {
            Assert.That(SearchTools.DeserializeList(null), Is.Empty);
            var l = new List<string> { "daft punk", "artista:\"Air\" formato:flac" };
            Assert.That(SearchTools.DeserializeList(SearchTools.SerializeList(l)), Is.EqualTo(l));
        }

        [Test]
        public void RemoveDuplicates_KeepsFirstOfSameFolder()
        {
            var a = new SearchResponse("u1", 1, true, 100, 0, new[] { F(@"\\x\Artist\Album\01.flac", 100), F(@"\\x\Artist\Album\02.flac", 200) });
            var b = new SearchResponse("u2", 1, true, 100, 0, new[] { F(@"\\y\Music\Album\01.flac", 100), F(@"\\y\Music\Album\02.flac", 200) });
            var c = new SearchResponse("u3", 1, true, 100, 0, new[] { F(@"\\z\Album\01.flac", 100) });
            var result = SearchTools.RemoveDuplicates(new[] { a, b, c }, true);
            Assert.That(result.Select(r => r.Username), Is.EqualTo(new[] { "u1", "u3" }));
        }

        [Test]
        public void Csv_EscapesAndMarksUnknown()
        {
            var r = new SearchResponse("bob", 1, false, 2048, 4, new[] { F(@"\\x\A, B\01 ""Hit"".mp3", 1234, bitrate: 320) });
            string csv = SearchTools.ToCsv(new[] { r }, true);
            var lines = csv.Trim().Split('\n');
            Assert.That(lines.Length, Is.EqualTo(2));
            Assert.That(lines[1], Does.Contain("\"\\\\x\\A, B\\01 \"\"Hit\"\".mp3\""));
            Assert.That(lines[1], Does.Contain(",mp3,320,unknown,unknown,unknown,unknown,no"));
        }

        [Test]
        public void BuildFilter_ProducesParsableSyntax()
        {
            string f = SearchTools.BuildFilter(new[] { "FLAC", "alac" }, false, true, 0, 0, 96000, 24, 0, 500, 0, 0, true, 10, 100, "live, remix", "");
            var q = AdvancedQuery.Parse(f);
            Assert.That(q.Warnings, Is.Empty, f);
            Assert.That(q.Formats, Does.Contain("flac"));
            Assert.That(q.HiResOnly, Is.True);
            Assert.That(q.FreeSlot, Is.True);
            Assert.That(q.ExcludedWords, Is.EquivalentTo(new[] { "live", "remix" }));
            Assert.That(q.Size.Single().Op, Is.EqualTo(AdvancedQuery.Op.Le));
        }

        [Test]
        public void Favorites_Toggle()
        {
            SearchTools.FavoriteSearches.Clear();
            SearchTools.ToggleFavorite("aphex twin");
            Assert.That(SearchTools.IsFavorite("Aphex Twin"), Is.True);
            SearchTools.ToggleFavorite("APHEX TWIN");
            Assert.That(SearchTools.IsFavorite("aphex twin"), Is.False);
        }
    }
}
