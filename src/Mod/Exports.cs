using System.Runtime.InteropServices;
using MewgenicsModSdk;
using MewgenicsModSdk.Api;

namespace Pedigree;

/// <summary>The C exports the framework's loader calls (NativeAOT needs them declared in the mod itself).</summary>
public static unsafe class Exports
{
    static readonly PedigreeMod Mod = new();

    [UnmanagedCallersOnly(EntryPoint = "MewMod_GetInfo")] public static ModInfo* GetInfo() => ModEntry.GetInfo(Mod);
    [UnmanagedCallersOnly(EntryPoint = "MewMod_Init")] public static void Init(MewgenicsApi* api) => ModEntry.Init(Mod, api);
    [UnmanagedCallersOnly(EntryPoint = "MewMod_Enable")] public static void Enable() => ModEntry.Enable(Mod);
    [UnmanagedCallersOnly(EntryPoint = "MewMod_Disable")] public static void Disable() => ModEntry.Disable(Mod);
    [UnmanagedCallersOnly(EntryPoint = "MewMod_ConfigReload")] public static void ConfigReload() => ModEntry.ConfigReload(Mod);
    [UnmanagedCallersOnly(EntryPoint = "MewMod_Action")] public static void Action(byte* key) => ModEntry.Action(Mod, Marshal.PtrToStringUTF8((nint)key) ?? "");
}
