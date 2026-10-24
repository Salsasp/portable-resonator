using System.IO;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace PortableResonator.Tests
{
    /// <summary>
    /// The portable resonator worn by a tamed elk in a front side slot, where cooking pots and crocks go: it attaches
    /// through the elk's own slots (vanilla <see cref="EntityBehaviorAttachable"/>), and
    /// <see cref="ResonatorModSystem"/> keeps it playing and publishes it on the elk. The model on the elk is client-only.
    /// </summary>
    public class ElkScenarios : AtlasScenarioBase
    {
        private const string Elk = "game:tameddeer-elk-male-adult";
        private const string RightSide = "RFrontAP";
        private const string LeftSide = "LFrontAP";

        [AtlasScenario]
        public async Task Both_variants_fit_the_front_side_slots_like_a_crock()
        {
            var player = await World.JoinPlayer("ElkFits");
            var elk = spawnElk(player);
            var crock = World.Api.World.GetBlock(new AssetLocation("game:crock-blue-fired"));
            Assert.NotNull(crock);

            foreach (var code in new[] { TestKit.EmptyResonator, TestKit.LoadedResonator })
            {
                var resonator = new DummySlot(new ItemStack(TestKit.Item(World, code)));
                foreach (var ap in new[] { RightSide, LeftSide })
                {
                    var slot = gearSlot(elk, ap);
                    Assert.True(slot.CanHold(new DummySlot(new ItemStack(crock))), "crock should fit " + ap);
                    Assert.True(slot.CanHold(resonator), code + " should fit " + ap);
                }
                // Not where the saddle or bridle go.
                Assert.False(gearSlot(elk, "MidAP").CanHold(resonator));
                Assert.False(gearSlot(elk, "HeadAP").CanHold(resonator));
            }
        }

        [AtlasScenario]
        public Task Each_side_and_variant_has_its_own_attached_shape()
        {
            foreach (var (code, variant) in new[] { (TestKit.EmptyResonator, "empty"), (TestKit.LoadedResonator, "loaded") })
            {
                var item = TestKit.Item(World, code);
                var attachable = IAttachableToEntity.FromCollectible(item);
                foreach (var (slotCode, side) in new[] { ("frontrightside", "right"), ("frontleftside", "left") })
                {
                    var shape = attachable.GetAttachedShape(new ItemStack(item), slotCode);
                    Assert.Equal($"portableresonator:item/wearable/elk/portableresonator-{side}-{variant}", shape?.Base.ToString());
                    string file = Path.Combine(TestKit.RepoRoot, "assets", "portableresonator", "shapes", "item", "wearable", "elk", $"portableresonator-{side}-{variant}.json");
                    Assert.True(File.Exists(file), file);
                    // Only the loaded variant shows the cylinder.
                    Assert.Equal(variant == "loaded", File.ReadAllText(file).Contains("\"name\": \"Cylinder\""));
                }
            }
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public async Task A_playing_resonator_keeps_playing_on_the_elk()
        {
            var player = await World.JoinPlayer("ElkPlay");
            var elk = spawnElk(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            string track = item.GetTrack(slot.Itemstack);
            item.TogglePlayback(player.Player, slot);
            await TestKit.UntilTicked(World, () => item.GetPosition(slot.Itemstack) > 0);
            double attachedAt = item.GetPosition(slot.Itemstack);

            var worn = attach(slot, elk, RightSide);

            await TestKit.UntilTicked(World, () => elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr) != null);
            // The elk publishes the track now, picking up where the player was; the player no longer does.
            Assert.Equal(track, elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
            Assert.Null(player.Entity.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
            Assert.True(item.IsPlaying(worn.Itemstack));
            Assert.True(item.GetPosition(worn.Itemstack) >= attachedAt);

            int playId = elk.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr);
            double before = item.GetPosition(worn.Itemstack);
            await TestKit.UntilTicked(World, () => item.GetPosition(worn.Itemstack) > before);
            Assert.True(item.GetPosition(worn.Itemstack) > before);
            Assert.Equal(playId, elk.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr));
        }

        [AtlasScenario]
        public async Task Saving_or_syncing_the_elk_does_not_restart_the_song()
        {
            var player = await World.JoinPlayer("ElkSync");
            var elk = spawnElk(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            item.TogglePlayback(player.Player, slot);
            attach(slot, elk, LeftSide);
            await TestKit.UntilTicked(World, () => elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr) != null);
            int playId = elk.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr);

            // Every save and client sync writes the gear out and reads a copy back in (EntityBehaviorContainer.storeInv).
            var gear = elk.GetBehavior<EntityBehaviorAttachable>();
            var original = gearSlot(elk, LeftSide).Itemstack;
            gear.storeInv();
            Assert.NotSame(original, gearSlot(elk, LeftSide).Itemstack);

            double before = item.GetPosition(gearSlot(elk, LeftSide).Itemstack);
            await TestKit.UntilTicked(World, () => item.GetPosition(gearSlot(elk, LeftSide).Itemstack) > before);
            Assert.True(item.GetPosition(gearSlot(elk, LeftSide).Itemstack) > before);
            Assert.Equal(playId, elk.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr));
        }

        [AtlasScenario]
        public async Task Taking_it_back_moves_playback_to_the_player()
        {
            var player = await World.JoinPlayer("ElkDetach");
            var elk = spawnElk(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            item.TogglePlayback(player.Player, slot);
            var worn = attach(slot, elk, RightSide);
            await TestKit.UntilTicked(World, () => elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr) != null);
            await TestKit.UntilTicked(World, () => item.GetPosition(worn.Itemstack) >= 1);
            double detachedAt = item.GetPosition(worn.Itemstack);

            Assert.True(worn.TryPutInto(World.Api.World, slot) > 0);
            elk.GetBehavior<EntityBehaviorAttachable>().storeInv();

            await TestKit.UntilTicked(World, () => elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr) == null);
            Assert.Null(elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
            await TestKit.UntilTicked(World, () => player.Entity.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr) != null);
            Assert.Equal(item.GetTrack(slot.Itemstack), player.Entity.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
            Assert.True(item.GetPosition(slot.Itemstack) >= detachedAt);
        }

        [AtlasScenario]
        public async Task A_stopped_resonator_on_the_elk_stays_silent()
        {
            var player = await World.JoinPlayer("ElkStopped");
            var elk = spawnElk(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            var worn = attach(slot, elk, RightSide);

            await World.Ticks(40);
            Assert.False(item.IsPlaying(worn.Itemstack));
            Assert.Null(elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
        }

        [AtlasScenario]
        public async Task Listeners_hear_the_elk()
        {
            var player = await World.JoinPlayer("ElkHost");
            var listener = await World.JoinPlayer("ElkListen");
            var elk = spawnElk(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            item.TogglePlayback(player.Player, slot);
            attach(slot, elk, RightSide);
            await TestKit.UntilTicked(World, () => elk.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr) != null);

            await listener.TeleportTo(World.PositionOf(elk).AsBlockPos.AddCopy(3, 0, 0));
            await World.Ticks(5);

            var source = ResonatorModSystem.FindSource(listener.Entity, new Entity[] { player.Entity, listener.Entity, elk }, out _);
            Assert.Equal(elk.EntityId, source?.EntityId);
        }

        [AtlasScenario]
        public async Task Clicks_on_an_entity_are_left_to_the_entity()
        {
            var player = await World.JoinPlayer("ElkClick");
            var elk = spawnElk(player);
            var (slot, item) = TestKit.GiveLoaded(World, player);
            var controls = player.Entity.Controls;
            var inventoryId = InventoryPortableResonator.ClassName + "-" + player.Player.PlayerUID;
            var onElk = new EntitySelection { Entity = elk, Position = World.PositionOf(elk).XYZ };

            // Ctrl + right-click is the elk's attach click; shift + right-click doesn't start the song either.
            foreach (bool ctrl in new[] { true, false })
            {
                controls.CtrlKey = ctrl;
                controls.ShiftKey = !ctrl;
                try
                {
                    var handling = EnumHandHandling.NotHandled;
                    item.OnHeldInteractStart(slot, player.Entity, null, onElk, true, ref handling);
                    Assert.NotEqual(EnumHandHandling.PreventDefault, handling);
                }
                finally
                {
                    controls.CtrlKey = false;
                    controls.ShiftKey = false;
                }
            }
            Assert.Null(player.Player.InventoryManager.GetInventory(inventoryId));
            Assert.False(item.IsPlaying(slot.Itemstack));
        }

        private Entity spawnElk(ITestPlayer near)
        {
            var elk = World.SpawnEntity(Elk, near.Position.AddCopy(2, 0, 0));
            Assert.NotNull(elk.GetBehavior<EntityBehaviorAttachable>());
            return elk;
        }

        private static ItemSlot gearSlot(Entity elk, string attachmentPoint)
        {
            var slot = elk.GetBehavior<EntityBehaviorAttachable>().GetSlotConfigFromAPName(attachmentPoint);
            Assert.NotNull(slot);
            return slot;
        }

        /// <summary>What the elk's attach click does on the server (EntityBehaviorAttachable.TryAttach): move the stack in, store the gear.</summary>
        private ItemSlot attach(ItemSlot from, Entity elk, string attachmentPoint)
        {
            var target = gearSlot(elk, attachmentPoint);
            Assert.True(target.CanHold(from));
            Assert.True(from.TryPutInto(World.Api.World, target) > 0);
            Assert.True(from.Empty);
            elk.GetBehavior<EntityBehaviorAttachable>().storeInv();
            return target;
        }
    }
}
