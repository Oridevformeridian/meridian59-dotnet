/*
 Copyright (c) 2012-2013 Clint Banzhaf
 This file is part of "Meridian59 .NET".

 "Meridian59 .NET" is free software: 
 You can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, 
 either version 3 of the License, or (at your option) any later version.

 "Meridian59 .NET" is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
 without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 See the GNU General Public License for more details.

 You should have received a copy of the GNU General Public License along with "Meridian59 .NET".
 If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.ComponentModel;

using Meridian59.Client;
using Meridian59.Files;
using Meridian59.Data.Models;
using Meridian59.Data;
using Meridian59.Protocol.GameMessages;
using Meridian59.Protocol.Enums;
using Meridian59.Common.Enums;
using Meridian59.Common.Constants;
using Meridian59.Common;
using System.Text;

namespace Meridian59.Bot
{
    /// <summary>
    /// A still abstract and generic bot client class extending BaseClient.
    /// </summary>
    /// <typeparam name="T">Type of GameTick or deriving class</typeparam>
    /// <typeparam name="R">Type of ResourceManager or deriving class</typeparam>
    /// <typeparam name="D">Type of DataController or deriving class</typeparam>
    /// <typeparam name="C">Type of Config or deriving class</typeparam>
    public abstract class BotClient<T, R, D, C> : BaseClient<T, R, D, C>, IDisposable
        where T : GameTick, new()
        where R : ResourceManager, new()
        where D : DataController, new()
        where C : BotConfig, new() 
    {
        #region Constants
        protected const int SLEEPAFTERERROR         = 5000;
        protected const int FIRSTLOGROW             = 6;
        protected const int LASTLOGROW              = 19;
        protected const ConsoleColor COLORDEFAULT   = ConsoleColor.White;
        protected const ConsoleColor COLORGOOD      = ConsoleColor.Green;
        protected const ConsoleColor COLORWARN      = ConsoleColor.DarkYellow;
        protected const ConsoleColor COLORERROR     = ConsoleColor.Red;
        protected const string LOG_INITCONFIG       = "Initializing configuration.xml ...";
        protected const string LOG_RELOADCONFIG     = "Reloading configuration.xml...";
        protected const string LOG_USEREXIT         = "User exited the application.";
        protected const string LOG_CREDENTIALSOK    = "Account credentials accepted.";
        protected const string LOG_CREDENTIALSWRONG = "Your account credentials are not correct.";
        protected const string LOG_WRONGRESVERSION  = "Your resource version in configuration.xml doesn't match this server.";
        protected const string LOG_NETERROR         = "Network interface malfunction.";
        protected const string LOG_APPVERSIONERROR  = "Your major/minor versions in configuration.xml don't match this server.";
        #endregion

        /// <summary>First row used for log text output. Override in subclasses for dynamic layouts.</summary>
        protected virtual int DynamicFirstLogRow => FIRSTLOGROW;

        /// <summary>Last row used for log text output. Override in subclasses for dynamic layouts.</summary>
        protected virtual int DynamicLastLogRow  => LASTLOGROW;

        /// <summary>
        /// Maximum characters per log line written to the console.
        /// Defaults to the full buffer width minus borders.
        /// Override in split-panel layouts to constrain output to the log panel only.
        /// </summary>
        protected virtual int LogLineWidth => Console.BufferWidth - 4;

        /// <summary>
        /// 
        /// </summary>
        protected StreamWriter logWriter;

        /// <summary>
        /// 
        /// </summary>
        protected int logLine = FIRSTLOGROW;

        /// <summary>
        /// Random for movement
        /// </summary>
        protected Random random = new Random();

        /// <summary>
        /// Enables random movement for load testing
        /// </summary>
        public bool IsRandomMoving { get; set; }

        protected bool sentUseCharacter = false;

        /// <summary>
        /// Collects latency samples for load testing. Accessible to subclasses.
        /// </summary>
        public LoadTestMetrics Metrics { get; } = new LoadTestMetrics();

        /// <summary>
        /// Tracks when RTT was last sampled to avoid flooding the metrics with duplicate readings.
        /// </summary>
        private long lastRttSampleMs = 0;

        /// <summary>
        /// Set this to true if you run the instance in a windows service environment.
        /// </summary>
        public bool IsService { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public BotClient()
            : base()
        {
            // sleep 10ms between threadloops
            SLEEPTIME = 10;

            // hook up lists/model observers
            Data.RoomObjects.ListChanged += OnRoomObjectsListChanged;
            Data.OnlinePlayers.ListChanged += OnOnlinePlayersListChanged;
            Data.InventoryObjects.ListChanged += OnInventoryObjectsListChanged;
            Data.AvatarCondition.ListChanged += OnAvatarConditionListChanged;
            Data.RoomInformation.PropertyChanged += OnRoomInformationPropertyChanged;
            Data.PropertyChanged += OnDataControllerPropertyChanged;          
        }

        /// <summary>
        /// Logs to file or console
        /// </summary>
        /// <param name="Type"></param>
        /// <param name="Text"></param>
        public override void Log(string Type, string Text)
        {
            if (IsService)
            {
                base.Log(Type, Text);
                return;
            }
            {
                if (Text.Contains(ChatSubStrings.NOTENOUGHMANA))
                    Type = "WARN";

                else if (Text.Contains(ChatSubStrings.NOTENOUGHVIGOR))
                    Type = "WARN";

                else if (Text.Contains(ChatSubStrings.NOREAGENTS))
                    Type = "WARN";

                else if (Text.Contains(ChatSubStrings.CONCENBROKEN))
                    Type = "WARN";

                else if (Text.Contains(ChatSubStrings.IMPROVED))
                    Type = "GOOD";               
            }

            // build line
            Text = DateTime.Now.ToLongTimeString().PadRight(10) + ' ' + Type.PadRight(6) + ' ' + Text;
                        
            // log to file full output
            if (logWriter != null)         
                logWriter.WriteLine(Text);
            
            // CONSOLE OUTPUT (not for services)
            if (!IsService)
            {
                // prepare text length
                if (Text.Length > LogLineWidth)
                    Text = Text.Substring(0, LogLineWidth - 3) + "...";

                else
                    Text = Text.PadRight(LogLineWidth);

                // set color
                switch (Type)
                {
                    case "GOOD":
                        Console.ForegroundColor = COLORGOOD;
                        break;

                    case "WARN":
                        Console.ForegroundColor = COLORWARN;
                        break;

                    case "ERROR":
                        Console.ForegroundColor = COLORERROR;
                        break;

                    default:
                        Console.ForegroundColor = COLORDEFAULT;
                        break;
                }

                // log to console
                Console.SetCursorPosition(2, logLine);
                Console.Write(Text);

                if (logLine < DynamicLastLogRow)
                {
                    logLine++;

                    Console.SetCursorPosition(2, logLine);
                    Console.Write(String.Empty.PadLeft(LogLineWidth));

                    if (logLine < DynamicLastLogRow - 1)
                    {
                        Console.SetCursorPosition(2, logLine + 1);
                        Console.Write(String.Empty.PadLeft(LogLineWidth));
                    }
                }
                else
                    logLine = DynamicFirstLogRow;

                // restore color
                Console.ForegroundColor = COLORDEFAULT;
            }
        }

        /// <summary>
        /// Major version of the client
        /// </summary>
        public override byte AppVersionMajor
        {
            get { return Config.MajorVersion; }
        }

        /// <summary>
        /// Minor version of the client
        /// </summary>
        public override byte AppVersionMinor
        {
            get { return Config.MinorVersion; }
        }

        /// <summary>
        /// 
        /// </summary>
        public override void Init()
        {
            base.Init();

            if (!IsService)
            {
                Console.CursorVisible = false;

                // draw the text ui basic boxes
                DrawBoxes();

                // initial
                DrawResting();
            }

            if (Config.HasLogFile())
            {
                // try to init writelock for stream on bot logfile
                try
                {
                    // get logwriter
                    logWriter = new StreamWriter(Config.LogFile, false, Encoding.Default);
                    logWriter.AutoFlush = true;
                }
                catch (Exception) { }
            }

            // connect to selected connection/server
            if (Config.SelectedConnectionInfo != null)
            {
                Log("SYS", "Connecting to " + Config.SelectedConnectionInfo.Host + ":" + 
                    Config.SelectedConnectionInfo.Port);

                Connect();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Error"></param>
        protected override void OnServerConnectionException(Exception Error)
        {
            // close connection and exit         
            ServerConnection.Disconnect();
            IsRunning = false;

            Log("ERROR", LOG_NETERROR);
            Thread.Sleep(SLEEPAFTERERROR);
        }

        protected bool sentSendCharacters = false;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleGameModeMessage(GameModeMessage Message)
        {
            if (Config.IsDebugEnabled)
                Log("DEBUG", "PI=" + Message.PI + " (" + ((MessageTypeGameMode)Message.PI).ToString() + ")");

            base.HandleGameModeMessage(Message);
        }

        protected override void HandleLoadModuleMessage(LoadModuleMessage Message)
        {
            Log("DEBUG", "LoadModule resource ID: " + Message.ResourceID);
            base.HandleLoadModuleMessage(Message);
        }

        protected virtual void HandleCharInfoOKMessage(CharInfoOkMessage Message)
        {
            Log("SYS", "Character configuration accepted.");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleGetClientMessage(GetClientMessage Message)
        {
            // server proposed client update
            // adjust major/min version in configuration.xml

            // close connection and exit         
            ServerConnection.Disconnect();
            IsRunning = false;

            Log("ERROR", LOG_APPVERSIONERROR);
            Thread.Sleep(SLEEPAFTERERROR);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleGetLoginMessage(GetLoginMessage Message)
        {
            // answer with our account credentials
            if (Config.SelectedConnectionInfo != null)
                SendLoginMessage(Config.SelectedConnectionInfo.Username, Config.SelectedConnectionInfo.Password);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleLoginOKMessage(LoginOKMessage Message)
        {
            Log("SYS", LOG_CREDENTIALSOK);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleLoginFailedMessage(LoginFailedMessage Message)
        {
            base.HandleLoginFailedMessage(Message);

            // close connection and exit         
            ServerConnection.Disconnect();
            IsRunning = false;

            Log("ERROR", LOG_CREDENTIALSWRONG);
            Thread.Sleep(SLEEPAFTERERROR);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleLoginModeMessageMessage(LoginModeMessageMessage Message)
        {           
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleDownloadMessage(DownloadMessage Message)
        {
            base.HandleDownloadMessage(Message);

            // server proposed different resources version
            // adjust resourceversion in configuration.xml

            // close connection and exit         
            ServerConnection.Disconnect();
            IsRunning = false;

            Log("ERROR", LOG_WRONGRESVERSION);
            Thread.Sleep(SLEEPAFTERERROR);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleCharactersMessage(CharactersMessage Message)
        {
            Log("SYS", "Received character list (" + Message.WelcomeInfo.Characters.Count + " characters).");
            bool found = false;

            if (Config.SelectedConnectionInfo != null)
            {
                // try to login the character which is defined in config
                foreach (CharSelectItem character in Message.WelcomeInfo.Characters)
                {
                    Log("SYS", "Found character on account: " + character.Name + " (ID: " + character.ID + ")");
                    if (character.Name.ToLower() == Config.SelectedConnectionInfo.Character.ToLower())
                    {
                        if (!sentUseCharacter)
                        {
                            Log("SYS", "Logging in character " + character.Name);

                            SendUseCharacterMessage(new ObjectID(character.ID), true);
                            sentUseCharacter = true;
                        }
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                // error - char not found
                // close connection and exit         
                ServerConnection.Disconnect();
                IsRunning = false;

                if (Config.SelectedConnectionInfo != null)
                    Log("ERROR", "Character " + Config.SelectedConnectionInfo.Character + " was not found on this account.");
                
                Thread.Sleep(SLEEPAFTERERROR);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleQuitMessage(QuitMessage Message)
        {
            base.HandleQuitMessage(Message);
            sentUseCharacter = false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandlePlayerMessage(PlayerMessage Message)
        {
            base.HandlePlayerMessage(Message);
            Log("SYS", "Entered room: " + Message.RoomInfo.RoomName);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected virtual void HandleOfferMessage(OfferMessage Message)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected virtual void HandleCounterOfferMessage(CounterOfferMessage Message)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected virtual void HandleCreateMessage(CreateMessage Message)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleSaidMessage(SaidMessage Message)
        {
            base.HandleSaidMessage(Message);

            // check if this is a whisper to us
            // by loooking for a substring in the plain rsc text
            if (Message.Message.ResourceName.Contains("tells you"))
            {
                // log it
                Log("CHAT", Message.Message.FullString);

                // get the part that is within the " ", the real text
                if (Message.Message.Variables.Count > 1 &&
                    Message.Message.Variables[1].Type == InlineVariableType.String)
                {
                    // the whispered text within ""
                    string text = (string)Message.Message.Variables[1].Data;

                    // handle null
                    if (text == null)
                        return;

                    // split up into words
                    string[] words = text.ToLower().Split(' ');

                    // need at least the commandword
                    if (words.Length > 0)
                    {
                        // handle it
                        ProcessCommand(Message.Message.SourceObjectID, words);
                    }
                }
            }
        }

        /// <summary>
        /// Override with main handler for received chat textcommands
        /// </summary>
        /// <param name="PartnerID">Command invoked by this (player) id</param>
        /// <param name="Words">First elment is command name</param>
        protected virtual void ProcessCommand(uint PartnerID, string[] Words)
        {
            if (Words.Length >= 2 && Words[0] == "!move")
            {
                if (Words[1] == "on")
                {
                    IsRandomMoving = true;
                    Log("SYS", "Random movement enabled.");
                }
                else if (Words[1] == "off")
                {
                    IsRandomMoving = false;
                    Log("SYS", "Random movement disabled.");
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public override void Update()
        {
            base.Update();

            // handle random movement
            if (IsRandomMoving && Data.AvatarObject != null && !Data.AvatarObject.IsMoving)
            {
                // pick a random spot in the room
                // Meridian 59 coordinates are usually 0-128
                ushort x = (ushort)random.Next(20, 100);
                ushort y = (ushort)random.Next(20, 100);
                byte speed = (byte)random.Next(64, 255);

                V2 destination = new V2(x, y);
                Data.AvatarObject.StartMoveTo(ref destination, speed);

                Log("BOT", "Random move to " + x + "/" + y + " with speed " + speed);
            }

            // update some values and read input for NON service instances
            if (!IsService)
            {
                if (Data.AvatarObject != null)
                    Console.Title = Data.AvatarObject.Name;

                DrawRTT();

                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);

                    ProcessKeyPress(key);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void OnRoomObjectsListChanged(object sender, ListChangedEventArgs e)
        {
            switch (e.ListChangedType)
            {
                case ListChangedType.ItemAdded:
                    OnRoomObjectAdded(Data.RoomObjects[e.NewIndex]);
                    break;

                case ListChangedType.ItemDeleted:
                    OnRoomObjectRemoved(Data.RoomObjects.LastDeletedItem);
                    break;

                case ListChangedType.Reset:
                    OnRoomObjectReset();
                    break;

                case ListChangedType.ItemChanged:
                    OnRoomObjectChanged(Data.RoomObjects[e.NewIndex]);
                    break;
            }

            
            DrawCoordinates();
        }

        /// <summary>
        /// Executed when roomobjectlist added an item
        /// </summary>
        /// <param name="RoomObject"></param>
        protected virtual void OnRoomObjectAdded(RoomObject RoomObject)
        {
        }

        /// <summary>
        /// Executed when roomobject list removed an item
        /// </summary>
        /// <param name="RoomObject"></param>
        protected virtual void OnRoomObjectRemoved(RoomObject RoomObject)
        {
        }

        /// <summary>
        /// Executed when roomobject list has been reset
        /// </summary>
        protected virtual void OnRoomObjectReset()
        {
        }

        /// <summary>
        /// Executed when roomobject list changed an item
        /// </summary>
        protected virtual void OnRoomObjectChanged(RoomObject RoomObject)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnRoomInformationPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case RoomInfo.PROPNAME_ROOMNAME:
                    DrawRoom();
                    break;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnDataControllerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case DataController.PROPNAME_ISRESTING:
                    DrawResting();
                    break;

                case DataController.PROPNAME_AVATAROBJECT:
                    if (Data.AvatarObject != null)
                    {
                        Log("DEBUG", "AvatarObject assigned to DataController. (ID: " + Data.AvatarObject.ID + ")");
                        Data.AvatarObject.PropertyChanged += OnAvatarPropertyChanged;
                        DrawCoordinates();
                        DrawCondition();
                        DrawRoom();
                    }
                    break;
            }
        }

        protected virtual void OnAvatarPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case RoomObject.PROPNAME_COORDINATEX:
                case RoomObject.PROPNAME_COORDINATEY:
                case RoomObject.PROPNAME_ANGLE:
                case RoomObject.PROPNAME_ANGLEUNITS:
                    DrawCoordinates();
                    break;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnAvatarConditionListChanged(object sender, ListChangedEventArgs e)
        {
            DrawCondition();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnInventoryObjectsListChanged(object sender, ListChangedEventArgs e)
        {
            DrawCash();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnOnlinePlayersListChanged(object sender, ListChangedEventArgs e)
        {            
        }

        /// <summary>
        /// 
        /// </summary>
        protected override void Cleanup()
        {
            if (logWriter != null)
            {
                logWriter.Close();
                logWriter = null;
            }

            base.Cleanup();
        }

        /// <summary>
        /// 
        /// </summary>
        public override void SendReqMoveMessage(bool ForceSend = false)
        {
            base.SendReqMoveMessage(ForceSend);
        }

        public void Dispose()
        {
        }

        #region Text UI
        /// <summary>
        /// 
        /// </summary>
        protected virtual void ProcessKeyPress(ConsoleKeyInfo Key)
        {
            switch (Key.Key)
            {
                case ConsoleKey.Q:
                    // log reload
                    Log("SYS", LOG_USEREXIT);

                    IsRunning = false;
                    break;

                case ConsoleKey.R:
                    // log reload
                    Log("SYS", LOG_RELOADCONFIG);

                    // reload
                    Config.Load(Config.ConfigFile, Config.ConfigFileAlt);
                    break;

                case ConsoleKey.M:
                    DumpMetrics();
                    break;

                case ConsoleKey.G:
                    DumpTimeSeries();
                    break;

                case ConsoleKey.S:
                    SaveMetrics();
                    break;
            }
        }

        /// <summary>
        /// Logs all collected metrics as histograms to the console log area.
        /// </summary>
        public void DumpMetrics()
        {
            List<string> names = Metrics.GetNames();
            if (names.Count == 0)
            {
                Log("METR", "No metrics collected yet.");
                return;
            }
            foreach (string name in names)
            {
                string output = Metrics.RenderHistogram(name);
                LogMultiline("METR", output);
            }
        }

        /// <summary>
        /// Logs all collected metrics as time-series line graphs to the console log area.
        /// </summary>
        public void DumpTimeSeries()
        {
            List<string> names = Metrics.GetNames();
            if (names.Count == 0)
            {
                Log("METR", "No metrics collected yet.");
                return;
            }
            foreach (string name in names)
            {
                string output = Metrics.RenderTimeSeries(name);
                LogMultiline("METR", output);
            }
        }

        /// <summary>
        /// Saves all collected metrics to a timestamped file next to the log file (or current dir).
        /// </summary>
        public void SaveMetrics()
        {
            List<string> names = Metrics.GetNames();
            if (names.Count == 0)
            {
                Log("METR", "No metrics to save.");
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename = "metrics_" + timestamp + ".txt";

            // Place alongside the configured log file if one exists, else current dir
            if (Config.HasLogFile())
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(Config.LogFile));
                filename = Path.Combine(dir, filename);
            }

            try
            {
                Metrics.SaveToFile(filename);
                Log("METR", "Metrics saved: " + filename);
            }
            catch (Exception ex)
            {
                Log("ERROR", "Failed to save metrics: " + ex.Message);
            }
        }

        /// <summary>
        /// Splits a multi-line string and logs each line individually.
        /// </summary>
        private void LogMultiline(string type, string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                    Log(type, trimmed);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawCondition()
        {
            // don't do it for services
            if (IsService)
                return;

            foreach (StatNumeric stat in Data.AvatarCondition)
            {
                string val = stat.ValueCurrent.ToString();
                string max = stat.ValueMaximum.ToString();
                string rendermax = stat.ValueRenderMax.ToString();

                val = val.PadLeft(3);
                max = max.PadRight(3);

                switch (stat.Num)
                {
                    case StatNums.HITPOINTS:
                        Console.SetCursorPosition(7, 1);                       
                        Console.Write(val + '/' + max);
                        break;

                    case StatNums.MANA:
                        Console.SetCursorPosition(7, 2);
                        Console.Write(val + '/' + max);
                        break;

                    case StatNums.VIGOR:
                        Console.SetCursorPosition(7, 3);
                        Console.Write(val + '/' + rendermax);
                        break;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawRoom()
        {
            // don't do it for services
            if (IsService)
                return;

            string room = Data.RoomInformation?.RoomName ?? "";

            if (room.Length > 35)
                room = room.Substring(0, 32) + "...";

            else
                room = room.PadRight(35);

            Console.SetCursorPosition(23, 1);
            Console.Write(room);
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawResting()
        {
            // don't do it for services
            if (IsService)
                return;

            Console.SetCursorPosition(37, 3);
            Console.Write(Data.IsResting.ToString().PadRight(8));
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawRTT()
        {
            // don't do it for services
            if (IsService)
                return;

            Console.SetCursorPosition(22, 3);
            Console.Write((ServerConnection.RTT.ToString() + "ms").PadRight(6));

            // sample RTT into metrics once per second
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if (nowMs - lastRttSampleMs >= 1000)
            {
                lastRttSampleMs = nowMs;
                Metrics.Record("rtt_ms", ServerConnection.RTT);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawCash()
        {
            // don't do it for services
            if (IsService)
                return;

            uint cash = 0;

            InventoryObject inventoryObject = 
                Data.InventoryObjects.GetItemByName("shilling", false);

            if (inventoryObject != null)
                cash = inventoryObject.Count;

            Console.SetCursorPosition(50, 3);
            Console.Write(cash.ToString().PadRight(9));
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawCoordinates()
        {
            // don't do it for services
            if (IsService)
                return;

            if (Data.AvatarObject != null)
            {
                Console.SetCursorPosition(64, 1);
                Console.Write(Data.AvatarObject.CoordinateX.ToString().PadRight(5));

                Console.SetCursorPosition(73, 1);
                Console.Write(Data.AvatarObject.CoordinateY.ToString().PadRight(5));
            }
        }

        public void DrawDynamic(string Text)
        {
            // don't do it for services
            if (IsService)
                return;

            Console.SetCursorPosition(61, 3);
            Console.Write(Text.PadRight(17));
        }

        /// <summary>
        /// 
        /// </summary>
        public virtual void DrawBoxes()
        {
            // don't do it for services
            if (IsService)
                return;

            // 1. Header

            Console.SetCursorPosition(0, 0);
            Console.Write("╔══════════════╦═══════════════════════════════════════════╦═══════════════════╗");

            Console.SetCursorPosition(0, 1);
            Console.Write("║ HP:  ---/--- ║ ROOM: ----------------------------------- ║ X: ----- Y: ----- ║");

            Console.SetCursorPosition(0, 2);
            Console.Write("║ MP:  ---/--- ╠═════════════╦═══════════════╦═════════════╬═══════════════════╣");

            Console.SetCursorPosition(0, 3);
            Console.Write("║ VIG: ---/--- ║ RTT: ----ms ║ REST: ------- ║ $: -------- ║                   ║");

            Console.SetCursorPosition(0, 4);
            Console.Write("╠══════════════╩═════════════╩═══════════════╩═════════════╩═══════════════════╣");

            // 2. Log area
            for (int i = 5; i < 21; i++)
            {
                Console.SetCursorPosition(0, i);
                Console.Write("║                                                                              ║");
            }

            Console.SetCursorPosition(0, 21);
            Console.Write("╠══════════════╦══════════════════╦════════════════════╦═══════════════════════╣");
            
            Console.SetCursorPosition(0, 22);
            Console.Write("║ [Q]uit       ║ [R]eload config  ║ [M]etrics [G]raph  ║ [S]ave metrics        ║");

            Console.SetCursorPosition(0, 23);
            Console.Write("╚══════════════╩══════════════════╩════════════════════╩═══════════════════════╝");
        }
        #endregion
    }
}
