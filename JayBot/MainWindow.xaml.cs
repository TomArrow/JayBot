using DSharpPlus;
using DSharpPlus.Entities;
using JayBot.SQLMappings;
using SQLite.Net2;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private DiscordClient discordClient = null;

        ulong botId = 845098024421425183;
        //ulong? channelId = null;

        Regex regex = new Regex(@">\s*\*\*((\d+)v(\d+)[^*]*)\*\*\s*\(\s*(\d+)\s*\/\s*(\d+)\)\s*\|\s*((`([^`]+)`\/?)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex regexDraftStage = new Regex(@"\*\*((\d+)v(\d+)[^*]*)\*\*\s*is now on the draft stage!", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex regexGameResults = new Regex(@"```markdown(\\n|\n)((\d+)v(\d+)[^\(]*)\([^\)]*\)\s*results", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex regexMatchCanceled = new Regex(@"your match has been canceled.\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        Regex nicknameFilterRegex = new Regex(@"([`<>\*_\\\[\]\~])|((?=\s)[^ ])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        ConcurrentDictionary<UInt64, string[]> channels = new ConcurrentDictionary<ulong, string[]>();

        public bool TestMode { get; set; } = false;

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
            foreach (var channel in channels)
            {
                scanChannelHistory(channel.Key);
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
        DateTime lastMessageSent = DateTime.Now;
        int millisecondTimeout = 5000;
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
                    if ((DateTime.Now - lastMessageSent).TotalMilliseconds < millisecondBuffer + millisecondTimeout) continue; // Shouldn't even be necessary but just to be safe.
                    //if (channelId == null) continue;
                    Tuple<string, ulong> messageToSend = null;
                    lock (messagesToSend)
                    {
                        if (messagesToSend.Count == 0) continue;
                        messageToSend = messagesToSend.Dequeue();
                    }
                    if (messageToSend == null) continue; // Shouldn't be the case but just in case.
                    var channel = await discordClient.GetChannelAsync(messageToSend.Item2);
                    await discordClient.SendMessageAsync(channel, messageToSend.Item1);
                    lastMessageSent = DateTime.Now;
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

            discordClient.ConnectAsync();

        }

        private Task DiscordClient_MessageReactionAdded(DiscordClient sender, DSharpPlus.EventArgs.MessageReactionAddEventArgs args)
        {
            UpdateUserReacted(args.User.Id,args.Channel.Id,args.Message.CreationTimestamp.UtcDateTime);
            return null;
        }

        ConcurrentDictionary<UInt64, BotMessageInfo> currentBotInfo = new ConcurrentDictionary<ulong, BotMessageInfo>();
        ConcurrentDictionary<UInt64, DateTime?> pickingActive = new ConcurrentDictionary<ulong, DateTime?>();

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

            BotMessageInfo botInfo = analyzeMessage(e.Message, channel);

            if(botInfo != null)
            {
                currentBotInfo[channel.Id] = botInfo;
                if(botInfo.pickingIndicator == QueuePickupIndicator.Picking)
                {
                    pickingActive[channel.Id] = DateTime.UtcNow;
                } else if (botInfo.pickingIndicator == QueuePickupIndicator.PickingEnded)
                {
                    pickingActive[channel.Id] = null;
                } else if (botInfo.pickingIndicator == QueuePickupIndicator.Unknown)
                {
                    if (pickingActive.ContainsKey(channel.Id))
                    {
                        DateTime? lastPicking = pickingActive[channel.Id];
                        if (lastPicking.HasValue &&  (DateTime.UtcNow - lastPicking.Value).TotalMinutes > 60)
                        {
                            // Reset after 1 hour in case something glitches. dumb solution but whatever.
                            pickingActive[channel.Id] = null;
                        }
                    }
                }
            }

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

        private void scanHistoryBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach(var kvp in channels)
            {
                scanChannelHistory(kvp.Key);
            }
        }


        ConcurrentDictionary<UInt64,ConcurrentBag<DSharpPlus.Entities.DiscordMember>> members = new ConcurrentDictionary<UInt64, ConcurrentBag<DSharpPlus.Entities.DiscordMember>>();

        private async void scanChannelHistory(UInt64 channelId)
        {
            // Scan half a year max
            DSharpPlus.Entities.DiscordChannel channel = await discordClient.GetChannelAsync(channelId);

            var guild = await discordClient.GetGuildAsync(channel.GuildId.GetValueOrDefault(0));
            var botUser = await discordClient.GetUserAsync(botId);

            members[channelId] = new ConcurrentBag<DSharpPlus.Entities.DiscordMember>();
            IReadOnlyCollection<DSharpPlus.Entities.DiscordMember> membersTmp = await guild.GetAllMembersAsync();

            if (membersTmp is null) return;

            StringBuilder membersDebug = new StringBuilder();

            HashSet<UInt64> memberIds = new HashSet<UInt64>();
            foreach (DSharpPlus.Entities.DiscordMember member in membersTmp)
            {
                members[channelId].Add(member);
                membersDebug.Append($"{member.Id}: DisplayName: {member.DisplayName}, Nickname: {member.Nickname}, Username: {member.Username}\n");
                memberIds.Add(member.Id);
                UpdateUser(member);
            }
            membersDebug.Append("\n\n");

            foreach (var user in users)
            {
                UpdateUserCurrentlyMember(user.Key,channel.Id,memberIds.Contains(user.Key));
            }

            File.AppendAllText("membersDebug.log", membersDebug.ToString());

            IReadOnlyList<DSharpPlus.Entities.DiscordMessage> messages = await channel.GetMessagesAsync();

            AnalyzeMessages(messages,channel);
            SaveData();


            DSharpPlus.Entities.DiscordMessage newestMessage = messages.Count > 0 ? messages[0] : null;
            DateTime newestMessageTime = newestMessage.CreationTimestamp.UtcDateTime;
            DSharpPlus.Entities.DiscordMessage oldestMessage = messages.Count > 0 ? messages[messages.Count - 1] : null;

            while (oldestMessage != null && (DateTime.UtcNow- oldestMessage.CreationTimestamp.UtcDateTime).TotalDays < 180)
            {
                lastMessageAnalyzedText.Text = "Message backwards analysis done to: (max 180 days) " + oldestMessage.CreationTimestamp.UtcDateTime.ToString();
                messages = await channel.GetMessagesBeforeAsync(oldestMessage.Id);
                AnalyzeMessages(messages, channel);
                SaveData();
                oldestMessage = messages.Count > 0 ? messages[messages.Count - 1] : null;
                if(metaInfo[channel.Id].latestMessageCrawled.HasValue && metaInfo[channel.Id].latestMessageCrawled.Value > oldestMessage.CreationTimestamp.UtcDateTime)
                {
                    // This was already crawled
                    break;
                }
            }
            metaInfo[channel.Id].latestMessageCrawled = newestMessageTime;
            lastMessageAnalyzedText.Text += " [Finished]";

            SaveData();
        }

        private void AnalyzeMessages(IReadOnlyList<DSharpPlus.Entities.DiscordMessage> messages, DSharpPlus.Entities.DiscordChannel channel)
        {
            if (messages is null) return;
            foreach (DSharpPlus.Entities.DiscordMessage message in messages)
            {
                analyzeMessage(message,channel);
            }
        }

        private BotMessageInfo analyzeMessage(DSharpPlus.Entities.DiscordMessage message,DSharpPlus.Entities.DiscordChannel channel)
        {
            DateTime thisMessageTime = message.CreationTimestamp.UtcDateTime;
            UpdateUser(message.Author);
            if (message.Author.Id == botId)
            {
                BotMessageInfo result = analyzeBotMessage(message);
                if (result is null) return null;
                if (!channels[channel.Id].Contains(result.queuename)) return null;
                foreach (var member in result.members)
                {
                    UpdateUserJoined(member.Id, channel.Id, thisMessageTime);
                }
                return result;
            } else
            {
                UpdateUserWrittenMessage(message.Author.Id, channel.Id, thisMessageTime);
                if (message.MentionedUsers != null)
                {
                    foreach (var user in message.MentionedUsers)
                    {
                        UpdateUserMentioned(user.Id, channel.Id, thisMessageTime);
                    }
                }
                return null;
            }
            
        }

        private void AscertainUserActivityExists(Tuple<UInt64, UInt64> userAndChannelId)
        {
            if (!userChannelActivity.ContainsKey(userAndChannelId))
            {
                userChannelActivity[userAndChannelId] = new UserChannelActivity() { channelId = (Int64)userAndChannelId.Item2, userId = (Int64)userAndChannelId.Item1 };
            }
        }
        //private void UpdateValueIfNeeded(ref DateTime? reference, DateTime when)
        //{
        //    if (!reference.HasValue || reference.Value < when)
        //    {
        //        reference = when;
        //    }
        //}
        private void UpdateUser(DSharpPlus.Entities.DiscordUser member)
        {
            if (!users.ContainsKey(member.Id))
            {
                users[member.Id] = new User() { discordId = (Int64)member.Id };
            }
            users[member.Id].discordId = (Int64)member.Id;
            users[member.Id].userName = member.Username;
        }
        private void UpdateUser(DSharpPlus.Entities.DiscordMember member)
        {
            if (!users.ContainsKey(member.Id))
            {
                users[member.Id] = new User() { discordId = (Int64)member.Id };
            }
            users[member.Id].discordId = (Int64)member.Id;
            users[member.Id].displayName = member.DisplayName;
            users[member.Id].nickName = member.Nickname;
            users[member.Id].userName = member.Username;
        }
        private void UpdateUserIgnore(UInt64 userId, UInt64 channelId, bool ignore)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            userChannelActivity[tuple].ignoreUser = ignore;
        }
        private void UpdateUserCurrentlyMember(UInt64 userId, UInt64 channelId, bool currentlyMember)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            userChannelActivity[tuple].isCurrentlyMember = currentlyMember;
        }
        private void UpdateUserMentioned(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeMentioned.HasValue || userChannelActivity[tuple].lastTimeMentioned.Value < when)
            {
                userChannelActivity[tuple].lastTimeMentioned = when;
            }
        }
        private void UpdateUserJoined(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeJoined.HasValue || userChannelActivity[tuple].lastTimeJoined.Value < when)
            {
                userChannelActivity[tuple].lastTimeJoined = when;
            }
        }
        private void UpdateUserReacted(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeReacted.HasValue || userChannelActivity[tuple].lastTimeReacted.Value < when)
            {
                userChannelActivity[tuple].lastTimeReacted = when;
            }
        }
        private void UpdateUserWrittenMessage(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeWrittenMessage.HasValue || userChannelActivity[tuple].lastTimeWrittenMessage.Value < when)
            {
                userChannelActivity[tuple].lastTimeWrittenMessage = when;
            }
        }
        private void UpdateUserTyped(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeTyped.HasValue || userChannelActivity[tuple].lastTimeTyped.Value < when)
            {
                userChannelActivity[tuple].lastTimeTyped = when;
            }
        }

        enum QueuePickupIndicator
        {
            Unknown,
            Picking,
            PickingEnded
        }
        class BotMessageInfo {
            public UInt64[] userIds;
            public DSharpPlus.Entities.DiscordMember[] members;
            public string queuename;
            public int teamPlayerCount1, teamPlayerCount2;
            public int totalPlayerCount, joinedPlayerCount;
            public QueuePickupIndicator pickingIndicator = QueuePickupIndicator.Unknown;
        }


        private BotMessageInfo analyzeBotMessage(DSharpPlus.Entities.DiscordMessage msg)
        {
            if (!members.ContainsKey(msg.ChannelId))
            {
                return null;
            }

            ConcurrentBag<DSharpPlus.Entities.DiscordMember> membersHere = members[msg.ChannelId];

            Match match = regex.Match(msg.Content);


            // Enable buttons
            int teamPlayerCount1, teamPlayerCount2;
            int totalPlayerCount, joinedPlayerCount;

            // 1 and 2: (6)v(6)
            // 3 aand 4: (7)/(12)
            // 6: [Captures] player names


            if (!match.Success)
            { // Check for pickup /draft stage stuff. We wanna notice picking starting (so we can stop bothering ppl) and picking ending (games reported/canceled)
                QueuePickupIndicator pickingIndicator = QueuePickupIndicator.PickingEnded;
                match = regexGameResults.Match(msg.Content);
                if (!match.Success && msg.Embeds.Count > 0 && msg.Embeds[0].Title != null)
                {
                    pickingIndicator = QueuePickupIndicator.Picking;
                    match = regexDraftStage.Match(msg.Embeds[0].Title);

                }
                string queueName = null;
                if (!match.Success)
                {
                    match = regexMatchCanceled.Match(msg.Content);
                    teamPlayerCount1 = teamPlayerCount2 = 6;
                    totalPlayerCount = teamPlayerCount1 + teamPlayerCount2;
                    joinedPlayerCount = 0;
                    queueName = channels[msg.Channel.Id][0];
                    pickingIndicator = QueuePickupIndicator.PickingEnded;
                } else
                {
                    int.TryParse(match.Groups[2].Value, out teamPlayerCount1);
                    int.TryParse(match.Groups[3].Value, out teamPlayerCount2);
                    totalPlayerCount = teamPlayerCount1 + teamPlayerCount2;
                    joinedPlayerCount = totalPlayerCount;
                    queueName = match.Groups[1].Value;
                }
                return new BotMessageInfo()
                {
                    members = new DSharpPlus.Entities.DiscordMember[0],
                    userIds = new UInt64[0],

                    joinedPlayerCount = joinedPlayerCount,
                    teamPlayerCount1 = teamPlayerCount1,
                    teamPlayerCount2 = teamPlayerCount2,
                    totalPlayerCount = totalPlayerCount,
                    queuename = queueName,
                    pickingIndicator = pickingIndicator
                };
            }



            int.TryParse(match.Groups[2].Value, out teamPlayerCount1);
            int.TryParse(match.Groups[3].Value, out teamPlayerCount2);
            int.TryParse(match.Groups[5].Value, out totalPlayerCount);
            int.TryParse(match.Groups[4].Value, out joinedPlayerCount);
            List<string> players = new List<string>();
            foreach (var capture in match.Groups[8].Captures)
            {
                players.Add(capture.ToString());
            }


            List<UInt64> userIds = new List<ulong>();
            List<DSharpPlus.Entities.DiscordMember> membersFound = new List<DSharpPlus.Entities.DiscordMember>();
            foreach (var member in membersHere)
            {
                string memberName = nicknameFilterRegex.Replace(member.DisplayName, "");
                foreach (string player in players)
                {
                    if (memberName.Equals(player,StringComparison.InvariantCultureIgnoreCase))
                    {
                        userIds.Add(member.Id);
                        membersFound.Add(member);
                    }
                }
            }
            return new BotMessageInfo() { members= membersFound.ToArray(), userIds = userIds.ToArray()
                 , joinedPlayerCount = joinedPlayerCount,
                  teamPlayerCount1 = teamPlayerCount1,
                  teamPlayerCount2 = teamPlayerCount2,
                   totalPlayerCount = totalPlayerCount,
                   queuename = match.Groups[1].Value
            };
        }


        static readonly string dbPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JayBot", "data.db");
        ConcurrentDictionary<UInt64,User> users = new ConcurrentDictionary<UInt64, User>();
        ConcurrentDictionary<Tuple<UInt64,UInt64>,UserChannelActivity> userChannelActivity = new ConcurrentDictionary<Tuple<UInt64, UInt64>, UserChannelActivity>();
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

        ConcurrentDictionary<UInt64, MetaInfo> metaInfo = new ConcurrentDictionary<ulong, MetaInfo>();
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

                    var userQuery = db.Table<User>();
                    var userActivityQuery = db.Table<UserChannelActivity>();
                    var metaInfoQuery = db.Table<MetaInfo>();

                    foreach(User user in userQuery)
                    {
                        users[(UInt64)user.discordId] = user;
                    }
                    foreach(UserChannelActivity userActivity in userActivityQuery)
                    {
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

        private async Task MainLoopForChannel(UInt64 channelId)
        {
            if (!currentBotInfo.ContainsKey(channelId))
            {
                return;
            }
            BotMessageInfo botInfo = currentBotInfo[channelId];

            if (pickingActive.ContainsKey(channelId) && pickingActive[channelId].HasValue) return; // don't do anything if pickup is active.

            HashSet<UInt64> prefilteredUsers = new HashSet<ulong>();


            bool doEveryone = false;

            if (botInfo.joinedPlayerCount <= botInfo.totalPlayerCount / 4)
            {
                // Less than quarter full. 
                // Just sit still for now, don't spam people
                doEveryone = !metaInfo[channelId].latestEveryoneMention.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestEveryoneMention.Value).TotalMinutes > 60;
            }
            else if(botInfo.joinedPlayerCount <= botInfo.totalPlayerCount / 2)
            {
                // Less than half full. 
                // Notify regular players
                doEveryone = !metaInfo[channelId].latestEveryoneMention.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestEveryoneMention.Value).TotalMinutes > 30;
                foreach (KeyValuePair<Tuple<ulong, ulong>, UserChannelActivity> thisUserChanActivity in userChannelActivity)
                {
                    if ((UInt64)thisUserChanActivity.Value.channelId != channelId)
                    {
                        continue;
                    }
                    if (botInfo.userIds.Contains((UInt64)thisUserChanActivity.Value.userId))
                    {
                        continue; // Already in queue
                    }
                    if (thisUserChanActivity.Value.ignoreUser)
                    {
                        continue; // Leave him alone.
                    }
                    if (thisUserChanActivity.Value.lastTimeMentioned.HasValue && (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeMentioned.Value).TotalMinutes < 60)
                    {
                        continue; // Was already mentioned in last 60 minutes, don't bother him.
                    }
                    if (!thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays > 7)
                    {
                        continue; // Didn't play in the past 7 days, leave him alone
                    }
                    if (thisUserChanActivity.Value.lastTimeWrittenMessage.HasValue && (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeWrittenMessage.Value).TotalMinutes < 30)
                    {
                        continue; // He was here not too long ago, we don't need to explicitly tell him
                    }
                    prefilteredUsers.Add((UInt64)thisUserChanActivity.Value.userId);
                }
            }
            else if(botInfo.joinedPlayerCount <= (botInfo.totalPlayerCount * 3/4))
            {
                // Less than three quarters full
                // Notify a bit more aggressively
                doEveryone = !metaInfo[channelId].latestEveryoneMention.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestEveryoneMention.Value).TotalMinutes > 15;
                foreach (KeyValuePair<Tuple<ulong, ulong>, UserChannelActivity> thisUserChanActivity in userChannelActivity)
                {
                    if ((UInt64)thisUserChanActivity.Value.channelId != channelId)
                    {
                        continue;
                    }
                    if (botInfo.userIds.Contains((UInt64)thisUserChanActivity.Value.userId))
                    {
                        continue; // Already in queue
                    }
                    if (thisUserChanActivity.Value.ignoreUser)
                    {
                        continue; // Leave him alone.
                    }
                    if (thisUserChanActivity.Value.lastTimeMentioned.HasValue && (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeMentioned.Value).TotalMinutes < 30)
                    {
                        continue; // Was already mentioned in last 30 minutes, don't bother him.
                    }
                    if (!thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays > 31)
                    {
                        continue; // Didn't play in the past 31 days, leave him alone
                    }
                    if (thisUserChanActivity.Value.lastTimeWrittenMessage.HasValue && (DateTime.UtcNow- thisUserChanActivity.Value.lastTimeWrittenMessage.Value).TotalMinutes < 20)
                    {
                        continue; // He was here not too long ago, we don't need to explicitly tell him
                    }
                    prefilteredUsers.Add((UInt64)thisUserChanActivity.Value.userId);
                }
            } else
            {
                // Almost full. Go hard.
                doEveryone = !metaInfo[channelId].latestEveryoneMention.HasValue || (DateTime.UtcNow - metaInfo[channelId].latestEveryoneMention.Value).TotalMinutes > 5;
                foreach (KeyValuePair<Tuple<ulong, ulong>, UserChannelActivity> thisUserChanActivity in userChannelActivity)
                {
                    if ((UInt64)thisUserChanActivity.Value.channelId != channelId)
                    {
                        continue;
                    }
                    if (botInfo.userIds.Contains((UInt64)thisUserChanActivity.Value.userId))
                    {
                        continue; // Already in queue
                    }
                    if (thisUserChanActivity.Value.ignoreUser)
                    {
                        continue; // Leave him alone.
                    }
                    if (thisUserChanActivity.Value.lastTimeMentioned.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeMentioned.Value).TotalMinutes < 15)
                    {
                        continue; // Was already mentioned in last 15 minutes, don't bother him.
                    }
                    if (!thisUserChanActivity.Value.lastTimeJoined.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeJoined.Value).TotalDays > 90)
                    {
                        continue; // Didn't play in the past 90 days, leave him alone
                    }
                    if (thisUserChanActivity.Value.lastTimeWrittenMessage.HasValue && (DateTime.UtcNow - thisUserChanActivity.Value.lastTimeWrittenMessage.Value).TotalMinutes < 10)
                    {
                        continue; // He was here not too long ago, we don't need to explicitly tell him
                    }
                    prefilteredUsers.Add((UInt64)thisUserChanActivity.Value.userId);
                }
            }

            // Now check which of the prefiltered users are members still.
            DSharpPlus.Entities.DiscordChannel channel = await discordClient.GetChannelAsync(channelId);
            var guild = channel.Guild;
            var members = await guild.GetAllMembersAsync();
            HashSet<UInt64> usersWhoAreStillInTheDiscord = new HashSet<ulong>();
            Dictionary<UInt64, DiscordMember> memberDetails = new Dictionary<ulong, DiscordMember>();
            foreach (var member in members)
            {
                if (prefilteredUsers.Contains(member.Id))
                {
                    memberDetails[member.Id] = member;
                    usersWhoAreStillInTheDiscord.Add(member.Id);
                }
            }
            StringBuilder message = new StringBuilder();
            if (TestMode)
            {
                message.Append("Testing, would @: ");
            }
            foreach(var user in usersWhoAreStillInTheDiscord)
            {
                UpdateUserMentioned(user,channel.Id,DateTime.UtcNow);
                if (TestMode)
                {
                    if (memberDetails.ContainsKey(user))
                    {
                        message.Append($"{memberDetails[user].DisplayName} ");
                    }
                } else
                {
                    message.Append($"<@{user}> ");
                }
            }
            if (usersWhoAreStillInTheDiscord.Count > 0) {

                enqueueMessage(message.ToString(),channel.Id);
            }

            message.Clear();
            // @everyone?
            if (doEveryone)
            {
                int playerDelta = botInfo.totalPlayerCount - botInfo.joinedPlayerCount;
                metaInfo[channelId].latestEveryoneMention = DateTime.UtcNow;
                if (TestMode)
                {
                    enqueueMessage($"@ everyone {playerDelta}", channel.Id);

                } else
                {
                    enqueueMessage($"@everyone {playerDelta}", channel.Id);
                }
            }

            SaveData();
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
