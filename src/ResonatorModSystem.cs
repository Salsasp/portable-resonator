using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace PortableResonator
{
    /// <summary>
    /// Runs portable resonator playback. The server finds each player's playing resonator (anywhere in their own
    /// inventories), advances its stored position, and publishes track / position / play id as watched attributes
    /// on the player, since other clients can't read the stack. Each client plays the nearest holder's track on
    /// the music channel, with the vanilla resonator block's distance falloff. Tracks loop.
    /// </summary>
    public class ResonatorModSystem : ModSystem
    {
        public const string TrackAttr = "portableresonator:track";
        public const string PositionAttr = "portableresonator:position";
        public const string PlayIdAttr = "portableresonator:playid";

        /// <summary>Beyond this the vanilla falloff curve is silent anyway.</summary>
        public const float HearingRange = 25f;

        public override bool ShouldLoad(EnumAppSide forSide) => true;

        public override void Start(ICoreAPI api)
        {
            api.RegisterItemClass("ItemPortableResonator", typeof(ItemPortableResonator));
        }

        #region Server

        private ICoreServerAPI sapi;
        private readonly Dictionary<string, Playback> playing = new Dictionary<string, Playback>();
        private int nextPlayId = new Random().Next(1, int.MaxValue / 2);

        private class Playback
        {
            public ItemStack Stack;
            public long LastMs;
            public int PlayId;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Event.RegisterGameTickListener(_ => { foreach (var player in api.World.AllOnlinePlayers) Refresh(player as IServerPlayer); }, 1000, 300);
            api.Event.PlayerDisconnect += player => { playing.Remove(player.PlayerUID); clear(player); };
        }

        /// <summary>
        /// Banks play time into the player's playing resonator and republishes its state, or clears it if the
        /// player no longer carries one. Called every second and right after every play/pause/eject.
        /// </summary>
        public void Refresh(IServerPlayer player)
        {
            if (sapi == null || player?.Entity == null) return;

            var slot = FindPlayingSlot(player);
            playing.TryGetValue(player.PlayerUID, out var current);
            long now = sapi.World.ElapsedMilliseconds;

            if (slot == null)
            {
                if (current != null) playing.Remove(player.PlayerUID);
                clear(player);
                return;
            }

            var item = (ItemPortableResonator)slot.Itemstack.Collectible;
            if (current == null || current.Stack != slot.Itemstack)
            {
                // A new playback (started, resumed, or the stack was replaced): listeners restart from the stored position.
                current = new Playback { Stack = slot.Itemstack, LastMs = now, PlayId = nextPlayId++ };
                playing[player.PlayerUID] = current;
            }
            else
            {
                // Mutating the server stack is enough: it's saved from here, and no client needs the number.
                item.SetPosition(current.Stack, item.GetPosition(current.Stack) + (now - current.LastMs) / 1000.0);
                current.LastMs = now;
            }

            var attrs = player.Entity.WatchedAttributes;
            string track = item.GetTrack(current.Stack);
            if (attrs.GetString(TrackAttr) != track || attrs.GetInt(PlayIdAttr) != current.PlayId)
            {
                attrs.SetString(TrackAttr, track);
                attrs.SetInt(PlayIdAttr, current.PlayId);
            }
            // Kept current so listeners who come into range join at roughly the right spot.
            attrs.SetFloat(PositionAttr, (float)item.GetPosition(current.Stack));
        }

        /// <summary>The first playing resonator in the player's hotbar (incl. off hand), backpack or mouse cursor.</summary>
        public static ItemSlot FindPlayingSlot(IPlayer player)
        {
            var manager = player.InventoryManager;
            foreach (var inv in new[] { manager.GetHotbarInventory(), manager.GetOwnInventory(GlobalConstants.backpackInvClassName), manager.GetOwnInventory(GlobalConstants.mousecursorInvClassName) })
            {
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    if (slot?.Itemstack?.Collectible is ItemPortableResonator item && item.IsPlaying(slot.Itemstack)) return slot;
                }
            }
            return null;
        }

        private static void clear(IPlayer player)
        {
            var attrs = player.Entity?.WatchedAttributes;
            if (attrs == null || !attrs.HasAttribute(TrackAttr)) return;
            attrs.RemoveAttribute(TrackAttr);
            attrs.RemoveAttribute(PlayIdAttr);
            attrs.RemoveAttribute(PositionAttr);
        }

        #endregion

        /// <summary>
        /// The player <paramref name="listener"/> hears: the nearest one within <see cref="HearingRange"/> publishing a
        /// track (the listener included), or null. The music channel plays one track at a time. Pure over entity
        /// positions and watched attributes, so the server-side tests can check it.
        /// </summary>
        public static EntityPlayer FindSource(EntityPlayer listener, IEnumerable<IPlayer> players, out double squareDistance)
        {
            EntityPlayer source = null;
            squareDistance = HearingRange * HearingRange;
            foreach (var plr in players)
            {
                var entity = plr.Entity;
                if (entity?.WatchedAttributes.GetString(TrackAttr) == null) continue;
                double dist = entity.Pos.SquareDistanceTo(listener.Pos.XYZ);
                if (dist <= squareDistance)
                {
                    squareDistance = dist;
                    source = entity;
                }
            }
            return source;
        }

        #region Client

        private ICoreClientAPI capi;
        private MusicTrack track;
        private long sourceEntityId;
        private int sourcePlayId;
        private string sourceTrack;
        private object startToken;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            api.Event.RegisterGameTickListener(onClientTick, 50);
            api.Event.LeaveWorld += stopTrack;
        }

        private void onClientTick(float dt)
        {
            var listener = capi.World.Player?.Entity;
            if (listener == null) return;

            var source = FindSource(listener, capi.World.AllOnlinePlayers, out double best);
            if (source == null)
            {
                stopTrack();
                return;
            }

            string trackCode = source.WatchedAttributes.GetString(TrackAttr);
            int playId = source.WatchedAttributes.GetInt(PlayIdAttr);
            // Not "track == null": if the engine refuses a track, retrying every 50ms would spam it.
            if (source.EntityId != sourceEntityId || playId != sourcePlayId || trackCode != sourceTrack)
            {
                stopTrack();
                sourceEntityId = source.EntityId;
                sourcePlayId = playId;
                sourceTrack = trackCode;
                startTrack(trackCode, source.WatchedAttributes.GetFloat(PositionAttr));
                return;
            }

            var sound = track?.Sound;
            if (sound == null || track.loading) return;

            // Finished: loop from the start.
            if (sound.HasStopped && !sound.IsPaused)
            {
                stopTrack();
                sourceEntityId = source.EntityId;
                sourcePlayId = playId;
                sourceTrack = trackCode;
                startTrack(trackCode, 0);
                return;
            }

            // Same falloff as the vanilla resonator block: 1/log10(d * 0.7) - 0.8, full volume within ~1.4 blocks.
            float distance = (float)Math.Sqrt(best);
            float volume = GameMath.Clamp(1 / (float)Math.Log10(Math.Max(1, distance * 0.7)) - 0.8f, 0, 1);
            sound.SetVolume(volume);
            sound.SetPitch(GameMath.Clamp(1 - capi.Render.ShaderUniforms.GlitchStrength, 0.1f, 1));
        }

        private void startTrack(string trackCode, float position)
        {
            // Each start gets a token, so a sound that finishes loading after a newer start (or a stop) is dropped.
            var token = new object();
            startToken = token;

            var started = capi.StartTrack(new AssetLocation(trackCode), 99f, EnumSoundType.MusicGlitchunaffected, sound =>
            {
                // The loader may call back off the main thread; attach there.
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    if (sound == null) return;
                    if (startToken != token || track == null)
                    {
                        sound.Dispose();
                        return;
                    }

                    track.Sound = sound;
                    float length = sound.SoundLengthSeconds;
                    sound.PlaybackPosition = length > 0 ? position % length : 0;
                    sound.SetVolume(0);
                    sound.Start();
                    track.loading = false;
                }, "portableresonator-track");
            });

            track = started;
            // Keeps the music engine from disposing the track while it loads (same trick as the resonator block).
            if (track != null) track.loading = true;
        }

        private void stopTrack()
        {
            startToken = null;
            track?.Stop();
            track = null;
            sourceEntityId = 0;
            sourcePlayId = 0;
            sourceTrack = null;
        }

        #endregion
    }
}
