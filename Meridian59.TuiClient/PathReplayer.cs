using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Threading;
using Meridian59.Bot;
using Meridian59.Data;

namespace Meridian59.TuiClient
{
    public class PathReplayer
    {
        private TuiClient client;
        private List<PathEvent> events;
        private int currentEventIndex = 0;
        private DateTime replayStartTime;

        public PathReplayer(TuiClient client)
        {
            this.client = client;
        }

        public bool Load(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<PathEvent>));
                    events = (List<PathEvent>)serializer.ReadObject(stream);
                    return true;
                }
            }
            catch (Exception ex)
            {
                client.Log("ERROR", "Failed to load path: " + ex.Message);
                return false;
            }
        }

        public void Start()
        {
            if (events == null || events.Count == 0) return;
            currentEventIndex = 0;
            replayStartTime = DateTime.Now;
            client.Log("SYS", "Replay started.");
        }

        public void Update()
        {
            if (events == null || currentEventIndex >= events.Count) return;

            var ev = events[currentEventIndex];
            double elapsed = (DateTime.Now - replayStartTime).TotalMilliseconds;

            if (elapsed >= ev.Timestamp)
            {
                ExecuteEvent(ev);
                currentEventIndex++;
            }
        }

        private void ExecuteEvent(PathEvent ev)
        {
            switch (ev.Action)
            {
                case "Move":
                    if (client.Data.AvatarObject != null)
                    {
                        client.Data.AvatarObject.CoordinateX = ev.X;
                        client.Data.AvatarObject.CoordinateY = ev.Y;
                        client.Data.AvatarObject.AngleUnits = ev.Angle;
                        client.SendReqMoveMessage(true);
                    }
                    break;
                case "Action":
                    // Parse action and send
                    break;
                case "Said":
                    // Parse transmission type and text and send
                    break;
            }
        }
    }
}
