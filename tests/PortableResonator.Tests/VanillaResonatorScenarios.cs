using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace PortableResonator.Tests
{
    /// <summary>
    /// The portable resonator next to the vanilla resonator block: both take the same cylinders, cylinders move
    /// freely between them, and right-clicking the block with a portable resonator in hand never touches the
    /// portable one. Drives the block through <see cref="BlockResonator.OnBlockInteractStart"/>, as the engine does
    /// for a plain right-click; the block's music is client-only.
    /// </summary>
    public class VanillaResonatorScenarios : AtlasScenarioBase
    {
        [AtlasScenario]
        public async Task Both_resonators_accept_exactly_the_same_cylinders()
        {
            var player = await World.JoinPlayer("VanSame");
            var (block, pos, be) = placeResonator(player);

            // Everything either side might take: anything flagged as a disc, plus everything the portable accepts.
            var candidates = World.Api.World.Items
                .Where(i => i?.Code != null && (i.Attributes?["isPlayableDisc"].AsBool(false) == true || ItemPortableResonator.IsCylinder(new ItemStack(i))))
                .ToList();
            Assert.NotEmpty(candidates);

            foreach (var item in candidates)
            {
                TestKit.ClearHotbar(player);
                TestKit.PutInHotbar(player, 0, new ItemStack(item));

                interact(block, pos, player);
                bool vanillaTook = be.HasDisc;
                if (vanillaTook) interact(block, pos, player); // eject again for the next one

                Assert.True(vanillaTook == ItemPortableResonator.IsCylinder(new ItemStack(item)),
                    $"{item.Code}: vanilla resonator {(vanillaTook ? "takes" : "refuses")} it, portable {(vanillaTook ? "refuses" : "takes")} it");
                Assert.False(be.HasDisc);
            }
        }

        [AtlasScenario]
        public async Task The_vanilla_resonator_does_not_take_a_portable_resonator()
        {
            var player = await World.JoinPlayer("VanNoTake");
            var (block, pos, be) = placeResonator(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            string track = item.GetTrack(slot.Itemstack);

            interact(block, pos, player);

            Assert.False(be.HasDisc);
            Assert.Equal(TestKit.LoadedResonator, slot.Itemstack?.Collectible.Code.ToString());
            Assert.Equal(track, item.GetTrack(slot.Itemstack));
        }

        [AtlasScenario]
        public async Task Ejecting_from_the_vanilla_resonator_leaves_a_playing_portable_one_alone()
        {
            var player = await World.JoinPlayer("VanEject");
            var (block, pos, be) = placeResonator(player);
            var cylinders = TestKit.Cylinders(World);

            // Load the block with one cylinder, then hold a playing portable resonator with another.
            TestKit.ClearHotbar(player);
            TestKit.PutInHotbar(player, 0, new ItemStack(cylinders[0]));
            interact(block, pos, player);
            Assert.True(be.HasDisc);

            var (slot, item) = TestKit.GiveLoaded(World, player, cylinders[cylinders.Count > 1 ? 1 : 0]);
            item.TogglePlayback(player.Player, slot);
            int playId = player.Entity.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr);

            interact(block, pos, player);

            // The block hands its cylinder back; the portable resonator keeps its own cylinder and keeps playing.
            Assert.False(be.HasDisc);
            Assert.Equal(1, TestKit.CountCarried(player, cylinders[0].Code.ToString()));
            Assert.True(item.IsPlaying(slot.Itemstack));
            await World.Ticks(30);
            Assert.Equal(playId, player.Entity.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr));
        }

        [AtlasScenario]
        public async Task A_cylinder_moves_between_the_portable_and_vanilla_resonators()
        {
            var player = await World.JoinPlayer("VanMove");
            var (block, pos, be) = placeResonator(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            string track = item.GetTrack(slot.Itemstack);

            // Out of the portable resonator (what taking it out of the UI does), into the active slot.
            var cylinder = item.GetCylinder(slot.Itemstack);
            Assert.True(ItemPortableResonator.StoreCylinder(player.Player, slot, null));
            var resonator = slot.Itemstack;
            TestKit.PutInHotbar(player, 1, resonator);
            TestKit.PutInHotbar(player, 0, cylinder);

            // Into the block: it plays the same track the portable one did.
            interact(block, pos, player);
            Assert.True(be.HasDisc);
            Assert.True(TestKit.HotbarSlot(player, 0).Empty);
            Assert.Equal(track, be.Inventory[0].Itemstack?.ItemAttributes?["musicTrack"].AsString(null));

            // Back out of the block, and back into the portable resonator.
            interact(block, pos, player);
            Assert.False(be.HasDisc);
            var returned = findCarried(player, cylinder.Collectible.Code);
            Assert.NotNull(returned);

            var portableSlot = TestKit.HotbarSlot(player, 1);
            Assert.True(ItemPortableResonator.StoreCylinder(player.Player, portableSlot, returned.Itemstack));
            Assert.Equal(track, ((ItemPortableResonator)portableSlot.Itemstack.Collectible).GetTrack(portableSlot.Itemstack));
        }

        private (BlockResonator block, BlockPos pos, BlockEntityResonator be) placeResonator(ITestPlayer player)
        {
            var block = World.Api.World.Blocks.OfType<BlockResonator>().FirstOrDefault();
            Assert.True(block != null, "vanilla resonator block not found");

            var pos = player.Position.AddCopy(2, 0, 0);
            World.SetBlock(block.Code.ToString(), pos);
            var be = World.BlockEntityAt<BlockEntityResonator>(pos);
            Assert.NotNull(be);
            return (block, pos, be);
        }

        private void interact(BlockResonator block, BlockPos pos, ITestPlayer player)
        {
            block.OnBlockInteractStart(World.Api.World, player.Player, new BlockSelection { Position = pos });
        }

        private static ItemSlot findCarried(ITestPlayer player, AssetLocation code)
        {
            foreach (var slot in player.Player.InventoryManager.GetHotbarInventory())
            {
                if (slot.Itemstack?.Collectible.Code.Equals(code) == true) return slot;
            }
            return null;
        }
    }
}
