using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Meridian59.Bot;
using Meridian59.Common;
using Meridian59.Common.Constants;
using Meridian59.Common.Enums;
using Meridian59.Data;
using Meridian59.Data.Models;
using Meridian59.Files;
using Meridian59.Files.ROO;
using Meridian59.Protocol.Enums;
using Meridian59.Protocol.GameMessages;

using Real = System.Single;

namespace Meridian59.TuiClient
{
    public enum PopupMode
    {
        None,
        MailList,
        MailRead,
        MailCompose,
        NewsList,
        NewsRead,
        NewsCompose
    }

    public class TuiClient : BotClient<GameTick, ResourceManager, DataController, TuiConfig>
    {
        private AsciiRenderer renderer;
        private PathRecorder recorder;
        private bool isRecording = false;
        private string recordingFile = null;
        private bool isNoClip = false;

        private int lastWindowWidth;
        private int lastWindowHeight;
        private uint lastRoomID = 0;

        // UI State
        private PopupMode activePopup = PopupMode.None;
        private int popupSelectedIndex = 0;
        private int popupScrollOffset = 0;
        private object currentPopupItem = null;
        private bool popupJustClosed = false;

        // Composition State
        private string composeRecipient = "";
        private string composeSubject = "";
        private List<string> composeBody = new List<string> { "" };
        private int composeState = 0; // 0=Recipient, 1=Subject, 2=Body

        // Concurrency
        private readonly object consoleLock = new object();
        private readonly object logLock = new object();

        // Overrides to prevent base class drawing
        public override void DrawCoordinates() { }
        public override void DrawCondition() { }
        public override void DrawRoom() { }
        public override void DrawResting() { }
        public override void DrawRTT() { }
        public override void DrawCash() { }
        public override void DrawBoxes() { }

        // Scrollback ringbuffer
        private const int LOG_CAPACITY = 500;
        private readonly List<LogEntry> logBuffer = new List<LogEntry>(LOG_CAPACITY + 1);
        private int scrollOffset = 0;   // 0 = newest at bottom, positive = scrolled back

        public struct LogEntry
        {
            public readonly DateTime Timestamp;
            public readonly string Type;
            public readonly string Text;
            public LogEntry(string type, string text) { Timestamp = DateTime.Now; Type = type; Text = text; }
        }

        // Text input
        private string inputBuffer = "";
        private bool inputMode = false;  // true = typing into chat input; false = gameplay keys active

        // Auto-quit
        private const int AUTO_QUIT_SECONDS = 0;
        private DateTime gameModeSince = DateTime.MinValue;

        // Script execution
        public bool NoAutoexec { get; set; } = false;

        // Layout constants
        private const int LOG_FIRST_ROW = 5;  // first row of the log area (row 0-4 are stats/borders)
        private const int LEFT_PANEL_WIDTH = 78; // printable cols inside left panel (cols 1..78)

        public TuiClient() : base()
        {
            renderer = new AsciiRenderer();
            recorder = new PathRecorder(this);
            IsService = true; // Completely suppress BotClient console drawing and input loop
            lastWindowWidth = Console.WindowWidth;
            lastWindowHeight = Console.WindowHeight;
        }

        #region BaseClient Implementation
        public override byte AppVersionMajor => 1;
        public override byte AppVersionMinor => 0;

        protected override void OnServerConnectionException(Exception Error)
        {
            Log("ERROR", "Connection error: " + Error.Message);
        }

        protected override void HandleLoginModeMessageMessage(LoginModeMessageMessage Message)
        {
            Log("SYS", Message.Message);
        }
        #endregion

        private bool HasTty => Console.WindowWidth > 0 && Console.WindowHeight > 0;

        // Row just above the input separator — last writable log row is LogBottom-1.
        private int LogBottom => Console.WindowHeight - 4;

        public override void Init()
        {
            base.Init();

            if (!HasTty) return;
            Console.CursorVisible = false;
            lock (consoleLock)
            {
                Console.Clear();
                DrawTuiLayout();
            }

            // Hook up data changes to trigger redraws if popup is active
            Data.NewsGroup.Articles.ListChanged += (s, e) => { if (activePopup == PopupMode.NewsList) DrawMap(); };
            ResourceManager.Mails.ListChanged += (s, e) => { if (activePopup == PopupMode.MailList) DrawMap(); };
            Data.NewsGroup.PropertyChanged += (s, e) => { if (activePopup == PopupMode.NewsRead && e.PropertyName == "Text") DrawMap(); };

            // Ensure UI updates when room or avatar changes
            Data.PropertyChanged += (s, e) => {
                if (e.PropertyName == "RoomInformation" || e.PropertyName == "AvatarObject")
                {
                    DrawRoomInfo();
                    DrawMap();
                }
            };
        }

        public override void Update()
        {
            try
            {
                base.Update();
            }
            catch (InvalidOperationException) { /* no TTY */ }

            ProcessScriptQueue();

            // Manually handle input loop since IsService=true disables it in base
            if (HasTty)
            {
                try
                {
                    while (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo key = Console.ReadKey(true);
                        ProcessKeyPress(key);
                    }
                }
                catch (InvalidOperationException) { /* No TTY attached */ }
            }

            // Game logic runs regardless of TTY
            if (AUTO_QUIT_SECONDS > 0 && gameModeSince != DateTime.MinValue &&
                (DateTime.Now - gameModeSince).TotalSeconds >= AUTO_QUIT_SECONDS)
            {
                Log("SYS", $"Auto-quit after {AUTO_QUIT_SECONDS}s in game mode.");
                ServerConnection.Disconnect();
                IsRunning = false;
                return;
            }

            // Record game mode entry
            if (Data.UIMode == UIMode.Playing && gameModeSince == DateTime.MinValue)
            {
                gameModeSince = DateTime.Now;
            }

            // UI rendering only when TTY is available
            if (!HasTty) return;

            lock (consoleLock)
            {
                if (Console.WindowWidth != lastWindowWidth || Console.WindowHeight != lastWindowHeight)
                {
                    lastWindowWidth = Console.WindowWidth;
                    lastWindowHeight = Console.WindowHeight;
                    Console.Clear();
                    renderer.Invalidate();
                    DrawTuiLayout();
                }
                else
                {
                    // Detect room change
                    var ri = Data.RoomInformation;
                    if (ri != null && ri.RoomID != lastRoomID)
                    {
                        lastRoomID = ri.RoomID;
                        Console.Clear();
                        DrawTuiLayout();
                    }
                    else
                    {
                        DrawStats();
                        DrawRoomInfo();
                        DrawMap();
                        DrawInputField();
                    }
                }
            }
        }

        protected override void HandleSaidMessage(SaidMessage Message)
        {
            base.HandleSaidMessage(Message);
            string text = Message.Message?.FullString;
            if (!string.IsNullOrEmpty(text))
            {
                var speaker = Data.RoomObjects.FirstOrDefault(o => o.ID == Message.Message.SourceObjectID);
                string speakerName = speaker?.Name ?? "???";
                Log("CHAT", $"{speakerName}: {text}");
            }
        }

        private Queue<string> scriptQueue = new Queue<string>();
        private DateTime nextScriptCommandTime = DateTime.MinValue;

        protected override void HandleGameStateMessage(GameStateMessage Message)
        {
            base.HandleGameStateMessage(Message);
            if (!NoAutoexec)
            {
                LoadAutoexec();
            }
        }

        private void LoadAutoexec()
        {
            if (File.Exists("autoexec.script"))
            {
                Log("SYS", "Loading autoexec.script...");
                string[] lines = File.ReadAllLines("autoexec.script");
                foreach (string line in lines)
                {
                    string cmd = line.Trim();
                    if (string.IsNullOrEmpty(cmd) || cmd.StartsWith("//") || cmd.StartsWith("#"))
                        continue;
                    scriptQueue.Enqueue(cmd);
                }
            }
        }

        private void ProcessScriptQueue()
        {
            if (scriptQueue.Count == 0 || DateTime.Now < nextScriptCommandTime)
                return;

            string cmd = scriptQueue.Dequeue();
            
            if (cmd.StartsWith("wait ", StringComparison.OrdinalIgnoreCase) || 
                cmd.StartsWith("sleep ", StringComparison.OrdinalIgnoreCase))
            {
                int spaceIdx = cmd.IndexOf(' ');
                if (int.TryParse(cmd.Substring(spaceIdx + 1), out int ms))
                {
                    nextScriptCommandTime = DateTime.Now.AddMilliseconds(ms);
                    return;
                }
            }
            if (cmd.StartsWith("log ", StringComparison.OrdinalIgnoreCase))
            {
                Log("SYS", cmd.Substring(4));
            }
            else
            {
                ProcessCommand(cmd);
            }
        }

        protected override void HandleCharactersMessage(CharactersMessage Message)
        {
            Log("SYS", $"Received character list ({Message.WelcomeInfo.Characters.Count} characters).");
            foreach (var character in Message.WelcomeInfo.Characters)
            {
                Log("SYS", $"Account Character: {character.Name} (ID: {character.ID})");
            }
            base.HandleCharactersMessage(Message);
        }

        protected override void HandleGameModeMessage(GameModeMessage Message)
        {
            var pi = (MessageTypeGameMode)Message.PI;

            if (Config.IsDebugEnabled)
            {
                Log("DEBUG", $"NET RECV: PI={Message.PI} ({pi}) Length={Message.ByteLength}");
            }

            if (pi == MessageTypeGameMode.Message)
            {
                var pMsg = (MessageMessage)Message;
                if (pMsg.Message != null)
                {
                    Log("SYS", pMsg.Message.FullString);
                }
            }
            else if (pi == MessageTypeGameMode.Move)
            {
                var m = (MoveMessage)Message;
                var avatarID = Data.AvatarObject?.ID ?? 0;
                Log("NET", $"Move: obj={m.ObjectID}{(m.ObjectID == avatarID ? " (AVATAR)" : "")} x={m.NewCoordinateX} y={m.NewCoordinateY} spd={m.MovementSpeed}");
            }
            else if (pi == MessageTypeGameMode.PlayWave)
            {
                var w = (PlayWaveMessage)Message;
                Log("NET", $"PlayWave: {w.PlayInfo?.ResourceName ?? "?"}");
            }
            else if (pi == MessageTypeGameMode.Effect)
            {
                var e = (EffectMessage)Message;
                Log("NET", $"Effect: {e.Effect}");
            }
            else if (pi == MessageTypeGameMode.Create)
            {
                var c = (CreateMessage)Message;
                Log("NET", $"Create: obj={c.NewRoomObject?.ID} name={c.NewRoomObject?.Name} x={c.NewRoomObject?.CoordinateX} y={c.NewRoomObject?.CoordinateY}");
            }
            else if (pi == MessageTypeGameMode.Spells)
            {
                var s = (SpellsMessage)Message;
                Log("NET", $"Spells: Count={s.SpellObjects.Length}");
                foreach (var spell in s.SpellObjects)
                {
                    Log("NET", $"  Spell: ID={spell.ID} Name={spell.Name} Targets={spell.TargetsCount}");
                }
            }

            base.HandleGameModeMessage(Message);
        }

        // ── Layout ──────────────────────────────────────────────────────────────

        private void DrawTuiLayout()
        {
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            if (w < 85 || h < 10) return;

            lock (consoleLock)
            {
                // Row 0: top border
                Console.SetCursorPosition(0, 0);
                string topL = "╔" + new string('═', 14) + "╦" + new string('═', 43) + "╦" + new string('═', 19) + "╗"; // 80 chars
                string topR = " ╔" + new string('═', Math.Max(0, w - 84)) + "╗";
                Console.Write(SafeLine(topL + topR, w - 1));

                // Rows 2-4: stats sub-borders
                Console.SetCursorPosition(0, 2);
                string midL = "║" + new string(' ', 14) + "╠" + new string('═', 13) + "╦" + new string('═', 15) + "╦" + new string('═', 13) + "╬" + new string('═', 19) + "╣";
                string midR = " ║" + new string(' ', Math.Max(0, w - 84)) + "║";
                Console.Write(SafeLine(midL + midR, w - 1));

                Console.SetCursorPosition(0, 3);
                string statL = "║" + new string(' ', 14) + "║" + new string(' ', 13) + "║" + new string(' ', 15) + "║" + new string(' ', 13) + "║" + new string(' ', 19) + "║";
                Console.Write(SafeLine(statL + midR, w - 1));

                Console.SetCursorPosition(0, 4);
                string botL = "╠" + new string('═', 14) + "╩" + new string('═', 13) + "╩" + new string('═', 15) + "╩" + new string('═', 13) + "╩" + new string('═', 19) + "╣";
                Console.Write(SafeLine(botL + midR, w - 1));

                // Side borders
                for (int i = 1; i < h - 1; i++)
                {
                    if (i == 2 || i == 3 || i == 4 || i == h - 3 || i == h - 1) continue;
                    Console.SetCursorPosition(0, i);
                    Console.Write("║");
                    Console.SetCursorPosition(79, i); 
                    Console.Write("║ ║"); 
                    Console.SetCursorPosition(w - 2, i);
                    Console.Write("║");
                }

                // Input separator at h-3
                Console.SetCursorPosition(0, h - 3);
                string sepL = "╠" + new string('═', 78) + "╣";
                string sepR = " ║" + new string(' ', Math.Max(0, w - 84)) + "║";
                Console.Write(SafeLine(sepL + sepR, w - 1));

                // Input field row at h-2: Draw Borders explicitly
                Console.SetCursorPosition(0, h - 2);
                Console.Write("║");
                Console.SetCursorPosition(79, h - 2);
                Console.Write("║ ╚" + new string('═', Math.Max(0, w - 84)) + "╝");

                // Hint bar at h-1
                Console.SetCursorPosition(0, h - 1);
                string hints = " [Enter]Chat  [Arrows/WASD]Move  [+/-]Zoom  [PgUp/Dn]Scroll  [Q]uit";
                Console.Write(SafeLine("╚" + hints.PadRight(78, '═') + "╝", w - 1));

                DrawStats();
                DrawRoomInfo();
                DrawLog();
                DrawInputField();
                DrawMap();
            }
        }

        private string SafeLine(string line, int max)
        {
            string sanitized = line.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return sanitized.Length > max ? sanitized[..max] : sanitized;
        }

        // ── Stats ────────────────────────────────────────────────────────────────

        private static string StatStr(Meridian59.Data.Lists.StatNumericList cond, uint num)
        {
            var s = cond.GetItemByNum(num);
            if (s == null) return "---/---";
            return $"{s.ValueCurrent,3}/{s.ValueMaximum,-3}";
        }

        public void DrawStats()
        {
            if (!HasTty) return;
            lock (consoleLock)
            {
                Console.SetCursorPosition(2, 1);
                Console.Write("HP:  " + StatStr(Data.AvatarCondition, 1)); 

                Console.SetCursorPosition(2, 2);
                Console.Write("MP:  " + StatStr(Data.AvatarCondition, 2)); 

                Console.SetCursorPosition(2, 3);
                Console.Write("VIG: " + StatStr(Data.AvatarCondition, 3)); 

                uint cash = 0;
                var shilling = Data.InventoryObjects.GetItemByName("shilling", false);
                if (shilling != null) cash = (uint)shilling.Count;

                Console.SetCursorPosition(50, 3);
                Console.Write($"$: {cash,-8}");

                Console.SetCursorPosition(17, 3);
                Console.Write($"RTT: {ServerConnection.RTT,-4}ms");

                Console.SetCursorPosition(33, 3);
                Console.Write($"REST: {Data.IsResting,-5}");
            }
        }

        public void DrawRoomInfo()
        {
            if (!HasTty) return;
            var ri = Data.RoomInformation;
            var avatar = Data.AvatarObject;

            lock (consoleLock)
            {
                Console.SetCursorPosition(17, 1);
                Console.ForegroundColor = ConsoleColor.White;
                string roomName = ri?.RoomName ?? "Unknown Room";
                if (roomName.Length > 30) roomName = roomName[..27] + "...";
                Console.Write($"ROOM: {roomName,-30}");
                Console.ResetColor();

                if (avatar != null)
                {
                    Console.SetCursorPosition(60, 1);
                    Console.Write($"X:{avatar.CoordinateX,5} Y:{avatar.CoordinateY,5}");

                    int rooX = avatar.CoordinateX * 16 - 1024;
                    int rooZ = avatar.CoordinateY * 16 - 1024;
                    int col    = rooX >= 0 ? rooX / 1024 + 1 : 0;
                    int row    = rooZ >= 0 ? rooZ / 1024 + 1 : 0;
                    int fineCol = rooX >= 0 ? (rooX % 1024) >> 4 : 0;
                    int fineRow = rooZ >= 0 ? (rooZ % 1024) >> 4 : 0;
                    Console.SetCursorPosition(17, 2);
                    Console.Write($"R:{row,3} C:{col,3} FR:{fineRow,2} FC:{fineCol,2}");
                }
            }
        }

        // ── Log ──────────────────────────────────────────────────────────────────

        public void DrawLog()
        {
            if (!HasTty) return;

            int logWidth = 78;
            int logHeight = LogBottom - LOG_FIRST_ROW;

            lock (consoleLock)
            {
                lock (logLock)
                {
                    for (int i = 0; i < logHeight; i++)
                    {
                        int bufferIdx = logBuffer.Count - 1 - scrollOffset - (logHeight - 1 - i);
                        Console.SetCursorPosition(1, LOG_FIRST_ROW + i);

                        if (bufferIdx >= 0 && bufferIdx < logBuffer.Count)
                        {
                            var entry = logBuffer[bufferIdx];
                            string prefix = entry.Timestamp.ToString("h:mm tt ");
                            Console.ForegroundColor = ConsoleColor.Gray;
                            Console.Write(prefix);

                            switch (entry.Type)
                            {
                                case "CHAT":  Console.ForegroundColor = ConsoleColor.Green; break;
                                case "SYS":   Console.ForegroundColor = ConsoleColor.Cyan; break;
                                case "NET":   Console.ForegroundColor = ConsoleColor.DarkGray; break;
                                case "ERROR": Console.ForegroundColor = ConsoleColor.Red; break;
                                case "DEBUG": Console.ForegroundColor = ConsoleColor.DarkMagenta; break;
                                default:      Console.ForegroundColor = ConsoleColor.White; break;
                            }
                            Console.Write($"{entry.Type,-8}");
                            Console.ResetColor();

                            string content = entry.Text;
                            int avail = logWidth - 16; 
                            Console.Write(SafeLine(content.PadRight(avail), avail));
                        }
                        else
                        {
                            Console.Write(new string(' ', logWidth));
                        }
                    }
                }
            }
        }

        public override void Log(string Type, string Text)
        {
            logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Type,-8} {Text}");

            if (Type == "DEBUG" && HasTty) return;

            int avail = LEFT_PANEL_WIDTH - 16;
            string sanitized = Text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            
            lock (logLock)
            {
                while (sanitized.Length > 0)
                {
                    string chunk;
                    if (sanitized.Length <= avail)
                    {
                        chunk = sanitized;
                        sanitized = "";
                    }
                    else
                    {
                        int lastSpace = sanitized.LastIndexOf(' ', avail);
                        if (lastSpace > 0)
                        {
                            chunk = sanitized[..lastSpace];
                            sanitized = sanitized[(lastSpace + 1)..];
                        }
                        else
                        {
                            chunk = sanitized[..avail];
                            sanitized = sanitized[avail..];
                        }
                    }
                    logBuffer.Add(new LogEntry(Type, chunk));
                }

                if (logBuffer.Count > LOG_CAPACITY)
                    logBuffer.RemoveAt(0);
            }

            if (scrollOffset == 0 && HasTty)
                DrawLog();
        }

        // ── Map ──────────────────────────────────────────────────────────────────

        public void DrawMap()
        {
            int startX = 82; 
            int startY = 5;  
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            
            int width  = w - startX - 2; 
            int height = h - 4 - startY; 

            if (width < 10 || height < 5) return;

            lock (consoleLock)
            {
                if (activePopup != PopupMode.None)
                {
                    DrawPopup(startX, startY, width, height);
                }
                else
                {
                    if (popupJustClosed)
                    {
                        // Clear popup frame leftovers
                        Console.SetCursorPosition(startX, startY);
                        Console.Write(new string(' ', width));
                        Console.SetCursorPosition(startX, startY + height - 1);
                        Console.Write(new string(' ', width));
                        for (int i = 1; i < height - 1; i++)
                        {
                            Console.SetCursorPosition(startX, startY + i);
                            Console.Write(" ");
                            Console.SetCursorPosition(startX + width - 1, startY + i);
                            Console.Write(" ");
                        }
                        popupJustClosed = false;
                    }
                    renderer.Render(this, startX + 1, startY + 1, width - 2, height - 2);
                }
            }
        }

        private List<Mail> GetValidMails()
        {
            return ResourceManager.Mails.Where(m => !m.IsMessageForNoMessages() 
                && !string.IsNullOrEmpty(m.Sender) 
                && m.Sender != "0" 
                && !m.Sender.Equals("none", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void DrawPopup(int startX, int startY, int width, int height)
        {
            int w = Console.WindowWidth;
            if (startX + width > w - 1) width = w - 1 - startX;
            if (width < 5) return;

            // 1. Window Frame
            Console.SetCursorPosition(startX, startY);
            Console.Write(SafeLine("╔" + new string('═', width - 2) + "╗", width));
            for (int i = 1; i < height - 1; i++)
            {
                Console.SetCursorPosition(startX, startY + i);
                Console.Write(SafeLine("║" + new string(' ', width - 2) + "║", width));
            }
            Console.SetCursorPosition(startX, startY + height - 1);
            Console.Write(SafeLine("╚" + new string('═', width - 2) + "╝", width));

            // 2. Title
            string title = $" {activePopup} ";
            if (title.Length > width - 4) title = title[..(width - 4)];
            Console.SetCursorPosition(startX + (width - title.Length) / 2, startY);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(title);
            Console.ResetColor();

            // 3. Footer
            string footer = activePopup.ToString().Contains("Compose") 
                ? " [Enter:Next/Send] [Esc:Cancel] "
                : " [Esc:Close] [Del:Delete] [R:Reply] [N:New] ";
            if (footer.Length > width - 4) footer = footer[..(width - 4)];
            Console.SetCursorPosition(startX + (width - footer.Length) / 2, startY + height - 1);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(footer);
            Console.ResetColor();

            int contentX = startX + 2;
            int contentY = startY + 2;
            int contentW = width - 4;
            int contentH = height - 4;

            if (contentW < 5 || contentH < 1) return;

            if (activePopup == PopupMode.MailList)
            {
                var mails = GetValidMails();
                for (int i = 0; i < contentH && (i + popupScrollOffset) < mails.Count; i++)
                {
                    var mail = mails[i + popupScrollOffset];
                    Console.SetCursorPosition(contentX, contentY + i);
                    if (i + popupScrollOffset == popupSelectedIndex) Console.BackgroundColor = ConsoleColor.DarkBlue;
                    string date = MeridianDate.ToDateTime(mail.Timestamp).ToShortDateString();
                    string sender = mail.Sender ?? "Unknown";
                    if (sender.Length > 20) sender = sender[..17] + "...";
                    string line = $"[{date}] {sender,-20} {mail.Title}";
                    Console.Write(SafeLine(line.PadRight(contentW), contentW));
                    Console.ResetColor();
                }
            }
            else if (activePopup == PopupMode.NewsList)
            {
                var articles = Data.NewsGroup.Articles;
                for (int i = 0; i < contentH && (i + popupScrollOffset) < articles.Count; i++)
                {
                    var art = articles[i + popupScrollOffset];
                    Console.SetCursorPosition(contentX, contentY + i);
                    if (i + popupScrollOffset == popupSelectedIndex) Console.BackgroundColor = ConsoleColor.DarkBlue;
                    string date = art.Time.ToShortDateString();
                    string poster = art.Poster ?? "Unknown";
                    if (poster.Length > 20) poster = poster[..17] + "...";
                    string line = $"{art.Number,4} {poster,-20} {art.Title} ({date})";
                    Console.Write(SafeLine(line.PadRight(contentW), contentW));
                    Console.ResetColor();
                }
            }
            else if (activePopup == PopupMode.MailRead || activePopup == PopupMode.NewsRead)
            {
                string text = "";
                string header = "";
                if (activePopup == PopupMode.MailRead && currentPopupItem is Mail m)
                {
                    header = $"From: {m.Sender}  Subj: {m.Title}";
                    text = m.Message?.FullString ?? "";
                }
                else if (activePopup == PopupMode.NewsRead && currentPopupItem is ArticleHead ah)
                {
                    header = $"By: {ah.Poster}  Subj: {ah.Title}";
                    text = Data.NewsGroup.Text ?? "(Loading...)";
                }

                Console.SetCursorPosition(contentX, contentY - 1);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(SafeLine(header.PadRight(contentW), contentW));
                Console.ResetColor();

                string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                List<string> wrapped = new List<string>();
                foreach (var l in lines)
                {
                    string rem = l;
                    if (string.IsNullOrEmpty(rem)) { wrapped.Add(""); continue; }
                    while (rem.Length > contentW)
                    {
                        wrapped.Add(rem[..contentW]);
                        rem = rem[contentW..];
                    }
                    wrapped.Add(rem);
                }

                for (int i = 0; i < contentH && (i + popupScrollOffset) < wrapped.Count; i++)
                {
                    Console.SetCursorPosition(contentX, contentY + i);
                    Console.Write(SafeLine(wrapped[i + popupScrollOffset].PadRight(contentW), contentW));
                }
            }
            else if (activePopup == PopupMode.MailCompose || activePopup == PopupMode.NewsCompose)
            {
                bool isMail = activePopup == PopupMode.MailCompose;
                int row = contentY;
                if (isMail)
                {
                    Console.SetCursorPosition(contentX, row++);
                    Console.Write(SafeLine(("To: ".PadRight(10) + composeRecipient).PadRight(contentW), contentW));
                    if (composeState == 0) { Console.SetCursorPosition(contentX + 10 + composeRecipient.Length, row - 1); Console.Write("_"); }
                }
                Console.SetCursorPosition(contentX, row++);
                Console.Write(SafeLine(("Subject: ".PadRight(10) + composeSubject).PadRight(contentW), contentW));
                if (composeState == 1) { Console.SetCursorPosition(contentX + 10 + composeSubject.Length, row - 1); Console.Write("_"); }
                row++;
                Console.SetCursorPosition(contentX, row++);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(SafeLine("Message Body:".PadRight(contentW), contentW));
                Console.ResetColor();
                int bodyStartRow = row;
                int availableRows = height - (bodyStartRow - startY) - 1;
                for (int i = 0; i < availableRows && i < composeBody.Count; i++)
                {
                    Console.SetCursorPosition(contentX, bodyStartRow + i);
                    Console.Write(SafeLine(composeBody[i].PadRight(contentW), contentW));
                    if (composeState == 2 && i == composeBody.Count - 1)
                    {
                        Console.SetCursorPosition(contentX + composeBody[i].Length, bodyStartRow + i);
                        Console.Write("_");
                    }
                }
            }
        }

        private void ProcessPopupKeyPress(ConsoleKeyInfo key)
        {
            if (activePopup == PopupMode.MailCompose || activePopup == PopupMode.NewsCompose)
            {
                HandleComposeInput(key);
                return;
            }

            int listSize = 0;
            if (activePopup == PopupMode.MailList) listSize = GetValidMails().Count;
            else if (activePopup == PopupMode.NewsList) listSize = Data.NewsGroup.Articles.Count;

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    if (activePopup == PopupMode.MailRead) activePopup = PopupMode.MailList;
                    else if (activePopup == PopupMode.NewsRead) activePopup = PopupMode.NewsList;
                    else {
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                    }
                    popupScrollOffset = 0;
                    DrawMap();
                    break;

                case ConsoleKey.Delete:
                    if (activePopup == PopupMode.MailList)
                    {
                        var mails = GetValidMails();
                        if (popupSelectedIndex < mails.Count)
                        {
                            var mailToDelete = mails[popupSelectedIndex];
                            SendDeleteMail(mailToDelete.Num);
                            ResourceManager.Mails.Remove(mailToDelete);
                            if (popupSelectedIndex >= GetValidMails().Count) popupSelectedIndex = Math.Max(0, popupSelectedIndex - 1);
                        }
                    }
                    else if (activePopup == PopupMode.NewsList)
                    {
                        if (popupSelectedIndex < Data.NewsGroup.Articles.Count)
                        {
                            var art = Data.NewsGroup.Articles[popupSelectedIndex];
#if !VANILLA
                            SendDeleteNews(Data.NewsGroup.NewsGlobeID, art.Number);
#endif
                            Data.NewsGroup.Articles.RemoveAt(popupSelectedIndex);
                            if (popupSelectedIndex >= Data.NewsGroup.Articles.Count) popupSelectedIndex = Math.Max(0, popupSelectedIndex - 1);
                        }
                    }
                    DrawMap();
                    break;

                case ConsoleKey.R:
                    if (activePopup == PopupMode.MailList)
                    {
                        var mails = GetValidMails();
                        if (popupSelectedIndex < mails.Count)
                        {
                            var m = mails[popupSelectedIndex];
                            activePopup = PopupMode.MailCompose;
                            composeRecipient = m.Sender;
                            composeSubject = m.Title.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? m.Title : "Re: " + m.Title;
                            composeBody = new List<string> { "" };
                            composeState = 2; 
                            DrawMap();
                        }
                    }
                    else if (activePopup == PopupMode.MailRead && currentPopupItem is Mail rm)
                    {
                        activePopup = PopupMode.MailCompose;
                        composeRecipient = rm.Sender;
                        composeSubject = rm.Title.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? rm.Title : "Re: " + rm.Title;
                        composeBody = new List<string> { "" };
                        composeState = 2; 
                        DrawMap();
                    }
                    else if (activePopup == PopupMode.NewsList && popupSelectedIndex < Data.NewsGroup.Articles.Count)
                    {
                        var art = Data.NewsGroup.Articles[popupSelectedIndex];
                        activePopup = PopupMode.NewsCompose;
                        composeSubject = art.Title.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? art.Title : "Re: " + art.Title;
                        composeBody = new List<string> { "" };
                        composeState = 2; 
                        DrawMap();
                    }
                    break;

                case ConsoleKey.N:
                    if (activePopup == PopupMode.MailList || activePopup == PopupMode.MailRead)
                    {
                        activePopup = PopupMode.MailCompose;
                        composeRecipient = "";
                        composeSubject = "";
                        composeBody = new List<string> { "" };
                        composeState = 0;
                        DrawMap();
                    }
                    else if (activePopup == PopupMode.NewsList || activePopup == PopupMode.NewsRead)
                    {
                        activePopup = PopupMode.NewsCompose;
                        composeSubject = "";
                        composeBody = new List<string> { "" };
                        composeState = 1;
                        DrawMap();
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (activePopup == PopupMode.MailList || activePopup == PopupMode.NewsList)
                    {
                        if (popupSelectedIndex > 0) popupSelectedIndex--;
                        if (popupSelectedIndex < popupScrollOffset) popupScrollOffset = popupSelectedIndex;
                    }
                    else if (popupScrollOffset > 0) popupScrollOffset--;
                    DrawMap();
                    break;

                case ConsoleKey.DownArrow:
                    if (activePopup == PopupMode.MailList || activePopup == PopupMode.NewsList)
                    {
                        if (popupSelectedIndex < listSize - 1) popupSelectedIndex++;
                        int h = Console.WindowHeight;
                        int height = h - 4 - 5; 
                        int contentH = height - 4;
                        if (popupSelectedIndex >= popupScrollOffset + contentH) popupScrollOffset = popupSelectedIndex - contentH + 1;
                    }
                    else popupScrollOffset++;
                    DrawMap();
                    break;

                case ConsoleKey.PageUp:
                    if (activePopup == PopupMode.MailList || activePopup == PopupMode.NewsList)
                    {
                        int h = Console.WindowHeight;
                        int contentH = h - 4 - 5 - 4;
                        popupSelectedIndex = Math.Max(0, popupSelectedIndex - contentH);
                        popupScrollOffset = Math.Max(0, popupScrollOffset - contentH);
                    }
                    else popupScrollOffset = Math.Max(0, popupScrollOffset - 10);
                    DrawMap();
                    break;

                case ConsoleKey.PageDown:
                    if (activePopup == PopupMode.MailList || activePopup == PopupMode.NewsList)
                    {
                        int h = Console.WindowHeight;
                        int contentH = h - 4 - 5 - 4;
                        popupSelectedIndex = Math.Min(listSize - 1, popupSelectedIndex + contentH);
                        popupScrollOffset = Math.Min(Math.Max(0, listSize - contentH), popupScrollOffset + contentH);
                    }
                    else popupScrollOffset += 10;
                    DrawMap();
                    break;

                case ConsoleKey.Enter:
                    if (activePopup == PopupMode.MailList)
                    {
                        var mails = GetValidMails();
                        if (popupSelectedIndex < mails.Count)
                        {
                            currentPopupItem = mails[popupSelectedIndex];
                            activePopup = PopupMode.MailRead;
                            popupScrollOffset = 0;
                        }
                    }
                    else if (activePopup == PopupMode.NewsList && popupSelectedIndex < Data.NewsGroup.Articles.Count)
                    {
                        var art = Data.NewsGroup.Articles[popupSelectedIndex];
                        currentPopupItem = art;
                        activePopup = PopupMode.NewsRead;
                        popupScrollOffset = 0;
                        SendReqArticle(Data.NewsGroup.NewsGlobeID, art.Number);
                    }
                    DrawMap();
                    break;
            }
        }

        private void HandleComposeInput(ConsoleKeyInfo key)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                activePopup = (activePopup == PopupMode.MailCompose) ? PopupMode.MailList : PopupMode.NewsList;
                DrawMap();
                return;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                if (composeState < 2) composeState++;
                else
                {
                    if (string.IsNullOrEmpty(composeBody[^1])) FinishCompose();
                    else composeBody.Add("");
                }
                DrawMap();
                return;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (composeState == 0 && composeRecipient.Length > 0) composeRecipient = composeRecipient[..^1];
                else if (composeState == 1 && composeSubject.Length > 0) composeSubject = composeSubject[..^1];
                else if (composeState == 2)
                {
                    var lastLine = composeBody[^1];
                    if (lastLine.Length > 0) composeBody[^1] = lastLine[..^1];
                    else if (composeBody.Count > 1) composeBody.RemoveAt(composeBody.Count - 1);
                }
                DrawMap();
                return;
            }

            if (!char.IsControl(key.KeyChar))
            {
                if (composeState == 0) composeRecipient += key.KeyChar;
                else if (composeState == 1) composeSubject += key.KeyChar;
                else if (composeState == 2) composeBody[^1] += key.KeyChar;
                DrawMap();
            }
        }

        private void FinishCompose()
        {
            string body = string.Join("\n", composeBody.Where(s => !string.IsNullOrEmpty(s)));
            if (activePopup == PopupMode.MailCompose)
            {
                Log("SYS", $"Resolving recipient: {composeRecipient}...");
                SendReqLookupNames(new[] { composeRecipient });
            }
            else
            {
                Log("SYS", "Posting to newsgroup...");
                SendPostArticle(Data.NewsGroup.NewsGlobeID, composeSubject, body);
                activePopup = PopupMode.NewsList;
            }
        }

        protected override void HandleLookupNamesMessage(LookupNamesMessage Message)
        {
            base.HandleLookupNamesMessage(Message);
            if (activePopup == PopupMode.MailCompose)
            {
                if (Message.ResolvedIDs.Length > 0 && ObjectID.IsValid(Message.ResolvedIDs[0].ID))
                {
                    string body = string.Join("\n", composeBody.Where(s => !string.IsNullOrEmpty(s)));
                    SendSendMail(new[] { Message.ResolvedIDs[0] }, composeSubject, body);
                    Log("SYS", "Mail sent.");
                    activePopup = PopupMode.MailList;
                    DrawMap();
                }
                else
                {
                    Log("ERROR", $"Recipient '{composeRecipient}' not found.");
                    composeState = 0; 
                    DrawMap();
                }
            }
        }

        // ── Input / movement ─────────────────────────────────────────────────────

        protected override void ProcessKeyPress(ConsoleKeyInfo key)
        {
            if (activePopup != PopupMode.None)
            {
                ProcessPopupKeyPress(key);
                return;
            }

            if (inputMode)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        if (!string.IsNullOrWhiteSpace(inputBuffer))
                        {
                            ProcessCommand(inputBuffer);
                            inputBuffer = "";
                        }
                        inputMode = false;
                        DrawInputField();
                        break;

                    case ConsoleKey.Escape:
                        inputBuffer = "";
                        inputMode = false;
                        DrawInputField();
                        break;

                    case ConsoleKey.Backspace:
                        if (inputBuffer.Length > 0)
                        {
                            inputBuffer = inputBuffer[..^1];
                            DrawInputField();
                        }
                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            inputBuffer += key.KeyChar;
                            DrawInputField();
                        }
                        break;
                }
            }
            else
            {
                if (Config.KeyMap.TryGetValue(key.Key, out TuiAction action))
                {
                    PerformAction(action);
                }
                else
                {
                    base.ProcessKeyPress(key);
                }
            }
        }

        private void PerformAction(TuiAction action)
        {
            switch (action)
            {
                case TuiAction.EnterChat:
                    inputMode = true;
                    DrawInputField();
                    break;

                case TuiAction.Quit:
                    Log("SYS", "Exiting...");
                    ServerConnection.Disconnect();
                    IsRunning = false;
                    break;

                case TuiAction.MoveNorth: HandleMovement( 0, -1); break;
                case TuiAction.MoveSouth: HandleMovement( 0,  1); break;
                case TuiAction.MoveWest:  HandleMovement(-1,  0); break;
                case TuiAction.MoveEast:  HandleMovement( 1,  0); break;

                case TuiAction.ScrollUp:
                    scrollOffset = Math.Min(scrollOffset + 5, logBuffer.Count - 5);
                    DrawLog();
                    break;
                case TuiAction.ScrollDown:
                    scrollOffset = Math.Max(0, scrollOffset - 5);
                    DrawLog();
                    break;

                case TuiAction.ZoomIn:
                    renderer.ZoomIn();
                    DrawMap();
                    break;
                case TuiAction.ZoomOut:
                    renderer.ZoomOut();
                    DrawMap();
                    break;

                case TuiAction.ToggleRotation:
                    renderer.CycleOrientation();
                    Log("SYS", $"Orientation: {renderer.Orientation}");
                    DrawMap();
                    break;

                case TuiAction.Use:
                    SendReqActivate();
                    break;

                case TuiAction.ManualGo:
                    Log("MOVE", $"Manual ReqGo at X={Data.AvatarObject?.CoordinateX} Y={Data.AvatarObject?.CoordinateY}");
                    SendReqGo(true);
                    break;

                case TuiAction.Mail:
                    ProcessCommand("/mail");
                    break;

                case TuiAction.Rest:
                    SendUserCommandRest();
                    break;

                case TuiAction.Stand:
                    SendUserCommandStand();
                    break;

                case TuiAction.ToggleNoClip:
                    isNoClip = !isNoClip;
                    Log("SYS", "Noclip: " + (isNoClip ? "ON" : "OFF"));
                    break;

                case TuiAction.Hotkey1:
                case TuiAction.Hotkey2:
                case TuiAction.Hotkey3:
                case TuiAction.Hotkey4:
                    // Implement specific hotkey logic if needed, for now just log it
                    Log("SYS", $"Hotkey pressed: {action}");
                    break;
            }
        }

        private void ProcessCommand(string text)
        {
            if (text.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/logout", StringComparison.OrdinalIgnoreCase))
            {
                Log("SYS", "Disconnecting...");
                ServerConnection.Disconnect();
                IsRunning = false;
                return;
            }

            if (text.Equals("/mail", StringComparison.OrdinalIgnoreCase))
            {
                activePopup = PopupMode.MailList;
                popupSelectedIndex = 0;
                popupScrollOffset = 0;
                SendReqGetMail();
                DrawMap();
                return;
            }

            if (text.Equals("/news", StringComparison.OrdinalIgnoreCase))
            {
                activePopup = PopupMode.NewsList;
                popupSelectedIndex = 0;
                popupScrollOffset = 0;
                if (Data.NewsGroup.NewsGlobeID != 0) SendReqArticles();
                else Log("SYS", "No newsgroup selected. (Look at a newsglobe first)");
                DrawMap();
                return;
            }

            if (text.Equals("go", StringComparison.OrdinalIgnoreCase))
            {
                SendReqGo(true);
                return;
            }

            if (text.Equals("w", StringComparison.OrdinalIgnoreCase)) { HandleMovement( 0, -1); return; }
            if (text.Equals("s", StringComparison.OrdinalIgnoreCase)) { HandleMovement( 0,  1); return; }
            if (text.Equals("a", StringComparison.OrdinalIgnoreCase)) { HandleMovement(-1,  0); return; }
            if (text.Equals("d", StringComparison.OrdinalIgnoreCase)) { HandleMovement( 1,  0); return; }

            if (text.Equals("noclip", StringComparison.OrdinalIgnoreCase))
            {
                isNoClip = !isNoClip;
                Log("SYS", "Noclip: " + (isNoClip ? "ON" : "OFF"));
                return;
            }

            if (text.Equals("rest", StringComparison.OrdinalIgnoreCase))
            {
                SendUserCommandRest();
                return;
            }
            if (text.Equals("stand", StringComparison.OrdinalIgnoreCase))
            {
                SendUserCommandStand();
                return;
            }

            if (text.StartsWith("cast ", StringComparison.OrdinalIgnoreCase))
            {
                string spellName = text[5..].Trim();
                var spell = Data.AvatarSpells.GetItemByName(spellName, false);
                if (spell != null)
                {
                    Log("SYS", $"Casting: {spell.ResourceName} (No Target)");
                    SendReqCastMessage(spell.ObjectID);
                }
                else Log("ERROR", $"Unknown spell: {spellName}");
                return;
            }

            if (text.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                SendSayToMessage(ChatTransmissionType.Normal, text[4..].Trim());
                return;
            }

            if (text.StartsWith("tell ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text[5..].Split(new[] { ' ' }, 2);
                if (parts.Length == 2)
                {
                    Log("CHAT", $"You tell {parts[0]}: {parts[1]}");
                    SendSayGroupMessage(0, parts[1]); 
                }
                return;
            }

            SendSayToMessage(ChatTransmissionType.Normal, text);
        }

        private bool DoFineStep(V2 direction, float worldDist)
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) { Log("DBG", "DoFineStep: avatar null"); return false; }
            if (CurrentRoom == null) { Log("DBG", $"DoFineStep: CurrentRoom null (roomFile={Data.RoomInformation.RoomFile})"); return false; }
            var start2D = avatar.Position2D;
            var targetEnd = start2D + (direction * (Real)worldDist);
            var rooStart = avatar.Position3D.Clone();
            rooStart.ConvertToROO();
            var rooEnd2D = new V2(targetEnd.X * 16.0f - 1024.0f, targetEnd.Y * 16.0f - 1024.0f);
            var allowedROO = CurrentRoom.VerifyMove(ref rooStart, ref rooEnd2D, 50);
            if (allowedROO.LengthSquared < 0.000001f) return false;
            allowedROO.Scale(0.0625f);
            var finalPos = start2D + allowedROO;
            avatar.Position3D = new V3(finalPos.X, avatar.Position3D.Y, finalPos.Y);
            avatar.UpdatePosition(10, Data.RoomInformation);
            return true;
        }

        private void HandleMovement(int dx, int dy)
        {
            if (Data.AvatarObject == null) return;

            if (renderer.Orientation == ViewOrientation.FollowRotation)
            {
                // In M59, AngleUnits are 0-4095 CW: 0=East, 1024=South, 2048=West, 3072=North
                float avatarRad = (float)(Data.AvatarObject.AngleUnits * 2.0 * Math.PI / 4096.0);
                float cos = MathF.Cos(avatarRad);
                float sin = MathF.Sin(avatarRad);

                float worldDx = (-dy * cos) + (dx * -sin);
                float worldDy = (-dy * sin) + (dx * cos);

                Move((int)Math.Round(worldDx), (int)Math.Round(worldDy), Data.AvatarObject.AngleUnits);
            }
            else if (renderer.Orientation == ViewOrientation.SouthUp)
            {
                // Upside down: Inverting both axes
                ushort angle = 0;
                if (dx == 1) angle = 2048;      // Move East (Right) -> World West
                else if (dx == -1) angle = 0;   // Move West (Left) -> World East
                else if (dy == 1) angle = 3072; // Move South (Down) -> World North
                else if (dy == -1) angle = 1024;// Move North (Up) -> World South
                Move(-dx, -dy, angle);
            }
            else
            {
                // Absolute North-up movement
                ushort angle = 0;
                if (dx == 1) angle = 0;
                else if (dx == -1) angle = 2048;
                else if (dy == 1) angle = 1024;
                else if (dy == -1) angle = 3072;
                Move(dx, dy, angle);
            }
        }

        private DateTime nextMoveAt = DateTime.MinValue;
        private DateTime nextReqGoAt = DateTime.MinValue;

        private void Move(int dx, int dy, ushort angle)
        {
            if (DateTime.Now < nextMoveAt) return;
            nextMoveAt = DateTime.Now.AddMilliseconds(100);
            var avatar = Data.AvatarObject;
            if (avatar == null) { Log("DBG", "Move: avatar null"); return; }
            Log("DBG", $"Move({dx},{dy}) X:{avatar.CoordinateX} Y:{avatar.CoordinateY} room:{CurrentRoom != null}");
            if (avatar.AngleUnits != angle)
            {
                avatar.AngleUnits = angle;
                SendReqTurnMessage(true);
            }
            if (isNoClip)
            {
                ushort origX = avatar.CoordinateX;
                ushort origY = avatar.CoordinateY;
                avatar.CoordinateX = (ushort)Math.Clamp((int)origX + dx * 16, 0, 65535);
                avatar.CoordinateY = (ushort)Math.Clamp((int)origY + dy * 16, 0, 65535);
                byte origSpeed = (byte)avatar.HorizontalSpeed;
                avatar.HorizontalSpeed = 16;
                SendReqMoveMessage(true);
                avatar.CoordinateX = origX;
                avatar.CoordinateY = origY;
                avatar.HorizontalSpeed = origSpeed;
            }
            else
            {
                var direction = new V2(dx, dy);
                if (direction.LengthSquared > 0.001f) direction.Normalize();
                float remaining = 16.0f;
                float stepSize = 4.0f;
                bool movedAtAll = false;
                while (remaining > 0.01f)
                {
                    float dist = Math.Min(remaining, stepSize);
                    if (DoFineStep(direction, dist))
                    {
                        remaining -= dist;
                        movedAtAll = true;
                        // restore step size after a successful sub-step
                        if (stepSize < 4.0f) stepSize = Math.Min(stepSize * 2.0f, 4.0f);
                    }
                    else
                    {
                        // Halve the step size progressively so we slide up to the wall
                        // boundary smoothly without overshooting.
                        stepSize *= 0.5f;
                        if (stepSize < 0.0625f) break;
                    }
                }
                if (movedAtAll)
                {
                    byte origSpeed = (byte)avatar.HorizontalSpeed;
                    avatar.HorizontalSpeed = 16;
                    SendReqMoveMessage(true);
                    avatar.HorizontalSpeed = origSpeed;
                }
                // When fully blocked, do NOT send ReqGo — that is an explicit door
                // action triggered by the G key only, not by walking into a wall.
            }
            if (isRecording) recorder.Record("Move", avatar, $"X:{avatar.CoordinateX},Y:{avatar.CoordinateY},A:{angle}");
        }

        public override void SendReqMoveMessage(bool ForceSend) => base.SendReqMoveMessage(ForceSend);

        public override void SendReqActivate()
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) return;
            RoomObject nearestObj = null;
            double minDistObj = 128.0; 
            foreach (var obj in Data.RoomObjects)
            {
                if (obj.ID == avatar.ID) continue;
                double dist = Math.Sqrt(Math.Pow(obj.CoordinateX - avatar.CoordinateX, 2) + Math.Pow(obj.CoordinateY - avatar.CoordinateY, 2));
                if (dist < minDistObj) { minDistObj = dist; nearestObj = obj; }
            }
            RooWall nearestWall = null;
            double minDistWall = 64.0; 
            if (CurrentRoom != null)
            {
                var pos2D = new V2(avatar.CoordinateX * 16f - 1024f, avatar.CoordinateY * 16f - 1024f);
                foreach (var wall in CurrentRoom.Walls)
                {
                    int uc;
                    var p1 = wall.P1;
                    var p2 = wall.P2;
                    double d2 = (double)pos2D.MinSquaredDistanceToLineSegment(ref p1, ref p2, out uc);
                    if (d2 < minDistWall * minDistWall) { minDistWall = Math.Sqrt(d2); nearestWall = wall; }
                }
            }
            if (nearestObj != null && minDistObj < minDistWall)
            {
                Log("SYS", $"Activating: {nearestObj.Name} (ID: {nearestObj.ID})");
                base.SendReqActivate(nearestObj.ID);
            }
            else if (nearestWall != null)
            {
                var side = nearestWall.RightSide ?? nearestWall.LeftSide;
                uint targetID = (side != null && side.ServerID != 0) 
                    ? 0x80000000 | (uint)(ushort)side.ServerID 
                    : 0x80000000 | (uint)(ushort)nearestWall.Num;
                ServerConnection.SendQueue.Enqueue(new ReqUseMessage(targetID));
            }
            else base.SendReqActivate();
        }

        public override void SendReqGo(bool SendPositionBefore = true)
        {
            var avatar = Data.AvatarObject;
            if (avatar != null && CurrentRoom != null)
            {
                var pos2D = new V2(avatar.CoordinateX * 16f - 1024f, avatar.CoordinateY * 16f - 1024f);
                RooWall nearestWall = null;
                double minDist2 = 128.0 * 128.0; 
                V2 snapPoint = pos2D;
                foreach (var wall in CurrentRoom.Walls)
                {
                    bool isPassable = (wall.LeftSide != null && wall.LeftSide.Flags.IsPassable) ||
                                      (wall.RightSide != null && wall.RightSide.Flags.IsPassable);
                    if (isPassable)
                    {
                        int uc;
                        var p1 = wall.P1;
                        var p2 = wall.P2;
                        double d2 = (double)pos2D.MinSquaredDistanceToLineSegment(ref p1, ref p2, out uc);
                        if (d2 < minDist2)
                        {
                            minDist2 = d2;
                            nearestWall = wall;
                            var wallVec = p2 - p1;
                            var startToPos = pos2D - p1;
                            snapPoint = p1 + startToPos.GetProjection(ref wallVec);
                        }
                    }
                }
                if (nearestWall != null)
                {
                    snapPoint.ConvertToWorld();
                    avatar.Position3D = new V3(snapPoint.X, avatar.Position3D.Y, snapPoint.Y);
                    avatar.UpdatePosition(10, Data.RoomInformation);
                    SendPositionBefore = true;
                }
            }
            base.SendReqGo(SendPositionBefore);
        }

        public void DrawInputField()
        {
            if (!HasTty) return;
            int h = Console.WindowHeight;
            lock (consoleLock)
            {
                Console.SetCursorPosition(2, h - 2);
                if (inputMode)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(SafeLine("> " + inputBuffer.PadRight(75), 77));
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(SafeLine("  (Press Enter to chat)  ".PadRight(77), 77));
                }
                Console.ResetColor();
            }
        }

        public override void SendActionMessage(ActionType Action)
        {
            base.SendActionMessage(Action);
            if (isRecording && Data.AvatarObject != null)
                recorder.Record("Action", Data.AvatarObject, Action.ToString());
        }

        public override void SendSayGroupMessage(uint TargetID, string Text)
        {
            base.SendSayGroupMessage(TargetID, Text);
            if (isRecording)
                recorder.Record("SaidGroup", Data.AvatarObject, $"{TargetID}:{Text}");
        }
        protected void Dispose(bool disposing)
        {
            if (disposing && HasTty)
            {
                Console.CursorVisible = true;
                Console.ResetColor();
                Console.SetCursorPosition(0, Console.WindowHeight - 1);
                Console.WriteLine();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
