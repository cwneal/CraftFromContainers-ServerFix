using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Z_CraftFromContainersServerFix
//
// Third-party compatibility patch, not affiliated with the original mod author(s).
//
// Root cause (confirmed via a dedicated-server crash log, not guessed): CraftFromContainers by
// Aedenthorn - and likely other Aedenthorn mods sharing the same embedded AedenthornUtils helper
// class (Gears, Quartz, PartyScout, QuickStack, WMM12SlotToolbelt look like plausible candidates by
// naming, unconfirmed) - use a shared static helper, AedenthornUtils.GetAssetPath(string, bool), to
// locate their own mod folder. That helper derives its base directory from
// Assembly.GetExecutingAssembly().Location, then calls Path.GetDirectoryName() on it. On this
// dedicated server's headless build, Location comes back as an empty string for dynamically-loaded
// mod assemblies (a build-specific quirk, not something wrong with the mod's files or config) -
// Path.GetDirectoryName("") throws ArgumentException("Invalid path"), which crashes the affected
// mod's InitMod() before it does anything else. Confirmed this does NOT happen in a local single-
// player client with the identical mod folder - server-only.
//
// This patch does two things, in order, from its own InitMod():
//   1. Harmony-patches every AedenthornUtils.GetAssetPath(string, bool) found in any currently
//      loaded mod assembly, replacing the broken Assembly.Location-based lookup with the game's own
//      already-correct Mod.Path (ModManager knows exactly where each mod's DLL lives - it logs the
//      right path itself right before the crash - this patch just uses that instead of asking the
//      broken helper to re-derive it).
//   2. Mod load order is not guaranteed to put this patch before the affected mod's own init (in
//      testing, this server's actual order was not alphabetical), so if any mod already failed to
//      load specifically because its assembly contains this same AedenthornUtils type, this
//      retries that mod's InitMod() once, now that the lookup is fixed. Safe to retry: the crash
//      happens on literally the first meaningful call inside the affected mod's LoadConfig(),
//      before any state has been set up, so there's nothing partial to clean up first.
public class CraftFromContainersServerFixApi : IModApi
{
    private const string Prefix = "[CraftFromContainersServerFix]";

    public void InitMod(Mod modInstance)
    {
        try
        {
            Harmony harmony = new Harmony("com.gloomatmosphere.craftfromcontainersserverfix");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Debug.Log(Prefix + " Patched AedenthornUtils.GetAssetPath(string,bool) in " + AffectedAssemblyNames.Count + " assembly(ies): " + string.Join(", ", AffectedAssemblyNames.ToArray()));

            RetryFailedAedenthornMods();
        }
        catch (Exception ex)
        {
            Debug.LogError(Prefix + " InitMod failed: " + ex);
        }
    }

    // Populated by GetAssetPathPatch.TargetMethods() as it discovers matching methods, so InitMod
    // can log exactly what got patched without duplicating the same assembly scan twice.
    internal static readonly List<string> AffectedAssemblyNames = new List<string>();

    // Deliberately does NOT use ModManager.GetFailedMods() - confirmed via a real server test that
    // it can return nothing at all even when a mod's own InitMod() visibly threw and was logged as
    // "Failed initializing ModAPI instance" one line earlier. Instead, this walks only the specific
    // assemblies the patch above actually found AedenthornUtils in (AffectedAssemblyNames), resolves
    // each one's owning Mod directly via ModManager.GetModForAssembly(), and retries unless BOTH
    // LoadState and ModLoaded() positively confirm it's already fine. Erring toward retrying is the
    // safe default here (idempotent - see the InitMod comment above), whereas erring toward NOT
    // retrying is exactly the failure mode that shipped last time.
    private static void RetryFailedAedenthornMods()
    {
        if (AffectedAssemblyNames.Count == 0)
        {
            Debug.Log(Prefix + " No assemblies were patched; nothing to check for retry.");
            return;
        }

        int checkedCount = 0;
        int retriedCount = 0;

        foreach (string assemblyName in AffectedAssemblyNames)
        {
            try
            {
                Assembly asm = FindLoadedAssemblyByName(assemblyName);
                if (asm == null)
                {
                    Debug.LogWarning(Prefix + " Could not re-find assembly '" + assemblyName + "' to check for retry.");
                    continue;
                }

                Mod mod = ModManager.GetModForAssembly(asm);
                if (mod == null)
                {
                    Debug.LogWarning(Prefix + " Could not resolve a Mod object for assembly '" + assemblyName + "'.");
                    continue;
                }
                checkedCount++;

                bool loaded = ModManager.ModLoaded(mod.Name);
                Debug.Log(Prefix + " '" + mod.Name + "': LoadState=" + mod.LoadState + ", ModLoaded=" + loaded + ".");

                if (mod.LoadState == Mod.EModLoadState.Success && loaded)
                {
                    // Positively confirmed fine - either it never hit the bug, or it happened to
                    // load before we did. Nothing to do.
                    continue;
                }

                Type modApiType = null;
                foreach (Type t in asm.GetTypes())
                {
                    if (typeof(IModApi).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    {
                        modApiType = t;
                        break;
                    }
                }
                if (modApiType == null)
                {
                    Debug.LogWarning(Prefix + " '" + mod.Name + "' isn't confirmed cleanly loaded, but no IModApi implementation was found to retry.");
                    continue;
                }

                IModApi modApiInstance = (IModApi)Activator.CreateInstance(modApiType);
                modApiInstance.InitMod(mod);
                retriedCount++;
                Debug.Log(Prefix + " Re-initialized '" + mod.Name + "' after patching its path lookup.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(Prefix + " Retry check for '" + assemblyName + "' failed: " + ex);
            }
        }

        Debug.Log(Prefix + " Retry pass complete: checked " + checkedCount + " affected mod(s), " + retriedCount + " retried.");
    }

    private static Assembly FindLoadedAssemblyByName(string name)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == name)
            {
                return asm;
            }
        }
        return null;
    }
}

// Bare [HarmonyPatch] + TargetMethods() lets this patch every matching method it finds across all
// currently loaded mod assemblies, rather than needing a compile-time reference to any specific
// mod's assembly (none of them are referenced by this project at all).
[HarmonyPatch]
internal static class AedenthornUtils_GetAssetPath_Patch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        List<MethodBase> targets = new List<MethodBase>();
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t;
            try
            {
                t = asm.GetType("AedenthornUtils");
            }
            catch
            {
                continue;
            }
            if (t == null)
            {
                continue;
            }

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetAssetPath" || m.ReturnType != typeof(string))
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool))
                {
                    targets.Add(m);
                    CraftFromContainersServerFixApi.AffectedAssemblyNames.Add(asm.GetName().Name);
                }
            }
        }
        return targets;
    }

    // Replaces the broken Assembly.Location-based lookup with the game's own already-correct
    // Mod.Path for whichever mod owns the method being patched (resolved fresh each call, since one
    // Prefix here may be shared across several different mods' copies of AedenthornUtils).
    private static bool Prefix(MethodBase __originalMethod, string name, bool create, ref string __result)
    {
        try
        {
            Assembly owningAssembly = __originalMethod.DeclaringType.Assembly;
            Mod owningMod = ModManager.GetModForAssembly(owningAssembly);
            string baseDir = (owningMod != null && !string.IsNullOrEmpty(owningMod.Path))
                ? owningMod.Path
                : AppDomain.CurrentDomain.BaseDirectory;

            string path = Path.Combine(baseDir, name);
            if (create && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            __result = path;
            return false; // skip the original (broken) implementation
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CraftFromContainersServerFix] GetAssetPath prefix failed, falling back to original implementation: " + ex);
            return true; // let the original run - no worse off than before this mod existed
        }
    }
}
