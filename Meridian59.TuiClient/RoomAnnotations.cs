using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Meridian59.TuiClient
{
    public enum AnnotationType { Door, Spawn }

    [DataContract]
    public class RoomAnnotation
    {
        [DataMember] public ushort X { get; set; }
        [DataMember] public ushort Y { get; set; }
        [DataMember] public string Type { get; set; } // "Door" or "Spawn"
    }

    /// <summary>
    /// Persists per-room Door and Spawn annotations to room_annotations.json.
    /// Key: room ID (as string for JSON compat). Value: list of annotations.
    /// </summary>
    public class RoomAnnotationStore
    {
        private const string FILE = "room_annotations.json";

        // roomID (string) -> list of annotations
        private Dictionary<string, List<RoomAnnotation>> store = new();

        public RoomAnnotationStore()
        {
            Load();
        }

        /// <summary>Returns all annotations for a room (never null).</summary>
        public List<RoomAnnotation> GetAnnotations(uint roomID)
        {
            var key = roomID.ToString();
            return store.TryGetValue(key, out var list) ? list : new List<RoomAnnotation>();
        }

        /// <summary>
        /// Records a Door at (fromX, fromY) in fromRoomID and a Spawn at (toX, toY) in toRoomID.
        /// Deduplicates by (roomID, x, y, type). Saves immediately.
        /// </summary>
        public void RecordTransition(uint fromRoomID, ushort fromX, ushort fromY,
                                     uint toRoomID,   ushort toX,   ushort toY)
        {
            bool changed = false;
            changed |= AddAnnotation(fromRoomID, fromX, fromY, AnnotationType.Door);
            changed |= AddAnnotation(toRoomID,   toX,   toY,   AnnotationType.Spawn);
            if (changed) Save();
        }

        private bool AddAnnotation(uint roomID, ushort x, ushort y, AnnotationType type)
        {
            var key = roomID.ToString();
            if (!store.TryGetValue(key, out var list))
            {
                list = new List<RoomAnnotation>();
                store[key] = list;
            }

            string typeStr = type.ToString();
            foreach (var a in list)
                if (a.X == x && a.Y == y && a.Type == typeStr) return false;

            list.Add(new RoomAnnotation { X = x, Y = y, Type = typeStr });
            return true;
        }

        private void Load()
        {
            if (!File.Exists(FILE)) return;
            try
            {
                using var stream = File.OpenRead(FILE);
                var ser = new DataContractJsonSerializer(typeof(Dictionary<string, List<RoomAnnotation>>));
                store = (Dictionary<string, List<RoomAnnotation>>)ser.ReadObject(stream)
                        ?? new Dictionary<string, List<RoomAnnotation>>();
            }
            catch
            {
                store = new Dictionary<string, List<RoomAnnotation>>();
            }
        }

        private void Save()
        {
            try
            {
                using var stream = File.Create(FILE);
                var ser = new DataContractJsonSerializer(typeof(Dictionary<string, List<RoomAnnotation>>));
                ser.WriteObject(stream, store);
            }
            catch { /* non-fatal */ }
        }
    }
}
