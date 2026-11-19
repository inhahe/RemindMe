using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RemindMe.Tests;

/// <summary>
/// Each access key of a desktop window (Alt+letter) belongs to one control: with two, WPF
/// only moves the focus between them, and neither acts. Read from the windows' XAML and the
/// text their code sets, so it needs no WPF: each menu is a scope of its own, and the texts
/// one statement gives a control (Pause and Resume, say) are that control's alone.
/// </summary>
public class AccessKeyTests
{
    private static readonly Regex Key = new(@"(?<!_)_([A-Za-z0-9])");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static DirectoryInfo Desktop()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RemindMe.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"No RemindMe.slnx above {AppContext.BaseDirectory}: the test reads the desktop's sources");
        return new DirectoryInfo(Path.Combine(dir!.FullName, "RemindMe.Desktop"));
    }

    private static char? KeyOf(string text) =>
        text.StartsWith('{') || Key.Match(text) is not { Success: true } m ? null : char.ToLowerInvariant(m.Groups[1].Value[0]);

    [Fact]
    public void EachAccessKeyBelongsToOneControl()
    {
        var clashes = new List<string>();
        foreach (var xaml in Desktop().GetFiles("*.xaml"))
        {
            var doc = XDocument.Load(xaml.FullName, LoadOptions.SetLineInfo);
            // Each control's keys and scope, by its x:Name (or its place, unnamed).
            var keys = new Dictionary<string, (string Scope, HashSet<char> Keys)>();
            var scopeOf = new Dictionary<string, string>();
            int n = 0;
            foreach (var e in doc.Descendants())
            {
                var menu = e.Ancestors().FirstOrDefault(a => a.Name.LocalName is "ContextMenu" or "Menu");
                string scope = menu is null ? "window" : $"menu at line {((System.Xml.IXmlLineInfo)menu).LineNumber}";
                string id = (string?)e.Attribute(X + "Name") ?? $"#{n++}";
                scopeOf[id] = scope;
                foreach (var a in e.Attributes().Where(a => a.Name.LocalName is "Content" or "Header"))
                    if (KeyOf(a.Value) is { } k)
                    {
                        if (!keys.TryGetValue(id, out var c)) keys[id] = c = (scope, []);
                        c.Keys.Add(k);
                    }
            }
            // The texts the window's code gives its controls: "A.Content = B.Header = x ? "..." : "...";",
            // and controls it makes: "new Button { Content = "..." }" (each a control of its own).
            var code = new FileInfo(xaml.FullName + ".cs");
            if (code.Exists)
            {
                // Comments aren't code: "// Content = "_Save"" makes no control. Strings are read
                // first, so a "//" inside one ("pack://...") stays.
                var source = Regex.Replace(File.ReadAllText(code.FullName),
                    @"(@""(?:[^""]|"""")*""|\$?""(?:[^""\\\r\n]|\\.)*"")|//[^\r\n]*|/\*.*?\*/",
                    m => m.Groups[1].Success ? m.Value : "", RegexOptions.Singleline);
                static List<char> KeysIn(string expression) =>
                    Regex.Matches(expression, @"\$?""((?:[^""\\]|\\.)*)""").Select(l => KeyOf(l.Groups[1].Value)).OfType<char>().ToList();
                foreach (Match m in Regex.Matches(source, @"((?:\w+\.(?:Content|Header)\s*=\s*)+)([^;]*);"))
                {
                    var found = KeysIn(m.Groups[2].Value);
                    if (found.Count == 0) continue;
                    foreach (Match target in Regex.Matches(m.Groups[1].Value, @"(\w+)\.(?:Content|Header)"))
                    {
                        string id = target.Groups[1].Value;
                        if (!keys.TryGetValue(id, out var c)) keys[id] = c = (scopeOf.GetValueOrDefault(id, "window"), []);
                        c.Keys.UnionWith(found);
                    }
                }
                // A control made in code: its text runs to the end of the initializer's entry, over
                // lines and past a comma in a literal. (A menu item made in code is in a menu of
                // its own, which this can't tell apart: its Header is left out rather than taken
                // for the window's.)
                foreach (Match m in Regex.Matches(source, @"(?<![.\w])Content\s*=\s*((?:\$?""(?:[^""\\]|\\.)*""|[^,;}""])*)"))
                    if (KeysIn(m.Groups[1].Value) is { Count: > 0 } found)
                        keys[$"new at {code.Name}:{m.Index}"] = ("window", [.. found]);
            }
            foreach (var scope in keys.GroupBy(kv => kv.Value.Scope))
                foreach (var key in scope.SelectMany(kv => kv.Value.Keys.Select(k => (k, kv.Key))).GroupBy(p => p.k))
                    if (key.Count() > 1)
                        clashes.Add($"{xaml.Name}, {scope.Key}: Alt+{key.Key} on {string.Join(", ", key.Select(p => p.Item2))}");
        }
        Assert.Empty(clashes);
    }
}
