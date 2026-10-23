using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace PortableResonator.Tests
{
    /// <summary>
    /// Several players on one server: each player's playback, cylinder UI and watched attributes stay their own, a
    /// playing resonator follows its stack between players, and <see cref="ResonatorModSystem.FindSource"/> picks
    /// the holder each listener hears. The music itself is client-only.
    /// </summary>
    public class MultiplayerScenarios : AtlasScenarioBase
    {
        [AtlasScenario]
        public async Task Two_players_play_independently()
        {
            var a = await World.JoinPlayer("MpIndepA");
            var b = await World.JoinPlayer("MpIndepB");
            var cylinders = TestKit.Cylinders(World);
            Assert.True(cylinders.Count >= 2, "need two different vanilla cylinders");
            var (slotA, itemA) = TestKit.GiveLoaded(World, a, cylinders[0]);
            var (slotB, itemB) = TestKit.GiveLoaded(World, b, cylinders[1]);

            Assert.True(itemA.TogglePlayback(a.Player, slotA));
            Assert.True(itemB.TogglePlayback(b.Player, slotB));

            var attrsA = a.Entity.WatchedAttributes;
            var attrsB = b.Entity.WatchedAttributes;
            Assert.Equal(itemA.GetTrack(slotA.Itemstack), attrsA.GetString(ResonatorModSystem.TrackAttr));
            Assert.Equal(itemB.GetTrack(slotB.Itemstack), attrsB.GetString(ResonatorModSystem.TrackAttr));
            Assert.NotEqual(attrsA.GetString(ResonatorModSystem.TrackAttr), attrsB.GetString(ResonatorModSystem.TrackAttr));
            Assert.NotEqual(attrsA.GetInt(ResonatorModSystem.PlayIdAttr), attrsB.GetInt(ResonatorModSystem.PlayIdAttr));

            // Stopping A leaves B's playback (and its play id) alone, and B keeps advancing.
            int playIdB = attrsB.GetInt(ResonatorModSystem.PlayIdAttr);
            Assert.True(itemA.TogglePlayback(a.Player, slotA));
            Assert.Null(attrsA.GetString(ResonatorModSystem.TrackAttr));

            double before = itemB.GetPosition(slotB.Itemstack);
            await TestKit.UntilTicked(World, () => itemB.GetPosition(slotB.Itemstack) > before);
            Assert.True(itemB.GetPosition(slotB.Itemstack) > before);
            Assert.Equal(playIdB, attrsB.GetInt(ResonatorModSystem.PlayIdAttr));
            Assert.True(itemB.IsPlaying(slotB.Itemstack));
        }

        [AtlasScenario]
        public async Task Two_players_on_the_same_track_get_separate_play_ids()
        {
            var a = await World.JoinPlayer("MpSameA");
            var b = await World.JoinPlayer("MpSameB");
            var (slotA, itemA) = TestKit.GiveLoaded(World, a);
            var (slotB, itemB) = TestKit.GiveLoaded(World, b);

            itemA.TogglePlayback(a.Player, slotA);
            itemB.TogglePlayback(b.Player, slotB);

            var attrsA = a.Entity.WatchedAttributes;
            var attrsB = b.Entity.WatchedAttributes;
            Assert.Equal(attrsA.GetString(ResonatorModSystem.TrackAttr), attrsB.GetString(ResonatorModSystem.TrackAttr));
            Assert.NotEqual(attrsA.GetInt(ResonatorModSystem.PlayIdAttr), attrsB.GetInt(ResonatorModSystem.PlayIdAttr));

            // A restarting doesn't restart B: listeners near B must not jump back to B's start.
            int playIdB = attrsB.GetInt(ResonatorModSystem.PlayIdAttr);
            itemA.TogglePlayback(a.Player, slotA);
            itemA.TogglePlayback(a.Player, slotA);
            await World.Ticks(30);
            Assert.Equal(playIdB, attrsB.GetInt(ResonatorModSystem.PlayIdAttr));
        }

        [AtlasScenario]
        public async Task Cylinder_uis_are_per_player()
        {
            var a = await World.JoinPlayer("MpUiA");
            var b = await World.JoinPlayer("MpUiB");
            var (slotA, itemA) = TestKit.GiveResonator(World, a);
            var (slotB, itemB) = TestKit.GiveResonator(World, b);

            var invA = itemA.OpenCylinderInventory(a.Player, slotA);
            var invB = itemB.OpenCylinderInventory(b.Player, slotB);
            Assert.NotEqual(invA.InventoryID, invB.InventoryID);

            // A cylinder put in A's UI loads only A's resonator.
            invA[0].Itemstack = new ItemStack(TestKit.Cylinders(World)[0]);
            invA[0].MarkDirty();
            Assert.Equal(TestKit.LoadedResonator, slotA.Itemstack.Collectible.Code.ToString());
            Assert.Equal(TestKit.EmptyResonator, slotB.Itemstack.Collectible.Code.ToString());
            Assert.True(invB[0].Empty);

            // Closing A's UI leaves B's open.
            a.Player.InventoryManager.CloseInventory(invA);
            Assert.Null(a.Player.InventoryManager.GetInventory(invA.InventoryID));
            Assert.NotNull(b.Player.InventoryManager.GetInventory(invB.InventoryID));
        }

        [AtlasScenario]
        public async Task A_playing_resonator_handed_to_another_player_plays_for_them()
        {
            var a = await World.JoinPlayer("MpHandA");
            var b = await World.JoinPlayer("MpHandB");
            var (slotA, item) = TestKit.GiveLoaded(World, a);
            TestKit.ClearHotbar(b);
            item.TogglePlayback(a.Player, slotA);

            var attrsA = a.Entity.WatchedAttributes;
            var attrsB = b.Entity.WatchedAttributes;
            int playIdA = attrsA.GetInt(ResonatorModSystem.PlayIdAttr);
            await TestKit.UntilTicked(World, () => item.GetPosition(slotA.Itemstack) > 0);
            double handedOverAt = item.GetPosition(slotA.Itemstack);

            // The stack moves as-is (playing flag and position included), as a trade or drop + pickup would move it.
            var stack = slotA.Itemstack;
            slotA.Itemstack = null;
            slotA.MarkDirty();
            TestKit.PutInHotbar(b, 0, stack);

            await TestKit.UntilTicked(World, () => attrsB.GetString(ResonatorModSystem.TrackAttr) != null && attrsA.GetString(ResonatorModSystem.TrackAttr) == null);
            Assert.Null(attrsA.GetString(ResonatorModSystem.TrackAttr));
            Assert.Equal(item.GetTrack(stack), attrsB.GetString(ResonatorModSystem.TrackAttr));
            // A new holder is a new playback, so B's listeners start it fresh, from where A left off.
            Assert.NotEqual(playIdA, attrsB.GetInt(ResonatorModSystem.PlayIdAttr));
            Assert.True(attrsB.GetFloat(ResonatorModSystem.PositionAttr) >= (float)handedOverAt - 0.001f);
        }

        [AtlasScenario]
        public async Task A_player_leaving_does_not_stop_the_others()
        {
            var a = await World.JoinPlayer("MpLeaveA");
            var b = await World.JoinPlayer("MpLeaveB");
            var (slotA, itemA) = TestKit.GiveLoaded(World, a);
            var (slotB, itemB) = TestKit.GiveLoaded(World, b);
            itemA.TogglePlayback(a.Player, slotA);
            itemB.TogglePlayback(b.Player, slotB);
            int playIdB = b.Entity.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr);

            a.Player.Disconnect("test");
            await World.Until(() => !a.IsConnected, 300);
            Assert.False(a.IsConnected);

            double before = itemB.GetPosition(slotB.Itemstack);
            await TestKit.UntilTicked(World, () => itemB.GetPosition(slotB.Itemstack) > before);
            Assert.True(itemB.GetPosition(slotB.Itemstack) > before);
            Assert.Equal(playIdB, b.Entity.WatchedAttributes.GetInt(ResonatorModSystem.PlayIdAttr));

            // Listeners only consider players still online: B now hears only themselves.
            var online = onlineAmong(a, b);
            Assert.DoesNotContain(online, p => p.PlayerUID == a.Player.PlayerUID);
            var source = ResonatorModSystem.FindSource(b.Entity, online, out _);
            Assert.Equal(b.Entity.EntityId, source?.EntityId);
        }

        [AtlasScenario]
        public async Task Listeners_hear_the_nearest_holder_in_range()
        {
            var near = await World.JoinPlayer("MpNear");
            var far = await World.JoinPlayer("MpFar");
            var listener = await World.JoinPlayer("MpListen");
            var origin = near.Position.Copy();

            await near.TeleportTo(origin);
            await far.TeleportTo(origin.AddCopy(12, 0, 0));
            await listener.TeleportTo(origin.AddCopy(3, 0, 0));
            await World.Ticks(5);

            var (slotNear, itemNear) = TestKit.GiveLoaded(World, near);
            var (slotFar, itemFar) = TestKit.GiveLoaded(World, far);
            TestKit.ClearHotbar(listener);
            var players = onlineAmong(near, far, listener);

            // Nobody playing: nothing to hear.
            Assert.Null(ResonatorModSystem.FindSource(listener.Entity, players, out _));

            itemNear.TogglePlayback(near.Player, slotNear);
            itemFar.TogglePlayback(far.Player, slotFar);
            Assert.Equal(near.Entity.EntityId, ResonatorModSystem.FindSource(listener.Entity, players, out _)?.EntityId);

            // Walk over to the other holder: they're nearer now.
            await listener.TeleportTo(origin.AddCopy(10, 0, 0));
            await World.Ticks(5);
            Assert.Equal(far.Entity.EntityId, ResonatorModSystem.FindSource(listener.Entity, players, out _)?.EntityId);

            // A stopped holder drops out even when nearest.
            itemFar.TogglePlayback(far.Player, slotFar);
            Assert.Equal(near.Entity.EntityId, ResonatorModSystem.FindSource(listener.Entity, players, out _)?.EntityId);

            // Out of range of everyone.
            await listener.TeleportTo(origin.AddCopy(-(int)ResonatorModSystem.HearingRange - 10, 0, 0));
            await World.Ticks(5);
            Assert.Null(ResonatorModSystem.FindSource(listener.Entity, players, out _));

            // A holder hears their own resonator.
            Assert.Equal(near.Entity.EntityId, ResonatorModSystem.FindSource(near.Entity, players, out _)?.EntityId);
        }

        [AtlasScenario]
        public async Task A_late_joiner_picks_up_the_song_mid_play()
        {
            var host = await World.JoinPlayer("MpLateHost");
            var (slot, item) = TestKit.GiveLoaded(World, host);
            item.TogglePlayback(host.Player, slot);
            await TestKit.UntilTicked(World, () => item.GetPosition(slot.Itemstack) >= 1);

            var late = await World.JoinPlayer("MpLateJoin");
            await late.TeleportTo(host.Position.AddCopy(2, 0, 0));
            await World.Ticks(5);

            var source = ResonatorModSystem.FindSource(late.Entity, onlineAmong(host, late), out _);
            Assert.Equal(host.Entity.EntityId, source?.EntityId);
            // What the late listener reads to seek: the track and a position already under way.
            Assert.Equal(item.GetTrack(slot.Itemstack), source.WatchedAttributes.GetString(ResonatorModSystem.TrackAttr));
            Assert.True(source.WatchedAttributes.GetFloat(ResonatorModSystem.PositionAttr) >= 1);
        }

        /// <summary>
        /// The online players among <paramref name="players"/>, as the game lists them. Each class shares one server, so
        /// players from earlier scenarios are still online (some still playing) and would otherwise be heard too.
        /// </summary>
        private IPlayer[] onlineAmong(params ITestPlayer[] players)
        {
            var uids = players.Select(p => p.Player.PlayerUID).ToHashSet();
            return World.Api.World.AllOnlinePlayers.Where(p => uids.Contains(p.PlayerUID)).ToArray();
        }
    }
}
