using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Meridian59.Data.Models;

namespace Meridian59.TuiClient
{
    [DataContract]
    public class PathEvent
    {
        [DataMember]
        public double Timestamp { get; set; }

        /// <summary>
        /// Action type: Start, Move, Go, Room, Rest, Stand, Cast, Say, Tell
        /// </summary>
        [DataMember]
        public string Action { get; set; }

        [DataMember]
        public ushort X { get; set; }

        [DataMember]
        public ushort Y { get; set; }

        [DataMember]
        public ushort Angle { get; set; }

        /// <summary>
        /// Action-specific payload:
        ///   Move/Go/Start: null
        ///   Room: "RoomID:RoomName"
        ///   Cast: spell resource name
        ///   Say:  message text
        ///   Tell: "RecipientName:message text"
        /// </summary>
        [DataMember]
        public string Data { get; set; }
    }

    public class PathRecorder
    {
        private readonly TuiClient client;
        private readonly List<PathEvent> events = new();
        private DateTime startTime;
        private string filePath;

        public bool IsRecording { get; private set; }

        public PathRecorder(TuiClient client)
        {
            this.client = client;
        }

        public void Start(string path)
        {
            filePath = path;
            events.Clear();
            startTime = DateTime.Now;
            IsRecording = true;
            RecordCore("Start", client.Data.AvatarObject);
            client.Log("REC", $"Recording started → {Path.GetFileName(filePath)}");
        }

        public void Stop()
        {
            if (!IsRecording) return;
            IsRecording = false;
            Save();
            client.Log("REC", $"Recording stopped. {events.Count} events saved.");
        }

        /// <summary>Record a movement (ReqMove) event.</summary>
        public void RecordMove(RoomObject avatar) => RecordCore("Move", avatar);

        /// <summary>Record a manual snap-to-wall / ReqGo event.</summary>
        public void RecordGo(RoomObject avatar) => RecordCore("Go", avatar);

        /// <summary>Record a room transition.</summary>
        public void RecordRoom(RoomObject avatar, uint roomID, string roomName)
            => RecordCore("Room", avatar, $"{roomID}:{roomName}");

        /// <summary>Record a rest action.</summary>
        public void RecordRest(RoomObject avatar) => RecordCore("Rest", avatar);

        /// <summary>Record a stand action.</summary>
        public void RecordStand(RoomObject avatar) => RecordCore("Stand", avatar);

        /// <summary>Record a spell cast.</summary>
        public void RecordCast(RoomObject avatar, string spellName) => RecordCore("Cast", avatar, spellName);

        /// <summary>Record a say/shout/emote.</summary>
        public void RecordSay(RoomObject avatar, string text) => RecordCore("Say", avatar, text);

        /// <summary>Record a tell.</summary>
        public void RecordTell(RoomObject avatar, string recipientAndText) => RecordCore("Tell", avatar, recipientAndText);

        /// <summary>Record picking up an item by name.</summary>
        public void RecordGet(RoomObject avatar, string itemName) => RecordCore("Get", avatar, itemName);

        /// <summary>Record a macro command (e.g. /conveyall, /drainunbound bless).</summary>
        public void RecordMacro(RoomObject avatar, string command) => RecordCore("Macro", avatar, command);

        private void RecordCore(string action, RoomObject avatar, string data = null)
        {
            if (!IsRecording) return;
            if (avatar == null) return;

            events.Add(new PathEvent
            {
                Timestamp = (DateTime.Now - startTime).TotalMilliseconds,
                Action    = action,
                X         = avatar.CoordinateX,
                Y         = avatar.CoordinateY,
                Angle     = avatar.AngleUnits,
                Data      = data
            });
        }

        private void Save()
        {
            try
            {
                using var stream = File.Create(filePath);
                var serializer = new DataContractJsonSerializer(typeof(List<PathEvent>));
                serializer.WriteObject(stream, events);
            }
            catch (Exception ex)
            {
                client.Log("ERROR", "Failed to save recording: " + ex.Message);
            }
        }
    }
}
