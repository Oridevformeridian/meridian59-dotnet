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

        protected const string LOG_RELOADCONFIG     = "Reloading configuration.";
        protected const string LOG_USEREXIT         = "User initiated exit.";
        protected const string LOG_NETERROR         = "Network interface malfunction.";
        protected const string LOG_CREDENTIALSWRONG = "Login failed (wrong credentials).";
        protected const string LOG_APPVERSIONERROR  = "Login failed (wrong application version).";
        protected const string LOG_WRONGRESVERSION  = "Login failed (wrong resource version).";

        protected const int FIRSTLOGROW             = 5;
        protected const int LOGAREAWIDTH            = 78;
        protected const int LOGAREAHEIGHT           = 15;

        protected ConsoleColor COLORDEFAULT         = ConsoleColor.Gray;
        protected ConsoleColor COLORGOOD            = ConsoleColor.Green;
        protected ConsoleColor COLORWARN            = ConsoleColor.Yellow;
        protected ConsoleColor COLORERROR           = ConsoleColor.Red;
        #endregion

        #region Fields
        /// <summary>
        /// 
        /// </summary>
        protected StreamWriter logWriter;
        protected StreamWriter metricsWriter;

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
        /// Set this to true if you run the instance in a windows service environment.
        /// </summary>
        public bool IsService { get; set; }
        #endregion

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
            string formattedText = $"[{DateTime.Now:HH:mm:ss.fff}] {Type,-8} {Text}";
                        
            // log to file full output
            if (logWriter != null)         
            {
                logWriter.WriteLine(formattedText);
                logWriter.Flush();
            }
            
            // CONSOLE OUTPUT (not for services)
            if (!IsService)
            {
                // prepare text length
                string consoleText = formattedText;
                if (consoleText.Length > LogLineWidth)
                    consoleText = consoleText.Substring(0, LogLineWidth - 3) + "...";

                else
                    consoleText = consoleText.PadRight(LogLineWidth);

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
                Console.Write(consoleText);

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
        /// Formats a PI into a human-readable name based on current protocol mode.
        /// </summary>
        protected string GetMessageName(byte pi)
        {
            if (ServerConnection?.MessageController == null) return pi.ToString();
            
            if (ServerConnection.MessageController.Mode == ProtocolMode.Login)
                return Enum.IsDefined(typeof(MessageTypeLoginMode), pi) 
                    ? ((MessageTypeLoginMode)pi).ToString() 
                    : $"LP_{pi}";
            
            return Enum.IsDefined(typeof(MessageTypeGameMode), (int)pi) 
                ? ((MessageTypeGameMode)pi).ToString() 
                : $"BP_{pi}";
        }

        /// <summary>
        /// Process message queues and record metrics.
        /// </summary>
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
                // Record metric for sending
                double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double ts = (double)message.SendRecvTimestamp / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
                double latency = now - ts;

                string name = GetMessageName(message.PI);
                
                // Exclude noisy types from metrics gathering (standard movement/status)
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
                // Record metric for receiving
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
            ServerConnection.IsOutgoingPacketLogEnabled = true;

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

                    // get metricsWriter
                    string metricsFile = Path.ChangeExtension(Config.LogFile, ".metrics.log");
                    metricsWriter = new StreamWriter(metricsFile, false, Encoding.Default);
                    metricsWriter.AutoFlush = true;
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
            ServerConnection.Disconnect($"Network Error: {Error.Message}");
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
            ServerConnection.Disconnect("Client version mismatch");
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
            // send login message
            if (Config.SelectedConnectionInfo != null)
                SendLoginMessage(Config.SelectedConnectionInfo.Username, Config.SelectedConnectionInfo.Password);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleLoginOKMessage(LoginOKMessage Message)
        {
            Log("SYS", "Account credentials accepted.");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleLoginFailedMessage(LoginFailedMessage Message)
        {
            base.HandleLoginFailedMessage(Message);

            // close connection and exit         
            ServerConnection.Disconnect("Login failed (wrong credentials)");
            IsRunning = false;

            Log("ERROR", LOG_CREDENTIALSWRONG);
            Thread.Sleep(SLEEPAFTERERROR);
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
            ServerConnection.Disconnect("Resource version mismatch (Download proposed)");
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
            foreach (CharSelectItem character in Message.WelcomeInfo.Characters)
            {
                Log("SYS", character.Name + " (ID: " + character.ID + ")");

                // look for character from config
                if (Config.SelectedConnectionInfo != null &&
                    character.Name.Equals(Config.SelectedConnectionInfo.Character, StringComparison.OrdinalIgnoreCase))
                {
                    Log("SYS", "Found character on account: " + character.Name + " (ID: " + character.ID + ")");

                    // character selection only once
                    if (!sentSendCharacters)
                    {
                        Log("SYS", "Logging in character " + character.Name);

                        // character select
                        SendUseCharacterMessage(new ObjectID(character.ID), true, character.Name);
                        sentSendCharacters = true;
                    }
                    found = true;
                }
            }

            if (!found)
            {
                // error - char not found
                // close connection and exit         
                ServerConnection.Disconnect("Character not found on account");
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
        protected override void HandleGameStateMessage(GameStateMessage Message)
        {
            base.HandleGameStateMessage(Message);

            // if we have no avatar object yet, 
            // the server will send character info after sending a position ack
            if (Data.AvatarObject == null)
            {
                Log("DEBUG", "No avatar object, sending initial position ack (0,0) to trigger character info.");
                SendReqMoveMessage(true);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandlePlayerMessage(PlayerMessage Message)
        {
            base.HandlePlayerMessage(Message);

            // log
            if (Config.SelectedConnectionInfo != null && Data.AvatarObject != null &&
                Data.AvatarObject.Name.Equals(Config.SelectedConnectionInfo.Character, StringComparison.OrdinalIgnoreCase))
            {
                if (Data.AvatarObject != null)
                {
                    Log("DEBUG", "AvatarObject assigned to DataController. (ID: " + Data.AvatarObject.ID + ")");
                }
            }
            
            Log("SYS", "Entered room: " + Message.RoomInfo.RoomName);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="Message"></param>
        protected override void HandleQuitMessage(QuitMessage Message)
        {
            // server confirms quit request
            // close connection and exit
            ServerConnection.Disconnect("Server confirmed quit");
            IsRunning = false;
        }

        #region Observers
        protected virtual void OnRoomObjectsListChanged(object sender, ListChangedEventArgs e)
        {
        }

        protected virtual void OnOnlinePlayersListChanged(object sender, ListChangedEventArgs e)
        {
        }

        protected virtual void OnInventoryObjectsListChanged(object sender, ListChangedEventArgs e)
        {
        }

        protected virtual void OnAvatarConditionListChanged(object sender, ListChangedEventArgs e)
        {
        }

        protected virtual void OnRoomInformationPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
        }

        protected virtual void OnDataControllerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
        }
        #endregion

        #region UI Properties
        public abstract int LogLineWidth { get; }
        public abstract int DynamicFirstLogRow { get; }
        public abstract int DynamicLastLogRow { get; }
        #endregion

        public abstract void DrawBoxes();
        public abstract void DrawResting();

        public override void SendReqMoveMessage(bool ForceSend)
        {
            // random movement?
            if (IsRandomMoving)
            {
                // TODO: random movement
            }

            base.SendReqMoveMessage(ForceSend);
        }

        public void Dispose()
        {
            if (logWriter != null)
            {
                logWriter.Close();
                logWriter = null;
            }
            if (metricsWriter != null)
            {
                metricsWriter.Close();
                metricsWriter = null;
            }
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
                    // log reload
                    Log("SYS", "Dumping metrics histogram...");
                    DumpMetrics();
                    break;

                case ConsoleKey.S:
                    // log reload
                    Log("SYS", "Saving metrics to file...");
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
                foreach (string line in output.Split('\n'))
                    Log("METR", line);
            }
        }

        /// <summary>
        /// Logs all collected metrics as time-series line graphs to the console log area.
        /// </summary>
        public void DumpMetricsSeries()
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
                foreach (string line in output.Split('\n'))
                    Log("METR", line);
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
            try
            {
                Metrics.SaveToFile(filename);
                Log("SYS", "Metrics saved to " + filename);
            }
            catch (Exception ex)
            {
                Log("ERROR", "Failed to save metrics: " + ex.Message);
            }
        }
        #endregion
    }
}
