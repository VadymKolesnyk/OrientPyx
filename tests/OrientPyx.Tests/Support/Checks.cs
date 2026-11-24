using System.Text;
using Xunit;

namespace OrientPyx.Tests.Support;

public static class Checks
{
    /// <summary>A Word document is a zip («PK») of a sensible size.</summary>
    public static void IsDocx(byte[] bytes)
    {
        Assert.True(bytes.Length > 1000, $"docx too small: {bytes.Length} bytes");
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    public static void IsHtml(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("<html", text, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Lazy<System.Xml.Schema.XmlSchemaSet> IofSchema = new(() =>
    {
        var set = new System.Xml.Schema.XmlSchemaSet();
        set.Add(OrientPyx.BusinessLogic.Models.IofDataStandard.Namespace,
            Path.Combine(AppContext.BaseDirectory, "Iof", "IOF.xsd"));
        set.Compile();
        return set;
    });

    /// <summary>Plain IOF export options for the sweeps (every group, birth dates, a fixed header).</summary>
    public static OrientPyx.BusinessLogic.Models.IofExportOptions IofOptions => new(
        null, OrientPyx.BusinessLogic.Models.IofOrganisationSource.Club, true, "Test", "OrientPyx test",
        new DateTimeOffset(2026, 6, 6, 20, 0, 0, TimeSpan.FromHours(3)));

    /// <summary>Builds and writes both IOF lists of the days and checks each against the schema.</summary>
    public static async Task IofListsAreValidAsync(IServiceProvider sp, params Guid[] dayIds)
    {
        var editor = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<OrientPyx.BusinessLogic.Interfaces.ICompetitionEditorService>(sp);
        var builder = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<OrientPyx.BusinessLogic.Interfaces.IIofExportBuilder>(sp);
        var writer = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<OrientPyx.BusinessLogic.Interfaces.IIofXmlWriter>(sp);
        var results = await editor.GetIofExportDataAsync(dayIds, OrientPyx.BusinessLogic.Models.IofListKind.ResultList);
        IsIofXml(writer.Write(builder.BuildResultList(results, IofOptions)));
        var starts = await editor.GetIofExportDataAsync(dayIds, OrientPyx.BusinessLogic.Models.IofListKind.StartList);
        IsIofXml(writer.Write(builder.BuildStartList(starts, IofOptions)));
    }

    /// <summary>An IOF XML file valid against the official IOF Data Standard 3.0 schema (<c>Iof/IOF.xsd</c>):
    /// UTF-8 without a BOM, every schema error listed in the failure.</summary>
    public static void IsIofXml(byte[] bytes)
    {
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "IOF XML has a BOM");
        var doc = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(bytes));
        var errors = new List<string>();
        System.Xml.Schema.Extensions.Validate(doc, IofSchema.Value, (_, e) => errors.Add($"{e.Severity}: {e.Message}"));
        Assert.True(errors.Count == 0, "IOF XML is not valid:\n" + string.Join("\n", errors.Take(20)));
    }
}
