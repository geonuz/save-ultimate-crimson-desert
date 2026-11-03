using System.IO;

namespace SaveUltimate.Tests;

// SAVEULTIMATE_FIXTURES names the test_fixture folder: saves as they come under collected/, saves a test names under scenarios/.
internal static class FixtureSaves
{
    internal static string? Root => Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
    internal static string? Scenarios => Root is null ? null : Path.Combine(Root, "scenarios");
}
