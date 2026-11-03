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
}
