#nullable disable
using System.Reflection;

namespace DW2Mod;

// Live channels between code mods (docs/DLL Injection.md in DW2-Mod-Launcher, "Live channels"): a mod that has data which changes in play
// provides a delegate under a channel name, and another mod asks for that delegate by name and calls it when it needs the answer. Neither
// names the other.
//
// A soft dependency like LauncherStatus: there is no compile-time reference to the loader; DW2ModLauncher.Loader.LiveChannels is found by
// name among the loaded assemblies and bound once. Without the launcher (the dll injected directly) Present is false, Provide does nothing
// and Get gives null, which a consumer reports as "nobody provides this".
//
// Every channel has a contract version, stated by the provider and by the consumer, and the consumer also states the delegate type it
// expects. The loader hands the consumer nothing (null, as if nobody provided) when either differs, and reports it on the status line,
// so a provider and consumer built against different contracts are told apart from a provider that is simply absent.
//
// Ask when you need it, never once at start-up: mods load one after another, so a provider may not have registered yet. The one start-up call
// that is fine is Expect, which only records that this mod uses the channel (the launcher lists who is connected from those records) and keeps nothing.
// Use delegate types built from BCL types only: each mod compiles its own copy of its own types, which differ from every other mod's.
//
// Shared source (dll/Shared): every code mod compiles its own internal copy.
internal static class LiveChannel
{
    public static readonly bool Present;

    // set when the launcher's loader is there but has no live channels (an older loader)
    public static readonly string BindFailure;

    static readonly string provider = typeof(LiveChannel).Assembly.GetName().Name;
    static readonly Func<string, string, int, Delegate, Delegate> provide;
    static readonly Func<string, string, int, Type, Delegate> get;
    static readonly Func<string, string> providerOf;

    static LiveChannel()
    {
        try
        {
            Type channels = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name == "DW2ModLauncher.Loader")
                {
                    channels = a.GetType("DW2ModLauncher.Loader.LiveChannels");
                    if (channels == null)
                    {
                        BindFailure = "the launcher's loader has no live channels (update the DW2 Mod Launcher)";
                    }
                    break;
                }
            }
            if (channels == null)
            {
                return;
            }
            provide = Bind<Func<string, string, int, Delegate, Delegate>>(channels, "Provide");
            get = Bind<Func<string, string, int, Type, Delegate>>(channels, "Get");
            providerOf = Bind<Func<string, string>>(channels, "ProviderOf");
            Present = true;
        }
        catch (Exception ex)
        {
            Present = false;
            BindFailure = ex.Message;
        }
    }

    static T Bind<T>(Type type, string name) where T : Delegate
        => (T)Delegate.CreateDelegate(typeof(T), type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(name), throwOnBindFailure: false)
            ?? throw new MissingMethodException($"the launcher's loader has no {name} with versioned live channels (update the DW2 Mod Launcher)");

    // Offers the handler on the channel under this mod's name. A later mod providing the same channel takes it over (the loader logs who
    // superseded whom). Returns the handler this one replaced, null if there was none: a mod that wants to modify the earlier mod's data
    // calls it from its own handler (assign the result to a variable the handler's lambda reads) and so publishes the adjusted answer.
    public static T Provide<T>(string channel, int version, T handler) where T : Delegate
    {
        Delegate previous = provide?.Invoke(provider, channel, version, handler);
        if (previous == null)
        {
            return null;
        }
        return previous as T ?? throw new InvalidCastException($"live channel '{channel}' was provided as a {previous.GetType()}, not the {typeof(T)} this mod provides");
    }

    // The channel's handler, or null when no mod provides it (yet) or the provider's version or delegate type is not the one asked for (the
    // loader has then said so on the status line). The loader has checked the type, so the handler is a T.
    public static T Get<T>(string channel, int version) where T : Delegate
        => (T)get?.Invoke(provider, channel, version, typeof(T));

    // Records that this mod uses the channel, with the version and delegate type it will ask for, without needing the answer now: the loader notes every Get as a consumer
    // (hooks-observed.json, then each mod's dw2modlauncher.hooks.json), and a mod that asks only late in play (when a screen opens, on a timer) would otherwise be missing
    // from the launcher's list of who receives what. Call it from the feature's Install with the same version and type as the later Get; the result is thrown away, so a
    // provider that has not registered yet costs nothing, and a mismatch is reported by the later Get as always.
    public static void Expect<T>(string channel, int version) where T : Delegate => get?.Invoke(provider, channel, version, typeof(T));

    // The name of the mod providing the channel, or null when nobody does. Tells "nobody provides it" (a player's choice, like a switched-off feature) from "a mod
    // provides it but not at the version or type asked for" (Get gives null for both).
    public static string ProviderOf(string channel) => providerOf?.Invoke(channel);
}
