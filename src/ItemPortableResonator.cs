using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace PortableResonator
{
    /// <summary>
    /// Handheld resonator. Ctrl + right-click opens a one-slot UI for the tuning cylinder
    /// (<see cref="InventoryPortableResonator"/>); shift + right-click starts/stops the song, resuming where it
    /// stopped. Plain right-click is left free for shelf placement. The cylinder lives in the stack's attributes, and
    /// the <c>cylinder</c> variant (<c>empty</c> / <c>loaded</c>) mirrors it, because item codes reach other
    /// clients while stack attributes don't. Playback itself is run by <see cref="ResonatorModSystem"/>.
    /// </summary>
    public class ItemPortableResonator : Item
    {
        private const string CylinderAttr = "cylinder";
        private const string PlayingAttr = "playing";
        private const string PositionAttr = "position";

        private AssetLocation swapSound;
        private AssetLocation playSound;
        private WorldInteraction[] interactions;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            var attr = Attributes?["portableResonator"];
            swapSound = AssetLocation.Create(attr?["swapSound"].AsString("game:sounds/effect/latch") ?? "game:sounds/effect/latch");
            playSound = AssetLocation.Create(attr?["playSound"].AsString("game:sounds/block/vinyl") ?? "game:sounds/block/vinyl");

            if (api.Side == EnumAppSide.Client) interactions = createInteractions(api);
        }

        #region Stack state

        /// <summary>A vanilla tuning cylinder: anything the resonator block would play.</summary>
        public static bool IsCylinder(ItemStack stack)
        {
            return stack?.ItemAttributes?["isPlayableDisc"].AsBool(false) == true
                && stack.ItemAttributes["musicTrack"].AsString(null) != null;
        }

        public ItemStack GetCylinder(ItemStack stack)
        {
            var cylinder = stack?.Attributes.GetItemstack(CylinderAttr);
            cylinder?.ResolveBlockOrItem(api.World);
            return cylinder?.Collectible == null ? null : cylinder;
        }

        public bool HasCylinder(ItemStack stack) => GetCylinder(stack) != null;

        /// <summary>The loaded cylinder's music track, or null.</summary>
        public string GetTrack(ItemStack stack) => GetCylinder(stack)?.ItemAttributes?["musicTrack"].AsString(null);

        public bool IsPlaying(ItemStack stack) => stack?.Attributes.GetBool(PlayingAttr) == true && GetTrack(stack) != null;

        /// <summary>Seconds into the track. Grows past the track's end; listeners wrap it, since playback loops.</summary>
        public double GetPosition(ItemStack stack) => stack?.Attributes.GetDouble(PositionAttr) ?? 0;

        public void SetPosition(ItemStack stack, double seconds) => stack.Attributes.SetDouble(PositionAttr, seconds);

        #endregion

        #region Server actions (also what the tests drive)

        /// <summary>
        /// Writes <paramref name="cylinder"/> (null = removed) into the resonator in <paramref name="slot"/> and swaps
        /// the variant to match. A different cylinder stops playback and rewinds. Returns false if the slot no longer
        /// holds a resonator.
        /// </summary>
        public static bool StoreCylinder(IPlayer player, ItemSlot slot, ItemStack cylinder)
        {
            if (slot?.Itemstack?.Collectible is not ItemPortableResonator item) return false;
            var api = item.api;

            var old = item.GetCylinder(slot.Itemstack);
            bool same = old != null && cylinder != null && old.Equals(api.World, cylinder, GlobalConstants.IgnoredStackAttributes);
            if (same) return true;

            var stack = item.withVariant(slot.Itemstack, cylinder == null ? "empty" : "loaded");
            if (cylinder == null) stack.Attributes.RemoveAttribute(CylinderAttr);
            else stack.Attributes.SetItemstack(CylinderAttr, cylinder.Clone());
            stack.Attributes.RemoveAttribute(PlayingAttr);
            stack.Attributes.RemoveAttribute(PositionAttr);
            slot.Itemstack = stack;
            slot.MarkDirty();

            api.ModLoader.GetModSystem<ResonatorModSystem>().Refresh(player as IServerPlayer);
            api.World.PlaySoundAt(item.swapSound, player.Entity, null, true, 16);
            return true;
        }

        /// <summary>Starts the loaded cylinder's song, or stops it if it's playing. Stopping keeps the position.</summary>
        public bool TogglePlayback(IPlayer player, ItemSlot slot)
        {
            var stack = slot.Itemstack;
            if (GetTrack(stack) == null) return false;

            var system = api.ModLoader.GetModSystem<ResonatorModSystem>();
            // Bank the time played so far before the flag flips.
            system.Refresh(player as IServerPlayer);

            bool play = !IsPlaying(stack);
            stack.Attributes.SetBool(PlayingAttr, play);
            slot.MarkDirty();
            system.Refresh(player as IServerPlayer);

            api.World.PlaySoundAt(swapSound, player.Entity, null, true, 16); // swap sound plays regardless of song state
            if (play) {
                api.World.PlaySoundAt(playSound, player.Entity, null, true, 16);
            }

            return true;
        }

        /// <summary>
        /// Builds and opens the cylinder UI inventory for the resonator in <paramref name="slot"/>, replacing one
        /// already open. Call on both sides; the client also shows the dialog.
        /// </summary>
        public InventoryPortableResonator OpenCylinderInventory(IPlayer player, ItemSlot slot)
        {
            var manager = player.InventoryManager;
            var existing = manager.GetInventory(InventoryPortableResonator.ClassName + "-" + player.PlayerUID);
            if (existing != null) manager.CloseInventory(existing);

            var inventory = new InventoryPortableResonator(player, slot, api);
            manager.OpenInventory(inventory);
            return inventory;
        }

        private ItemStack withVariant(ItemStack stack, string state)
        {
            var item = api.World.GetItem(CodeWithVariant("cylinder", state)) ?? this;
            return new ItemStack(item) { Attributes = stack.Attributes.Clone() };
        }

        #endregion

        #region Interaction

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            // Plain right-click is left to the targeted block (shelf placement); ctrl + shift + right-click is ground
            // placement (the GroundStorable behavior, run by base).
            var controls = byEntity.Controls;
            if (!firstEvent || controls.CtrlKey == controls.ShiftKey || byEntity is not EntityPlayer { Player: { } player })
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            handling = EnumHandHandling.PreventDefault;

            if (controls.ShiftKey)
            {
                if (GetTrack(slot.Itemstack) == null)
                {
                    if (api is ICoreClientAPI capi && byEntity == capi.World.Player?.Entity)
                    {
                        capi.TriggerIngameError(this, "resonatornocylinder", Lang.Get("portableresonator:ingameerror-resonator-nocylinder"));
                    }
                    return;
                }
                if (api.Side == EnumAppSide.Server) TogglePlayback(player, slot);
                return;
            }

            if (api is ICoreClientAPI clientApi)
            {
                // Clicks in the world can't reach us while the dialog has the mouse, so this only ever opens.
                if (clientApi.Gui.OpenedGuis.Any(g => g is GuiDialogPortableResonator)) return;
                new GuiDialogPortableResonator(OpenCylinderInventory(player, slot), clientApi).TryOpen();
            }
            else
            {
                OpenCylinderInventory(player, slot);
            }
        }

        private WorldInteraction[] createInteractions(ICoreAPI api)
        {
            return ObjectCacheUtil.GetOrCreate(api, "portableResonatorInteractions", () => new[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "portableresonator:heldhelp-resonator-open",
                    HotKeyCode = "ctrl",
                    MouseButton = EnumMouseButton.Right
                },
                new WorldInteraction
                {
                    ActionLangCode = "portableresonator:heldhelp-resonator-toggle",
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right
                }
            });
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            if (interactions == null) return base.GetHeldInteractionHelp(inSlot);

            // Play/stop only applies once a cylinder is loaded.
            var shown = HasCylinder(inSlot.Itemstack) ? interactions : new[] { interactions[0] };
            return shown.Append(base.GetHeldInteractionHelp(inSlot));
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

            var cylinder = GetCylinder(inSlot.Itemstack);
            if (cylinder == null)
            {
                dsc.AppendLine(Lang.Get("portableresonator:resonator-empty"));
                return;
            }

            dsc.AppendLine(Lang.Get("portableresonator:resonator-loaded", cylinder.GetName()));
            dsc.AppendLine(Lang.Get(IsPlaying(inSlot.Itemstack) ? "portableresonator:resonator-playing" : "portableresonator:resonator-gui-stopped"));
        }

        #endregion
    }
}
