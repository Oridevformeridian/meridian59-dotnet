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
        CharSheet,
        Login,
        CharSelect,
        NewChar,
        Buy,
        Offer,
        SpellTarget,
        LookList,
        LookDetail,
        GetList,
        StatChange,
        StatChangeConfirm,
    }

    public enum NewCharTab { Stats = 0, Skills = 1, Spells = 2, Looks = 3 }

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
        private PathReplayer replayer;
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
        private DateTime lastRoomTransitionTime = DateTime.MinValue;

        // Key-hold movement: replay the last direction every tick while the key is still down.
        // Console gives only key-down events; OS auto-repeat fires every ~30ms after initial ~400ms delay.
        // We use those repeat events to keep lastKeyMoveTime fresh; if it goes stale (>150ms) we stop.
        private int heldMoveDx = 0;
        private int heldMoveDz = 0;
        private DateTime lastKeyMoveTime = DateTime.MinValue;
        private const int KEY_HOLD_TIMEOUT_MS = 150;

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

        // Login dialog state
        private enum LoginField { Username, Password }
        private LoginField loginField = LoginField.Username;
        private string loginUsername = "";
        private string loginPassword = "";

        // Char select state
        private List<Meridian59.Data.Models.CharSelectItem> charSelectList = new List<Meridian59.Data.Models.CharSelectItem>();

        // New char creation popup state
        private NewCharTab newCharTab = NewCharTab.Stats;
        private int newCharSelectionIndex = 0;
        private int newCharScrollOffset = 0;
        private string newCharName = "";
        private bool newCharNamingMode = false; // true = typing name
        // Stat inline-edit mode: which stat row is being edited (-1 = none), and the buffer
        private int newCharEditingStatIndex = -1;
        private string newCharStatEditBuffer = "";
        // Looks tab: selected preset index (0-4)
        private int newCharLooksPreset = 0;

        // Buy popup state
        private int buySelectedIndex = 0;
        private string buyQuantityBuffer = "";  // non-empty = user is typing a quantity
        private int buyQuantityItemIndex = -1;  // which item the quantity edit is for

        // Offer popup state
        private int offerSelectedIndex = 0;     // selection in your inventory list
        private List<uint> offerPendingIDs = new List<uint>(); // items you've staged to offer

        // Spell target picker state: pending cast that needs an inventory/room target
        private uint spellTargetSpellID = 0;
        private int spellTargetSelectedIndex = 0;
        private bool spellTargetInventory = true; // true=inventory, false=room objects

        // Look popup state
        private List<RoomObject> lookCandidates = new();
        private int lookSelectedIndex = 0;

        // Get popup state
        private List<RoomObject> getCandidates = new();
        private int getSelectedIndex = 0;

        // StatChange popup state
        // Rows 0-5 = stats (Might/Int/Sta/Agi/Mys/Aim), rows 6-12 = schools (Sha/Qor/Kra/Far/Rij/Jal/WC)
        private int statChangeSelectedIndex = 0;
        private int statChangeEditingIndex = -1;   // -1 = not editing
        private string statChangeEditBuffer = "";

        // Combat state
        private bool autoAttack = false;
        private bool showWhoList = false;
        private bool soundEnabled = false;
        private int lastAvatarHP = -1;

        // Convey-all macro state
        private Queue<uint> conveyQueue = new Queue<uint>();
        private uint conveySpellID = 0;
        private DateTime conveyNextCastTime = DateTime.MinValue;

        // Drain-unbound macro state
        private bool drainUnbound = false;
        private uint drainUnboundSpellID = 0;
        private string drainUnboundSpellName = "";

        // HP flash state — flashes bar white→red for a short burst on damage
        private int    hpFlashFrames  = 0;   // countdown frames remaining
        private bool   hpFlashPhase   = false; // alternates bar color each draw
        private const int HP_FLASH_FRAMES = 8; // ~800ms at 100ms tick

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
            public readonly List<ChatStyle> Styles;     // null = plain text
            public readonly int StyleOffset;             // unused; reserved
            public readonly bool IsContinuation;         // true = wrap overflow line, no prefix
            public LogEntry(string type, string text) { Timestamp = DateTime.Now; Type = type; Text = text; Styles = null; StyleOffset = 0; IsContinuation = false; }
            public LogEntry(string type, string text, List<ChatStyle> styles, int styleOffset, bool isContinuation = false) { Timestamp = DateTime.Now; Type = type; Text = text; Styles = styles; StyleOffset = styleOffset; IsContinuation = isContinuation; }
        }

        /// <summary>Maps a M59 ChatColor to the nearest ConsoleColor.</summary>
        private static ConsoleColor ChatColorToConsole(ChatColor c, bool bold)
        {
            switch (c)
            {
                case ChatColor.Red:          return bold ? ConsoleColor.Red          : ConsoleColor.DarkRed;
                case ChatColor.Green:        return bold ? ConsoleColor.Green        : ConsoleColor.DarkGreen;
                case ChatColor.Blue:         return bold ? ConsoleColor.Blue         : ConsoleColor.DarkBlue;
                case ChatColor.Cyan:         return bold ? ConsoleColor.Cyan         : ConsoleColor.DarkCyan;
                case ChatColor.Aquamarine:   return bold ? ConsoleColor.Cyan         : ConsoleColor.DarkCyan;
                case ChatColor.Purple:       return bold ? ConsoleColor.Magenta      : ConsoleColor.DarkMagenta;
                case ChatColor.Magenta:      return bold ? ConsoleColor.Magenta      : ConsoleColor.DarkMagenta;
                case ChatColor.Violet:       return bold ? ConsoleColor.Magenta      : ConsoleColor.DarkMagenta;
                case ChatColor.Yellow:       return bold ? ConsoleColor.Yellow       : ConsoleColor.DarkYellow;
                case ChatColor.Jonquil:      return bold ? ConsoleColor.Yellow       : ConsoleColor.DarkYellow;
                case ChatColor.Golden:       return bold ? ConsoleColor.Yellow       : ConsoleColor.DarkYellow;
                case ChatColor.Champagne:    return bold ? ConsoleColor.Yellow       : ConsoleColor.DarkYellow;
                case ChatColor.Orange:       return bold ? ConsoleColor.Yellow       : ConsoleColor.DarkYellow;
                case ChatColor.Fire:         return bold ? ConsoleColor.Red          : ConsoleColor.DarkYellow;
                case ChatColor.Lime:         return bold ? ConsoleColor.Green        : ConsoleColor.Green;
                case ChatColor.Emerald:      return bold ? ConsoleColor.Green        : ConsoleColor.DarkGreen;
                case ChatColor.ToxicGreen:   return bold ? ConsoleColor.Green        : ConsoleColor.DarkGreen;
                case ChatColor.QuestGreen:   return bold ? ConsoleColor.Green        : ConsoleColor.DarkGreen;
                case ChatColor.QuestRed:     return bold ? ConsoleColor.Red          : ConsoleColor.DarkRed;
                case ChatColor.ImperialBlue: return bold ? ConsoleColor.Blue         : ConsoleColor.DarkBlue;
                case ChatColor.Steel:        return bold ? ConsoleColor.Cyan         : ConsoleColor.DarkCyan;
                case ChatColor.Pink:         return bold ? ConsoleColor.Magenta      : ConsoleColor.Magenta;
                case ChatColor.Drab:         return bold ? ConsoleColor.DarkGray     : ConsoleColor.DarkGray;
                case ChatColor.Bronze:       return bold ? ConsoleColor.DarkYellow   : ConsoleColor.DarkYellow;
                case ChatColor.OffWhite:     return bold ? ConsoleColor.White        : ConsoleColor.Gray;
                case ChatColor.Black:        return ConsoleColor.DarkGray; // black-on-black invisible; use dark gray
                case ChatColor.White:        return bold ? ConsoleColor.White        : ConsoleColor.Gray;
                case ChatColor.MercenaryColor: return bold ? ConsoleColor.Cyan       : ConsoleColor.DarkCyan;
                // Gray scale
                case ChatColor.Gray1:  case ChatColor.Gray2:  case ChatColor.Gray3:
                    return ConsoleColor.DarkGray;
                case ChatColor.Gray4:  case ChatColor.Gray5:  case ChatColor.Gray6:
                    return ConsoleColor.Gray;
                case ChatColor.Gray7:  case ChatColor.Gray8:  case ChatColor.Gray9:  case ChatColor.Gray10:
                    return ConsoleColor.White;
                default:             return bold ? ConsoleColor.White : ConsoleColor.Gray;
            }
        }

        /// <summary>
        /// Writes up to <paramref name="maxLen"/> chars of <paramref name="text"/> with
        /// M59 chat styles applied, then pads with spaces to <paramref name="maxLen"/>.
        /// </summary>
        private static void WriteStyledString(string text, List<ChatStyle> styles, int maxLen)
        {
            if (styles == null || styles.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(text.Length <= maxLen ? text.PadRight(maxLen) : text[..maxLen]);
                return;
            }

            int written = 0;
            int remaining = maxLen;

            foreach (var style in styles)
            {
                if (remaining <= 0) break;
                int start = style.StartIndex;
                int len   = Math.Min(style.Length, remaining);
                if (len <= 0) continue;
                if (start + len > text.Length) len = Math.Max(0, text.Length - start);
                if (len <= 0) continue;

                Console.ForegroundColor = ChatColorToConsole(style.Color, style.IsBold);
                Console.Write(text.Substring(start, len));
                written   += len;
                remaining -= len;
            }

            // Pad remainder
            if (remaining > 0)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(new string(' ', remaining));
            }
        }

        // Text input
        private string inputBuffer = "";
        private bool inputMode = false;  // true = typing into chat input; false = gameplay keys active
        private readonly List<string> commandHistory = new List<string>();
        private int historyIndex = -1;  // -1 = not browsing; 0 = oldest entry

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

        protected override void HandleGetLoginMessage(GetLoginMessage Message)
        {
            // If credentials already configured, use them directly (BotClient base behaviour)
            string user = Config.SelectedConnectionInfo?.Username;
            string pass = Config.SelectedConnectionInfo?.Password;
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                SendLoginMessage(user, pass);
                return;
            }

            // Otherwise show the interactive login popup
            loginUsername = "";
            loginPassword = "";
            loginField = LoginField.Username;
            activePopup = PopupMode.Login;
            DrawMap();
        }

        protected override void HandleLoginOKMessage(LoginOKMessage Message)
        {
            Log("SYS", "Login accepted.");
        }

        protected override void HandleLoginFailedMessage(Meridian59.Protocol.GameMessages.LoginFailedMessage Message)
        {
            if (activePopup == PopupMode.Login)
            {
                loginPassword = "";
                loginField = LoginField.Username;
                Log("ERROR", "Login failed — check credentials.");
                DrawMap();
            }
            else
            {
                base.HandleLoginFailedMessage(Message);
            }
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
             Data.AvatarBuffs.ListChanged += (s, e) => { if (HasTty) lock (consoleLock) DrawBuffs(); };
              Data.OnlinePlayers.ListChanged += (s, e) =>
              {
                  if (!HasTty) return;
                  if (soundEnabled && (e.ListChangedType == System.ComponentModel.ListChangedType.ItemAdded || e.ListChangedType == System.ComponentModel.ListChangedType.ItemDeleted))
                      try { Console.Beep(880, 120); } catch { }
                  if (showWhoList) lock (consoleLock) DrawMap();
              };
             Data.AvatarBuffs.ListChanged += (s, e) => { if (HasTty) lock (consoleLock) DrawBuffs(); };
             Data.NewsGroup.PropertyChanged += (s, e) =>
             {
                 if (e.PropertyName == "Text" && activePopup == PopupMode.NewsRead) DrawMap();
                  if (e.PropertyName == "IsVisible" && Data.NewsGroup.IsVisible)
                  {
                      Data.LookObject.IsVisible = false;  // suppress the companion Look message
                      activePopup      = PopupMode.NewsList;
                      popupSelectedIndex  = 0;
                      popupScrollOffset   = 0;
                      DrawMap();
                  }
             };

             // Look response — server sent us a description
             Data.LookObject.PropertyChanged += (s, e) =>
             {
                 if (e.PropertyName == "IsVisible" && Data.LookObject.IsVisible)
                 {
                     // Don't overwrite the news popup if a newsglobe look just opened it
                     if (activePopup == PopupMode.NewsList || activePopup == PopupMode.NewsRead)
                         return;
                     activePopup = PopupMode.LookDetail;
                     DrawMap();
                 }
             };

             Data.LookPlayer.PropertyChanged += (s, e) =>
             {
                 if (e.PropertyName == "IsVisible" && Data.LookPlayer.IsVisible)
                 {
                     activePopup = PopupMode.LookDetail;
                     DrawMap();
                 }
             };

            // Ensure UI updates when room or avatar changes
            Data.PropertyChanged += (s, e) => {
                if (e.PropertyName == "RoomInformation" || e.PropertyName == "AvatarObject")
                {
                    DrawRoomInfo();
                    DrawBuffs();
                    DrawMap();
                }
            };

            // Ancient trinket / stat reset offer from server
            Data.StatChangeInfo.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "IsVisible" && Data.StatChangeInfo.IsVisible)
                {
                    statChangeSelectedIndex = 0;
                    statChangeEditingIndex  = -1;
                    statChangeEditBuffer    = "";
                    activePopup = PopupMode.StatChange;
                    Log("SYS", "Ancient trinket activated — redistribute your stats.");
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

            replayer?.Update();
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

            // Key-hold movement: replay last direction every tick while key is held
            if (heldMoveDx != 0 || heldMoveDz != 0)
            {
                if ((DateTime.Now - lastKeyMoveTime).TotalMilliseconds > KEY_HOLD_TIMEOUT_MS)
                {
                    heldMoveDx = 0;
                    heldMoveDz = 0;
                }
                else
                {
                    HandleMovement(heldMoveDx, heldMoveDz);
                }
            }

            // Convey-all macro: drain one item per 500ms, only consume when cast can fire
            while (conveyQueue.Count > 0 && DateTime.Now >= conveyNextCastTime)
            {
                uint itemID = conveyQueue.Peek();
                // Skip items that were already removed from inventory (consumed by a prior cast)
                var item = Data.InventoryObjects.GetItemByID(itemID);
                if (item == null)
                {
                    conveyQueue.Dequeue();
                    continue;
                }
                // Wait for cast cooldown — don't consume the item until we can actually send
                if (!GameTick.CanReqCast())
                    break;
                conveyQueue.Dequeue();
                Data.SelfTarget = false;
                Data.TargetID = itemID;
                SendReqCastMessage(conveySpellID);
                Log("SYS", $"[Convey] {item.Name} ({conveyQueue.Count} remaining)");
                conveyNextCastTime = DateTime.Now.AddMilliseconds(500);
                break;
            }

            // Drain-unbound: self-cast a spell at max rate whenever not targeting a creature
            if (drainUnbound && drainUnboundSpellID != 0)
            {
                var drainTgt = Data.TargetObject as RoomObject;
                bool combatTarget = drainTgt != null &&
                                    (drainTgt.Flags.IsCreature || drainTgt.Flags.IsAttackable);
                if (!combatTarget && GameTick.CanReqCast())
                {
                    Data.SelfTarget = true;
                    SendReqCastMessage(drainUnboundSpellID);
                    Data.SelfTarget = false;
                }
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

                // Auto-target as soon as any mob acquires aggro on us (MM_AGGRO_SELF),
                // even before a hit lands. Fall back to nearest if nothing has aggro.
                if (Data.TargetObject == null)
                {
                    var aggressor = Data.RoomObjects
                        .OfType<RoomObject>()
                        .Where(o => o.Flags.IsMinimapAggroSelf)
                        .OrderBy(o => {
                            var av = Data.AvatarObject;
                            if (av == null) return float.MaxValue;
                            float dx = o.CoordinateX - av.CoordinateX;
                            float dz = o.CoordinateY - av.CoordinateY;
                            return dx * dx + dz * dz;
                        })
                        .FirstOrDefault();
                    if (aggressor != null)
                    {
                        Data.SelfTarget = false;
                        Data.TargetID = aggressor.ID;
                        FaceTarget(aggressor);
                        Log("SYS", $"Auto-targeted aggressor: {aggressor.Name}");
                    }
                }
                // HP tracking — flash bar and fallback auto-target on damage
                int currentHP = (int)(Data.AvatarCondition.GetItemByNum(1)?.ValueCurrent ?? -1);
                if (lastAvatarHP >= 0 && currentHP >= 0 && currentHP < lastAvatarHP)
                {
                    hpFlashFrames = HP_FLASH_FRAMES;
                    hpFlashPhase  = true;
                    if (Data.TargetObject == null)
                        AutoTargetNearest();
                }
                if (currentHP >= 0) lastAvatarHP = currentHP;
            }

            else
            {
                // NPC context targeting: auto-select the nearest NPC in front of the avatar
                // (only when not locked onto a live hostile combat target)
                UpdateNPCTarget();

                // Still track HP for flash even when autoAttack is off
                int nonCombatHP = (int)(Data.AvatarCondition.GetItemByNum(1)?.ValueCurrent ?? -1);
                if (lastAvatarHP >= 0 && nonCombatHP >= 0 && nonCombatHP < lastAvatarHP)
                {
                    hpFlashFrames = HP_FLASH_FRAMES;
                    hpFlashPhase  = true;
                }
                if (nonCombatHP >= 0) lastAvatarHP = nonCombatHP;
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
                DrawBuffs();
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
            var msg = Message.Message;
            string text = msg?.FullString;
            if (!string.IsNullOrEmpty(text))
            {
                var speaker = Data.RoomObjects.FirstOrDefault(o => o.ID == msg.SourceObjectID);
                string speakerName = speaker?.Name ?? "???";

                bool isEmote = msg.TransmissionType == ChatTransmissionType.Emote;
                string logTag = isEmote ? "EMOTE" : "CHAT";
                // Emotes: "* Name text"  — Said: "Name: text"
                string prefix = isEmote ? $"* {speakerName} " : $"{speakerName}: ";
                string full = prefix + text;

                // Offset all style indices by the plain prefix length so they align in the combined string
                List<ChatStyle> styles = null;
                if (msg.Styles != null && msg.Styles.Count > 0)
                {
                    styles = new List<ChatStyle>(msg.Styles.Count);
                    // Leading plain prefix — emotes in italic-style yellow, says in white
                    ChatColor prefixColor = isEmote ? ChatColor.Yellow : ChatColor.White;
                    styles.Add(new ChatStyle(0, prefix.Length, false, false, false, false, false, prefixColor));
                    foreach (var s in msg.Styles)
                        styles.Add(new ChatStyle(s.StartIndex + prefix.Length, s.Length, s.IsBold, s.IsCursive, s.IsUnderline, s.IsStrikeout, s.IsLink, s.Color));
                }

                LogStyled(logTag, full, styles);
            }
        }

        private Queue<string> scriptQueue = new Queue<string>();
        private DateTime nextScriptCommandTime = DateTime.MinValue;

        protected override void HandleGameStateMessage(GameStateMessage Message)
        {
            base.HandleGameStateMessage(Message);

            // Apply preferences from config to ClientPreferences then send to server
            Data.ClientPreferences.IsSafetyOff   = Config.PrefSafetyOff;
            Data.ClientPreferences.TempSafe       = Config.PrefTempSafe;
            Data.ClientPreferences.Grouping       = Config.PrefGrouping;
            Data.ClientPreferences.AutoLoot       = Config.PrefAutoLoot;
            Data.ClientPreferences.AutoCombine    = Config.PrefAutoCombine;
            Data.ClientPreferences.ReagentBag     = Config.PrefReagentBag;
            Data.ClientPreferences.SpellPower     = Config.PrefSpellPower;
            SendUserCommandSendPreferences();
            Log("SYS", $"Prefs sent: TempSafe={Config.PrefTempSafe} AutoLoot={Config.PrefAutoLoot} AutoCombine={Config.PrefAutoCombine} Grouping={Config.PrefGrouping}");

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
                return;

            // Auto-select if a character is configured
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

            // Script mode: auto-pick first character
            if (!string.IsNullOrEmpty(ScriptFile) && Message.WelcomeInfo.Characters.Count > 0)
            {
                var fallback = Message.WelcomeInfo.Characters.FirstOrDefault(c => !c.IsEmptySlot);
                if (fallback != null)
                {
                    Data.ExpectedAvatarName = fallback.Name;
                    SendUseCharacterMessage(new ObjectID(fallback.ID), true, fallback.Name);
                    return;
                }
            }

            // Interactive: show character select popup
            charSelectList = Message.WelcomeInfo.Characters.ToList();
            popupSelectedIndex = 0;
            activePopup = PopupMode.CharSelect;
            DrawMap();
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
                recorder?.RecordRoom(Data.AvatarObject, Message.RoomInfo.RoomID, Message.RoomInfo.RoomName);
                lastRoomTransitionTime = DateTime.Now;
                replayer?.NotifyRoomChanged(Message.RoomInfo.RoomID);
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

            // Clear stale articles before base appends new ones
            if (pi == MessageTypeGameMode.Articles)
                Data.NewsGroup.Articles.Clear();

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

            // News debug: log after base has processed so Data is already updated
            if (pi == MessageTypeGameMode.LookNewsGroup && Message is LookNewsGroupMessage lngMsg)
            {
                Log("SYS", $"NEWS DBG LookNewsGroup: GlobeID={lngMsg.NewsGroup?.NewsGlobeID} IsVisible={Data.NewsGroup.IsVisible} activePopup={activePopup}");
                SendReqArticles();
            }
            else if (pi == MessageTypeGameMode.Articles && Message is ArticlesMessage artMsg)
                Log("SYS", $"NEWS DBG Articles: count={artMsg.Articles?.Length} total now={Data.NewsGroup.Articles.Count} activePopup={activePopup}");
            else if (pi == MessageTypeGameMode.Look && Message is LookMessage lookMsg)
                Log("SYS", $"NEWS DBG Look: name={lookMsg.ObjectInfo?.ObjectBase?.Name} LookObject.IsVisible={Data.LookObject.IsVisible} activePopup={activePopup}");

            // Server position correction: base's StartMoveTo() only initiates interpolation;
            // CoordinateX/Y stays at the old position until animation ticks complete.
            // For a headless client we don't need smooth animation — snap immediately so
            // the renderer and any subsequent move packets use the server-authoritative position.
            if (pi == MessageTypeGameMode.Move && Message is MoveMessage snapMsg &&
                snapMsg.ObjectID == Data.AvatarID && Data.AvatarObject != null)
            {
                var av = Data.AvatarObject;
                ushort prevX = av.CoordinateX;
                ushort prevY = av.CoordinateY;
                // Stop interpolation before snapping so UpdatePosition() can't
                // tick the avatar back to the optimistic MoveDestination after we correct.
                av.IsMoving = false;
                av.CoordinateX = snapMsg.NewCoordinateX;
                av.CoordinateY = snapMsg.NewCoordinateY;
                // Invalidate dedup so the next sent move reflects the corrected position.
                lastSentPositionX = ushort.MaxValue;
                lastSentPositionY = ushort.MaxValue;
                // Significant correction while replaying → abort replay to avoid fight with server.
                // Suppress for 3s after a room transition — entry-point drift is expected and harmless.
                int ddx = (int)snapMsg.NewCoordinateX - (int)prevX;
                int ddy = (int)snapMsg.NewCoordinateY - (int)prevY;
                double corr = Math.Sqrt(ddx * ddx + ddy * ddy);
                bool recentTransition = (DateTime.Now - lastRoomTransitionTime).TotalMilliseconds < 3000;
                if (corr > 128 && replayer != null && replayer.IsReplaying && !recentTransition)
                {
                    replayer.Stop();
                    Log("SYS", $"Server correction ({corr:F0} units) — replay aborted.");
                }
                renderer.Invalidate();
            }

            // After base has populated Data.CharCreationInfo, open the popup
            if (pi == MessageTypeGameMode.CharInfo && charCreationState == CharCreationState.AwaitingCharInfo)
            {
                charCreationState = CharCreationState.None;
                newCharTab = NewCharTab.Stats;
                newCharSelectionIndex = -1;  // start on name field
                newCharScrollOffset = 0;
                newCharName = Data.CharCreationInfo.AvatarName ?? "";
                newCharNamingMode = false;
                activePopup = PopupMode.NewChar;
                DrawMap();
            }

            // Buy list arrived — open the buy popup
            if (pi == MessageTypeGameMode.BuyList)
            {
                buySelectedIndex = 0;
                buyQuantityItemIndex = -1;
                buyQuantityBuffer = "";
                activePopup = PopupMode.Buy;
                DrawMap();
            }

            // Trade: incoming offer from another party — open offer popup
            if (pi == MessageTypeGameMode.Offer)
            {
                offerSelectedIndex = 0;
                offerPendingIDs.Clear();
                activePopup = PopupMode.Offer;
                DrawMap();
            }

            // Trade: echo of our items / counter-offer arrived — refresh if already open
            if (pi == MessageTypeGameMode.Offered || pi == MessageTypeGameMode.CounterOffer
                || pi == MessageTypeGameMode.CounterOffered)
            {
                if (activePopup != PopupMode.Offer)
                {
                    offerSelectedIndex = 0;
                    offerPendingIDs.Clear();
                    activePopup = PopupMode.Offer;
                }
                DrawMap();
            }

            // Trade cancelled by other party
            if (pi == MessageTypeGameMode.OfferCanceled)
            {
                Log("SYS", "Trade offer cancelled by other party.");
                if (activePopup == PopupMode.Offer)
                {
                    activePopup = PopupMode.None;
                    popupJustClosed = true;
                    offerPendingIDs.Clear();
                }
                DrawMap();
            }
        }

        // ── Layout ──────────────────────────────────────────────────────────────

        private void DrawTuiLayout()
        {
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
             if (w < 85 || h < 10) return;

             lock (consoleLock)
             {
                 DrawChrome(w, h);
                 DrawStats();
                 DrawRoomInfo();
                 DrawBuffs();
                 DrawLog();
                 DrawInputField();
                 DrawMap();
             }
         }

         private void DrawChrome(int w = 0, int h = 0)
         {
             if (w == 0) w = Console.WindowWidth;
             if (h == 0) h = Console.WindowHeight;
             if (w < 85 || h < 10) return;

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

             // Input field row at h-2
             Console.SetCursorPosition(0, h - 2);
             Console.Write("║");
             Console.SetCursorPosition(79, h - 2);
             Console.Write("║ ╚" + new string('═', Math.Max(0, w - 84)) + "╝");

             // Hint bar at h-1
             Console.SetCursorPosition(0, h - 1);
             string hints = " [Enter]Chat [WASD]Move [C]harSheet [F]ight [T]arget [R]un [+/-]Zoom [Q]uit";
             Console.Write(SafeLine("╚" + hints.PadRight(78, '═') + "╝", w - 1));
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

        /// <summary>
        /// Draws a single stat row as a full-width color bar across cols 1–14.
        /// The text itself is the bar: filled portion has bar bg + black fg,
        /// empty portion has dark gray bg + white fg. Split point tracks pct.
        /// </summary>
        private void DrawStatBar(int row, string label,
            int cur, int max,
            ConsoleColor barFull, ConsoleColor barEmpty,
            ConsoleColor flashFull, ConsoleColor flashEmpty,
            bool flashing)
        {
            const int COL   = 1;
            const int WIDTH = 14;  // cols 1..14 inclusive

            ConsoleColor fillBg  = flashing ? flashFull  : barFull;
            ConsoleColor emptyBg = flashing ? flashEmpty : ConsoleColor.DarkGray;

            // Build the 14-char content string: "HP 100/100    "
            string valStr = max > 0 ? $"{cur,3}/{max,-3}" : "---/---";
            string content = $"{label} {valStr}".PadRight(WIDTH);
            if (content.Length > WIDTH) content = content[..WIDTH];

            float pct    = (max > 0) ? Math.Clamp((float)cur / max, 0f, 1f) : 0f;
            int   filled = (int)Math.Round(pct * WIDTH);

            Console.SetCursorPosition(COL, row);

            // Filled portion — bar color bg, black text
            if (filled > 0)
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = fillBg;
                Console.Write(content[..filled]);
            }

            // Empty portion — dark gray bg, white text
            if (filled < WIDTH)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.BackgroundColor = emptyBg;
                Console.Write(content[filled..]);
            }

            Console.ResetColor();
        }

        public void DrawStats()
        {
            if (!HasTty) return;
            lock (consoleLock)
            {
                var cond = Data.AvatarCondition;
                var hp   = cond.GetItemByNum(1);
                var mp   = cond.GetItemByNum(2);
                var vg   = cond.GetItemByNum(3);

                // Advance flash
                bool flashing = hpFlashFrames > 0;
                if (flashing)
                {
                    hpFlashPhase = !hpFlashPhase;
                    hpFlashFrames--;
                }

                // HP — red bar, flashes White→Red on damage
                DrawStatBar(1, "HP",
                    hp != null ? (int)hp.ValueCurrent : 0,
                    hp != null ? (int)hp.ValueMaximum : 0,
                    barFull:   ConsoleColor.DarkRed,
                    barEmpty:  ConsoleColor.DarkGray,
                    flashFull: hpFlashPhase ? ConsoleColor.White : ConsoleColor.Red,
                    flashEmpty:ConsoleColor.DarkGray,
                    flashing:  flashing);

                // MP — blue bar
                DrawStatBar(2, "MP",
                    mp != null ? (int)mp.ValueCurrent : 0,
                    mp != null ? (int)mp.ValueMaximum : 0,
                    barFull:   ConsoleColor.DarkBlue,
                    barEmpty:  ConsoleColor.DarkGray,
                    flashFull: ConsoleColor.DarkBlue,
                    flashEmpty:ConsoleColor.DarkGray,
                    flashing:  false);

                // VG — green bar
                DrawStatBar(3, "VG",
                    vg != null ? (int)vg.ValueCurrent : 0,
                    vg != null ? (int)vg.ValueMaximum : 0,
                    barFull:   ConsoleColor.DarkGreen,
                    barEmpty:  ConsoleColor.DarkGray,
                    flashFull: ConsoleColor.DarkGreen,
                    flashEmpty:ConsoleColor.DarkGray,
                    flashing:  false);

                // Unbound energy and training points from AvatarAttributes (matched by name)
                var unboundStat  = Data.AvatarAttributes.FirstOrDefault(s =>
                    s.ResourceName.IndexOf("unbound", StringComparison.OrdinalIgnoreCase) >= 0);
                var trainingStat = Data.AvatarAttributes.FirstOrDefault(s =>
                    s.ResourceName.IndexOf("train", StringComparison.OrdinalIgnoreCase) >= 0);
                int unboundVal  = unboundStat  != null ? (int)unboundStat.ValueCurrent  : -1;
                int trainingVal = trainingStat != null ? (int)trainingStat.ValueCurrent : -1;

                // RTT
                Console.SetCursorPosition(17, 3);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"RTT:{ServerConnection.RTT,-3}  ");

                // RST / STD modal badge
                if (Data.IsResting)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write("RST");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write("STD");
                }

                // Unbound energy
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  ");
                if (unboundVal >= 0)
                    Console.Write($"UNB:{unboundVal,-5}");
                else
                    Console.Write("          ");

                // Training points
                if (trainingVal >= 0)
                    Console.Write($"TRN:{trainingVal,-3}");
                else
                    Console.Write("       ");

                Console.ResetColor();
            }
        }

        public void DrawRoomInfo()
        {
            if (!HasTty) return;
            var ri = Data.RoomInformation;

            lock (consoleLock)
            {
                // Room Name: fills cols 17-57 (between the ╦ borders at 15 and 59)
                Console.SetCursorPosition(17, 1);
                Console.ForegroundColor = ConsoleColor.White;
                string roomName = ri?.RoomName ?? "Unknown Room";
                if (roomName.Length > 41) roomName = roomName[..38] + "...";
                Console.Write($"{roomName,-41}");
                Console.ResetColor();
            }
        }

        // ── Buff bar ──────────────────────────────────────────────────────────────

        // Maps buff Name (English, lowercase) → (abbreviation, fg, bg)
        // School colors: Kraanan=DarkBlue/Navy  Qor=Black/DarkGray  Faren=Red/DarkRed
        //                Jala=Green  Shalille=White/Gray  Riija=Magenta
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolKraanan  = (ConsoleColor.White,   ConsoleColor.DarkBlue);
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolQor      = (ConsoleColor.Gray,    ConsoleColor.DarkGray);
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolFaren    = (ConsoleColor.White,   ConsoleColor.DarkRed);
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolJala     = (ConsoleColor.Black,   ConsoleColor.Green);
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolShalille = (ConsoleColor.Black,   ConsoleColor.Gray);
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolRiija    = (ConsoleColor.White,   ConsoleColor.DarkMagenta);
        private static readonly (ConsoleColor fg, ConsoleColor bg) SchoolUnknown  = (ConsoleColor.Black,   ConsoleColor.DarkGray);

        private static readonly Dictionary<string, (string abbr, ConsoleColor fg, ConsoleColor bg)> BuffInfo
            = new(StringComparer.OrdinalIgnoreCase)
        {
            // Kraanan
            { "bless",                    ("BL",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "super strength",           ("SS",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "haste",                    ("HS",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "free action",              ("FA",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "deflect",                  ("DF",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "magic shield",             ("MS",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "armor of gort",            ("AG",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "night vision",             ("NV",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "eagle eyes",               ("EE",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "detect invisible",         ("DI",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "greater detect invisible", ("GD",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "resist magic",             ("RM",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            { "resist poison",            ("RP",  SchoolKraanan.fg,  SchoolKraanan.bg) },
            // Qor
            { "invisibility",             ("IN",  SchoolQor.fg,      SchoolQor.bg) },
            { "cloak",                    ("CL",  SchoolQor.fg,      SchoolQor.bg) },
            { "shadow form",              ("SF",  SchoolQor.fg,      SchoolQor.bg) },
            { "acid touch",               ("AT",  SchoolQor.fg,      SchoolQor.bg) },
            { "unholy touch",             ("UT",  SchoolQor.fg,      SchoolQor.bg) },
            { "kara'hol's curse",         ("KC",  SchoolQor.fg,      SchoolQor.bg) },
            { "gaze of the basilisk",     ("GB",  SchoolQor.fg,      SchoolQor.bg) },
            { "death link",               ("DL",  SchoolQor.fg,      SchoolQor.bg) },
            { "detect good",              ("DG",  SchoolQor.fg,      SchoolQor.bg) },
            { "unholy resolve",           ("UR",  SchoolQor.fg,      SchoolQor.bg) },
            // Faren
            { "touch of flame",           ("TF",  SchoolFaren.fg,    SchoolFaren.bg) },
            { "icy fingers",              ("IF",  SchoolFaren.fg,    SchoolFaren.bg) },
            { "zap",                      ("ZP",  SchoolFaren.fg,    SchoolFaren.bg) },
            { "withstand fire",           ("WF",  SchoolFaren.fg,    SchoolFaren.bg) },
            { "resist cold",              ("RC",  SchoolFaren.fg,    SchoolFaren.bg) },
            { "resist shock",             ("RS",  SchoolFaren.fg,    SchoolFaren.bg) },
            { "mana focus",               ("MF",  SchoolFaren.fg,    SchoolFaren.bg) },
            // Shalille
            { "holy touch",               ("HT",  SchoolShalille.fg, SchoolShalille.bg) },
            { "resist acid",              ("RA",  SchoolShalille.fg, SchoolShalille.bg) },
            { "holy resolve",             ("HR",  SchoolShalille.fg, SchoolShalille.bg) },
            { "detect evil",              ("DE",  SchoolShalille.fg, SchoolShalille.bg) },
            // Riija
            { "denial",                   ("DN",  SchoolRiija.fg,    SchoolRiija.bg) },
            { "anonymity",                ("AN",  SchoolRiija.fg,    SchoolRiija.bg) },
            { "eavesdrop",                ("EV",  SchoolRiija.fg,    SchoolRiija.bg) },
        };

        private static (string abbr, ConsoleColor fg, ConsoleColor bg) GetBuffInfo(string name)
        {
            if (name == null) return ("??", SchoolUnknown.fg, SchoolUnknown.bg);
            if (BuffInfo.TryGetValue(name, out var info)) return info;
            // Unknown buff — use first 2 chars, unknown color
            string abbr = name.Length >= 2 ? name[..2].ToUpper() : name.ToUpper().PadRight(2);
            return (abbr, SchoolUnknown.fg, SchoolUnknown.bg);
        }

        public void DrawBuffs()
        {
            if (!HasTty) return;
            lock (consoleLock)
            {
                // 3 rows × 19 cols at cols 60-78, rows 1-3
                // Each token "[AB]" = 4 chars → 4 tokens per row (16 used) + 3 blank
                const int COL       = 60;
                const int ROW_START = 1;
                const int ROWS      = 3;
                const int ROW_W     = 19;
                const int TOKEN_W   = 4;
                const int PER_ROW   = ROW_W / TOKEN_W; // 4

                var tokens = Data.AvatarBuffs.ToList()
                    .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => GetBuffInfo(g.Key))
                    .ToList();

                for (int row = 0; row < ROWS; row++)
                {
                    Console.SetCursorPosition(COL, ROW_START + row);
                    int written = 0;
                    for (int slot = 0; slot < PER_ROW; slot++)
                    {
                        int idx = row * PER_ROW + slot;
                        if (idx < tokens.Count)
                        {
                            var (abbr, fg, bg) = tokens[idx];
                            Console.ForegroundColor = fg;
                            Console.BackgroundColor = bg;
                            Console.Write($"[{abbr,2}]");
                        }
                        else
                        {
                            Console.ResetColor();
                            Console.Write("    "); // blank slot
                        }
                        written += TOKEN_W;
                    }
                    // Pad remaining 3 chars (19 - 16)
                    Console.ResetColor();
                    Console.Write(new string(' ', ROW_W - written));
                }
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

                            if (entry.IsContinuation)
                            {
                                // No timestamp/type — indent to align with content column of the
                                // previous first-line.  We don't know the parent type width here,
                                // so use a fixed indent matching the minimum prefix (time+space = 9).
                                int indent = 9; // matches time(8)+space(1); type omitted
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.Write(new string(' ', indent));
                                Console.ResetColor();
                                int contentW = Math.Max(1, logWidth - indent);
                                if (entry.Styles != null)
                                    WriteStyledString(entry.Text, entry.Styles, contentW);
                                else
                                    Console.Write(SafeLine(entry.Text.PadRight(contentW), contentW));
                            }
                            else
                            {
                            // hh:mm:ss (8 chars) + space = 9 chars
                            string timeStr = entry.Timestamp.ToString("HH:mm:ss");
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write($"{timeStr} ");

                            switch (entry.Type)
                            {
                                case "CHAT":  Console.ForegroundColor = ConsoleColor.Green; break;
                                case "EMOTE": Console.ForegroundColor = ConsoleColor.Yellow; break;
                                case "SYS":   Console.ForegroundColor = ConsoleColor.Magenta; break;
                                case "NET":   Console.ForegroundColor = ConsoleColor.DarkGray; break;
                                case "RECV":  Console.ForegroundColor = ConsoleColor.Yellow; break;
                                case "ERROR": Console.ForegroundColor = ConsoleColor.Red; break;
                                case "DEBUG": Console.ForegroundColor = ConsoleColor.DarkMagenta; break;
                                default:      Console.ForegroundColor = ConsoleColor.White; break;
                            }
                            // Type with single trailing space — no fixed padding
                            Console.Write($"{entry.Type} ");
                            Console.ResetColor();

                            // Content fills remaining width
                            int usedCols = 9 + entry.Type.Length + 1; // time(9) + type + space
                            int contentW = Math.Max(1, logWidth - usedCols);
                            if (entry.Styles != null)
                                WriteStyledString(entry.Text, entry.Styles, contentW);
                            else
                                Console.Write(SafeLine(entry.Text.PadRight(contentW), contentW));
                            }
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

            // time(9) + longest-type("ERROR"=5) + space(1) = 15; content width = 78 - 15 = 63
            const int avail = 63;
            string sanitized = Text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

            // Chat tab shows only CHAT/EMOTE/SYS/ERROR/REC; everything else goes to network tab
            var chatTypes = new HashSet<string> { "CHAT", "EMOTE", "SYS", "ERROR", "REC" };
            var buffer = chatTypes.Contains(Type) ? logBuffer : netBuffer;

            lock (logLock)
            {
                bool isFirst = true;
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
                    {
                        buffer.Add(new LogEntry(Type, chunk.TrimEnd(), null, 0, !isFirst));
                        isFirst = false;
                    }
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

        /// <summary>Like Log() but preserves ChatStyle list for colored rendering in DrawLog.</summary>
        private void LogStyled(string type, string text, List<ChatStyle> styles)
        {
            if (styles == null) { Log(type, text); return; }

            const int avail = 63;
            var chatTypes = new HashSet<string> { "CHAT", "EMOTE", "SYS", "ERROR", "REC" };
            var buffer = chatTypes.Contains(type) ? logBuffer : netBuffer;

            // Word-wrap; carry styles along with each chunk via offset
            string remaining = text;
            int offset = 0;
            lock (logLock)
            {
                bool isFirst = true;
                while (remaining.Length > 0)
                {
                    string chunk;
                    int chunkLen;
                    if (remaining.Length <= avail)
                    {
                        chunk = remaining;
                        chunkLen = remaining.Length;
                        remaining = "";
                    }
                    else
                    {
                        int cut = remaining.LastIndexOf(' ', avail);
                        if (cut <= 0) cut = avail;
                        chunk = remaining[..cut].TrimEnd();
                        chunkLen = cut;
                        remaining = remaining[cut..].TrimStart();
                    }

                    if (!string.IsNullOrWhiteSpace(chunk))
                    {
                        // Slice styles that overlap this chunk, adjusted to chunk-local indices
                        var chunkStyles = new List<ChatStyle>();
                        foreach (var s in styles)
                        {
                            int sStart = s.StartIndex - offset;
                            int sEnd   = sStart + s.Length;
                            if (sEnd <= 0 || sStart >= chunk.Length) continue;
                            int clampStart = Math.Max(0, sStart);
                            int clampLen   = Math.Min(chunk.Length, sEnd) - clampStart;
                            if (clampLen <= 0) continue;
                            chunkStyles.Add(new ChatStyle(clampStart, clampLen, s.IsBold, s.IsCursive, s.IsUnderline, s.IsStrikeout, s.IsLink, s.Color));
                        }
                        buffer.Add(new LogEntry(type, chunk.TrimEnd(), chunkStyles.Count > 0 ? chunkStyles : null, 0, !isFirst));
                        isFirst = false;
                    }
                    offset += chunkLen;
                }

                if (buffer.Count > LOG_CAPACITY) buffer.RemoveAt(0);
            }

            if (HasTty)
            {
                bool shouldRedraw = false;
                if (activeTab == LogTab.Chat && buffer == logBuffer && scrollOffset == 0) shouldRedraw = true;
                if (activeTab == LogTab.Network && buffer == netBuffer && netScrollOffset == 0) shouldRedraw = true;
                if (shouldRedraw) DrawLog();
            }
        }

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

                // Draw Map Header: MapStatus left, ATK/TGT right-justified
                Console.SetCursorPosition(startX, 1);
                var mapAvatar = Data.AvatarObject;
                int availW = width - 1;
                string mapStatus = renderer.MapStatus;

                // ATK/DRAIN/TGT right-justified in the same row
                string tgtName2 = Data.TargetObject?.Name ?? "";
                string atkStr2 = autoAttack ? "[ATK:ON]" : "[ATK:OFF]";
                string drainStr = drainUnbound ? $"[DRAIN:{drainUnboundSpellName}]" : "";
                string statusRight = drainStr.Length > 0 ? $"{atkStr2} {drainStr}" : atkStr2;
                if (tgtName2.Length > 0) statusRight += $" TGT:{tgtName2}";
                // Truncate if needed so it doesn't eat the whole row
                int maxRight = availW / 2;
                if (statusRight.Length > maxRight) statusRight = statusRight[..maxRight];

                string headerLine;
                int leftW2 = availW - statusRight.Length;
                string leftPart2 = mapStatus.Length > leftW2 ? mapStatus[..leftW2] : mapStatus.PadRight(leftW2);
                headerLine = leftPart2 + statusRight;

                // Override right portion color: red if attacking, cyan if draining, gray otherwise
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(leftPart2);
                Console.ForegroundColor = autoAttack ? ConsoleColor.Red : (drainUnbound ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
                Console.Write(statusRight);
                Console.ResetColor();

                if (activePopup != PopupMode.None)
                {
                    DrawPopup(startX, startY, width, height);
                }
                else
                {
                    if (popupJustClosed)
                    {
                        // Blank the entire console so no popup remnants survive in any panel.
                        // Then force a full redraw of every panel.
                        Console.BackgroundColor = ConsoleColor.Black;
                        Console.ForegroundColor = ConsoleColor.Gray;
                        string blankLine = new string(' ', Console.WindowWidth);
                        for (int row = 0; row < Console.WindowHeight - 1; row++)
                        {
                            Console.SetCursorPosition(0, row);
                            Console.Write(blankLine);
                        }
                        renderer.Invalidate();
                        popupJustClosed = false;

                        // Repaint all panels
                        DrawChrome();
                        DrawStats();
                        DrawRoomInfo();
                        DrawBuffs();
                        DrawLog();
                    }
                    renderer.Render(this, startX + 1, startY + 1, width - 2, height - 2);
                }

                if (showWhoList) DrawWhoList();
            }
        }

        private void DrawWhoList()
        {
            int mapStartX = 82;
            int mapStartY = 5;
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            int mapWidth  = w - mapStartX - 2;
            int mapHeight = h - 4 - mapStartY;
            if (mapWidth < 16 || mapHeight < 3) return;

            // Panel: right-edge is w-2 (border col), stop 1 before → rightmost content col = w-3
            // Width of overlay: fit longest entry up to 24 chars + 2 border cols
            const int maxNameW = 24;
            int panelW = maxNameW + 2; // including side borders

            // Clamp so we don't spill into the map left edge or past right border
            int rightEdge = w - 3;          // one col before the terminal right border
            int panelX = rightEdge - panelW + 1;
            if (panelX < mapStartX + 1) { panelW = rightEdge - mapStartX; panelX = mapStartX + 1; }
            int contentW = panelW - 2;

            // Build player list: deduplicate by ID (server sometimes sends duplicates), collect in-room names
            var seen = new HashSet<uint>();
            var players = Data.OnlinePlayers
                .Where(p => seen.Add(p.ID))
                .ToList();

            var roomNames = new HashSet<string>(
                Data.RoomObjects.Where(o => o.Flags.IsPlayer && !o.IsAvatar).Select(o => o.Name ?? ""),
                StringComparer.OrdinalIgnoreCase);

            int maxRows = mapHeight - 2; // leave row for header + bottom border
            int panelH = Math.Min(players.Count + 2, maxRows + 2); // header + entries + bottom
            int panelY = mapStartY + 1; // just inside the map top border

            // Draw panel — floats over the map (no lock needed, called from within DrawMap's lock)
            // Top border with title
            string title = $"─ WHO ({players.Count}) ";
            int titlePad = contentW - title.Length;
            if (titlePad < 0) { title = title[..contentW]; titlePad = 0; }
            Console.SetCursorPosition(panelX, panelY);
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write("┌" + title + new string('─', titlePad) + "┐");

            int entryRows = panelH - 2;
            for (int i = 0; i < entryRows; i++)
            {
                Console.SetCursorPosition(panelX, panelY + 1 + i);
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.BackgroundColor = ConsoleColor.Black;
                Console.Write("│");

                if (i < players.Count)
                {
                    var p = players[i];
                    bool inRoom = roomNames.Contains(p.Name ?? "");

                    // Name color by lawful status
                    ConsoleColor fg;
                    switch (p.Flags.Player)
                    {
                        case ObjectFlags.PlayerType.Killer: fg = ConsoleColor.Magenta;   break; // pink/red
                        case ObjectFlags.PlayerType.Outlaw: fg = ConsoleColor.DarkYellow; break; // orange
                        default:                             fg = ConsoleColor.White;      break; // lawful
                    }
                    ConsoleColor bg = inRoom ? ConsoleColor.DarkBlue : ConsoleColor.Black;

                    Console.ForegroundColor = fg;
                    Console.BackgroundColor = bg;
                    string name = (p.Name ?? "?");
                    if (name.Length > contentW) name = name[..contentW];
                    Console.Write(name.PadRight(contentW));
                }
                else
                {
                    Console.Write(new string(' ', contentW));
                }

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.BackgroundColor = ConsoleColor.Black;
                Console.Write("│");
            }

            // Bottom border
            Console.SetCursorPosition(panelX, panelY + panelH - 1);
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write("└" + new string('─', contentW) + "┘");

            Console.ResetColor();
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
                Data.SelfTarget = false;
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
                var skillColor = s.SkillPoints >= 99 ? ConsoleColor.Green
                    : s.SkillPoints >= 50 ? ConsoleColor.Yellow
                    : s.SkillPoints >= 20 ? ConsoleColor.DarkYellow
                    : ConsoleColor.Red;
                rows.Add(($"  {s.ResourceName,-26} {s.SkillPoints,2}%", skillColor));
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
                var spellColor = spell.SkillPoints >= 99 ? ConsoleColor.Green
                    : spell.SkillPoints >= 50 ? ConsoleColor.Yellow
                    : spell.SkillPoints >= 20 ? ConsoleColor.DarkYellow
                    : ConsoleColor.Red;
                rows.Add(($"  ✓ {spell.ResourceName,-26} {spell.SkillPoints,2}%", spellColor));
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

            var rows = new List<(string text, bool equipped)>();
            foreach (var obj in items)
            {
                bool equipped = obj.IsInUse;
                string line = obj.Count > 0
                    ? $"{obj.Name,-28} x{obj.Count,4}"
                    : $"{obj.Name}";
                rows.Add((line, equipped));
            }
            if (rows.Count == 0)
            {
                var empty = new List<(string, ConsoleColor)> { ("  (Empty)", ConsoleColor.DarkGray) };
                RenderScrollableRows(empty, contentX, contentY, contentW, contentH);
                return;
            }

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
                    bool sel      = idx == charSelectionIndex;
                    bool equipped = rows[idx].equipped;
                    // Colour scheme mirrors original clientd3d yellow-highlight for equipped items:
                    //   equipped + selected   → black on yellow
                    //   equipped + unselected → white on dark yellow
                    //   normal  + selected    → black on gray
                    //   normal  + unselected  → gray on black
                    Console.ForegroundColor = (sel && equipped) ? ConsoleColor.Black
                                            : (!sel && equipped) ? ConsoleColor.White
                                            : sel                ? ConsoleColor.Black
                                                                 : ConsoleColor.Gray;
                    Console.BackgroundColor = (sel && equipped) ? ConsoleColor.Yellow
                                            : (!sel && equipped) ? ConsoleColor.DarkYellow
                                            : sel                ? ConsoleColor.Gray
                                                                 : ConsoleColor.Black;
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
                                charTab == CharTab.Inventory ? " [Esc:Close] [←/→:Tab] [↑↓:Select] [Enter:Use] [D:Drop] [L:Look] " :
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

        // ── Login dialog ─────────────────────────────────────────────────────────
        private void DrawLoginDialog(int startX, int startY, int width, int height)
        {
            int w = Console.WindowWidth, h = Console.WindowHeight;
            // Center a fixed-size box
            int dlgW = Math.Min(50, w - 4);
            int dlgH = 9;
            int dlgX = (w - dlgW) / 2;
            int dlgY = (h - dlgH) / 2;

            Console.SetCursorPosition(dlgX, dlgY);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string title = " Meridian 59 Login ";
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            string footer = " [Tab:Switch] [Enter:Login] [Esc:Quit] ";
            Console.Write("╚" + new string('═', (dlgW - 2 - footer.Length) / 2) + footer + new string('═', dlgW - 2 - footer.Length - (dlgW - 2 - footer.Length) / 2) + "╝");
            Console.ResetColor();

            int cx = dlgX + 2, cw = dlgW - 4;
            // Username row
            Console.SetCursorPosition(cx, dlgY + 2);
            Console.ForegroundColor = loginField == LoginField.Username ? ConsoleColor.White : ConsoleColor.Gray;
            Console.Write("Username: ");
            string udisp = loginUsername.Length > cw - 10 ? loginUsername[^(cw - 10)..] : loginUsername;
            Console.Write((udisp + (loginField == LoginField.Username ? "_" : " ")).PadRight(cw - 10));
            // Password row
            Console.SetCursorPosition(cx, dlgY + 4);
            Console.ForegroundColor = loginField == LoginField.Password ? ConsoleColor.White : ConsoleColor.Gray;
            Console.Write("Password: ");
            string pmask = new string('*', loginPassword.Length);
            string pdisp = pmask.Length > cw - 10 ? pmask[^(cw - 10)..] : pmask;
            Console.Write((pdisp + (loginField == LoginField.Password ? "_" : " ")).PadRight(cw - 10));
            Console.ResetColor();

            // Status hint
            Console.SetCursorPosition(cx, dlgY + 6);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("Enter credentials then press Enter to connect.".PadRight(cw));
            Console.ResetColor();
        }

        // ── Buy popup ─────────────────────────────────────────────────────────────
        private void DrawBuyPopup(int startX, int startY, int width, int height)
        {
            var buy = Data.Buy;
            var items = buy?.Items?.ToList() ?? new List<Meridian59.Data.Models.TradeOfferObject>();
            string vendor = buy?.TradePartner?.Name ?? "Merchant";

            int dlgW = Math.Min(64, Console.WindowWidth - 4);
            int dlgH = Math.Min(items.Count + 9, Console.WindowHeight - 4);
            int dlgX = (Console.WindowWidth - dlgW) / 2;
            int dlgY = Math.Max(1, (Console.WindowHeight - dlgH) / 2);

            buySelectedIndex = Math.Clamp(buySelectedIndex, 0, Math.Max(0, items.Count - 1));

            // Frame
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.SetCursorPosition(dlgX, dlgY);
            string title = $" Buy from {vendor} ";
            if (title.Length > dlgW - 4) title = title[..(dlgW - 4)];
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            string footer = buyQuantityItemIndex >= 0
                ? " Type qty + Enter to confirm, Esc to cancel "
                : " [↑↓:Select] [Enter:Buy] [Esc:Close] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) { footer = footer[..(dlgW - 2)]; fp = 0; }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            // Shillings line
            Console.ForegroundColor = ConsoleColor.Yellow;
            string moneyStr = $" You have: {Data.Money} shillings ";
            if (moneyStr.Length > dlgW - 2) moneyStr = moneyStr[..(dlgW - 2)];
            Console.SetCursorPosition(dlgX + 1, dlgY + 1);
            Console.Write(moneyStr.PadRight(dlgW - 2));
            Console.ResetColor();

            // Column headers
            int cx = dlgX + 2, cw = dlgW - 4;
            Console.SetCursorPosition(cx, dlgY + 2);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  {"Item",-32} {"Price",8}".PadRight(cw));
            Console.ResetColor();

            // Separator
            Console.SetCursorPosition(dlgX, dlgY + 3);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("╠" + new string('═', dlgW - 2) + "╣");
            Console.ResetColor();

            // Item rows
            int listH = dlgH - 5;
            int scrollOffset = Math.Max(0, buySelectedIndex - listH + 1);
            for (int i = 0; i < listH && (i + scrollOffset) < items.Count; i++)
            {
                int idx = i + scrollOffset;
                var item = items[idx];
                bool sel = idx == buySelectedIndex;
                Console.SetCursorPosition(cx, dlgY + 4 + i);
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;

                string nameStr = item.Name ?? "?";
                if (nameStr.Length > 30) nameStr = nameStr[..30];

                string rowRight;
                if (sel && buyQuantityItemIndex == idx)
                    rowRight = $"qty:{buyQuantityBuffer}_";
                else
                    rowRight = $"{item.Price,8}g";

                string row = $"  {nameStr,-32} {rowRight}";
                Console.Write(row.PadRight(cw));
                Console.ResetColor();
            }
        }

        // ── Offer popup ───────────────────────────────────────────────────────────
        private void DrawOfferPopup(int startX, int startY, int width, int height)
        {
            var trade = Data.Trade;
            var inventory = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
            string partner = trade?.TradePartner?.Name ?? "Partner";

            int dlgW = Math.Min(64, Console.WindowWidth - 4);
            int dlgH = Math.Min(Console.WindowHeight - 4, 24);
            int dlgX = (Console.WindowWidth - dlgW) / 2;
            int dlgY = Math.Max(1, (Console.WindowHeight - dlgH) / 2);

            offerSelectedIndex = Math.Clamp(offerSelectedIndex, 0, Math.Max(0, inventory.Count - 1));

            // Frame
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.SetCursorPosition(dlgX, dlgY);
            string title = trade.IsBackgroundOffer ? $" Offer from {partner} " : $" Offer to {partner} ";
            if (title.Length > dlgW - 4) title = title[..(dlgW - 4)];
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            string footer = " [↑↓:Select] [Space:Stage] [A:Accept] [C:Cancel] [Esc:Close] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) { footer = footer[..(dlgW - 2)]; fp = 0; }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            int cx = dlgX + 2, cw = dlgW - 4;
            int half = (dlgH - 4) / 2;

            // Their offer (top half)
            Console.SetCursorPosition(cx, dlgY + 1);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  They offer:".PadRight(cw));
            Console.SetCursorPosition(dlgX, dlgY + 2);
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("╠" + new string('═', dlgW - 2) + "╣");
            Console.ResetColor();

            var theirItems = trade?.ItemsPartner?.ToList() ?? new List<Meridian59.Data.Models.ObjectBase>();
            for (int i = 0; i < half; i++)
            {
                Console.SetCursorPosition(cx, dlgY + 3 + i);
                Console.ForegroundColor = ConsoleColor.Cyan;
                if (i < theirItems.Count)
                    Console.Write($"  {theirItems[i].Name}".PadRight(cw));
                else
                    Console.Write(new string(' ', cw));
                Console.ResetColor();
            }

            // Separator
            int midY = dlgY + 3 + half;
            Console.SetCursorPosition(dlgX, midY);
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("╠" + new string('═', dlgW - 2) + "╣");
            Console.ResetColor();

            // Your inventory / staged offer (bottom half)
            Console.SetCursorPosition(cx, midY + 1);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  Your offer — Space to stage/unstage:".PadRight(cw));
            int invH = dlgH - 4 - half - 3;
            int scrollOffset = Math.Max(0, offerSelectedIndex - invH + 1);
            for (int i = 0; i < invH && (i + scrollOffset) < inventory.Count; i++)
            {
                int idx = i + scrollOffset;
                var item = inventory[idx];
                bool sel = idx == offerSelectedIndex;
                bool staged = offerPendingIDs.Contains(item.ID);
                Console.SetCursorPosition(cx, midY + 2 + i);
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = staged ? ConsoleColor.Green : (sel ? ConsoleColor.White : ConsoleColor.Gray);
                string check = staged ? "✓" : " ";
                Console.Write($"  {check} {item.Name}".PadRight(cw));
                Console.ResetColor();
            }
        }

        // ── Spell target picker ───────────────────────────────────────────────────
        private void DrawSpellTargetPopup(int startX, int startY, int width, int height)
        {
            var spell = Data.AvatarSpells.FirstOrDefault(s => s.ObjectID == spellTargetSpellID);
            string spellName = spell?.ResourceName ?? "Spell";

            var invItems  = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
            var roomItems = Data.RoomObjects.Where(o => o.ID != Data.AvatarID).ToList();
            var items     = spellTargetInventory ? invItems.Cast<Meridian59.Data.Models.ObjectBase>().ToList()
                                                 : roomItems.Cast<Meridian59.Data.Models.ObjectBase>().ToList();

            int dlgW = Math.Min(60, Console.WindowWidth - 4);
            int dlgH = Math.Min(items.Count + 8, Console.WindowHeight - 4);
            int dlgX = (Console.WindowWidth - dlgW) / 2;
            int dlgY = Math.Max(1, (Console.WindowHeight - dlgH) / 2);

            spellTargetSelectedIndex = Math.Clamp(spellTargetSelectedIndex, 0, Math.Max(0, items.Count - 1));

            // Frame
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.SetCursorPosition(dlgX, dlgY);
            string title = $" {spellName}: pick target ";
            if (title.Length > dlgW - 4) title = title[..(dlgW - 4)];
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            string footer = " [↑↓:Select] [Tab:Inv/Room] [Enter:Cast] [S:Self] [Esc:Cancel] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) { footer = footer[..(dlgW - 2)]; fp = 0; }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            // Source toggle line
            int cx = dlgX + 2, cw = dlgW - 4;
            Console.SetCursorPosition(cx, dlgY + 1);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            string srcLabel = spellTargetInventory ? "Source: [INVENTORY] / room" : "Source: inventory / [ROOM]";
            Console.Write(srcLabel.PadRight(cw));

            // Separator
            Console.SetCursorPosition(dlgX, dlgY + 2);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write("╠" + new string('═', dlgW - 2) + "╣");
            Console.ResetColor();

            // Item list
            int listH = dlgH - 4;
            int scrollOffset = Math.Max(0, spellTargetSelectedIndex - listH + 1);
            for (int i = 0; i < listH && (i + scrollOffset) < items.Count; i++)
            {
                int idx = i + scrollOffset;
                var item = items[idx];
                bool sel = idx == spellTargetSelectedIndex;
                Console.SetCursorPosition(cx, dlgY + 3 + i);
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;
                Console.Write($"  {item.Name}".PadRight(cw));
                Console.ResetColor();
            }
            if (items.Count == 0)
            {
                Console.SetCursorPosition(cx, dlgY + 3);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("  (no targets available)".PadRight(cw));
                Console.ResetColor();
            }
        }

        // ── Look List popup ───────────────────────────────────────────────────────
        private void DrawLookListPopup(int startX, int startY, int width, int height)
        {
            // Re-filter live so objects that left the room are removed
            var liveIDs = new HashSet<uint>(Data.RoomObjects.Select(o => o.ID));
            lookCandidates = lookCandidates.Where(o => liveIDs.Contains(o.ID)).ToList();
            if (lookCandidates.Count == 0) { activePopup = PopupMode.None; popupJustClosed = true; DrawMap(); return; }
            var candidates = lookCandidates;
            int dlgW = Math.Min(50, Console.WindowWidth - 4);
            int dlgH = Math.Min(candidates.Count + 6, Console.WindowHeight - 4);
            int dlgX = (Console.WindowWidth - dlgW) / 2;
            int dlgY = Math.Max(1, (Console.WindowHeight - dlgH) / 2);
            lookSelectedIndex = Math.Clamp(lookSelectedIndex, 0, Math.Max(0, candidates.Count - 1));

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.SetCursorPosition(dlgX, dlgY);
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string title = " Look at what? ";
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            string footer = " [↑↓:Select] [Enter:Look] [Esc:Cancel] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) { footer = footer[..(dlgW-2)]; fp = 0; }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            int listH = dlgH - 2;
            int scrollOffset = Math.Max(0, lookSelectedIndex - listH + 1);
            for (int i = 0; i < listH && (i + scrollOffset) < candidates.Count; i++)
            {
                int idx = i + scrollOffset;
                var obj = candidates[idx];
                bool sel = idx == lookSelectedIndex;
                Console.SetCursorPosition(dlgX + 2, dlgY + 1 + i);
                if (sel) Console.BackgroundColor = ConsoleColor.DarkCyan;
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;
                string name = obj.Name ?? "???";
                if (name.Length > dlgW - 4) name = name[..(dlgW - 4)];
                Console.Write(name.PadRight(dlgW - 4));
                Console.ResetColor();
            }
        }

        // ── Get List popup ────────────────────────────────────────────────────────
        private void DrawGetListPopup(int startX, int startY, int width, int height)
        {
            // Re-filter live against RoomObjects so picked-up items vanish immediately
            var liveIDs = new HashSet<uint>(Data.RoomObjects.Select(o => o.ID));
            getCandidates = getCandidates.Where(o => liveIDs.Contains(o.ID)).ToList();
            if (getCandidates.Count == 0) { activePopup = PopupMode.None; popupJustClosed = true; DrawMap(); return; }
            var candidates = getCandidates;
            int dlgW = Math.Min(52, Console.WindowWidth - 4);
            int dlgH = Math.Min(candidates.Count + 7, Console.WindowHeight - 4);
            int dlgX = (Console.WindowWidth - dlgW) / 2;
            int dlgY = Math.Max(1, (Console.WindowHeight - dlgH) / 2);
            getSelectedIndex = Math.Clamp(getSelectedIndex, 0, Math.Max(0, candidates.Count - 1));

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.SetCursorPosition(dlgX, dlgY);
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string title = " Get what? ";
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            string footer = " [↑↓:Select] [Enter:Get] [A:Get All] [Esc:Cancel] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) { footer = footer[..(dlgW-2)]; fp = 0; }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            int listH = dlgH - 2;
            int scrollOffset = Math.Max(0, getSelectedIndex - listH + 1);
            for (int i = 0; i < listH && (i + scrollOffset) < candidates.Count; i++)
            {
                int idx = i + scrollOffset;
                var obj = candidates[idx];
                bool sel = idx == getSelectedIndex;
                Console.SetCursorPosition(dlgX + 2, dlgY + 1 + i);
                if (sel) Console.BackgroundColor = ConsoleColor.DarkYellow;
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;
                string name = obj.Name ?? "???";
                if (name.Length > dlgW - 4) name = name[..(dlgW - 4)];
                Console.Write(name.PadRight(dlgW - 4));
                Console.ResetColor();
            }
        }

        // ── Look Detail popup (uses text panel width, left side) ──────────────────
        private void DrawLookDetailPopup(int startX, int startY, int width, int height)
        {
            string objName, desc, inscription;
            if (Data.LookPlayer.IsVisible)
            {
                var p = Data.LookPlayer;
                objName = p.ObjectBase?.Name ?? "Player";
                var parts = new System.Text.StringBuilder();
                string titles = p.Titles?.FullString ?? "";
                if (!string.IsNullOrWhiteSpace(titles)) parts.AppendLine(titles).AppendLine();
                string msg = p.Message?.FullString ?? "";
                if (!string.IsNullOrWhiteSpace(msg)) parts.AppendLine(msg).AppendLine();
                string web = p.Website ?? "";
                if (!string.IsNullOrWhiteSpace(web)) parts.Append("Web: ").AppendLine(web);
                desc = parts.ToString().TrimEnd();
                inscription = "";
            }
            else
            {
                var look = Data.LookObject;
                objName     = look?.ObjectBase?.Name ?? "Object";
                desc        = look?.Message?.FullString ?? "";
                inscription = (look?.LookType?.IsInscribed == true || look?.LookType?.IsEditable == true)
                    ? look?.Inscription?.FullString ?? "" : "";
            }

            // Use the left text panel area (cols 0..79, rows 1..height-3)
            int dlgX = 1;
            int dlgY = 1;
            int dlgW = Math.Min(78, Console.WindowWidth - 2);
            int dlgH = Console.WindowHeight - 3;

            // Word-wrap description into lines
            var lines = new List<string>();
            int contentW = dlgW - 4;
            foreach (var raw in (desc + (inscription.Length > 0 ? "\n\n" + inscription : "")).Split('\n'))
            {
                string remaining = raw.TrimEnd();
                if (remaining.Length == 0) { lines.Add(""); continue; }
                while (remaining.Length > contentW)
                {
                    int cut = remaining.LastIndexOf(' ', contentW);
                    if (cut <= 0) cut = contentW;
                    lines.Add(remaining[..cut].TrimEnd());
                    remaining = remaining[cut..].TrimStart();
                }
                if (remaining.Length > 0) lines.Add(remaining);
            }

            // Clamp to available height
            int maxLines = dlgH - 4;
            if (lines.Count > maxLines) lines = lines[..maxLines];

            // Resize dialog to content
            dlgH = Math.Min(lines.Count + 4, Console.WindowHeight - 3);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.SetCursorPosition(dlgX, dlgY);
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string titleStr = $" {objName} ";
            if (titleStr.Length > dlgW - 4) titleStr = titleStr[..(dlgW - 4)];
            Console.SetCursorPosition(dlgX + (dlgW - titleStr.Length) / 2, dlgY);
            Console.Write(titleStr);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            string footer2 = " [Esc/Enter:Close] ";
            int fp2 = dlgW - 2 - footer2.Length; if (fp2 < 0) { footer2 = footer2[..(dlgW-2)]; fp2 = 0; }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            Console.Write("╚" + new string('═', fp2 / 2) + footer2 + new string('═', fp2 - fp2 / 2) + "╝");
            Console.ResetColor();

            // Content lines
            for (int i = 0; i < lines.Count; i++)
            {
                Console.SetCursorPosition(dlgX + 2, dlgY + 1 + i);
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write(lines[i].PadRight(contentW));
                Console.ResetColor();
            }
        }

        // ── Character Select popup ────────────────────────────────────────────────
        private void DrawCharSelectPopup(int startX, int startY, int width, int height)
        {
            int dlgW = Math.Min(60, Console.WindowWidth - 4);
            int dlgH = Math.Min(charSelectList.Count + 7, Console.WindowHeight - 4);
            int dlgX = (Console.WindowWidth  - dlgW) / 2;
            int dlgY = (Console.WindowHeight - dlgH) / 2;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.SetCursorPosition(dlgX, dlgY);
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string title = " Select Character ";
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            string footer = " [↑↓:Select] [Enter:Play] [N:New] [Esc:Quit] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) { footer = footer[..(dlgW - 2)]; fp = 0; }
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            int cx = dlgX + 2, cw = dlgW - 4, cy = dlgY + 2;
            for (int i = 0; i < charSelectList.Count && cy + i < dlgY + dlgH - 1; i++)
            {
                var c = charSelectList[i];
                Console.SetCursorPosition(cx, cy + i);
                bool sel = i == popupSelectedIndex;
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = c.IsEmptySlot ? ConsoleColor.DarkGray : ConsoleColor.White;
                string label = c.IsEmptySlot ? $"  [ Empty Slot {i + 1} ]" : $"  {c.Name}";
                Console.Write(label.PadRight(cw));
                Console.ResetColor();
            }
        }

        // ── New Character popup ───────────────────────────────────────────────────
        // ── Stat Change (Ancient Trinket) Popup ─────────────────────────────────

        private static readonly (string Label, string Short)[] StatChangeStatLabels =
        {
            ("Might",     "MGT"), ("Intellect", "INT"), ("Stamina",   "STA"),
            ("Agility",   "AGL"), ("Mysticism", "MYS"), ("Aim",       "AIM"),
        };

        private static readonly (string Label, string Short)[] StatChangeSchoolLabels =
        {
            ("Shal'ille",   "SHA"), ("Qor",      "QOR"), ("Kraanan",    "KRA"),
            ("Faren",       "FAR"), ("Riija",     "RIJ"), ("Jala",       "JAL"),
            ("Weaponcraft", "WC "),
        };

        private void DrawStatChangePopup(int startX, int startY, int width, int height)
        {
            var sc = Data.StatChangeInfo;
            if (sc == null) return;

            int dlgW = Math.Min(72, Console.WindowWidth - 4);
            int dlgH = Math.Min(26, Console.WindowHeight - 4);
            int dlgX = (Console.WindowWidth  - dlgW) / 2;
            int dlgY = Math.Max(1, (Console.WindowHeight - dlgH) / 2);

            bool isConfirm = (activePopup == PopupMode.StatChangeConfirm);

            // Frame
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.SetCursorPosition(dlgX, dlgY);
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string title = isConfirm ? " Confirm Stat Change " : " Ancient Trinket — Redistribute Stats ";
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            string footer = isConfirm
                ? " [Enter:Confirm & Send] [Esc:Go Back] "
                : " [↑↓:Select] [Enter:Edit] [Tab:Review & Confirm] [Esc:Cancel] ";
            int fp = dlgW - 2 - footer.Length; if (fp < 0) fp = 0;
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            int cx = dlgX + 2;
            int cw = dlgW - 4;
            int cy = dlgY + 1;

            if (isConfirm)
            {
                DrawStatChangeConfirm(cx, cy, cw, dlgH - 2, sc);
            }
            else
            {
                DrawStatChangeEditor(cx, cy, cw, dlgH - 2, sc);
            }
        }

        private void DrawStatChangeEditor(int cx, int cy, int cw, int ch, Meridian59.Data.Models.StatChangeInfo sc)
        {
            // Column widths: label(12) value(4) bar(rest)
            int barW = Math.Max(8, cw - 20);

            // Section header: Stats
            Console.SetCursorPosition(cx, cy);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("── Stats ──────────────────────────────".PadRight(cw)[..cw]);
            Console.ResetColor();

            byte[] statVals = { sc.Might, sc.Intellect, sc.Stamina, sc.Agility, sc.Mysticism, sc.Aim };
            for (int i = 0; i < statVals.Length; i++)
            {
                int row = cy + 1 + i;
                if (row >= cy + ch - 4) break;
                Console.SetCursorPosition(cx, row);

                bool sel     = (statChangeSelectedIndex == i);
                bool editing = (statChangeEditingIndex == i);
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;

                string valStr = editing ? (statChangeEditBuffer + "_") : statVals[i].ToString();
                string bar    = editing ? "" : new string('█', Math.Min(barW, statVals[i] / 2));
                string row_s  = $"  {StatChangeStatLabels[i].Label,-12} {valStr,-5} {bar}";
                Console.Write(row_s.PadRight(cw)[..cw]);
                Console.ResetColor();
            }

            // Stats footer: points remaining
            uint avail = sc.AttributesAvailable;
            int footerRow = cy + 1 + statVals.Length;
            if (footerRow < cy + ch - 2)
            {
                Console.SetCursorPosition(cx, footerRow);
                Console.ForegroundColor = avail == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.Write($"  Points remaining: {avail,3}  (pool: {Meridian59.Data.Models.StatChangeInfo.ATTRIBUTE_MAXSUM})".PadRight(cw)[..cw]);
                Console.ResetColor();
            }

            // Section header: Schools
            int schoolHeaderRow = footerRow + 1;
            if (schoolHeaderRow < cy + ch - 1)
            {
                Console.SetCursorPosition(cx, schoolHeaderRow);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("── Schools (can only reduce) ──────────".PadRight(cw)[..cw]);
                Console.ResetColor();
            }

            byte[] schoolVals    = { sc.LevelSha, sc.LevelQor, sc.LevelKraanan, sc.LevelFaren, sc.LevelRiija, sc.LevelJala, sc.LevelWC };
            byte[] schoolOrigMax = { sc.OrigLevelSha, sc.OrigLevelQor, sc.OrigLevelKraanan, sc.OrigLevelFaren, sc.OrigLevelRiija, sc.OrigLevelJala, sc.OrigLevelWC };
            for (int i = 0; i < schoolVals.Length; i++)
            {
                int row = schoolHeaderRow + 1 + i;
                if (row >= cy + ch) break;
                Console.SetCursorPosition(cx, row);

                int listIdx  = 6 + i;  // schools start at row index 6
                bool sel     = (statChangeSelectedIndex == listIdx);
                bool editing = (statChangeEditingIndex == listIdx);
                bool locked  = (schoolOrigMax[i] == 0);  // never had this school

                if (locked)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"  {StatChangeSchoolLabels[i].Label,-12} --    (not learned)".PadRight(cw)[..cw]);
                }
                else
                {
                    if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                    Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;
                    string valStr = editing ? (statChangeEditBuffer + "_") : schoolVals[i].ToString();
                    string maxStr = $"/{schoolOrigMax[i]}";
                    string row_s  = $"  {StatChangeSchoolLabels[i].Label,-12} {valStr}{maxStr,-4}";
                    Console.Write(row_s.PadRight(cw)[..cw]);
                }
                Console.ResetColor();
            }
        }

        private void DrawStatChangeConfirm(int cx, int cy, int cw, int ch, Meridian59.Data.Models.StatChangeInfo sc)
        {
            byte[] statVals = { sc.Might, sc.Intellect, sc.Stamina, sc.Agility, sc.Mysticism, sc.Aim };
            byte[] schoolVals = { sc.LevelSha, sc.LevelQor, sc.LevelKraanan, sc.LevelFaren, sc.LevelRiija, sc.LevelJala, sc.LevelWC };
            byte[] schoolOrigMax = { sc.OrigLevelSha, sc.OrigLevelQor, sc.OrigLevelKraanan, sc.OrigLevelFaren, sc.OrigLevelRiija, sc.OrigLevelJala, sc.OrigLevelWC };

            int row = cy;
            Console.SetCursorPosition(cx, row++);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Your token will be consumed. Review changes:".PadRight(cw)[..cw]);
            Console.ResetColor();
            row++;

            Console.SetCursorPosition(cx, row++);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("Stats:".PadRight(cw)[..cw]);
            Console.ResetColor();

            for (int i = 0; i < statVals.Length; i++)
            {
                if (row >= cy + ch - 3) break;
                Console.SetCursorPosition(cx, row++);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($"  {StatChangeStatLabels[i].Label,-12} {statVals[i],3}".PadRight(cw)[..cw]);
            }
            Console.SetCursorPosition(cx, row++);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"  Points used: {sc.AttributesCurrent}/{Meridian59.Data.Models.StatChangeInfo.ATTRIBUTE_MAXSUM}".PadRight(cw)[..cw]);
            Console.ResetColor();
            row++;

            if (row < cy + ch - 1)
            {
                Console.SetCursorPosition(cx, row++);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("Schools:".PadRight(cw)[..cw]);
                Console.ResetColor();
            }

            for (int i = 0; i < schoolVals.Length; i++)
            {
                if (row >= cy + ch) break;
                if (schoolOrigMax[i] == 0) continue;
                Console.SetCursorPosition(cx, row++);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($"  {StatChangeSchoolLabels[i].Label,-12} {schoolVals[i]}/{schoolOrigMax[i]}".PadRight(cw)[..cw]);
            }

            if (row < cy + ch)
            {
                Console.SetCursorPosition(cx, row);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("  Int needed for schools: " + sc.IntellectNeeded);
                Console.ResetColor();
            }
        }

        private void AdjustStatChangeStat(Meridian59.Data.Models.StatChangeInfo sc, int idx, byte val)
        {
            switch (idx)
            {
                case 0: sc.Might     = val; break;
                case 1: sc.Intellect = val; break;
                case 2: sc.Stamina   = val; break;
                case 3: sc.Agility   = val; break;
                case 4: sc.Mysticism = val; break;
                case 5: sc.Aim       = val; break;
                case 6: sc.LevelSha     = val; break;
                case 7: sc.LevelQor     = val; break;
                case 8: sc.LevelKraanan = val; break;
                case 9: sc.LevelFaren   = val; break;
                case 10: sc.LevelRiija  = val; break;
                case 11: sc.LevelJala   = val; break;
                case 12: sc.LevelWC     = val; break;
            }
        }

        private static byte GetSchoolOrigMax(Meridian59.Data.Models.StatChangeInfo sc, int schoolIdx)
        {
            return schoolIdx switch
            {
                0 => sc.OrigLevelSha, 1 => sc.OrigLevelQor,     2 => sc.OrigLevelKraanan,
                3 => sc.OrigLevelFaren, 4 => sc.OrigLevelRiija, 5 => sc.OrigLevelJala,
                6 => sc.OrigLevelWC,  _ => 0,
            };
        }

        private void DrawNewCharPopup(int startX, int startY, int width, int height)
        {
            var info = Data.CharCreationInfo;
            if (info == null) return;

            int dlgW = Math.Min(70, Console.WindowWidth - 4);
            int dlgH = Console.WindowHeight - 4;
            int dlgX = (Console.WindowWidth  - dlgW) / 2;
            int dlgY = 2;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.SetCursorPosition(dlgX, dlgY);
            Console.Write("╔" + new string('═', dlgW - 2) + "╗");
            string title = " Create Character ";
            Console.SetCursorPosition(dlgX + (dlgW - title.Length) / 2, dlgY);
            Console.Write(title);
            for (int i = 1; i < dlgH - 1; i++)
            {
                Console.SetCursorPosition(dlgX, dlgY + i);
                Console.Write("║" + new string(' ', dlgW - 2) + "║");
            }
            Console.SetCursorPosition(dlgX, dlgY + dlgH - 1);
            string footer = newCharTab == NewCharTab.Stats
                ? " [Tab:NextTab] [←/→:Gender] [↑↓:Navigate] [Enter:Edit] [Esc:Cancel] "
                : newCharTab == NewCharTab.Looks
                ? " [Tab:NextTab] [↑↓:Preset] [Enter:Create] [Esc:Cancel] "
                : " [Tab:NextTab] [↑↓:Select] [Space:Toggle] [Enter:Create] [Esc:Cancel] ";
            if (footer.Length > dlgW - 2) footer = footer[..(dlgW - 2)];
            int fp = dlgW - 2 - footer.Length; if (fp < 0) fp = 0;
            Console.Write("╚" + new string('═', fp / 2) + footer + new string('═', fp - fp / 2) + "╝");
            Console.ResetColor();

            // Tab bar
            int tabX = dlgX + 1;
            var tabs = new[] { ("STATS", NewCharTab.Stats), ("SKILLS", NewCharTab.Skills), ("SPELLS", NewCharTab.Spells), ("LOOKS", NewCharTab.Looks) };
            Console.SetCursorPosition(tabX, dlgY + 1);
            foreach (var (lbl, tab) in tabs)
            {
                Console.ForegroundColor = tab == newCharTab ? ConsoleColor.White : ConsoleColor.DarkGray;
                Console.Write($"[{lbl}] ");
            }
            Console.ResetColor();

            // Name field — selected when newCharSelectionIndex == -1
            int cx = dlgX + 2, cw = dlgW - 4;
            Console.SetCursorPosition(cx, dlgY + 2);
            bool nameSelected = (newCharSelectionIndex == -1);
            if (nameSelected) Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = newCharNamingMode ? ConsoleColor.White : (nameSelected ? ConsoleColor.White : ConsoleColor.Gray);
            string nameLabel = "Name: ";
            string nameVal = newCharName + (newCharNamingMode ? "_" : (nameSelected && !newCharNamingMode ? " " : ""));
            Console.Write((nameLabel + nameVal).PadRight(cw));
            Console.ResetColor();

            // Separator
            Console.SetCursorPosition(dlgX, dlgY + 3);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("╠" + new string('═', dlgW - 2) + "╣");
            Console.ResetColor();

            int contentY = dlgY + 4;
            int contentH = dlgH - 6;

            if (newCharTab == NewCharTab.Stats)
                DrawNewCharStats(cx, contentY, cw, contentH, info);
            else if (newCharTab == NewCharTab.Skills)
                DrawNewCharAbilities(cx, contentY, cw, contentH, info, isSpells: false);
            else if (newCharTab == NewCharTab.Spells)
                DrawNewCharAbilities(cx, contentY, cw, contentH, info, isSpells: true);
            else
                DrawNewCharLooks(cx, contentY, cw, contentH, info);
        }

        private void DrawNewCharStats(int cx, int cy, int cw, int ch, Meridian59.Data.Models.CharCreationInfo info)
        {
            // Gender line (row index -1, always above stats)
            Console.SetCursorPosition(cx, cy);
            Console.ForegroundColor = ConsoleColor.Gray;
            string genderLine = $"Gender: {info.Gender,-8}  [← / → to change]";
            Console.Write(genderLine.PadRight(cw));

            // Stat rows
            var stats = new (string Label, uint Value)[] {
                ("Might",     info.Might),
                ("Intellect", info.Intellect),
                ("Stamina",   info.Stamina),
                ("Agility",   info.Agility),
                ("Mysticism", info.Mysticism),
                ("Aim",       info.Aim),
            };
            uint avail = info.AttributesAvailable;
            for (int i = 0; i < stats.Length && i < ch - 2; i++)
            {
                Console.SetCursorPosition(cx, cy + 2 + i);
                bool sel = (i == newCharSelectionIndex);
                bool editing = (i == newCharEditingStatIndex);
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;

                string valStr;
                if (editing)
                    valStr = newCharStatEditBuffer + "_";
                else
                    valStr = stats[i].Value.ToString();

                string bar = editing ? "" : new string('█', (int)(stats[i].Value / 2));
                string row = $"  {stats[i].Label,-12} {valStr,-6}  {bar,-25}";
                Console.Write(row.PadRight(cw));
                Console.ResetColor();
            }
            // Points remaining
            Console.SetCursorPosition(cx, cy + 2 + stats.Length + 1);
            Console.ForegroundColor = avail == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write($"  Points remaining: {avail,3}  (sum must be {Meridian59.Data.Models.CharCreationInfo.ATTRIBUTE_MAXSUM})".PadRight(cw));
            Console.ResetColor();
        }

        private void DrawNewCharAbilities(int cx, int cy, int cw, int ch, Meridian59.Data.Models.CharCreationInfo info, bool isSpells)
        {
            var available = isSpells
                ? info.Spells.Cast<object>().ToList()
                : info.Skills.Cast<object>().ToList();
            var selected  = isSpells
                ? info.SelectedSpells.Cast<object>().ToList()
                : info.SelectedSkills.Cast<object>().ToList();

            newCharSelectionIndex = Math.Clamp(newCharSelectionIndex, -1, Math.Max(0, available.Count - 1));

            // Scroll to keep selection visible — but scroll offset must never go negative.
            // When selection is -1 (name field), treat it as "above the list" and don't scroll.
            if (newCharSelectionIndex >= 0)
            {
                if (newCharSelectionIndex < newCharScrollOffset) newCharScrollOffset = newCharSelectionIndex;
                else if (newCharSelectionIndex >= newCharScrollOffset + ch) newCharScrollOffset = newCharSelectionIndex - ch + 1;
            }
            newCharScrollOffset = Math.Max(0, newCharScrollOffset);

            uint spLeft = info.SkillPointsAvailable;
            for (int i = 0; i < ch && (i + newCharScrollOffset) < available.Count; i++)
            {
                int idx = i + newCharScrollOffset;
                var item = available[idx];
                bool isSel = idx == newCharSelectionIndex;
                bool isPicked = selected.Contains(item);

                string name  = isSpells
                    ? ((Meridian59.Data.Models.AvatarCreatorSpellObject)item).SpellName
                    : ((Meridian59.Data.Models.AvatarCreatorSkillObject)item).SkillName;
                uint cost    = isSpells
                    ? ((Meridian59.Data.Models.AvatarCreatorSpellObject)item).SpellCost
                    : ((Meridian59.Data.Models.AvatarCreatorSkillObject)item).SkillCost;

                Console.SetCursorPosition(cx, cy + i);
                if (isSel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                Console.ForegroundColor = isPicked ? ConsoleColor.Green : ConsoleColor.Gray;
                string check = isPicked ? "✓" : " ";
                string row = $"  {check} {name,-30} cost:{cost,3}";
                Console.Write(row.PadRight(cw));
                Console.ResetColor();
            }
            // Points footer
            Console.SetCursorPosition(cx, cy + ch);
            Console.ForegroundColor = spLeft == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write($"  Skill points remaining: {spLeft,3}".PadRight(cw));
            Console.ResetColor();
        }

        // 5 named appearance presets (pokemon-ish, legally distinct).
        // Each tuple: (name, skinIdx, hairColorIdx, hairIdx, eyeIdx, noseIdx, mouthIdx)
        // Indices wrap to pool size at apply-time so they're always safe.
        private static readonly (string Name, string Desc, int Skin, int HairColor, int Hair, int Eye, int Nose, int Mouth)[] LooksPresets =
        {
            ("Torchic",  "Warm golden skin, auburn hair, wide curious eyes",    0, 2, 0, 0, 0, 1),
            ("Mudkip",   "Pale complexion, slate-blue hair, round features",    2, 4, 1, 2, 1, 0),
            ("Snorlix",  "Deep bronze tone, dark thick hair, heavy-lidded",     4, 0, 3, 3, 2, 2),
            ("Vaporeen", "Ashen skin, silver-white hair, angular features",     1, 5, 2, 1, 0, 3),
            ("Genblur",  "Olive complexion, jet-black hair, piercing gaze",     3, 1, 4, 4, 3, 1),
        };

        private void DrawNewCharLooks(int cx, int cy, int cw, int ch, Meridian59.Data.Models.CharCreationInfo info)
        {
            Console.SetCursorPosition(cx, cy);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write("Choose an appearance preset:".PadRight(cw));

            for (int i = 0; i < LooksPresets.Length && i < ch - 2; i++)
            {
                Console.SetCursorPosition(cx, cy + 2 + i);
                bool sel = (i == newCharLooksPreset);
                Console.ForegroundColor = sel ? ConsoleColor.White : ConsoleColor.Gray;
                if (sel) Console.BackgroundColor = ConsoleColor.DarkBlue;
                string check = sel ? "▶" : " ";
                string row = $"  {check} {LooksPresets[i].Name,-12}  {LooksPresets[i].Desc}";
                Console.Write(row.PadRight(cw));
                Console.ResetColor();
            }

            // Preview line
            var p = LooksPresets[newCharLooksPreset];
            Console.SetCursorPosition(cx, cy + 2 + LooksPresets.Length + 1);
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            string preview = $"  Selected: {p.Name} — {p.Desc}";
            if (preview.Length > cw) preview = preview[..cw];
            Console.Write(preview.PadRight(cw));
            Console.ResetColor();
        }

        private void DrawPopup(int startX, int startY, int width, int height)
        {
            if (activePopup == PopupMode.CharSheet)
            {
                DrawCharSheet(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.Login)
            {
                DrawLoginDialog(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.CharSelect)
            {
                DrawCharSelectPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.NewChar)
            {
                DrawNewCharPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.Buy)
            {
                DrawBuyPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.Offer)
            {
                DrawOfferPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.SpellTarget)
            {
                DrawSpellTargetPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.LookList)
            {
                DrawLookListPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.LookDetail)
            {
                DrawLookDetailPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.GetList)
            {
                DrawGetListPopup(startX, startY, width, height);
                return;
            }
            if (activePopup == PopupMode.StatChange || activePopup == PopupMode.StatChangeConfirm)
            {
                DrawStatChangePopup(startX, startY, width, height);
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
            // ── Login dialog ─────────────────────────────────────────────────────
            if (activePopup == PopupMode.Login)
            {
                if (newCharNamingMode) { /* not used here */ }
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        IsRunning = false;
                        break;
                    case ConsoleKey.Tab:
                        loginField = loginField == LoginField.Username ? LoginField.Password : LoginField.Username;
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (loginField == LoginField.Username)
                        {
                            loginField = LoginField.Password;
                            DrawMap();
                        }
                        else if (!string.IsNullOrEmpty(loginUsername) && !string.IsNullOrEmpty(loginPassword))
                        {
                            // Close popup and send credentials
                            activePopup = PopupMode.None;
                            DrawMap();
                            SendLoginMessage(loginUsername, loginPassword);
                        }
                        break;
                    case ConsoleKey.Backspace:
                        if (loginField == LoginField.Username && loginUsername.Length > 0)
                            loginUsername = loginUsername[..^1];
                        else if (loginField == LoginField.Password && loginPassword.Length > 0)
                            loginPassword = loginPassword[..^1];
                        DrawMap();
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            if (loginField == LoginField.Username) loginUsername += key.KeyChar;
                            else loginPassword += key.KeyChar;
                            DrawMap();
                        }
                        break;
                }
                return;
            }

            // ── Char Select ──────────────────────────────────────────────────────
            if (activePopup == PopupMode.CharSelect)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        IsRunning = false;
                        break;
                    case ConsoleKey.UpArrow:
                        popupSelectedIndex = Math.Max(0, popupSelectedIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        popupSelectedIndex = Math.Min(charSelectList.Count - 1, popupSelectedIndex + 1);
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (charSelectList.Count > 0)
                        {
                            var sel = charSelectList[popupSelectedIndex];
                            if (sel.IsEmptySlot)
                            {
                                // Request char creation info for this slot
                                activePopup = PopupMode.None;
                                charCreationState = CharCreationState.AwaitingCharInfo;
                                SendSystemMessageSendCharInfo(sel.ID);
                            }
                            else
                            {
                                activePopup = PopupMode.None;
                                popupJustClosed = true;
                                Data.ExpectedAvatarName = sel.Name;
                                SendUseCharacterMessage(new ObjectID(sel.ID), true, sel.Name);
                            }
                        }
                        break;
                    case ConsoleKey.N:
                        // Pick the first empty slot and start new char
                        var emptySlot = charSelectList.FirstOrDefault(c => c.IsEmptySlot);
                        if (emptySlot != null)
                        {
                            activePopup = PopupMode.None;
                            charCreationState = CharCreationState.AwaitingCharInfo;
                            SendSystemMessageSendCharInfo(emptySlot.ID);
                        }
                        else Log("SYS", "No empty character slots available.");
                        break;
                }
                return;
            }

            // ── Stat Change (Ancient Trinket) popup ──────────────────────────────
            if (activePopup == PopupMode.StatChange || activePopup == PopupMode.StatChangeConfirm)
            {
                var sc = Data.StatChangeInfo;

                // ── Confirm screen ──────────────────────────────────────────────
                if (activePopup == PopupMode.StatChangeConfirm)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            // Send it
                            SendChangedStatsMessage();
                            activePopup = PopupMode.None;
                            popupJustClosed = true;
                            Log("SYS", "Stat change submitted.");
                            DrawMap();
                            break;
                        case ConsoleKey.Escape:
                            activePopup = PopupMode.StatChange;
                            DrawMap();
                            break;
                    }
                    return;
                }

                // ── Inline digit-edit mode ──────────────────────────────────────
                if (statChangeEditingIndex >= 0)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            if (byte.TryParse(statChangeEditBuffer, out byte v))
                                AdjustStatChangeStat(sc, statChangeEditingIndex, v);
                            statChangeEditingIndex = -1;
                            statChangeEditBuffer   = "";
                            DrawMap();
                            break;
                        case ConsoleKey.Escape:
                            statChangeEditingIndex = -1;
                            statChangeEditBuffer   = "";
                            DrawMap();
                            break;
                        case ConsoleKey.Backspace:
                            if (statChangeEditBuffer.Length > 0) statChangeEditBuffer = statChangeEditBuffer[..^1];
                            DrawMap();
                            break;
                        default:
                            if (char.IsDigit(key.KeyChar) && statChangeEditBuffer.Length < 3)
                            { statChangeEditBuffer += key.KeyChar; DrawMap(); }
                            break;
                    }
                    return;
                }

                // ── Normal navigation ───────────────────────────────────────────
                const int TOTAL_ROWS = 13; // 6 stats + 7 schools
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        statChangeSelectedIndex = Math.Max(0, statChangeSelectedIndex - 1);
                        // Skip locked school rows (schools with origMax == 0)
                        while (statChangeSelectedIndex >= 6 && GetSchoolOrigMax(sc, statChangeSelectedIndex - 6) == 0 && statChangeSelectedIndex > 6)
                            statChangeSelectedIndex--;
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        statChangeSelectedIndex = Math.Min(TOTAL_ROWS - 1, statChangeSelectedIndex + 1);
                        while (statChangeSelectedIndex >= 6 && GetSchoolOrigMax(sc, statChangeSelectedIndex - 6) == 0 && statChangeSelectedIndex < TOTAL_ROWS - 1)
                            statChangeSelectedIndex++;
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        statChangeEditingIndex = statChangeSelectedIndex;
                        statChangeEditBuffer   = "";
                        DrawMap();
                        break;
                    case ConsoleKey.Tab:
                        // Advance to confirm screen
                        activePopup = PopupMode.StatChangeConfirm;
                        DrawMap();
                        break;
                }
                return;
            }

            // ── New Character popup ───────────────────────────────────────────────
            if (activePopup == PopupMode.NewChar)
            {
                var info = Data.CharCreationInfo;

                // ── Name typing mode ─────────────────────────────────────────────
                if (newCharNamingMode)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            newCharNamingMode = false;
                            info.AvatarName = newCharName;
                            DrawMap();
                            break;
                        case ConsoleKey.Escape:
                            newCharNamingMode = false;
                            DrawMap();
                            break;
                        case ConsoleKey.Backspace:
                            if (newCharName.Length > 0) newCharName = newCharName[..^1];
                            DrawMap();
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar)) { newCharName += key.KeyChar; DrawMap(); }
                            break;
                    }
                    return;
                }

                // ── Stat inline-edit mode ────────────────────────────────────────
                if (newCharEditingStatIndex >= 0)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            if (uint.TryParse(newCharStatEditBuffer, out uint v))
                                AdjustNewCharStatAbsolute(info, newCharEditingStatIndex, v);
                            newCharEditingStatIndex = -1;
                            newCharStatEditBuffer = "";
                            DrawMap();
                            break;
                        case ConsoleKey.Escape:
                            newCharEditingStatIndex = -1;
                            newCharStatEditBuffer = "";
                            DrawMap();
                            break;
                        case ConsoleKey.Backspace:
                            if (newCharStatEditBuffer.Length > 0) newCharStatEditBuffer = newCharStatEditBuffer[..^1];
                            DrawMap();
                            break;
                        default:
                            if (char.IsDigit(key.KeyChar) && newCharStatEditBuffer.Length < 3)
                            { newCharStatEditBuffer += key.KeyChar; DrawMap(); }
                            break;
                    }
                    return;
                }

                // ── Normal navigation ────────────────────────────────────────────
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        ResetCharCreation();
                        DrawMap();
                        break;
                    case ConsoleKey.Tab:
                        newCharTab = (NewCharTab)(((int)newCharTab + 1) % 4);
                        newCharSelectionIndex = -1;  // back to name field on tab switch
                        newCharScrollOffset = 0;
                        DrawMap();
                        break;
                    case ConsoleKey.LeftArrow:
                        if (newCharTab == NewCharTab.Stats)
                        {
                            info.SetExampleModel(Meridian59.Common.Enums.Gender.Male);
                            DrawMap();
                        }
                        break;
                    case ConsoleKey.RightArrow:
                        if (newCharTab == NewCharTab.Stats)
                        {
                            info.SetExampleModel(Meridian59.Common.Enums.Gender.Female);
                            DrawMap();
                        }
                        break;
                    case ConsoleKey.UpArrow:
                        if (newCharTab == NewCharTab.Looks)
                            newCharLooksPreset = Math.Max(0, newCharLooksPreset - 1);
                        else
                            newCharSelectionIndex = Math.Max(-1, newCharSelectionIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        if (newCharTab == NewCharTab.Looks)
                            newCharLooksPreset = Math.Min(LooksPresets.Length - 1, newCharLooksPreset + 1);
                        else
                            newCharSelectionIndex++;
                        DrawMap();
                        break;
                    case ConsoleKey.Spacebar:
                        if (newCharTab == NewCharTab.Skills) ToggleNewCharAbility(info, newCharSelectionIndex, false);
                        else if (newCharTab == NewCharTab.Spells) ToggleNewCharAbility(info, newCharSelectionIndex, true);
                        DrawMap();
                        break;
                    case ConsoleKey.F2:
                        newCharName = info.AvatarName ?? "";
                        newCharNamingMode = true;
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (newCharSelectionIndex == -1)
                        {
                            // Name field selected — open naming mode
                            newCharName = info.AvatarName ?? "";
                            newCharNamingMode = true;
                            DrawMap();
                        }
                        else if (newCharTab == NewCharTab.Stats && newCharSelectionIndex >= 0 && newCharSelectionIndex < 6)
                        {
                            // Open inline stat editor
                            newCharStatEditBuffer = GetStatValue(info, newCharSelectionIndex).ToString();
                            newCharEditingStatIndex = newCharSelectionIndex;
                            DrawMap();
                        }
                        else
                        {
                            SubmitNewCharacter(info);
                        }
                        break;
                }
                return;
            }

            // ── Buy popup ─────────────────────────────────────────────────────────
            if (activePopup == PopupMode.Buy)
            {
                var items = Data.Buy?.Items?.ToList() ?? new List<Meridian59.Data.Models.TradeOfferObject>();
                buySelectedIndex = Math.Clamp(buySelectedIndex, 0, Math.Max(0, items.Count - 1));

                // Quantity edit mode
                if (buyQuantityItemIndex >= 0)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            uint qty = 1;
                            if (!uint.TryParse(buyQuantityBuffer, out qty) || qty < 1) qty = 1;
                            var buyItem = items[buyQuantityItemIndex];
                            SendReqBuyItemsMessage(Data.Buy.TradePartner.ID,
                                new[] { new ObjectID(buyItem.ID, qty) });
                            Log("SYS", $"Buying {qty}x {buyItem.Name} for {buyItem.Price * qty}g");
                            buyQuantityItemIndex = -1;
                            buyQuantityBuffer = "";
                            DrawMap();
                            break;
                        case ConsoleKey.Escape:
                            buyQuantityItemIndex = -1;
                            buyQuantityBuffer = "";
                            DrawMap();
                            break;
                        case ConsoleKey.Backspace:
                            if (buyQuantityBuffer.Length > 0) buyQuantityBuffer = buyQuantityBuffer[..^1];
                            DrawMap();
                            break;
                        default:
                            if (char.IsDigit(key.KeyChar) && buyQuantityBuffer.Length < 4)
                            { buyQuantityBuffer += key.KeyChar; DrawMap(); }
                            break;
                    }
                    return;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        buySelectedIndex = Math.Max(0, buySelectedIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        buySelectedIndex = Math.Min(items.Count - 1, buySelectedIndex + 1);
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (items.Count > 0)
                        {
                            buyQuantityItemIndex = buySelectedIndex;
                            buyQuantityBuffer = "1";
                            DrawMap();
                        }
                        break;
                }
                return;
            }

            // ── Offer popup ───────────────────────────────────────────────────────
            if (activePopup == PopupMode.Offer)
            {
                var inventory = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
                offerSelectedIndex = Math.Clamp(offerSelectedIndex, 0, Math.Max(0, inventory.Count - 1));

                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        offerPendingIDs.Clear();
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        offerSelectedIndex = Math.Max(0, offerSelectedIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        offerSelectedIndex = Math.Min(inventory.Count - 1, offerSelectedIndex + 1);
                        DrawMap();
                        break;
                    case ConsoleKey.Spacebar:
                        if (inventory.Count > 0)
                        {
                            uint id = inventory[offerSelectedIndex].ID;
                            if (offerPendingIDs.Contains(id)) offerPendingIDs.Remove(id);
                            else offerPendingIDs.Add(id);
                            DrawMap();
                        }
                        break;
                    case ConsoleKey.Enter:
                    case ConsoleKey.O:
                        // Send the staged offer
                        if (offerPendingIDs.Count > 0)
                        {
                            var offerObjs = offerPendingIDs
                                .Select(id => new ObjectID(id, 1))
                                .ToArray();
                            if (Data.Trade.IsBackgroundOffer)
                                SendReqCounterOffer(offerObjs);
                            else
                            {
                                var partner = Data.Trade.TradePartner;
                                if (partner != null)
                                    SendReqOffer(new ObjectID(partner.ID), offerObjs);
                            }
                            Log("SYS", $"Offering {offerPendingIDs.Count} item(s).");
                            offerPendingIDs.Clear();
                            DrawMap();
                        }
                        break;
                    case ConsoleKey.A:
                        SendAcceptOffer();
                        Log("SYS", "Offer accepted.");
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        offerPendingIDs.Clear();
                        DrawMap();
                        break;
                    case ConsoleKey.C:
                        SendCancelOffer();
                        Log("SYS", "Offer cancelled.");
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        offerPendingIDs.Clear();
                        DrawMap();
                        break;
                }
                return;
            }

            // ── Spell target picker ───────────────────────────────────────────────
            if (activePopup == PopupMode.SpellTarget)
            {
                var invItems  = Data.InventoryObjects.ToList().OrderBy(o => o.Name).Cast<Meridian59.Data.Models.ObjectBase>().ToList();
                var roomItems = Data.RoomObjects.Where(o => o.ID != Data.AvatarID).Cast<Meridian59.Data.Models.ObjectBase>().ToList();
                var items     = spellTargetInventory ? invItems : roomItems;
                spellTargetSelectedIndex = Math.Clamp(spellTargetSelectedIndex, 0, Math.Max(0, items.Count - 1));

                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                    case ConsoleKey.Tab:
                        spellTargetInventory = !spellTargetInventory;
                        spellTargetSelectedIndex = 0;
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        spellTargetSelectedIndex = Math.Max(0, spellTargetSelectedIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        spellTargetSelectedIndex = Math.Min(items.Count - 1, spellTargetSelectedIndex + 1);
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (items.Count > 0)
                        {
                            var target = items[spellTargetSelectedIndex];
                            Data.SelfTarget = false;
                            Data.TargetID = target.ID;
                            SendReqCastMessage(spellTargetSpellID);
                            Log("SYS", $"Casting on: {target.Name}");
                            activePopup = PopupMode.None;
                            popupJustClosed = true;
                            DrawMap();
                        }
                        break;
                    case ConsoleKey.S:
                        // Cast on self
                        Data.SelfTarget = true;
                        SendReqCastMessage(spellTargetSpellID);
                        Log("SYS", $"Casting on self.");
                        Data.SelfTarget = false;
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                }
                return;
            }

            if (activePopup == PopupMode.LookList)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        lookSelectedIndex = Math.Max(0, lookSelectedIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        lookSelectedIndex = Math.Min(lookCandidates.Count - 1, lookSelectedIndex + 1);
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (lookCandidates.Count > 0)
                        {
                            var target = lookCandidates[lookSelectedIndex];
                            activePopup = PopupMode.None;
                            SendReqLookMessage(target.ID);
                            // LookDetail will open via PropertyChanged when server responds
                        }
                        break;
                }
                return;
            }

            if (activePopup == PopupMode.LookDetail)
            {
                // Any key closes it
                Data.LookObject.IsVisible = false;
                Data.LookPlayer.IsVisible = false;
                activePopup = PopupMode.None;
                popupJustClosed = true;
                DrawMap();
                return;
            }

            if (activePopup == PopupMode.GetList)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                    case ConsoleKey.UpArrow:
                        getSelectedIndex = Math.Max(0, getSelectedIndex - 1);
                        DrawMap();
                        break;
                    case ConsoleKey.DownArrow:
                        getSelectedIndex = Math.Min(getCandidates.Count - 1, getSelectedIndex + 1);
                        DrawMap();
                        break;
                    case ConsoleKey.Enter:
                        if (getCandidates.Count > 0)
                        {
                            var obj = getCandidates[getSelectedIndex];
                            SendReqGetMessage(new ObjectID(obj.ID));
                            if (recorder.IsRecording) recorder.RecordGet(Data.AvatarObject, obj.Name ?? obj.ID.ToString());
                            Log("SYS", $"Getting: {obj.Name}");
                            activePopup = PopupMode.None;
                            popupJustClosed = true;
                            DrawMap();
                        }
                        break;
                    case ConsoleKey.A:
                        // Get all
                        foreach (var obj in getCandidates)
                        {
                            SendReqGetMessage(new ObjectID(obj.ID));
                            if (recorder.IsRecording) recorder.RecordGet(Data.AvatarObject, obj.Name ?? obj.ID.ToString());
                        }
                        Log("SYS", $"Getting all {getCandidates.Count} item(s).");
                        activePopup = PopupMode.None;
                        popupJustClosed = true;
                        DrawMap();
                        break;
                }
                return;
            }

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
                                var chosen = spells[si].spell;
                                // Find the SpellObject to check TargetsCount
                                var spellObj = Data.SpellObjects.FirstOrDefault(s => s.ID == chosen.ObjectID);
                                if (spellObj != null && spellObj.TargetsCount > 0)
                                {
                                    // Open target picker
                                    spellTargetSpellID = chosen.ObjectID;
                                    spellTargetSelectedIndex = 0;
                                    spellTargetInventory = true;
                                    activePopup = PopupMode.SpellTarget;
                                    Log("SYS", $"Pick target for: {chosen.ResourceName}");
                                }
                                else
                                {
                                    SendReqCastMessage(chosen.ObjectID);
                                    Log("SYS", $"Casting: {chosen.ResourceName}");
                                }
                            }
                        }
                        else if (charTab == CharTab.Inventory)
                        {
                            var items = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
                            int ii = Math.Clamp(charSelectionIndex, 0, items.Count - 1);
                            if (items.Count > 0)
                            {
                                var item = items[ii];
                                if (item.IsInUse)
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

                    case ConsoleKey.D:
                        if (charTab == CharTab.Inventory)
                        {
                            var items = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
                            int ii = Math.Clamp(charSelectionIndex, 0, items.Count - 1);
                            if (items.Count > 0)
                            {
                                var item = items[ii];
                                // Don't drop equipped items without unequipping first
                                if (item.IsInUse)
                                {
                                    Log("SYS", $"Unequip {item.Name} first before dropping.");
                                }
                                else
                                {
                                    SendReqDropMessage(new ObjectID(item.ID));
                                    Log("SYS", $"Dropping: {item.Name}");
                                    // Move selection up if we were at the bottom
                                    if (charSelectionIndex >= items.Count - 1)
                                        charSelectionIndex = Math.Max(0, charSelectionIndex - 1);
                                    DrawMap();
                                }
                            }
                        }
                        break;

                    case ConsoleKey.L:
                        if (charTab == CharTab.Inventory)
                        {
                            var litems = Data.InventoryObjects.ToList().OrderBy(o => o.Name).ToList();
                            int li = Math.Clamp(charSelectionIndex, 0, litems.Count - 1);
                            if (litems.Count > 0)
                            {
                                var litem = litems[li];
                                Log("SYS", $"INV DBG: {litem.Name} IsInUse={litem.IsInUse}");
                                SendReqLookMessage(litem.ID);
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
                        if (activePopup == PopupMode.NewsList)
                            Data.NewsGroup.IsVisible = false;
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
            // Ctrl+Q always quits, regardless of mode
            if (key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Log("SYS", "Disconnecting...");
                ServerConnection.Disconnect("User quit command");
                IsRunning = false;
                return;
            }

            // Ctrl+W toggles the who list overlay
            if (key.Key == ConsoleKey.W && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                showWhoList = !showWhoList;
                if (showWhoList) SendSendPlayers();
                DrawMap();
                return;
            }

            // Ctrl+S toggles sound/beep
            if (key.Key == ConsoleKey.S && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                soundEnabled = !soundEnabled;
                Log("SYS", $"Sound {(soundEnabled ? "ON" : "OFF")}");
                return;
            }

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
                            // Add to history (deduplicate consecutive identical commands)
                            if (commandHistory.Count == 0 || commandHistory[^1] != inputBuffer)
                                commandHistory.Add(inputBuffer);
                            historyIndex = -1;
                            ProcessCommand(inputBuffer);
                            inputBuffer = "";
                        }
                        inputMode = false;
                        DrawInputField();
                        break;

                    case ConsoleKey.Escape:
                        inputBuffer = "";
                        historyIndex = -1;
                        inputMode = false;
                        DrawInputField();
                        break;

                    case ConsoleKey.UpArrow:
                        if (commandHistory.Count > 0)
                        {
                            if (historyIndex < 0)
                                historyIndex = commandHistory.Count - 1;
                            else if (historyIndex > 0)
                                historyIndex--;
                            inputBuffer = commandHistory[historyIndex];
                            DrawInputField();
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (historyIndex >= 0)
                        {
                            historyIndex++;
                            if (historyIndex >= commandHistory.Count)
                            {
                                historyIndex = -1;
                                inputBuffer = "";
                            }
                            else
                                inputBuffer = commandHistory[historyIndex];
                            DrawInputField();
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (inputBuffer.Length > 0)
                        {
                            inputBuffer = inputBuffer[..^1];
                            historyIndex = -1;
                            DrawInputField();
                        }
                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            inputBuffer += key.KeyChar;
                            historyIndex = -1;
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

        private void PerformLook()
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) { Log("SYS", "No avatar."); return; }

            // Forward cone filter for room objects.
            float facingRad = avatar.AngleUnits / 4096f * 2f * MathF.PI;
            const float VIEW_ARC  = MathF.PI / 2f;
            const float MAX_DIST  = 512f;

            var candidates = Data.RoomObjects
                .Where(o =>
                {
                    if (o.ID == Data.AvatarID) return false;
                    float dx = o.CoordinateX - avatar.CoordinateX;
                    float dz = o.CoordinateY - avatar.CoordinateY;
                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    if (dist > MAX_DIST) return false;
                    float angleToObj = MathF.Atan2(dz, dx);
                    return AngleDiff(angleToObj, facingRad) <= VIEW_ARC;
                })
                .OrderBy(o => {
                    float dx = o.CoordinateX - avatar.CoordinateX;
                    float dz = o.CoordinateY - avatar.CoordinateY;
                    return dx * dx + dz * dz;
                })
                .ToList();

            if (candidates.Count == 0)
            {
                Log("SYS", "Nothing nearby to look at.");
                return;
            }

            if (candidates.Count == 1)
            {
                SendReqLookMessage(candidates[0].ID);
                return;
            }

            lookCandidates    = candidates;
            lookSelectedIndex = 0;
            activePopup       = PopupMode.LookList;
            DrawMap();
        }

        private void PerformGet(bool getAll = false)
        {
            const int GET_RANGE = 384; // ~6 tiles in KOD units

            var avatar = Data.AvatarObject;
            if (avatar == null) { Log("SYS", "Not in game."); return; }

            var candidates = Data.RoomObjects
                .OfType<RoomObject>()
                .Where(o => {
                    if (!o.Flags.IsGettable) return false;
                    float dx = o.CoordinateX - avatar.CoordinateX;
                    float dz = o.CoordinateY - avatar.CoordinateY;
                    return (dx * dx + dz * dz) <= (GET_RANGE * GET_RANGE);
                })
                .OrderBy(o => {
                    float dx = o.CoordinateX - avatar.CoordinateX;
                    float dz = o.CoordinateY - avatar.CoordinateY;
                    return dx * dx + dz * dz;
                })
                .ToList();

            if (candidates.Count == 0) { Log("SYS", "Nothing gettable nearby."); return; }

            if (getAll)
            {
                foreach (var obj in candidates)
                {
                    SendReqGetMessage(new ObjectID(obj.ID));
                    if (recorder.IsRecording) recorder.RecordGet(avatar, obj.Name ?? obj.ID.ToString());
                }
                Log("SYS", $"Getting all {candidates.Count} item(s).");
                return;
            }

            if (candidates.Count == 1)
            {
                var obj = candidates[0];
                SendReqGetMessage(new ObjectID(obj.ID));
                if (recorder.IsRecording) recorder.RecordGet(avatar, obj.Name ?? obj.ID.ToString());
                Log("SYS", $"Getting: {obj.Name}");
                return;
            }

            // Multiple items — open picker popup
            getCandidates    = candidates;
            getSelectedIndex = 0;
            activePopup      = PopupMode.GetList;
            DrawMap();
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

                case TuiAction.MoveUp:    SetHeldMove( 0, -1); HandleMovement( 0, -1); break;
                case TuiAction.MoveDown:  SetHeldMove( 0,  1); HandleMovement( 0,  1); break;
                case TuiAction.MoveLeft:  SetHeldMove(-1,  0); HandleMovement(-1,  0); break;
                case TuiAction.MoveRight: SetHeldMove( 1,  0); HandleMovement( 1,  0); break;

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
                    SendReqInventoryMessage(); // refresh inventory before showing
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
                    Data.SelfTarget = false;
                    AutoTargetNearest();
                    break;

                case TuiAction.TargetSelf:
                    Data.TargetID = Data.AvatarID;
                    Data.SelfTarget = true;
                    Log("SYS", $"Targeting self: {Data.AvatarObject?.Name}");
                    DrawStats();
                    break;

                case TuiAction.Look:
                    PerformLook();
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
                if (Data.NewsGroup.NewsGlobeID != 0) { Data.NewsGroup.Articles.Clear(); SendReqArticles(); }
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

            if (text.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                SendReqBuyMessage();
                return;
            }

            if (text.Equals("offer", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("trade", StringComparison.OrdinalIgnoreCase))
            {
                // Open offer popup with current target as trade partner
                var target = Data.TargetObject;
                if (target == null || !target.Flags.IsOfferable)
                {
                    Log("SYS", "No offerable target selected. Target an NPC or player first.");
                    return;
                }
                offerSelectedIndex = 0;
                offerPendingIDs.Clear();
                // Seed the TradePartner so the popup header shows the right name
                Data.Trade.TradePartner = target;
                Data.Trade.IsBackgroundOffer = false;
                activePopup = PopupMode.Offer;
                DrawMap();
                return;
            }

            if (text.Equals("go", StringComparison.OrdinalIgnoreCase))
            {
                SendReqGo(true);
                return;
            }

            if (text.Equals("look", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("l",    StringComparison.OrdinalIgnoreCase))
            {
                PerformLook();
                return;
            }

            if (text.Equals("get", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("get all", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("pickup", StringComparison.OrdinalIgnoreCase))
            {
                PerformGet(text.Equals("get all", StringComparison.OrdinalIgnoreCase));
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
                if (recorder.IsRecording) recorder.RecordRest(Data.AvatarObject);
                Log("SYS", "Resting...");
                var command = new UserCommandRest();
                ServerConnection.SendQueue.Enqueue(new UserCommandMessage(command, null));
                Data.IsResting = true;
                return;
            }
            if (text.Equals("stand", StringComparison.OrdinalIgnoreCase) || text.Equals("/stand", StringComparison.OrdinalIgnoreCase))
            {
                if (recorder.IsRecording) recorder.RecordStand(Data.AvatarObject);
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

            if (text.Equals("/players", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("who", StringComparison.OrdinalIgnoreCase))
            {
                SendSendPlayers();
                var players = Data.OnlinePlayers.ToList();
                Log("SYS", $"Online players ({players.Count}):");
                foreach (var p in players)
                    Log("SYS", $"  ID={p.ID} Name='{p.Name}' NameRID={p.NameRID} Type={p.Flags.Player}");
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

            if (text.Equals("record stop", StringComparison.OrdinalIgnoreCase))
            {
                recorder.Stop();
                isRecording = false;
                recordingFile = null;
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

                if (recorder.IsRecording)
                    recorder.Stop();

                recordingFile = filename;
                if (recorder == null) recorder = new PathRecorder(this);
                recorder.Start(recordingFile);
                return;
            }

            if (text.Equals("replay stop", StringComparison.OrdinalIgnoreCase))
            {
                replayer?.Stop();
                return;
            }

            if (text.StartsWith("replay ", StringComparison.OrdinalIgnoreCase))
            {
                string rest = text[7..].Trim();
                if (string.IsNullOrEmpty(rest)) { Log("ERROR", "Usage: replay [loop|pingpong] <filename.json>"); return; }
                ReplayMode replayMode = ReplayMode.Once;
                string filename = rest;
                if (rest.StartsWith("loop ", StringComparison.OrdinalIgnoreCase))
                    { replayMode = ReplayMode.Loop; filename = rest[5..].Trim(); }
                else if (rest.StartsWith("pingpong ", StringComparison.OrdinalIgnoreCase))
                    { replayMode = ReplayMode.PingPong; filename = rest[9..].Trim(); }
                if (string.IsNullOrEmpty(filename)) { Log("ERROR", "Usage: replay [loop|pingpong] <filename.json>"); return; }
                replayer ??= new PathReplayer(this);
                if (replayer.Load(filename)) replayer.Start(replayMode);
                return;
            }

            if (text.StartsWith("cast ", StringComparison.OrdinalIgnoreCase))
            {
                string spellName = text[5..].Trim();
                var spell = Data.AvatarSpells.GetItemByName(spellName, false);
                if (spell != null)
                {
                    Log("SYS", $"Casting: {spell.ResourceName} (No Target)");
                    if (recorder.IsRecording) recorder.RecordCast(Data.AvatarObject, spell.ResourceName);
                    SendReqCastMessage(spell.ObjectID);
                }
                else Log("ERROR", $"Unknown spell: {spellName}");
                return;
            }

            if (text.Equals("conveyall", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/conveyall", StringComparison.OrdinalIgnoreCase))
            {
                var spell = Data.AvatarSpells.GetItemByName("conveyance", false);
                if (spell == null)
                {
                    Log("ERROR", "You don't have the Convey spell.");
                    return;
                }
                conveySpellID = spell.ObjectID;
                conveyQueue.Clear();
                foreach (var item in Data.InventoryObjects)
                {
                    if (!item.IsStackable) continue;
                    if (item.Name.IndexOf("shilling", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    conveyQueue.Enqueue(item.ID);
                }
                if (conveyQueue.Count == 0)
                {
                    Log("SYS", "No stackable items to convey (excluding shillings).");
                    return;
                }
                // Small delay before first cast so any active cooldown can expire
                conveyNextCastTime = DateTime.Now.AddMilliseconds(300);
                if (recorder.IsRecording) recorder.RecordMacro(Data.AvatarObject, "/conveyall");
                Log("SYS", $"[Convey] Starting convey on {conveyQueue.Count} item stack(s)...");
                return;
            }

            if (text.Equals("conveyall stop", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("/conveyall stop", StringComparison.OrdinalIgnoreCase))
            {
                conveyQueue.Clear();
                if (recorder.IsRecording) recorder.RecordMacro(Data.AvatarObject, "/conveyall stop");
                Log("SYS", "[Convey] Macro cancelled.");
                return;
            }

            if (text.StartsWith("/drainunbound", StringComparison.OrdinalIgnoreCase))
            {
                string arg = text.Length > 13 ? text[13..].Trim() : "";
                if (string.IsNullOrEmpty(arg) || arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    drainUnbound = false;
                    drainUnboundSpellID = 0;
                    drainUnboundSpellName = "";
                    if (recorder.IsRecording) recorder.RecordMacro(Data.AvatarObject, "/drainunbound stop");
                    Log("SYS", "[Drain] Stopped.");
                    DrawStats();
                    return;
                }
                var drainSpell = Data.AvatarSpells.GetItemByName(arg, false);
                if (drainSpell == null)
                {
                    Log("ERROR", $"[Drain] Spell '{arg}' not found in your spell list.");
                    return;
                }
                drainUnboundSpellID = drainSpell.ObjectID;
                drainUnboundSpellName = drainSpell.ResourceName;
                drainUnbound = true;
                if (recorder.IsRecording) recorder.RecordMacro(Data.AvatarObject, $"/drainunbound {arg}");
                Log("SYS", $"[Drain] Active: {drainUnboundSpellName} — casting when no creature targeted.");
                DrawStats();
                return;
            }

            if (text.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
            {
                string msg = text[4..].Trim();
                if (recorder.IsRecording) recorder.RecordSay(Data.AvatarObject, msg);
                SendSayToMessage(ChatTransmissionType.Normal, msg);
                return;
            }

            if (text.StartsWith("yell ", StringComparison.OrdinalIgnoreCase))
            {
                SendSayToMessage(ChatTransmissionType.Yell, text[5..].Trim());
                return;
            }

            if (text.StartsWith("broadcast ", StringComparison.OrdinalIgnoreCase))
            {
                SendSayToMessage(ChatTransmissionType.Everyone, text[10..].Trim());
                return;
            }

            if (text.StartsWith("guild ", StringComparison.OrdinalIgnoreCase))
            {
                SendSayToMessage(ChatTransmissionType.Guild, text[6..].Trim());
                return;
            }

            if (text.StartsWith("emote ", StringComparison.OrdinalIgnoreCase))
            {
                SendSayToMessage(ChatTransmissionType.Emote, text[6..].Trim());
                return;
            }

            if (text.StartsWith("e ", StringComparison.OrdinalIgnoreCase))
            {
                SendSayToMessage(ChatTransmissionType.Emote, text[2..].Trim());
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
                    string targetName = parts[0];
                    string msg = parts[1];
                    // Look up player object ID by name from the online player list
                    var onlineTarget = Data.OnlinePlayers.GetItemByName(targetName);
                    if (onlineTarget != null)
                    {
                        Log("CHAT", $"You tell {onlineTarget.Name}: {msg}");
                        if (recorder.IsRecording) recorder.RecordTell(Data.AvatarObject, $"{onlineTarget.Name}:{msg}");
                        SendSayGroupMessage(onlineTarget.ID, msg);
                    }
                    else
                    {
                        Log("ERROR", $"Player '{targetName}' not found online. Use /players to list online players.");
                    }
                }
                return;
            }

            // /prefs [flag] [on|off]  — show or toggle server-side preferences
            if (text.StartsWith("/prefs", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    // Show current state (live from server-acknowledged ClientPreferences)
                    var p = Data.ClientPreferences;
                    Log("SYS", $"Prefs: safetyoff={p.IsSafetyOff} tempsafe={p.TempSafe} grouping={p.Grouping} autoloot={p.AutoLoot} autocombine={p.AutoCombine} reagentbag={p.ReagentBag} spellpower={p.SpellPower}");
                }
                else if (parts.Length == 3 && bool.TryParse(parts[2], out bool val))
                {
                    var p = Data.ClientPreferences;
                    switch (parts[1].ToLower())
                    {
                        case "safetyoff":   p.IsSafetyOff  = val; break;
                        case "tempsafe":    p.TempSafe      = val; break;
                        case "grouping":    p.Grouping      = val; break;
                        case "autoloot":    p.AutoLoot      = val; break;
                        case "autocombine": p.AutoCombine   = val; break;
                        case "reagentbag":  p.ReagentBag    = val; break;
                        case "spellpower":  p.SpellPower    = val; break;
                        default: Log("ERROR", $"Unknown pref: {parts[1]}"); return;
                    }
                    SendUserCommandSendPreferences();
                    Log("SYS", $"Set {parts[1]}={val} and sent to server.");
                }
                else
                {
                    Log("SYS", "Usage: /prefs  OR  /prefs <flag> <true|false>");
                    Log("SYS", "Flags: safetyoff tempsafe grouping autoloot autocombine reagentbag spellpower");
                }
                return;
            }

            if (recorder.IsRecording) recorder.RecordSay(Data.AvatarObject, text);
            SendSayToMessage(ChatTransmissionType.Normal, text);
        }

        private void ResetCharCreation()
        {
            charCreationState = CharCreationState.None;
            inputMode = false;
            inputBuffer = "";
            DrawInputField();
        }

        private static readonly string[] statNames = { "Might", "Intellect", "Stamina", "Agility", "Mysticism", "Aim" };

        private uint GetStatValue(Meridian59.Data.Models.CharCreationInfo info, int idx) => idx switch
        {
            0 => info.Might,
            1 => info.Intellect,
            2 => info.Stamina,
            3 => info.Agility,
            4 => info.Mysticism,
            5 => info.Aim,
            _ => 0,
        };

        // Set a stat to an absolute value (model enforces sum/min/max constraints via its setter).
        private void AdjustNewCharStatAbsolute(Meridian59.Data.Models.CharCreationInfo info, int idx, uint value)
        {
            uint clamped = Math.Clamp(value,
                Meridian59.Data.Models.CharCreationInfo.ATTRIBUTE_MINVALUE,
                Meridian59.Data.Models.CharCreationInfo.ATTRIBUTE_MAXVALUE);
            switch (idx)
            {
                case 0: info.Might     = clamped; break;
                case 1: info.Intellect = clamped; break;
                case 2: info.Stamina   = clamped; break;
                case 3: info.Agility   = clamped; break;
                case 4: info.Mysticism = clamped; break;
                case 5: info.Aim       = clamped; break;
            }
        }

        // Kept for any existing callers; internally delegates to absolute setter via delta.
        private void AdjustNewCharStat(Meridian59.Data.Models.CharCreationInfo info, int idx, int delta)
        {
            AdjustNewCharStatAbsolute(info, idx, (uint)Math.Max(0, (int)GetStatValue(info, idx) + delta));
            DrawMap();
        }

        private void ToggleNewCharAbility(Meridian59.Data.Models.CharCreationInfo info, int idx, bool isSpells)
        {
            if (isSpells)
            {
                var list = info.Spells.ToList();
                if (idx < 0 || idx >= list.Count) return;
                var spell = list[idx];
                if (info.SelectedSpells.Any(s => s.ExtraID == spell.ExtraID))
                    info.SelectedSpells.RemoveAll(s => s.ExtraID == spell.ExtraID);
                else
                    info.SelectedSpells.Add(spell);
            }
            else
            {
                var list = info.Skills.ToList();
                if (idx < 0 || idx >= list.Count) return;
                var skill = list[idx];
                if (info.SelectedSkills.Any(s => s.ExtraID == skill.ExtraID))
                    info.SelectedSkills.RemoveAll(s => s.ExtraID == skill.ExtraID);
                else
                    info.SelectedSkills.Add(skill);
            }
        }

        private void SubmitNewCharacter(Meridian59.Data.Models.CharCreationInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.AvatarName))
            {
                Log("SYS", "Please enter a character name (press F2).");
                return;
            }
            // Apply selected looks preset (indices wrap to pool size)
            var p = LooksPresets[newCharLooksPreset];
            int skinIdx      = info.SkinColors.Length      > 0 ? p.Skin      % info.SkinColors.Length      : 0;
            int hairColorIdx = info.HairColors.Length      > 0 ? p.HairColor % info.HairColors.Length      : 0;
            bool isMale      = info.Gender == Meridian59.Common.Enums.Gender.Male;
            int hairIdx      = isMale
                ? (info.MaleHairIDs.Length   > 0 ? p.Hair  % info.MaleHairIDs.Length   : 0)
                : (info.FemaleHairIDs.Length > 0 ? p.Hair  % info.FemaleHairIDs.Length : 0);
            int eyesIdx      = isMale
                ? (info.MaleEyeIDs.Length    > 0 ? p.Eye   % info.MaleEyeIDs.Length    : 0)
                : (info.FemaleEyeIDs.Length  > 0 ? p.Eye   % info.FemaleEyeIDs.Length  : 0);
            int noseIdx      = isMale
                ? (info.MaleNoseIDs.Length   > 0 ? p.Nose  % info.MaleNoseIDs.Length   : 0)
                : (info.FemaleNoseIDs.Length > 0 ? p.Nose  % info.FemaleNoseIDs.Length : 0);
            int mouthIdx     = isMale
                ? (info.MaleMouthIDs.Length  > 0 ? p.Mouth % info.MaleMouthIDs.Length  : 0)
                : (info.FemaleMouthIDs.Length> 0 ? p.Mouth % info.FemaleMouthIDs.Length: 0);
            info.SetExampleModel(info.Gender, skinIdx, hairColorIdx, hairIdx, eyesIdx, noseIdx, mouthIdx);

            activePopup = PopupMode.None;
            popupJustClosed = true;
            SendSystemMessageNewCharInfo();
            ResetCharCreation();
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

        public void ExecuteMacro(string command) => ProcessCommand(command);

        // Used by PathReplayer for "Go" events: force-sends the recorded position then raw ReqGo
        // without re-running the wall-snap logic. Re-snapping from a replayed position can pick a
        // different exit wall than the original and cause the server to ignore the door crossing.
        public void SendReplayGo(ushort x, ushort y, ushort angle)
        {
            var avatar = Data.AvatarObject;
            if (avatar == null) return;
            avatar.CoordinateX = x;
            avatar.CoordinateY = y;
            avatar.AngleUnits  = angle;
            SendReqMoveMessage(true);
            ServerConnection.SendQueue.Enqueue(new ReqGoMessage());
            GameTick.DidReqGo();
        }

        public override void SendReqMoveMessage(bool ForceSend = false)
        {
            // AvatarID is invalid during login/char-select — silently skip, not an error
            if (!ObjectID.IsValid(Data.AvatarID))
                return;

            // AVOID sending ReqMove(0,0) when we have a character selected but no avatar object yet.
            // This prevents the server from snapping our spawn position to 0,0 during room entry.
            if (Data.AvatarObject == null)
                return;

            base.SendReqMoveMessage(ForceSend);
        }

        private void SetHeldMove(int dx, int dz)
        {
            heldMoveDx = dx;
            heldMoveDz = dz;
            lastKeyMoveTime = DateTime.Now;
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
                if (recorder.IsRecording) recorder.RecordMove(avatar);
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
            if (recorder.IsRecording) recorder.RecordMove(avatar);
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
                // TODO: record Activate with target ID for replayer
                base.SendReqActivate(nearestObj.ID);
            }
            else
            {
                // TODO: record Activate (no target) for replayer
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
                    if (recorder.IsRecording) recorder.RecordGo(avatar);
                    return;
                }
                else
                {
                    Log("MOVE", $"No exit wall found within range. Nearest: {Math.Sqrt(minDist2):F1}");
                }
            }

            if (recorder.IsRecording) recorder.RecordGo(avatar);
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
        }

        public override void SendSayGroupMessage(uint TargetID, string Text)
        {
            base.SendSayGroupMessage(TargetID, Text);
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
