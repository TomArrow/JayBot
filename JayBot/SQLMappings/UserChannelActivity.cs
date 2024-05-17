using SQLite.Net2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        public DateTime? lastTimeActiveJoinReminded { get; set; } = null;
        public DateTime? lastTimeWrittenMessage { get; set; } = null;
        public DateTime? lastTimeTyped { get; set; } = null;
        public DateTime? lastTimeReacted { get; set; } = null;
        public DateTime? lastTimeMentioned { get; set; } = null;
        public DateTime? lastTimeExpired { get; set; } = null;
        public bool ignoreUser { get; set; } = false;
        public bool isCurrentlyMember { get; set; } = false;
        public double getNormalizedHourlyJRatio(int hour)
        {
            double total = 0;
            foreach (Int64 hourlyJs in hourlyJsHistorical)
            {
                total += hourlyJs;
            }
            if (total <= 0) return 0;
            return (double)hourlyJsHistorical[hour]*24.0 / total;
        }
        public Int64[] hourlyJsHistorical = new Int64[24];
        public string hourlyJsHistoricalJson
        {
            get
            {
                try
                {
                    return JsonSerializer.Serialize(hourlyJsHistorical);
                } catch(Exception e)
                {
                    return "null";
                }
            }
            set
            {
                try
                {
                    var tmp = JsonSerializer.Deserialize<Int64[]>(value);
                    if (tmp is null || tmp.Length < 24)
                    {
                        hourlyJsHistorical = new Int64[24];
                    }
                    else
                    {
                        hourlyJsHistorical = tmp;
                    }
                }
                catch (Exception e)
                {
                    hourlyJsHistorical = new Int64[24];
                }
            }
        }
        public Int64[] hourlyMessagesHistorical = new Int64[24];
        public string hourlyMessagesHistoricalJson
        {
            get
            {
                try
                {
                    return JsonSerializer.Serialize(hourlyMessagesHistorical);
                } catch(Exception e)
                {
                    return "null";
                }
            }
            set
            {
                try
                {
                    var tmp = JsonSerializer.Deserialize<Int64[]>(value);
                    if(tmp is null || tmp.Length < 24)
                    {
                        hourlyMessagesHistorical = new Int64[24];
                    } else
                    {
                        hourlyMessagesHistorical = tmp;
                    }
                }
                catch (Exception e)
                {
                    hourlyMessagesHistorical = new Int64[24];
                }
            }
        }
    }
}
