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
    public partial class MainWindow : Window
    {

        private async Task scanChannelHistory(UInt64 channelId)
        {
            // Scan half a year max
            DSharpPlus.Entities.DiscordChannel channel = await discordClient.GetChannelAsync(channelId);

            var guild = await discordClient.GetGuildAsync(channel.GuildId.GetValueOrDefault(0));
            //var botUser = await discordClient.GetUserAsync(botId);

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
                UpdateUserCurrentlyMember(user.Key, channel.Id, memberIds.Contains(user.Key));
            }

            File.AppendAllText("membersDebug.log", membersDebug.ToString());

            IReadOnlyList<DSharpPlus.Entities.DiscordMessage> messages = await channel.GetMessagesAsync();

            BotMessageInfoContainer newestBotInfo = new BotMessageInfoContainer();

            await AnalyzeMessages(messages, channel, newestBotInfo);
            SaveData();


            DSharpPlus.Entities.DiscordMessage newestMessage = messages.Count > 0 ? messages[0] : null;
            DateTime newestMessageTime = newestMessage.CreationTimestamp.UtcDateTime;
            DSharpPlus.Entities.DiscordMessage oldestMessage = messages.Count > 0 ? messages[messages.Count - 1] : null;

            while (oldestMessage != null && (DateTime.UtcNow - oldestMessage.CreationTimestamp.UtcDateTime).TotalDays < 600)
            {
                lastMessageAnalyzedText.Text = "Message backwards analysis done to: (max 600 days) " + oldestMessage.CreationTimestamp.UtcDateTime.ToString();
                messages = await channel.GetMessagesBeforeAsync(oldestMessage.Id);
                if (messages.Count == 0) break;
                await AnalyzeMessages(messages, channel, newestBotInfo);
                SaveData();
                oldestMessage = messages.Count > 0 ? messages[messages.Count - 1] : null;
                if (metaInfo[channel.Id].latestMessageCrawled.HasValue && metaInfo[channel.Id].latestMessageCrawled.Value > oldestMessage.CreationTimestamp.UtcDateTime)
                {
                    // This was already crawled
                    break;
                }
            }
            metaInfo[channel.Id].latestMessageCrawled = newestMessageTime;
            lastMessageAnalyzedText.Text += " [Finished]";

            ProcessLatestBotMessage(newestBotInfo.botInfo, channel.Id);

            SaveData();
        }

        class BotMessageInfoContainer
        {
            public BotMessageInfo botInfo = null;
        }

        private async Task AnalyzeMessages(IReadOnlyList<DSharpPlus.Entities.DiscordMessage> messages, DSharpPlus.Entities.DiscordChannel channel, BotMessageInfoContainer newestBotInfo)
        {
            if (messages is null) return;
            foreach (DSharpPlus.Entities.DiscordMessage message in messages)
            {
                BotMessageInfo botInfo = await analyzeMessage(message, channel);
                if(newestBotInfo.botInfo is null)
                {
                    newestBotInfo.botInfo = botInfo;
                }
            }
        }

        private async Task<BotMessageInfo> analyzeMessage(DSharpPlus.Entities.DiscordMessage message, DSharpPlus.Entities.DiscordChannel channel)
        {
            DateTime thisMessageTime = message.CreationTimestamp.UtcDateTime;
            UpdateUser(message.Author);
            if (message.Author.Id == botId || message.Author.Id == botId2)
            {
                BotMessageInfo result = analyzeBotMessage(message);
                if (result is null) return null;
                if (result.pickingIndicator == QueuePickupIndicator.PickingEnded)
                {
                    if (!metaInfo[channel.Id].lastGameOver.HasValue || metaInfo[channel.Id].lastGameOver < thisMessageTime)
                    {
                        metaInfo[channel.Id].lastGameOver = thisMessageTime;
                    }
                }
                if (result.containsPlayersList)
                {
                    if (!metaInfo[channel.Id].lastBotMessageWithPlayersParsed.HasValue || metaInfo[channel.Id].lastBotMessageWithPlayersParsed < thisMessageTime)
                    {
                        metaInfo[channel.Id].lastBotMessageWithPlayersParsed = thisMessageTime;
                    }
                }
                if (!channels[channel.Id].Contains(result.queuename)) return null;
                foreach (var member in result.members)
                {
                    UpdateUserJoined(member.Id, channel.Id, thisMessageTime);
                    if (!result.messageSeenBefore)
                    {
                        UpdateUserJoinedActivity(member.Id, channel.Id, thisMessageTime);
                    }
                }
                return result;
            }
            else
            {
                if(message.Reactions != null && message.Reactions.Count > 0)
                {
                    // TODO skip this? could it be too slow? idk
                    foreach (var reaction in message.Reactions)
                    {
                        IReadOnlyList<DiscordUser> thisemojireactions = await message.GetReactionsAsync(reaction.Emoji);
                        foreach(var emojiReaction in thisemojireactions)
                        {
                            UpdateUserReacted(emojiReaction.Id,channel.Id,thisMessageTime);
                        }
                    }
                }
                if (message.MentionEveryone)
                {
                    if (!metaInfo[channel.Id].latestEveryoneMention.HasValue || metaInfo[channel.Id].latestEveryoneMention < thisMessageTime)
                    {
                        metaInfo[channel.Id].latestEveryoneMention = thisMessageTime;
                    }
                }
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
        private void UpdateUserRemindedOfJoin(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeActiveJoinReminded.HasValue || userChannelActivity[tuple].lastTimeActiveJoinReminded.Value < when)
            {
                userChannelActivity[tuple].lastTimeActiveJoinReminded = when;
            }
        }
        private void UpdateUserRemindedOfJoinSoft(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeActiveJoinRemindedSoft.HasValue || userChannelActivity[tuple].lastTimeActiveJoinRemindedSoft.Value < when)
            {
                userChannelActivity[tuple].lastTimeActiveJoinRemindedSoft = when;
            }
        }
        private void UpdateUserExpired(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            if (!userChannelActivity[tuple].lastTimeExpired.HasValue || userChannelActivity[tuple].lastTimeExpired.Value < when)
            {
                userChannelActivity[tuple].lastTimeExpired = when;
            }
        }
        private void UpdateUserJoinedActivity(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            userChannelActivity[tuple].hourlyJsHistorical[when.Hour]++;            
        }
        private void UpdateUserWrittenActivity(UInt64 userId, UInt64 channelId, DateTime when)
        {
            var tuple = new Tuple<UInt64, UInt64>(userId, channelId);
            AscertainUserActivityExists(tuple);
            userChannelActivity[tuple].hourlyMessagesHistorical[when.Hour]++;            
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



        private BotMessageInfo analyzeBotMessage(DSharpPlus.Entities.DiscordMessage msg)
        {
            if (!members.ContainsKey(msg.ChannelId))
            {
                return null;
            }

            ConcurrentBag<DSharpPlus.Entities.DiscordMember> membersHere = members[msg.ChannelId];

            bool messageSeenBefore = analyzedMessages.ContainsKey(msg.Id);

            analyzedMessages[msg.Id] = new CrawledMessage() { messageId=(Int64)msg.Id };

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
                bool playerCountKnown = false;
                if (!match.Success)
                {
                    match = regexMatchCanceled.Match(msg.Content);
                    if (match.Success)
                    {
                        teamPlayerCount1 = teamPlayerCount2 = 6;
                        totalPlayerCount = teamPlayerCount1 + teamPlayerCount2;
                        joinedPlayerCount = 0;
                        queueName = channels[msg.Channel.Id][0];
                        pickingIndicator = QueuePickupIndicator.PickingEnded;
                    } else
                    {
                        if(msg.Content.Trim().Equals("> no players",StringComparison.InvariantCultureIgnoreCase))
                        {
                            teamPlayerCount1 = teamPlayerCount2 = 6;
                            totalPlayerCount = teamPlayerCount1 + teamPlayerCount2;
                            joinedPlayerCount = 0;
                            queueName = channels[msg.Channel.Id][0];
                            pickingIndicator = QueuePickupIndicator.Unknown;
                            playerCountKnown = true;
                        } else
                        {
                            match = playerExpiredRegex.Match(msg.Content);
                            if (match.Success)
                            {
                                UInt64 expiredPlayer;
                                if (UInt64.TryParse(match.Groups[1].Value, out expiredPlayer))
                                {
                                    UpdateUserExpired(msg.Id, msg.ChannelId, msg.CreationTimestamp.UtcDateTime);
                                }

                            }
                            return null;
                        }
                    }
                }
                else
                {
                    int.TryParse(match.Groups[2].Value, out teamPlayerCount1);
                    int.TryParse(match.Groups[3].Value, out teamPlayerCount2);
                    totalPlayerCount = teamPlayerCount1 + teamPlayerCount2;
                    joinedPlayerCount = totalPlayerCount;
                    queueName = match.Groups[1].Value;
                }
                return new BotMessageInfo()
                {
                    utcTime = msg.CreationTimestamp.UtcDateTime,
                    members = new DSharpPlus.Entities.DiscordMember[0],
                    userIds = new UInt64[0],

                    joinedPlayerCount = joinedPlayerCount,
                    teamPlayerCount1 = teamPlayerCount1,
                    teamPlayerCount2 = teamPlayerCount2,
                    totalPlayerCount = totalPlayerCount,
                    queuename = queueName,
                    pickingIndicator = pickingIndicator,
                    messageSeenBefore = messageSeenBefore,
                    containsPlayersList = playerCountKnown
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
                    if (memberName.Equals(player, StringComparison.InvariantCultureIgnoreCase))
                    {
                        userIds.Add(member.Id);
                        membersFound.Add(member);
                    }
                }
            }


            return new BotMessageInfo()
            {
                utcTime = msg.CreationTimestamp.UtcDateTime,
                members = membersFound.ToArray(),
                userIds = userIds.ToArray()
                 ,
                joinedPlayerCount = joinedPlayerCount,
                teamPlayerCount1 = teamPlayerCount1,
                teamPlayerCount2 = teamPlayerCount2,
                totalPlayerCount = totalPlayerCount,
                queuename = match.Groups[1].Value,
                messageSeenBefore = messageSeenBefore,
                containsPlayersList = true
            };
        }
    }
}
