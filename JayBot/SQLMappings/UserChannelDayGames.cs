using SQLite.Net2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace JayBot.SQLMappings
{
    // Not just joined, but actually played ("game started" stage) linked to game id and day
    class UserChannelDayGames
    {
        [PrimaryKey]
        public Int64 userId { get; set; }
        [PrimaryKey]
        public Int64 channelId { get; set; }
        [PrimaryKey]
        public Int64 DayIndex { get; set; } // unix timestamp divided to get day count and roudned
        [PrimaryKey]
        public Int64 GameId { get; set; } // Bot game number
    }
}
