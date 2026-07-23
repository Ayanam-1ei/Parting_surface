using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PartingSurface.Detection
{
    /// <summary>
    /// Minimal JSON serializer using StringBuilder. No external dependencies.
    /// Serializes Dictionary&lt;string,object&gt;, List&lt;object&gt;, string, double, int, bool, null to indented JSON.
    /// </summary>
    internal static class JsonHelper
    {
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int indent)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            Type type = value.GetType();

            if (type == typeof(string))
            {
                WriteString(sb, (string)value);
                return;
            }

            if (type == typeof(char))
            {
                WriteString(sb, value.ToString());
                return;
            }

            if (type == typeof(bool))
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }

            if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) ||
                type == typeof(ushort) || type == typeof(sbyte))
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (type == typeof(double))
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                }
                return;
            }

            if (type == typeof(float))
            {
                float f = (float)value;
                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                }
                return;
            }

            if (type == typeof(decimal))
            {
                sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is IDictionary<string, object>)
            {
                WriteObject(sb, (IDictionary<string, object>)value, indent);
                return;
            }

            if (value is IDictionary)
            {
                WriteObjectFromDict(sb, (IDictionary)value, indent);
                return;
            }

            if (value is IList || value is IEnumerable)
            {
                WriteArray(sb, (IEnumerable)value, indent);
                return;
            }

            // Fallback: treat as string
            WriteString(sb, value.ToString());
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object> dict, int indent)
        {
            if (dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{");
            string padding = GetPadding(indent + 1);
            bool first = true;
            foreach (KeyValuePair<string, object> pair in dict)
            {
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("\n");
                sb.Append(padding);
                WriteString(sb, pair.Key);
                sb.Append(": ");
                WriteValue(sb, pair.Value, indent + 1);
            }
            sb.Append("\n");
            sb.Append(GetPadding(indent));
            sb.Append("}");
        }

        private static void WriteObjectFromDict(StringBuilder sb, IDictionary dict, int indent)
        {
            if (dict.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append("{");
            string padding = GetPadding(indent + 1);
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("\n");
                sb.Append(padding);
                WriteString(sb, entry.Key.ToString());
                sb.Append(": ");
                WriteValue(sb, entry.Value, indent + 1);
            }
            sb.Append("\n");
            sb.Append(GetPadding(indent));
            sb.Append("}");
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, int indent)
        {
            sb.Append("[");
            string padding = GetPadding(indent + 1);
            bool first = true;
            foreach (object item in list)
            {
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("\n");
                sb.Append(padding);
                WriteValue(sb, item, indent + 1);
            }
            if (!first)
            {
                sb.Append("\n");
                sb.Append(GetPadding(indent));
            }
            sb.Append("]");
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static string GetPadding(int indent)
        {
            if (indent <= 0)
            {
                return string.Empty;
            }
            return new string(' ', indent * 2);
        }

        /// <summary>
        /// Deep-clone a Dictionary&lt;string,object&gt; structure (equivalent to Python copy.deepcopy).
        /// </summary>
        public static object DeepClone(object value)
        {
            if (value == null)
            {
                return null;
            }

            Type type = value.GetType();

            if (type == typeof(string))
            {
                return value; // strings are immutable
            }

            if (type.IsPrimitive || type == typeof(decimal))
            {
                return value; // value types are copied
            }

            if (value is Dictionary<string, object>)
            {
                var dict = (Dictionary<string, object>)value;
                var clone = new Dictionary<string, object>(dict.Count);
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    clone[pair.Key] = DeepClone(pair.Value);
                }
                return clone;
            }

            if (value is List<object>)
            {
                var list = (List<object>)value;
                var clone = new List<object>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    clone.Add(DeepClone(list[i]));
                }
                return clone;
            }

            if (value is List<Dictionary<string, object>>)
            {
                var list = (List<Dictionary<string, object>>)value;
                var clone = new List<object>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    clone.Add(DeepClone(list[i]));
                }
                return clone;
            }

            if (value is IList)
            {
                var list = (IList)value;
                var clone = new List<object>(list.Count);
                foreach (object item in list)
                {
                    clone.Add(DeepClone(item));
                }
                return clone;
            }

            // For other types, return as-is (cannot deep clone)
            return value;
        }

        /// <summary>
        /// Merge two dictionaries (equivalent to Python dict(base, **overrides)).
        /// Returns a new dictionary with base values overridden by overrides.
        /// </summary>
        public static Dictionary<string, object> MergeDict(
            Dictionary<string, object> baseDict,
            Dictionary<string, object> overrides)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(baseDict);
            if (overrides != null)
            {
                foreach (KeyValuePair<string, object> pair in overrides)
                {
                    result[pair.Key] = pair.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// Try to get a value from a dictionary as a specific type.
        /// </summary>
        public static T GetAs<T>(Dictionary<string, object> dict, string key, T defaultValue)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                if (value is T)
                {
                    return (T)value;
                }
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// Try to get a double value from a dictionary, handling int/long/double.
        /// </summary>
        public static double? GetAsDouble(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                if (value is double)
                {
                    return (double)value;
                }
                if (value is int)
                {
                    return (double)(int)value;
                }
                if (value is long)
                {
                    return (double)(long)value;
                }
                if (value is float)
                {
                    return (double)(float)value;
                }
                if (value is decimal)
                {
                    return (double)(decimal)value;
                }
                double parsed;
                if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        /// <summary>
        /// Try to get an int value from a dictionary.
        /// </summary>
        public static int? GetAsInt(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                if (value is int)
                {
                    return (int)value;
                }
                if (value is long)
                {
                    return (int)(long)value;
                }
                if (value is double)
                {
                    return (int)(double)value;
                }
                int parsed;
                if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        /// <summary>
        /// Try to get a bool value from a dictionary.
        /// </summary>
        public static bool? GetAsBool(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                if (value is bool)
                {
                    return (bool)value;
                }
                if (value is string)
                {
                    string s = (string)value;
                    if (s == "true" || s == "True") return true;
                    if (s == "false" || s == "False") return false;
                }
            }
            return null;
        }

        /// <summary>
        /// Try to get a string value from a dictionary.
        /// </summary>
        public static string GetAsString(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value) && value != null)
            {
                return value.ToString();
            }
            return null;
        }

        /// <summary>
        /// Try to get a nested dictionary from a dictionary.
        /// </summary>
        public static Dictionary<string, object> GetAsDict(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                if (value is Dictionary<string, object>)
                {
                    return (Dictionary<string, object>)value;
                }
            }
            return null;
        }

        /// <summary>
        /// Try to get a list from a dictionary.
        /// </summary>
        public static List<object> GetAsList(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                if (value is List<object>)
                {
                    return (List<object>)value;
                }
                if (value is List<Dictionary<string, object>>)
                {
                    var list = (List<Dictionary<string, object>>)value;
                    var result = new List<object>(list.Count);
                    foreach (var item in list)
                    {
                        result.Add(item);
                    }
                    return result;
                }
                if (value is IList)
                {
                    var result = new List<object>();
                    foreach (object item in (IList)value)
                    {
                        result.Add(item);
                    }
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// Try to get a list of dictionaries from a dictionary.
        /// </summary>
        public static List<Dictionary<string, object>> GetAsDictList(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict != null && dict.TryGetValue(key, out value))
            {
                if (value is List<Dictionary<string, object>>)
                {
                    return (List<Dictionary<string, object>>)value;
                }
                if (value is List<object>)
                {
                    var list = (List<object>)value;
                    var result = new List<Dictionary<string, object>>();
                    foreach (object item in list)
                    {
                        if (item is Dictionary<string, object>)
                        {
                            result.Add((Dictionary<string, object>)item);
                        }
                    }
                    return result;
                }
            }
            return null;
        }
    }
}
