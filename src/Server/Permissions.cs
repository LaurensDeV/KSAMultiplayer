namespace KSAMultiplayer.Server;

/// <summary>
/// What each command needs of the player who types it in a game. The console needs nothing: it is
/// the machine's, and whoever is at it is the operator.
///
/// <para>Trusted is what keeps a world tidy and running with no admin about: debris cleared,
/// craft protected and renamed, worlds saved and loaded, a griefer kicked. Admin is what cannot be
/// put back, and who else may: deleting a save, saving over one, reloading the world with its time,
/// and giving or taking roles. An operator is an admin who may also have the server's log. A command not in here is an admin's, so one added later and
/// forgotten is closed rather than open.</para>
/// </summary>
public static class Permissions
{
    private static readonly Dictionary<string, Wire.Role> Needs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reading, which tells a player nothing they could do harm with: not even addresses, which
        // only the console is shown.
        ["players"] = Wire.Role.Player,
        ["list"] = Wire.Role.Player,
        ["admins"] = Wire.Role.Player,
        ["protected"] = Wire.Role.Player,
        ["saves"] = Wire.Role.Player,
        ["status"] = Wire.Role.Player,
        ["version"] = Wire.Role.Player,
        ["help"] = Wire.Role.Player,
        ["?"] = Wire.Role.Player,

        ["say"] = Wire.Role.Trusted,
        ["kick"] = Wire.Role.Trusted,
        ["clear"] = Wire.Role.Trusted,
        ["sweep"] = Wire.Role.Trusted,
        ["remove"] = Wire.Role.Trusted,
        ["protect"] = Wire.Role.Trusted,
        ["unprotect"] = Wire.Role.Trusted,
        ["rename"] = Wire.Role.Trusted,
        // Under a new name; over an old one is an admin's, which GameServer.Keep says.
        ["save"] = Wire.Role.Trusted,
        ["snapshot"] = Wire.Role.Trusted,
        // The world it replaces is kept first, and a kept world that is let go goes to the bin.
        ["load"] = Wire.Role.Trusted,

        ["delete"] = Wire.Role.Admin,
        ["deleted"] = Wire.Role.Admin,
        ["restore"] = Wire.Role.Admin,
        ["reload"] = Wire.Role.Admin,
        // The world it throws away is kept first, so an admin may; never a trusted player.
        ["fresh"] = Wire.Role.Admin,
        ["reset"] = Wire.Role.Admin,
        ["op"] = Wire.Role.Admin,
        ["trust"] = Wire.Role.Admin,
        ["deop"] = Wire.Role.Admin,

        // Everybody's comings and goings, and the server's own troubles: see LogText.
        ["logs"] = Wire.Role.Operator,
    };

    /// <summary>The commands that change the world, a save or who may do what: what the audit log keeps.</summary>
    private static readonly HashSet<string> Audited = new(StringComparer.OrdinalIgnoreCase)
    {
        "kick", "clear", "sweep", "remove", "protect", "unprotect", "rename", "save", "snapshot", "load",
        "delete", "restore", "reload", "reset", "op", "trust", "deop", "fresh", "set", "settings", "quit", "exit", "stop",
    };

    /// <summary>The first word of a command line, as it is looked up.</summary>
    public static string Verb(string line) => line.Split(' ', 2, StringSplitOptions.TrimEntries)[0];

    /// <summary>What a player has to be to run this line.</summary>
    public static Wire.Role Need(string line) => Needs.TryGetValue(Verb(line), out Wire.Role role) ? role : Wire.Role.Admin;

    public static bool Audits(string line) => Audited.Contains(Verb(line));

    /// <summary>A role as a player reads it: "an admin", "trusted".</summary>
    public static string Said(Wire.Role role) => role switch
    {
        Wire.Role.Operator => "an operator",
        Wire.Role.Admin => "an admin",
        Wire.Role.Trusted => "trusted",
        _ => "a player",
    };
}
