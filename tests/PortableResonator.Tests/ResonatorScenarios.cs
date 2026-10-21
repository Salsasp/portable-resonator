using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace PortableResonator.Tests
{
    /// <summary>
    /// <see cref="ItemPortableResonator"/>: the cylinder UI inventory (server side of it), start/stop, and the
    /// watched attributes <see cref="ResonatorModSystem"/> publishes for listeners. The dialog and the music
    /// itself are client-only.
    /// </summary>
    public class ResonatorScenarios : AtlasScenarioBase
    {
        private const string Empty = "portableresonator:portableresonator-empty";
        private const string Loaded = "portableresonator:portableresonator-loaded";

        [AtlasScenario]
        public Task Both_variants_are_registered_with_the_class()
        {
            Assert.IsType<ItemPortableResonator>(TestKit.Item(World, Empty));
            Assert.IsType<ItemPortableResonator>(TestKit.Item(World, Loaded));
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public Task Vanilla_has_tuning_cylinders_to_play()
        {
            Assert.NotNull(cylinderItem());
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public async Task Cylinder_slot_only_takes_tuning_cylinders()
        {
            var player = await World.JoinPlayer("ResoSlot");
            var (slot, item) = giveResonator(player);
            var inv = item.OpenCylinderInventory(player.Player, slot);

            Assert.False(inv[0].CanHold(new DummySlot(new ItemStack(TestKit.Item(World, "game:gear-temporal")))));
            Assert.True(inv[0].CanHold(new DummySlot(new ItemStack(cylinderItem()))));
            Assert.Equal(1, inv[0].MaxSlotStackSize);
        }

        [AtlasScenario]
        public async Task Placing_a_cylinder_in_the_ui_loads_the_resonator()
        {
            var player = await World.JoinPlayer("ResoInsert");
            var (slot, item) = giveResonator(player);
            var inv = item.OpenCylinderInventory(player.Player, slot);
            Assert.True(inv[0].Empty);

            inv[0].Itemstack = new ItemStack(cylinderItem());
            inv[0].MarkDirty();

            Assert.Equal(Loaded, slot.Itemstack.Collectible.Code.ToString());
            Assert.Equal(cylinderItem().Code, ((ItemPortableResonator)slot.Itemstack.Collectible).GetCylinder(slot.Itemstack).Collectible.Code);

            // Closing hands nothing back: the cylinder is in the resonator, not duplicated into the inventory.
            player.Player.InventoryManager.CloseInventory(inv);
            Assert.Equal(0, TestKit.CountCarried(player, cylinderItem().Code.ToString()));
        }

        [AtlasScenario]
        public async Task Taking_the_cylinder_out_of_the_ui_empties_the_resonator_and_stops_it()
        {
            var player = await World.JoinPlayer("ResoEject");
            var (slot, item) = giveLoaded(player);
            item.TogglePlayback(player.Player, slot);
            var attrs = player.Entity.WatchedAttributes;
            Assert.NotNull(attrs.GetString(ResonatorModSystem.TrackAttr));

            var inv = item.OpenCylinderInventory(player.Player, slot);
            Assert.Equal(cylinderItem().Code, inv[0].Itemstack?.Collectible.Code);

            inv[0].Itemstack = null;
            inv[0].MarkDirty();

            Assert.Equal(Empty, slot.Itemstack.Collectible.Code.ToString());
            Assert.Null(((ItemPortableResonator)slot.Itemstack.Collectible).GetCylinder(slot.Itemstack));
            Assert.Null(attrs.GetString(ResonatorModSystem.TrackAttr));
        }

        [AtlasScenario]
        public async Task A_cylinder_that_could_not_be_stored_is_given_back_on_close()
        {
            var player = await World.JoinPlayer("ResoGiveBack");
            var (slot, item) = giveResonator(player);
            var inv = item.OpenCylinderInventory(player.Player, slot);

            // The resonator leaves the slot while the UI is still open.
            slot.Itemstack = null;
            inv[0].Itemstack = new ItemStack(cylinderItem());
            inv[0].MarkDirty();
            player.Player.InventoryManager.CloseInventory(inv);

            Assert.Equal(1, TestKit.CountCarried(player, cylinderItem().Code.ToString()));
        }

        [AtlasScenario]
        public async Task Play_publishes_the_track_and_stop_keeps_the_position()
        {
            var player = await World.JoinPlayer("ResoPlay");
            var (slot, item) = giveLoaded(player);
            var attrs = player.Entity.WatchedAttributes;

            Assert.True(item.TogglePlayback(player.Player, slot));
            Assert.Equal(item.GetTrack(slot.Itemstack), attrs.GetString(ResonatorModSystem.TrackAttr));
            int playId = attrs.GetInt(ResonatorModSystem.PlayIdAttr);

            await TestKit.UntilTicked(World, () => item.GetPosition(slot.Itemstack) > 0);
            Assert.True(item.GetPosition(slot.Itemstack) > 0);
            // Position updates don't count as a restart.
            Assert.Equal(playId, attrs.GetInt(ResonatorModSystem.PlayIdAttr));

            Assert.True(item.TogglePlayback(player.Player, slot));
            double stopped = item.GetPosition(slot.Itemstack);
            Assert.False(item.IsPlaying(slot.Itemstack));
            Assert.Null(attrs.GetString(ResonatorModSystem.TrackAttr));

            await World.Ticks(60);
            Assert.Equal(stopped, item.GetPosition(slot.Itemstack));

            // Starting again is a new playback, so listeners restart from the stored position.
            Assert.True(item.TogglePlayback(player.Player, slot));
            Assert.NotEqual(playId, attrs.GetInt(ResonatorModSystem.PlayIdAttr));
        }

        [AtlasScenario]
        public async Task Keeps_playing_when_not_held_and_stops_when_gone()
        {
            var player = await World.JoinPlayer("ResoCarry");
            var (slot, item) = giveLoaded(player);
            var attrs = player.Entity.WatchedAttributes;
            item.TogglePlayback(player.Player, slot);

            // Another hotbar slot, not the active one (test players have no bags, so no backpack item slots).
            var other = TestKit.HotbarSlot(player, 5);
            other.Itemstack = slot.Itemstack;
            slot.Itemstack = null;
            await World.Ticks(60);
            Assert.NotNull(attrs.GetString(ResonatorModSystem.TrackAttr));

            other.Itemstack = null;
            await TestKit.UntilTicked(World, () => attrs.GetString(ResonatorModSystem.TrackAttr) == null);
            Assert.Null(attrs.GetString(ResonatorModSystem.TrackAttr));
        }

        [AtlasScenario]
        public async Task Toggle_does_nothing_without_a_cylinder()
        {
            var player = await World.JoinPlayer("ResoNoCyl");
            var (slot, item) = giveResonator(player);

            Assert.False(item.TogglePlayback(player.Player, slot));
            Assert.Null(player.Entity.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
        }

        [AtlasScenario]
        public Task Placeable_on_the_ground_and_on_shelves()
        {
            foreach (var code in new[] { Empty, Loaded })
            {
                var item = TestKit.Item(World, code);
                Assert.NotNull(item.GetBehavior<CollectibleBehaviorGroundStorable>());
                Assert.Equal(EnumShelvableLayout.SingleCenter, BlockEntityShelf.GetShelvableLayout(new ItemStack(item)));
            }
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public async Task Ctrl_shift_right_click_is_left_to_ground_placement()
        {
            var player = await World.JoinPlayer("ResoCtrl");
            var (slot, item) = giveLoaded(player);
            var controls = player.Entity.Controls;
            controls.ShiftKey = true;
            controls.CtrlKey = true;
            try
            {
                var handling = EnumHandHandling.NotHandled;
                item.OnHeldInteractStart(slot, player.Entity, null, null, true, ref handling);
                Assert.False(item.IsPlaying(slot.Itemstack));
            }
            finally
            {
                controls.ShiftKey = false;
                controls.CtrlKey = false;
            }
        }

        [AtlasScenario]
        public async Task Ctrl_right_click_opens_the_ui_and_plain_right_click_is_left_to_shelves()
        {
            var player = await World.JoinPlayer("ResoOpen");
            var (slot, item) = giveLoaded(player);
            var controls = player.Entity.Controls;
            var inventoryId = InventoryPortableResonator.ClassName + "-" + player.Player.PlayerUID;

            var handling = EnumHandHandling.NotHandled;
            item.OnHeldInteractStart(slot, player.Entity, null, null, true, ref handling);
            Assert.NotEqual(EnumHandHandling.PreventDefault, handling);
            Assert.Null(player.Player.InventoryManager.GetInventory(inventoryId));

            controls.CtrlKey = true;
            try
            {
                handling = EnumHandHandling.NotHandled;
                item.OnHeldInteractStart(slot, player.Entity, null, null, true, ref handling);
                Assert.Equal(EnumHandHandling.PreventDefault, handling);
                Assert.NotNull(player.Player.InventoryManager.GetInventory(inventoryId));
                Assert.False(item.IsPlaying(slot.Itemstack));
            }
            finally
            {
                controls.CtrlKey = false;
            }
        }

        private Item cylinderItem()
        {
            return World.Api.World.Items.FirstOrDefault(i => i?.Code != null && ItemPortableResonator.IsCylinder(new ItemStack(i)));
        }

        private (ItemSlot slot, ItemPortableResonator item) giveResonator(ITestPlayer player)
        {
            TestKit.ClearHotbar(player);
            var item = (ItemPortableResonator)TestKit.Item(World, Empty);
            TestKit.PutInHotbar(player, 0, new ItemStack(item));
            return (TestKit.HotbarSlot(player, 0), item);
        }

        private (ItemSlot slot, ItemPortableResonator item) giveLoaded(ITestPlayer player)
        {
            var (slot, _) = giveResonator(player);
            Assert.True(ItemPortableResonator.StoreCylinder(player.Player, slot, new ItemStack(cylinderItem())));
            return (slot, (ItemPortableResonator)slot.Itemstack.Collectible);
        }
    }
}
