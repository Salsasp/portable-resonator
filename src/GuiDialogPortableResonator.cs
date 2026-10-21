using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace PortableResonator
{
    /// <summary>
    /// One tuning cylinder slot plus a status line. Closes itself when the resonator leaves the slot it was
    /// opened from (hotbar switch, dropped), and closing closes the inventory on both sides.
    /// </summary>
    public class GuiDialogPortableResonator : GuiDialogGeneric
    {
        private readonly InventoryPortableResonator inventory;

        public GuiDialogPortableResonator(InventoryPortableResonator inventory, ICoreClientAPI capi)
            : base(Lang.Get("portableresonator:resonator-gui-title"), capi)
        {
            this.inventory = inventory;
            compose();
        }

        public override string ToggleKeyCombinationCode => null;

        private void compose()
        {
            ElementBounds slotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 1, 1);
            ElementBounds infoBounds = ElementBounds.Fixed(65, 38, 220, 40);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(slotBounds, infoBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            SingleComposer = capi.Gui
                .CreateCompo("portableresonator-cylinder", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(inventory, p => capi.Network.SendPacketClient(p), 1, new[] { 0 }, slotBounds, "cylinderSlot")
                    .AddDynamicText("", CairoFont.WhiteSmallText(), infoBounds, "info")
                .EndChildElements()
                .Compose();

            updateInfo();
            inventory.SlotModified += _ => updateInfo();
        }

        private void updateInfo()
        {
            var stack = inventory.ResonatorSlot.Itemstack;
            var item = stack?.Collectible as ItemPortableResonator;
            string text = inventory[0].Empty
                ? Lang.Get("portableresonator:resonator-gui-insert")
                : Lang.Get(item?.IsPlaying(stack) == true ? "portableresonator:resonator-playing" : "portableresonator:resonator-gui-stopped");
            SingleComposer?.GetDynamicText("info")?.SetNewText(text);
        }

        public override void OnRenderGUI(float deltaTime)
        {
            var active = capi.World.Player?.InventoryManager.ActiveHotbarSlot;
            if (active != inventory.ResonatorSlot || active?.Itemstack?.Collectible is not ItemPortableResonator)
            {
                TryClose();
                return;
            }
            base.OnRenderGUI(deltaTime);
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            SingleComposer?.GetSlotGrid("cylinderSlot")?.OnGuiClosed(capi);
            capi.World.Player?.InventoryManager.CloseInventoryAndSync(inventory);
        }
    }
}
