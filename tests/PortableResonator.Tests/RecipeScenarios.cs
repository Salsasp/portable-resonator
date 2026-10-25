using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;

namespace PortableResonator.Tests
{
    /// <summary>
    /// The wrench grid recipes converting between the vanilla resonator block and the portable one. The engine
    /// drops a recipe whose codes don't resolve with only a log warning, so these check the recipes made it in.
    /// The item has a cylinder variant group, so its codes always carry -empty/-loaded.
    /// </summary>
    public class RecipeScenarios : AtlasScenarioBase
    {
        private const string Empty = "portableresonator:portableresonator-empty";
        private const string Vanilla = "game:resonator-north";

        [AtlasScenario]
        public Task Wrench_turns_a_vanilla_resonator_into_an_empty_portable_one()
        {
            Assert.Contains(recipes(), r => outputCode(r) == Empty && ingredientCodes(r).Contains(Vanilla));
            return Task.CompletedTask;
        }

        [AtlasScenario]
        public Task Wrench_turns_an_empty_portable_resonator_back_into_the_block()
        {
            Assert.Contains(recipes(), r => outputCode(r) == Vanilla && ingredientCodes(r).Contains(Empty));
            return Task.CompletedTask;
        }

        private IEnumerable<GridRecipe> recipes() => World.Api.World.GridRecipes.Where(r => r.Enabled);

        private static string outputCode(GridRecipe r) => r.Output?.ResolvedItemStack?.Collectible?.Code?.ToString();

        private static List<string> ingredientCodes(GridRecipe r) =>
            r.ResolvedIngredients.Where(i => i != null).Select(i => i.Code.ToString()).ToList();
    }
}
