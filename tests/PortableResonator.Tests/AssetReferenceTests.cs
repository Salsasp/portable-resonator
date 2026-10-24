using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PortableResonator.Tests
{
    /// <summary>
    /// Static checks over the asset JSON, no server needed. A missing texture renders invisible in-game with
    /// only a log line, and the server never loads textures, so the scenarios can't catch these.
    /// </summary>
    public class AssetReferenceTests
    {
        private const string Domain = "portableresonator";

        private static readonly string RepoRoot = TestKit.RepoRoot;
        private static readonly string ModAssets = Path.Combine(RepoRoot, "assets", Domain);
        private static readonly string GameAssets = Path.Combine(
            Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new InvalidOperationException("VINTAGE_STORY is not set"),
            "assets");

        /// <summary>The "game" domain is spread over these folders of the install.</summary>
        private static readonly string[] GameDomainFolders = { "survival", "game", "creative" };

        public static IEnumerable<object[]> ShapeFiles => jsonFiles("shapes");
        public static IEnumerable<object[]> TypeFiles => jsonFiles("itemtypes").Concat(jsonFiles("blocktypes"));

        [Theory]
        [MemberData(nameof(ShapeFiles))]
        public void Shape_textures_exist(string file)
        {
            var textures = parse(file)["textures"] as JObject;
            if (textures == null) return;

            foreach (var texture in textures.Properties())
            {
                assertExists(texture.Value.ToString(), "textures", ".png", $"{file}: texture '{texture.Name}'");
            }
        }

        [Theory]
        [MemberData(nameof(ShapeFiles))]
        public void Shape_faces_use_defined_textures(string file)
        {
            var json = parse(file);
            var defined = (json["textures"] as JObject)?.Properties().Select(p => p.Name).ToHashSet() ?? new HashSet<string>();

            // Worn shapes take their textures from the item JSON, which the type check covers.
            if (defined.Count == 0) return;

            var undefined = json.SelectTokens("$..faces.*")
                .Where(face => face["enabled"]?.Value<bool>() != false)
                .Select(face => face["texture"]?.ToString().TrimStart('#'))
                // "#null" is VS Model Creator's marker for a face with no texture assigned.
                .Where(code => !string.IsNullOrEmpty(code) && code != "null" && !defined.Contains(code))
                .Distinct()
                .ToList();

            Assert.True(undefined.Count == 0, $"{file}: faces use undefined textures: {string.Join(", ", undefined)}");
        }

        /// <summary>
        /// Shapes attached to an entity (elk gear) hang every top-level element on an element of the entity's shape.
        /// One without a stepParentName is dropped in-game with only a log warning ("Will not be visible").
        /// </summary>
        [Theory]
        [MemberData(nameof(ShapeFiles))]
        public void Worn_shape_top_level_elements_have_a_step_parent(string file)
        {
            if (!file.StartsWith("shapes/item/wearable/")) return;

            var orphans = ((parse(file)["elements"] as JArray) ?? new JArray())
                .Where(e => string.IsNullOrEmpty(e["stepParentName"]?.ToString()))
                .Select(e => e["name"]?.ToString())
                .ToList();

            Assert.True(orphans.Count == 0, $"{file}: top-level elements without stepParentName (invisible in-game): {string.Join(", ", orphans)}");
        }

        /// <summary>
        /// The client's animator lerps all three axes of a keyframe's offset, rotation or stretch as non-null values
        /// (Animation.lerpKeyFrameElement), so a keyframe setting only rotationY crashes the client when the
        /// animator is built. VS Model Creator always writes all three; hand-written shapes must too.
        /// </summary>
        [Theory]
        [MemberData(nameof(ShapeFiles))]
        public void Keyframes_set_all_three_axes(string file)
        {
            var groups = new[]
            {
                new[] { "offsetX", "offsetY", "offsetZ" },
                new[] { "rotationX", "rotationY", "rotationZ" },
                new[] { "stretchX", "stretchY", "stretchZ" }
            };

            var problems = new List<string>();
            foreach (var animation in (parse(file)["animations"] as JArray) ?? new JArray())
            {
                foreach (var keyFrame in (animation["keyframes"] as JArray) ?? new JArray())
                {
                    foreach (var element in (keyFrame["elements"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    {
                        var pose = (JObject)element.Value;
                        foreach (var axes in groups.Where(g => g.Any(a => pose[a] != null) && !g.All(a => pose[a] != null)))
                        {
                            problems.Add($"{animation["code"]} frame {keyFrame["frame"]} {element.Name}: sets only {string.Join("/", axes.Where(a => pose[a] != null))}");
                        }
                    }
                }
            }

            Assert.True(problems.Count == 0, $"{file}: keyframes missing axes (crashes the client animator):\n" + string.Join("\n", problems));
        }

        [Theory]
        [MemberData(nameof(TypeFiles))]
        public void Type_textures_and_shapes_exist(string file)
        {
            var json = parse(file);

            if (json["textures"] is JObject textures)
            {
                foreach (var texture in textures.Properties())
                {
                    string path = texture.Value["base"]?.ToString();
                    if (path != null) assertExists(path, "textures", ".png", $"{file}: texture '{texture.Name}'");
                }
            }

            string shape = json["shape"]?["base"]?.ToString();
            if (shape != null) assertExists(shape, "shapes", ".json", $"{file}: shape");
        }

        private static void assertExists(string location, string category, string extension, string what)
        {
            // Variant placeholders and wildcards are resolved by the engine per variant.
            if (location.Contains('{') || location.Contains('*')) return;

            var (domain, path) = split(location);
            // VS Model Creator can save absolute disk paths ("C:/Users/..."); the game can't resolve those,
            // and without this check Path.Combine would find the file on this machine and pass.
            Assert.True(domain == Domain || domain == "game", $"{what}: {location} is not a game: or {Domain}: asset path");
            Assert.False(Path.IsPathRooted(path), $"{what}: {location} is an absolute path");
            // VS Model Creator can write Windows separators; asset paths only use '/', and on Windows
            // Path.Combine would accept the backslashes and pass.
            Assert.False(path.Contains('\\'), $"{what}: {location} uses backslashes");

            var paths = candidates(domain, path, category, extension).ToList();
            Assert.True(paths.Any(File.Exists), $"{what}: {location} not found (looked in {string.Join(", ", paths)})");
        }

        private static IEnumerable<string> candidates(string domain, string path, string category, string extension)
        {
            string relative = Path.Combine(category, path + extension);
            if (domain == Domain) return new[] { Path.Combine(ModAssets, relative) };
            return GameDomainFolders.Select(folder => Path.Combine(GameAssets, folder, relative));
        }

        /// <summary>Unprefixed paths in this mod's assets belong to this mod's domain.</summary>
        private static (string domain, string path) split(string location)
        {
            int colon = location.IndexOf(':');
            return colon < 0 ? (Domain, location) : (location[..colon], location[(colon + 1)..]);
        }

        private static JObject parse(string file) => JObject.Parse(File.ReadAllText(Path.Combine(ModAssets, file)));

        private static IEnumerable<object[]> jsonFiles(string category)
        {
            string dir = Path.Combine(ModAssets, category);
            if (!Directory.Exists(dir)) return Enumerable.Empty<object[]>();

            return Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                .Select(f => new object[] { Path.GetRelativePath(ModAssets, f).Replace('\\', '/') });
        }
    }
}
