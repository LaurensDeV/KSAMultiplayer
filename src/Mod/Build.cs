using System.IO;

namespace KSAMultiplayer;

/// <summary>What build this is, read off the assembly rather than written down.</summary>
internal static class Build
{
    /// <summary>
    /// Whether this is a developer's install: a <c>developer</c> file beside the assembly, which
    /// <c>tools/deploy.sh</c> writes and <c>tools/package.sh</c> never stages. It starts the bridge,
    /// which a player has no use for and which no released mod should be listening on.
    /// </summary>
    public static bool Developer { get; } = ResolveDeveloper();

    private static bool ResolveDeveloper()
    {
        try
        {
            string? dir = Path.GetDirectoryName(typeof(Build).Assembly.Location);
            return dir is not null && File.Exists(Path.Combine(dir, "developer"));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
