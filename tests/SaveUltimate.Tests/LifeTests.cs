using System.Buffers.Binary;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class LifeTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void BusinessCatalogNamesEveryRegionInEveryLanguage()
    {
        var catalog = ManagementCatalog.Shared;
        Assert.AreEqual(18, catalog.Shops.Length);
        Assert.IsTrue(catalog.Shops.All(s => s.Kind != "festival" && s.MaxLevel == 5));
        foreach (var shop in catalog.Shops)
            foreach (string language in new[] { "ko", "en", "ja", "ru" })
                Assert.IsTrue(catalog.Regions[shop.Region].ContainsKey(language), $"{shop.Key} {language}");
        Assert.AreEqual("에르난드", catalog.RegionName("Her", "ko"));
    }
}
