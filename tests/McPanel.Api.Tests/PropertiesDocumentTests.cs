using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class PropertiesDocumentTests
{
    [Fact]
    public void Set_preserves_comments_unknown_values_and_order()
    {
        var document = PropertiesDocument.Parse("# generated\nplugin-setting=keep\nmotd=Old\n");
        document.Set("motd", "New server");
        document.Set("server-port", "25566");

        var result = document.ToString();
        Assert.Contains("# generated", result);
        Assert.Contains("plugin-setting=keep", result);
        Assert.Contains("motd=New server", result);
        Assert.EndsWith($"server-port=25566{Environment.NewLine}", result);
    }

    [Fact]
    public void Set_rejects_line_injection()
    {
        var document = PropertiesDocument.Empty();
        Assert.ThrowsAny<Exception>(() => document.Set("motd", "safe\nop=true"));
    }
}
