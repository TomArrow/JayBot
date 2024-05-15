using SQLite.Net2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JayBot.SQLMappings
{
    class MetaInfo
    {
        [PrimaryKey]
        public Int64 channelId { get; set; }
        public DateTime? latestMessageCrawled { get; set; }
        public DateTime? latestEveryoneMention { get; set; }
    }
}
