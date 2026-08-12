# Z_CraftFromContainersServerFix

A small, server-only compatibility patch. Not affiliated with the author of Craft From Containers
or any other Aedenthorn mod - this is a local fix for a specific dedicated-server crash.

## The problem

On this dedicated server (not on a local single-player client with the identical mod folder),
`CraftFromContainers` failed to initialize with:

```
ERR [MODS]     Failed initializing ModAPI instance on mod 'CraftFromContainers'
EXC Invalid path
  at System.IO.Path.GetDirectoryName (System.String path)
  at AedenthornUtils.GetAssetPath (System.String name, System.Boolean create)
  at AedenthornUtils.GetAssetPath (System.Object obj, System.Boolean create)
  at CraftFromContainers.CraftFromContainers.LoadConfig ()
  at CraftFromContainers.CraftFromContainers.InitMod (Mod modInstance)
```

Root cause: `AedenthornUtils.GetAssetPath` - a helper class Aedenthorn's mods share, each bundling
their own compiled copy of it - locates its own mod folder via
`Assembly.GetExecutingAssembly().Location`, then calls `Path.GetDirectoryName()` on it.
On this server's headless dedicated-server build, `Location` comes back as an empty string for
dynamically-loaded mod assemblies (a build-specific quirk, not a broken install or bad config) -
`Path.GetDirectoryName("")` throws `ArgumentException: Invalid path`, which crashes the affected
mod's `InitMod()` before it does anything else. Since that's the very first substantive call inside
`InitMod()`, the mod's actual feature (Harmony patches, config, everything) never gets wired up -
this is a full outage for that mod, not a cosmetic log line.

Confirmed effect is server-only: the identical mod folder loads fine in a local single-player
client.

## The fix

This mod's `InitMod()` does two things:

1. Harmony-patches every `AedenthornUtils.GetAssetPath(string, bool)` it finds across **all**
   currently loaded mod assemblies (not just `CraftFromContainers` - several other mods on this
   server look like they may share the same Aedenthorn-authored helper class), replacing the
   broken `Assembly.Location` lookup with the game's own already-correct `Mod.Path` - `ModManager`
   logs the right path for every mod itself, right before the crash, so this just uses that instead
   of asking the broken helper to re-derive it.
2. Mod load order on this server is **not** alphabetical (confirmed from the server log - actual
   order was `TFP_Harmony, Gears, Quartz, CraftFromContainers, Ocb*, ...`), so this patch isn't
   guaranteed to apply before the affected mod's own `InitMod()` runs first. If any mod already
   failed specifically because its assembly contains `AedenthornUtils`, this retries that mod's
   `InitMod()` once, now that the lookup is fixed. Safe to retry: the crash happens on the very
   first meaningful call inside the affected mod's `LoadConfig()`, before any state is set up, so
   there's nothing partial left behind to clean up first.

## Install

**Server-side only.** Build with `build.ps1` (same pattern as the other mods in this collection) and
copy the whole `Z_CraftFromContainersServerFix` folder into your **dedicated server's** `Mods`
directory. Player clients don't need it - the bug doesn't occur there.

```powershell
.\build.ps1 -GamePath "<path to your 7 Days To Die client install>"
```

Built against the client's `Assembly-CSharp.dll`/`0Harmony.dll` as a stand-in for the dedicated
server's own (not directly accessible from this machine) - same game version, so this should match,
but worth confirming via the server log after deploying.
