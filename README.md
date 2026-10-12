# Silverpine Guard System

`SilverpineGuardSystem` is a BepInEx plugin that expands Silverpine's built-in
`NeuralNPC` `call_guards` function.

Current version: **1.3.5**.

Version 1.3.5 corrects witnessed-crime confirmation: a responder already handling
another incident, or prevented from accepting the response routine, no longer
reports success. A confirmed report requires an actually installed new response
routine; failed reports create no pending incident or success notification.

### Reporting witnessed crimes from other mods

Version 1.3.4 adds the public, synchronous, main-thread API
`Plugin.TryReportWitnessedCrime(NeuralNPC caller, string reason, Vector2Int incident)`.
The reason completes the phrase "the player ...". In 1.3.5, it returns true only
when a configured guard accepts a new response routine, then records the pending
incident for the existing fine/jail/release flow. Busy guards keep their current
incident and return false; rejected routines also return false without recording
an incident or displaying a successful guard call. A guard witness may respond
directly. It does not ask the
dialogue model for a reason, end unrelated dialogue, or broadcast a crime rumor.
The API respects the active save's guard roster and supported Silverpine area.

Breaking and Entering has used this API for visible break-in attempts since
1.1.0. Pair its 1.2.1 update with Guard System 1.3.5 for accurate confirmation of
new reports. Other guard calls through dialogue retain their existing behavior.

## Features

- Adds a large, resolution-aware **Guard & Jail Manager** to Modding Tools'
  inventory **Mods** tab. It scales by screen height, fits itself within narrow
  displays, and uses auto-sized labels to avoid clipped controls.
- Opens NPC guard designations in a separate searchable, scrollable interface.
  It shows all currently loaded base and custom `NeuralNPC`s in a responsive
  one-, two-, or three-column roster, can filter guards or civilians, and
  toggles each NPC directly from the list.
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
- Supports any number of independent jail cells. Each cell has its own exact
  teleport destination and its own connected set of doors. The selected cell
  is preferred for a new prisoner; if it is unavailable, the first configured,
  loaded, unoccupied cell is used. Cell selection is never random.
- Adds, cycles, and safely removes jail cells from the manager. A cell cannot
  be removed while occupied or while one of its connected doors is unloaded.
- Connects or disconnects a loaded door from the currently selected jail cell
  by clicking it in the world. A door can belong to only one cell.
- Can designate the player as a guard. Every normal NPC conversation then
  receives **Guard: Accuse of Lawbreaking** in its action menu.
- The player writes the alleged law and the accused NPC responds naturally in
  the continuing conversation. The accusation action is then replaced by
  **Guard: Decide Punishment**, allowing the player to keep questioning the NPC
  before choosing an outcome.
- Locks the conversation's **Leave** action while an accusation awaits a legal
  outcome. Fining or jailing ends the case; releasing every accused NPC unlocks
  Leave and continues the conversation with a response to the release.
- In multi-NPC dialogue, the player selects one or more defendants before
  writing the law. Every selected NPC responds in sequence, and the punishment
  screen supports an independent Fine, Jail, or Release outcome for each one.
  Selecting Fine charges the NPC immediately without an affordability check;
  NPC imprisonment happens only when the player explicitly selects Jail.
- Law entry uses the active conversation's Talk field, keeping the prompt
  visible while the NPC dialogue is open instead of opening Silverpine's hidden
  generic text-entry window after the conversation ends.
- Jailed NPCs are teleported into their assigned cell, have their previous
  routine stack halted, and are held by a dedicated jail routine. New override
  routines are rejected until their independently tracked sentence expires.
- A guard-enabled player can use **Guard: Release Prisoner Early** while
  speaking with a jailed NPC. Multi-NPC conversations provide a named release
  action for each prisoner. Early release restores normal routines immediately
  and opens the cell when it is no longer occupied.
- Converts the fine into jail time using a configurable gold-per-hour rate, or
  allows a configurable fixed sentence instead. It closes and locks every
  designated jail door for the resulting sentence, then unlocks and opens the
  doors automatically.
- Suppresses the base door `publicAccess` exception during an active sentence.
  Prisoners and civilians cannot open the cell from either side, while a
  guard-enabled player can open it; the door closes and locks after the guard
  passes through and clears the doorway.
- Immediately ends and cleans up the responding guard conversation when the
  player accepts jail.
- Does not broadcast the initial accusation or guard call as a rumor. It also
  suppresses Silverpine's automatic conversation rumor for justice resolutions.
  One explicit Silverpine-wide outcome rumor is created only when the player or
  an NPC is actually fined or jailed; an innocent release creates no rumor.
- Stores NPC/player guard designations, independent cell destinations and door
  links, fine settings, and player/NPC active sentences per save.
- Ignores `SaveUI`'s placeholder main-menu run ID, so opening or quitting from
  the main menu does not create a phantom save configuration, including during
  the early startup frames before `MainMenuUI` exists. Version 1.1 performs a
  one-time cleanup of untouched legacy entries only when their IDs cannot be
  found in any real Silverpine save, preserving default configurations that do
  belong to actual saves.
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
