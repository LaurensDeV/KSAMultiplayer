using System.Globalization;
using System.Net;
using System.Text;

namespace KSAMultiplayer.Server;

/// <summary>
/// The server's settings: one table of them, read from the command line, the environment and a
/// TOML file, in that order of precedence, and written back to the file from the console.
///
/// <para>The file is flat -- <c>key = value</c> lines and comments, with strings, numbers and
/// booleans -- because that is all a server's settings are. It is read and written here rather
/// than by a library: a server shipped as one executable carries no more than it must, and a line
/// is changed in place so whatever the operator wrote around it stays.</para>
/// </summary>
public static class Settings
{
    public enum Kind { Text, Whole, Number, Flag }

    /// <param name="Live">Whether a running server takes it up at once; otherwise it waits for a restart.</param>
    public sealed record Setting(string Key, Kind Kind, string Default, string Says, bool Live);

    /// <summary>Every setting, in the order the file lists them. A key is also its --flag and, in capitals, its KSAMP_ variable.</summary>
    public static readonly Setting[] All =
    [
        new("name", Kind.Text, "KSA server", "the server's name, shown to players", true),
        new("id", Kind.Text, "", "a short name for this server that never changes though its name and address may, in letters, digits, dots, dashes and underscores (my-clan-cars): a mod manager knows the mods it installed for this server by it; empty to be known by address", true),
        new("port", Kind.Whole, "50100", "UDP port to listen on", false),
        new("bind", Kind.Text, "", "address to listen on; empty for every address", false),
        new("password", Kind.Text, "", "a password players must give; empty for none", true),
        new("max-players", Kind.Whole, "16", "the most players at once, 1 to 250", true),
        new("data", Kind.Text, "data", "where the world is kept between runs, relative to where the server is started", false),
        new("ksa-build", Kind.Text, "", "the KSA build players must run; empty for the build this server was built for", true),
        new("mods", Kind.Text, "", "the mods players must have, separated by commas, each held to a version after an @ where one is given (FastAndPurrious@0.1.13); empty for those of whoever gave the server its world", true),
        new("snapshot-every", Kind.Number, "300", "seconds between fresh saves of the world, at least 30", true),
        new("warp-answer", Kind.Number, "30", "seconds everyone has to answer a warp, 1 to 300", true),
        new("lapse-within", Kind.Number, "10", "seconds a craft stays its owner's while they are not near it, 1 to 3600", true),
        new("keep-time", Kind.Flag, "false", "let universe time run with nobody playing", true),
        new("speed", Kind.Number, "1", "how fast universe time runs, 0 to 100", true),
        new("empty-world", Kind.Text, "", "start with an empty world of this system rather than waiting for a player to bring one, and the world 'fresh' puts in place of the one there is; empty to wait", true),
        new("advertise", Kind.Flag, "false", "list this server in the public directories so players can find it; its address becomes public", true),
        new("advertise-as", Kind.Text, "", "the hostname to be listed under; empty to be listed at the address a directory sees", true),
        new("directories", Kind.Text, "", "where to announce, separated by commas; empty to read the shared list, which only an advertising server does", true),
    ];

    public const string FileName = "ksamp-server.toml";

    /// <summary>Beside the executable: one server to an install, and the file goes where the server is.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static Setting? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    public static string EnvironmentName(string key) => "KSAMP_" + key.ToUpperInvariant().Replace('-', '_');

    /// <summary>What every setting comes to, and where from, with a flag over the environment over the file over its default.</summary>
    public static Dictionary<string, (string Value, string From)> Resolve(
        IReadOnlyDictionary<string, string> flags, Func<string, string?> environment, IReadOnlyDictionary<string, string> file)
    {
        Dictionary<string, (string, string)> resolved = [];
        foreach (Setting s in All)
        {
            resolved[s.Key] = flags.TryGetValue(s.Key, out string? f) ? (f, $"--{s.Key}")
                : environment(EnvironmentName(s.Key)) is { } e ? (e, EnvironmentName(s.Key))
                : file.TryGetValue(s.Key, out string? v) ? (v, "the settings file")
                : (s.Default, "its default");
        }
        return resolved;
    }

    /// <summary>The options those settings come to, held to what each may be. Throws <see cref="FormatException"/> naming the setting that is not a value of its kind.</summary>
    public static (ServerOptions Options, IPAddress? Bind, string Data) Options(IReadOnlyDictionary<string, (string Value, string From)> s)
    {
        string Text(string key) => s[key].Value;
        double Number(string key)
        {
            (string v, string from) = s[key];
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
                ? d : throw new FormatException($"{key} from {from} is '{v}', which is not a number");
        }
        int Whole(string key)
        {
            (string v, string from) = s[key];
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n : throw new FormatException($"{key} from {from} is '{v}', which is not a whole number");
        }
        bool Flag(string key)
        {
            (string v, string from) = s[key];
            return v.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" or "" => false,
                _ => throw new FormatException($"{key} from {from} is '{v}', which is not true or false"),
            };
        }

        string Empty()
        {
            (string v, string from) = s["empty-world"];
            string system = v.Trim();
            // Held to the worlds this server carries, since the only thing it could do with any
            // other name is nothing, and a server that starts empty when it was told not to is a
            // world a room is waiting on for no reason anybody can see.
            return system.Length == 0 || StockWorld.Has(system) ? system
                : throw new FormatException($"empty-world from {from} is '{system}', and there is no empty world for it; there is one for {string.Join(", ", StockWorld.Systems)}");
        }

        string Id()
        {
            (string v, string from) = s["id"];
            string id = v.Trim();
            // Held to what a link can carry, here where the operator reads the answer: an id a
            // game would have to drop is a server known by its address after all, silently.
            return id.Length == 0 || ModLink.IsId(id) ? id
                : throw new FormatException($"id from {from} is '{id}', which is not 1 to {Wire.IdChars} letters, digits, dots, dashes and underscores beginning and ending with a letter or digit");
        }

        int port = Whole("port");
        if (port is < 0 or > 65535) throw new FormatException($"port {port} is not a port");
        ServerOptions options = new()
        {
            Port = port,
            Name = Text("name"),
            Id = Id(),
            Password = Text("password"),
            MaxPlayers = Math.Clamp(Whole("max-players"), 1, 250),
            KsaBuild = Text("ksa-build"),
            Mods = Text("mods").Trim(),
            SnapshotEverySeconds = Math.Max(30.0, Number("snapshot-every")),
            WarpAnswerSeconds = Math.Clamp(Number("warp-answer"), 1.0, 300.0),
            LapseWithin = Math.Clamp(Number("lapse-within"), 1.0, 3600.0),
            KeepTime = Flag("keep-time"),
            Speed = Math.Clamp(Number("speed"), 0.0, 100.0),
            EmptyWorld = Empty(),
            Advertise = Flag("advertise"),
            AdvertiseAs = Text("advertise-as").Trim(),
            Directories = Text("directories").Trim(),
        };
        IPAddress? bind = Text("bind").Trim() is { Length: > 0 } b
            ? IPAddress.TryParse(b, out IPAddress? at) ? at : throw new FormatException($"bind is '{b}', which is not an address")
            : null;
        string data = Text("data").Trim() is { Length: > 0 } d ? d : "data";
        return (options, bind, data);
    }

    // ------------------------------------------------------------------ the file

    /// <summary>The file a server writes when it has none: every setting at its default, with what it does.</summary>
    public static string Template()
    {
        StringBuilder t = new();
        t.AppendLine("# KSA Multiplayer server settings. A --flag or a KSAMP_ environment variable overrides a");
        t.AppendLine("# line here. 'settings' at the console reads this again; 'set <key> <value>' changes it.");
        foreach (Setting s in All)
        {
            t.AppendLine();
            t.AppendLine($"# {s.Says}{(s.Live ? "" : " (a restart takes it up)")}");
            t.AppendLine($"{s.Key} = {Format(s, s.Default)}");
        }
        return t.ToString();
    }

    /// <summary>
    /// The settings in a file's text, each as a string as a flag would give it. Throws
    /// <see cref="FormatException"/> with the line for anything that is not a known key and a value.
    /// </summary>
    public static Dictionary<string, string> Read(string text)
    {
        Dictionary<string, string> read = [];
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[') throw new FormatException($"line {i + 1}: tables are not settings here: {line}");
            int eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"line {i + 1}: not 'key = value': {line}");
            string key = line[..eq].Trim();
            if (Find(key) is null) throw new FormatException($"line {i + 1}: no setting called '{key}'");
            // TOML forbids a key twice, and here it would be worse than untidy: Write changes the
            // first line with a key on it and this kept the last, so 'set port 50999' wrote a value
            // into the file that the server went on ignoring, with nothing said either way.
            if (read.ContainsKey(key)) throw new FormatException($"line {i + 1}: '{key}' is set twice");
            read[key] = Value(line[(eq + 1)..].Trim(), i + 1);
        }
        return read;
    }

    /// <summary>The file's text with one setting changed in place, or added at the end when it has no line yet.</summary>
    public static string Write(string text, Setting setting, string value)
    {
        string line = $"{setting.Key} = {Format(setting, value)}";
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            int eq = trimmed.IndexOf('=');
            if (trimmed.StartsWith('#') || eq <= 0 || trimmed[..eq].Trim() != setting.Key) continue;
            lines[i] = line + (lines[i].EndsWith('\r') ? "\r" : "");
            return string.Join('\n', lines);
        }
        return text.TrimEnd() + Environment.NewLine + line + Environment.NewLine;
    }

    private static string Format(Setting s, string value) => s.Kind == Kind.Text ? Quote(value) : value.Trim();

    private static string Quote(string s)
    {
        StringBuilder q = new("\"");
        foreach (char c in s)
        {
            q.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return q.Append('"').ToString();
    }

    // A value as TOML writes one: a basic or literal string, or a bare number or boolean, with a
    // comment after it allowed.
    private static string Value(string v, int line)
    {
        if (v.StartsWith('\''))
        {
            int end = v.IndexOf('\'', 1);
            if (end < 0) throw new FormatException($"line {line}: a string that does not end");
            Rest(v[(end + 1)..], line);
            return v[1..end];
        }
        if (v.StartsWith('"'))
        {
            StringBuilder s = new();
            for (int i = 1; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '"')
                {
                    Rest(v[(i + 1)..], line);
                    return s.ToString();
                }
                if (c != '\\')
                {
                    s.Append(c);
                    continue;
                }
                if (++i >= v.Length) break;
                switch (v[i])
                {
                    case '"': s.Append('"'); break;
                    case '\\': s.Append('\\'); break;
                    case 'n': s.Append('\n'); break;
                    case 'r': s.Append('\r'); break;
                    case 't': s.Append('\t'); break;
                    case 'u' when i + 4 < v.Length && int.TryParse(v.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u):
                        s.Append((char)u);
                        i += 4;
                        break;
                    default: throw new FormatException($"line {line}: '\\{v[i]}' is no escape TOML knows");
                }
            }
            throw new FormatException($"line {line}: a string that does not end");
        }
        int hash = v.IndexOf('#');
        string bare = (hash >= 0 ? v[..hash] : v).Trim().Replace("_", "");
        if (bare.Length == 0) throw new FormatException($"line {line}: no value");
        return bare;
    }

    private static void Rest(string after, int line)
    {
        string rest = after.Trim();
        if (rest.Length > 0 && rest[0] != '#') throw new FormatException($"line {line}: '{rest}' after the value");
    }
}
