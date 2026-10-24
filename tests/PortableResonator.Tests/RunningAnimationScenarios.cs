using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;

namespace PortableResonator.Tests
{
    /// <summary>
    /// The "running" shape animation shown while a track plays. The baked meshes and their rendering are client-only;
    /// these check the pose maths (<see cref="ShapeAnimationPose"/>) against the real item shape, and which stacks
    /// count as playing (<see cref="ItemPortableResonator.ShowsPlaying"/>).
    /// </summary>
    public class RunningAnimationScenarios : AtlasScenarioBase
    {
        private const float Tolerance = 0.01f;

        private (Shape shape, Animation animation) runningAnimation()
        {
            var shape = Shape.TryGet(World.Api, new AssetLocation("portableresonator:shapes/item/tool/portableresonator.json"));
            Assert.NotNull(shape);
            var animation = shape.Animations?.FirstOrDefault(a => a.Code == ItemPortableResonator.RunningAnimation);
            Assert.NotNull(animation);
            return (shape, animation);
        }

        [AtlasScenario]
        public Task Poses_follow_the_keyframes()
        {
            var (_, animation) = runningAnimation();
            Assert.Equal(30, animation.QuantityFrames);

            // Frame 0: at rest, so the resting mesh and the first frame match.
            foreach (var name in new[] { "Crank", "OuterGear", "GearLarger30" })
            {
                Assert.Equal(0, ShapeAnimationPose.PoseAt(animation, name, 0).Rotation.Length(), 3);
            }

            // Keyframes 0 and 29: a straight lerp between them (the flag on frame 29 only affects the wrap after it).
            float half = 360f * 15 / 29;
            Assert.Equal(-half, ShapeAnimationPose.PoseAt(animation, "Crank", 15).Rotation.X, Tolerance);
            Assert.Equal(half, ShapeAnimationPose.PoseAt(animation, "OuterGear", 15).Rotation.X, Tolerance);
            Assert.Equal(0, ShapeAnimationPose.PoseAt(animation, "GearLarger30", 15).Rotation.Length(), 3);
            Assert.Equal(-360f, ShapeAnimationPose.PoseAt(animation, "Crank", 29).Rotation.X, Tolerance);
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public Task Applying_a_pose_turns_a_copy_of_the_shape()
        {
            var (shape, animation) = runningAnimation();
            double crank = shape.GetElementByName("Crank").RotationX;
            double gear = shape.GetElementByName("OuterGear").RotationX;

            var posed = shape.Clone();
            ShapeAnimationPose.Apply(posed, animation, 15);

            float half = 360f * 15 / 29;
            Assert.Equal(crank - half, posed.GetElementByName("Crank").RotationX, Tolerance);
            Assert.Equal(gear + half, posed.GetElementByName("OuterGear").RotationX, Tolerance);
            // The cached original is untouched.
            Assert.Equal(crank, shape.GetElementByName("Crank").RotationX);
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public async Task Own_stacks_show_playing_from_their_own_flag()
        {
            var player = await World.JoinPlayer("AnimOwn");
            var (slot, item) = TestKit.GiveLoaded(World, player);
            Assert.False(item.ShowsPlaying(slot.Itemstack, slot));

            item.TogglePlayback(player.Player, slot);
            Assert.True(item.ShowsPlaying(slot.Itemstack, slot));

            // A second, stopped resonator stays still even though its holder is publishing a track.
            TestKit.PutInHotbar(player, 1, slot.Itemstack.Clone());
            var second = TestKit.HotbarSlot(player, 1);
            second.Itemstack.Attributes.SetBool("playing", false);
            Assert.False(item.ShowsPlaying(second.Itemstack, second));

            item.TogglePlayback(player.Player, slot);
            Assert.False(item.ShowsPlaying(slot.Itemstack, slot));
        }

        [AtlasScenario]
        public async Task Another_players_copy_shows_playing_while_they_publish_a_track()
        {
            var player = await World.JoinPlayer("AnimRemote");
            var (slot, item) = TestKit.GiveLoaded(World, player);

            // What another client holds for this player's hand: the item code, without stack attributes.
            TestKit.PutInHotbar(player, 1, new ItemStack(TestKit.Item(World, TestKit.LoadedResonator)));
            TestKit.PutInHotbar(player, 2, new ItemStack(TestKit.Item(World, TestKit.EmptyResonator)));
            var loadedCopy = TestKit.HotbarSlot(player, 1);
            var emptyCopy = TestKit.HotbarSlot(player, 2);
            var emptyItem = (ItemPortableResonator)emptyCopy.Itemstack.Collectible;
            Assert.False(item.ShowsPlaying(loadedCopy.Itemstack, loadedCopy));

            item.TogglePlayback(player.Player, slot);
            Assert.True(item.ShowsPlaying(loadedCopy.Itemstack, loadedCopy));
            Assert.False(emptyItem.ShowsPlaying(emptyCopy.Itemstack, emptyCopy));
            // Outside a player's inventory there's no holder to ask.
            Assert.False(item.ShowsPlaying(loadedCopy.Itemstack, new DummySlot(loadedCopy.Itemstack)));

            item.TogglePlayback(player.Player, slot);
            Assert.False(item.ShowsPlaying(loadedCopy.Itemstack, loadedCopy));
        }
    }
}
