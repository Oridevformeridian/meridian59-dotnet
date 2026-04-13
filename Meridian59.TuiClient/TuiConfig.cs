using System;
using System.Collections.Generic;
using System.Xml;
using Meridian59.Bot;

namespace Meridian59.TuiClient
{
    public enum TuiAction
    {
        None,
        MoveNorth,
        MoveSouth,
        MoveEast,
        MoveWest,
        Hotkey1,
        Hotkey2,
        Hotkey3,
        Hotkey4,
        Use,
        Mail,
        Rest,
        Stand,
        Quit,
        ZoomIn,
        ZoomOut,
        ToggleRotation,
        ScrollUp,
        ScrollDown,
        EnterChat,
        ToggleNoClip,
        ManualGo
    }

    public class TuiConfig : BotConfig
    {
        public Dictionary<ConsoleKey, TuiAction> KeyMap { get; private set; }

        public TuiConfig() : base()
        {
            KeyMap = new Dictionary<ConsoleKey, TuiAction>();
            SetDefaultKeyMap();
        }

        private void SetDefaultKeyMap()
        {
            KeyMap.Clear();
            KeyMap[ConsoleKey.UpArrow]    = TuiAction.MoveNorth;
            KeyMap[ConsoleKey.DownArrow]  = TuiAction.MoveSouth;
            KeyMap[ConsoleKey.LeftArrow]  = TuiAction.MoveWest;
            KeyMap[ConsoleKey.RightArrow] = TuiAction.MoveEast;
            KeyMap[ConsoleKey.W]          = TuiAction.MoveNorth;
            KeyMap[ConsoleKey.S]          = TuiAction.MoveSouth;
            KeyMap[ConsoleKey.A]          = TuiAction.MoveWest;
            KeyMap[ConsoleKey.D]          = TuiAction.MoveEast;
            KeyMap[ConsoleKey.Spacebar]   = TuiAction.Use;
            KeyMap[ConsoleKey.Q]          = TuiAction.Quit;
            KeyMap[ConsoleKey.Add]        = TuiAction.ZoomIn;
            KeyMap[ConsoleKey.OemPlus]    = TuiAction.ZoomIn;
            KeyMap[ConsoleKey.Subtract]   = TuiAction.ZoomOut;
            KeyMap[ConsoleKey.OemMinus]   = TuiAction.ZoomOut;
            KeyMap[ConsoleKey.R]          = TuiAction.ToggleRotation;
            KeyMap[ConsoleKey.PageUp]     = TuiAction.ScrollUp;
            KeyMap[ConsoleKey.PageDown]   = TuiAction.ScrollDown;
            KeyMap[ConsoleKey.Enter]      = TuiAction.EnterChat;
            KeyMap[ConsoleKey.G]          = TuiAction.ManualGo;
            // Hotkeys not bound by default in code, but available via config
        }

        public override void ReadXml(XmlDocument Document)
        {
            base.ReadXml(Document);

            XmlNode keymapNode = Document.DocumentElement.SelectSingleNode("/configuration/keymap");
            if (keymapNode != null)
            {
                foreach (XmlNode child in keymapNode.ChildNodes)
                {
                    if (child.Name != "bind") continue;

                    string keyStr = child.Attributes["key"]?.Value;
                    string actionStr = child.Attributes["action"]?.Value;

                    if (Enum.TryParse(keyStr, true, out ConsoleKey key) && 
                        Enum.TryParse(actionStr, true, out TuiAction action))
                    {
                        KeyMap[key] = action;
                    }
                }
            }
        }
    }
}
