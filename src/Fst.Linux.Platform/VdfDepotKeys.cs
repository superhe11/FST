using System.Text;
using System.Text.RegularExpressions;

namespace Fst.App;

// Edit only existing DecryptionKey values. The upstream CLI still creates
// missing depot entries from the Lua after FST's conflict policy is applied.
internal static class VdfDepotKeys
{
    internal static string ApplyOverrides(string text, IReadOnlyDictionary<string, string> keys)
    {
        var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]|[^\\s{}\"]+")
            .Where(m => !m.Value.StartsWith("//", StringComparison.Ordinal)).ToArray();
        var edits = new List<(int Start, int Length, string Key)>();
        var path = new List<string>();
        var index = 0;
        void Parse(bool nested)
        {
            if (path.Count > 128)
                throw new InvalidDataException("VDF nesting limit exceeded.");
            while (index < tokens.Length)
            {
                var name = tokens[index++].Value;
                if (name == "}")
                {
                    if (!nested)
                        throw new InvalidDataException("Unexpected VDF closing brace.");
                    return;
                }
                // Valve's optional platform conditions follow a value/block.
                if (name.StartsWith('[') && name.EndsWith(']'))
                    continue;
                if (name == "{" || index == tokens.Length)
                    throw new InvalidDataException("Invalid VDF key/value.");
                name = name.Trim('"');
                var value = tokens[index++];
                if (value.Value == "{")
                {
                    path.Add(name);
                    Parse(true);
                    path.RemoveAt(path.Count - 1);
                }
                else
                {
                    if (value.Value == "}")
                        throw new InvalidDataException("Missing VDF value.");
                    if (name.Equals("DecryptionKey", StringComparison.OrdinalIgnoreCase) && path.Count == 6 &&
                        path.Take(5).SequenceEqual(new[] { "InstallConfigStore", "Software", "Valve", "Steam", "depots" }, StringComparer.OrdinalIgnoreCase) &&
                        keys.TryGetValue(path[5], out var key))
                        edits.Add((value.Index, value.Length, key));
                }
            }
            if (nested)
                throw new InvalidDataException("Unclosed VDF block.");
        }
        Parse(false);
        var output = new StringBuilder(text);
        foreach (var edit in edits.AsEnumerable().Reverse())
            output.Remove(edit.Start, edit.Length).Insert(edit.Start, '"' + edit.Key + '"');
        return output.ToString();
    }
}
