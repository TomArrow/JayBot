using DSharpPlus;
using DSharpPlus.Entities;
using JayBot.SQLMappings;
using SQLite.Net2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace JayBot
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        enum QueuePickupIndicator
        {
            Unknown,
            Picking,
            PickingEnded
        }
        class BotMessageInfo
        {
            public DateTime utcTime;
            public UInt64[] userIds;
            public DSharpPlus.Entities.DiscordMember[] members;
            public string queuename;
            public int teamPlayerCount1, teamPlayerCount2;
            public int totalPlayerCount, joinedPlayerCount;
            public QueuePickupIndicator pickingIndicator = QueuePickupIndicator.Unknown;
            public bool messageSeenBefore = false;
            public bool containsPlayersList = false;
        }

        private DiscordClient discordClient = null;

        ulong botId = 845098024421425183;
        ulong botId2 = 177022387903004673;
        //ulong? channelId = null;

        Regex regex = new Regex(@">\s*\*\*((\d+)v(\d+)[^*]*)\*\*\s*\(\s*(\d+)\s*\/\s*(\d+)\)\s*\|\s*((`([^`]+)`\/?)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex regexDraftStage = new Regex(@"\*\*((\d+)v(\d+)[^*]*)\*\*\s*is now on the draft stage!", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex regexGameResults = new Regex(@"```markdown(?:\\n|\n)((\d+)v(\d+)[^\(]*)\([^\)]*\)\s*results", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex regexMatchCanceled = new Regex(@"your match has been canceled.\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex nicknameFilterRegex = new Regex(@"([`<>\*_\\\[\]\~])|((?=\s)[^ ])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex playerExpiredRegex = new Regex(@"<@\s*(\d+)> were removed from all queues \(expire time ran off\)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        ConcurrentDictionary<UInt64, string[]> channels = new ConcurrentDictionary<ulong, string[]>();
        ConcurrentDictionary<UInt64, User> users = new ConcurrentDictionary<UInt64, User>();
        ConcurrentDictionary<Tuple<UInt64, UInt64>, UserChannelActivity> userChannelActivity = new ConcurrentDictionary<Tuple<UInt64, UInt64>, UserChannelActivity>();
        ConcurrentDictionary<UInt64, MetaInfo> metaInfo = new ConcurrentDictionary<ulong, MetaInfo>();
        ConcurrentDictionary<UInt64, BotMessageInfo> currentBotInfo = new ConcurrentDictionary<ulong, BotMessageInfo>();
        ConcurrentDictionary<UInt64, DateTime?> lastPlayerCountIncreaseWithoutEveryoneMention = new ConcurrentDictionary<ulong, DateTime?>();
        ConcurrentDictionary<UInt64, DateTime> lastPlayerCountIncrease = new ConcurrentDictionary<ulong, DateTime>();
        ConcurrentDictionary<UInt64, int> messagesSinceLastWho = new ConcurrentDictionary<ulong, int>();
        ConcurrentDictionary<UInt64, DateTime?> pickingActive = new ConcurrentDictionary<ulong, DateTime?>();
        ConcurrentDictionary<UInt64, ConcurrentBag<DSharpPlus.Entities.DiscordMember>> members = new ConcurrentDictionary<UInt64, ConcurrentBag<DSharpPlus.Entities.DiscordMember>>();
        ConcurrentDictionary<UInt64, CrawledMessage> analyzedMessages = new ConcurrentDictionary<ulong, CrawledMessage>();

        public bool TestMode { get; set; } = false;
        public bool MentionsActive { get; set; } = false;
        public bool SilentMode { get; set; } = false;

        public MainWindow()
        {
            SQLitePCL.Batteries_V2.Init();
            InitializeComponent();

            this.DataContext = this;

            try
            {
                //soundPlayer = new System.Media.SoundPlayer(@"C:\Windows\Media\Windows Notify Calendar.wav");

            }
            catch (Exception ex)
            {
                // whatever
            }

            if (!File.Exists("token.txt"))
            {
                MessageBox.Show("Need token.txt with your token.");
                return;
            }
            if (!File.Exists("channels.txt"))
            {
                MessageBox.Show("Need channels.txt with channelnum:queue1,queue2 etc.");
                return;
            }
            if (!loadChannelInfo())
            {
                MessageBox.Show("Error loading channels from channels.txt.");
                return;
            }

            UInt64[] channelKeys = channels.Keys.ToArray();
            msgSendChannelCombo.ItemsSource = channelKeys;

            LoadData();
            this.Closed += MainWindow_Closed;
            startDataSaver();
            //test();
            startMessageRunner();
            start();
            scanPastAndStartLoop();
        }

        private async void scanPastAndStartLoop()
        {

            foreach (var channel in channels)
            {
                await scanChannelHistory(channel.Key);
            }
            // Do second time quick because that might have taken a while and not gotten all
            foreach (var channel in channels)
            {
                await scanChannelHistory(channel.Key);
            }
            startMainLoop();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            SaveData();
        }

        private bool loadChannelInfo()
        {
            string[] channelInfo = File.ReadAllLines("channels.txt");
            if(channelInfo is null || channelInfo.Length == 0)
            {
                return false;
            }
            int channelsFound = 0;
            foreach(string channel in channelInfo)
            {
                if (string.IsNullOrWhiteSpace(channel)) { continue;  }
                string[] parts = channel.Split(':',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                if(parts is null || parts.Length < 2)
                {
                    continue;
                }
                UInt64 channelNum;
                if (!UInt64.TryParse(parts[0], out channelNum))
                {
                    continue;
                }
                string[] queues = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if(queues is null || queues.Length == 0)
                {
                    continue;
                }

                channels[channelNum] = queues;
            }

            return true;
        }

        Queue<Tuple<string,UInt64>> messagesToSend = new Queue<Tuple<string, UInt64>>();
        DateTime lastMessageSent = DateTime.UtcNow;
        int millisecondTimeout = 500;
        int millisecondBuffer = 2000;
        private void enqueueMessage(string message, UInt64 channelId)
        {
            lock (messagesToSend)
            {
                messagesToSend.Enqueue(new Tuple<string, ulong>(message,channelId));
            }
        }
        private void startMessageRunner()
        {
            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(async () => {
                while (true)
                {
                    Thread.Sleep(50); // Kinda dumb solution but too lazy to do a proper promise thingie whatever things thangs
                    if ((DateTime.UtcNow - lastMessageSent).TotalMilliseconds < millisecondBuffer + millisecondTimeout) continue; // Shouldn't even be necessary but just to be safe.
                    //if (channelId == null) continue;
                    Tuple<string, ulong> messageToSend = null;
                    lock (messagesToSend)
                    {
                        if (messagesToSend.Count == 0) continue;
                        messageToSend = messagesToSend.Dequeue();
                    }
                    if (messageToSend == null) continue; // Shouldn't be the case but just in case.
                    var channel = await discordClient.GetChannelAsync(messageToSend.Item2);
                    if (SilentMode)
                    {
                        Dispatcher.Invoke(()=> {

                            string currentText = silentLogTxt.Text;
                            currentText += $"\n\n\n[{DateTime.Now.ToString()}]\n{messageToSend.Item2}:\n\n{messageToSend.Item1}";
                            if (currentText.Length > 5000)
                            {
                                currentText = currentText.Substring(currentText.Length - 5000);
                            }
                            silentLogTxt.Text = currentText;
                        });
                    } else
                    {
                        await discordClient.SendMessageAsync(channel, messageToSend.Item1);
                    }
                    lastMessageSent = DateTime.UtcNow;
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(_ =>
            {
                System.Media.SystemSounds.Exclamation.Play(); // Lol, so bad.
                startMessageRunner(); // Restart *shrug*
            },
            TaskContinuationOptions.OnlyOnFaulted); ;
        }
        
        private void startMainLoop()
        {
            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(async () => {
                while (true)
                {
                    Thread.Sleep(5000); // Kinda dumb solution but too lazy to do a proper promise thingie whatever things thangs
                    foreach(var channel in channels)
                    {
                        await MainLoopForChannel(channel.Key);
                    }
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(_ =>
            {
                System.Media.SystemSounds.Exclamation.Play(); // Lol, so bad.
                startMainLoop(); // Restart *shrug*
            },
            TaskContinuationOptions.OnlyOnFaulted); ;
        }
        private void startDataSaver()
        {
            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(async () => {
                while (true)
                {
                    Thread.Sleep(60000*5); // every 5 minutes should be fine
                    SaveData();
                }
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(_ =>
            {
                System.Media.SystemSounds.Exclamation.Play(); // Lol, so bad.
                startDataSaver(); // Restart *shrug*
            },
            TaskContinuationOptions.OnlyOnFaulted); 
        }
        void start()
        {
            //int tokenTypeInt = 0;
            //TokenType tokenType = (TokenType)tokenTypeInt;

            discordClient = new DiscordClient(new DiscordConfiguration()
            {
                Token = File.ReadAllText("token.txt").Trim(),
                TokenType = TokenType.Bot, // Override stupid obsolete error
                Intents= DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents
            });

            //discordClient.MessageCreated += DiscordClient_MessageCreated;
            discordClient.MessageCreated += async (e,b) =>
            {

                Task.Run(() => {
                    DiscordClient_MessageCreated(b);
                });
            };
            discordClient.MessageReactionAdded += DiscordClient_MessageReactionAdded;
            discordClient.TypingStarted += DiscordClient_TypingStarted;

            discordClient.ConnectAsync();

        }

        private Task DiscordClient_TypingStarted(DiscordClient sender, DSharpPlus.EventArgs.TypingStartEventArgs args)
        {
            UpdateUserTyped(args.User.Id,args.Channel.Id,args.StartedAt.UtcDateTime);
            return null;
        }

        private Task DiscordClient_MessageReactionAdded(DiscordClient sender, DSharpPlus.EventArgs.MessageReactionAddEventArgs args)
        {
            UpdateUserReacted(args.User.Id,args.Channel.Id,args.Message.CreationTimestamp.UtcDateTime);
            return null;
        }


        Mutex logMutex = new Mutex();
        private async Task DiscordClient_MessageCreated(DSharpPlus.EventArgs.MessageCreateEventArgs e)
        {
            //if (e.Message.Content.ToLower().StartsWith("abc"))
            //if (e.Message.Author.Id == )
            {
                //e.Message.Author.Id;
                //var channel = await discordClient.GetChannelAsync(e.Message.ChannelId);
                //await discordClient.SendMessageAsync(channel, "def");

            }


            var channel = await discordClient.GetChannelAsync(e.Message.ChannelId);
            var guild = await discordClient.GetGuildAsync(channel.GuildId.GetValueOrDefault(0));
            var member = await guild.GetMemberAsync(e.Message.Author.Id);
            string nickname = member.Nickname;

            //if (guild.Id != guildId)
            if (!channels.ContainsKey(channel.Id))
            {
                return;
            }

            BotMessageInfo botInfo = await analyzeMessage(e.Message, channel);

            if ((botInfo != null && botInfo.userIds != null && botInfo.userIds.Length > 0)|| (e.Message.Content?.Trim().ToLowerInvariant().Equals("=who",StringComparison.InvariantCultureIgnoreCase)).GetValueOrDefault(false))
            {
                messagesSinceLastWho[channel.Id] = 0;
            } else {
                if (!messagesSinceLastWho.ContainsKey(channel.Id))
                {
                    messagesSinceLastWho[channel.Id] = 0;
                }
                messagesSinceLastWho[channel.Id]++;
                if(e.Message.Embeds != null)
                {
                    foreach (var embed in e.Message.Embeds)
                    {
                        if(embed.Thumbnail != null)
                        {
                            int lines = Math.Min(embed.Thumbnail.Height,350) / 50;
                            messagesSinceLastWho[channel.Id] += lines;
                        }
                    }
                }
            }

            ProcessLatestBotMessage(botInfo, channel.Id);

            try
            {
                lock (logMutex)
                {

                    File.AppendAllLines("debugLog3.log", new string[] { guild.Id.ToString(), nickname, e.Message.Author.ToString(), e.Message.Author.Username, e.Message.CreationTimestamp.UtcDateTime.ToString(), e.Message.ChannelId.ToString(), e.Message.Content, e.Message.Author.Id.ToString(), "" });
                }
            }
            catch (Exception edasfwqe)
            {
                // Whatever dont care
            }                    //await e.Message.RespondAsync("pong!");
        }

        private async Task scanHistoryBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach(var kvp in channels)
            {
                await scanChannelHistory(kvp.Key);
            }
        }

        private void ProcessLatestBotMessage(BotMessageInfo botInfo, UInt64 channelId)
        {
            if (botInfo != null)
            {
                bool playerCountIncreased = false;
                if (currentBotInfo.ContainsKey(channelId))
                {
                    if(currentBotInfo[channelId].utcTime > botInfo.utcTime)
                    {
                        return; // older message
                    }
                    if(currentBotInfo[channelId].joinedPlayerCount < botInfo.joinedPlayerCount)
                    {
                        playerCountIncreased = true;
                    }   
                }
                currentBotInfo[channelId] = botInfo;
                if (playerCountIncreased)
                {
                    lastPlayerCountIncreaseWithoutEveryoneMention[channelId] = DateTime.UtcNow;
                    lastPlayerCountIncrease[channelId] = DateTime.UtcNow;
                }
                if (botInfo.pickingIndicator == QueuePickupIndicator.Picking)
                {
                    pickingActive[channelId] = botInfo.utcTime;
                }
                else if (botInfo.pickingIndicator == QueuePickupIndicator.PickingEnded)
                {
                    pickingActive[channelId] = null;
                }
                else if (botInfo.pickingIndicator == QueuePickupIndicator.Unknown)
                {
                    if (pickingActive.ContainsKey(channelId))
                    {
                        DateTime? lastPicking = pickingActive[channelId];
                        if (lastPicking.HasValue && (botInfo.utcTime - lastPicking.Value).TotalMinutes > 60)
                        {
                            // Reset after 1 hour in case something glitches. dumb solution but whatever.
                            pickingActive[channelId] = null;
                        }
                    }
                }
            }
        }


        static readonly string dbPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JayBot", "data.db");
        private void SaveData()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JayBot"));
            try
            {
                using (new GlobalMutexHelper("JayBotSQliteDataMutex"))
                {
                    if (!File.Exists(dbPath))
                    {
                        //File.CreateText(dbPath).Dispose();
                    }
                    var db = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create, false);

                    db.CreateTable<User>();
                    db.CreateTable<UserChannelActivity>();
                    db.CreateTable<MetaInfo>();
                    db.CreateTable<CrawledMessage>();
                    db.BeginTransaction();

                    foreach(var user in users)
                    {
                        db.InsertOrReplace(user.Value);
                    }
                    foreach(var userActivity in userChannelActivity)
                    {
                        db.InsertOrReplace(userActivity.Value);
                    }
                    foreach(var metaInfoHere in metaInfo)
                    {
                        db.InsertOrReplace(metaInfoHere.Value);
                    }
                    foreach (var msg in analyzedMessages)
                    {
                        db.InsertOrReplace(msg.Value);
                    }
                    // Save stats.
                    //foreach (ServerInfo serverInfo in data)
                    //{
                    //    if (serverInfo.StatusResponseReceived)
                    //    {
                    //        db.Insert(ServerInfoPublic.convertFromJKClient(serverInfo));
                    //    }
                    //}
                    db.Commit();
                    db.Close();
                    db.Dispose();
                }
            }
            catch (Exception e)
            {
                Helpers.logToFile(new string[] { "Failed to save data to database.", e.ToString() });
            }
        }

        private void LoadData()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JayBot"));
            try
            {
                using (new GlobalMutexHelper("JayBotSQliteDataMutex"))
                {
                    if (!File.Exists(dbPath))
                    {
                        //File.CreateText(dbPath).Dispose();
                    }
                    var db = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create,false);

                    db.CreateTable<User>();
                    db.CreateTable<UserChannelActivity>();
                    db.CreateTable<MetaInfo>();
                    db.CreateTable<CrawledMessage>();

                    var userQuery = db.Table<User>();
                    var userActivityQuery = db.Table<UserChannelActivity>();
                    var metaInfoQuery = db.Table<MetaInfo>();
                    var crawledMessageQuery = db.Table<CrawledMessage>();

                    foreach (CrawledMessage msg in crawledMessageQuery)
                    {
                        ObjectDateTimeNormalizer.AllDateTimesToUTC(msg);
                        analyzedMessages[(UInt64)msg.messageId] = msg;
                    }
                    foreach(User user in userQuery)
                    {
                        ObjectDateTimeNormalizer.AllDateTimesToUTC(user);
                        users[(UInt64)user.discordId] = user;
                    }
                    foreach(UserChannelActivity userActivity in userActivityQuery)
                    {
                        ObjectDateTimeNormalizer.AllDateTimesToUTC(userActivity);
                        var tuple = new Tuple<UInt64,UInt64>((UInt64)userActivity.userId, (UInt64)userActivity.channelId);
                        userChannelActivity[tuple] = userActivity;
                    }

                    HashSet<UInt64> channelMetaIds = new HashSet<ulong>();
                    foreach (MetaInfo meta in metaInfoQuery)
                    {
                        channelMetaIds.Add((UInt64)meta.channelId);
                    }
                    bool needReload = false;
                    foreach(var channel in channels)
                    {
                        if (!channelMetaIds.Contains(channel.Key))
                        {
                            MetaInfo blah = new MetaInfo() { channelId= (Int64)channel.Key };
                            db.Insert(blah);
                            needReload = true;
                        }
                    }

                    if (needReload)
                    {
                        metaInfoQuery = db.Table<MetaInfo>();
                    }
                    foreach (MetaInfo meta in metaInfoQuery)
                    {
                        ObjectDateTimeNormalizer.AllDateTimesToUTC(meta);
                        metaInfo[(UInt64)meta.channelId] = meta;
                    }


                    db.Close();
                    db.Dispose();
                }
            }
            catch (Exception e)
            {
                Helpers.logToFile(new string[] { "Failed to save data to database.", e.ToString() });
            }
        }

        // Logic on how regularly we mention someone at most depending on how active he is
        // Last j 1 day ago: 4.8 minutes (its still limited to like 15 minutes elsewhere)
        // Last j 7 days ago: 54 minutes
        // Last j 14 days ago: 129 minutes
        // Last j 1 month ago: 5.6 hours
        // Last j 2 months ago: 13.35 hours
        // In addition, people who haven't j'd for very long are only @ towards the end of filling a queue, to get the last few js
        private const double daysSinceJExponent = 1.25;// 1.55;
        private const double daysSinceJFactor = 4.8;

        class MentionSettings
        {
            public double ActiveJoinReminderDelayMinutes = double.PositiveInfinity; // for when ppl are afk
            public double ActiveJoinReminderRepeatDelayMinutes = double.PositiveInfinity; // for when ppl are afk
            public double ActiveJoinAfkRemovalDelayMinutes = double.PositiveInfinity; // for when ppl are afk
            public bool doEveryone;
            public bool doMentions;
            public double EveryoneMinutesDelayMin;
            public double MentionMessaageMinutesDelayMin;
            public double HourlyRatioMin;
            public double LastTimeJoinedDaysMax;
            public double LastTimeMentionedMinutesMin;
            public double LastTimeExpiredFastTrackMax;
            public double LastTimeWrittenMessageMinutesMin;
            public double LastTimeWrittenMessageDaysMax;
            public double LastTimeReactedMinutesMin;
            public double LastTimeTypedMinutesMin;
            public double RandomChanceMentionPercentage;
        }
        readonly MentionSettings[] settingsLevels = new MentionSettings[]
        {
            new MentionSettings(){ //0-3
                ActiveJoinReminderDelayMinutes = 9*60,
                ActiveJoinReminderRepeatDelayMinutes = 60,
                ActiveJoinAfkRemovalDelayMinutes = 12*60,
                doMentions = false,
                doEveryone = false,
                EveryoneMinutesDelayMin = 180,
                MentionMessaageMinutesDelayMin= 5,
                 HourlyRatioMin = 1.0,
                 LastTimeJoinedDaysMax = 1.0,//7.0,
                 LastTimeMentionedMinutesMin=240.0,
                 LastTimeExpiredFastTrackMax = 5.0,
                 LastTimeWrittenMessageMinutesMin = 60.0,
                 LastTimeWrittenMessageDaysMax = 2.0,
                 LastTimeReactedMinutesMin = 60.0,
                 LastTimeTypedMinutesMin = 30.0,
            },
            new MentionSettings(){ //4-6
                ActiveJoinReminderDelayMinutes = 170,
                ActiveJoinReminderRepeatDelayMinutes = 45,
                ActiveJoinAfkRemovalDelayMinutes = 10*60,
                doMentions = true,
                doEveryone = true,
                EveryoneMinutesDelayMin = 90,
                MentionMessaageMinutesDelayMin= 5,
                 HourlyRatioMin = 1.0,
                 LastTimeJoinedDaysMax = 3.0,//7.0,
                 LastTimeMentionedMinutesMin=180.0,
                 LastTimeExpiredFastTrackMax = 10.0,
                 LastTimeWrittenMessageMinutesMin = 30.0,
                 LastTimeWrittenMessageDaysMax = 5.0,
                 LastTimeReactedMinutesMin = 30.0,
                 LastTimeTypedMinutesMin = 15.0,
            },
            new MentionSettings(){ //6-8
                ActiveJoinReminderDelayMinutes = 120,
                ActiveJoinReminderRepeatDelayMinutes = 30,
                ActiveJoinAfkRemovalDelayMinutes = 180,
                doMentions = true,
                doEveryone = true,
                EveryoneMinutesDelayMin = 45,
                MentionMessaageMinutesDelayMin= 5,
                 HourlyRatioMin = 0.25,
                 LastTimeJoinedDaysMax = 14.0,//7.0,
                 LastTimeMentionedMinutesMin=120.0,
                 LastTimeExpiredFastTrackMax = 20.0,
                 LastTimeWrittenMessageMinutesMin = 20.0,
                 LastTimeWrittenMessageDaysMax = 7.0,
                 LastTimeReactedMinutesMin = 20.0,
                 LastTimeTypedMinutesMin = 10.0,
            },
            new MentionSettings(){ // 9 -11
                ActiveJoinReminderDelayMinutes = 100,
                ActiveJoinReminderRepeatDelayMinutes = 10,
                ActiveJoinAfkRemovalDelayMinutes = 125,
                doMentions = true,
                doEveryone = true,
                EveryoneMinutesDelayMin = 15,
                MentionMessaageMinutesDelayMin= 2,
                 HourlyRatioMin = 0.1,
                 LastTimeJoinedDaysMax = 30.0,//7.0,
                 LastTimeMentionedMinutesMin=60.0,
                 LastTimeExpiredFastTrackMax = 30.0,
                 LastTimeWrittenMessageMinutesMin = 10.0,
                 LastTimeWrittenMessageDaysMax = 31.0,
                 LastTimeReactedMinutesMin = 10.0,
                 LastTimeTypedMinutesMin = 5.0,
            },
            new MentionSettings(){ // Last j
                ActiveJoinReminderDelayMinutes = 80,
                ActiveJoinReminderRepeatDelayMinutes = 5,
                ActiveJoinAfkRemovalDelayMinutes = 110,
                doMentions = true,
                doEveryone = true,
                EveryoneMinutesDelayMin = 8,
                MentionMessaageMinutesDelayMin= 2,
                 HourlyRatioMin = 0.05,
                 LastTimeJoinedDaysMax = 120.0,//7.0,
                 LastTimeMentionedMinutesMin=30.0,
                 LastTimeExpiredFastTrackMax = 60.0,
                 LastTimeWrittenMessageMinutesMin = 5.0,
                 LastTimeWrittenMessageDaysMax = 120.0,
                 LastTimeReactedMinutesMin = 5.0,
                 LastTimeTypedMinutesMin = 2.5,
                 RandomChanceMentionPercentage= 0.3
            },
        };

        enum RemindType
        {
            Remind,
            Remove
        }

        Random rnd = new Random();
        private async Task MainLoopForChannel(UInt64 channelId)
        {
            if (!MentionsActive) return;
            try
            {

                if (!currentBotInfo.ContainsKey(channelId))
                {
                    return;
                }
                BotMessageInfo botInfo = currentBotInfo[channelId];

                if (pickingActive.ContainsKey(channelId) && pickingActive[channelId].HasValue) return; // don't do anything if pickup is active.

                if (lastPlayerCountIncrease.ContainsKey(channelId) && (DateTime.UtcNow- lastPlayerCountIncrease[channelId]).TotalMinutes > 60) return; // Last j was over an hour ago. Fair to say its dead

                HashSet<UInt64> prefilteredUsers = new HashSet<ulong>();
                HashSet<UInt64> prefilteredUsersToRemind = new HashSet<ulong>();
                Dictionary<UInt64, RemindType> remindUsersType = new Dictionary<ulong, RemindType>();
                Dictionary<UInt64, double> afkTimes = new Dictionary<ulong, double>();

                bool doMessage = false;
                bool doEveryone = false;

                
                MentionSettings mentionSettings;

                if (botInfo.joinedPlayerCount <= botInfo.totalPlayerCount / 4)
                {
                    // Less than quarter full. 
                    // Just sit still for now, don't spam people
                    mentionSettings = settingsLevels[0];
                }
                else if (botInfo.joinedPlayerCount < botInfo.totalPlayerCount / 2)
                {
                    // Less than half full. 
                    // Notify regular players
                    mentionSettings = settingsLevels[1];
                }
                else if (botInfo.joinedPlayerCount < (botInfo.totalPlayerCount * 3 / 4))
                {
                    // Less than three quarters full
                    // Notify a bit more aggressively
                    mentionSettings = settingsLevels[2];

                }
                else if (botInfo.joinedPlayerCount <= (botInfo.totalPlayerCount-1))
                {
                    // Almost full. Go relatively hard.
                    mentionSettings = settingsLevels[3];

                }
                else
                {
                    // One missing! Go really hard.
                    mentionSettings = settingsLevels[4];

                }


                DateTime? lastGameOver = metaInfo[channelId].lastGameOver;
                doEveryone = (lastPlayerCountIncreaseWithoutEveryoneMention.ContainsKey(channelId) && lastPlayerCountIncreaseWithoutEveryoneMention[channelId].HasValue && (
                        !metaInfo[channelId].latestEveryoneMention.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestEveryoneMention.Value).TotalSeconds > 15
                    )) || !metaInfo[channelId].latestEveryoneMention.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestEveryoneMention.Value).TotalMinutes > mentionSettings.EveryoneMinutesDelayMin;
                doMessage = !metaInfo[channelId].latestMentionMessageSent.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestMentionMessageSent.Value).TotalMinutes > mentionSettings.MentionMessaageMinutesDelayMin;
                //if (mentionSettings.doMentions)
                {
                    foreach (KeyValuePair<Tuple<ulong, ulong>, UserChannelActivity> thisUserChanActivity in userChannelActivity)
                    {
                        double daysSinceJ = 0;
                        if ((UInt64)thisUserChanActivity.Value.channelId != channelId)
                        {
                            continue;
                        }
                        if (botInfo.userIds.Contains((UInt64)thisUserChanActivity.Value.userId))
                        {
                            // Already in queue
                            // Check if he has been afk for some time...
                            DateTime? lastActivity = null;
                            if(thisUserChanActivity.Value.lastTimeWrittenMessage.HasValue && (!lastActivity.HasValue || thisUserChanActivity.Value.lastTimeWrittenMessage > lastActivity.Value))
                            {
                                lastActivity = thisUserChanActivity.Value.lastTimeWrittenMessage;
                            }
                            if(thisUserChanActivity.Value.lastTimeReacted.HasValue && (!lastActivity.HasValue || thisUserChanActivity.Value.lastTimeReacted > lastActivity.Value))
                            {
                                lastActivity = thisUserChanActivity.Value.lastTimeReacted;
                            }
                            if(thisUserChanActivity.Value.lastTimeTyped.HasValue && (!lastActivity.HasValue || thisUserChanActivity.Value.lastTimeTyped > lastActivity.Value))
                            {
                                lastActivity = thisUserChanActivity.Value.lastTimeTyped;
                            }
                            double minuteSinceActive = (DateTime.UtcNow - lastActivity.Value).TotalMinutes;
                            if (lastActivity.HasValue && minuteSinceActive > mentionSettings.ActiveJoinReminderDelayMinutes )
                            {
                                if (!thisUserChanActivity.Value.lastTimeActiveJoinReminded.HasValue || (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeActiveJoinReminded.Value).TotalMinutes > mentionSettings.ActiveJoinReminderRepeatDelayMinutes)
                                {
                                    prefilteredUsersToRemind.Add((UInt64)thisUserChanActivity.Value.userId);
                                    bool removeUser = minuteSinceActive > mentionSettings.ActiveJoinAfkRemovalDelayMinutes;
                                    if (removeUser)
                                    {

                                        if (!thisUserChanActivity.Value.lastTimeActiveJoinReminded.HasValue)
                                        {
                                            removeUser = false;
                                        } else 
                                        {
                                            // Dont remove user if he wasn't reminded yet.
                                            if (thisUserChanActivity.Value.lastTimeActiveJoinReminded <= (DateTime.UtcNow - new TimeSpan(0, 0, (int)(minuteSinceActive*60.0))))
                                            {
                                                removeUser = false;
                                            }
                                        }
                                        //UpdateUserRemindedOfJoinSoft((UInt64)thisUserChanActivity.Value.userId,channelId,DateTime.UtcNow);
                                    }
                                    remindUsersType[(UInt64)thisUserChanActivity.Value.userId] = removeUser ? RemindType.Remove : RemindType.Remind;
                                    afkTimes[(UInt64)thisUserChanActivity.Value.userId] = minuteSinceActive;
                                }
                            }
                            continue; 
                        }
                        if (thisUserChanActivity.Value.ignoreUser)
                        {
                            continue; // Leave him alone.
                        }
                        if(mentionSettings.RandomChanceMentionPercentage > 0.0)
                        {
                            double rndVal;
                            lock (rnd)
                            {
                                rndVal = rnd.NextDouble();
                            }
                            rndVal *= 100.0;
                            if(rndVal < mentionSettings.RandomChanceMentionPercentage)
                            {
                                prefilteredUsers.Add((UInt64)thisUserChanActivity.Value.userId);
                                continue;
                            }
                        }
                        double hourlyRatio = thisUserChanActivity.Value.getNormalizedHourlyJRatio(DateTime.UtcNow.Hour);
                        if (hourlyRatio < mentionSettings.HourlyRatioMin)
                        {
                            if(hourlyRatio > 0)
                            {
                                if (users.ContainsKey(thisUserChanActivity.Key.Item1))
                                {
                                    Debug.WriteLine($"Hourly ratio too low: Player {users[thisUserChanActivity.Key.Item1].userName}, ratio {hourlyRatio}, required ratio {mentionSettings.HourlyRatioMin}");
                                }
                                else
                                {
                                    Debug.WriteLine($"Hourly ratio too low: Unknown user, ratio {hourlyRatio}, required ratio {mentionSettings.HourlyRatioMin}");
                                }
                            }
                            continue; // This is not a common time for this player to join
                        }
                        if (thisUserChanActivity.Value.lastTimeWrittenMessage.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeWrittenMessage.Value).TotalDays > mentionSettings.LastTimeWrittenMessageDaysMax)
                        {
                            continue; // Didn't write for quite some time
                        }
                        if (!thisUserChanActivity.Value.lastTimeJoined.HasValue || (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays > mentionSettings.LastTimeJoinedDaysMax)
                        {
                            continue; // Didn't play in the past 90 days, leave him alone
                        }
                        daysSinceJ = (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays;
                        double lastTimeMentionedMin = Math.Max(mentionSettings.LastTimeMentionedMinutesMin, daysSinceJFactor * Math.Pow(daysSinceJ, daysSinceJExponent));
                        if (thisUserChanActivity.Value.lastTimeExpired.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeExpired.Value).TotalMinutes < mentionSettings.LastTimeExpiredFastTrackMax)
                        {
                            // Fast-track if recently expired
                            lastTimeMentionedMin = 10;
                        }
                        if (thisUserChanActivity.Value.lastTimeMentioned.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeMentioned.Value).TotalMinutes < lastTimeMentionedMin)
                        {
                            if (lastGameOver.HasValue && lastGameOver > thisUserChanActivity.Value.lastTimeMentioned && thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays < 1.0)
                            {
                                // Continue anyway. He was mentioned recently yes, but a game ended since then.
                            } else
                            {
                                continue; // Was already mentioned in last 15 minutes, don't bother him.
                            }
                        }
                        if (thisUserChanActivity.Value.lastTimeWrittenMessage.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeWrittenMessage.Value).TotalMinutes < mentionSettings.LastTimeWrittenMessageMinutesMin)
                        {
                            if (lastGameOver.HasValue && lastGameOver > thisUserChanActivity.Value.lastTimeWrittenMessage && thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays < 1.0)
                            {
                                // Continue anyway. He was here recently yes, but a game ended since then.
                            }
                            else
                            {
                                continue; // He was here not too long ago, we don't need to explicitly tell him
                            }
                        }
                        if (thisUserChanActivity.Value.lastTimeReacted.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeReacted.Value).TotalMinutes < mentionSettings.LastTimeReactedMinutesMin)
                        {
                            if (lastGameOver.HasValue && lastGameOver > thisUserChanActivity.Value.lastTimeReacted && thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays < 1.0)
                            {
                                // Continue anyway. He was here recently yes, but a game ended since then.
                            }
                            else
                            {
                                continue; // He was here not too long ago, we don't need to explicitly tell him
                            }
                        }
                        if (thisUserChanActivity.Value.lastTimeTyped.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeTyped.Value).TotalMinutes < mentionSettings.LastTimeTypedMinutesMin)
                        {
                            if (lastGameOver.HasValue && lastGameOver > thisUserChanActivity.Value.lastTimeTyped && thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays < 1.0)
                            {
                                // Continue anyway. He was here recently yes, but a game ended since then.
                            }
                            else
                            {
                                continue; // He was here not too long ago, we don't need to explicitly tell him
                            }
                        }
                        prefilteredUsers.Add((UInt64)thisUserChanActivity.Value.userId);
                    }
                }
                if (!mentionSettings.doMentions && !TestMode)
                {
                    prefilteredUsers.Clear();
                }

                bool anyMsgSent = false;

                // Now check which of the prefiltered users are members still.
                DSharpPlus.Entities.DiscordChannel channel = await discordClient.GetChannelAsync(channelId);
                var guild = channel.Guild;
                var members = await guild.GetAllMembersAsync();
                HashSet<UInt64> usersWhoAreStillInTheDiscord = new HashSet<ulong>();
                HashSet<UInt64> usersWhoAreStillInTheDiscordRemind = new HashSet<ulong>();
                Dictionary<UInt64, DiscordMember> memberDetails = new Dictionary<ulong, DiscordMember>();
                foreach (var member in members)
                {
                    if (prefilteredUsers.Contains(member.Id))
                    {
                        memberDetails[member.Id] = member;
                        usersWhoAreStillInTheDiscord.Add(member.Id);
                    }
                    if (prefilteredUsersToRemind.Contains(member.Id))
                    {
                        memberDetails[member.Id] = member;
                        usersWhoAreStillInTheDiscordRemind.Add(member.Id);
                    }
                }
                StringBuilder message = new StringBuilder();
                if (TestMode)
                {
                    message.Append("Testing, would @: ");
                }
                foreach (var user in usersWhoAreStillInTheDiscord)
                {
                    UpdateUserMentioned(user, channel.Id, DateTime.UtcNow);
                    if (TestMode)
                    {
                        if (memberDetails.ContainsKey(user))
                        {
                            message.Append($"{memberDetails[user].DisplayName} ");
                        }
                    }
                    else
                    {
                        message.Append($"<@{user}> ");
                    }
                }
                if (usersWhoAreStillInTheDiscord.Count > 0 && doMessage)
                {

                    enqueueMessage(message.ToString(), channel.Id);
                    metaInfo[channelId].latestMentionMessageSent = DateTime.UtcNow;
                    anyMsgSent = true;
                }

                message.Clear();
                StringBuilder messageRemove = new StringBuilder();
                int remindCount = 0;
                int removeCount = 0;
                if (TestMode)
                {
                    message.Append("Testing, would remind: ");
                    messageRemove.Append("Testing, would remove: ");
                } else
                {
                    messageRemove.Append($"maybe remove, afk: ");
                }
                foreach (var user in usersWhoAreStillInTheDiscordRemind)
                {
                    UpdateUserRemindedOfJoin(user, channel.Id, DateTime.UtcNow);
                    StringBuilder stringToAppend = message;
                    if (remindUsersType[user] == RemindType.Remove)
                    {
                        stringToAppend = messageRemove;
                        removeCount++;
                    } else
                    {
                        remindCount++;
                    }
                    if (TestMode)
                    {
                        if (memberDetails.ContainsKey(user))
                        {

                            stringToAppend.Append($"{memberDetails[user].DisplayName} *({(int)afkTimes[user]} mins afk)* ");
                        }
                    }
                    else
                    {
                        stringToAppend.Append($"<@{user}> *({(int)afkTimes[user]} mins afk)* ");
                    }
                }
                message.Append($"still here?");
                if (remindCount > 0/* && doMessage*/)
                {

                    enqueueMessage(message.ToString(), channel.Id);
                    anyMsgSent = true;
                }
                if (removeCount > 0/* && doMessage*/)
                {

                    enqueueMessage(messageRemove.ToString(), channel.Id);
                    anyMsgSent = true;
                }

                message.Clear();
                // @everyone?
                if (doEveryone && mentionSettings.doEveryone)
                {
                    int playerDelta = botInfo.totalPlayerCount - botInfo.joinedPlayerCount;
                    metaInfo[channelId].latestEveryoneMention = DateTime.UtcNow;
                    if (TestMode)
                    {
                        enqueueMessage($"@ everyone {playerDelta}", channel.Id);

                    }
                    else
                    {
                        enqueueMessage($"@everyone {playerDelta}", channel.Id);
                    }
                    anyMsgSent = true;
                    lastPlayerCountIncreaseWithoutEveryoneMention[channelId] = null;
                }

                if (!messagesSinceLastWho.ContainsKey(channel.Id))
                {
                    messagesSinceLastWho[channel.Id] = 0;
                }
                if (anyMsgSent && messagesSinceLastWho[channel.Id]>=8)
                {
                    enqueueMessage("=who", channel.Id);
                    messagesSinceLastWho[channel.Id] = 0;
                }

                SaveData();
            }catch(Exception e)
            {
                Debug.WriteLine(e.ToString());
                Helpers.logToFile(e.ToString());
            }
        }

        private void resetPickingBtn_Click(object sender, RoutedEventArgs e)
        {
            UInt64[] keys = pickingActive.Keys.ToArray();
            foreach (UInt64 key in keys)
            {
                pickingActive[key] = null;
            }
        }

        private void msgSendSendBtn_Click(object sender, RoutedEventArgs e)
        {

            UInt64? channelId = msgSendChannelCombo.SelectedItem as UInt64?;
            if (channelId is null) return;
            if (!channels.ContainsKey(channelId.Value)) return;
            string msg = msgSendMsgTxt.Text;
            if (string.IsNullOrWhiteSpace(msg)) return;
            enqueueMessage(msg,channelId.Value);
        }

    }

}
