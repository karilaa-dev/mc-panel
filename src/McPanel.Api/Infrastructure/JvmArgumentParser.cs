using System.Text;

namespace McPanel.Api.Infrastructure;

public static class JvmArgumentParser
{
    private enum Mode { Unquoted, SingleQuoted, DoubleQuoted, EscapeUnquoted, EscapeDoubleQuoted }

    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Any(char.IsControl)) throw PanelProblems.Validation("JVM arguments cannot contain control characters.");

        var values = new List<string>();
        var current = new StringBuilder();
        var mode = Mode.Unquoted;
        foreach (var character in text)
        {
            switch (mode)
            {
                case Mode.EscapeUnquoted:
                case Mode.EscapeDoubleQuoted:
                    current.Append(character);
                    mode = mode == Mode.EscapeDoubleQuoted ? Mode.DoubleQuoted : Mode.Unquoted;
                    break;
                case Mode.SingleQuoted:
                    if (character == '\'') mode = Mode.Unquoted;
                    else current.Append(character);
                    break;
                case Mode.DoubleQuoted:
                    if (character == '"') mode = Mode.Unquoted;
                    else if (character == '\\') mode = Mode.EscapeDoubleQuoted;
                    else current.Append(character);
                    break;
                case Mode.Unquoted:
                    if (character == '\'') mode = Mode.SingleQuoted;
                    else if (character == '"') mode = Mode.DoubleQuoted;
                    else if (character == '\\') mode = Mode.EscapeUnquoted;
                    else if (char.IsWhiteSpace(character)) Add(values, current);
                    else current.Append(character);
                    break;
            }
        }
        if (mode != Mode.Unquoted) throw PanelProblems.Validation("JVM arguments contain an unmatched quote or escape.");
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
