#nullable enable

using Silverpine.ModdingTools;
using System;
using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SilverpineGuardSystem;

internal sealed class GuardManagerWindow : ModToolBehaviour
{
    private const int NpcsPerPage = 4;

    private enum WorldSelectionMode
    {
        JailDoor,
        JailCell
    }

    private static GuardManagerWindow? current;
    private GameObject canvasRoot = null!;
    private InventoryUI inventory = null!;
    private Button buttonTemplate = null!;
    private bool open;
    private bool selectingWorld;
    private int npcPage;

    internal static void Open(
        InventoryUI inventory,
        ModToolSession session)
    {
        CloseCurrent();
        Plugin.Instance.RefreshSaveIdentity();

        ModOverlay overlay = ModUi.CreateOverlay(
            inventory,
            "Silverpine Guard and Jail Manager",
            new Vector2(900f, 900f),
            sortingOrder: 520);
        GuardManagerWindow window =
            overlay.Panel.AddComponent<GuardManagerWindow>();
        current = window;
        window.canvasRoot = overlay.Root;
        window.inventory = inventory;
        window.buttonTemplate = ModUi.GetInventoryButtonTemplate(inventory);
        window.AttachSession(session);
        window.open = true;
        window.Build();
    }

    internal static void CloseCurrent()
    {
        if (current != null)
            current.Close();
    }

    private void Update()
    {
        if (open && !selectingWorld && Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    private void Build()
    {
        VerticalLayoutGroup layout =
            GetComponent<VerticalLayoutGroup>()
            ?? gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(42, 42, 25, 25);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;

        ModUi.CloneTitle(buttonTemplate, transform, "Guard & Jail Manager");
        AddStatus();
        AddGuardButtons();
        AddButton("Toggle Jail Door by Clicking World", () =>
            BeginWorldSelection(WorldSelectionMode.JailDoor));
        AddButton("Select Jail Destination Tile", () =>
            BeginWorldSelection(WorldSelectionMode.JailCell));
        AddButton(
            "Fine: " + Plugin.Instance.FineModeLabel
            + " (click to change)",
            CycleFine);
        AddButton(
            "Jail time: " + Plugin.Instance.SentenceModeLabel
            + " (click to change)",
            CycleSentenceMode);
        AddButton(
            "Jail value: " + Plugin.Instance.SentenceValueLabel
            + " (click to change)",
            CycleSentenceValue);
        AddButton("Close", Close);
    }

    private void AddStatus()
    {
        int loadedGuards = Plugin.Instance.GetLoadedNpcs()
            .Count(Plugin.Instance.IsConfiguredGuard);
        string cell = Plugin.Instance.TryGetJailCell(out Vector2Int position)
            ? position.x + ", " + position.y
            : "not selected";
        string sentence = Plugin.Instance.RemainingSentenceHours > 0
            ? Plugin.Instance.RemainingSentenceHours
                + " game hours remain in the active sentence."
            : "No active jail sentence.";
        string status =
            "Loaded designated guards: " + loadedGuards
            + " | Jail doors: " + Plugin.Instance.JailDoorCount + "\n"
            + "Jail destination: " + cell + " | " + sentence;

        TextMeshProUGUI text = ModUi.CloneTitle(
            buttonTemplate,
            transform,
            status,
            preferredHeight: 92f);
        text.fontSize = 21f;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void AddGuardButtons()
    {
        var npcs = Plugin.Instance.GetLoadedNpcs();
        int pageCount = Math.Max(
            1,
            Mathf.CeilToInt(npcs.Count / (float)NpcsPerPage));
        npcPage = Mathf.Clamp(npcPage, 0, pageCount - 1);

        TextMeshProUGUI heading = ModUi.CloneTitle(
            buttonTemplate,
            transform,
            "Click an NPC to designate or remove a guard",
            preferredHeight: 42f);
        heading.fontSize = 22f;

        foreach (NeuralNPC npc in npcs
                     .Skip(npcPage * NpcsPerPage)
                     .Take(NpcsPerPage))
        {
            NeuralNPC captured = npc;
            bool isGuard = Plugin.Instance.IsConfiguredGuard(npc);
            AddButton(
                (isGuard ? "[GUARD] " : "[CIVILIAN] ")
                + npc.GetFinalName(),
                () =>
                {
                    Plugin.Instance.ToggleGuard(captured);
                    Rebuild();
                });
        }

        if (npcs.Count == 0)
        {
            Button empty = AddButton(
                "No NeuralNPCs are currently loaded",
                () => { });
            empty.interactable = false;
        }

        AddButton(
            "Guard Page " + (npcPage + 1) + "/" + pageCount
            + (pageCount > 1 ? " (next)" : ""),
            () =>
            {
                if (pageCount > 1)
                    npcPage = (npcPage + 1) % pageCount;
                Rebuild();
            });
    }

    private Button AddButton(string label, Action action) =>
        ModUi.CloneButton(buttonTemplate, transform, label, action);

    private void CycleFine()
    {
        if (Plugin.Instance.UsesBaseGameFine)
        {
            Plugin.Instance.SetFineAmount(10);
            Rebuild();
            return;
        }

        int next = Plugin.Instance.FineAmount switch
        {
            < 20 => 20,
            < 50 => 50,
            < 100 => 100,
            < 200 => 200,
            < 500 => 500,
            < 1000 => 1000,
            _ => -1
        };
        if (next < 0)
            Plugin.Instance.UseBaseGameFine();
        else
            Plugin.Instance.SetFineAmount(next);
        Rebuild();
    }

    private void CycleSentenceMode()
    {
        Plugin.Instance.ToggleSentenceMode();
        Rebuild();
    }

    private void CycleSentenceValue()
    {
        if (Plugin.Instance.UsesFineBasedSentence)
        {
            int nextGold = Plugin.Instance.GoldPerJailHour switch
            {
                < 2 => 2,
                < 5 => 5,
                < 10 => 10,
                < 20 => 20,
                < 50 => 50,
                < 100 => 100,
                _ => 1
            };
            Plugin.Instance.SetGoldPerJailHour(nextGold);
        }
        else
        {
            int nextHours = Plugin.Instance.FixedSentenceHours switch
            {
                < 6 => 6,
                < 12 => 12,
                < 24 => 24,
                < 48 => 48,
                < 72 => 72,
                < 168 => 168,
                _ => 1
            };
            Plugin.Instance.SetFixedSentenceHours(nextHours);
        }
        Rebuild();
    }

    private void BeginWorldSelection(WorldSelectionMode mode)
    {
        if (!selectingWorld)
            StartCoroutine(SelectWorldCoroutine(mode));
    }

    private IEnumerator SelectWorldCoroutine(WorldSelectionMode mode)
    {
        selectingWorld = true;
        CanvasGroup group = canvasRoot.GetComponent<CanvasGroup>()
            ?? canvasRoot.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        bool modToolsWorldInput = InventoryModTools.BeginWorldInput();
        if (!modToolsWorldInput
            && PauseMenuManager.Instance != null
            && PauseMenuManager.Instance.open)
        {
            PauseMenuManager.Instance.Close();
        }

        UpperNotificationUI.Instance.OneOff(
            mode == WorldSelectionMode.JailDoor
                ? "Click a loaded door to toggle its jail-door designation. "
                    + "Press Escape to cancel."
                : "Click the exact loaded tile where jailed players should be "
                    + "teleported. Press Escape to cancel.");

        yield return null;
        yield return null;
        yield return null;

        while (open)
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                UpperNotificationUI.Instance.OneOff("World selection cancelled.");
                RestoreAfterWorldSelection(group, modToolsWorldInput);
                yield break;
            }

            if (!Input.GetKeyUp(KeyCode.Mouse0))
            {
                yield return null;
                continue;
            }

            Camera? camera = FindWorldCamera();
            if (camera == null)
            {
                UpperNotificationUI.Instance.OneOff(
                    "No active world camera was found.");
                RestoreAfterWorldSelection(group, modToolsWorldInput);
                yield break;
            }

            Vector3 world = camera.ScreenToWorldPoint(Input.mousePosition);
            Vector2Int tile = Vector2Int.RoundToInt(
                new Vector2(world.x, world.y));

            if (mode == WorldSelectionMode.JailDoor)
            {
                Door? door = Door.allDoors.FirstOrDefault(candidate =>
                    candidate != null
                    && candidate.transform.GetVector2IntPosition() == tile);
                if (door == null)
                {
                    UpperNotificationUI.Instance.OneOff(
                        "There is no loaded door on " + tile.x + ", "
                        + tile.y + ". Try again or press Escape.");
                    yield return null;
                    continue;
                }

                bool wasJailDoor = Plugin.Instance.IsJailDoor(door);
                string display = Plugin.GetDoorDisplayName(door);
                Plugin.Instance.ToggleJailDoor(door);
                UpperNotificationUI.Instance.OneOff(
                    display + (wasJailDoor
                        ? " is no longer a jail door."
                        : " is now a jail door."));
            }
            else
            {
                if (!Turfs.IsValidTurf(tile))
                {
                    UpperNotificationUI.Instance.OneOff(
                        "No loaded world tile exists at " + tile.x + ", "
                        + tile.y + ". Try again or press Escape.");
                    yield return null;
                    continue;
                }
                if (TurfCollider.HasImpassableCollider(tile))
                {
                    UpperNotificationUI.Instance.OneOff(
                        "That tile is blocked. Select a passable jail tile.");
                    yield return null;
                    continue;
                }

                Plugin.Instance.SetJailCell(tile);
                UpperNotificationUI.Instance.OneOff(
                    "Jail destination set to " + tile.x + ", " + tile.y + ".");
            }

            RestoreAfterWorldSelection(group, modToolsWorldInput);
            Rebuild();
            yield break;
        }
    }

    private static Camera? FindWorldCamera()
    {
        Camera? camera = Camera.main;
        if (camera == null && Player.Instance != null)
        {
            Transform cameraTransform =
                Player.Instance.transform.Find("Main Camera");
            if (cameraTransform != null)
                camera = cameraTransform.GetComponent<Camera>();
        }
        return camera ?? Camera.allCameras.FirstOrDefault(candidate =>
            candidate != null && candidate.isActiveAndEnabled);
    }

    private void RestoreAfterWorldSelection(
        CanvasGroup group,
        bool modToolsWorldInput)
    {
        if (modToolsWorldInput)
        {
            InventoryModTools.EndWorldInput();
        }
        else if (PauseMenuManager.Instance != null
            && !PauseMenuManager.Instance.open)
        {
            PauseMenuManager.Instance.Toggle();
        }

        group.alpha = 1f;
        group.interactable = true;
        group.blocksRaycasts = true;
        selectingWorld = false;
    }

    private void Rebuild()
    {
        foreach (Transform child in transform.Cast<Transform>().ToArray())
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
        Build();
    }

    private void Close()
    {
        if (!open)
            return;
        open = false;
        selectingWorld = false;
        ReleaseSession();
        if (canvasRoot != null)
            Destroy(canvasRoot);
        else
            Destroy(gameObject);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(current, this))
            current = null;
        base.OnDestroy();
    }
}
