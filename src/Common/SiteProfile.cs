using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 单个站点的**连接参数**：并发、间隔、重试、超时、UA。
    ///
    /// 这个类只存"怎么连接"，**不存"怎么提取内容"** —— 后者（选择器 / 正则 / XPath）
    /// 一旦内置或分发，性质就变成"分发书源"了，这是本项目明确不碰的红线。
    /// <see cref="SiteProfileStore.Validate"/> 会检查用户手改的文件里有没有这类参数。
    /// </summary>
    public class SiteProfile
    {
        /// <summary>站点 key（和设置界面里的站点名对应：fanqie / biquga-m / biquga）</summary>
        public string Name = "";
        /// <summary>给用户看的中文名</summary>
        public string DisplayName = "";

        public int Workers = 8;              // 并发线程数
        public int MinDelayMs = 60;          // 请求间隔（随机区间）
        public int MaxDelayMs = 180;
        public int TimeoutSeconds = 25;      // 单次请求超时
        public int MaxRetries = 3;           // 单次请求重试次数
        public string UserAgent = "";        // 空 = 用内置默认

        /// <summary>
        /// HTTP 代理。空 = 直连。
        /// 支持 http:// / https:// / socks5:// 三种写法；也可以只写 "127.0.0.1:7890"（按 http 处理）。
        /// 交给 curl.exe 的 -x 执行，所以 SOCKS 也能用（.NET 原生后端不支持 SOCKS，
        /// 那种情况只有 curl 后端生效）。
        /// </summary>
        public string Proxy = "";

        /// <summary>
        /// Cookie 请求头。空 = 不带。
        /// 用途是「让站点认得你」而不是绕过权限：有些站的封面/详情接口需要登录态
        /// 才返回完整数据，填自己的 Cookie 就能正常拿到。
        /// </summary>
        public string Cookie = "";

        /// <summary>
        /// 输出文件编码。只允许这两种：
        ///  utf-8（默认，带 BOM，手机阅读器/记事本都认）
        ///  gbk（老设备/老阅读器需要）
        /// </summary>
        public string OutputEncoding = "utf-8";

        /// <summary>
        /// 站点域名覆盖（只在 [biquga-m] 有意义）。空 = 用内置默认。
        ///
        /// 这是给"站点自己换域名"准备的逃生口：域名变了不用等作者发新版，
        /// 改一行配置就能继续用。实测 `m.biquga.com` 曾被 CDN 按 SNI 拒掉 TLS，
        /// 而当时域名是 const、运行时改不了，用户只能改代码重编译。
        ///
        /// 注意：换域名**不能**把移动版的快速目录结构带过去 ——
        /// `www.biquga.com/{dir}/dindex_1.html` 返回的是 PC 版页面。
        /// 所以域名不可用时正确的做法是换站点，不是换域名（见 NetDiag 的提示）。
        /// </summary>
        public string MobileHost = "";

        public void Clamp()
        {
            Workers = Clamp(Workers, 1, 32);
            MinDelayMs = Clamp(MinDelayMs, 0, 10000);
            MaxDelayMs = Clamp(MaxDelayMs, MinDelayMs, 10000);
            TimeoutSeconds = Clamp(TimeoutSeconds, 5, 300);
            MaxRetries = Clamp(MaxRetries, 0, 10);
            if (string.IsNullOrEmpty(UserAgent)) UserAgent = "";
            if (string.IsNullOrEmpty(Proxy)) Proxy = "";
            if (string.IsNullOrEmpty(Cookie)) Cookie = "";
            if (string.IsNullOrEmpty(MobileHost)) MobileHost = "";
            if (!IsUtf8(OutputEncoding) && !IsGbk(OutputEncoding)) OutputEncoding = "utf-8";
        }

        public bool IsUtf8(string s) { return !string.IsNullOrEmpty(s) && s.Replace("-", "").ToLowerInvariant() == "utf8"; }
        public bool IsGbk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var v = s.Replace("-", "").ToLowerInvariant();
            return v == "gbk" || v == "gb2312" || v == "gb18030";
        }

        /// <summary>输出文件的编码对象（utf-8 带 BOM / gbk 不带）</summary>
        public Encoding FileEncoding()
        {
            if (IsGbk(OutputEncoding))
            {
                try { return Encoding.GetEncoding("GBK"); } catch { return new UTF8Encoding(true); }
            }
            return new UTF8Encoding(true);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public override string ToString()
        {
            return string.Format("{0}: 并发 {1}，间隔 {2}~{3}ms，超时 {4}s，重试 {5}，编码 {6}",
                DisplayName.Length > 0 ? DisplayName : Name, Workers, MinDelayMs, MaxDelayMs,
                TimeoutSeconds, MaxRetries, OutputEncoding);
        }
    }

    /// <summary>
    /// 站点参数的读写：一个 `站点配置.ini`（键=值，可手改）。
    ///
    /// 为什么还是 ini 而不是 json：和 settings.ini 保持一致（这个项目里所有配置都是
    /// "能用记事本改"的形式），而且 ini 不会被误当成"书源规则文件"。
    /// </summary>
    public static class SiteProfileStore
    {
        public const string FileName = "站点配置.ini";

        /// <summary>被明确拒绝的键（这些属于"内容提取"，不属于"连接参数"）</summary>
        private static readonly string[] ForbiddenKeys = new[]
        {
            "selector", "selectors", "xpath", "regex", "regexp", "pattern", "patterns",
            "rule", "rules", "extract", "extractor", "contentselector", "titleselector",
            "listselector", "chapterrule", "bookrule", "parser", "script", "js", "eval",
            "选择器", "正则", "规则", "提取", "解析规则",
        };

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        /// <summary>
        /// 默认值：按 <see cref="SiteRegistry"/> 给**每一个**注册站点生成一份，
        /// 加站点不用再来这里补一段。少数站点有实测调过的参数（下面几个特例）就覆盖掉。
        /// </summary>
        public static List<SiteProfile> Defaults()
        {
            var list = new List<SiteProfile>();
            foreach (var d in SiteRegistry.All)
            {
                bool mobile = string.Equals(d.Key, "biquga-m", StringComparison.OrdinalIgnoreCase);
                bool fanqie = string.Equals(d.Key, "fanqie", StringComparison.OrdinalIgnoreCase);

                var p = new SiteProfile
                {
                    Name = d.Key,
                    DisplayName = d.DisplayName,
                    Workers = DefaultWorkersFor(d.Key),
                    MinDelayMs = mobile ? 60 : (fanqie ? 120 : 120),
                    MaxDelayMs = mobile ? 180 : (fanqie ? 400 : 400),
                    TimeoutSeconds = 25,
                    MaxRetries = 3,
                    // MobileHost 保持空：它的语义是"**用户**手填的域名覆盖"，
                    // 给它预填一个默认值会让"用户没改过"和"用户改成了同一个值"
                    // 变得无法区分（BiqugaMobileSite.SetHost 里有自己的内置默认）。
                    MobileHost = "",
                };
                list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// 各站点的默认并发。
        ///
        /// ★ 2026-10-19 重新测过（**批量抓取上线之后**，每档 80 章、同一章节段、限流全程 0）：
        ///
        ///   笔尖中文：6→78.9ms  12→79.3ms  16→79.2ms  20→79.9ms  24→77.4ms
        ///
        ///   **基本是一条平线**：6 并发和 24 并发一样快（差异在噪声内）。
        ///   原因：v1.6.0 启用了批量抓取（一个 curl 进程连抓 32 章、复用连接），
        ///   瓶颈已经从"我们的线程数"挪到了**站点侧 / 单连接**；
        ///   再加线程只是让更多批次排队，不会更快。
        ///
        ///   ⚠ 别再引用 2026-10-14 那组旧数据（12→60ms、16→48ms、24→43ms）——
        ///   那是**批量抓取之前**测的，当时每章一个新 curl 进程，线程越多越有效。
        ///   现在这个前提没了，旧数字会把判断带偏 —— 我自己就因此误判过一次，
        ///   还照着旧数据建议过"调到 16~20 能快 1.25 倍"，实测是零收益。
        ///
        /// 为什么仍然保留 12（而不是照平线砍到 6）：
        ///   · 别的站点（笔趣阁 PC / 移动版 / 番茄）**没有**做过同样的批量抓取验证，
        ///     延迟特性不同，砍太低可能变慢；
        ///   · 12 在站点侧仍算克制（实测限流 0），而"少一点并发"换不来速度，
        ///     "稍微多一点"在慢站点上可能真的有用 —— 12 是那个安全中点。
        ///
        /// 所以现在的结论：**并发不再是提速杠杆**，想更快只能换源或改抓取方式。
        /// </summary>
        public static int DefaultWorkersFor(string siteKey)
        {
            if (string.IsNullOrEmpty(siteKey)) return 6;
            if (string.Equals(siteKey, "fanqie", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(siteKey, "biquga-m", StringComparison.OrdinalIgnoreCase)) return 8;
            if (string.Equals(siteKey, "biquga", StringComparison.OrdinalIgnoreCase)) return 6;

            // 规则表站点（笔尖中文 / 笔趣阁365 …）：实测轻、12 并发无限流。
            //
            // ★ 但**只对注册表里真实存在的站点**给 12。
            //   不认识的 key 给保守值 —— 那些可能是用户手写的、或将来新加还没实测的源，
            //   在没有"12 并发不会被限流"的实测依据之前，用 6 更安全。
            if (SiteRegistry.ByKey(siteKey) != null) return 12;
            return 6;
        }

        /// <summary>按站点 key 取参数（文件里没有这一节就用默认值）</summary>
        public static SiteProfile Get(string siteKey)
        {
            var list = Load(DefaultPath);
            foreach (var p in list)
                if (string.Equals(p.Name, siteKey, StringComparison.OrdinalIgnoreCase)) return p;
            foreach (var p in Defaults())
                if (string.Equals(p.Name, siteKey, StringComparison.OrdinalIgnoreCase)) return p;
            return new SiteProfile { Name = siteKey, DisplayName = siteKey };
        }

        /// <summary>
        /// 加载配置。文件不存在 → 返回默认值（并写一份带注释的样例，方便用户手改）。
        /// 文件损坏 → 记下 <see cref="LastError"/> 并回退默认值，绝不让程序起不来。
        /// </summary>
        public static List<SiteProfile> Load(string path)
        {
            LastError = null;
            var result = Defaults();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

                var byName = new Dictionary<string, SiteProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in result) byName[p.Name] = p;

                string section = null;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        if (!byName.ContainsKey(section))
                        {
                            var np = new SiteProfile { Name = section, DisplayName = section };
                            byName[section] = np;
                            result.Add(np);
                        }
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();

                    // ★ 合规闸门：拒绝任何"内容提取"类参数，并在日志/提示里说明原因
                    foreach (var bad in ForbiddenKeys)
                    {
                        if (key.ToLowerInvariant().Replace(" ", "").Contains(bad))
                        {
                            LastError = string.Format(
                                "配置里有「{0}」这类参数 —— 那是「内容提取规则」，本工具不支持也不分发。" +
                                "这个文件只允许放连接参数（并发/间隔/超时/重试/UA/编码）。该行已忽略。", key);
                            continue;
                        }
                    }

                    if (section == null) continue;
                    var prof = byName[section];
                    int n;
                    var k = key.ToLowerInvariant();
                    switch (k)
                    {
                        case "workers": if (int.TryParse(val, out n)) prof.Workers = n; break;
                        case "mindelayms": if (int.TryParse(val, out n)) prof.MinDelayMs = n; break;
                        case "maxdelayms": if (int.TryParse(val, out n)) prof.MaxDelayMs = n; break;
                        case "timeoutseconds": if (int.TryParse(val, out n)) prof.TimeoutSeconds = n; break;
                        case "maxretries": if (int.TryParse(val, out n)) prof.MaxRetries = n; break;
                        case "useragent": if (val.Length > 0) prof.UserAgent = val; break;
                        case "outputencoding": prof.OutputEncoding = val; break;
                        case "displayname": if (val.Length > 0) prof.DisplayName = val; break;
                        case "proxy": prof.Proxy = val; break;
                        case "cookie": prof.Cookie = val; break;
                        case "mobilehost": prof.MobileHost = val; break;
                        // 未知键忽略：以后加参数时老文件也不会报错
                    }
                }
                foreach (var p in result) p.Clamp();
                return result;
            }
            catch (Exception ex)
            {
                LastError = "读取站点配置失败（已回退默认值）：" + ex.Message;
                return Defaults();
            }
        }

        /// <summary>最近一次加载的错误/警告（给界面显示用）</summary>
        public static string LastError;

        /// <summary>写一份带注释的样例配置（首次运行时生成，让用户有东西可改）</summary>
        public static void SaveSample(string path, List<SiteProfile> profiles)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# 站点连接参数（这个文件可以直接用记事本改，改完重启程序生效）");
            sb.AppendLine("#");
            sb.AppendLine("# 只放「怎么连接」的参数：并发 / 请求间隔 / 超时 / 重试 / User-Agent / 输出编码 / 代理 / Cookie。");
            sb.AppendLine("# 本文件**不接受**选择器、正则、XPath 这类「内容怎么提取」的规则 ——");
            sb.AppendLine("# 那是书源规则，本工具不内置、不分发（放进来会被忽略）。");
            sb.AppendLine("#");
            sb.AppendLine("# 调节建议：被站点限速（正文变成「访问太频繁」）就把 workers 调小、间隔调大；");
            sb.AppendLine("#           网络好又想快，可以适当调大 workers，但别超过 16。");
            sb.AppendLine("#");
            sb.AppendLine("# proxy：走代理时填，例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080，");
            sb.AppendLine("#        留空 = 直连。SOCKS 只有 curl 后端支持（Windows 10+ 自带 curl，一般都能用）。");
            sb.AppendLine("# cookie：需要登录态的站点填自己的 Cookie（浏览器 F12 → Network → 请求头里复制整串）。");
            sb.AppendLine("#         这是让站点「认得你」，不是绕过权限 —— 请只填你自己账号的 Cookie。");
            sb.AppendLine("# ============================================================");
            sb.AppendLine();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# 关于本文件末尾可能出现的 [评分] 段");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# 勾上界面上的「自动」（自动选最快站点）之后，程序会实测各站速度，");
            sb.AppendLine("# 并把结果写在本文件末尾的 [评分] 段里。格式（时间单位毫秒，越小越快）：");
            sb.AppendLine("#     站点key=搜索,目录,正文,目录章数,Ticks时间戳,实测书名");
            sb.AppendLine("# 不可用的站点三项耗时**留空**，但时间戳照存 —— 否则 24 小时过期算不准。");
            sb.AppendLine("# **删掉 [评分] 段就会重新实测**；留着则 24 小时内直接用缓存。");
            sb.AppendLine("# 单站实测有 90 秒上限：笔趣阁 PC 版真实遍历要 15 分钟，");
            sb.AppendLine("# 不设上限的话点一次「检测站点」要等一刻钟。");
            sb.AppendLine("# ============================================================");
            sb.AppendLine();
            foreach (var p in profiles)
            {
                sb.AppendLine("[" + p.Name + "]");
                sb.AppendLine("displayname=" + p.DisplayName);
                sb.AppendLine("workers=" + p.Workers);
                sb.AppendLine("mindelayms=" + p.MinDelayMs);
                sb.AppendLine("maxdelayms=" + p.MaxDelayMs);
                sb.AppendLine("timeoutseconds=" + p.TimeoutSeconds);
                sb.AppendLine("maxretries=" + p.MaxRetries);
                sb.AppendLine("outputencoding=" + p.OutputEncoding);
                sb.AppendLine("useragent=" + p.UserAgent);
                sb.AppendLine("proxy=" + p.Proxy);
                sb.AppendLine("cookie=" + p.Cookie);
                if (string.Equals(p.Name, "biquga-m", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("# mobilehost：移动版域名。只有站点自己换域名时才需要改；");
                    sb.AppendLine("# 如果只是 m.biquga.com 在当前网络下连不上（HTTPS 握手被拒），");
                    sb.AppendLine("# 请改用界面上的「笔趣阁（PC版·慢）」，换域名救不了（dindex 路径在 www 下是 PC 版页面）。");
                    sb.AppendLine("mobilehost=" + p.MobileHost);
                }
                sb.AppendLine();
            }
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); }
            catch { /* 写不出样例不影响使用 */ }
        }

        /// <summary>把站点参数真正作用到取数层（和 AppSettings 一起生效）</summary>
        public static void Apply(SiteProfile p)
        {
            if (p == null) return;
            Http.Configure(p.MinDelayMs, p.MaxDelayMs);
            Http.TimeoutSeconds = p.TimeoutSeconds;
            Http.MaxRetries = p.MaxRetries;
            if (!string.IsNullOrEmpty(p.UserAgent)) Http.UserAgent = p.UserAgent;
            // 代理 / Cookie：空字符串也要写回去，否则上一本书设置的代理会残留到这一本
            Http.Proxy = p.Proxy ?? "";
            Http.Cookie = p.Cookie ?? "";
            // 移动版域名覆盖（只有 [biquga-m] 会填）
            if (!string.IsNullOrEmpty(p.MobileHost)) BiqugaMobileSite.SetHost(p.MobileHost);
        }

        public static string CurrentEncodingName(SiteProfile p)
        {
            return p == null ? "utf-8" : p.OutputEncoding;
        }
    }
}
