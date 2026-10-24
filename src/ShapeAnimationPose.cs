using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace PortableResonator
{
    /// <summary>
    /// Bakes a shape animation's pose at a whole frame into the shape itself, so a static mesh can show it: held items
    /// render one mesh and nothing animates them. Matches the engine's interpolation for version 0 animations
    /// (Animation.lerpKeyFrameElement + ElementPose.Add): per element and channel, lerp between the keyframes around the
    /// frame, wrapping at the end, with rotShortestDistance taken from the earlier keyframe. In version 0 the
    /// animated degrees simply add to the element's own rotation, and offsets to its position.
    /// </summary>
    public static class ShapeAnimationPose
    {
        /// <summary>The engine plays animations at 30 frames per second (animation speed 1).</summary>
        public const float FramesPerSecond = 30f;

        /// <summary>Rotation in degrees and offset in voxels of one element at a frame.</summary>
        public struct Pose
        {
            public Vec3f Rotation;
            public Vec3f Offset;
        }

        public static Pose PoseAt(Animation animation, string elementName, int frame)
        {
            var keyFrames = animation.KeyFrames
                .Where(k => k.Elements != null && k.Elements.ContainsKey(elementName))
                .OrderBy(k => k.Frame)
                .ToList();

            return new Pose
            {
                Rotation = channel(animation, keyFrames, elementName, frame, e => e.RotationX != null,
                    e => new Vec3f((float)e.RotationX, (float)e.RotationY, (float)e.RotationZ), shortest: true),
                Offset = channel(animation, keyFrames, elementName, frame, e => e.OffsetX != null,
                    e => new Vec3f((float)e.OffsetX, (float)e.OffsetY, (float)e.OffsetZ), shortest: false)
            };
        }

        /// <summary>Adds the animation's pose at <paramref name="frame"/> to the elements of <paramref name="shape"/>.</summary>
        public static void Apply(Shape shape, Animation animation, int frame)
        {
            var names = animation.KeyFrames.Where(k => k.Elements != null).SelectMany(k => k.Elements.Keys).Distinct();
            foreach (string name in names)
            {
                var element = shape.GetElementByName(name);
                if (element == null) continue;

                var pose = PoseAt(animation, name, frame);
                element.RotationX += pose.Rotation.X;
                element.RotationY += pose.Rotation.Y;
                element.RotationZ += pose.Rotation.Z;
                for (int i = 0; i < 3; i++)
                {
                    element.From[i] += pose.Offset[i];
                    element.To[i] += pose.Offset[i];
                }
            }
        }

        private static Vec3f channel(Animation animation, List<AnimationKeyFrame> keyFrames, string name, int frame,
            System.Func<AnimationKeyFrameElement, bool> sets, System.Func<AnimationKeyFrameElement, Vec3f> value, bool shortest)
        {
            var frames = keyFrames.Where(k => sets(k.Elements[name])).ToList();
            if (frames.Count == 0) return new Vec3f();

            int total = animation.QuantityFrames;
            var prev = frames.LastOrDefault(k => k.Frame <= frame) ?? frames[^1];
            var next = frames.FirstOrDefault(k => k.Frame > frame) ?? frames[0];
            var from = value(prev.Elements[name]);
            if (prev == next) return from;

            var to = value(next.Elements[name]);
            float span = next.Frame > prev.Frame ? next.Frame - prev.Frame : total - prev.Frame + next.Frame;
            float t = (frame >= prev.Frame ? frame - prev.Frame : total - prev.Frame + frame) / span;

            var flags = prev.Elements[name];
            return new Vec3f(
                lerp(from.X, to.X, t, shortest && flags.RotShortestDistanceX),
                lerp(from.Y, to.Y, t, shortest && flags.RotShortestDistanceY),
                lerp(from.Z, to.Z, t, shortest && flags.RotShortestDistanceZ));
        }

        private static float lerp(float from, float to, float t, bool shortestDistance)
        {
            return shortestDistance ? from + GameMath.AngleDegDistance(from, to) * t : from * (1 - t) + to * t;
        }
    }
}
