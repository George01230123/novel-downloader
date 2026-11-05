using System;

namespace TomatoBiquga
{
    /// <summary>一行队列输入解析后的结果。</summary>
    public sealed class QueueInputLine
    {
        public string Original = "";
        public string Keyword = "";
        public string SiteKey = "";
    }

    /// <summary>任务队列输入的纯解析逻辑，供界面和离线测试共用。</summary>
    public static class QueueInput
    {
        internal static System.Collections.Generic.List<string> Bind(System.Collections.Generic.IEnumerable<string> lines, string defaultSite)
        {
            var result = new System.Collections.Generic.List<string>();
            foreach (var raw in lines)
            {
                var item = Parse(raw);
                if (item.Keyword.Length == 0) continue;
                string key = item.SiteKey;
                Uri url;
                if (Uri.TryCreate(item.Keyword, UriKind.Absolute, out url) &&
                    (url.Scheme == "http" || url.Scheme == "https"))
                {
                    string addressSite = "";
                    if (url.Host.Equals("fanqienovel.com", StringComparison.OrdinalIgnoreCase) ||
                        url.Host.Equals("www.fanqienovel.com", StringComparison.OrdinalIgnoreCase)) addressSite = "fanqie";
                    foreach (var site in SiteRegistry.All)
                    {
                        Uri origin;
                        if (Uri.TryCreate(site.DefaultBase, UriKind.Absolute, out origin) &&
                            origin.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase)) addressSite = site.Key;
                    }
                    if (key.Length == 0) key = addressSite;
                    else if (addressSite.Length > 0 && key != addressSite)
                        throw new ArgumentException("站点前缀与链接所属站点不一致：" + raw);
                }
                if (key.Length == 0) key = defaultSite;
                result.Add(key + ":" + item.Keyword);
            }
            return result;
        }

        public static QueueInputLine Parse(string raw)
        {
            var result = new QueueInputLine
            {
                Original = raw == null ? "" : raw.Trim(),
                Keyword = raw == null ? "" : raw.Trim(),
            };
            if (result.Keyword.Length == 0) return result;

            // URL 的冒号属于协议，不是站点前缀。
            if (result.Keyword.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                result.Keyword.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return result;

            int sep = result.Keyword.IndexOf('：');
            if (sep < 0) sep = result.Keyword.IndexOf(':');
            if (sep <= 0) return result;

            string prefix = result.Keyword.Substring(0, sep).Trim();
            string key = SiteKeyForPrefix(prefix);
            if (string.IsNullOrEmpty(key)) return result;

            result.SiteKey = key;
            result.Keyword = result.Keyword.Substring(sep + 1).Trim();
            return result;
        }

        internal static string SiteKeyForPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return "";
            string p = prefix.Trim();
            string lower = p.ToLowerInvariant();

            if (lower == "番茄" || lower == "fanqie" || lower == "tomato") return "fanqie";
            if (lower == "移动版" || lower == "biquga-m") return "biquga-m";
            if (lower == "pc" || lower == "pc版" || lower == "biquga-pc") return "biquga";
            if (lower == "笔趣阁") return "biquga";

            int byKey = SiteRegistry.IndexOf(p);
            if (byKey >= 0) return SiteRegistry.All[byKey].Key;
            foreach (var d in SiteRegistry.All)
                if (d != null && string.Equals(d.DisplayName, p, StringComparison.OrdinalIgnoreCase))
                    return d.Key;
            return "";
        }
    }
}
