using System.Collections.Generic;
using UnityEngine;

namespace SilverpineGuardSystem;

public sealed partial class Plugin
{
    /// <summary>
    /// Dispatch configured guards for an observed crime outside dialogue. The reason
    /// completes "the player ...". Uses the same pending incident and justice flow as
    /// call_guards, without asking the language model or ending unrelated dialogue.
    /// Returns false when this save/location has no available configured responder
    /// or the responder is already busy or cannot accept the new response routine.
    /// Must be called on Unity's main thread.
    /// </summary>
    public static bool TryReportWitnessedCrime(NeuralNPC caller, string reason, Vector2Int incident)
    {
        if (ReferenceEquals(Instance, null) || caller == null || Player.Instance == null
            || !caller.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(reason)) return false;
        Instance.RefreshSaveIdentity();
        if (Instance.currentSave == null || !PersistenceZone.IsNPCInsidePersistenceZone(
                PersistenceZoneName.Silverpine, caller.npcName)) return false;

        List<NeuralNPC> guards = Instance.IsConfiguredGuard(caller)
            && caller.GetComponent<NPCRoutineExecutor>() != null
                ? new List<NeuralNPC> { caller }
                : Instance.GetRespondingGuards(caller);
        bool dispatched = false;
        foreach (NeuralNPC guard in guards)
        {
            NPCRoutineExecutor executor = guard.GetComponent<NPCRoutineExecutor>();
            if (executor == null) continue;
            // Do not stack response routines or replace an incident already under investigation.
            if (Instance.IsManagedGuardResponse(guard)) continue;
            string activity = "responding to " + caller.GetFinalName() + " calling the guards because "
                + Player.Instance.playerName + " " + reason.Trim().TrimEnd('.');
            var response = new NPCRoutine(activity, activity, activity, incident, new RoutineArgument_GuardResponse());
            executor.StartOverrideRoutine(response);
            if (!ReferenceEquals(executor.currentRoutine, response)) continue;
            Instance.pendingGuardIncidents[GetNpcKey(guard)] = new GuardIncident
            {
                callerName = caller.GetFinalName(), reason = reason.Trim().TrimEnd('.')
            };
            guard.AddThingThatHappened(activity + ".", overwrite: true);
            Log.LogInfo("Witnessed crime from " + caller.GetFinalName() + " dispatched " + guard.GetFinalName() + ": " + reason);
            dispatched = true;
        }
        if (dispatched)
            AnimationHelper.Instance.SpawnFloatingText("<color=\"red\">Called the guards!</color>", caller.transform);
        return dispatched;
    }
}
