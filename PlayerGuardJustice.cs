#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SilverpineGuardSystem;

public sealed partial class Plugin
{
    private const string NpcJailActivityPrefix =
        "serving a Silverpine jail sentence in ";
    private const string NpcJailHoldingActivityPrefix =
        "held for a Silverpine jail sentence in ";

    private enum PlayerGuardOutcome
    {
        Undecided,
        Fine,
        Jail,
        Release
    }

    private sealed class PlayerGuardAccusation
    {
        internal readonly List<NeuralNPC> targets = new();
        internal readonly List<NeuralNPC> participants = new();
        internal readonly Dictionary<NeuralNPC, int> fineAmounts = new();
        internal readonly Dictionary<NeuralNPC, PlayerGuardOutcome> outcomes =
            new();
        internal string law = "";
        internal bool responsesComplete;
        internal bool calculatingFines;
    }

    private bool playerGuardTargetSelectionActive;
    private List<NeuralNPC>? activePlayerGuardLawTargets;
    private PlayerGuardAccusation? activePlayerGuardAccusation;

    internal bool PlayerIsGuard => currentSave?.playerIsGuard ?? false;

    internal void TogglePlayerGuard()
    {
        RefreshSaveIdentity();
        if (currentSave == null)
            return;

        currentSave.playerIsGuard = !currentSave.playerIsGuard;
        SaveDatabase();
    }

    internal bool CanPlayerGuardActOn(NeuralNPC? target)
    {
        return PlayerIsGuard
            && target != null
            && DialogBox.Instance != null
            && DialogBox.Instance.isOpen
            && (NeuralNPC.currentActiveDialogNeuralNPC == target
                || NeuralNPC.multiDialogParticipants?.Contains(target) == true)
            && !IsManagedGuardResponse(target);
    }

    internal bool HasPendingPlayerGuardAccusation =>
        activePlayerGuardAccusation != null;

    internal bool PendingPlayerGuardResponsesComplete =>
        activePlayerGuardAccusation?.responsesComplete == true;

    internal bool IsPlayerGuardSetupActive =>
        playerGuardTargetSelectionActive
        || activePlayerGuardLawTargets != null;

    internal void BeginPlayerGuardJustice(NeuralNPC target)
    {
        RefreshSaveIdentity();
        if (!CanPlayerGuardActOn(target))
        {
            Log.LogWarning(
                "Player guard action was clicked after its conversation "
                + "context was no longer active.");
            UpperNotificationUI.Instance?.OneOff(
                "The guard action is no longer available in this conversation.");
            return;
        }
        if (HasPendingPlayerGuardAccusation || IsPlayerGuardSetupActive)
            return;

        List<NeuralNPC> candidates = GetPlayerGuardConversationParticipants(
            target)
            .Where(candidate => !currentSave!.npcSentences.Any(sentence =>
                string.Equals(
                    sentence.npcKey,
                    GetNpcKey(candidate),
                    StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0)
        {
            UpperNotificationUI.Instance.OneOff(
                "Every NPC in this conversation is already serving a sentence.");
            return;
        }

        if (candidates.Count > 1)
        {
            playerGuardTargetSelectionActive = true;
            ShowPlayerGuardTargetSelection(
                target,
                candidates,
                new HashSet<NeuralNPC>());
            return;
        }

        BeginPlayerGuardLawEntry(candidates);
    }

    private List<NeuralNPC> GetPlayerGuardConversationParticipants(
        NeuralNPC fallback)
    {
        if (NeuralNPC.multiDialogParticipants != null
            && NeuralNPC.multiDialogParticipants.Count > 0)
        {
            return NeuralNPC.multiDialogParticipants
                .Where(npc => npc != null)
                .Distinct()
                .ToList();
        }
        return new List<NeuralNPC> { fallback };
    }

    private void ShowPlayerGuardTargetSelection(
        NeuralNPC anchor,
        IReadOnlyList<NeuralNPC> candidates,
        HashSet<NeuralNPC> selected)
    {
        if (!CanPlayerGuardActOn(anchor))
        {
            playerGuardTargetSelectionActive = false;
            return;
        }

        List<DialogOption> options = new();
        foreach (NeuralNPC candidate in candidates)
        {
            NeuralNPC captured = candidate;
            options.Add(new DialogOption(
                (selected.Contains(captured) ? "[ACCUSED] " : "[ ] ")
                + captured.GetFinalName(),
                () =>
                {
                    if (!selected.Add(captured))
                        selected.Remove(captured);
                    ShowPlayerGuardTargetSelection(
                        anchor,
                        candidates,
                        selected);
                },
                endDialog: false));
        }

        options.Add(new DialogOption(
            "Confirm Accused (" + selected.Count + ")",
            () =>
            {
                if (selected.Count == 0)
                {
                    UpperNotificationUI.Instance.OneOff(
                        "Select at least one NPC to accuse.");
                    ShowPlayerGuardTargetSelection(
                        anchor,
                        candidates,
                        selected);
                    return;
                }
                playerGuardTargetSelectionActive = false;
                BeginPlayerGuardLawEntry(candidates
                    .Where(selected.Contains)
                    .ToList());
            },
            endDialog: false));
        options.Add(new DialogOption(
            "Cancel Accusation",
            () =>
            {
                playerGuardTargetSelectionActive = false;
                anchor.GenerateSystemDialog(
                    Player.Instance.playerName
                    + " decided not to make a formal accusation.");
            },
            endDialog: false));

        DialogBox.Instance.DisplayTextNoDialog(
            "Select every NPC accused of the same crime. Multiple defendants "
            + "may be selected.",
            options.ToArray());
    }

    private void BeginPlayerGuardLawEntry(List<NeuralNPC> targets)
    {
        if (targets.Count == 0 || !CanPlayerGuardActOn(targets[0]))
            return;

        activePlayerGuardLawTargets = new List<NeuralNPC>(targets);
        string names = FormatNpcNames(targets);
        Log.LogInfo(
            "Opened the player guard law prompt for " + names + ".");
        DialogBox.Instance.DisplayText(
            "State the law " + names
            + (targets.Count == 1 ? " is" : " are")
            + " accused of breaking. Type the law in the Talk field below "
            + "and submit it. Choose Leave to cancel.",
            text => SubmitPlayerGuardLaw(targets, text),
            new List<UpperButtonOption>());
    }

    private void SubmitPlayerGuardLaw(
        IReadOnlyList<NeuralNPC> targets,
        string text)
    {
        if (activePlayerGuardLawTargets == null
            || !targets.SequenceEqual(activePlayerGuardLawTargets))
        {
            return;
        }

        activePlayerGuardLawTargets = null;
        if (targets.Count == 0 || !CanPlayerGuardActOn(targets[0]))
        {
            Log.LogWarning(
                "Player guard law submission lost its active conversation.");
            UpperNotificationUI.Instance?.OneOff(
                "The conversation ended before the law could be submitted.");
            return;
        }

        string law = NormalizeLaw(text);
        if (string.IsNullOrWhiteSpace(law))
        {
            UpperNotificationUI.Instance?.OneOff(
                "Write a law before submitting the guard action.");
            BeginPlayerGuardLawEntry(targets.ToList());
            return;
        }

        PlayerGuardAccusation accusation = new()
        {
            law = law
        };
        accusation.targets.AddRange(targets.Where(npc => npc != null));
        accusation.participants.AddRange(
            GetPlayerGuardConversationParticipants(targets[0]));
        foreach (NeuralNPC accused in accusation.targets)
            accusation.outcomes[accused] = PlayerGuardOutcome.Undecided;
        activePlayerGuardAccusation = accusation;
        foreach (NeuralNPC participant in accusation.participants)
            SuppressJusticeDialogRumors(participant);

        Log.LogInfo(
            "Player guard accusation submitted against "
            + FormatNpcNames(accusation.targets) + ".");
        _ = GeneratePlayerGuardAccusationResponses(accusation);
    }

    private async Task GeneratePlayerGuardAccusationResponses(
        PlayerGuardAccusation accusation)
    {
        try
        {
            string accusedNames = FormatNpcNames(accusation.targets);
            foreach (NeuralNPC accused in accusation.targets)
            {
                if (!ReferenceEquals(activePlayerGuardAccusation, accusation)
                    || DialogBox.Instance == null
                    || !DialogBox.Instance.isOpen)
                {
                    return;
                }

                bool displayed = false;
                accused.GenerateSystemDialog(
                    "Formal guard accusation: " + Player.Instance.playerName
                    + " has accused " + accusedNames
                    + " of breaking this law: " + accusation.law + ". "
                    + accused.GetFinalName()
                    + " must now respond directly to the accusation, "
                    + "explaining " + accused.Pronouns.His()
                    + " defense or admission. Do not decide the punishment; "
                    + "the player guard will decide it later.",
                    () => displayed = true);

                while (ReferenceEquals(activePlayerGuardAccusation, accusation)
                    && DialogBox.Instance != null
                    && DialogBox.Instance.isOpen
                    && !displayed)
                {
                    await Task.Yield();
                }
                if (!ReferenceEquals(activePlayerGuardAccusation, accusation))
                    return;
                while (DialogBox.Instance != null
                    && DialogBox.Instance.isOpen
                    && (DialogBox.Instance.isAnimatingText
                        || NeuralNPC.npcFunctionsBeingInvoked))
                {
                    await Task.Yield();
                }
                await Task.Yield();
            }

            if (!ReferenceEquals(activePlayerGuardAccusation, accusation)
                || DialogBox.Instance == null
                || !DialogBox.Instance.isOpen)
            {
                return;
            }

            accusation.responsesComplete = true;
            DialogBox.Instance.AddUpperButtonOption(new UpperButtonOption(
                "Guard: Decide Punishment",
                BeginPlayerGuardPunishmentDecision), shake: true);
            ApplyPendingPlayerGuardLeaveLock(DialogBox.Instance);
            Log.LogInfo(
                "All accused NPCs responded; punishment decision is "
                + "available.");
        }
        catch (Exception exception)
        {
            Log.LogError(
                "Generating player guard accusation responses failed: "
                + exception);
            if (!ReferenceEquals(activePlayerGuardAccusation, accusation))
                return;

            accusation.responsesComplete = true;
            if (DialogBox.Instance?.isOpen == true)
            {
                DialogBox.Instance.AddUpperButtonOption(new UpperButtonOption(
                    "Guard: Decide Punishment",
                    BeginPlayerGuardPunishmentDecision), shake: true);
                ApplyPendingPlayerGuardLeaveLock(DialogBox.Instance);
            }
            UpperNotificationUI.Instance?.OneOff(
                "The accused could not finish responding; the punishment "
                + "decision is still available.");
        }
    }

    internal void BeginPlayerGuardPunishmentDecision()
    {
        PlayerGuardAccusation? accusation = activePlayerGuardAccusation;
        if (accusation == null
            || !accusation.responsesComplete
            || accusation.calculatingFines
            || !IsPlayerGuardAccusationConversationActive(accusation))
        {
            return;
        }
        _ = PreparePlayerGuardPunishmentDecision(accusation);
    }

    private async Task PreparePlayerGuardPunishmentDecision(
        PlayerGuardAccusation accusation)
    {
        try
        {
            accusation.calculatingFines = true;
            DialogBox.Instance.DisplayLoadingNoDialog();
            foreach (NeuralNPC accused in accusation.targets)
            {
                if (!ReferenceEquals(activePlayerGuardAccusation, accusation))
                    return;
                if (!accusation.fineAmounts.ContainsKey(accused))
                {
                    accusation.fineAmounts[accused] =
                        await DetermineNpcFineAmount(accused, accusation.law);
                }
            }

            if (!ReferenceEquals(activePlayerGuardAccusation, accusation)
                || !IsPlayerGuardAccusationConversationActive(accusation))
            {
                return;
            }
            ShowPlayerGuardPunishmentChoices(accusation);
        }
        catch (Exception exception)
        {
            Log.LogError("Player guard punishment decision failed: " + exception);
            UpperNotificationUI.Instance?.OneOff(
                "The punishment decision could not be prepared.");
            NeuralNPC? speaker = NeuralNPC.currentActiveDialogNeuralNPC;
            if (speaker != null && DialogBox.Instance?.isOpen == true)
            {
                speaker.GenerateSystemDialog(
                    "The punishment decision was delayed. The conversation "
                    + "continues while the player guard considers the case.");
            }
        }
        finally
        {
            accusation.calculatingFines = false;
        }
    }

    private async Task<int> DetermineNpcFineAmount(
        NeuralNPC target,
        string law)
    {
        if (!UsesBaseGameFine)
            return FineAmount;

        string answer = await target.AskQuestion(
            "How much should " + target.GetFinalName() + " be fined for "
            + "breaking this law: " + law + "? Answer with just the cost of "
            + "the fine. 100 if not specified.",
            deterministic: true,
            6,
            "root ::= \"0\" | non-zero-number | \"1000\"\n"
            + "non-zero-number ::= [1-9] [0-9]{0,2}");
        int amount = Mathf.Abs(
            APIModelHandler.HandleNoGrammarInt(answer, 100));
        return Mathf.Clamp(amount, 0, 1000);
    }

    private void ShowPlayerGuardPunishmentChoices(
        PlayerGuardAccusation accusation)
    {
        if (!ReferenceEquals(activePlayerGuardAccusation, accusation)
            || !IsPlayerGuardAccusationConversationActive(accusation))
        {
            return;
        }

        if (accusation.targets.Count == 1)
        {
            ShowSinglePlayerGuardPunishmentChoice(accusation);
            return;
        }

        List<DialogOption> options = new();
        foreach (NeuralNPC accused in accusation.targets)
        {
            NeuralNPC captured = accused;
            options.Add(new DialogOption(
                GetPlayerGuardOutcomeLabel(accusation, captured),
                () =>
                {
                    accusation.outcomes[captured] =
                        GetNextPlayerGuardOutcome(
                            accusation.outcomes[captured]);
                    ShowPlayerGuardPunishmentChoices(accusation);
                },
                endDialog: false));
        }
        options.Add(new DialogOption(
            "Confirm All Decisions",
            () => ApplyPlayerGuardPunishments(accusation),
            endDialog: false));

        DialogBox.Instance.DisplayTextNoDialog(
            "Decide each defendant's outcome for the law: "
            + accusation.law
            + "\n\nClick each NPC to cycle UNDECIDED, FINE, JAIL, and "
            + "RELEASE. Confirm after every outcome is assigned.",
            options.ToArray());
    }

    private void ShowSinglePlayerGuardPunishmentChoice(
        PlayerGuardAccusation accusation)
    {
        NeuralNPC accused = accusation.targets[0];
        int fineAmount = accusation.fineAmounts[accused];
        int sentenceHours = GetSentenceHours(fineAmount);
        bool jailReady = GetAvailablePlayerGuardJailCellCount() > 0;
        DialogBox.Instance.DisplayTextNoDialog(
            "Decide " + accused.GetFinalName() + "'s outcome for breaking "
            + "the law: " + accusation.law,
            new DialogOption(
                "Fine " + accused.GetFinalName() + " ("
                + fineAmount + " gold)",
                () =>
                {
                    accusation.outcomes[accused] = PlayerGuardOutcome.Fine;
                    ApplyPlayerGuardPunishments(accusation);
                },
                endDialog: false),
            new DialogOption(
                jailReady
                    ? "Jail " + accused.GetFinalName() + " ("
                        + sentenceHours + " hours)"
                    : "Jail Unavailable",
                () =>
                {
                    if (GetAvailablePlayerGuardJailCellCount() == 0)
                    {
                        UpperNotificationUI.Instance.OneOff(
                            "No configured, loaded jail cell is available.");
                        ShowSinglePlayerGuardPunishmentChoice(accusation);
                        return;
                    }
                    accusation.outcomes[accused] = PlayerGuardOutcome.Jail;
                    ApplyPlayerGuardPunishments(accusation);
                },
                endDialog: false),
            new DialogOption(
                "Release (No Violation)",
                () =>
                {
                    accusation.outcomes[accused] = PlayerGuardOutcome.Release;
                    ApplyPlayerGuardPunishments(accusation);
                },
                endDialog: false));
    }

    private string GetPlayerGuardOutcomeLabel(
        PlayerGuardAccusation accusation,
        NeuralNPC accused)
    {
        int fine = accusation.fineAmounts[accused];
        return accusation.outcomes[accused] switch
        {
            PlayerGuardOutcome.Fine =>
                "[FINE " + fine + "g] " + accused.GetFinalName(),
            PlayerGuardOutcome.Jail =>
                "[JAIL " + GetSentenceHours(fine) + "h] "
                + accused.GetFinalName(),
            PlayerGuardOutcome.Release =>
                "[RELEASE] " + accused.GetFinalName(),
            _ => "[UNDECIDED] " + accused.GetFinalName()
        };
    }

    private static PlayerGuardOutcome GetNextPlayerGuardOutcome(
        PlayerGuardOutcome outcome) => outcome switch
        {
            PlayerGuardOutcome.Undecided => PlayerGuardOutcome.Fine,
            PlayerGuardOutcome.Fine => PlayerGuardOutcome.Jail,
            PlayerGuardOutcome.Jail => PlayerGuardOutcome.Release,
            _ => PlayerGuardOutcome.Undecided
        };

    private void ApplyPlayerGuardPunishments(PlayerGuardAccusation accusation)
    {
        if (!ReferenceEquals(activePlayerGuardAccusation, accusation)
            || !IsPlayerGuardAccusationConversationActive(accusation))
        {
            return;
        }
        if (accusation.targets.Any(accused =>
                accusation.outcomes[accused]
                    == PlayerGuardOutcome.Undecided))
        {
            UpperNotificationUI.Instance.OneOff(
                "Choose an outcome for every accused NPC.");
            ShowPlayerGuardPunishmentChoices(accusation);
            return;
        }

        List<NeuralNPC> missingInventories = accusation.targets
            .Where(accused =>
                accusation.outcomes[accused] == PlayerGuardOutcome.Fine
                && accused.GetComponent<WorldInventory>() == null)
            .ToList();
        if (missingInventories.Count > 0)
        {
            UpperNotificationUI.Instance.OneOff(
                FormatNpcNames(missingInventories)
                + " cannot be fined because no inventory is available. "
                + "Choose jail or release instead.");
            ShowPlayerGuardPunishmentChoices(accusation);
            return;
        }

        int requiredCells = accusation.targets.Count(accused =>
            accusation.outcomes[accused] == PlayerGuardOutcome.Jail);
        int availableCells = GetAvailablePlayerGuardJailCellCount();
        if (requiredCells > availableCells)
        {
            UpperNotificationUI.Instance.OneOff(
                requiredCells + " jail cells are required, but only "
                + availableCells + " configured cells are available.");
            ShowPlayerGuardPunishmentChoices(accusation);
            return;
        }

        bool allReleased = accusation.targets.All(accused =>
            accusation.outcomes[accused] == PlayerGuardOutcome.Release);
        foreach (NeuralNPC accused in accusation.targets)
        {
            int fineAmount = accusation.fineAmounts[accused];
            switch (accusation.outcomes[accused])
            {
                case PlayerGuardOutcome.Fine:
                    ApplyNpcFine(
                        accused,
                        accusation.law,
                        fineAmount);
                    break;
                case PlayerGuardOutcome.Jail:
                    ApplyNpcJail(
                        accused,
                        accusation.law,
                        fineAmount,
                        forcedByInsufficientFunds: false);
                    break;
                case PlayerGuardOutcome.Release:
                    RecordNpcRelease(accused, accusation.law);
                    break;
            }
        }

        activePlayerGuardAccusation = null;
        SaveDatabase();
        if (allReleased)
        {
            NeuralNPC speaker = NeuralNPC.currentActiveDialogNeuralNPC
                ?? accusation.targets[0];
            speaker.GenerateSystemDialog(
                Player.Instance.playerName + " found no proven violation and "
                + "released " + FormatNpcNames(accusation.targets)
                + " without punishment. The conversation continues normally.");
            return;
        }

        DialogBox.Instance.QueueDialogEnd();
        DialogBox.Instance.EndDialog();
    }

    private int GetAvailablePlayerGuardJailCellCount()
    {
        if (currentSave == null)
            return 0;
        return currentSave.jailCells.Count(cell =>
            IsCellConfigured(cell, out _)
            && !IsCellOccupied(cell));
    }

    private bool ApplyNpcFine(
        NeuralNPC target,
        string law,
        int fineAmount)
    {
        WorldInventory inventory = target.GetComponent<WorldInventory>();
        if (inventory != null)
            inventory.inventory.gold -= fineAmount;
        AnimationHelper.Instance.SpawnFloatingGoldText(
            -fineAmount,
            target.transform);
        target.AddThingThatHappened(
            Player.Instance.playerName + " fined " + target.GetFinalName()
            + " " + fineAmount + " gold for breaking the law: " + law + ".");
        PublishNpcJusticeOutcome(
            target,
            "was fined " + fineAmount + " gold",
            law);
        return true;
    }

    private bool ApplyNpcJail(
        NeuralNPC target,
        string law,
        int fineAmount,
        bool forcedByInsufficientFunds)
    {
        if (currentSave == null || WorldInfoManager.Instance == null)
            return false;
        if (currentSave.npcSentences.Any(sentence => string.Equals(
                sentence.npcKey,
                GetNpcKey(target),
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (!TryGetAvailableJailCell(
                out JailCellSave cell,
                out string reason))
        {
            UpperNotificationUI.Instance.OneOff(reason);
            return false;
        }

        int sentenceHours = GetSentenceHours(fineAmount);
        NpcSentenceSave sentence = new()
        {
            npcKey = GetNpcKey(target),
            npcName = target.GetFinalName(),
            cellId = cell.id,
            fineAmount = fineAmount,
            law = law,
            releaseTurn = WorldInfoManager.Instance.TotalTurnCount
                + sentenceHours * WorldInfoManager.HOUR_TURN_LENGTH
        };
        currentSave.npcSentences.Add(sentence);
        cell.pendingReleaseDoorKeys.Clear();
        EnsureNpcJailRoutine(sentence);
        CloseAndLockCellDoors(cell);

        UpperNotificationUI.Instance.OneOff(
            target.GetFinalName()
            + (forcedByInsufficientFunds
                ? " could not afford the fine and was arrested for "
                : " was jailed for ")
            + sentenceHours + " game hours in " + cell.name + ".");
        target.AddThingThatHappened(
            Player.Instance.playerName
            + (forcedByInsufficientFunds
                ? " jailed " + target.GetFinalName() + " after "
                    + target.GetFinalName() + " could not afford a "
                    + fineAmount + " gold fine"
                : " sentenced " + target.GetFinalName())
            + " to " + sentenceHours + " game hours in jail for breaking "
            + "the law: " + law + ".");
        PublishNpcJusticeOutcome(
            target,
            "was jailed for " + sentenceHours + " game hours",
            law);
        return true;
    }

    private static void RecordNpcRelease(NeuralNPC target, string law)
    {
        target.AddThingThatHappened(
            Player.Instance.playerName + " considered whether "
            + target.GetFinalName() + " violated the law, concluded no "
            + "violation was proven, and released " + target.Pronouns.Him()
            + ". The alleged law was: " + law + ".");
    }

    private bool IsPlayerGuardAccusationConversationActive(
        PlayerGuardAccusation accusation)
    {
        NeuralNPC? speaker = NeuralNPC.currentActiveDialogNeuralNPC;
        return PlayerIsGuard
            && DialogBox.Instance != null
            && DialogBox.Instance.isOpen
            && speaker != null
            && accusation.participants.Contains(speaker);
    }

    internal void ApplyPendingPlayerGuardLeaveLock(DialogBox dialog)
    {
        if (activePlayerGuardAccusation == null || dialog == null)
            return;
        foreach (Button button in dialog.GetComponentsInChildren<Button>(true))
        {
            TextMeshProUGUI? label =
                button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null
                || !string.Equals(
                    label.text,
                    "Leave",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            button.interactable = false;
            Color color = label.color;
            color.a = 0.5f;
            label.color = color;
        }
    }

    private static string FormatNpcNames(IEnumerable<NeuralNPC> npcs) =>
        NeuralNPC.ToAnd(npcs
            .Where(npc => npc != null)
            .Select(npc => npc.GetFinalName())
            .ToList());

    internal void ClearPlayerGuardLawEntry(NeuralNPC target)
    {
        playerGuardTargetSelectionActive = false;
        activePlayerGuardLawTargets = null;
        if (activePlayerGuardAccusation?.participants.Contains(target) == true)
            activePlayerGuardAccusation = null;
    }

    private void EnsureNpcJailRoutine(NpcSentenceSave sentence)
    {
        if (currentSave == null || WorldInfoManager.Instance == null)
            return;

        NeuralNPC? npc = GetLoadedNpcs().FirstOrDefault(candidate =>
            string.Equals(
                GetNpcKey(candidate),
                sentence.npcKey,
                StringComparison.OrdinalIgnoreCase));
        JailCellSave? cell = GetCell(sentence.cellId);
        if (npc == null || cell == null || !cell.hasDestination)
            return;

        NPCRoutineExecutor executor = npc.GetComponent<NPCRoutineExecutor>();
        if (executor == null)
            return;

        string activity = NpcJailActivityPrefix + cell.id;
        if (executor.currentRoutine != null
            && string.Equals(
                executor.currentRoutine.activity,
                activity,
                StringComparison.Ordinal))
        {
            return;
        }

        int remainingTurns = Math.Max(
            1,
            sentence.releaseTurn
                - WorldInfoManager.Instance.TotalTurnCount);
        Vector2Int destination = new(cell.destinationX, cell.destinationY);
        executor.preOverrideRoutines.Clear();
        executor.StartNPCRoutine(new NPCRoutine(
            "being held in " + cell.name,
            NpcJailHoldingActivityPrefix + cell.id,
            "being held in jail",
            destination));
        executor.StartOverrideRoutine(new NPCRoutine(
            "being taken to " + cell.name,
            activity,
            "serving a jail sentence",
            destination,
            new RoutineArgument_EndOverrideIf_AfterTurns(remainingTurns)));
        npc.GetComponent<EntityMover>().Teleport(destination);
        Log.LogInfo(
            "Halted existing routines on jailed NPC "
            + npc.GetFinalName() + ".");
    }

    internal bool ShouldBlockNpcRoutineWhileJailed(
        NPCRoutineExecutor executor,
        NPCRoutine npcRoutine)
    {
        RefreshSaveIdentity();
        if (currentSave == null
            || executor == null
            || executor.neuralNPC == null
            || npcRoutine == null
            || npcRoutine.activity.StartsWith(
                NpcJailActivityPrefix,
                StringComparison.Ordinal))
        {
            return false;
        }

        string npcKey = GetNpcKey(executor.neuralNPC);
        return currentSave.npcSentences.Any(sentence => string.Equals(
            sentence.npcKey,
            npcKey,
            StringComparison.OrdinalIgnoreCase));
    }

    internal IReadOnlyList<NeuralNPC> GetPlayerGuardEarlyReleaseCandidates(
        NeuralNPC fallback)
    {
        RefreshSaveIdentity();
        if (!PlayerIsGuard
            || currentSave == null
            || DialogBox.Instance?.isOpen != true
            || HasPendingPlayerGuardAccusation
            || IsPlayerGuardSetupActive)
        {
            return Array.Empty<NeuralNPC>();
        }

        return GetPlayerGuardConversationParticipants(fallback)
            .Where(candidate => FindNpcSentence(candidate) != null)
            .ToList();
    }

    internal void ReleaseNpcPrisonerEarly(NeuralNPC target)
    {
        RefreshSaveIdentity();
        if (!CanPlayerGuardActOn(target)
            || HasPendingPlayerGuardAccusation
            || IsPlayerGuardSetupActive)
        {
            return;
        }

        NpcSentenceSave? sentence = FindNpcSentence(target);
        if (sentence == null)
        {
            UpperNotificationUI.Instance?.OneOff(
                target.GetFinalName() + " is no longer serving a sentence.");
            return;
        }

        string cellName = GetCell(sentence.cellId)?.name ?? "jail";
        ReleaseNpcPrisoner(sentence, releasedEarlyByPlayerGuard: true);
        UpperNotificationUI.Instance?.OneOff(
            target.GetFinalName() + " was released early from "
            + cellName + ".");
        target.GenerateSystemDialog(
            Player.Instance.playerName + " used guard authority to release "
            + target.GetFinalName() + " early from " + cellName
            + ". The original sentence was for breaking this law: "
            + sentence.law + ". Respond directly to being released early; "
            + "the conversation then continues normally.");
    }

    private NpcSentenceSave? FindNpcSentence(NeuralNPC npc)
    {
        if (currentSave == null)
            return null;

        string npcKey = GetNpcKey(npc);
        return currentSave.npcSentences.FirstOrDefault(sentence =>
            string.Equals(
                sentence.npcKey,
                npcKey,
                StringComparison.OrdinalIgnoreCase));
    }

    private void ReleaseNpcPrisoner(
        NpcSentenceSave sentence,
        bool releasedEarlyByPlayerGuard = false)
    {
        if (currentSave == null)
            return;

        JailCellSave? cell = GetCell(sentence.cellId);
        NeuralNPC? npc = GetLoadedNpcs().FirstOrDefault(candidate =>
            string.Equals(
                GetNpcKey(candidate),
                sentence.npcKey,
                StringComparison.OrdinalIgnoreCase));
        if (npc != null)
        {
            NPCRoutineExecutor executor = npc.GetComponent<NPCRoutineExecutor>();
            NPCRoutine? jailRoutine = executor?
                .GetCurrentAndPreOverrideRoutines()
                .FirstOrDefault(routine => routine.activity.StartsWith(
                    NpcJailActivityPrefix,
                    StringComparison.Ordinal));
            if (executor != null && jailRoutine != null)
                executor.StopOverrideRoutine(jailRoutine);
            if (releasedEarlyByPlayerGuard)
            {
                npc.AddThingThatHappened(
                    Player.Instance.playerName + " used guard authority to "
                    + "release " + npc.GetFinalName()
                    + " before the end of " + npc.Pronouns.His()
                    + " jail sentence for breaking the law: "
                    + sentence.law + ".");
            }
            else
            {
                npc.AddThingThatHappened(
                    npc.GetFinalName() + " completed "
                    + npc.Pronouns.His()
                    + " jail sentence and was released.");
            }
        }

        currentSave.npcSentences.Remove(sentence);
        if (cell != null)
            AddPendingDoorReleases(cell);
        ApplyPendingDoorReleases();
        SaveDatabase();
    }

    private void PublishPlayerJusticeOutcome(
        NeuralNPC guard,
        string outcome)
    {
        pendingGuardIncidents.TryGetValue(
            GetNpcKey(guard),
            out GuardIncident? incident);
        string context = incident == null
            ? ""
            : " after " + incident.callerName + " reported that "
                + Player.Instance.playerName + " " + incident.reason;
        string memory = Player.Instance.playerName + " " + outcome + " by "
            + guard.GetFinalName() + context + ".";
        NeuralNPC.AddGlobalMemory(
            memory,
            npc => npc.faction == Faction.Silverpine);
        pendingGuardIncidents.Remove(GetNpcKey(guard));
    }

    private static void PublishNpcJusticeOutcome(
        NeuralNPC target,
        string outcome,
        string law)
    {
        string memory = target.GetFinalName() + " " + outcome + " by "
            + Player.Instance.playerName + " for breaking the law: " + law
            + ".";
        NeuralNPC.AddGlobalMemory(
            memory,
            npc => npc.faction == Faction.Silverpine);
    }

    internal void SuppressJusticeDialogRumors(NeuralNPC npc)
    {
        suppressedDialogRumorKeys.Add(GetNpcKey(npc));
    }

    internal bool IsJusticeDialogRumorSuppressed(NeuralNPC npc)
    {
        return suppressedDialogRumorKeys.Contains(GetNpcKey(npc));
    }

    internal void ClearJusticeDialogRumorSuppression(NeuralNPC npc)
    {
        suppressedDialogRumorKeys.Remove(GetNpcKey(npc));
    }

    private static string NormalizeLaw(string law)
    {
        string normalized = law.Trim().TrimEnd('.');
        return normalized.Length <= 500
            ? normalized
            : normalized.Substring(0, 500).TrimEnd();
    }
}
