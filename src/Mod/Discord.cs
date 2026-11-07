using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KSAMultiplayer;

/// <summary>
/// The shared world in Discord: which server a player is on and with how many, and the way in for
/// whoever they invite or let join. Through Discord's Social SDK, whose native library ships beside
/// the mod. With no application id, no library or no Discord running there is simply no Discord,
/// and nothing else notices.
///
/// <para>Nobody signs in. Rich presence, its party and its join secret go through the Discord
/// client already running, and so does an invite accepted there: Discord hands the secret back to
/// the game it launches or the one already running, through <see cref="OnJoin"/>.</para>
///
/// <para>The secret is the server's address and its password. Discord shows it to nobody but the
/// players the host invites or lets join, and every one of them could read it from the game.</para>
/// </summary>
internal static unsafe class Discord
{
    /// <summary>
    /// The Discord application this mod speaks as, given to the build and read back from this
    /// assembly: Discord counts it a developer credential, which may not be in an open-source
    /// project's source. Zero for a build that was given none, and then nothing here runs.
    /// </summary>
    private static readonly ulong ApplicationId =
        typeof(Discord).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DiscordApplicationId")?.Value is { } id && ulong.TryParse(id.Trim(), out ulong parsed) ? parsed : 0;

    private const string Library = "discord_partner_sdk";

    // Discord takes a handful of presence updates in a short while and drops the rest.
    private const double UpdateEverySeconds = 5.0;

    // Discord refuses a join secret longer than this.
    private const int SecretBytes = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct Handle
    {
        public nint Opaque;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Text
    {
        public byte* Ptr;
        public nuint Size;
    }

    private static Handle* _client;
    private static bool _tried, _complained;
    private static string _shown = "";
    private static double _shownAt = double.NegativeInfinity;
    private static (string Address, string Password)? _invited;

    /// <summary>An invite accepted in Discord, taken once: the server's address and its password.</summary>
    public static (string Address, string Password)? TakeInvite()
    {
        (string, string)? invited = _invited;
        _invited = null;
        return invited;
    }

    /// <summary>
    /// Discord's callbacks run, which is when an invite arrives. From the frame once the game has
    /// loaded, and from KSA's configuration before it, where a game launched from an invite is.
    /// </summary>
    public static void Pump()
    {
        if (!Started()) return;
        try
        {
            Discord_RunCallbacks();
        }
        catch (Exception e)
        {
            Complain("running Discord's callbacks", e);
        }
    }

    /// <summary>What this player is doing, told to Discord when it changes and no oftener than it takes it.</summary>
    public static void Update(double now)
    {
        Pump();
        if (_client is null || now - _shownAt < UpdateEverySeconds) return;
        if (Sync.Client is not { Joined: true } c)
        {
            if (_shown.Length == 0) return;
            Discord_Client_ClearRichPresence(_client);
            (_shown, _shownAt) = ("", now);
            return;
        }

        string server = c.ServerName.Length > 0 ? c.ServerName : "a shared world";
        Wire.Player me = c.Players.FirstOrDefault(p => p.Id == c.Id);
        string doing = Sync.Now != Sync.Stage.Playing ? "Joining"
            : me.Controlling is { Length: > 0 } flying ? $"Flying {flying}" : "In the world";
        int here = Math.Max(1, c.Players.Length);
        int most = Math.Max(here, c.MostPlayers);
        string secret = $"{Sync.ServerAddress}\n{Sync.ServerPassword}";
        // Too long to be let in by is no way in at all; the rest of the presence still stands.
        if (Encoding.UTF8.GetByteCount(secret) > SecretBytes) secret = "";

        string shown = $"{server}|{doing}|{here}|{most}|{secret}";
        if (shown == _shown) return;
        (_shown, _shownAt) = (shown, now);
        Show(server, doing, here, most, secret);
    }

    public static void Shutdown()
    {
        if (_client is null) return;
        try
        {
            Discord_Client_Drop(_client);
        }
        catch (Exception e)
        {
            Complain("letting go of Discord", e);
        }
        NativeMemory.Free(_client);
        _client = null;
    }

    private static bool Started()
    {
        if (_client is not null) return true;
        if (_tried || ApplicationId == 0) return false;
        _tried = true;
        Handle* client = (Handle*)NativeMemory.AllocZeroed((nuint)sizeof(Handle));
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Discord).Assembly, Resolve);
            Discord_Client_Init(client);
            Discord_Client_SetApplicationId(client, ApplicationId);
            Discord_Client_SetActivityJoinCallback(client, &OnJoin, null, null);
            _client = client;
            Register();
            Log.Info("Discord: ready");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            NativeMemory.Free(client);
            Log.Info($"Discord: not here ({e.Message})");
            return false;
        }
    }

    // Beside this assembly, where the mod's zip puts it: a mod's folder is nowhere the runtime looks.
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;
        string folder = Path.GetDirectoryName(assembly.Location) ?? "";
        foreach (string file in (string[])[$"{Library}.dll", $"lib{Library}.so", $"lib{Library}.dylib"])
        {
            if (NativeLibrary.TryLoad(Path.Combine(folder, file), out nint handle)) return handle;
        }
        return 0;
    }

    /// <summary>
    /// How Discord starts this game for an invite accepted while it is closed: the way it was started
    /// this time, StarMap and its profile included. The invite then arrives as it would to a game
    /// already running, and the start window joins it before KSA has made a universe.
    /// </summary>
    private static void Register()
    {
        string[] args = Environment.GetCommandLineArgs();
        string command = $"\"{Environment.ProcessPath}\"" + string.Concat(args.Skip(1).Select(a => $" \"{a}\""));
        using Utf8 text = new(command);
        if (!Discord_Client_RegisterLaunchCommand(_client, ApplicationId, text.Value)) Log.Warn("Discord: could not register how to start this game");
    }

    private static void Show(string server, string doing, int here, int most, string secret)
    {
        try
        {
            Handle activity = default, party = default, secrets = default;
            Discord_Activity_Init(&activity);
            Discord_ActivityParty_Init(&party);
            Discord_ActivitySecrets_Init(&secrets);
            try
            {
                using Utf8 details = new(server), state = new(doing), id = new(PartyId(Sync.ServerAddress)), join = new(secret);
                Text detailsText = details.Value, stateText = state.Value;
                Discord_Activity_SetType(&activity, ActivityPlaying);
                Discord_Activity_SetDetails(&activity, &detailsText);
                Discord_Activity_SetState(&activity, &stateText);
                Discord_Activity_SetSupportedPlatforms(&activity, PlatformDesktop);
                Discord_ActivityParty_SetId(&party, id.Value);
                Discord_ActivityParty_SetCurrentSize(&party, here);
                Discord_ActivityParty_SetMaxSize(&party, most);
                // Public: a friend can ask to join without being invited, and the host still says yes.
                Discord_ActivityParty_SetPrivacy(&party, PartyPublic);
                Discord_Activity_SetParty(&activity, &party);
                if (secret.Length > 0)
                {
                    Discord_ActivitySecrets_SetJoin(&secrets, join.Value);
                    Discord_Activity_SetSecrets(&activity, &secrets);
                }
                Discord_Client_UpdateRichPresence(_client, &activity, &OnShown, null, null);
            }
            finally
            {
                Discord_ActivitySecrets_Drop(&secrets);
                Discord_ActivityParty_Drop(&party);
                Discord_Activity_Drop(&activity);
            }
        }
        catch (Exception e)
        {
            Complain("telling Discord what this player is doing", e);
        }
    }

    // The same for everybody on one server, and nothing anyone can read the address back out of.
    private static string PartyId(string address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant())))[..32];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnJoin(Text secret, void* userData)
    {
        try
        {
            string said = Encoding.UTF8.GetString(secret.Ptr, (int)secret.Size);
            int split = said.IndexOf('\n');
            _invited = split < 0 ? (said, "") : (said[..split], said[(split + 1)..]);
            Log.Info("Discord: an invite accepted, to join a server");
        }
        catch (Exception e)
        {
            Complain("taking an invite from Discord", e);
        }
        finally
        {
            Discord_Free(secret.Ptr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShown(Handle* result, void* userData)
    {
        try
        {
            if (!Discord_ClientResult_Successful(result))
            {
                Text error = default;
                Discord_ClientResult_Error(result, &error);
                Log.Warn($"Discord: the presence was refused: {Encoding.UTF8.GetString(error.Ptr, (int)error.Size)}");
                Discord_Free(error.Ptr);
            }
        }
        catch (Exception e)
        {
            Complain("hearing back from Discord", e);
        }
        finally
        {
            Discord_ClientResult_Drop(result);
        }
    }

    private static void Complain(string what, Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error($"Discord: {what} failed", e);
    }

    /// <summary>A string as Discord takes one: UTF-8 it copies, in memory of this side's that is freed after the call.</summary>
    private readonly struct Utf8 : IDisposable
    {
        public readonly Text Value;

        public Utf8(string text)
        {
            int bytes = Encoding.UTF8.GetByteCount(text);
            byte* at = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes, 1));
            Encoding.UTF8.GetBytes(text, new Span<byte>(at, bytes));
            Value = new Text { Ptr = at, Size = (nuint)bytes };
        }

        public void Dispose() => NativeMemory.Free(Value.Ptr);
    }

    // ------------------------------------------------------------------ cdiscord.h

    private const int ActivityPlaying = 0, PlatformDesktop = 1, PartyPublic = 1;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_RunCallbacks();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Free(void* ptr);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Client_Init(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Client_Drop(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Client_SetApplicationId(Handle* self, ulong applicationId);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Client_ClearRichPresence(Handle* self);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Discord_Client_RegisterLaunchCommand(Handle* self, ulong applicationId, Text command);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Discord_Client_SetActivityJoinCallback(Handle* self, delegate* unmanaged[Cdecl]<Text, void*, void> cb,
                                                                      delegate* unmanaged[Cdecl]<void*, void> free, void* userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Discord_Client_UpdateRichPresence(Handle* self, Handle* activity, delegate* unmanaged[Cdecl]<Handle*, void*, void> cb,
                                                                 delegate* unmanaged[Cdecl]<void*, void> free, void* userData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool Discord_ClientResult_Successful(Handle* self);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ClientResult_Error(Handle* self, Text* returnValue);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ClientResult_Drop(Handle* self);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_Init(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_Drop(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_SetType(Handle* self, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_SetDetails(Handle* self, Text* value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_SetState(Handle* self, Text* value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_SetSupportedPlatforms(Handle* self, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_SetParty(Handle* self, Handle* value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_Activity_SetSecrets(Handle* self, Handle* value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivityParty_Init(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivityParty_Drop(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivityParty_SetId(Handle* self, Text value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivityParty_SetCurrentSize(Handle* self, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivityParty_SetMaxSize(Handle* self, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivityParty_SetPrivacy(Handle* self, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivitySecrets_Init(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivitySecrets_Drop(Handle* self);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void Discord_ActivitySecrets_SetJoin(Handle* self, Text value);
}
