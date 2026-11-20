using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AzerothCorePanel.Services.Config;

/// <summary>
/// An AzerothCore .conf file (with its .conf.dist for defaults) that can be edited and saved while keeping every
/// other line, comment and spacing exactly as it was.
/// </summary>
public sealed class ConfDocument
{
    public const string AddedBlockComment = "# Added by AzerothCore CPanel (missing from this file, present in the .conf.dist)";

    private static readonly Regex SettingLine =
        new(@"^(?<prefix>\s*(?<key>[A-Za-z0-9_.\-]+)\s*=\s*)(?<value>.*?)(?<suffix>\s*)$", RegexOptions.Compiled);

    // A comment line that is only an option name, like "#    Rate.XP.Quest".
    private static readonly Regex OptionName = new(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)+$|^[A-Z][A-Za-z0-9_]+$", RegexOptions.Compiled);

    private static readonly Regex CommentedOption = new(@"^#\s*[A-Za-z0-9_.\-]+\s*=", RegexOptions.Compiled);

    private readonly List<string> _lines;
    private readonly string _newLine;
    private readonly Encoding _encoding;

    private ConfDocument(string path, string? distPath, List<string> lines, string newLine, Encoding encoding,
        List<ConfSetting> settings)
    {
        Path = path;
        DistPath = distPath;
        _lines = lines;
        _newLine = newLine;
        _encoding = encoding;
        Settings = settings;
    }

    public string Path { get; }

    public string? DistPath { get; }

    /// <summary>Options in file order, then the ones that only exist in the .conf.dist.</summary>
    public IReadOnlyList<ConfSetting> Settings { get; }

    public bool HasChanges => Settings.Any(s => s.IsModified);

    public static ConfDocument Load(string path, string? distPath)
    {
        (List<string> lines, string newLine, Encoding encoding) = ReadLines(path);
        List<ParsedSetting> parsed = Parse(lines);

        Dictionary<string, ParsedSetting> defaults = new(StringComparer.OrdinalIgnoreCase);
        List<ParsedSetting> distOrder = new();
        if (distPath is not null && File.Exists(distPath))
        {
            foreach (ParsedSetting setting in Parse(ReadLines(distPath).Lines))
            {
                defaults[setting.Key] = setting;
                distOrder.Add(setting);
            }
        }

        var settings = new List<ConfSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // AzerothCore uses the last occurrence of a key, so that is the line to edit.
        foreach (ParsedSetting setting in parsed.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.Last())
                     .OrderBy(s => s.LineIndex))
        {
            defaults.TryGetValue(setting.Key, out ParsedSetting? dist);
            string description = setting.Description.Length > 0 ? setting.Description : dist?.Description ?? "";
            settings.Add(new ConfSetting(setting.Key, setting.Section, description, setting.Value, setting.Quoted,
                setting.LineIndex, setting.Prefix, setting.Suffix, dist?.Value));
            seen.Add(setting.Key);
        }

        foreach (ParsedSetting dist in distOrder.Where(d => seen.Add(d.Key)))
        {
            settings.Add(new ConfSetting(dist.Key, dist.Section, dist.Description, dist.Value, dist.Quoted,
                null, $"{dist.Key} = ", string.Empty, dist.Value));
        }

        return new ConfDocument(path, distPath, lines, newLine, encoding, settings);
    }

    /// <summary>
    /// Writes the changed values. Lines of existing options are replaced in place; options that were missing are
    /// appended in one block at the end. The previous file is kept as "&lt;name&gt;.bak".
    /// </summary>
    public void Save()
    {
        List<ConfSetting> changed = Settings.Where(s => s.IsModified).ToList();
        if (changed.Count == 0)
        {
            return;
        }

        List<ConfSetting> added = new();
        foreach (ConfSetting setting in changed)
        {
            if (setting.LineIndex is int index)
            {
                _lines[index] = setting.LinePrefix + setting.FormattedValue + setting.LineSuffix;
            }
            else
            {
                added.Add(setting);
            }
        }

        if (added.Count > 0)
        {
            // Keep the file's final newline after the new block.
            bool endsWithNewLine = _lines.Count > 0 && _lines[^1].Length == 0;
            if (endsWithNewLine)
            {
                _lines.RemoveAt(_lines.Count - 1);
            }

            if (!_lines.Contains(AddedBlockComment))
            {
                _lines.Add(string.Empty);
                _lines.Add(AddedBlockComment);
            }

            foreach (ConfSetting setting in added)
            {
                setting.LinePrefix = $"{setting.Key} = ";
                setting.LineSuffix = string.Empty;
                setting.LineIndex = _lines.Count;
                _lines.Add(setting.LinePrefix + setting.FormattedValue);
            }

            if (endsWithNewLine)
            {
                _lines.Add(string.Empty);
            }
        }

        File.Copy(Path, Path + ".bak", overwrite: true);
        File.WriteAllText(Path, string.Join(_newLine, _lines), _encoding);

        foreach (ConfSetting setting in changed)
        {
            setting.AcceptChanges();
        }
    }

    private static (List<string> Lines, string NewLine, Encoding Encoding) ReadLines(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var encoding = new UTF8Encoding(bom);
        string text = encoding.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        string newLine = text.Contains("\r\n") ? "\r\n" : "\n";

        // Splitting on '\n' keeps a trailing empty entry when the file ends with a newline, so joining restores it.
        List<string> lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        return (lines, newLine, encoding);
    }

    private static List<ParsedSetting> Parse(IReadOnlyList<string> lines)
    {
        var result = new List<ParsedSetting>();
        var comments = new List<string>();
        string section = string.Empty;
        bool afterSeparator = false;

        // Several options can share one comment block ("#    Rate.XP.Kill", "#    Rate.XP.Quest", ... then the
        // description): the block is kept for consecutive options and only replaced by the next comment block.
        bool lastWasSetting = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith('#'))
            {
                if (trimmed.StartsWith("####", StringComparison.Ordinal))
                {
                    afterSeparator = true;
                    lastWasSetting = false;
                    comments.Clear();
                    continue;
                }

                // A commented-out option ("# Appender.GM=2,5,15,gm_%s.log") is an example, not a description.
                if (CommentedOption.IsMatch(trimmed))
                {
                    continue;
                }

                if (lastWasSetting)
                {
                    comments.Clear();
                    lastWasSetting = false;
                }

                // "# SECTION NAME" right after a "####" line.
                if (afterSeparator)
                {
                    string title = trimmed.Trim('#', ' ', '\t');
                    if (title.Length > 0)
                    {
                        section = title.EndsWith(" BEGIN", StringComparison.Ordinal) ? section : title;
                        afterSeparator = false;
                    }

                    continue;
                }

                comments.Add(line);
                continue;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                comments.Clear();
                continue;
            }

            Match match = SettingLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string key = match.Groups["key"].Value;
            string raw = match.Groups["value"].Value;
            bool quoted = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"';

            result.Add(new ParsedSetting(key, section, Describe(comments, key), quoted ? raw[1..^1] : raw, quoted, i,
                match.Groups["prefix"].Value, match.Groups["suffix"].Value));
            lastWasSetting = true;
        }

        return result;
    }

    /// <summary>
    /// The comment lines after "#    Key" and any other option names listed with it (or the whole block when the key
    /// is not named), without '#' and common indentation.
    /// </summary>
    private static string Describe(List<string> comments, string key)
    {
        static string Text(string comment) => comment.Trim().TrimStart('#').Trim();

        int start = comments.FindLastIndex(c => Text(c).Equals(key, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> block = comments;
        if (start >= 0)
        {
            int next = start + 1;
            while (next < comments.Count && OptionName.IsMatch(Text(comments[next])))
            {
                next++;
            }

            block = comments.Skip(next);
        }

        List<string> text = block.Select(c => c.TrimStart().StartsWith('#') ? c.TrimStart()[1..] : c).ToList();
        int indent = text.Where(t => t.Trim().Length > 0).Select(t => t.Length - t.TrimStart().Length)
            .DefaultIfEmpty(0).Min();

        return string.Join("\n", text.Select(t => t.Length >= indent ? t[indent..].TrimEnd() : t.Trim())).Trim('\n');
    }

    private sealed record ParsedSetting(string Key, string Section, string Description, string Value, bool Quoted,
        int LineIndex, string Prefix, string Suffix);
}
