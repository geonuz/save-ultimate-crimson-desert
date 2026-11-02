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
    }

    [TestMethod]
    public void NativeLoadFailuresNameTheFileAndReason()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var missing = ErrorCodes.DiagnoseNative(directory);
            Assert.AreEqual("SU-101", missing.Code);
            StringAssert.Contains(missing.Message, "parc_validation.dll");
            File.WriteAllText(Path.Combine(directory, "parc_validation.dll"), "not a library");
            var damaged = ErrorCodes.DiagnoseNative(directory);
            Assert.AreEqual("SU-105", damaged.Code);
            StringAssert.Contains(damaged.Message, "193");
            // With the real libraries laid out as in a release, all load and the probe finds nothing to blame.
            foreach (string name in new[] { "parc_validation.dll", "parc_reader.dll", "parc_index.dll" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(directory, name), true);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "Magick.Native-Q8-x64.dll"), Path.Combine(directory, "Magick.Native-Q8-x64.dll"));
            var loaded = ErrorCodes.DiagnoseNative(directory);
            Assert.AreEqual("SU-109", loaded.Code);
            StringAssert.Contains(loaded.Message, "?");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void EveryCodeHasAMessageInEveryLanguage()
    {
        string Locale(string language) => Path.Combine(AppContext.BaseDirectory, "resources", "locale", language + ".json");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Locale("en")))!
            .Keys.Where(k => k.StartsWith("error.SU-", StringComparison.Ordinal)).Order().ToArray();
        Assert.AreEqual(17, english.Length);
        foreach (string language in new[] { "ko", "ja", "ru" })
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Locale(language)))!
                .Keys.Where(k => k.StartsWith("error.SU-", StringComparison.Ordinal)).Order().ToArray();
            CollectionAssert.AreEqual(english, keys, language);
        }
    }

    private static Exception Thrown(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new AssertFailedException("Expected an exception.");
    }
}
