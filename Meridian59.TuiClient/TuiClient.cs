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
        NewsCompose,
        CharSheet
    }

    public enum CharTab { Stats = 0, Skills = 1, Spells = 2, Inventory = 3 }

    public enum CharCreationState
    {
        None,
        AwaitingCharInfo,
        Name,
        Gender,
        Stats,
        Spells,
        Review
    }

    public enum LogTab
    {
        Chat,
        Network
    }

    public class TuiClient : BotClient<GameTick, ResourceManager, DataController, TuiConfig>
    {
        private LogTab activeTab = LogTab.Chat;
        private AsciiRenderer renderer;
        private PathRecorder recorder;
        private bool isRecording = false;
        private string recordingFile = null;
        private bool isNoClip = false;
        private bool isRunning = false;

        // Movement constants matching original clientd3d/move.c / draw3d.h
        // MOVEUNITS = FINENESS >> 2 = 256 CLIENT_FINE = 16 World units
        // MOVE_DELAY = 100ms minimum between steps
        // Walk speed byte = 18, Run = 36 (2x walk) per original RequestMove
        private const float WORLD_STEP_WALK = 0.25f;
        private const float WORLD_STEP_RUN  = 0.5f;
        private const byte  SPEED_WALK      = 18;
        private const byte  SPEED_RUN       = 36;
        private const int   MOVE_DELAY_MS   = 100;
        private DateTime lastMoveTime = DateTime.MinValue;

        private int lastWindowWidth;
        private int lastWindowHeight;
        private uint lastRoomID = 0;

        // UI State
        private PopupMode activePopup = PopupMode.None;
        private CharCreationState charCreationState = CharCreationState.None;
        private int popupSelectedIndex = 0;
        private int popupScrollOffset = 0;
        private object currentPopupItem = null;
        private bool popupJustClosed = false;
        private CharTab charTab = CharTab.Stats;
        private int charScrollOffset = 0;
        private int charSelectionIndex = 0;

        // Combat state
        private bool autoAttack = false;
        private int lastAvatarHP = -1;

        // Composition State
        private string composeRecipient = "";
        private string composeSubject = "";
        private List<string> composeBody = new List<string> { "" };
        private int composeState = 0; // 0=Recipient, 1=Subject, 2=Body

        // Concurrency
        private readonly object consoleLock = new object();
        private readonly object logLock = new object();

        // BotClient implementation
        public override int LogLineWidth => LEFT_PANEL_WIDTH;
        public override int DynamicFirstLogRow => LOG_FIRST_ROW;
        public override int DynamicLastLogRow => Console.WindowHeight - 5;
        public override void DrawBoxes() { }
        public override void DrawResting() { }

        // Scrollback ringbuffers
        private const int LOG_CAPACITY = 500;
        private readonly List<LogEntry> logBuffer = new List<LogEntry>(LOG_CAPACITY + 1);
        private readonly List<LogEntry> netBuffer = new List<LogEntry>(LOG_CAPACITY + 1);
        private int scrollOffset = 0;   // 0 = newest at bottom, positive = scrolled back
        private int netScrollOffset = 0;

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
        public string ScriptFile { get; set; } = null;

        // Layout constants
        private const int LOG_FIRST_ROW = 5;  // first row of the log area (row 0-4 are stats/borders)
        private const int LEFT_PANEL_WIDTH = 78; // printable cols inside left panel (cols 1..78)

        public TuiClient() : base()
        {
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

        protected override void HandleLoginModeMessage(LoginModeMessage Message)
        {
            Log("SYS", Message.Description);
            base.HandleLoginModeMessage(Message);
        }

        protected override void HandleLoginModeMessageMessage(LoginModeMessageMessage Message)
        {
            Log("SYS", Message.Message);
        }
        #endregion

        private bool HasTty => string.IsNullOrEmpty(ScriptFile) && Console.WindowWidth > 0 && Console.WindowHeight > 0;

        // Row just above the input separator — last writable log row is LogBottom-1.
        private int LogBottom => Console.WindowHeight - 4;

        public override void Init()
        {
            base.Init();
            renderer = new AsciiRenderer();
            recorder = new PathRecorder(this);

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
                ServerConnection.Disconnect("Auto-quit timeout");
                IsRunning = false;
                return;
            }

            // Record game mode entry
            if (Data.UIMode == UIMode.Playing && gameModeSince == DateTime.MinValue)
            {
                gameModeSince = DateTime.Now;
            }

            // Combat tick — runs regardless of TTY
            if (autoAttack)
            {
                var attackTarget = Data.TargetObject as RoomObject;
                if (attackTarget != null)
                {
                    FaceTarget(attackTarget);
                    SendReqAttackMessage(attackTarget);
                }

                // Auto-target nearest hostile when we take damage with no target
                int currentHP = (int)(Data.AvatarCondition.GetItemByNum(1)?.ValueCurrent ?? -1);
                if (lastAvatarHP >= 0 && currentHP >= 0 && currentHP < lastAvatarHP && Data.TargetObject == null)
                    AutoTargetNearest();
                if (currentHP >= 0) lastAvatarHP = currentHP;
            }

            else
            {
                // NPC context targeting: auto-select the nearest NPC in front of the avatar
                // (only when not locked onto a live hostile combat target)
                UpdateNPCTarget();
                lastAvatarHP = (int)(Data.AvatarCondition.GetItemByNum(1)?.ValueCurrent ?? -1);
            }

            // UI rendering only when TTY is available
            if (!HasTty) return;

            lock (consoleLock)
            {
                bool layoutChanged = false;
                if (Console.WindowWidth != lastWindowWidth || Console.WindowHeight != lastWindowHeight)
                {
                    lastWindowWidth = Console.WindowWidth;
                    lastWindowHeight = Console.WindowHeight;
                    renderer.Invalidate();
                    layoutChanged = true;
                }
                
                var ri = Data.RoomInformation;
                if (ri != null && ri.RoomID != lastRoomID)
                {
                    lastRoomID = ri.RoomID;
                    layoutChanged = true;
                }

                if (layoutChanged)
                {
                    renderer.Invalidate();
                    Console.Clear();
                    DrawTuiLayout();
                }
                DrawStats();
                DrawRoomInfo();
                DrawMap();
                DrawInputField();
            }
        }

        protected override void ProcessQueues()
        {
            Exception error;
            GameMessage message;

            // Handle all exceptions from networkclient
            while (ServerConnection.ExceptionQueue.TryDequeue(out error))          
                OnServerConnectionException(error);

            // Handle the outgoing MessageLog (debug for sent packets)
            while (ServerConnection.OutgoingPacketLog.TryDequeue(out message))
            {
                double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double ts = (double)message.SendRecvTimestamp / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double latency = now - ts;

                string name = GetMessageName(message.PI);
                
                // Exclude noisy types from metrics gathering
                if (message.PI != (byte)MessageTypeGameMode.ReqMove && 
                    message.PI != (byte)MessageTypeGameMode.ReqTurn &&
                    message.PI != (byte)MessageTypeGameMode.SendPlayer)
                {
                    Metrics.Record($"Sent:{name}", latency);
                }

                if (metricsWriter != null)
                {
                    metricsWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Sent:{name,-20} Latency:{latency,8:F2}ms");
                    metricsWriter.Flush();
                }

                Data.LogOutgoingPacket(message); 
            }
               
            // Handle all pending incoming messages done from enrichment
            while (MessageEnrichment.OutputQueue.TryDequeue(out message))
            {
                double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double ts = (double)message.SendRecvTimestamp / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double latency = now - ts;

                string name = GetMessageName(message.PI);

                // Exclude noisy types from metrics gathering
                if (message.PI != (byte)MessageTypeGameMode.Player && 
                    message.PI != (byte)MessageTypeGameMode.Stat &&
                    message.PI != (byte)MessageTypeGameMode.LightShading)
                {
                    Metrics.Record($"Recv:{name}", latency);
                }

                if (metricsWriter != null)
                {
                    metricsWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Recv:{name,-20} Latency:{latency,8:F2}ms");
                    metricsWriter.Flush();
                }

                HandleGameMessage(message);       
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
            if (!string.IsNullOrEmpty(ScriptFile))
            {
                LoadScript(ScriptFile);
            }
        }

        private void LoadScript(string path)
        {
            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                int enqueued = 0;
                foreach (string line in lines)
                {
                    string cmd = line.Trim();
                    if (string.IsNullOrEmpty(cmd) || cmd.StartsWith("//") || cmd.StartsWith("#"))
                        continue;
                    scriptQueue.Enqueue(cmd);
                    enqueued++;
                }
                Log("SYS", $"Enqueued {enqueued} commands from {path}");
            }
            else
            {
                Log("ERROR", $"Script file not found: {path}");
            }
        }

        private void LoadAutoexec()
        {
            string path = "autoexec.script";
            if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "autoexec.script");
            if (!File.Exists(path)) path = "bin/autoexec.script";

            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines)
                {
                    string cmd = line.Trim();
                    if (string.IsNullOrEmpty(cmd) || cmd.StartsWith("//") || cmd.StartsWith("#"))
                        continue;
                    scriptQueue.Enqueue(cmd);
                }
                Log("SYS", $"Enqueued {scriptQueue.Count} commands from autoexec.script");
            }
        }

        public override void SendUserCommandRest()
        {
            Log("SYS", "Sending REST command...");
            var command = new UserCommandRest();
            ServerConnection.SendQueue.Enqueue(new UserCommandMessage(command, null));
            Data.IsResting = true;
        }

        public override void SendUserCommandStand()
        {
            Log("SYS", "Sending STAND command...");
            var command = new UserCommandStand();
            ServerConnection.SendQueue.Enqueue(new UserCommandMessage(command, null));
            Data.IsResting = false;
        }

        private void ProcessScriptQueue()
        {
            if (scriptQueue.Count == 0 || DateTime.Now < nextScriptCommandTime)
                return;

            string cmd = scriptQueue.Dequeue();
            Log("SYS", $"Executing script command: {cmd}");
            
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
            if (Data.UIMode == UIMode.Playing)
            {
                // Log("DEBUG", "Received character list while already playing. Likely protocol desync. Skipping.");
                return;
            }

            // Custom selection logic
            string targetChar = Config.SelectedConnectionInfo?.Character;
            if (!string.IsNullOrEmpty(targetChar))
            {
                var found = Message.WelcomeInfo.Characters.FirstOrDefault(c => c.Name.Equals(targetChar, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    Data.ExpectedAvatarName = found.Name;
                    SendUseCharacterMessage(new ObjectID(found.ID), true, found.Name);
                    return;
                }
            }
            
                if (!string.IsNullOrEmpty(ScriptFile) && Message.WelcomeInfo.Characters.Count > 0)
                {
                    var fallback = Message.WelcomeInfo.Characters[0];
                    Data.ExpectedAvatarName = fallback.Name;
                    SendUseCharacterMessage(new ObjectID(fallback.ID), true, fallback.Name);
                    return;
                }
        }

        private uint lastLoggedRoomId = 0;
        protected override void HandlePlayerMessage(PlayerMessage Message)
        {
            renderer.Invalidate();

            // Data layer was already updated by BaseClient.HandleGameModeMessage before routing here.
            // Calling Data.HandleGameModeMessage again would double-invoke HandlePlayer (clears room objects twice).

            if (Message.RoomInfo.RoomID != lastLoggedRoomId)
            {
                Log("SYS", $"Entered room: {Message.RoomInfo.RoomName} (ID: {Message.RoomInfo.RoomID}, File: {Message.RoomInfo.RoomFile})");
                lastLoggedRoomId = Message.RoomInfo.RoomID;
            }

            // Resolve ROO resources on main thread if needed (base class logic)
            RooFile rooFile = Message.RoomInfo.ResourceRoom;
            if (rooFile != null && rooFile.IsResourcesResolved)
            {
                rooFile.Reset();
                rooFile.ResolveResources(ResourceManager);
                rooFile.UncompressAll();
            }

            // Reset position tracking so the next move is sent unconditionally.
            // Do NOT send ReqMove(0,0) here: the 900 server auto-delivers room contents on login
            // and treating (0,0) as a real position rubber-bands the character to the room boundary.
            lastSentPositionX = ushort.MaxValue;
            lastSentPositionY = ushort.MaxValue;
            lastSentSector = null;

            Util.ForceMaximumGC();
        }

        protected override void HandleCharInfoOKMessage(Meridian59.Protocol.GameMessages.CharInfoOkMessage Message)
        {
            Log("SYS", "Character creation successful! Logging in...");
            base.HandleCharInfoOKMessage(Message);
        }

        protected override void HandleGameModeMessage(GameModeMessage Message)
        {
            var pi = (MessageTypeGameMode)Message.PI;

            // Only log important stuff to UI to prevent spam
            if (pi == MessageTypeGameMode.Message && Message is MessageMessage pMsg)
            {
                if (pMsg.Message != null)
                {
                    Log("SYS", pMsg.Message.FullString);
                }
            }
            else if (pi == MessageTypeGameMode.Move && Message is MoveMessage m)
            {
                // Suppress movement log to avoid console pollution
            }
            else if (pi == MessageTypeGameMode.Characters && Message is CharactersMessage chars)
            {
                // auto-selection handled in HandleCharactersMessage override
            }
            else if (pi == MessageTypeGameMode.LightAmbient || pi == MessageTypeGameMode.LightPlayer || pi == MessageTypeGameMode.StatGroup || pi == MessageTypeGameMode.LightShading)
            {
                // Silently ignore these for UI log
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
                string hints = " [Enter]Chat [WASD]Move [C]harSheet [F]ight [T]arget [R]un [+/-]Zoom [Q]uit";
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
                // Bars (starts col 2)
                Console.SetCursorPosition(2, 1);
                Console.Write("HP: " + StatStr(Data.AvatarCondition, 1).PadRight(7));

                Console.SetCursorPosition(2, 2);
                Console.Write("MP: " + StatStr(Data.AvatarCondition, 2).PadRight(7));

                Console.SetCursorPosition(2, 3);
                Console.Write("VG: " + StatStr(Data.AvatarCondition, 3).PadRight(7));

                uint cash = 0;
                var shilling = Data.InventoryObjects.GetItemByName("shilling", false);
                if (shilling != null) cash = (uint)shilling.Count;

                // Sub-stats (starts col 17, 26, 32)
                Console.SetCursorPosition(17, 3);
                Console.Write($"RTT:{ServerConnection.RTT,-3}");

                Console.SetCursorPosition(26, 3);
                Console.Write($"RST:{(Data.IsResting ? "Y" : "N"),-1}");

                Console.SetCursorPosition(32, 3);
                Console.Write($"$:{cash,-5}");

                // Combat status (row 2, cols 17-57)
                string tgtName = Data.TargetObject?.Name ?? "";
                string atkStr = autoAttack ? "[ATK:ON ] " : "[ATK:OFF] ";
                string combatLine = tgtName.Length > 0
                    ? atkStr + "TGT:" + (tgtName.Length > 27 ? tgtName[..24] + "..." : tgtName)
                    : atkStr;
                Console.SetCursorPosition(17, 2);
                Console.ForegroundColor = autoAttack ? ConsoleColor.Red : ConsoleColor.DarkGray;
                Console.Write(combatLine.PadRight(41));
                Console.ResetColor();
            }
        }

        public void DrawRoomInfo()
        {
            if (!HasTty) return;
            var ri = Data.RoomInformation;
            var avatar = Data.AvatarObject;

            lock (consoleLock)
            {
                // Room Name: fills cols 17-57 (between the ╦ borders at 15 and 59)
                Console.SetCursorPosition(17, 1);
                Console.ForegroundColor = ConsoleColor.White;
                string roomName = ri?.RoomName ?? "Unknown Room";
                if (roomName.Length > 41) roomName = roomName[..38] + "...";
                Console.Write($"{roomName,-41}");

                // Clear right section cols 60-78 (formerly held Tile overflow)
                Console.SetCursorPosition(60, 1);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(new string(' ', 19));

                Console.ResetColor();
            }
        }

        // ── Log ──────────────────────────────────────────────────────────────────

        public void DrawLog()
        {
            if (!HasTty) return;

            int logWidth = 78;
            int logHeight = LogBottom - LOG_FIRST_ROW;

            var buffer = activeTab == LogTab.Network ? netBuffer : logBuffer;
            var offset = activeTab == LogTab.Network ? netScrollOffset : scrollOffset;

            lock (consoleLock)
            {
                // Draw tab header
                Console.SetCursorPosition(1, LOG_FIRST_ROW - 1);
                Console.ForegroundColor = activeTab == LogTab.Chat ? ConsoleColor.White : ConsoleColor.DarkGray;
                Console.Write(" [CHAT] ");
                Console.ForegroundColor = activeTab == LogTab.Network ? ConsoleColor.White : ConsoleColor.DarkGray;
                Console.Write(" [NET] ");
                Console.ResetColor();

                lock (logLock)
                {
                    for (int i = 0; i < logHeight; i++)
                    {
                        int bufferIdx = buffer.Count - 1 - offset - (logHeight - 1 - i);
                        Console.SetCursorPosition(1, LOG_FIRST_ROW + i);

                        if (bufferIdx >= 0 && bufferIdx < buffer.Count)
                        {
                            var entry = buffer[bufferIdx];
                            // hh:mm tt (8 chars) + space = 9 chars
                            string timeStr = entry.Timestamp.ToString("hh:mm:ss");
                            Console.ForegroundColor = ConsoleColor.Gray;
                            Console.Write($"{timeStr,-9}");

                            switch (entry.Type)
                            {
                                case "CHAT":  Console.ForegroundColor = ConsoleColor.Green; break;
                                case "SYS":   Console.ForegroundColor = ConsoleColor.Cyan; break;
                                case "NET":   Console.ForegroundColor = ConsoleColor.DarkGray; break;
                                case "RECV":  Console.ForegroundColor = ConsoleColor.Yellow; break;
                                case "ERROR": Console.ForegroundColor = ConsoleColor.Red; break;
                                case "DEBUG": Console.ForegroundColor = ConsoleColor.DarkMagenta; break;
                                default:      Console.ForegroundColor = ConsoleColor.White; break;
                            }
                            // Type (8 chars) + space = 9 chars
                            Console.Write($"{entry.Type,-9}");
                            Console.ResetColor();

                            // Content (max 60 chars)
                            string content = entry.Text;
                            const int avail = 60; 
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
            base.Log(Type, Text);

            if (Type == "DEBUG" && HasTty) return;

            // Total line = 9 (time) + 9 (type) + 60 (content) = 78. Fits perfectly in left panel.
            const int avail = 60;
            string sanitized = Text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

            var buffer = (Type == "RECV" || Type == "SEND") ? netBuffer : logBuffer;

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
                    if (!string.IsNullOrWhiteSpace(chunk))
                        buffer.Add(new LogEntry(Type, chunk.TrimEnd()));
                }

                if (buffer.Count > LOG_CAPACITY)
                    buffer.RemoveAt(0);
            }

            if (HasTty)
            {
                bool shouldRedraw = false;
                if (activeTab == LogTab.Chat && buffer == logBuffer && scrollOffset == 0) shouldRedraw = true;
                if (activeTab == LogTab.Network && buffer == netBuffer && netScrollOffset == 0) shouldRedraw = true;
                
                if (shouldRedraw) DrawLog();
            }
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
                // Ensure the panel separator at col 79, row 1 is intact before we write MapStatus next to it
                Console.SetCursorPosition(79, 1);
                Console.Write("║ ║");

                // Draw Map Header: MapStatus on the left, X/Y/Tile right-aligned
                Console.SetCursorPosition(startX, 1);
                var mapAvatar = Data.AvatarObject;
                string coordStr = "";
                if (mapAvatar != null)
                {
                    float tileCol = (float)(mapAvatar.Position3D.X - 64.0);
                    float tileRow = (float)(mapAvatar.Position3D.Z - 64.0);
                    coordStr = $" X:{mapAvatar.CoordinateX,4} Y:{mapAvatar.CoordinateY,4}  Tile:{tileCol,4:F1},{tileRow,4:F1}";
                }
                int availW = width - 1;
                string mapStatus = renderer.MapStatus;
                string headerLine;
                if (coordStr.Length == 0 || availW <= coordStr.Length)
                {
                    headerLine = mapStatus.Length > availW ? mapStatus[..availW] : mapStatus.PadRight(availW);
                }
                else
                {
                    int leftW = availW - coordStr.Length;
                    string leftPart = mapStatus.Length > leftW ? mapStatus[..leftW] : mapStatus.PadRight(leftW);
                    headerLine = leftPart + coordStr;
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(headerLine);
                Console.ResetColor();

                if (activePopup != PopupMode.None)
                {
                    DrawPopup(startX, startY, width, height);
                }
                else
                {
                    if (popupJustClosed)
                    {
                        // Physically blank the entire inner map area so the differential
                        // renderer doesn't leave popup text in cells it thinks are already ' '
                        Console.BackgroundColor = ConsoleColor.Black;
                        Console.ForegroundColor = ConsoleColor.Gray;
                        string blank = new string(' ', width - 2);
                        for (int row = startY + 1; row < startY + height - 1; row++)
                        {
                            Console.SetCursorPosition(startX + 1, row);
                            Console.Write(blank);
                        }
                        renderer.Invalidate();
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

        // ── Combat ───────────────────────────────────────────────────────────────

        private static float AngleDiff(float a, float b)
        {
            float d = a - b;
            while (d >  MathF.PI) d -= 2 * MathF.PI;
            while (d < -MathF.PI) d += 2 * MathF.PI;
            return MathF.Abs(d);
        }

        private void UpdateNPCTarget()
        {
            // Don't override a live hostile combat target
            var current = Data.TargetObject as RoomObject;
            if (current != null && (current.Flags.IsCreature || current.Flags.IsAttackable))
                return;

            var avatar = Data.AvatarObject;
            if (avatar == null) return;

            // Avatar facing in radians (M59: 0=E,1024=S,2048=W,3072=N maps 1:1 to atan2 scale)
            float facingRad = avatar.AngleUnits / 4096f * 2 * MathF.PI;
            const float VIEW_ARC  = MathF.PI / 3f;  // ±60° forward cone
            const float MAX_DIST  = 384f;            // ~6 tiles in KOD units (64/tile)

            RoomObject nearest = null;
            float minDist = MAX_DIST;

            foreach (var obj in Data.RoomObjects.ToList())
            {
                if (!obj.Flags.IsNPC) continue;
                float dx = obj.CoordinateX - avatar.CoordinateX;
                float dz = obj.CoordinateY - avatar.CoordinateY;
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist > MAX_DIST) continue;
                float angleToObj = MathF.Atan2(dz, dx);
                if (AngleDiff(angleToObj, facingRad) > VIEW_ARC) continue;
                if (dist < minDist) { minDist = dist; nearest = obj; }
            }

            uint newID  = nearest?.ID ?? uint.MaxValue;
            uint currID = current?.ID ?? uint.MaxValue;
            if (newID != currID)
            {
                Data.TargetID = newID;
                DrawStats();
            }
        }

        private void FaceTarget(RoomObject target)
        {
            var avatar = Data.AvatarObject;
            if (avatar == null || target == null) return;
            float dx = target.CoordinateX - avatar.CoordinateX;
            float dz = target.CoordinateY - avatar.CoordinateY;
            // M59 angle: 0=E, 1024=S, 2048=W, 3072=N (4096 units/circle)
            ushort angle = (ushort)(((float)Math.Atan2(dz, dx) / (2 * MathF.PI) * 4096 + 4096) % 4096);
            if (avatar.AngleUnits != angle)
            {
                avatar.AngleUnits = angle;
                SendReqTurnMessage(true);
            }
        }

        private void AutoTargetNearest()
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) return;
            RoomObject nearest = null;
            float minDist = float.MaxValue;
            foreach (var obj in Data.RoomObjects.ToList())
            {
                if (obj.IsAvatar) continue;
                if (!obj.Flags.IsCreature && !obj.Flags.IsAttackable) continue;
                float dx = obj.CoordinateX - avatar.CoordinateX;
                float dz = obj.CoordinateY - avatar.CoordinateY;
                float d = dx * dx + dz * dz;
                if (d < minDist) { minDist = d; nearest = obj; }
            }
            if (nearest != null)
            {
                Data.TargetID = nearest.ID;
                FaceTarget(nearest);
                Log("SYS", $"Auto-targeted: {nearest.Name}");
                DrawStats();
            }
        }

        // ── Character Sheet ───────────────────────────────────────────────────────

        private void RenderScrollableRows(List<(string text, ConsoleColor color)> rows,
            int contentX, int contentY, int contentW, int contentH)
        {
            charScrollOffset = Math.Max(0, Math.Min(charScrollOffset, Math.Max(0, rows.Count - contentH)));
            for (int i = 0; i < contentH; i++)
            {
                int idx = i + charScrollOffset;
                Console.SetCursorPosition(contentX, contentY + i);
                if (idx < rows.Count)
                {
                    Console.ForegroundColor = rows[idx].color;
                    Console.Write(SafeLine(rows[idx].text.PadRight(contentW), contentW));
                    Console.ResetColor();
                }
                else
                {
                    Console.Write(new string(' ', contentW));
                }
            }
        }

        private void DrawCharStats(int contentX, int contentY, int contentW, int contentH)
        {
            var rows = new List<(string, ConsoleColor)>();
            foreach (var s in Data.AvatarAttributes.ToList())
                rows.Add(($"{s.ResourceName,-24} {s.ValueCurrent,5}", ConsoleColor.Gray));
            if (rows.Count == 0)
                rows.Add(("  (No data yet)", ConsoleColor.DarkGray));
            RenderScrollableRows(rows, contentX, contentY, contentW, contentH);
        }

        private void DrawCharSkills(int contentX, int contentY, int contentW, int contentH)
        {
            var rows = new List<(string, ConsoleColor)>();
            int lastGroup = -1;
            int groupNum = 0;
            foreach (var s in Data.AvatarSkills.ToList().OrderBy(x => x.Num))
            {
                int group = s.Num / 20;
                if (group != lastGroup)
                {
                    groupNum++;
                    string sep = $"─── Level {groupNum} " + new string('─', Math.Max(0, contentW - 12));
                    rows.Add((sep, ConsoleColor.DarkGray));
                    lastGroup = group;
                }
                rows.Add(($"  {s.ResourceName,-26} {s.SkillPoints,2}%", ConsoleColor.Gray));
            }
            if (rows.Count == 0)
                rows.Add(("  (No skills)", ConsoleColor.DarkGray));
            RenderScrollableRows(rows, contentX, contentY, contentW, contentH);
        }

        private void DrawCharSpells(int contentX, int contentY, int contentW, int contentH)
        {
            var schoolOrder = new[] {
                SchoolType.Shalille, SchoolType.Qor, SchoolType.Kraanan,
                SchoolType.Faren, SchoolType.Riija, SchoolType.Jala, SchoolType.WeaponCraft
            };
            var spellLookup = new Dictionary<uint, SchoolType>();
            foreach (var so in Data.SpellObjects.ToList())
                spellLookup[so.ID] = so.SchoolType;

            var spells = Data.AvatarSpells.ToList()
                .Select(s => (spell: s, school: spellLookup.TryGetValue(s.ObjectID, out var sc) ? sc : (SchoolType)0))
                .OrderBy(x => { int i = Array.IndexOf(schoolOrder, x.school); return i < 0 ? 99 : i; })
                .ThenBy(x => x.spell.ResourceName)
                .ToList();

            charSelectionIndex = Math.Clamp(charSelectionIndex, 0, Math.Max(0, spells.Count - 1));

            // Build display rows, recording which display row each spell lands on
            var rows = new List<(string text, ConsoleColor color)>();
            var spellDisplayRow = new List<int>(); // display row index for each spell
            SchoolType lastSchool = (SchoolType)255;
            foreach (var (spell, school) in spells)
            {
                if (school != lastSchool)
                {
                    string sn = school == 0 ? "Unknown" : school.ToString();
                    rows.Add(($"─── {sn} " + new string('─', Math.Max(0, contentW - sn.Length - 5)), ConsoleColor.DarkCyan));
                    lastSchool = school;
                }
                spellDisplayRow.Add(rows.Count);
                rows.Add(($"  ✓ {spell.ResourceName}", ConsoleColor.Gray));
            }
            if (rows.Count == 0) { rows.Add(("  (No spells)", ConsoleColor.DarkGray)); RenderScrollableRows(rows, contentX, contentY, contentW, contentH); return; }

            int selectedDisplay = spells.Count > 0 ? spellDisplayRow[charSelectionIndex] : -1;

            // Scroll to keep selection visible
            if (selectedDisplay >= 0)
            {
                if (selectedDisplay < charScrollOffset) charScrollOffset = selectedDisplay;
                else if (selectedDisplay >= charScrollOffset + contentH) charScrollOffset = selectedDisplay - contentH + 1;
            }
            charScrollOffset = Math.Clamp(charScrollOffset, 0, Math.Max(0, rows.Count - contentH));

            for (int i = 0; i < contentH; i++)
            {
                int idx = i + charScrollOffset;
                Console.SetCursorPosition(contentX, contentY + i);
                if (idx < rows.Count)
                {
                    bool sel = idx == selectedDisplay;
                    Console.ForegroundColor = sel ? ConsoleColor.Black : rows[idx].color;
                    Console.BackgroundColor = sel ? ConsoleColor.Gray  : ConsoleColor.Black;
                    Console.Write(SafeLine(rows[idx].text.PadRight(contentW), contentW));
                    Console.ResetColor();
                }
                else Console.Write(new string(' ', contentW));
            }
        }

        private void DrawCharInventory(int contentX, int contentY, int contentW, int contentH)
        {
            var items = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
            charSelectionIndex = Math.Clamp(charSelectionIndex, 0, Math.Max(0, items.Count - 1));

            var rows = new List<(string text, ConsoleColor color)>();
            foreach (var obj in items)
            {
                bool equipped = obj.Flags.IsEquipped;
                string suffix = equipped ? " [E]" : "";
                string line = obj.Count > 0
                    ? $"{obj.Name,-28} x{obj.Count,4}{suffix}"
                    : $"{obj.Name}{suffix}";
                rows.Add((line, equipped ? ConsoleColor.Yellow : ConsoleColor.Gray));
            }
            if (rows.Count == 0) { rows.Add(("  (Empty)", ConsoleColor.DarkGray)); RenderScrollableRows(rows, contentX, contentY, contentW, contentH); return; }

            // Scroll to keep selection visible
            if (charSelectionIndex < charScrollOffset) charScrollOffset = charSelectionIndex;
            else if (charSelectionIndex >= charScrollOffset + contentH) charScrollOffset = charSelectionIndex - contentH + 1;
            charScrollOffset = Math.Clamp(charScrollOffset, 0, Math.Max(0, rows.Count - contentH));

            for (int i = 0; i < contentH; i++)
            {
                int idx = i + charScrollOffset;
                Console.SetCursorPosition(contentX, contentY + i);
                if (idx < rows.Count)
                {
                    bool sel = idx == charSelectionIndex;
                    Console.ForegroundColor = sel ? ConsoleColor.Black : rows[idx].color;
                    Console.BackgroundColor = sel ? ConsoleColor.Gray  : ConsoleColor.Black;
                    Console.Write(SafeLine(rows[idx].text.PadRight(contentW), contentW));
                    Console.ResetColor();
                }
                else Console.Write(new string(' ', contentW));
            }
        }

        private void DrawCharSheet(int startX, int startY, int width, int height)
        {
            int w = Console.WindowWidth;
            if (startX + width > w - 1) width = w - 1 - startX;
            if (width < 20 || height < 8) return;

            // Top border with centred title
            string titleText = " Character ";
            int leftPad  = (width - 2 - titleText.Length) / 2;
            int rightPad = width - 2 - titleText.Length - leftPad;
            Console.SetCursorPosition(startX, startY);
            Console.Write(SafeLine("╔" + new string('═', leftPad) + titleText + new string('═', rightPad) + "╗", width));

            // Side borders for all interior rows
            for (int i = 1; i < height - 1; i++)
            {
                Console.SetCursorPosition(startX, startY + i);
                Console.Write(SafeLine("║" + new string(' ', width - 2) + "║", width));
            }

            // Bottom border with centred footer hint
            string footerText = charTab == CharTab.Spells   ? " [Esc:Close] [←/→:Tab] [↑↓:Select] [Enter:Cast] " :
                                charTab == CharTab.Inventory ? " [Esc:Close] [←/→:Tab] [↑↓:Select] [Enter:Use] " :
                                                               " [Esc:Close] [←/→:Tab] [↑↓:Scroll] ";
            if (footerText.Length > width - 4) footerText = footerText[..(width - 4)];
            int flPad = (width - 2 - footerText.Length) / 2;
            int frPad = width - 2 - footerText.Length - flPad;
            Console.SetCursorPosition(startX, startY + height - 1);
            Console.Write(SafeLine("╚" + new string('═', flPad) + footerText + new string('═', frPad) + "╝", width));

            // Tab bar (row startY+1)
            var tabs = new[] { ("STATS", CharTab.Stats), ("SKILLS", CharTab.Skills),
                               ("SPELLS", CharTab.Spells), ("INV", CharTab.Inventory) };
            int tabX = startX + 1;
            foreach (var (label, tab) in tabs)
            {
                Console.SetCursorPosition(tabX, startY + 1);
                Console.ForegroundColor = tab == charTab ? ConsoleColor.White : ConsoleColor.DarkGray;
                string item = $"[{label}] ";
                Console.Write(item);
                Console.ResetColor();
                tabX += item.Length;
            }
            // Pad rest of tab row
            int remaining = startX + width - 1 - tabX;
            if (remaining > 0) { Console.SetCursorPosition(tabX, startY + 1); Console.Write(new string(' ', remaining)); }

            // Separator under tab bar (row startY+2)
            Console.SetCursorPosition(startX, startY + 2);
            Console.Write(SafeLine("╠" + new string('═', width - 2) + "╣", width));

            // Content area
            int contentX = startX + 2;
            int contentY = startY + 3;
            int contentW = width - 4;
            int contentH = height - 5;
            if (contentW < 5 || contentH < 1) return;

            switch (charTab)
            {
                case CharTab.Stats:     DrawCharStats(contentX, contentY, contentW, contentH);     break;
                case CharTab.Skills:    DrawCharSkills(contentX, contentY, contentW, contentH);    break;
                case CharTab.Spells:    DrawCharSpells(contentX, contentY, contentW, contentH);    break;
                case CharTab.Inventory: DrawCharInventory(contentX, contentY, contentW, contentH); break;
            }
        }

        private void DrawPopup(int startX, int startY, int width, int height)
        {
            if (activePopup == PopupMode.CharSheet)
            {
                DrawCharSheet(startX, startY, width, height);
                return;
            }

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
            if (activePopup == PopupMode.CharSheet)
            {
                int pageSize = Math.Max(1, Console.WindowHeight - 13);
                bool selectable = charTab == CharTab.Spells || charTab == CharTab.Inventory;
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        charScrollOffset = 0;
                        DrawMap();
                        break;
                    case ConsoleKey.LeftArrow:
                        charTab = (CharTab)Math.Max(0, (int)charTab - 1);
                        charScrollOffset = 0;
                        charSelectionIndex = 0;
                        DrawMap();
                        break;
                    case ConsoleKey.RightArrow:
                        charTab = (CharTab)Math.Min(3, (int)charTab + 1);
                        charScrollOffset = 0;
                        charSelectionIndex = 0;
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        if (selectable) charSelectionIndex = Math.Max(0, charSelectionIndex - 1);
                        else charScrollOffset = Math.Max(0, charScrollOffset - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        if (selectable) charSelectionIndex++;
                        else charScrollOffset++;
                        DrawMap();
                        break;
                    case ConsoleKey.PageUp:
                        if (selectable) charSelectionIndex = Math.Max(0, charSelectionIndex - pageSize);
                        else charScrollOffset = Math.Max(0, charScrollOffset - pageSize);
                        DrawMap();
                        break;
                    case ConsoleKey.PageDown:
                        if (selectable) charSelectionIndex += pageSize;
                        else charScrollOffset += pageSize;
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (charTab == CharTab.Spells)
                        {
                            var schoolOrder = new[] {
                                SchoolType.Shalille, SchoolType.Qor, SchoolType.Kraanan,
                                SchoolType.Faren, SchoolType.Riija, SchoolType.Jala, SchoolType.WeaponCraft
                            };
                            var spellLookup = new Dictionary<uint, SchoolType>();
                            foreach (var so in Data.SpellObjects.ToList()) spellLookup[so.ID] = so.SchoolType;
                            var spells = Data.AvatarSpells.ToList()
                                .Select(s => (spell: s, school: spellLookup.TryGetValue(s.ObjectID, out var sc) ? sc : (SchoolType)0))
                                .OrderBy(x => { int i = Array.IndexOf(schoolOrder, x.school); return i < 0 ? 99 : i; })
                                .ThenBy(x => x.spell.ResourceName)
                                .ToList();
                            int si = Math.Clamp(charSelectionIndex, 0, spells.Count - 1);
                            if (spells.Count > 0)
                            {
                                SendReqCastMessage(spells[si].spell.ObjectID);
                                Log("SYS", $"Casting: {spells[si].spell.ResourceName}");
                            }
                        }
                        else if (charTab == CharTab.Inventory)
                        {
                            var items = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
                            int ii = Math.Clamp(charSelectionIndex, 0, items.Count - 1);
                            if (items.Count > 0)
                            {
                                var item = items[ii];
                                if (item.Flags.IsEquipped)
                                {
                                    SendReqUnuseMessage(item.ID);
                                    Log("SYS", $"Unequipping: {item.Name}");
                                }
                                else
                                {
                                    SendReqUseMessage(item.ID);
                                    Log("SYS", $"Using/equipping: {item.Name}");
                                }
                                DrawMap();
                            }
                        }
                        break;
                }
                return;
            }

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
                    ServerConnection.Disconnect("User quit (Hotkey)");
                    IsRunning = false;
                    break;

                case TuiAction.MoveUp:    HandleMovement( 0, -1); break;
                case TuiAction.MoveDown:  HandleMovement( 0,  1); break;
                case TuiAction.MoveLeft:  HandleMovement(-1,  0); break;
                case TuiAction.MoveRight: HandleMovement( 1,  0); break;

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

                case TuiAction.Use:
                    SendReqActivate();
                    break;

                case TuiAction.ManualGo:
                    Log("MOVE", $"Manual ReqGo at X={Data.AvatarObject?.CoordinateX} Y={Data.AvatarObject?.CoordinateY}");
                    SendReqGo(true);
                    break;

                case TuiAction.OpenCharSheet:
                    activePopup = PopupMode.CharSheet;
                    charTab = CharTab.Stats;
                    charScrollOffset = 0;
                    DrawMap();
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

                case TuiAction.ToggleRun:
                    isRunning = !isRunning;
                    Log("SYS", "Run mode: " + (isRunning ? "ON" : "OFF"));
                    break;

                case TuiAction.Refresh:
                    lock (consoleLock)
                    {
                        renderer.Invalidate();
                        Console.Clear();
                        DrawTuiLayout();
                    }
                    break;

                case TuiAction.Hotkey1:
                case TuiAction.Hotkey2:
                case TuiAction.Hotkey3:
                case TuiAction.Hotkey4:
                    // Implement specific hotkey logic if needed, for now just log it
                    Log("SYS", $"Hotkey pressed: {action}");
                    break;

                case TuiAction.ToggleNetTab:
                    activeTab = (activeTab == LogTab.Chat) ? LogTab.Network : LogTab.Chat;
                    DrawLog();
                    break;

                case TuiAction.ToggleAutoAttack:
                    autoAttack = !autoAttack;
                    Log("SYS", "Auto-attack: " + (autoAttack ? "ON" : "OFF"));
                    DrawStats();
                    break;

                case TuiAction.TargetNearest:
                    AutoTargetNearest();
                    break;
            }
        }

        private bool TryParseDirection(string text, out int dx, out int dz)
        {
            string t = text.Trim();
            // Strip optional verb prefix
            if (t.StartsWith("go ",   StringComparison.OrdinalIgnoreCase)) t = t[3..].Trim();
            else if (t.StartsWith("move ", StringComparison.OrdinalIgnoreCase)) t = t[5..].Trim();

            switch (t.ToLowerInvariant())
            {
                case "north":     case "n":  dx =  0; dz = -1; return true;
                case "south":     case "s":  dx =  0; dz =  1; return true;
                case "east":      case "e":  dx =  1; dz =  0; return true;
                case "west":      case "w":  dx = -1; dz =  0; return true;
                case "northeast": case "ne": dx =  1; dz = -1; return true;
                case "southeast": case "se": dx =  1; dz =  1; return true;
                case "southwest": case "sw": dx = -1; dz =  1; return true;
                case "northwest": case "nw": dx = -1; dz = -1; return true;
                default: dx = 0; dz = 0; return false;
            }
        }

        private void ProcessCommand(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (charCreationState != CharCreationState.None)
            {
                HandleCharCreationInput(text);
                return;
            }

            Log("CMD", text);

            if (text.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/logout", StringComparison.OrdinalIgnoreCase))
            {
                Log("SYS", "Disconnecting...");
                ServerConnection.Disconnect("User quit command");
                IsRunning = false;
                return;
            }

            if (text.Equals("w",  StringComparison.OrdinalIgnoreCase)) { HandleMovement( 0, -1); return; }
            if (text.Equals("s",  StringComparison.OrdinalIgnoreCase)) { HandleMovement( 0,  1); return; }
            if (text.Equals("a",  StringComparison.OrdinalIgnoreCase)) { HandleMovement(-1,  0); return; }
            if (text.Equals("d",  StringComparison.OrdinalIgnoreCase)) { HandleMovement( 1,  0); return; }
            if (text.Equals("g",  StringComparison.OrdinalIgnoreCase)) { PerformAction(TuiAction.ManualGo); return; }
            if (text.Equals("u",  StringComparison.OrdinalIgnoreCase)) { PerformAction(TuiAction.Use); return; }

            // Cardinal direction movement (for scripts: "north", "go north", "move north", etc.)
            if (TryParseDirection(text, out int cdx, out int cdz)) { HandleMovement(cdx, cdz); return; }

            if (text.StartsWith("/createchar", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out int slotIdx) || slotIdx < 0 || slotIdx > 3)
                {
                    Log("ERROR", "Usage: /createchar <slot_index (0-3)>");
                    return;
                }

                if (Data.WelcomeInfo == null || Data.WelcomeInfo.Characters == null || slotIdx >= Data.WelcomeInfo.Characters.Count)
                {
                    Log("ERROR", "Character list not loaded or invalid slot index.");
                    return;
                }

                var character = Data.WelcomeInfo.Characters[slotIdx];
                Log("SYS", $"Initiating character creation on slot {slotIdx} (ID: {character.ID})...");
                charCreationState = CharCreationState.AwaitingCharInfo;
                inputMode = true; // Ensure we capture the next inputs
                inputBuffer = "";
                SendSystemMessageSendCharInfo(character.ID);
                return;
            }

            if (text.StartsWith("/use", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out int slotIdx) || slotIdx < 0 || slotIdx > 3)
                {
                    Log("ERROR", "Usage: /use <slot_index (0-3)>");
                    return;
                }

                if (Data.WelcomeInfo == null || Data.WelcomeInfo.Characters == null || slotIdx >= Data.WelcomeInfo.Characters.Count)
                {
                    Log("ERROR", "Character list not loaded or invalid slot index.");
                    return;
                }

                var character = Data.WelcomeInfo.Characters[slotIdx];
                Log("SYS", $"Selecting character in slot {slotIdx} (ID: {character.ID})...");
                Data.ExpectedAvatarName = character.Name;
                SendUseCharacterMessage(new ObjectID(character.ID), true, character.Name);
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

            if (text.Equals("use",      StringComparison.OrdinalIgnoreCase) ||
                text.Equals("opendoor", StringComparison.OrdinalIgnoreCase))
            {
                SendReqActivate();
                return;
            }

            if (text.Equals("go", StringComparison.OrdinalIgnoreCase))
            {
                SendReqGo(true);
                return;
            }

            if (text.Equals("noclip", StringComparison.OrdinalIgnoreCase))
            {
                isNoClip = !isNoClip;
                Log("SYS", "Noclip: " + (isNoClip ? "ON" : "OFF"));
                return;
            }

            if (text.StartsWith("move ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("tile ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && float.TryParse(parts[1], out float tx) && float.TryParse(parts[2], out float ty))
                {
                    var av = Data.AvatarObject;
                    if (av != null)
                    {
                        // Set Kod units: Tile * 64 + 64
                        var newPos = av.Position3D;
                        newPos.X = (tx * 64.0f) + 64.0f;
                        newPos.Z = (ty * 64.0f) + 64.0f;
                        av.Position3D = newPos;
                        SendReqMoveMessage(true);
                        Log("SYS", $"Moved to tile {tx}, {ty} (Kod:{newPos.X},{newPos.Z})");
                    }
                }
                return;
            }

            if (text.StartsWith("raw ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && float.TryParse(parts[0], out float rx) && float.TryParse(parts[1], out float ry))
                {
                    var av = Data.AvatarObject;
                    if (av != null)
                    {
                        // Set Kod units via property (converts to ROO)
                        av.CoordinateX = (ushort)rx;
                        av.CoordinateY = (ushort)ry;
                        SendReqMoveMessage(true);
                        Log("SYS", $"Moved to Kod {rx}, {ry} (ROO:{av.Position3D.X},{av.Position3D.Z})");
                    }
                }
                return;
            }

            if (text.Equals("/dumpobjects", StringComparison.OrdinalIgnoreCase))
            {
                var objs = Data.RoomObjects;
                Log("SYS", $"Objects in room ({objs.Count}):");
                foreach (var obj in objs)
                {
                    Log("SYS", $"  ID:{obj.ID} Name:{obj.Name} Kod:{obj.CoordinateX},{obj.CoordinateY} P:{obj.Flags.IsPlayer} M:{obj.Flags.IsCreature} A:{obj.Flags.IsAttackable}");
                }
                return;
            }

            if (text.Equals("pos", StringComparison.OrdinalIgnoreCase) || text.Equals("log pos", StringComparison.OrdinalIgnoreCase))
            {
                var av = Data.AvatarObject;
                if (av != null)
                {
                    float tx = ((float)av.Position3D.X - 64.0f) / 64.0f;
                    float ty = ((float)av.Position3D.Z - 64.0f) / 64.0f;
                    Log("SYS", $"Pos: Kod({av.CoordinateX},{av.CoordinateY}) Tile:{tx:F2},{ty:F2}");
                }
                return;
            }

            if (text.Equals("rest", StringComparison.OrdinalIgnoreCase) || text.Equals("/rest", StringComparison.OrdinalIgnoreCase))
            {
                if (isRecording) recorder.Record("Rest", Data.AvatarObject);
                Log("SYS", "Resting...");
                var command = new UserCommandRest();
                ServerConnection.SendQueue.Enqueue(new UserCommandMessage(command, null));
                Data.IsResting = true;
                return;
            }
            if (text.Equals("stand", StringComparison.OrdinalIgnoreCase) || text.Equals("/stand", StringComparison.OrdinalIgnoreCase))
            {
                if (isRecording) recorder.Record("Stand", Data.AvatarObject);
                Log("SYS", "Standing up...");
                var command = new UserCommandStand();
                ServerConnection.SendQueue.Enqueue(new UserCommandMessage(command, null));
                Data.IsResting = false;
                return;
            }

            if (text.Equals("dump room", StringComparison.OrdinalIgnoreCase))
            {
                var ri = Data.RoomInformation;
                if (ri != null && ri.ResourceRoom != null)
                {
                    var roo = ri.ResourceRoom;
                    var box = roo.GetBoundingBox2D(true);
                    Log("SYS", $"Room {ri.RoomID} ({roo.Filename}) Bounds: Min({box.Min.X},{box.Min.Y}) Max({box.Max.X},{box.Max.Y})");
                    Log("SYS", $"Walls: {roo.Walls.Count} Sectors: {roo.Sectors.Count}");
                }
                else Log("ERROR", "No room data available.");
                return;
            }

            if (text.Equals("dump walls", StringComparison.OrdinalIgnoreCase))
            {
                if (CurrentRoom != null)
                {
                    var avatar = Data.AvatarObject;
                    var pos2D = new V2(avatar.CoordinateX * 16f - 1024f, avatar.CoordinateY * 16f - 1024f);
                    Log("SYS", $"Current Pos: ({pos2D.X:F1},{pos2D.Y:F1})");
                    foreach (var wall in CurrentRoom.Walls)
                    {
                        bool isPassable = (wall.LeftSide != null && wall.LeftSide.Flags.IsPassable) ||
                                          (wall.RightSide != null && wall.RightSide.Flags.IsPassable);
                        bool isExit = (wall.LeftSectorNum == 0 || wall.RightSectorNum == 0);
                        if (isPassable || isExit)
                        {
                            int uc;
                            var p1 = wall.P1; var p2 = wall.P2;
                            double d2 = (double)pos2D.MinSquaredDistanceToLineSegment(ref p1, ref p2, out uc);
                            Log("SYS", $"Wall {wall.Num}: ({p1.X},{p1.Y})->({p2.X},{p2.Y}) Dist: {Math.Sqrt(d2):F1} Exit: {isExit}");
                        }
                    }
                }
                return;
            }

            if (text.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                var ri = Data.RoomInformation;
                var av = Data.AvatarObject;
                if (av != null)
                {
                    Log("SYS", $"STATUS: Room={ri?.RoomName} HP={StatStr(Data.AvatarCondition, 1)} MP={StatStr(Data.AvatarCondition, 2)} VIG={StatStr(Data.AvatarCondition, 3)}");
                }
                else Log("SYS", $"STATUS: Room={ri?.RoomName} (Avatar null)");
                return;
            }

            if (text.StartsWith("record ", StringComparison.OrdinalIgnoreCase))
            {
                string filename = text[7..].Trim();
                if (string.IsNullOrEmpty(filename))
                {
                    Log("ERROR", "Usage: record <filename.json>");
                    return;
                }

                if (isRecording)
                {
                    recorder?.Stop();
                    Log("SYS", $"Recording stopped: {recordingFile}");
                }

                recordingFile = filename;
                isRecording = true;
                if (recorder == null) recorder = new PathRecorder(this);
                recorder.Start(recordingFile);
                Log("SYS", $"Recording started: {recordingFile}");
                return;
            }

            if (text.Equals("stop", StringComparison.OrdinalIgnoreCase) && isRecording)
            {
                recorder?.Stop();
                Log("SYS", $"Recording stopped: {recordingFile}");
                isRecording = false;
                recordingFile = null;
                return;
            }

            if (text.StartsWith("cast ", StringComparison.OrdinalIgnoreCase))
            {
                string spellName = text[5..].Trim();
                var spell = Data.AvatarSpells.GetItemByName(spellName, false);
                if (spell != null)
                {
                    Log("SYS", $"Casting: {spell.ResourceName} (No Target)");
                    if (isRecording) recorder.Record("Cast", Data.AvatarObject, spell.ResourceName);
                    SendReqCastMessage(spell.ObjectID);
                }
                else Log("ERROR", $"Unknown spell: {spellName}");
                return;
            }

            if (text.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                string msg = text[4..].Trim();
                if (isRecording) recorder.Record("Say", Data.AvatarObject, msg);
                SendSayToMessage(ChatTransmissionType.Normal, msg);
                return;
            }

            // @ is shorthand for tell: "@PlayerName message"
            if (text.StartsWith("@") && text.Length > 1 && text[1] != ' ')
                text = "tell " + text[1..];

            if (text.StartsWith("tell ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text[5..].Split(new[] { ' ' }, 2);
                if (parts.Length == 2)
                {
                    Log("CHAT", $"You tell {parts[0]}: {parts[1]}");
                    if (isRecording) recorder.Record("Tell", Data.AvatarObject, $"{parts[0]}:{parts[1]}");
                    SendSayGroupMessage(0, parts[1]); 
                }
                return;
            }

            if (isRecording) recorder.Record("Say", Data.AvatarObject, text);
            SendSayToMessage(ChatTransmissionType.Normal, text);
        }

        private void ResetCharCreation()
        {
            charCreationState = CharCreationState.None;
            inputMode = false;
            inputBuffer = "";
            DrawInputField();
        }

        private void HandleCharCreationInput(string text)
        {
            var info = Data.CharCreationInfo;
            switch (charCreationState)
            {
                case CharCreationState.Name:
                    info.AvatarName = text;
                    Log("SYS", $"Name set to: {info.AvatarName}");
                    charCreationState = CharCreationState.Gender;
                    Log("SYS", "Enter gender (M/F):");
                    break;

                case CharCreationState.Gender:
                    if (text.Equals("M", StringComparison.OrdinalIgnoreCase)) { info.SetExampleModel(Gender.Male); }
                    else if (text.Equals("F", StringComparison.OrdinalIgnoreCase)) { info.SetExampleModel(Gender.Female); }
                    else { Log("ERROR", "Invalid gender. Enter M or F:"); return; }
                    Log("SYS", $"Gender set to: {info.Gender}");
                    charCreationState = CharCreationState.Stats;
                    Log("SYS", "Enter stats: Might Intellect Stamina Agility Mysticism Aim");
                    Log("SYS", "(6 numbers, e.g. '30 30 40 30 40 30', sum must be 200)");
                    break;

                case CharCreationState.Stats:
                    string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 6) { Log("ERROR", "Need exactly 6 numbers:"); return; }
                    uint[] s = new uint[6];
                    uint sum = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        if (!uint.TryParse(parts[i], out s[i])) { Log("ERROR", $"Invalid number: {parts[i]}"); return; }
                        sum += s[i];
                    }
                    if (sum != 200) { Log("ERROR", $"Sum is {sum}, must be 200:"); return; }
                    info.Might = s[0]; info.Intellect = s[1]; info.Stamina = s[2];
                    info.Agility = s[3]; info.Mysticism = s[4]; info.Aim = s[5];
                    Log("SYS", $"Stats set: M={s[0]} I={s[1]} S={s[2]} A={s[3]} My={s[4]} Ai={s[5]}");
                    charCreationState = CharCreationState.Spells;
                    Log("SYS", "Select spells/skills. Type 'list' for available, 'add <name>' to select, 'done' when finished.");
                    break;

                case CharCreationState.Spells:
                    if (text.Equals("list", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("SYS", "Available Spells:");
                        foreach (var spell in info.Spells) Log("SYS", $"  [Spell] {spell.SpellName}");
                        Log("SYS", "Available Skills:");
                        foreach (var skill in info.Skills) Log("SYS", $"  [Skill] {skill.SkillName}");
                    }
                    else if (text.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = text[4..].Trim();
                        var spell = info.Spells.FirstOrDefault(x => x.SpellName.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (spell != null) { info.SelectedSpells.Add(spell); Log("SYS", $"Added spell: {spell.SpellName}"); }
                        else
                        {
                            var skill = info.Skills.FirstOrDefault(x => x.SkillName.Equals(name, StringComparison.OrdinalIgnoreCase));
                            if (skill != null) { info.SelectedSkills.Add(skill); Log("SYS", $"Added skill: {skill.SkillName}"); }
                            else Log("ERROR", $"Unknown spell/skill: {name}");
                        }
                    }
                    else if (text.Equals("done", StringComparison.OrdinalIgnoreCase))
                    {
                        charCreationState = CharCreationState.Review;
                        Log("SYS", "--- Review Character ---");
                        Log("SYS", $"Name: {info.AvatarName} Gender: {info.Gender}");
                        Log("SYS", $"Stats: M={info.Might} I={info.Intellect} S={info.Stamina} A={info.Agility} My={info.Mysticism} Ai={info.Aim}");
                        Log("SYS", "Type 'confirm' to create character, or 'cancel' to abort.");
                    }
                    break;

                case CharCreationState.Review:
                    if (text.Equals("confirm", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("SYS", "Creating character...");
                        // Randomize appearance from available pools
                        Random r = new Random();
                        int skinIdx = r.Next(info.SkinColors.Length);
                        int hairColorIdx = r.Next(info.HairColors.Length);
                        int hairIdx = (info.Gender == Gender.Male) ? r.Next(info.MaleHairIDs.Length) : r.Next(info.FemaleHairIDs.Length);
                        int eyesIdx = (info.Gender == Gender.Male) ? r.Next(info.MaleEyeIDs.Length) : r.Next(info.FemaleEyeIDs.Length);
                        int noseIdx = (info.Gender == Gender.Male) ? r.Next(info.MaleNoseIDs.Length) : r.Next(info.FemaleNoseIDs.Length);
                        int mouthIdx = (info.Gender == Gender.Male) ? r.Next(info.MaleMouthIDs.Length) : r.Next(info.FemaleMouthIDs.Length);

                        info.SetExampleModel(info.Gender, skinIdx, hairColorIdx, hairIdx, eyesIdx, noseIdx, mouthIdx);
                        
                        SendSystemMessageNewCharInfo();
                        ResetCharCreation();
                    }
                    else if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("SYS", "Character creation aborted.");
                        ResetCharCreation();
                    }
                    break;
            }
        }

        public override void SendReqMoveMessage(bool ForceSend = false)
        {
            if (!ObjectID.IsValid(Data.AvatarID))
            {
                Logger.Log("TuiClient", LogType.Error, $"SendReqMoveMessage: AvatarID is INVALID: {Data.AvatarID:X8}");
            }

            // AVOID sending ReqMove(0,0) when we have a character selected but no avatar object yet.
            // This prevents the server from snapping our spawn position to 0,0 during room entry.
            if (ObjectID.IsValid(Data.AvatarID) && Data.AvatarObject == null) 
            {
                return;
            }
            base.SendReqMoveMessage(ForceSend);
        }

        private void HandleMovement(int dx, int dy)
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) 
            {
                return;
            }

            // Rate-limit: original MOVE_DELAY = 100ms between steps
            if ((DateTime.Now - lastMoveTime).TotalMilliseconds < MOVE_DELAY_MS) return;

            // Facing angle: 0=E, 1024=S, 2048=W, 3072=N (NUMDEGREES=4096)
            ushort angle;
            if (dx > 0)      angle = (dy > 0) ? (ushort)512  : (dy < 0) ? (ushort)3584 : (ushort)0;
            else if (dx < 0) angle = (dy > 0) ? (ushort)1536 : (dy < 0) ? (ushort)2560 : (ushort)2048;
            else             angle = (dy > 0) ? (ushort)1024 : (ushort)3072;

            if (avatar.AngleUnits != angle)
            {
                avatar.AngleUnits = angle;
                SendReqTurnMessage(true);
            }

            float stepSize = isRunning ? WORLD_STEP_RUN : WORLD_STEP_WALK;
            byte  speedByte = isRunning ? SPEED_RUN : SPEED_WALK;

            if (isNoClip)
            {
                var p = avatar.Position3D;
                p.X += dx * stepSize;
                p.Z += dy * stepSize;
                avatar.Position3D = p;
                avatar.HorizontalSpeed = speedByte;
                SendReqMoveMessage(true);
                lastMoveTime = DateTime.Now;
                if (isRecording) recorder.Record("Move", avatar, $"X:{avatar.CoordinateX},Y:{avatar.CoordinateY},A:{angle}");
                return;
            }

            // Optimistic movement: move locally, let server rubber-band if wrong.
            // Matches original clientd3d/move.c behavior.
            {
                var p = avatar.Position3D;
                p.X += dx * stepSize;
                p.Z += dy * stepSize;
                avatar.Position3D = p;
                avatar.HorizontalSpeed = speedByte;
                SendReqMoveMessage(true);
            }

            lastMoveTime = DateTime.Now;
            if (isRecording) recorder.Record("Move", avatar, $"X:{avatar.CoordinateX},Y:{avatar.CoordinateY},A:{angle}");
        }

        public override void SendReqActivate()
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) return;

            // 0. Priority: If resting/sitting, Spacebar stands you up
            if (Data.IsResting)
            {
                SendUserCommandStand();
                return;
            }

            // 1. Priority: Check for nearby exit walls (Spacebar triggers 'go' in original client if near exit)
            RooWall nearestWall = null;
            double minDistWall2 = 128.0 * 128.0; 
            var pos2D = new V2(avatar.CoordinateX * 16f - 1024f, avatar.CoordinateY * 16f - 1024f);
            var roo = CurrentRoom;

            if (roo != null)
            {
                foreach (var wall in roo.Walls)
                {
                    bool isPassable = (wall.LeftSide != null && wall.LeftSide.Flags.IsPassable) ||
                                      (wall.RightSide != null && wall.RightSide.Flags.IsPassable);
                    bool isExit = (wall.LeftSectorNum == 0 || wall.RightSectorNum == 0);

                    if (isPassable || isExit)
                    {
                        int uc;
                        var p1 = wall.P1; var p2 = wall.P2;
                        double d2 = (double)pos2D.MinSquaredDistanceToLineSegment(ref p1, ref p2, out uc);
                        if (d2 < minDistWall2)
                        {
                            minDistWall2 = d2;
                            nearestWall = wall;
                        }
                    }
                }
            }

            if (nearestWall != null)
            {
                Log("SYS", $"Near exit wall {nearestWall.Num}, triggering Go.");
                SendReqGo(true);
                return;
            }

            // 2. Secondary: Check for nearby activatable objects
            RoomObject nearestObj = null;
            double minDistObj = 128.0; 
            foreach (var obj in Data.RoomObjects)
            {
                if (obj.ID == avatar.ID) continue;
                if (!obj.Flags.IsActivatable) continue;
                double d = (double)avatar.GetDistance(obj);
                if (d < minDistObj)
                {
                    minDistObj = d;
                    nearestObj = obj;
                }
            }

            if (nearestObj != null && minDistObj < 128.0)
            {
                Log("SYS", $"Activating: {nearestObj.Name} (ID: {nearestObj.ID})");
                if (isRecording) recorder.Record("Activate", avatar, $"Object:{nearestObj.ID}");
                base.SendReqActivate(nearestObj.ID);
            }
            else
            {
                if (isRecording) recorder.Record("Activate", avatar, "Empty");
                base.SendReqActivate();
            }
        }

        public override void SendReqGo(bool SendPositionBefore = true)
        {
            var avatar = Data.AvatarObject;
            var roo = CurrentRoom;
            if (avatar != null && roo != null)
            {
                // ROO coordinates (0-based room space)
                // Use the standardized conversion from V3.ConvertToROO()
                // X = X * 16 - 1024, Z = Z * 16 - 1024
                var pos2D = new V2(avatar.CoordinateX * 16f - 1024f, avatar.CoordinateY * 16f - 1024f);
                
                RooWall nearestWall = null;
                double minDist2 = double.MaxValue;
                V2 snapPoint = pos2D;

                foreach (var wall in roo.Walls)
                {
                    bool isExit = (wall.LeftSectorNum == 0 || wall.RightSectorNum == 0);
                    if (isExit)
                    {
                        int uc;
                        var p1 = new V2((float)wall.P1.X, (float)wall.P1.Y);
                        var p2 = new V2((float)wall.P2.X, (float)wall.P2.Y);
                        double d2 = (double)pos2D.MinSquaredDistanceToLineSegment(ref p1, ref p2, out uc);
                        
                        if (d2 < minDist2)
                        {
                            minDist2 = d2;
                            nearestWall = wall;
                            
                            V2 diff = p2 - p1;
                            float l2 = (float)diff.LengthSquared;
                            if (l2 > 0)
                            {
                                float t = Math.Clamp((float)(((pos2D - p1) * diff) / l2), 0, 1);
                                snapPoint = p1 + diff * t;
                            }
                            else snapPoint = p1;
                        }
                    }
                }

                // If no exit wall found, try passable walls (might be a dynamic exit)
                if (nearestWall == null || minDist2 > 512.0 * 512.0)
                {
                    foreach (var wall in roo.Walls)
                    {
                        bool isPassable = (wall.LeftSide != null && wall.LeftSide.Flags.IsPassable) ||
                                          (wall.RightSide != null && wall.RightSide.Flags.IsPassable);
                        if (isPassable)
                        {
                            int uc;
                            var p1 = new V2((float)wall.P1.X, (float)wall.P1.Y);
                            var p2 = new V2((float)wall.P2.X, (float)wall.P2.Y);
                            double d2 = (double)pos2D.MinSquaredDistanceToLineSegment(ref p1, ref p2, out uc);
                            if (d2 < minDist2)
                            {
                                minDist2 = d2;
                                nearestWall = wall;
                                V2 diff = p2 - p1;
                                float l2 = (float)diff.LengthSquared;
                                if (l2 > 0)
                                {
                                    float t = Math.Clamp((float)(((pos2D - p1) * diff) / l2), 0, 1);
                                    snapPoint = p1 + diff * t;
                                }
                                else snapPoint = p1;
                            }
                        }
                    }
                }

                if (nearestWall != null && minDist2 < 1024.0 * 1024.0) // Within a reasonable distance
                {
                    Log("MOVE", $"Snapping to wall {nearestWall.Num} (Exit: {nearestWall.LeftSectorNum==0||nearestWall.RightSectorNum==0}) at ({snapPoint.X:F1},{snapPoint.Y:F1}) Dist: {Math.Sqrt(minDist2):F1}");
                    
                    // Convert snapped ROO back to World (1-based KOD)
                    // World = (Roo + 1024) / 16
                    ushort x = (ushort)Math.Clamp((int)Math.Round(snapPoint.X + 1024f) / 16, 0, 65535);
                    ushort y = (ushort)Math.Clamp((int)Math.Round(snapPoint.Y + 1024f) / 16, 0, 65535);

                    // Ensure we are in the EXACT center of the KOD square (which is 64 units wide)
                    ushort row = (ushort)(y / 64);
                    ushort col = (ushort)(x / 64);
                    
                    x = (ushort)(col * 64 + 32);
                    y = (ushort)(row * 64 + 32);

                    Log("MOVE", $"ReqMove to center of square: X={x} Y={y} (Row={row} Col={col})");

                    // Send the precise move request before the GO. ReqMove constructor is (X, Y, Mode, MapID, Angle)
                    var moveMsg = new ReqMoveMessage(x, y, 0, Data.RoomInformation.RoomID, avatar.AngleUnits);
                    ServerConnection.SendQueue.Enqueue(moveMsg);
                    
                    // Small delay or just enqueue GO immediately
                    ServerConnection.SendQueue.Enqueue(new ReqGoMessage());
                    return;
                }
                else
                {
                    Log("MOVE", $"No exit wall found within range. Nearest: {Math.Sqrt(minDist2):F1}");
                }
            }

            if (isRecording) recorder.Record("Go", avatar, $"X:{avatar.CoordinateX},Y:{avatar.CoordinateY}");
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
            base.Dispose();
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
