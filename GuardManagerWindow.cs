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
    private const float DesiredPanelWidth = 1560f;
    private const float DesiredPanelHeight = 980f;
    private const float HorizontalScreenMargin = 80f;
    private const float VerticalScreenMargin = 60f;

    private enum ManagerView
    {
        Main,
        NpcGuards
    }

    private enum GuardRosterFilter
    {
        All,
        Guards,
        Civilians
    }

    private enum WorldSelectionMode
    {
        JailDoor,
        JailCell
    }

    private static GuardManagerWindow? current;
    private GameObject canvasRoot = null!;
    private RectTransform panelRect = null!;
    private InventoryUI inventory = null!;
    private Button buttonTemplate = null!;
    private bool open;
    private bool selectingWorld;
    private ManagerView view;
    private GuardRosterFilter guardRosterFilter;
    private string guardSearch = "";
    private RectTransform? guardListContent;
    private ScrollRect? guardListScroll;
    private ResponsiveGuardGrid? guardGrid;
    private TextMeshProUGUI? guardRosterStatus;
    private Button? guardFilterButton;

    internal static void Open(
        InventoryUI inventory,
        ModToolSession session)
    {
        CloseCurrent();
        Plugin.Instance.RefreshSaveIdentity();

        ModOverlay overlay = ModUi.CreateOverlay(
            inventory,
            "Silverpine Guard and Jail Manager",
            new Vector2(DesiredPanelWidth, DesiredPanelHeight),
            sortingOrder: 520);
        CanvasScaler scaler = overlay.Root.GetComponent<CanvasScaler>();
        scaler.matchWidthOrHeight = 1f;
        GuardManagerWindow window =
            overlay.Panel.AddComponent<GuardManagerWindow>();
        current = window;
        window.canvasRoot = overlay.Root;
        window.panelRect = overlay.Panel.GetComponent<RectTransform>();
        window.inventory = inventory;
        window.buttonTemplate = ModUi.GetInventoryButtonTemplate(inventory);
        window.AttachSession(session);
        window.open = true;
        Canvas.ForceUpdateCanvases();
        window.ResizePanel();
        window.Build();
    }

    internal static void CloseCurrent()
    {
        if (current != null)
            current.Close();
    }

    private void Update()
    {
        ResizePanel();
        if (open && !selectingWorld && Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    private void ResizePanel()
    {
        if (canvasRoot == null || panelRect == null)
            return;

        RectTransform canvasRect = canvasRoot.GetComponent<RectTransform>();
        Vector2 canvasSize = canvasRect.rect.size;
        if (canvasSize.x <= 0f || canvasSize.y <= 0f)
            return;

        Vector2 targetSize = new(
            Mathf.Min(
                DesiredPanelWidth,
                Mathf.Max(320f, canvasSize.x - HorizontalScreenMargin)),
            Mathf.Min(
                DesiredPanelHeight,
                Mathf.Max(480f, canvasSize.y - VerticalScreenMargin)));
        if ((panelRect.sizeDelta - targetSize).sqrMagnitude > 0.25f)
            panelRect.sizeDelta = targetSize;
    }

    private void Build()
    {
        VerticalLayoutGroup layout =
            GetComponent<VerticalLayoutGroup>()
            ?? gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(46, 46, 28, 28);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        if (view == ManagerView.NpcGuards)
            BuildNpcGuardManager();
        else
            BuildMainManager();
    }

    private void BuildMainManager()
    {
        AddTitle("Guard & Jail Manager", 68f, 38f);
        AddStatus();
        int loadedNpcCount = Plugin.Instance.GetLoadedNpcs().Count;
        int guardCount = Plugin.Instance.GetLoadedNpcs()
            .Count(Plugin.Instance.IsConfiguredGuard);
        AddButton(
            "Manage NPC Guards...  (" + guardCount + " guards / "
            + loadedNpcCount + " loaded NPCs)",
            () =>
            {
                view = ManagerView.NpcGuards;
                Rebuild();
            },
            preferredHeight: 62f);
        AddButton(
            "Player Guard: " + (Plugin.Instance.PlayerIsGuard ? "ON" : "OFF")
            + " (click to toggle)",
            () =>
            {
                Plugin.Instance.TogglePlayerGuard();
                Rebuild();
            });
        AddButton(
            "Selected Jail: " + Plugin.Instance.SelectedJailCellLabel
            + " (click for next)",
            () =>
            {
                Plugin.Instance.SelectNextJailCell();
                Rebuild();
            });
        AddButton("Add New Jail Cell", () =>
        {
            Plugin.Instance.AddJailCell();
            Rebuild();
        });
        AddButton("Remove Selected Jail Cell", () =>
        {
            if (!Plugin.Instance.RemoveSelectedJailCell(out string reason))
                UpperNotificationUI.Instance.OneOff(reason);
            Rebuild();
        });
        AddButton("Connect/Disconnect Door to Selected Cell", () =>
            BeginWorldSelection(WorldSelectionMode.JailDoor));
        AddButton("Set Selected Cell Destination Tile", () =>
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

    private void BuildNpcGuardManager()
    {
        AddTitle("NPC Guard Designations", 68f, 38f);
        guardRosterStatus = AddTitle("", 44f, 21f);
        guardRosterStatus.enableWordWrapping = false;
        guardRosterStatus.overflowMode = TextOverflowModes.Ellipsis;
        CreateSearchInput();
        guardFilterButton = AddButton("", CycleGuardRosterFilter);
        CreateGuardList();
        AddButton(
            "Back to Guard & Jail Manager",
            () =>
            {
                view = ManagerView.Main;
                Rebuild();
            },
            preferredHeight: 58f);
        PopulateGuardList(preserveScroll: false);
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
            + " | Player guard: " + (Plugin.Instance.PlayerIsGuard ? "yes" : "no")
            + "\nCells: " + Plugin.Instance.JailCellCount
            + " | Doors: " + Plugin.Instance.JailDoorCount
            + " | NPC prisoners: " + Plugin.Instance.NpcPrisonerCount
            + "\nSelected destination: " + cell + " | " + sentence;

        TextMeshProUGUI text = ModUi.CloneTitle(
            buttonTemplate,
            transform,
            status,
            preferredHeight: 94f);
        text.fontSize = 20f;
        text.enableAutoSizing = true;
        text.fontSizeMin = 15f;
        text.fontSizeMax = 20f;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    private TextMeshProUGUI AddTitle(
        string label,
        float preferredHeight,
        float fontSize)
    {
        TextMeshProUGUI title = ModUi.CloneTitle(
            buttonTemplate,
            transform,
            label,
            preferredHeight);
        title.fontSize = fontSize;
        title.enableAutoSizing = true;
        title.fontSizeMin = Mathf.Min(15f, fontSize);
        title.fontSizeMax = fontSize;
        return title;
    }

    private Button AddButton(
        string label,
        Action action,
        float preferredHeight = 52f) =>
        AddButton(transform, label, action, preferredHeight);

    private Button AddButton(
        Transform parent,
        string label,
        Action action,
        float preferredHeight = 52f)
    {
        Button button = ModUi.CloneButton(
            buttonTemplate,
            parent,
            label,
            action,
            preferredHeight);
        TextMeshProUGUI? text =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
        {
            text.enableAutoSizing = true;
            text.fontSizeMin = 14f;
            text.fontSizeMax = 28f;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }
        return button;
    }

    private void CreateSearchInput()
    {
        GameObject inputObject = new(
            "Guard Search",
            typeof(RectTransform),
            typeof(Image),
            typeof(TMP_InputField),
            typeof(LayoutElement));
        RectTransform inputRect = inputObject.GetComponent<RectTransform>();
        inputRect.SetParent(transform, false);
        LayoutElement inputLayout = inputObject.GetComponent<LayoutElement>();
        inputLayout.preferredHeight = 58f;

        Image background = inputObject.GetComponent<Image>();
        if (buttonTemplate.targetGraphic is Image sourceImage)
        {
            background.sprite = sourceImage.sprite;
            background.type = sourceImage.type;
            background.color = sourceImage.color;
        }
        else
        {
            background.color = new Color(0.2f, 0.16f, 0.12f, 1f);
        }

        GameObject viewportObject = new(
            "Text Area",
            typeof(RectTransform),
            typeof(RectMask2D));
        RectTransform viewport = viewportObject.GetComponent<RectTransform>();
        viewport.SetParent(inputRect, false);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(22f, 7f);
        viewport.offsetMax = new Vector2(-22f, -7f);

        TextMeshProUGUI sourceText =
            buttonTemplate.GetComponentInChildren<TextMeshProUGUI>(true);
        TextMeshProUGUI inputText = Instantiate(
            sourceText, viewport, worldPositionStays: false);
        ConfigureInputText(inputText, "");
        TextMeshProUGUI placeholder = Instantiate(
            sourceText, viewport, worldPositionStays: false);
        ConfigureInputText(placeholder, "Search NPC names...");
        placeholder.fontStyle = FontStyles.Italic;
        Color placeholderColor = placeholder.color;
        placeholderColor.a = 0.55f;
        placeholder.color = placeholderColor;

        TMP_InputField input = inputObject.GetComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.textViewport = viewport;
        input.textComponent = inputText;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.characterLimit = 80;
        input.onFocusSelectAll = false;
        input.colors = buttonTemplate.colors;
        input.SetTextWithoutNotify(guardSearch);
        input.onValueChanged.AddListener(value =>
        {
            guardSearch = value ?? "";
            PopulateGuardList(preserveScroll: false);
        });
    }

    private static void ConfigureInputText(
        TextMeshProUGUI text,
        string value)
    {
        text.text = value;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.enableAutoSizing = true;
        text.fontSizeMin = 15f;
        text.fontSizeMax = 27f;
        text.raycastTarget = false;
        RectTransform rect = text.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero;
    }

    private void CreateGuardList()
    {
        GameObject hostObject = new(
            "Guard Roster",
            typeof(RectTransform),
            typeof(ScrollRect),
            typeof(LayoutElement));
        RectTransform host = hostObject.GetComponent<RectTransform>();
        host.SetParent(transform, false);
        LayoutElement hostLayout = hostObject.GetComponent<LayoutElement>();
        hostLayout.minHeight = 220f;
        hostLayout.preferredHeight = 620f;
        hostLayout.flexibleHeight = 1f;

        GameObject viewportObject = new(
            "Guard Roster Viewport",
            typeof(RectTransform),
            typeof(Image),
            typeof(RectMask2D));
        RectTransform viewport = viewportObject.GetComponent<RectTransform>();
        viewport.SetParent(host, false);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = new Vector2(-28f, 0f);
        Image viewportImage = viewportObject.GetComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.16f);
        viewportImage.raycastTarget = true;

        GameObject contentObject = new(
            "Guard Roster Content",
            typeof(RectTransform),
            typeof(GridLayoutGroup));
        guardListContent = contentObject.GetComponent<RectTransform>();
        guardListContent.SetParent(viewport, false);
        guardListContent.anchorMin = new Vector2(0f, 1f);
        guardListContent.anchorMax = new Vector2(1f, 1f);
        guardListContent.pivot = new Vector2(0.5f, 1f);
        guardListContent.anchoredPosition = Vector2.zero;
        guardListContent.sizeDelta = Vector2.zero;

        GridLayoutGroup grid = contentObject.GetComponent<GridLayoutGroup>();
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        guardGrid = contentObject.AddComponent<ResponsiveGuardGrid>();
        guardGrid.Configure(viewport, guardListContent, grid);

        Scrollbar scrollbar = CreateScrollbar(host);
        guardListScroll = hostObject.GetComponent<ScrollRect>();
        guardListScroll.viewport = viewport;
        guardListScroll.content = guardListContent;
        guardListScroll.horizontal = false;
        guardListScroll.vertical = true;
        guardListScroll.movementType = ScrollRect.MovementType.Clamped;
        guardListScroll.inertia = true;
        guardListScroll.decelerationRate = 0.135f;
        guardListScroll.scrollSensitivity = 48f;
        guardListScroll.verticalScrollbar = scrollbar;
        guardListScroll.verticalScrollbarVisibility =
            ScrollRect.ScrollbarVisibility.Permanent;
        guardListScroll.verticalScrollbarSpacing = 6f;
    }

    private static Scrollbar CreateScrollbar(RectTransform parent)
    {
        GameObject scrollbarObject = new(
            "Guard Roster Scrollbar",
            typeof(RectTransform),
            typeof(Image),
            typeof(Scrollbar));
        RectTransform scrollbarRect =
            scrollbarObject.GetComponent<RectTransform>();
        scrollbarRect.SetParent(parent, false);
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = Vector2.one;
        scrollbarRect.pivot = new Vector2(1f, 0.5f);
        scrollbarRect.offsetMin = new Vector2(-20f, 0f);
        scrollbarRect.offsetMax = Vector2.zero;
        Image background = scrollbarObject.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.32f);

        GameObject slidingObject = new(
            "Sliding Area",
            typeof(RectTransform));
        RectTransform sliding = slidingObject.GetComponent<RectTransform>();
        sliding.SetParent(scrollbarRect, false);
        sliding.anchorMin = Vector2.zero;
        sliding.anchorMax = Vector2.one;
        sliding.offsetMin = new Vector2(3f, 3f);
        sliding.offsetMax = new Vector2(-3f, -3f);

        GameObject handleObject = new(
            "Handle",
            typeof(RectTransform),
            typeof(Image));
        RectTransform handle = handleObject.GetComponent<RectTransform>();
        handle.SetParent(sliding, false);
        handle.anchorMin = Vector2.zero;
        handle.anchorMax = Vector2.one;
        handle.offsetMin = Vector2.zero;
        handle.offsetMax = Vector2.zero;
        Image handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(0.78f, 0.68f, 0.50f, 0.92f);

        Scrollbar scrollbar = scrollbarObject.GetComponent<Scrollbar>();
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handleImage;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.size = 0.2f;
        return scrollbar;
    }

    private void PopulateGuardList(bool preserveScroll)
    {
        if (guardListContent == null || guardGrid == null)
            return;

        float scrollPosition = preserveScroll && guardListScroll != null
            ? guardListScroll.verticalNormalizedPosition
            : 1f;
        foreach (Transform child in guardListContent
                     .Cast<Transform>()
                     .ToArray())
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var allNpcs = Plugin.Instance.GetLoadedNpcs();
        string search = guardSearch.Trim();
        var visibleNpcs = allNpcs.Where(npc =>
        {
            bool guard = Plugin.Instance.IsConfiguredGuard(npc);
            bool passesFilter = guardRosterFilter switch
            {
                GuardRosterFilter.Guards => guard,
                GuardRosterFilter.Civilians => !guard,
                _ => true
            };
            return passesFilter
                && (search.Length == 0
                    || npc.GetFinalName().IndexOf(
                        search,
                        StringComparison.OrdinalIgnoreCase) >= 0);
        }).ToList();

        foreach (NeuralNPC npc in visibleNpcs)
            AddGuardRosterButton(npc);

        int entryCount = visibleNpcs.Count;
        if (entryCount == 0)
        {
            Button empty = AddButton(
                guardListContent,
                allNpcs.Count == 0
                    ? "No NeuralNPCs are currently loaded"
                    : "No NPCs match the current search and filter",
                () => { },
                preferredHeight: 56f);
            empty.interactable = false;
            entryCount = 1;
        }

        guardGrid.SetEntryCount(entryCount);
        UpdateGuardRosterLabels(visibleNpcs.Count, allNpcs.Count);
        Canvas.ForceUpdateCanvases();
        guardGrid.RefreshLayout();
        if (guardListScroll != null)
            guardListScroll.verticalNormalizedPosition = scrollPosition;
    }

    private void AddGuardRosterButton(NeuralNPC npc)
    {
        NeuralNPC captured = npc;
        Button? row = null;
        row = AddButton(
            guardListContent!,
            GetGuardRosterLabel(captured),
            () =>
            {
                Plugin.Instance.ToggleGuard(captured);
                if (guardRosterFilter == GuardRosterFilter.All && row != null)
                {
                    ConfigureGuardRosterButton(row, captured);
                    UpdateGuardRosterLabels();
                }
                else
                {
                    PopulateGuardList(preserveScroll: true);
                }
            },
            preferredHeight: 56f);
        ConfigureGuardRosterButton(row, captured);
    }

    private static string GetGuardRosterLabel(NeuralNPC npc) =>
        (Plugin.Instance.IsConfiguredGuard(npc)
            ? "[GUARD]  "
            : "[CIVILIAN]  ")
        + npc.GetFinalName()
        + (npc.IsCustomNPC() ? "  [Custom NPC]" : "");

    private static void ConfigureGuardRosterButton(
        Button button,
        NeuralNPC npc)
    {
        TextMeshProUGUI? text =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text == null)
            return;
        text.text = GetGuardRosterLabel(npc);
        text.color = Plugin.Instance.IsConfiguredGuard(npc)
            ? new Color(0.68f, 1f, 0.68f, 1f)
            : Color.white;
    }

    private void CycleGuardRosterFilter()
    {
        guardRosterFilter = guardRosterFilter switch
        {
            GuardRosterFilter.All => GuardRosterFilter.Guards,
            GuardRosterFilter.Guards => GuardRosterFilter.Civilians,
            _ => GuardRosterFilter.All
        };
        PopulateGuardList(preserveScroll: false);
    }

    private void UpdateGuardRosterLabels(
        int visibleCount = -1,
        int totalCount = -1)
    {
        var allNpcs = Plugin.Instance.GetLoadedNpcs();
        if (totalCount < 0)
            totalCount = allNpcs.Count;
        if (visibleCount < 0)
        {
            string search = guardSearch.Trim();
            visibleCount = allNpcs.Count(npc =>
            {
                bool guard = Plugin.Instance.IsConfiguredGuard(npc);
                bool passesFilter = guardRosterFilter switch
                {
                    GuardRosterFilter.Guards => guard,
                    GuardRosterFilter.Civilians => !guard,
                    _ => true
                };
                return passesFilter
                    && (search.Length == 0
                        || npc.GetFinalName().IndexOf(
                            search,
                            StringComparison.OrdinalIgnoreCase) >= 0);
            });
        }

        int guardCount = allNpcs.Count(Plugin.Instance.IsConfiguredGuard);
        if (guardRosterStatus != null)
        {
            guardRosterStatus.text = "Showing " + visibleCount + " of "
                + totalCount + " loaded NPCs  |  " + guardCount
                + " designated guards";
        }
        if (guardFilterButton != null)
        {
            SetButtonLabel(
                guardFilterButton,
                "Show: " + (guardRosterFilter switch
                    {
                        GuardRosterFilter.Guards => "GUARDS ONLY",
                        GuardRosterFilter.Civilians => "CIVILIANS ONLY",
                        _ => "ALL NPCS"
                    }) + "  (click to change)");
        }
    }

    private static void SetButtonLabel(Button button, string label)
    {
        TextMeshProUGUI? text =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
            text.text = label;
    }

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
                ? "Click a loaded door to connect or disconnect it from "
                    + Plugin.Instance.SelectedJailCellLabel
                    + ". Press Escape to cancel."
                : "Click the exact loaded tile used as the destination for "
                    + Plugin.Instance.SelectedJailCellLabel
                    + ". Press Escape to cancel.");

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

                UpperNotificationUI.Instance.OneOff(
                    Plugin.Instance.ToggleJailDoor(door));
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
                    Plugin.Instance.SelectedJailCellLabel
                    + " destination set to " + tile.x + ", " + tile.y + ".");
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

internal sealed class ResponsiveGuardGrid : MonoBehaviour
{
    private const float HorizontalPadding = 18f;
    private const float VerticalPadding = 18f;
    private const float HorizontalSpacing = 12f;
    private const float VerticalSpacing = 12f;
    private const float CellHeight = 56f;

    private RectTransform viewport = null!;
    private RectTransform content = null!;
    private GridLayoutGroup grid = null!;
    private int entryCount;
    private float lastViewportWidth = -1f;
    private float lastViewportHeight = -1f;

    internal void Configure(
        RectTransform viewportRect,
        RectTransform contentRect,
        GridLayoutGroup layout)
    {
        viewport = viewportRect;
        content = contentRect;
        grid = layout;
        RefreshLayout();
    }

    internal void SetEntryCount(int value)
    {
        entryCount = Mathf.Max(0, value);
        RefreshLayout();
    }

    internal void RefreshLayout()
    {
        if (viewport == null || content == null || grid == null)
            return;

        float width = viewport.rect.width;
        float height = viewport.rect.height;
        if (width <= 1f || height <= 1f)
            return;

        int columns = width >= 1220f
            ? 3
            : width >= 760f
                ? 2
                : 1;
        float usableWidth = Mathf.Max(
            1f,
            width
            - HorizontalPadding * 2f
            - HorizontalSpacing * (columns - 1));
        grid.padding = new RectOffset(
            Mathf.RoundToInt(HorizontalPadding),
            Mathf.RoundToInt(HorizontalPadding),
            Mathf.RoundToInt(VerticalPadding),
            Mathf.RoundToInt(VerticalPadding));
        grid.spacing = new Vector2(HorizontalSpacing, VerticalSpacing);
        grid.cellSize = new Vector2(usableWidth / columns, CellHeight);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;

        int rows = entryCount == 0
            ? 0
            : Mathf.CeilToInt(entryCount / (float)columns);
        float requiredHeight = VerticalPadding * 2f
            + rows * CellHeight
            + Mathf.Max(0, rows - 1) * VerticalSpacing;
        content.sizeDelta = new Vector2(
            0f,
            Mathf.Max(height, requiredHeight));
        lastViewportWidth = width;
        lastViewportHeight = height;
    }

    private void LateUpdate()
    {
        if (viewport == null)
            return;
        if (Mathf.Abs(viewport.rect.width - lastViewportWidth) > 0.5f
            || Mathf.Abs(viewport.rect.height - lastViewportHeight) > 0.5f)
        {
            RefreshLayout();
        }
    }
}
