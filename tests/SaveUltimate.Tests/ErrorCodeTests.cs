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
        StringAssert.Contains(ErrorCodes.Classify(new InvalidOperationException("Select a skill.")).Message, "스킬을 선택하세요.");
    }

    [TestMethod]
    [TestCategory("Unit")]
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
            // A DLL whose imported library is absent fails with 126 although it exists, as 1.1.2's libwinpthread-1.dll import did.
            string validation = Path.Combine(directory, "parc_validation.dll");
            byte[] image = File.ReadAllBytes(validation);
            byte[] from = "msvcrt.dll\0"u8.ToArray(), to = "msvcrx.dll\0"u8.ToArray();
            // The name also appears outside the import table, so rename every occurrence.
            int renamed = 0;
            for (int at; (at = image.AsSpan().IndexOf(from)) >= 0; renamed++) to.CopyTo(image, at);
            Assert.IsTrue(renamed > 0);
            File.WriteAllBytes(validation, image);
            var dependency = ErrorCodes.DiagnoseNative(directory);
            Assert.AreEqual("SU-106", dependency.Code);
            StringAssert.Contains(dependency.Message, "msvcrx.dll");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void NativeLibrariesNeedOnlyWindowsLibraries()
    {
        // A MinGW runtime import such as libwinpthread-1.dll is absent on PCs without MinGW, so saves would not open there.
        string[] system = ["kernel32.dll", "msvcrt.dll", "bcrypt.dll", "ucrtbase.dll"];
        foreach (string name in new[] { "parc_validation.dll", "parc_index.dll", "parc_reader.dll" })
        {
            var imports = ErrorCodes.NativeImports(Path.Combine(AppContext.BaseDirectory, name));
            CollectionAssert.Contains(imports.Select(i => i.ToLowerInvariant()).ToList(), "kernel32.dll", name);
            var foreign = imports.Where(i => !system.Contains(i.ToLowerInvariant()) && !i.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(0, foreign.Length, $"{name} imports {string.Join(", ", foreign)}");
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EveryCodeHasAMessageInEveryLanguage()
    {
        string Locale(string language) => Path.Combine(AppContext.BaseDirectory, "resources", "locale", language + ".json");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(Locale("en")))!
            .Keys.Where(k => k.StartsWith("error.SU-", StringComparison.Ordinal)).Order().ToArray();
        Assert.AreEqual(18, english.Length);
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
