using System.Text;

namespace McPanel.Api.Infrastructure;

public static class JvmArgumentParser
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Any(char.IsControl)) throw PanelProblems.Validation("JVM arguments cannot contain control characters.");

        var values = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaping = false;
        foreach (var character in text)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }
            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0'; else current.Append(character);
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (char.IsWhiteSpace(character))
            {
                Add(values, current);
                continue;
            }
            current.Append(character);
        }
        if (escaping || quote != '\0') throw PanelProblems.Validation("JVM arguments contain an unmatched quote or escape.");
        Add(values, current);
        return values;
    }

    private static void Add(List<string> values, StringBuilder value)
    {
        if (value.Length == 0) return;
        var item = value.ToString();
        value.Clear();
        if (item.Equals("-jar", StringComparison.OrdinalIgnoreCase) || item.StartsWith("@", StringComparison.Ordinal) ||
            item.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) || item.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase))
            throw PanelProblems.Validation("-jar, argument files, and JVM memory flags are managed by MC Panel.");
        values.Add(item);
    }
}
