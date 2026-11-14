using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ErrorCodeTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void ExceptionsMapToStableCodes()
    {
        Assert.AreEqual("SU-201", ErrorCodes.Classify(new FileNotFoundException("x")).Code);
        Assert.AreEqual("SU-202", ErrorCodes.Classify(new UnauthorizedAccessException("x")).Code);
        Assert.AreEqual("SU-203", ErrorCodes.Classify(new IOException("x", unchecked((int)0x80070020))).Code);
        Assert.AreEqual("SU-204", ErrorCodes.Classify(new IOException("x", unchecked((int)0x80070070))).Code);
        Assert.AreEqual("SU-205", ErrorCodes.Classify(new PathTooLongException("x")).Code);
        Assert.AreEqual("SU-209", ErrorCodes.Classify(new IOException("x")).Code);
        Assert.AreEqual("SU-401", ErrorCodes.Classify(new OutOfMemoryException()).Code);
        Assert.AreEqual("SU-901", ErrorCodes.Classify(new FormatException("x")).Code);
        // InvalidOperationException from the runtime, not from SaveUltimate code, is unexpected rather than a refused edit.
        Assert.AreEqual("SU-901", ErrorCodes.Classify(Thrown(() => new List<int>().First())).Code);
        var save = Thrown(() => SaveContainer.Decode(new byte[200]));
        Assert.AreEqual("SU-301", ErrorCodes.Classify(save).Code);
        StringAssert.Contains(ErrorCodes.Classify(save).Message, save.Message);
        // Tests run in Korean; an English Core message with a locale entry is shown translated.
        StringAssert.Contains(ErrorCodes.Classify(new InvalidOperationException("This save has no free number for a new record.")).Message, "이 세이브에는 새 레코드에 쓸 빈 번호가 없습니다.");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EveryCodeHasAMessageInEveryLanguage()
    {
        string Locale(string language) => Path.Combine(AppContext.BaseDirectory, "resources", "locale", language + ".json");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Locale("en")))!
            .Keys.Where(k => k.StartsWith("error.SU-", StringComparison.Ordinal)).Order().ToArray();
        Assert.AreEqual(11, english.Length);
        foreach (string language in new[] { "ko", "ja", "ru" })
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Locale(language)))!
                .Keys.Where(k => k.StartsWith("error.SU-", StringComparison.Ordinal)).Order().ToArray();
            CollectionAssert.AreEqual(english, keys, language);
        }
    }

    // A cache that parses but breaks a rule (here a socket limit of 9) once stopped the app before its window opened.
    [TestMethod]
    [TestCategory("Unit")]
    [DoNotParallelize]
    public void ABrokenCacheFallsBackToTheShippedDataAtStartup()
    {
        string cache = CatalogFiles.Cache, log = ErrorCodes.LogPath;
        Assert.IsFalse(Directory.Exists(cache), "The test output folder holds no synced data.");
        ErrorCodes.LogPath = Path.Combine(Path.GetTempPath(), $"SaveUltimate-{Guid.NewGuid():N}.log");
        try
        {
            Directory.CreateDirectory(Path.Combine(cache, "data"));
            File.WriteAllText(Path.Combine(cache, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(cache, "data", "item_names.json"), """{"items":[{"itemKey":1,"socketLimit":9}]}""");
            File.WriteAllText(Path.Combine(cache, "data", "max_enchant_map.json"), "{}");
            StringAssert.StartsWith(CatalogFiles.TryReload(), "[SU-302]");
            Assert.IsFalse(CatalogFiles.Synced);
        }
        finally
        {
            Directory.Delete(cache, true);
            if (File.Exists(ErrorCodes.LogPath)) File.Delete(ErrorCodes.LogPath);
            ErrorCodes.LogPath = log;
            CatalogFiles.Reload();
        }
    }

    private static Exception Thrown(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new AssertFailedException("Expected an exception.");
    }
}
