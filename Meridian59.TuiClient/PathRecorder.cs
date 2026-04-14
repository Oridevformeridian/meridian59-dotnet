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
        
        [DataMember]
        public string Action { get; set; }
        
        [DataMember]
        public ushort X { get; set; }
        
        [DataMember]
        public ushort Y { get; set; }
        
        [DataMember]
        public ushort Angle { get; set; }
        
        [DataMember]
        public string Data { get; set; }
    }

    public class PathRecorder
    {
        private TuiClient client;
        private List<PathEvent> events;
        private DateTime startTime;
        private string filePath;

        public PathRecorder(TuiClient client)
        {
            this.client = client;
            events = new List<PathEvent>();
            client.Log("SYS", "PathRecorder initialized.");
        }

        public void Start(string filePath)
        {
            this.filePath = filePath;
            this.events.Clear();
            this.startTime = DateTime.Now;
            
            // Record initial position
            Record("Start", client.Data.AvatarObject);
        }

        public void Stop()
        {
            Save();
        }

        public void Record(string action, RoomObject avatar, string data = null)
        {
            if (avatar == null)
            {
                client.Log("SYS", $"PathRecorder: Skip recording {action} - avatar null");
                return;
            }

            var ev = new PathEvent
            {
                Timestamp = (DateTime.Now - startTime).TotalMilliseconds,
                Action = action,
                X = avatar.CoordinateX,
                Y = avatar.CoordinateY,
                Angle = avatar.AngleUnits,
                Data = data
            };
            events.Add(ev);
            client.Log("SYS", $"PathRecorder: Recorded {action} at ({ev.X},{ev.Y})");
        }

        private void Save()
        {
            try
            {
                using (var stream = File.Create(filePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<PathEvent>));
                    serializer.WriteObject(stream, events);
                }
                client.Log("SYS", $"PathRecorder: Saved {events.Count} events to {filePath}");
            }
            catch (Exception ex)
            {
                client.Log("ERROR", "Failed to save path: " + ex.Message);
            }
        }
    }
}
