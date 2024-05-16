using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JayBot
{
    static class ObjectDateTimeNormalizer
    {
        public static void AllDateTimesToUTC(object obj)
        {
            var properties = obj.GetType().GetProperties();
            foreach(var property in properties)
            {
                if(property.PropertyType == typeof(DateTime?))
                {
                    DateTime? value = property.GetValue(obj) as DateTime?;
                    property.SetValue(obj, value.HasValue ? value.Value.ToUniversalTime() : null);
                }
            }
        }
    }
}
