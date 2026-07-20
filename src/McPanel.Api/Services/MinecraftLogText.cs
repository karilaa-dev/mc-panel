using System.Text;
using System.Text.RegularExpressions;

namespace McPanel.Api.Services;

public static partial class MinecraftLogText
{
    public static string SanitizeForParsing(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var code = value[index];
            if (code == '\u001b')
            {
                if (index + 1 >= value.Length) continue;
                var introducer = value[++index];
                if (introducer == '[')
                {
                    while (index + 1 < value.Length && value[index + 1] is not (>= '@' and <= '~')) index++;
                    if (index + 1 < value.Length) index++;
                    continue;
                }
                if (introducer is ']' or 'P' or 'X' or '^' or '_')
                {
                    while (index + 1 < value.Length)
                    {
                        index++;
                        if (value[index] == '\a') break;
                        if (value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\') { index++; break; }
                    }
                }
                continue;
            }
            if (code == '\u009b')
            {
                while (index + 1 < value.Length && value[index + 1] is not (>= '@' and <= '~')) index++;
                if (index + 1 < value.Length) index++;
                continue;
            }
            if (code == '\u009d')
            {
                while (index + 1 < value.Length && value[++index] != '\a') { }
                continue;
            }
            if (char.IsControl(code) && code != '\t') continue;
            result.Append(code);
        }
        return result.ToString();
    }

    public static bool IsLegacyAnsiLeakOf(string candidate, string canonical)
    {
        if (candidate.Length <= canonical.Length || !candidate.EndsWith(canonical, StringComparison.OrdinalIgnoreCase)) return false;
        return AnsiLeakPrefixRegex().IsMatch(candidate[..^canonical.Length]);
    }

    [GeneratedRegex(@"^\d{1,3}m$")]
    private static partial Regex AnsiLeakPrefixRegex();
}
