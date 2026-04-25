using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using Meridian59.Data.Models;

namespace Meridian59.TuiClient
{
    public enum ReplayMode { Once, Loop, PingPong }

    public class PathReplayer
    {
        private readonly TuiClient client;
        private List<PathEvent> forwardEvents;
        private List<PathEvent> reverseEvents;   // pre-built for PingPong
        private List<PathEvent> activeEvents;    // whichever direction we're currently playing
        private int currentEventIndex;
        private DateTime replayStartTime;
        private bool waitingForRoom;
        private uint waitRoomID;
        private DateTime waitingForRoomSince;
        private ReplayMode mode;
        private bool goingForward = true;

        public bool IsReplaying => activeEvents != null && currentEventIndex < activeEvents.Count;

        public PathReplayer(TuiClient client)
        {
            this.client = client;
        }

        public bool Load(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                var serializer = new DataContractJsonSerializer(typeof(List<PathEvent>));
                forwardEvents = (List<PathEvent>)serializer.ReadObject(stream);
                reverseEvents = BuildReverseEvents(forwardEvents);
                client.Log("REC", $"Loaded {forwardEvents.Count} events from {Path.GetFileName(filePath)} ({reverseEvents.Count} move events in reverse)");
                return true;
            }
            catch (Exception ex)
            {
                client.Log("ERROR", "Failed to load recording: " + ex.Message);
                return false;
            }
        }

        public void Start(ReplayMode replayMode = ReplayMode.Once)
        {
            if (forwardEvents == null || forwardEvents.Count == 0) return;
            mode = replayMode;
            goingForward = true;
            activeEvents = forwardEvents;
            currentEventIndex = 0;
            waitingForRoom = false;
            replayStartTime = DateTime.Now;
            client.Log("REC", $"Replay started ({mode}).");
        }

        public void Stop()
        {
            currentEventIndex = activeEvents?.Count ?? 0;
            client.Log("REC", "Replay stopped.");
        }

        /// <summary>Called by TuiClient when a room transition occurs during replay.</summary>
        public void NotifyRoomChanged(uint roomID)
        {
            if (waitingForRoom && roomID == waitRoomID)
            {
                waitingForRoom = false;
                replayStartTime = DateTime.Now - TimeSpan.FromMilliseconds(
                    activeEvents[currentEventIndex].Timestamp);
                client.Log("REC", $"Room {roomID} entered — replay resuming.");
            }
        }

        public void Update()
        {
            if (activeEvents == null || currentEventIndex >= activeEvents.Count) return;
            if (waitingForRoom)
            {
                if ((DateTime.Now - waitingForRoomSince).TotalSeconds > 10)
                {
                    client.Log("REC", $"Timed out waiting for room {waitRoomID} — replay aborted.");
                    Stop();
                }
                return;
            }

            double elapsed = (DateTime.Now - replayStartTime).TotalMilliseconds;

            // Non-move events fire when their timestamp is due.
            // Move/Go events fire strictly in sequence, one per tick, gated on the
            // game rate limiter (100ms between packets) — never skip a position.
            // Skipping would create a jump larger than one walk step, which the
            // server anticheat rejects, causing the snap-back loop.
            while (currentEventIndex < activeEvents.Count)
            {
                var ev = activeEvents[currentEventIndex];
                bool isMove = ev.Action == "Move" || ev.Action == "Go";

                if (isMove)
                {
                    // Don't send the next move until the rate limiter allows it.
                    // Also don't fire it before its recorded timestamp — avoid
                    // running faster than the original playthrough.
                    if (elapsed < ev.Timestamp) break;
                    if (!client.GameTick.CanReqMove()) break;

                    ExecuteEvent(ev);
                    currentEventIndex++;
                    break;  // one move per Update() call — let 100ms pass before the next
                }
                else
                {
                    // Non-move events: fire when due, batch freely (no rate concern).
                    if (elapsed < ev.Timestamp) break;
                    ExecuteEvent(ev);
                    currentEventIndex++;
                }

                if (waitingForRoom) break;
            }

            if (currentEventIndex >= activeEvents.Count && !waitingForRoom)
                OnPassComplete();
        }

        private void OnPassComplete()
        {
            switch (mode)
            {
                case ReplayMode.Once:
                    client.Log("REC", "Replay complete.");
                    break;

                case ReplayMode.Loop:
                    // Restart forward pass from the beginning
                    activeEvents = forwardEvents;
                    currentEventIndex = 0;
                    replayStartTime = DateTime.Now;
                    client.Log("REC", "Loop: restarting.");
                    break;

                case ReplayMode.PingPong:
                    // Flip direction
                    goingForward = !goingForward;
                    activeEvents = goingForward ? forwardEvents : reverseEvents;
                    currentEventIndex = 0;
                    replayStartTime = DateTime.Now;
                    client.Log("REC", $"PingPong: going {(goingForward ? "forward" : "reverse")}.");
                    break;
            }
        }

        /// <summary>
        /// Build a reverse event list from the forward recording.
        /// Only Move/Go events are included (Cast/Say/Room/Rest/Stand don't make sense backward).
        /// Positions are reversed, timestamps re-spaced at the same average interval,
        /// and facing is flipped 180° (angle + 2048 mod 4096).
        /// </summary>
        private static List<PathEvent> BuildReverseEvents(List<PathEvent> fwd)
        {
            var moves = fwd.Where(e => e.Action == "Move" || e.Action == "Go").ToList();
            if (moves.Count == 0) return new List<PathEvent>();

            // Compute gaps between consecutive move events in the forward pass
            // so the reverse preserves the same pacing (slow in slow sections, fast in fast)
            var gaps = new double[moves.Count];
            gaps[0] = moves.Count > 1 ? (moves[1].Timestamp - moves[0].Timestamp) : 100.0;
            for (int i = 1; i < moves.Count; i++)
                gaps[i] = moves[i].Timestamp - moves[i - 1].Timestamp;

            // Reverse: event[0] in reverse = last forward event, gap[0] = gap before last forward event
            var result = new List<PathEvent>(moves.Count);
            double t = 0;
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                var src = moves[i];
                result.Add(new PathEvent
                {
                    Timestamp = t,
                    Action    = src.Action,
                    X         = src.X,
                    Y         = src.Y,
                    Angle     = (ushort)((src.Angle + 2048) % 4096),
                    Data      = src.Data
                });
                // The gap BEFORE this event in forward = gaps[i]; use it as gap AFTER in reverse
                t += Math.Max(gaps[i], 50.0);  // floor at 50ms to avoid pileups
            }
            return result;
        }

        private void ExecuteEvent(PathEvent ev)
        {
            var avatar = client.Data.AvatarObject;

            switch (ev.Action)
            {
                case "Start":
                    break;

                case "Move":
                    if (avatar != null)
                    {
                        avatar.CoordinateX = ev.X;
                        avatar.CoordinateY = ev.Y;
                        avatar.AngleUnits  = ev.Angle;
                        client.SendReqMoveMessage(false);  // respect rate limit
                    }
                    break;

                case "Go":
                    client.SendReplayGo(ev.X, ev.Y, ev.Angle);
                    break;

                case "Room":
                    if (!goingForward) break; // skip room waits during reverse pass
                    if (ev.Data != null)
                    {
                        var colon = ev.Data.IndexOf(':');
                        if (colon > 0 && uint.TryParse(ev.Data[..colon], out uint rid))
                        {
                            if (client.Data.RoomInformation?.RoomID != rid)
                            {
                                waitingForRoom = true;
                                waitRoomID = rid;
                                waitingForRoomSince = DateTime.Now;
                                client.Log("REC", $"Waiting for room {rid}…");
                            }
                        }
                    }
                    break;

                case "Rest":
                    if (goingForward) client.SendUserCommandRest();
                    break;

                case "Stand":
                    if (goingForward) client.SendUserCommandStand();
                    break;

                case "Cast":
                    if (goingForward && ev.Data != null)
                    {
                        var spellEntry = client.Data.AvatarSpells
                            .FirstOrDefault(s => string.Equals(s.ResourceName, ev.Data,
                                StringComparison.OrdinalIgnoreCase));
                        if (spellEntry != null)
                            client.SendReqCastMessage(spellEntry.ObjectID);
                        else
                            client.Log("REC", $"Replay: spell '{ev.Data}' not found.");
                    }
                    break;

                case "Say":
                    if (goingForward && ev.Data != null)
                        client.SendSayToMessage(Meridian59.Common.Enums.ChatTransmissionType.Normal, ev.Data);
                    break;

                case "Tell":
                    if (goingForward && ev.Data != null)
                    {
                        var colon = ev.Data.IndexOf(':');
                        if (colon > 0)
                            client.SendSayGroupMessage(0, ev.Data[(colon + 1)..]);
                    }
                    break;

                case "Macro":
                    if (goingForward && ev.Data != null)
                        client.ExecuteMacro(ev.Data);
                    break;

                case "Get":
                    if (goingForward && ev.Data != null)
                    {
                        var av2 = client.Data.AvatarObject;
                        if (av2 != null)
                        {
                            var target = client.Data.RoomObjects
                                .Where(o => o.ID != client.Data.AvatarID && o.Flags.IsGettable)
                                .Where(o => {
                                    float dx = o.CoordinateX - av2.CoordinateX;
                                    float dz = o.CoordinateY - av2.CoordinateY;
                                    return (dx * dx + dz * dz) <= 512f * 512f;
                                })
                                .OrderBy(o => {
                                    float dx = o.CoordinateX - av2.CoordinateX;
                                    float dz = o.CoordinateY - av2.CoordinateY;
                                    return dx * dx + dz * dz;
                                })
                                .FirstOrDefault(o => string.Equals(o.Name, ev.Data,
                                    StringComparison.OrdinalIgnoreCase));
                            if (target != null)
                                client.SendReqGetMessage(new ObjectID(target.ID));
                            else
                                client.Log("REC", $"Replay: gettable item '{ev.Data}' not in range.");
                        }
                    }
                    break;
            }
        }
    }
}
