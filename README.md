# Silverpine Guard System

`SilverpineGuardSystem` is a BepInEx plugin that expands Silverpine's built-in
`NeuralNPC` `call_guards` function.

## Features

- Adds **Guard & Jail Manager** to Modding Tools' inventory **Mods** tab.
- Designates any currently loaded base or custom `NeuralNPC` as a guard.
- Dispatches the closest loaded designated guard to the player's incident tile
  when an NPC invokes `call_guards`, instead of hard-coding Darian as the only
  responder.
  The plugin replaces each loaded NPC's base call-guard function with its own
  dispatcher, so this does not depend solely on patching the base async call.
- Lets the player explicitly choose **Pay Fine** or **Accept Jail** while a
  responding guard has detained them. If Silverpine's original Darian fine
  function is selected, the plugin intercepts it before gold changes and
  displays the same blocking fine-or-jail choice prompt.
- Uses Silverpine's original AI-driven fine-price determination by default.
  The manager can instead select a fixed fine override from 10 to 1,000 gold.
- Automatically arrests a player who cannot afford the determined fine rather
  than reducing their gold below zero.
- Preserves Darian's AI-driven **let go without a fine** outcome and gives
  custom designated guards an equivalent function. A guard may use it when
  genuinely persuaded that the player committed no crime.
- Toggles any loaded door as a jail door by clicking it in the world.
- Selects the exact passable world tile used as the jail teleport destination.
- Converts the fine into jail time using a configurable gold-per-hour rate, or
  allows a configurable fixed sentence instead. It closes and locks every
  designated jail door for the resulting sentence, then unlocks and opens the
  doors automatically.
- Suppresses the base door `publicAccess` exception during an active sentence,
  so the jail cannot be opened from either side until release.
- Immediately ends and cleans up the responding guard conversation when the
  player accepts jail.
- Stores guard, jail-door, jail-tile, fine, sentence, and active-sentence state
  per save.
- Ignores `SaveUI`'s placeholder main-menu run ID, so opening or quitting from
  the main menu does not create a phantom save configuration.
- Keeps its runtime service alive across menu/scene transitions and does not
  tear down Harmony patches from `OnDestroy`.

Darian is the default guard for a save the first time the plugin sees it, so
the base behavior remains available until changed in the manager.

## Requirements

- BepInEx 5
- Silverpine Modding Tools Menu

## Build

```powershell
dotnet build .\SilverpineGuardSystem.csproj -c Release
```

Copy `bin/Release/netstandard2.1/SilverpineGuardSystem.dll` into a BepInEx
plugin directory alongside a loadable `ModdingTools.dll`.

Per-save configuration is written to:

```text
BepInEx/config/SilverpineGuardSystem/guards.json
```

## Credits

Created by **Saelac and ChatGPT**.
