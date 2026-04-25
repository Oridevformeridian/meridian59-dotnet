using System;
using System.Collections.Generic;
using System.Xml;
using Meridian59.Bot;

namespace Meridian59.TuiClient
{
    public enum TuiAction
    {
        None,
        MoveUp,
        MoveDown,
        MoveRight,
        MoveLeft,
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
        ScrollUp,
        ScrollDown,
        EnterChat,
        ToggleNoClip,
        ToggleRun,
        Refresh,
        ManualGo,
        ToggleNetTab,
        OpenCharSheet,
        ToggleAutoAttack,
        TargetNearest,
        TargetSelf,
        Look,
    }

    public class TuiConfig : BotConfig
    {
        public Dictionary<ConsoleKey, TuiAction> KeyMap { get; private set; }

        // Server-side player preferences (CF_* flags sent via UC_SEND_PREFERENCES)
        public bool PrefSafetyOff    { get; set; } = false;
        public bool PrefTempSafe     { get; set; } = true;
        public bool PrefGrouping     { get; set; } = false;
        public bool PrefAutoLoot     { get; set; } = true;
        public bool PrefAutoCombine  { get; set; } = true;
        public bool PrefReagentBag   { get; set; } = false;
        public bool PrefSpellPower   { get; set; } = false;

        public TuiConfig() : base()
        {
            KeyMap = new Dictionary<ConsoleKey, TuiAction>();
            SetDefaultKeyMap();
        }

        private void SetDefaultKeyMap()
        {
            KeyMap.Clear();
            KeyMap[ConsoleKey.UpArrow]    = TuiAction.MoveUp;
            KeyMap[ConsoleKey.DownArrow]  = TuiAction.MoveDown;
            KeyMap[ConsoleKey.LeftArrow]  = TuiAction.MoveLeft;
            KeyMap[ConsoleKey.RightArrow] = TuiAction.MoveRight;
            KeyMap[ConsoleKey.W]          = TuiAction.MoveUp;
            KeyMap[ConsoleKey.S]          = TuiAction.MoveDown;
            KeyMap[ConsoleKey.A]          = TuiAction.MoveLeft;
            KeyMap[ConsoleKey.D]          = TuiAction.MoveRight;
            KeyMap[ConsoleKey.Spacebar]   = TuiAction.Use;
            KeyMap[ConsoleKey.Q]          = TuiAction.TargetSelf;
            KeyMap[ConsoleKey.Add]        = TuiAction.ZoomIn;
            KeyMap[ConsoleKey.OemPlus]    = TuiAction.ZoomIn;
            KeyMap[ConsoleKey.Subtract]   = TuiAction.ZoomOut;
            KeyMap[ConsoleKey.OemMinus]   = TuiAction.ZoomOut;
            KeyMap[ConsoleKey.PageUp]     = TuiAction.ScrollUp;
            KeyMap[ConsoleKey.PageDown]   = TuiAction.ScrollDown;
            KeyMap[ConsoleKey.Enter]      = TuiAction.EnterChat;
            KeyMap[ConsoleKey.R]          = TuiAction.ToggleRun;
            KeyMap[ConsoleKey.F5]         = TuiAction.Refresh;
            KeyMap[ConsoleKey.G]          = TuiAction.ManualGo;
            KeyMap[ConsoleKey.N]          = TuiAction.ToggleNetTab;
            KeyMap[ConsoleKey.C]          = TuiAction.OpenCharSheet;
            KeyMap[ConsoleKey.F]          = TuiAction.ToggleAutoAttack;
            KeyMap[ConsoleKey.T]          = TuiAction.TargetNearest;
            KeyMap[ConsoleKey.L]          = TuiAction.Look;
        }

        public override void ReadXml(XmlDocument Document)
        {
            base.ReadXml(Document);

            XmlNode prefsNode = Document.DocumentElement.SelectSingleNode("/configuration/preferences");
            if (prefsNode != null)
            {
                bool TryBool(string attr, bool def) {
                    var v = prefsNode.Attributes[attr]?.Value;
                    return v != null && bool.TryParse(v, out bool r) ? r : def;
                }
                PrefSafetyOff   = TryBool("safetyoff",   PrefSafetyOff);
                PrefTempSafe    = TryBool("tempsafe",     PrefTempSafe);
                PrefGrouping    = TryBool("grouping",     PrefGrouping);
                PrefAutoLoot    = TryBool("autoloot",     PrefAutoLoot);
                PrefAutoCombine = TryBool("autocombine",  PrefAutoCombine);
                PrefReagentBag  = TryBool("reagentbag",   PrefReagentBag);
                PrefSpellPower  = TryBool("spellpower",   PrefSpellPower);
            }

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
