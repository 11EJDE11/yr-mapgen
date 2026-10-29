using System.Text;

namespace MapGen.Formats;

/// <summary>Minimal ordered INI document. Keys keep insertion order; lookups are case-insensitive.</summary>
public sealed class IniDocument
{
    private readonly List<string> order = new();
    private readonly Dictionary<string, IniSectionData> sections = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<IniSectionData> Sections => order.Select(n => sections[n]);

    public IniSectionData Section(string name)
    {
        if (!sections.TryGetValue(name, out var s))
        {
            s = new IniSectionData(name);
            sections[name] = s;
            order.Add(name);
        }
        return s;
    }

    public IniSectionData TryGetSection(string name) => sections.TryGetValue(name, out var s) ? s : null;

    public string Get(string section, string key, string defaultValue = null)
        => TryGetSection(section)?.Get(key) ?? defaultValue;

    public void Set(string section, string key, string value) => Section(section).Set(key, value);

    public static IniDocument Parse(string text)
    {
        var doc = new IniDocument();
        IniSectionData current = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine;
            int comment = line.IndexOf(';');
            if (comment >= 0)
                line = line.Substring(0, comment);
            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                current = doc.Section(end > 0 ? line.Substring(1, end - 1) : line.Substring(1));
                continue;
            }

            int eq = line.IndexOf('=');
            if (current == null || eq <= 0)
                continue;
            current.Set(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
        }
        return doc;
    }

    public static IniDocument Load(string path) => Parse(File.ReadAllText(path, Encoding.Latin1));

    public string Write()
    {
        var sb = new StringBuilder();
        foreach (var s in Sections)
        {
            sb.Append('[').Append(s.Name).Append("]\r\n");
            foreach (var (k, v) in s.Entries)
                sb.Append(k).Append('=').Append(v).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

public sealed class IniSectionData
{
    private readonly List<string> keys = new();
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public IniSectionData(string name) { Name = name; }

    public string Name { get; }

    public IEnumerable<(string Key, string Value)> Entries => keys.Select(k => (k, values[k]));

    public IReadOnlyDictionary<string, string> Values => values;

    public string Get(string key) => values.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int defaultValue)
        => int.TryParse(Get(key), out int i) ? i : defaultValue;

    public void Set(string key, string value)
    {
        if (!values.ContainsKey(key))
            keys.Add(key);
        values[key] = value;
    }

    /// <summary>Removes every key; the section keeps its place in the document.</summary>
    public void Clear()
    {
        keys.Clear();
        values.Clear();
    }
}
