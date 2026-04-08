using System;
using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using Meridian59.Bot;
using Meridian59.Client;
using Meridian59.Files;
using Meridian59.Data;
using Meridian59.Data.Models;
using Meridian59.Protocol.Enums;
using Meridian59.Protocol.GameMessages;
using Meridian59.Common.Enums;
using Meridian59.Common;

namespace Meridian59.TuiClient
{
    /// <summary>
    /// One entry in the scrollback log ringbuffer.
    /// </summary>
    readonly struct LogEntry
    {
        public readonly string Timestamp;
        public readonly string Type;
        public readonly string Text;
        public LogEntry(string ts, string type, string text) { Timestamp = ts; Type = type; Text = text; }
    }

    public class TuiClient : BotClient<GameTick, ResourceManager, DataController, TuiConfig>
    {
        private AsciiRenderer renderer;
        private PathRecorder recorder;
        private bool isRecording = false;
        private string recordingFile = null;

        private int lastWindowWidth;
        private int lastWindowHeight;
        private bool sentRoomContentsRequest = false;
        private uint lastRoomID = 0;

        // Scrollback ringbuffer
        private const int LOG_CAPACITY = 500;
        private readonly List<LogEntry> logBuffer = new List<LogEntry>(LOG_CAPACITY + 1);
        private int scrollOffset = 0;   // 0 = newest at bottom, positive = scrolled back

        // Text input
        private string inputBuffer = "";
        private bool inputMode = false;  // true = typing into chat input; false = gameplay keys active

        // Auto-quit: disconnect this many seconds after entering game mode (0 = disabled)
        private const int AUTO_QUIT_SECONDS = 0;
        private DateTime gameModeSince = DateTime.MinValue;

        // Script execution
        private Queue<string> scriptQueue = null;
        private DateTime scriptNextAt = DateTime.MinValue;
        private bool scriptTriggered = false;

        // Layout constants
        private const int LOG_FIRST_ROW = 5;  // first row of the log area (row 0-4 are stats)
        private const int LEFT_PANEL_WIDTH = 78; // printable cols inside left panel (cols 1..78)

        public TuiClient() : base()
        {
            renderer = new AsciiRenderer();
            recorder = new PathRecorder(this);
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
            Console.Clear();
            DrawTuiLayout();
        }

        public override void Update()
        {
            try { base.Update(); }
            catch (InvalidOperationException) { /* no TTY */ }

            // Game logic runs regardless of TTY
            if (AUTO_QUIT_SECONDS > 0 && gameModeSince != DateTime.MinValue &&
                (DateTime.Now - gameModeSince).TotalSeconds >= AUTO_QUIT_SECONDS)
            {
                Log("SYS", $"Auto-quit after {AUTO_QUIT_SECONDS}s in game mode.");
                ServerConnection.Disconnect();
                IsRunning = false;
                return;
            }

            // Trigger autoexec once avatar is in world
            if (!scriptTriggered && Data.AvatarObject != null &&
                !string.IsNullOrEmpty(Data.RoomInformation?.RoomName))
            {
                scriptTriggered = true;
                LoadAutoexec();
                scriptNextAt = DateTime.Now.AddMilliseconds(500);
            }
            TickScript();

            // UI rendering only when TTY is available
            if (!HasTty) return;

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
                DrawStats();
                DrawRoomInfo();
                DrawMap();
                DrawInputField();
            }
        }

        protected override void HandleSaidMessage(Meridian59.Protocol.GameMessages.SaidMessage Message)
        {
            base.HandleSaidMessage(Message);
            string text = Message.Message?.FullString;
            if (!string.IsNullOrEmpty(text))
                Log("CHAT", text);
        }

        protected override void HandleAdminMessage(Meridian59.Protocol.GameMessages.AdminMessage Message)
        {
            base.HandleAdminMessage(Message);
            if (!string.IsNullOrEmpty(Message.Message))
                Log("SYS", Message.Message);
        }

        protected override void HandleMessageMessage(Meridian59.Protocol.GameMessages.MessageMessage Message)
        {
            base.HandleMessageMessage(Message);
            string text = Message.Message?.FullString;
            if (!string.IsNullOrEmpty(text))
                Log("SYS", text);
        }

        protected override void HandleGameModeMessage(Meridian59.Protocol.GameMessages.GameModeMessage Message)
        {
            if (gameModeSince == DateTime.MinValue)
                gameModeSince = DateTime.Now;

            var pi = (Meridian59.Protocol.Enums.MessageTypeGameMode)Message.PI;

            if (pi == Meridian59.Protocol.Enums.MessageTypeGameMode.Player)
            {
                var pMsg = (Meridian59.Protocol.GameMessages.PlayerMessage)Message;
                if (pMsg.RoomInfo.RoomID != lastRoomID)
                {
                    sentRoomContentsRequest = false;
                    lastRoomID = pMsg.RoomInfo.RoomID;
                }
                var ri = pMsg.RoomInfo;
                Log("NET", $"Player: room={ri.RoomID} avatar={ri.AvatarID}");
            }
            else if (pi == Meridian59.Protocol.Enums.MessageTypeGameMode.Move)
            {
                var m = (Meridian59.Protocol.GameMessages.MoveMessage)Message;
                var avatarID = Data.AvatarObject?.ID ?? 0;
                Log("NET", $"Move: obj={m.ObjectID}{(m.ObjectID == avatarID ? " (AVATAR)" : "")} x={m.NewCoordinateX} y={m.NewCoordinateY} spd={m.MovementSpeed}");
            }
            else if (pi == Meridian59.Protocol.Enums.MessageTypeGameMode.PlayWave)
            {
                var w = (Meridian59.Protocol.GameMessages.PlayWaveMessage)Message;
                Log("NET", $"PlayWave: {w.PlayInfo?.ResourceName ?? "?"}");
            }
            else if (pi == Meridian59.Protocol.Enums.MessageTypeGameMode.Effect)
            {
                var e = (Meridian59.Protocol.GameMessages.EffectMessage)Message;
                Log("NET", $"Effect: {e.Effect}");
            }
            else if (pi == Meridian59.Protocol.Enums.MessageTypeGameMode.Create)
            {
                var c = (Meridian59.Protocol.GameMessages.CreateMessage)Message;
                Log("NET", $"Create: obj={c.NewRoomObject?.ID} name={c.NewRoomObject?.Name} x={c.NewRoomObject?.CoordinateX} y={c.NewRoomObject?.CoordinateY}");
            }
            else if (Message is Meridian59.Protocol.GameMessages.GenericGameMessage gen)
            {
                // Log unknown server messages as hex for analysis
                var hexBytes = gen.Data != null
                    ? BitConverter.ToString(gen.Data).Replace("-", " ")
                    : "(null)";
                Log("NET", $"Unknown PI={Message.PI}: {hexBytes}");
            }

            base.HandleGameModeMessage(Message);
        }

        // ── Layout ──────────────────────────────────────────────────────────────

        public override void DrawBoxes()
        {
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;

            // Row 0: top border
            Console.SetCursorPosition(0, 0);
            Console.Write("╔" + new string('═', 14) + "╦" + new string('═', 43) + "╦" + new string('═', 19) + "╗ ╔" + new string('═', Math.Max(0, w - 84)) + "╗");

            // Rows 2-4: stats sub-borders
            Console.SetCursorPosition(0, 2);
            Console.Write("║" + new string(' ', 14) + "╠" + new string('═', 13) + "╦" + new string('═', 15) + "╦" + new string('═', 13) + "╬" + new string('═', 19) + "╣ ║" + new string(' ', Math.Max(0, w - 84)) + "║");
            Console.SetCursorPosition(0, 3);
            Console.Write("║" + new string(' ', 14) + "║" + new string(' ', 13) + "║" + new string(' ', 15) + "║" + new string(' ', 13) + "║" + new string(' ', 19) + "║ ║" + new string(' ', Math.Max(0, w - 84)) + "║");
            Console.SetCursorPosition(0, 4);
            Console.Write("╠" + new string('═', 14) + "╩" + new string('═', 13) + "╩" + new string('═', 15) + "╩" + new string('═', 13) + "╩" + new string('═', 19) + "╣ ║" + new string(' ', Math.Max(0, w - 84)) + "║");

            // Side borders: rows 1 and rows 5..h-4 (log area)
            for (int i = 1; i < h - 3; i++)
            {
                if (i == 2 || i == 3 || i == 4) continue;
                Console.SetCursorPosition(0, i);
                Console.Write("║");
                Console.SetCursorPosition(15, i);
                if (i < 4) Console.Write("║");
                Console.SetCursorPosition(59, i);
                if (i < 4) Console.Write("║");
                Console.SetCursorPosition(79, i);
                Console.Write("║ ║");
                Console.SetCursorPosition(Math.Max(0, w - 1), i);
                Console.Write("║");
            }

            // Input separator at h-3
            Console.SetCursorPosition(0, h - 3);
            Console.Write("╠" + new string('═', 79) + "╣ ║" + new string(' ', Math.Max(0, w - 84)) + "║");

            // Input field row at h-2
            Console.SetCursorPosition(0, h - 2);
            Console.Write("║" + new string(' ', 79) + "║ ╚" + new string('═', Math.Max(0, w - 84)) + "╝");

            // Hint bar at h-1
            Console.SetCursorPosition(0, h - 1);
            string hints = " [Enter]Chat  [Arrows/WASD]Move  [+/-]Zoom  [PgUp/Dn]Scroll  [Q]uit";
            Console.Write("╚" + hints.PadRight(79, '═') + "╝");
        }

        private void DrawTuiLayout()
        {
            DrawBoxes();
            DrawStats();
            DrawRoomInfo();
            DrawLog();
            DrawInputField();
            DrawMap();
        }

        // ── Stats ────────────────────────────────────────────────────────────────

        private static string StatStr(Meridian59.Data.Lists.StatNumericList cond, uint num)
        {
            var s = cond.GetItemByNum(num);
            if (s == null) return "---/---";
            int max = s.ValueRenderMax > 0 ? s.ValueRenderMax : s.ValueMaximum;
            return $"{s.ValueCurrent,3}/{max,3}";
        }

        public void DrawStats()
        {
            Console.SetCursorPosition(2, 1);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"HP: {StatStr(Data.AvatarCondition, Meridian59.Common.Constants.StatNums.HITPOINTS)}");

            Console.SetCursorPosition(2, 2);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"MP: {StatStr(Data.AvatarCondition, Meridian59.Common.Constants.StatNums.MANA)}");

            Console.SetCursorPosition(2, 3);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"VIG:{StatStr(Data.AvatarCondition, Meridian59.Common.Constants.StatNums.VIGOR)}");

            Console.SetCursorPosition(17, 3);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write($"RTT: {ServerConnection.RTT}ms   ");

            Console.SetCursorPosition(33, 3);
            Console.Write($"REST: {Data.IsResting,-5}");

            Console.SetCursorPosition(49, 3);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"$: {Data.Money,-8}");

            Console.ResetColor();
        }

        public void DrawRoomInfo()
        {
            Console.SetCursorPosition(17, 1);
            Console.ForegroundColor = ConsoleColor.Cyan;
            string roomName = Data.RoomInformation.RoomName ?? "LOADING...";
            Console.Write($"ROOM: {roomName.PadRight(38)}");

            Console.SetCursorPosition(61, 1);
            var avatar = Data.AvatarObject;
            if (avatar != null)
                Console.Write($"X:{avatar.CoordinateX,5} Y:{avatar.CoordinateY,5}");
            else
                Console.Write($"X:----- Y:-----");

            Console.ResetColor();
        }

        // ── Log ──────────────────────────────────────────────────────────────────

        public void DrawLog()
        {
            if (!HasTty) return;

            int logTop  = LOG_FIRST_ROW + 1;
            int logBot  = LogBottom;
            int logH    = logBot - logTop;
            if (logH <= 0) return;

            int count = logBuffer.Count;
            scrollOffset = Math.Max(0, Math.Min(scrollOffset, Math.Max(0, count - logH)));

            int startIdx = count - scrollOffset - logH;

            for (int row = 0; row < logH; row++)
            {
                int idx = startIdx + row;
                Console.SetCursorPosition(1, logTop + row);

                if (idx < 0 || idx >= count)
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write(new string(' ', LEFT_PANEL_WIDTH));
                    continue;
                }

                var e = logBuffer[idx];
                // e.Text is pre-formatted to LEFT_PANEL_WIDTH chars (done in Log())
                // Continuation entries have empty Type → DarkGray
                if (string.IsNullOrEmpty(e.Type))
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                else switch (e.Type)
                {
                    case "ERROR": Console.ForegroundColor = ConsoleColor.Red;     break;
                    case "CHAT":  Console.ForegroundColor = ConsoleColor.Cyan;    break;
                    case "SYS":   Console.ForegroundColor = ConsoleColor.Yellow;  break;
                    case "SYNC":  Console.ForegroundColor = ConsoleColor.Magenta; break;
                    default:      Console.ForegroundColor = ConsoleColor.Gray;    break;
                }
                Console.Write(e.Text);
            }

            if (scrollOffset > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                string ind = $" ↑{scrollOffset} ";
                Console.SetCursorPosition(79 - ind.Length, logTop);
                Console.Write(ind);
            }

            Console.ResetColor();
        }

        // ── Input field ──────────────────────────────────────────────────────────

        public void DrawInputField()
        {
            if (!HasTty) return;

            int h = Console.WindowHeight;
            int fieldWidth = LEFT_PANEL_WIDTH - 2; // room after "> "

            Console.SetCursorPosition(1, h - 2);

            if (inputMode)
            {
                Console.ForegroundColor = ConsoleColor.White;
                string display = inputBuffer.Length <= fieldWidth
                    ? inputBuffer
                    : inputBuffer[^fieldWidth..];
                Console.Write("> " + display.PadRight(fieldWidth));
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  " + "[Enter] to chat".PadRight(fieldWidth));
            }

            Console.ResetColor();
        }

        // ── Map ──────────────────────────────────────────────────────────────────

        public void DrawMap()
        {
            int startX = 82;
            int startY = 1;
            int width  = Console.WindowWidth - startX - 2;
            int height = Console.WindowHeight - 3;
            renderer.Render(this, startX, startY, width, height);
        }

        // ── Log override ─────────────────────────────────────────────────────────

        public override void Log(string Type, string Text)
        {
            logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Type,-8} {Text}");

            if (Type == "DEBUG" && HasTty) return;

            string ts     = DateTime.Now.ToShortTimeString();
            string prefix = $"{ts} {Type,-8} ";          // e.g. "7:43 PM SYS      " (17–18 chars)
            int    avail  = LEFT_PANEL_WIDTH - prefix.Length;  // chars available for message text
            if (avail < 1) avail = 1;

            // Sanitize: replace newline/tab chars with a space so Console.Write never jumps rows
            string safe = Text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

            // First line
            string firstText = WordWrapChunk(safe, avail, out string rest);
            AddLogRow(ts, Type, (prefix + firstText).PadRight(LEFT_PANEL_WIDTH));

            // Continuation lines: same indent width, blank prefix
            string indent = new string(' ', prefix.Length);
            int    cWidth = LEFT_PANEL_WIDTH - indent.Length;
            if (cWidth < 1) cWidth = 1;
            while (rest.Length > 0)
            {
                string chunk = WordWrapChunk(rest, cWidth, out rest);
                AddLogRow("", "", (indent + chunk).PadRight(LEFT_PANEL_WIDTH));
            }

            if (HasTty && scrollOffset == 0)
                DrawLog();
        }

        /// <summary>
        /// Returns up to <paramref name="width"/> chars from <paramref name="text"/>,
        /// breaking at the last space within that limit when possible.
        /// <paramref name="remainder"/> receives the leftover (leading spaces stripped).
        /// </summary>
        private static string WordWrapChunk(string text, int width, out string remainder)
        {
            if (text.Length <= width)
            {
                remainder = "";
                return text;
            }

            // Try to break at the last space within the allowed width
            int breakAt = text.LastIndexOf(' ', width - 1);
            if (breakAt <= 0)
                breakAt = width; // no space found — hard break

            string chunk = text[..breakAt];
            remainder = text[breakAt..].TrimStart(' ');
            return chunk;
        }

        private void AddLogRow(string ts, string type, string displayText)
        {
            logBuffer.Add(new LogEntry(ts, type, displayText));
            if (logBuffer.Count > LOG_CAPACITY)
                logBuffer.RemoveAt(0);
        }

        // ── Script runner ────────────────────────────────────────────────────────

        private void LoadAutoexec()
        {
            const string path = "autoexec.script";
            if (!File.Exists(path)) return;
            scriptQueue = new Queue<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                scriptQueue.Enqueue(line);
            }
            Log("SYS", $"autoexec.script: {scriptQueue.Count} command(s) queued.");
        }

        private void TickScript()
        {
            if (scriptQueue == null || scriptQueue.Count == 0) return;
            if (DateTime.Now < scriptNextAt) return;

            string line = scriptQueue.Dequeue();
            Log("SYS", $"[script] {line}");
            ExecuteScriptLine(line);
        }

        private void ExecuteScriptLine(string line)
        {
            if (line.StartsWith("sleep ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.Substring(6).Trim(), out int ms))
                    scriptNextAt = DateTime.Now.AddMilliseconds(ms);
                return;
            }
            ProcessCommand(line);
        }

        // ── Command parsing ──────────────────────────────────────────────────────

        private void ProcessCommand(string text)
        {
            // /quit, /logout
            if (text.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/logout", StringComparison.OrdinalIgnoreCase))
            {
                Log("SYS", "Disconnecting...");
                ServerConnection.Disconnect();
                IsRunning = false;
                return;
            }

            // rest / stand
            if (text.Equals("rest", StringComparison.OrdinalIgnoreCase))
            {
                SendUserCommandRest();
                Log("SYS", "Resting...");
                return;
            }
            if (text.Equals("stand", StringComparison.OrdinalIgnoreCase))
            {
                SendUserCommandStand();
                Log("SYS", "Standing.");
                return;
            }

            // cast <spell>
            if (text.StartsWith("cast ", StringComparison.OrdinalIgnoreCase))
            {
                string spellName = text.Substring(5).Trim();
                var matches = Data.SpellObjects.GetItemsByNamePrefix(spellName);
                if (matches.Count == 0)
                {
                    Log("SYS", $"Unknown spell: '{spellName}'");
                    if (Data.SpellObjects.Count > 0)
                        Log("SYS", "Known: " + string.Join(", ", System.Linq.Enumerable.Select(Data.SpellObjects, s => s.Name)));
                    return;
                }
                var spell = matches[0];
                // Build and send ReqCastMessage directly to bypass the
                // IsVisibleFrom(CurrentRoom) check which returns false when
                // the ROO file isn't loaded (bot/headless context).
                ObjectID[] targets;
                if (spell.TargetsCount > 0 && Data.AvatarObject != null)
                    targets = new[] { new ObjectID(Data.AvatarObject.ID) };
                else
                    targets = new ObjectID[0];
                ServerConnection.SendQueue.Enqueue(
                    new Meridian59.Protocol.GameMessages.ReqCastMessage(spell.ID, targets));
                Log("SYS", $"Casting: {spell.Name}");
                return;
            }

            // say
            SendSayToMessage(ChatTransmissionType.Normal, text);
            Log("SYS", $"You: {text}");

            if (isRecording)
                recorder.Record("Said", Data.AvatarObject, $"Normal:{text}");
        }

        // ── Input / movement ─────────────────────────────────────────────────────

        protected override void ProcessKeyPress(ConsoleKeyInfo key)
        {
            if (inputMode)
            {
                // ── Chat input mode ──────────────────────────────────────────
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        if (inputBuffer.Length > 0)
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
                return;
            }

            // ── Normal / gameplay mode ───────────────────────────────────────
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    // Enter chat mode
                    inputMode = true;
                    DrawInputField();
                    break;

                case ConsoleKey.PageUp:
                {
                    int logH = LogBottom - (LOG_FIRST_ROW + 1);
                    scrollOffset = Math.Min(scrollOffset + Math.Max(1, logH / 2),
                                           Math.Max(0, logBuffer.Count - logH));
                    DrawLog();
                    break;
                }

                case ConsoleKey.PageDown:
                {
                    int logH = LogBottom - (LOG_FIRST_ROW + 1);
                    scrollOffset = Math.Max(0, scrollOffset - Math.Max(1, logH / 2));
                    DrawLog();
                    break;
                }

                case ConsoleKey.OemPlus:
                case ConsoleKey.Add:
                    renderer.ZoomIn();
                    renderer.Invalidate();
                    DrawMap();
                    break;

                case ConsoleKey.OemMinus:
                case ConsoleKey.Subtract:
                    renderer.ZoomOut();
                    renderer.Invalidate();
                    DrawMap();
                    break;

                // Arrow key movement
                case ConsoleKey.UpArrow:    Move( 0, -1, 3072); break;
                case ConsoleKey.DownArrow:  Move( 0,  1, 1024); break;
                case ConsoleKey.LeftArrow:  Move(-1,  0, 2048); break;
                case ConsoleKey.RightArrow: Move( 1,  0,    0); break;

                // WASD also moves
                case ConsoleKey.W: Move( 0, -1, 3072); break;
                case ConsoleKey.S: Move( 0,  1, 1024); break;
                case ConsoleKey.A: Move(-1,  0, 2048); break;
                case ConsoleKey.D: Move( 1,  0,    0); break;

                case ConsoleKey.R:
                    if (key.Modifiers == ConsoleModifiers.Control)
                    {
                        if (!isRecording)
                        {
                            recordingFile = $"path_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                            recorder.Start(recordingFile);
                            isRecording = true;
                            Log("SYS", "Recording started: " + recordingFile);
                        }
                        else
                        {
                            recorder.Stop();
                            isRecording = false;
                            Log("SYS", "Recording saved: " + recordingFile);
                        }
                    }
                    break;

                default:
                    base.ProcessKeyPress(key);
                    break;
            }
        }

        private void Move(int dx, int dy, ushort angle)
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) return;

            // Save current server-authoritative position
            ushort origX     = avatar.CoordinateX;
            ushort origY     = avatar.CoordinateY;
            ushort origAngle = avatar.AngleUnits;

            // Desired position
            ushort newX = (ushort)Math.Clamp((int)origX + dx * 64, 0, 65535);
            ushort newY = (ushort)Math.Clamp((int)origY + dy * 64, 0, 65535);

            // Temporarily set desired coords so SendReqMoveMessage can read them, then restore.
            // The server will confirm or rubberband via BP_Move → StartMoveTo.
            avatar.CoordinateX = newX;
            avatar.CoordinateY = newY;
            avatar.AngleUnits  = angle;
            SendReqMoveMessage(true);
            avatar.CoordinateX = origX;
            avatar.CoordinateY = origY;
            avatar.AngleUnits  = origAngle;

            if (isRecording)
                recorder.Record("Move", avatar, $"X:{newX},Y:{newY},A:{angle}");
        }

        public override void SendReqMoveMessage(bool ForceSend)
        {
            base.SendReqMoveMessage(ForceSend);
        }

        public override void SendActionMessage(ActionType Action)
        {
            base.SendActionMessage(Action);
            if (isRecording && Data.AvatarObject != null)
                recorder.Record("Action", Data.AvatarObject, Action.ToString());
        }

        public override void SendSayToMessage(ChatTransmissionType Type, string Text)
        {
            base.SendSayToMessage(Type, Text);
            if (isRecording)
                recorder.Record("Said", Data.AvatarObject, $"{Type}:{Text}");
        }

        public override void SendSayGroupMessage(uint TargetID, string Text)
        {
            base.SendSayGroupMessage(TargetID, Text);
            if (isRecording)
                recorder.Record("SaidGroup", Data.AvatarObject, $"{TargetID}:{Text}");
        }
    }
}
