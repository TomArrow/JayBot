using System;
using SQLite.Net2;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JayBot.SQLMappings
{
    class CrawledMessage
    {
        [PrimaryKey]
        public Int64 messageId { get; set; }
    }
}
