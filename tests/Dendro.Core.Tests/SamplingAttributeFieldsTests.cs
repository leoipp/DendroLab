using Dendro.Core.Sampling;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace Dendro.Core.Tests;

public sealed class SamplingAttributeFieldsTests
{
    [Fact]
    public void ReadsTheSelectedShapefilesActualAttributeNames()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dendro-attributes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string first = Path.Combine(folder, "first.shp"), second = Path.Combine(folder, "second.shp");
            Write(first, "TALHAO", "FAZENDA");
            Write(second, "CODIGO", "PROJETO");
            Assert.Equal(new[] { "TALHAO", "FAZENDA" }, ForestSampler.ReadAttributeFields(first));
            Assert.Equal(new[] { "CODIGO", "PROJETO" }, ForestSampler.ReadAttributeFields(second));
            File.Delete(Path.ChangeExtension(second, ".dbf"));
            Assert.Throws<FileNotFoundException>(() => ForestSampler.ReadAttributeFields(second));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Write(string path, string id, string project)
    {
        var attributes = new AttributesTable { { id, "T01" }, { project, "P01" } };
        Shapefile.WriteAllFeatures(new[] { new Feature(new Point(500000, 7500000), attributes) }, path);
    }
}
