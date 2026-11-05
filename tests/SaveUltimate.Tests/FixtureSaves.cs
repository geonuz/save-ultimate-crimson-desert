using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// WPF windows and DlcContent.Hidden are process-wide, so tests using them carry [DoNotParallelize].
[assembly: Parallelize(Workers = 12, Scope = ExecutionScope.ClassLevel)]

namespace SaveUltimate.Tests;

// SAVEULTIMATE_FIXTURES names the test_fixture folder. Saves are <owner>/<game version>_<stage>_<purpose>.save;
// tests name the purpose, so a newer save can replace an old one without touching them.
internal static class FixtureSaves
{
    internal static string? Root => Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
    internal static string Named(string purpose)
        => Directory.GetFiles(Root ?? throw new AssertInconclusiveException("Set SAVEULTIMATE_FIXTURES."), $"*_{purpose}.save", SearchOption.AllDirectories).Single();
}
