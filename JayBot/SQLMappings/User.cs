using SQLite.Net2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JayBot.SQLMappings
{
    public class User
    {
        [PrimaryKey]
        public Int64 discordId { get; set; }
        public string displayName { get; set; }
        public string nickName { get; set; }
        public string userName { get; set; }
    }
}
