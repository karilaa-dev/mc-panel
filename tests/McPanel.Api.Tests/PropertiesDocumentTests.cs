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

    [Fact]
    public void Entries_returns_effective_keys_in_file_order_and_uses_the_last_duplicate()
    {
        var document = PropertiesDocument.Parse("# generated\nmotd=first\nunknown-option=keep\nMOTD=effective\nwhite-list=true\n");

        KeyValuePair<string, string>[] expected =
        [
            new("unknown-option", "keep"),
            new("MOTD", "effective"),
            new("white-list", "true")
        ];
        Assert.Equal(expected, document.Entries());

        document.Set("motd", "updated");
        Assert.Contains("motd=first", document.ToString());
        Assert.Contains("MOTD=updated", document.ToString());
    }
}
