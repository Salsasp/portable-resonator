using System.IO;
using Atlas.Api;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace PortableResonator.Tests
{
    /// <summary>Shared setup for scenarios: item lookup and hotbar handling.</summary>
    internal static class TestKit
    {
        public const string Domain = "portableresonator";

        /// <summary>
        /// The repository checkout (modinfo.json + assets/). Found from the test assembly's location, not
        /// AppContext.BaseDirectory: Atlas points that at the game install once a server has booted.
        /// </summary>
        public static readonly string RepoRoot = findRepoRoot();

        private static string findRepoRoot()
        {
            string start = Path.GetDirectoryName(typeof(TestKit).Assembly.Location);
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "modinfo.json")) && Directory.Exists(Path.Combine(dir.FullName, "assets"))
                    && !dir.FullName.Contains("atlas-mods"))
                {
                    return dir.FullName;
                }
            }
            throw new InvalidOperationException("Repository root (modinfo.json + assets/) not found above " + start);
        }

        public static Item Item(IWorldSession world, string code)
        {
            var item = world.Api.World.GetItem(new AssetLocation(code));
            Assert.True(item != null, "Item not registered: " + code);
            return item;
        }

        public static ItemSlot HotbarSlot(ITestPlayer player, int index)
        {
            return player.Player.InventoryManager.GetHotbarInventory()[index];
        }

        public static void PutInHotbar(ITestPlayer player, int index, ItemStack stack)
        {
            var slot = HotbarSlot(player, index);
            slot.Itemstack = stack;
            slot.MarkDirty();
        }

        public static void ClearHotbar(ITestPlayer player)
        {
            foreach (var slot in player.Player.InventoryManager.GetHotbarInventory())
            {
                if (slot.Empty) continue;
                slot.Itemstack = null;
                slot.MarkDirty();
            }
        }

        /// <summary>How many of the given item the player carries across hotbar and backpack.</summary>
        public static int CountCarried(ITestPlayer player, string code)
        {
            var loc = new AssetLocation(code);
            int count = 0;
            foreach (var inv in new[] { player.Player.InventoryManager.GetHotbarInventory(), player.Player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName) })
            {
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    if (slot.Itemstack?.Collectible.Code.Equals(loc) == true) count += slot.StackSize;
                }
            }
            return count;
        }

        /// <summary>Runs until the mod systems' 1 second server tick has had a chance to fire, or the condition holds.</summary>
        public static Task UntilTicked(IWorldSession world, Func<bool> condition) => world.Until(condition, 300);

        public const string EmptyResonator = "portableresonator:portableresonator-empty";
        public const string LoadedResonator = "portableresonator:portableresonator-loaded";

        /// <summary>Every vanilla tuning cylinder (anything the portable resonator accepts), in a stable order.</summary>
        public static List<Item> Cylinders(IWorldSession world)
        {
            return world.Api.World.Items
                .Where(i => i?.Code != null && ItemPortableResonator.IsCylinder(new ItemStack(i)))
                .OrderBy(i => i.Code.ToString())
                .ToList();
        }

        /// <summary>Clears the hotbar and puts an empty portable resonator in slot 0.</summary>
        public static (ItemSlot slot, ItemPortableResonator item) GiveResonator(IWorldSession world, ITestPlayer player)
        {
            ClearHotbar(player);
            var item = (ItemPortableResonator)Item(world, EmptyResonator);
            PutInHotbar(player, 0, new ItemStack(item));
            return (HotbarSlot(player, 0), item);
        }

        /// <summary>As <see cref="GiveResonator"/>, loaded with <paramref name="cylinder"/> (default: the first cylinder).</summary>
        public static (ItemSlot slot, ItemPortableResonator item) GiveLoaded(IWorldSession world, ITestPlayer player, Item cylinder = null)
        {
            var (slot, _) = GiveResonator(world, player);
            Assert.True(ItemPortableResonator.StoreCylinder(player.Player, slot, new ItemStack(cylinder ?? Cylinders(world)[0])));
            return (slot, (ItemPortableResonator)slot.Itemstack.Collectible);
        }
    }
}
