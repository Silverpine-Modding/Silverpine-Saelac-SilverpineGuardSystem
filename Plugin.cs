#nullable enable

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace SilverpineGuardSystem;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "renegadex.silverpine.guardsystem";
    public const string PluginName = "Silverpine Guard System";
    public const string PluginVersion = "1.0.6";

    private static readonly FieldInfo RunIdField =
        AccessTools.Field(typeof(SaveUI), "runID");
    private static readonly FieldInfo StaticIdField =
        AccessTools.Field(typeof(StaticIdentifier), "id");
    private static readonly MethodInfo DoorCloseMethod =
        AccessTools.Method(typeof(Door), "Close");
    private static readonly MethodInfo DoorOpenMethod =
        AccessTools.Method(typeof(Door), "Open");

    internal static Plugin Instance = null!;
    internal static ManualLogSource Log = null!;

    private GuardDatabase database = new();
    private GuardSave? currentSave;
    private string currentRunId = "";
    private Harmony harmony = null!;
    private float nextMaintenanceTime;
    private TaskCompletionSource<bool>? activeJusticePrompt;
    private NeuralNPC? activeJusticeGuard;
    private Task? fineOrJailOfferTask;
    private int activePromptFineAmount = 100;

    internal bool HasActiveSave => currentSave != null;

    private string DatabasePath => Path.Combine(
        Paths.ConfigPath,
        "SilverpineGuardSystem",
        "guards.json");

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        DontDestroyOnLoad(gameObject);
        LoadDatabase();

        InventoryModTools.RegisterSession(
            PluginGuid + ".manager",
            "Guard & Jail Manager",
            GuardManagerWindow.Open,
            order: 310);

        harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        SerializationManager.OnFinishedLoadingSave -= HandleFinishedLoadingSave;
        SerializationManager.OnFinishedLoadingSave += HandleFinishedLoadingSave;
        Log.LogInfo("Registered Guard & Jail Manager in the ModTools inventory tab.");
    }

    private void Update()
    {
        if (Time.unscaledTime < nextMaintenanceTime)
            return;

        nextMaintenanceTime = Time.unscaledTime + 0.5f;
        RefreshSaveIdentity();
        ApplyGuardMarkers();
        MaintainSentence();
    }

    private void OnApplicationQuit()
    {
        CompleteJusticePrompt();
        GuardManagerWindow.CloseCurrent();
        if (database.saves.Count > 0)
            SaveDatabase();
    }

    private static void HandleFinishedLoadingSave()
    {
        if (ReferenceEquals(Instance, null))
            return;

        if (ActionQueue.Instance != null)
        {
            ActionQueue.Instance.DoAfterXFrames(3, () =>
            {
                Instance.RefreshSaveIdentity(force: true);
                Instance.ApplyGuardMarkers();
                Instance.MaintainSentence();
            });
        }
        else
        {
            Instance.RefreshSaveIdentity(force: true);
        }
    }

    internal void RefreshSaveIdentity(bool force = false)
    {
        if (MainMenuUI.Instance != null && MainMenuUI.Instance.IsOpen())
        {
            currentRunId = "";
            currentSave = null;
            return;
        }

        if (SaveUI.Instance == null || RunIdField == null)
        {
            currentRunId = "";
            currentSave = null;
            return;
        }

        string runId = RunIdField.GetValue(SaveUI.Instance) as string ?? "";
        if (string.IsNullOrWhiteSpace(runId))
        {
            currentRunId = "";
            currentSave = null;
            return;
        }

        if (!force && runId == currentRunId && currentSave != null)
            return;

        currentRunId = runId;
        if (!database.saves.TryGetValue(runId, out GuardSave save))
        {
            save = GuardSave.CreateDefault();
            database.saves[runId] = save;
            SaveDatabase();
        }

        save.Normalize();
        currentSave = save;
    }

    internal IReadOnlyList<NeuralNPC> GetLoadedNpcs()
    {
        if (NeuralNPC.neuralNPCs == null)
            return Array.Empty<NeuralNPC>();

        return NeuralNPC.neuralNPCs.Values
            .Where(npc => npc != null)
            .OrderBy(npc => npc.GetFinalName(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string GetNpcKey(NeuralNPC npc) =>
        npc.IsCustomNPC()
            ? "custom:" + npc.GetFinalName()
            : npc.npcName.ToString();

    internal bool IsConfiguredGuard(NeuralNPC? npc)
    {
        return npc != null
            && currentSave != null
            && Contains(currentSave.guardKeys, GetNpcKey(npc));
    }

    internal void ToggleGuard(NeuralNPC npc)
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        string key = GetNpcKey(npc);
        if (!Remove(currentSave.guardKeys, key))
            currentSave.guardKeys.Add(key);
        else
            StopGuardResponse(npc);

        ApplyGuardMarker(npc);
        SaveDatabase();
    }

    private void ApplyGuardMarkers()
    {
        if (currentSave == null || NeuralNPC.neuralNPCs == null)
            return;

        foreach (NeuralNPC npc in NeuralNPC.neuralNPCs.Values.Where(n => n != null))
        {
            EnsureExpandedCallGuardsFunction(npc);
            ApplyGuardMarker(npc);
        }
    }

    internal static void EnsureExpandedCallGuardsFunction(NeuralNPC npc)
    {
        if (npc.npcFunctions.Any(function =>
                function is ExpandedCallGuardsFunction))
        {
            return;
        }

        int index = npc.npcFunctions.FindIndex(function =>
            function != null
            && function.GetType() == typeof(NPCFunction_CallGuards));
        if (index < 0)
            return;

        npc.npcFunctions[index] = new ExpandedCallGuardsFunction
        {
            owner = npc
        };
    }

    private void ApplyGuardMarker(NeuralNPC npc)
    {
        GuardJusticeDescription? marker =
            npc.GetComponent<GuardJusticeDescription>();
        if (IsConfiguredGuard(npc))
        {
            if (marker == null)
                npc.gameObject.AddComponent<GuardJusticeDescription>();
            if (!npc.npcFunctions.Any(function =>
                    function is GuardJusticeChoiceFunction))
            {
                npc.AddNPCFunction(new GuardJusticeChoiceFunction());
            }
            if (!npc.npcFunctions.Any(function =>
                    function is CustomNeuralFunctionCallFine.NPCFunction_LetGo
                    || function is GuardLetGoWithoutFineFunction))
            {
                npc.AddNPCFunction(new GuardLetGoWithoutFineFunction());
            }
        }
        else if (marker != null)
        {
            Destroy(marker);
            npc.RemoveNPCFunctionOfType<GuardJusticeChoiceFunction>();
            npc.RemoveNPCFunctionOfType<GuardLetGoWithoutFineFunction>();
        }
    }

    internal bool IsCallGuardAvailable(NeuralNPC caller)
    {
        if (currentSave == null
            || DialogBox.Instance == null
            || !DialogBox.Instance.IsEndDialogAllowed(caller))
        {
            return false;
        }

        if (!PersistenceZone.IsNPCInsidePersistenceZone(
                PersistenceZoneName.Silverpine,
                caller.npcName))
        {
            return false;
        }

        return GetRespondingGuards(caller).Count > 0;
    }

    private List<NeuralNPC> GetRespondingGuards(NeuralNPC caller)
    {
        if (NeuralNPC.neuralNPCs == null)
            return new List<NeuralNPC>();

        Vector2Int incident = Player.Instance != null
            ? Player.Instance.transform.GetVector2IntPosition()
            : caller.transform.GetVector2IntPosition();
        return NeuralNPC.neuralNPCs.Values
            .Where(npc => npc != null
                && npc != caller
                && npc.gameObject.activeInHierarchy
                && IsConfiguredGuard(npc)
                && npc.GetComponent<NPCRoutineExecutor>() != null)
            .OrderBy(npc => ManhattanDistance(
                npc.transform.GetVector2IntPosition(),
                incident))
            .ThenBy(
                npc => npc.GetFinalName(),
                StringComparer.OrdinalIgnoreCase)
            .Take(1)
            .ToList();
    }

    private static int ManhattanDistance(Vector2Int first, Vector2Int second) =>
        Math.Abs(first.x - second.x) + Math.Abs(first.y - second.y);

    internal async Task InvokeCallGuards(NeuralNPC caller)
    {
        try
        {
            string reason = await caller.AskQuestion(
                "What is " + caller.GetFinalName() + " calling the guards on "
                + Player.Instance.playerName + " for?",
                deterministic: false,
                -1,
                "root ::= [A-Za-z0-9 .,!?'-]*",
                caller.GetFinalName() + " is calling the guards on "
                + Player.Instance.playerName + " because "
                + Player.Instance.playerName + " ");
            if (InferenceServerSetupHandler.Instance.apiMode)
            {
                reason = APIModelHandler.EnforceSimpleTextGrammar(
                    reason,
                    trimNewLine: true);
            }

            reason = reason.Trim().TrimEnd('.');
            List<NeuralNPC> guards = GetRespondingGuards(caller);
            Log.LogInfo(
                "Guard call from " + caller.GetFinalName()
                + " dispatching closest loaded guard: "
                + (guards.Count == 0
                    ? "none"
                    : string.Join(", ", guards.Select(
                        guard => guard.GetFinalName()))));
            foreach (NeuralNPC guard in guards)
            {
                string activity = "responding to " + caller.GetFinalName()
                    + " calling the guards because " + Player.Instance.playerName
                    + " " + reason;
                guard.GetComponent<NPCRoutineExecutor>().StartOverrideRoutine(
                    new NPCRoutine(
                        "on " + guard.Pronouns.His() + " way " + activity,
                        activity,
                        activity,
                        Player.Instance.transform.GetVector2IntPosition(),
                        new RoutineArgument_GuardResponse()));
                Log.LogInfo(
                    "Started guard-response routing for "
                    + guard.GetFinalName() + ".");
            }

            AnimationHelper.Instance.SpawnFloatingText(
                "<color=\"red\">Called the guards!</color>",
                caller.transform);
            NeuralNPC.AddGlobalMemory(
                caller.GetFinalName() + " called the guards on "
                + Player.Instance.playerName + " because "
                + Player.Instance.playerName + " " + reason + ".",
                npc => npc != caller && npc.faction == Faction.Silverpine);
            DialogBox.Instance.QueueDialogEnd();
        }
        catch (Exception exception)
        {
            Log.LogError("Expanded call_guards failed: " + exception);
            UpperNotificationUI.Instance?.OneOff(
                "The guards could not answer the call.");
        }
    }

    internal bool IsManagedGuardResponse(NeuralNPC? npc)
    {
        if (!IsConfiguredGuard(npc) || npc == null)
            return false;

        NPCRoutineExecutor executor = npc.GetComponent<NPCRoutineExecutor>();
        return executor != null
            && executor.currentRoutine != null
            && executor.currentRoutine
                .GetRoutineArgument<RoutineArgument_GuardResponse>() != null;
    }

    internal bool IsConfiguredGuardDialog(NeuralNPC? npc)
    {
        return npc != null
            && IsConfiguredGuard(npc)
            && DialogBox.Instance != null
            && DialogBox.Instance.isOpen
            && NeuralNPC.currentActiveDialogNeuralNPC == npc;
    }

    internal int FineAmount => currentSave?.fineAmount ?? 100;

    internal bool UsesBaseGameFine => currentSave?.useBaseGameFine ?? true;

    internal string FineModeLabel => UsesBaseGameFine
        ? "Base game determines"
        : FineAmount + " gold override";

    internal bool UsesFineBasedSentence =>
        currentSave?.useFineBasedSentence ?? true;

    internal int FixedSentenceHours => currentSave?.fixedSentenceHours ?? 24;

    internal int GoldPerJailHour => currentSave?.goldPerJailHour ?? 5;

    internal string SentenceModeLabel => UsesFineBasedSentence
        ? "Fine-based"
        : "Fixed";

    internal string SentenceValueLabel => UsesFineBasedSentence
        ? GoldPerJailHour + " gold = 1 hour"
        : FixedSentenceHours + " hours";

    internal void SetFineAmount(int amount)
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.useBaseGameFine = false;
        currentSave.fineAmount = Mathf.Clamp(amount, 1, 1000);
        SaveDatabase();
    }

    internal void UseBaseGameFine()
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.useBaseGameFine = true;
        SaveDatabase();
    }

    internal void ToggleSentenceMode()
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.useFineBasedSentence = !currentSave.useFineBasedSentence;
        SaveDatabase();
    }

    internal void SetGoldPerJailHour(int amount)
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.goldPerJailHour = Mathf.Clamp(amount, 1, 1000);
        SaveDatabase();
    }

    internal void SetFixedSentenceHours(int hours)
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.fixedSentenceHours = Mathf.Clamp(hours, 1, 1000);
        SaveDatabase();
    }

    internal int GetSentenceHours(int fineAmount)
    {
        if (!UsesFineBasedSentence)
            return FixedSentenceHours;

        return Math.Max(
            1,
            Mathf.CeilToInt(Math.Max(0, fineAmount) / (float)GoldPerJailHour));
    }

    internal void PayFine(NeuralNPC guard)
    {
        if (!IsConfiguredGuardDialog(guard) || Player.Instance == null)
            return;

        int amount = activeJusticePrompt != null
            ? activePromptFineAmount
            : FineAmount;
        int availableGold = Player.Instance.worldInventory.inventory.gold;
        if (availableGold < amount)
        {
            if (!IsJailReady(out string unavailable))
            {
                UpperNotificationUI.Instance.OneOff(
                    "You cannot afford the " + amount
                    + " gold fine. Arrest cannot begin: " + unavailable);
                ShowFineOrJailPrompt(guard);
                return;
            }

            UpperNotificationUI.Instance.OneOff(
                "You cannot afford the " + amount
                + " gold fine. You are being arrested.");
            AcceptJail(guard, forcedByInsufficientFunds: true);
            return;
        }

        Player.Instance.worldInventory.inventory.gold -= amount;

        AnimationHelper.Instance.SpawnFloatingGoldText(
            -amount,
            Player.Instance.transform);
        guard.AddThingThatHappened(
            Player.Instance.playerName + " paid a " + amount
            + " gold fine and was released by " + guard.GetFinalName() + ".");
        StopOtherGuardResponses(guard);
        CompleteJusticePrompt();
        DialogBox.Instance.QueueDialogEnd();
    }

    internal Task OfferFineOrJail(NeuralNPC guard)
    {
        if (!IsConfiguredGuardDialog(guard))
            return Task.CompletedTask;

        if (activeJusticePrompt != null)
            return activeJusticePrompt.Task;

        fineOrJailOfferTask ??= CreateFineOrJailOffer(guard);
        return fineOrJailOfferTask;
    }

    private async Task CreateFineOrJailOffer(NeuralNPC guard)
    {
        try
        {
            activePromptFineAmount = await DetermineFineAmount(guard);
            if (!IsConfiguredGuardDialog(guard))
                return;

            activeJusticeGuard = guard;
            activeJusticePrompt = new TaskCompletionSource<bool>();
            int availableGold = Player.Instance.worldInventory.inventory.gold;
            if (availableGold < activePromptFineAmount
                && IsJailReady(out _))
            {
                Log.LogInfo(
                    Player.Instance.playerName + " has " + availableGold
                    + " gold and cannot afford the " + activePromptFineAmount
                    + " gold fine; arresting the player.");
                Task promptTask = activeJusticePrompt.Task;
                AcceptJail(guard, forcedByInsufficientFunds: true);
                await promptTask;
                return;
            }

            Log.LogInfo(
                "Intercepted automatic justice resolution from "
                + guard.GetFinalName() + "; waiting for the player's "
                + activePromptFineAmount + " gold fine or jail choice.");
            ShowFineOrJailPrompt(guard);
            await activeJusticePrompt.Task;
        }
        finally
        {
            fineOrJailOfferTask = null;
        }
    }

    private async Task<int> DetermineFineAmount(NeuralNPC guard)
    {
        if (!UsesBaseGameFine)
            return FineAmount;

        string answer = await guard.AskQuestion(
            "How much is " + guard.GetFinalName() + " fining "
            + Player.Instance.playerName + " for? Answer with just the cost "
            + "of the fine. 100 if not specified.",
            deterministic: true,
            6,
            "root ::= \"0\" | non-zero-number | \"1000\"\n"
            + "non-zero-number ::= [1-9] [0-9]{0,2}");
        int amount = Mathf.Abs(
            APIModelHandler.HandleNoGrammarInt(answer, 100));
        return Mathf.Clamp(amount, 0, 1000);
    }

    internal Task LetGoWithoutFine(NeuralNPC guard)
    {
        if (!IsConfiguredGuardDialog(guard))
            return Task.CompletedTask;

        guard.AddThingThatHappened(
            guard.GetFinalName() + " concluded that "
            + Player.Instance.playerName
            + " had not committed a crime and released "
            + Player.Instance.pronouns.Him() + " without a fine.");
        Log.LogInfo(
            guard.GetFinalName()
            + " released the player without a fine after the guard dialogue.");
        StopOtherGuardResponses(guard);
        CompleteJusticePrompt();
        DialogBox.Instance.QueueDialogEnd();
        DialogBox.Instance.EndDialog();
        return Task.CompletedTask;
    }

    private void ShowFineOrJailPrompt(NeuralNPC guard)
    {
        if (activeJusticePrompt == null
            || activeJusticeGuard != guard
            || DialogBox.Instance == null)
        {
            return;
        }

        bool jailReady = IsJailReady(out string unavailableReason);
        int fineAmount = activePromptFineAmount;
        int sentenceHours = GetSentenceHours(fineAmount);
        DialogOption pay = new(
            "Pay Fine (" + fineAmount + " gold)",
            () => PayFine(guard),
            endDialog: false);
        DialogOption jail = new(
            jailReady
                ? "Accept Jail (" + sentenceHours + " hours)"
                : "Jail Unavailable",
            () =>
            {
                if (!IsJailReady(out string reason))
                {
                    UpperNotificationUI.Instance.OneOff(reason);
                    ShowFineOrJailPrompt(guard);
                    return;
                }
                AcceptJail(guard);
            },
            endDialog: false);

        string jailText = jailReady
            ? "serve " + sentenceHours + " game hours in jail"
            : "jail is unavailable: " + unavailableReason;
        DialogBox.Instance.DisplayTextNoDialog(
            guard.GetFinalName() + " has detained you. Choose your penalty: "
            + "pay the " + fineAmount + " gold fine or " + jailText + ". "
            + "If you cannot afford the fine, you will be arrested.",
            pay,
            jail);
    }

    private void CompleteJusticePrompt()
    {
        TaskCompletionSource<bool>? prompt = activeJusticePrompt;
        activeJusticePrompt = null;
        activeJusticeGuard = null;
        activePromptFineAmount = FineAmount;
        prompt?.TrySetResult(true);
    }

    internal bool IsJailReady(out string reason)
    {
        reason = "";
        if (currentSave == null)
        {
            reason = "No loaded save is active.";
            return false;
        }
        if (currentSave.jailDoorKeys.Count == 0)
        {
            reason = "No jail doors are designated.";
            return false;
        }
        if (!currentSave.hasJailCell)
        {
            reason = "No jail destination tile is selected.";
            return false;
        }

        Vector2Int cell = new(currentSave.jailCellX, currentSave.jailCellY);
        if (!Turfs.IsValidTurf(cell))
        {
            reason = "The selected jail tile is not currently loaded.";
            return false;
        }
        if (TurfCollider.HasImpassableCollider(cell))
        {
            reason = "The selected jail tile is blocked.";
            return false;
        }
        if (GetConfiguredLoadedDoors().Count == 0)
        {
            reason = "No designated jail door is currently loaded.";
            return false;
        }
        return true;
    }

    internal void AcceptJail(
        NeuralNPC guard,
        bool forcedByInsufficientFunds = false)
    {
        if (!IsConfiguredGuardDialog(guard)
            || currentSave == null
            || WorldInfoManager.Instance == null)
        {
            return;
        }

        if (!IsJailReady(out string reason))
        {
            UpperNotificationUI.Instance.OneOff(reason);
            return;
        }

        Vector2Int destination = new(
            currentSave.jailCellX,
            currentSave.jailCellY);
        int fineAmount = activeJusticePrompt != null
            ? activePromptFineAmount
            : FineAmount;
        int sentenceHours = GetSentenceHours(fineAmount);
        int sentenceTurns = sentenceHours * WorldInfoManager.HOUR_TURN_LENGTH;
        currentSave.releaseTurn =
            WorldInfoManager.Instance.TotalTurnCount + sentenceTurns;
        currentSave.pendingReleaseDoorKeys.Clear();
        Player.Instance.entityMover.Teleport(destination);
        CloseAndLockConfiguredDoors();
        guard.AddThingThatHappened(
            Player.Instance.playerName
            + (forcedByInsufficientFunds
                ? " could not afford the " + fineAmount
                    + " gold fine and was arrested for "
                : " accepted a ")
            + sentenceHours + " hour jail sentence"
            + (forcedByInsufficientFunds
                ? "."
                : " instead of paying the " + fineAmount + " gold fine."));
        StopOtherGuardResponses(guard);
        CompleteJusticePrompt();
        SaveDatabase();
        UpperNotificationUI.Instance.OneOff(
            (forcedByInsufficientFunds
                ? "Unable to pay the " + fineAmount
                    + " gold fine. Arrested for "
                : "Jailed for ")
            + sentenceHours
            + " game hours. The jail will unlock automatically.");
        DialogBox.Instance.QueueDialogEnd();
        DialogBox.Instance.EndDialog();
    }

    internal bool IsJailDoorLockedDown(Door door)
    {
        RefreshSaveIdentity();
        if (currentSave == null || currentSave.releaseTurn < 0)
            return false;

        if (WorldInfoManager.Instance != null
            && WorldInfoManager.Instance.TotalTurnCount
                >= currentSave.releaseTurn)
        {
            ReleasePrisoner();
            return false;
        }

        return Contains(currentSave.jailDoorKeys, GetDoorKey(door));
    }

    private void MaintainSentence()
    {
        if (currentSave == null || WorldInfoManager.Instance == null)
            return;

        if (currentSave.releaseTurn >= 0)
        {
            if (WorldInfoManager.Instance.TotalTurnCount
                >= currentSave.releaseTurn)
            {
                ReleasePrisoner();
            }
            else
            {
                CloseAndLockConfiguredDoors();
            }
        }
        ApplyPendingDoorReleases();
    }

    private void ReleasePrisoner()
    {
        if (currentSave == null)
            return;

        currentSave.releaseTurn = -1;
        currentSave.pendingReleaseDoorKeys =
            new List<string>(currentSave.jailDoorKeys);
        ApplyPendingDoorReleases();
        SaveDatabase();
        UpperNotificationUI.Instance?.OneOff(
            "Your sentence is complete. The jail is open.");
    }

    private void CloseAndLockConfiguredDoors()
    {
        foreach (Door door in GetConfiguredLoadedDoors())
        {
            try
            {
                DoorCloseMethod.Invoke(
                    door,
                    new object[] { door.gameObject, true });
                door.isLocked = true;
            }
            catch (Exception exception)
            {
                Log.LogWarning(
                    "Could not close jail door " + GetDoorDisplayName(door)
                    + ": " + exception.Message);
            }
        }
    }

    private void ApplyPendingDoorReleases()
    {
        if (currentSave == null
            || currentSave.pendingReleaseDoorKeys.Count == 0)
        {
            return;
        }

        List<string> pending =
            new(currentSave.pendingReleaseDoorKeys);
        HashSet<string> released =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (Door door in Door.allDoors.Where(door => door != null).ToList())
        {
            string key = GetDoorKey(door);
            if (!Contains(pending, key))
                continue;

            UnlockAndOpenDoor(door);
            released.Add(key);
        }

        foreach (string key in released)
            Remove(currentSave.pendingReleaseDoorKeys, key);

        if (currentSave.pendingReleaseDoorKeys.Count == 0)
            SaveDatabase();
    }

    private static void UnlockAndOpenDoor(Door door)
    {
        try
        {
            door.isLocked = false;
            DoorOpenMethod.Invoke(
                door,
                new object[] { door.gameObject, true });
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Could not open released jail door "
                + GetDoorDisplayName(door) + ": " + exception.Message);
        }
    }

    internal int RemainingSentenceHours
    {
        get
        {
            if (currentSave == null
                || currentSave.releaseTurn < 0
                || WorldInfoManager.Instance == null)
            {
                return 0;
            }

            int turns = Math.Max(
                0,
                currentSave.releaseTurn
                    - WorldInfoManager.Instance.TotalTurnCount);
            return Mathf.CeilToInt(
                turns / (float)WorldInfoManager.HOUR_TURN_LENGTH);
        }
    }

    internal bool IsJailDoor(Door door)
    {
        return currentSave != null
            && Contains(currentSave.jailDoorKeys, GetDoorKey(door));
    }

    internal int JailDoorCount => currentSave?.jailDoorKeys.Count ?? 0;

    internal void ToggleJailDoor(Door door)
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        string key = GetDoorKey(door);
        if (Remove(currentSave.jailDoorKeys, key))
        {
            Remove(currentSave.pendingReleaseDoorKeys, key);
            UnlockAndOpenDoor(door);
        }
        else
        {
            currentSave.jailDoorKeys.Add(key);
            if (currentSave.releaseTurn >= 0)
                CloseAndLockConfiguredDoors();
        }
        SaveDatabase();
    }

    internal void SetJailCell(Vector2Int position)
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.hasJailCell = true;
        currentSave.jailCellX = position.x;
        currentSave.jailCellY = position.y;
        SaveDatabase();
    }

    internal bool TryGetJailCell(out Vector2Int position)
    {
        position = default;
        if (currentSave == null || !currentSave.hasJailCell)
            return false;

        position = new Vector2Int(
            currentSave.jailCellX,
            currentSave.jailCellY);
        return true;
    }

    private List<Door> GetConfiguredLoadedDoors()
    {
        if (currentSave == null)
            return new List<Door>();

        return Door.allDoors
            .Where(door => door != null
                && Contains(currentSave.jailDoorKeys, GetDoorKey(door)))
            .ToList();
    }

    internal static string GetDoorKey(Door door)
    {
        StaticIdentifier identifier = door.GetComponent<StaticIdentifier>();
        string id = identifier != null
            ? StaticIdField.GetValue(identifier) as string ?? ""
            : "";
        if (!string.IsNullOrWhiteSpace(id))
            return "static:" + id;

        Vector2Int position = door.transform.GetVector2IntPosition();
        return "tile:" + position.x + "," + position.y;
    }

    internal static string GetDoorDisplayName(Door door)
    {
        Vector2Int position = door.transform.GetVector2IntPosition();
        string label = string.IsNullOrWhiteSpace(door.designation)
            ? "Door"
            : door.designation;
        return label + " at " + position.x + ", " + position.y;
    }

    private static void StopOtherGuardResponses(NeuralNPC activeGuard)
    {
        if (NeuralNPC.neuralNPCs == null)
            return;

        foreach (NeuralNPC guard in NeuralNPC.neuralNPCs.Values
                     .Where(npc => npc != null && npc != activeGuard))
        {
            StopGuardResponse(guard);
        }
    }

    private static void StopGuardResponse(NeuralNPC guard)
    {
        NPCRoutineExecutor executor = guard.GetComponent<NPCRoutineExecutor>();
        if (executor == null || executor.currentRoutine == null)
            return;
        if (executor.currentRoutine
            .GetRoutineArgument<RoutineArgument_GuardResponse>() == null)
        {
            return;
        }
        executor.StopOverrideRoutine(executor.currentRoutine);
    }

    private void LoadDatabase()
    {
        try
        {
            if (!File.Exists(DatabasePath))
                return;
            database = JsonConvert.DeserializeObject<GuardDatabase>(
                File.ReadAllText(DatabasePath)) ?? new GuardDatabase();
            database.Normalize();
        }
        catch (Exception exception)
        {
            database = new GuardDatabase();
            Log.LogError("Could not load guard configuration: " + exception);
        }
    }

    private void SaveDatabase()
    {
        try
        {
            string? directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                DatabasePath,
                JsonConvert.SerializeObject(database, Formatting.Indented));
        }
        catch (Exception exception)
        {
            Log.LogError("Could not save guard configuration: " + exception);
        }
    }

    private static bool Contains(IEnumerable<string> values, string value) =>
        values.Any(item => string.Equals(
            item,
            value,
            StringComparison.OrdinalIgnoreCase));

    private static bool Remove(ICollection<string> values, string value)
    {
        string? existing = values.FirstOrDefault(item => string.Equals(
            item,
            value,
            StringComparison.OrdinalIgnoreCase));
        return existing != null && values.Remove(existing);
    }
}

internal sealed class GuardJusticeDescription :
    MonoBehaviour,
    IAdditionalCharacterDescriptionProvider
{
    public AdditionalCharacterDescriptionElement GetAdditionalCharacterDescription()
    {
        NeuralNPC npc = GetComponent<NeuralNPC>();
        if (!ReferenceEquals(Plugin.Instance, null)
            && Plugin.Instance.IsManagedGuardResponse(npc))
        {
            return new AdditionalCharacterDescriptionElement(
                secret: true,
                npc.GetFinalName() + " is acting as a Silverpine guard. "
                + Player.Instance.playerName + " cannot leave until the guard "
                + "is genuinely convinced no crime occurred and releases the "
                + "player without a fine, or the player uses the guard controls "
                + "to pay the fine or accept the configured jail sentence. If "
                + "the player cannot afford the fine, the player is arrested "
                + "and serves that jail sentence. The guard "
                + "must not claim an outcome has already happened and should "
                + "only release the player when persuaded of innocence.");
        }
        return AdditionalCharacterDescriptionElement.empty;
    }
}

internal sealed class GuardJusticeChoiceFunction : NPCFunction
{
    public override string Name =>
        "In this excerpt, did " + owner.GetFinalName() + " require "
        + Player.Instance.playerName + " to choose between paying a fine "
        + "and serving a jail sentence?";

    public override string APIName => "offer_fine_or_jail";

    public override bool IsActive(string result)
    {
        return IsAPIActive()
            && result.ContainsAnyWordIgnoreCase(
                "fine",
                "jail",
                "prison",
                "sentence",
                "penalty",
                "guilty");
    }

    public override bool IsAPIActive() =>
        Plugin.Instance.IsManagedGuardResponse(owner);

    public override bool IsOncePerDialogOnly() => true;

    public override bool IsConflicting() => true;

    public override Task Invoke() =>
        Plugin.Instance.OfferFineOrJail(owner);
}

internal sealed class GuardLetGoWithoutFineFunction : NPCFunction
{
    public override string Name =>
        "In this excerpt, did " + owner.GetFinalName() + " conclude that "
        + Player.Instance.playerName + " committed no crime and explicitly "
        + "release " + Player.Instance.pronouns.Him() + " without a fine?";

    public override string APIName =>
        "let_" + Player.Instance.playerName.ToLower()
        + "_go_without_fine";

    public override bool IsActive(string result) => IsAPIActive();

    public override bool IsAPIActive() =>
        Plugin.Instance.IsManagedGuardResponse(owner);

    public override bool IsOncePerDialogOnly() => true;

    public override bool IsConflicting() => true;

    public override Task Invoke() =>
        Plugin.Instance.LetGoWithoutFine(owner);
}

internal sealed class ExpandedCallGuardsFunction : NPCFunction_CallGuards
{
    public override bool IsActive(string result)
    {
        return IsAPIActive()
            && result.ContainsAnyWordIgnoreCase(
                "call",
                "guard",
                "guards",
                "help");
    }

    public override bool IsAPIActive()
    {
        if (ReferenceEquals(Plugin.Instance, null))
            return false;
        Plugin.Instance.RefreshSaveIdentity();
        return Plugin.Instance.IsCallGuardAvailable(owner);
    }

    public override Task Invoke() =>
        Plugin.Instance.InvokeCallGuards(owner);
}

[HarmonyPatch(typeof(NeuralNPC), "Awake")]
internal static class NeuralNpcCallGuardsInstallPatch
{
    [HarmonyPostfix]
    private static void InstallExpandedDispatcher(NeuralNPC __instance)
    {
        Plugin.EnsureExpandedCallGuardsFunction(__instance);
    }
}

[HarmonyPatch(typeof(NeuralNPC), "GetActiveNPCFunctions")]
internal static class NeuralNpcActiveFunctionsPatch
{
    [HarmonyPrefix]
    private static void EnsureExpandedDispatcher(NeuralNPC __instance)
    {
        Plugin.EnsureExpandedCallGuardsFunction(__instance);
    }
}

[HarmonyPatch(typeof(Door), nameof(Door.OnInteract))]
internal static class JailDoorInteractionLockdownPatch
{
    [HarmonyPrefix]
    private static bool BlockPublicAccessDuringSentence(
        Door __instance,
        GameObject caller)
    {
        if (ReferenceEquals(Plugin.Instance, null)
            || !Plugin.Instance.IsJailDoorLockedDown(__instance)
            || __instance.IsOpen)
        {
            return true;
        }

        if (Player.Instance != null
            && caller == Player.Instance.gameObject
            && DialogBox.Instance != null)
        {
            DialogBox.Instance.DisplayTextNoDialog(
                "The jail door is locked until your sentence is complete.");
        }
        return false;
    }
}

[HarmonyPatch(typeof(Door), "Open")]
internal static class JailDoorOpenLockdownPatch
{
    [HarmonyPrefix]
    private static bool BlockEveryOpenPathDuringSentence(Door __instance)
    {
        return ReferenceEquals(Plugin.Instance, null)
            || !Plugin.Instance.IsJailDoorLockedDown(__instance);
    }
}

internal sealed class GuardDatabase
{
    public Dictionary<string, GuardSave> saves =
        new(StringComparer.OrdinalIgnoreCase);

    internal void Normalize()
    {
        saves ??= new Dictionary<string, GuardSave>(
            StringComparer.OrdinalIgnoreCase);
        foreach (GuardSave save in saves.Values)
            save?.Normalize();
    }
}

internal sealed class GuardSave
{
    public List<string> guardKeys = new();
    public List<string> jailDoorKeys = new();
    public bool hasJailCell;
    public int jailCellX;
    public int jailCellY;
    public bool useBaseGameFine = true;
    public int fineAmount = 100;
    public bool useFineBasedSentence = true;
    public int fixedSentenceHours = 24;
    public int goldPerJailHour = 5;
    public int releaseTurn = -1;
    public List<string> pendingReleaseDoorKeys = new();

    internal static GuardSave CreateDefault()
    {
        GuardSave save = new();
        save.guardKeys.Add(NPCName.Darian.ToString());
        return save;
    }

    internal void Normalize()
    {
        guardKeys ??= new List<string>();
        jailDoorKeys ??= new List<string>();
        pendingReleaseDoorKeys ??= new List<string>();
        fineAmount = Mathf.Clamp(fineAmount, 1, 1000);
        fixedSentenceHours = Mathf.Clamp(fixedSentenceHours, 1, 1000);
        goldPerJailHour = Mathf.Clamp(goldPerJailHour, 1, 1000);
    }
}

[HarmonyPatch(typeof(NPCFunction_CallGuards), nameof(NPCFunction_CallGuards.IsAPIActive))]
internal static class CallGuardsActivePatch
{
    [HarmonyPrefix]
    private static bool UseConfiguredGuards(
        NPCFunction_CallGuards __instance,
        ref bool __result)
    {
        Plugin.Instance.RefreshSaveIdentity();
        if (!Plugin.Instance.HasActiveSave)
            return true;

        __result = Plugin.Instance.IsCallGuardAvailable(__instance.owner);
        return false;
    }
}

[HarmonyPatch(typeof(NPCFunction_CallGuards), nameof(NPCFunction_CallGuards.Invoke))]
internal static class CallGuardsInvokePatch
{
    [HarmonyPrefix]
    private static bool DispatchConfiguredGuards(
        NPCFunction_CallGuards __instance,
        ref Task __result)
    {
        Plugin.Instance.RefreshSaveIdentity();
        if (!Plugin.Instance.HasActiveSave)
            return true;

        __result = Plugin.Instance.InvokeCallGuards(__instance.owner);
        return false;
    }
}

[HarmonyPatch(typeof(DialogBox), "DrawUpperButtons")]
internal static class GuardResolutionButtonsPatch
{
    [HarmonyPrefix]
    private static void AddResolutionChoices(
        List<UpperButtonOption> upperButtonOptions)
    {
        NeuralNPC guard = NeuralNPC.currentActiveDialogNeuralNPC;
        if (guard == null || !Plugin.Instance.IsManagedGuardResponse(guard))
            return;

        if (!upperButtonOptions.Any(option =>
                option.label.StartsWith(
                    "Resolve Fine or Jail",
                    StringComparison.Ordinal)))
        {
            upperButtonOptions.Add(new UpperButtonOption(
                "Resolve Fine or Jail",
                () => _ = Plugin.Instance.OfferFineOrJail(guard)));
        }
    }
}

[HarmonyPatch(
    typeof(CustomNeuralFunctionCallFine.NPCFunction_Fine),
    nameof(CustomNeuralFunctionCallFine.NPCFunction_Fine.Invoke))]
internal static class ReplaceAutomaticGuardFinePatch
{
    [HarmonyPrefix]
    private static bool RequirePlayerChoice(
        CustomNeuralFunctionCallFine.NPCFunction_Fine __instance,
        ref Task __result)
    {
        if (!Plugin.Instance.IsConfiguredGuardDialog(__instance.owner))
            return true;

        __result = Plugin.Instance.OfferFineOrJail(__instance.owner);
        return false;
    }
}

[HarmonyPatch(
    typeof(CustomNeuralFunctionCallFine.NPCFunction_LetGo),
    nameof(CustomNeuralFunctionCallFine.NPCFunction_LetGo.Invoke))]
internal static class ReplaceAutomaticGuardReleasePatch
{
    [HarmonyPrefix]
    private static bool RequirePlayerChoice(
        CustomNeuralFunctionCallFine.NPCFunction_LetGo __instance,
        ref Task __result)
    {
        if (!Plugin.Instance.IsConfiguredGuardDialog(__instance.owner))
            return true;

        __result = Plugin.Instance.LetGoWithoutFine(__instance.owner);
        return false;
    }
}
