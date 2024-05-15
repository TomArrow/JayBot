using SQLite.Net2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JayBot.SQLMappings
{
    public class UserChannelActivity
    {
        [PrimaryKey]
        public Int64 userId { get; set; }
        [PrimaryKey]
        public Int64 channelId { get; set; }
        public DateTime? lastTimeJoined { get; set; } = null;
        public DateTime? lastTimeWrittenMessage { get; set; } = null;
        public DateTime? lastTimeTyped { get; set; } = null;
        public DateTime? lastTimeReacted { get; set; } = null;
        public DateTime? lastTimeMentioned { get; set; } = null;
        public bool ignoreUser { get; set; } = false;
        public bool isCurrentlyMember { get; set; } = false;
    }
}
