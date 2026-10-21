using Vintagestory.API.Common;

namespace PortableResonator
{
    /// <summary>
    /// The portable resonator's one-slot cylinder UI inventory, id <c>portableresonator-&lt;player uid&gt;</c>. Both sides
    /// build and open it when the player right-clicks (held interactions run on both), so the normal inventory
    /// packets carry the slot clicks. It's only a view: on the server every change is written straight into the
    /// resonator stack in <see cref="ResonatorSlot"/>. A cylinder that couldn't be written (the resonator left that
    /// slot) is handed back to the player when the UI closes.
    /// </summary>
    public class InventoryPortableResonator : InventoryGeneric
    {
        public const string ClassName = "portableresonator";

        /// <summary>The slot holding the resonator when the UI was opened.</summary>
        public ItemSlot ResonatorSlot { get; }

        private readonly IPlayer owner;
        private bool stored = true;

        public InventoryPortableResonator(IPlayer owner, ItemSlot resonatorSlot, ICoreAPI api)
            : base(1, ClassName, owner.PlayerUID, api, (id, self) => new ItemSlotTuningCylinder(self))
        {
            this.owner = owner;
            ResonatorSlot = resonatorSlot;

            var item = resonatorSlot.Itemstack?.Collectible as ItemPortableResonator;
            this[0].Itemstack = item?.GetCylinder(resonatorSlot.Itemstack);

            if (api.Side == EnumAppSide.Server)
            {
                SlotModified += _ => stored = ItemPortableResonator.StoreCylinder(owner, ResonatorSlot, this[0].Itemstack);
                OnInventoryClosed += onClosed;
            }
        }

        private void onClosed(IPlayer player)
        {
            var leftover = this[0].Itemstack;
            this[0].Itemstack = null;
            if (stored || leftover == null) return;

            if (!owner.InventoryManager.TryGiveItemstack(leftover, true))
            {
                Api.World.SpawnItemEntity(leftover, owner.Entity.Pos.XYZ);
            }
        }
    }

    /// <summary>Accepts one tuning cylinder (anything the vanilla resonator block plays).</summary>
    public class ItemSlotTuningCylinder : ItemSlot
    {
        public ItemSlotTuningCylinder(InventoryBase inventory) : base(inventory) { }

        public override int MaxSlotStackSize => 1;

        public override bool CanHold(ItemSlot sourceSlot)
        {
            return ItemPortableResonator.IsCylinder(sourceSlot?.Itemstack) && base.CanHold(sourceSlot);
        }

        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        {
            return ItemPortableResonator.IsCylinder(sourceSlot?.Itemstack) && base.CanTakeFrom(sourceSlot, priority);
        }
    }
}
