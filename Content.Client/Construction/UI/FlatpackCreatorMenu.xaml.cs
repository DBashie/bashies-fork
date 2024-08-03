using System.Linq;
using Content.Client.Materials;
using Content.Client.Message;
using Content.Client.UserInterface.Controls;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Materials;
using Robust.Client.AutoGenerated;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.Construction.UI;

[GenerateTypedNameReferences]
public sealed partial class FlatpackCreatorMenu : FancyWindow
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    private readonly ItemSlotsSystem _itemSlots;
    private readonly FlatpackSystem _flatpack;
    private readonly MaterialStorageSystem _materialStorage;
    private readonly SpriteSystem _spriteSystem;

    private readonly EntityUid _owner;

    [ValidatePrototypeId<EntityPrototype>]
    public const string NoBoardEffectId = "FlatpackerNoBoardEffect";

    private EntityUid? _currentBoard = EntityUid.Invalid;
    private EntityUid? _machinePreview;

    public event Action? PackButtonPressed;

    public FlatpackCreatorMenu(EntityUid uid)
    {
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);

        _itemSlots = _entityManager.System<ItemSlotsSystem>();
        _flatpack = _entityManager.System<FlatpackSystem>();
        _materialStorage = _entityManager.System<MaterialStorageSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();

        _owner = uid;

        PackButton.OnPressed += _ => PackButtonPressed?.Invoke();

        MaterialStorageControl.SetOwner(uid);
        InsertLabel.SetMarkup(Loc.GetString("flatpacker-ui-insert-board"));
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        if (_machinePreview is not { } && _entityManager.Deleted(_machinePreview))
        {
            _machinePreview = null;
            MachineSprite.SetEntity(_machinePreview);
        }

        if (!_entityManager.TryGetComponent<FlatpackCreatorComponent>(_owner, out var flatpacker) ||
            !_itemSlots.TryGetSlot(_owner, flatpacker.SlotId, out var itemSlot))
            return;

        MachineBoardComponent? machineBoardComp = null;
        if (flatpacker.Packing)
        {
            PackButton.Disabled = true;
        }
        else if (_currentBoard != null)
        {
            Dictionary<string, int> cost;
            if (_entityManager.TryGetComponent(_currentBoard, out machineBoardComp))
                cost = _flatpack.GetFlatpackCreationCost((_owner, flatpacker), (_currentBoard.Value, machineBoardComp));
            else
                cost = _flatpack.GetFlatpackCreationCost((_owner, flatpacker), null);

            PackButton.Disabled = !_materialStorage.CanChangeMaterialAmount(_owner, cost);
        }

        if (_currentBoard == itemSlot.Item)
            return;

        if (_machinePreview != null)
            _entityManager.DeleteEntity(_machinePreview);

        _currentBoard = itemSlot.Item;
        CostHeaderLabel.Visible = _currentBoard != null;
        InsertLabel.Visible = _currentBoard == null;

        if (_currentBoard is not null)
        {
            string? prototype = null;
            Dictionary<string, int>? cost = null;

            if (machineBoardComp != null || _entityManager.TryGetComponent(_currentBoard, out machineBoardComp))
            {
                prototype = machineBoardComp.Prototype;
                cost = _flatpack.GetFlatpackCreationCost((_owner, flatpacker), (_currentBoard.Value, machineBoardComp));
            }
            else if (_entityManager.TryGetComponent<ComputerBoardComponent>(_currentBoard, out var computerBoard))
            {
                prototype = computerBoard.Prototype;
                cost = _flatpack.GetFlatpackCreationCost((_owner, flatpacker), null);
            }

            if (prototype is not null && cost is not null)
            {
                var proto = _prototypeManager.Index<EntityPrototype>(prototype);
                _machinePreview = _entityManager.Spawn(proto.ID);
                _spriteSystem.ForceUpdate(_machinePreview.Value);
                MachineNameLabel.SetMessage(proto.Name);
                CostLabel.SetMarkup(GetCostString(cost));
            }
        }
        else
        {
            _machinePreview = _entityManager.Spawn(NoBoardEffectId);
            CostLabel.SetMessage(Loc.GetString("flatpacker-ui-no-board-label"));
            MachineNameLabel.SetMessage(" ");
            PackButton.Disabled = true;
        }

        MachineSprite.SetEntity(_machinePreview);
    }

    private string GetCostString(Dictionary<string, int> costs)
    {
        var orderedCosts = costs.OrderBy(p => p.Value).ToArray();
        var msg = new FormattedMessage();
        for (var i = 0; i < orderedCosts.Length; i++)
        {
            var (mat, amount) = orderedCosts[i];

            var matProto = _prototypeManager.Index<MaterialPrototype>(mat);

            var sheetVolume = _materialStorage.GetSheetVolume(matProto);
            var sheets = (float) -amount / sheetVolume;
            var amountText = Loc.GetString("lathe-menu-material-amount",
                ("amount", sheets),
                ("unit", Loc.GetString(matProto.Unit)));
            var text = Loc.GetString("lathe-menu-tooltip-display",
                ("amount", amountText),
                ("material", Loc.GetString(matProto.Name)));

            msg.AddMarkup(text);

            if (i != orderedCosts.Length - 1)
                msg.PushNewline();
        }

        return msg.ToMarkup();
    }

    public override void Close()
    {
        base.Close();

        _entityManager.DeleteEntity(_machinePreview);
        _machinePreview = null;
    }
}
