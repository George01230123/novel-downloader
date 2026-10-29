using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 把网络错误翻译成人话，并给站点做探活。
    ///
    /// 为什么需要它：实测踩到 `m.biquga.com` 被 CDN 按 SNI 拒掉 TLS 握手时，
    /// 界面上只显示：
    ///
    ///     curl 退出码 35
    ///
    /// 这对用户等于没说 —— 他不知道是自己网络的问题、站点挂了、还是要挂代理，
    /// 更不知道**换个站点就能用**。这个模块把这类错误归因成
    /// "哪一层坏了 + 建议怎么办"。
    ///
    /// 归类依据是 curl 的退出码（curl 有明确定义的退出码语义，见 curl 文档）
    /// 加异常文本特征。**宁可归到"未知"也不要瞎猜** —— 猜错的建议会把人带偏。
    /// </summary>
    public static class NetDiag
    {
        /// <summary>一次探活的结果</summary>
        public class ProbeResult
        {
            /// <summary>是否可用</summary>
            public bool Ok;
            /// <summary>给用户看的原因（Ok=false 时有意义）</summary>
            public string Reason = "";
            /// <summary>建议怎么做</summary>
            public string Advice = "";
            /// <summary>curl 退出码（0 = 没跑到 / 成功）</summary>
            public int CurlExit;
            /// <summary>耗时（毫秒）</summary>
            public long ElapsedMs;

            public override string ToString()
            {
                return Ok ? "可用（" + ElapsedMs + "ms）" : Reason;
            }
        }

        /// <summary>
        /// 按 curl 退出码归类。curl 的退出码是有语义的：
        ///   6  无法解析主机        → DNS
        ///   7  无法连接            → TCP 层被拒 / 端口不通
        ///   28 超时                → 超时
        ///   35 SSL/TLS 握手失败    → 最容易被误判成"自己网络坏了"，其实是站点/CDN 侧
        ///   56 接收数据失败        → 连接被重置
        ///   60 证书校验失败        → 证书问题（系统时间不对也会这样）
        /// 归类不出来就返回"未知"，让调用方把原始错误原样显示。
        /// </summary>
        public static string Classify(int curlExit, string rawError, out string advice)
        {
            advice = "";
            switch (curlExit)
            {
                case 6:
                    advice = "检查 DNS 或网络连接；也可能是该域名已失效。";
                    return "域名解析失败";
                case 7:
                    advice = "检查网络/防火墙；若开了代理，确认代理地址和端口填对了。";
                    return "连不上服务器（端口被拒或网络不通）";
                case 28:
                    advice = "站点响应太慢或当前网络拥塞。可以在「设置」里调大超时，稍后重试。";
                    return "请求超时";
                case 35:
                    advice = "这是**站点/CDN 侧**拒绝了 HTTPS 握手，不是你的网络问题。"
                           + "换个站点（例如「笔趣阁 PC版」）通常就能用；挂代理有时也能绕过。";
                    return "HTTPS 握手被拒绝";
                case 51:
                case 60:
                    advice = "先确认系统日期时间是否正确（时间偏差大会导致证书校验失败）。";
                    return "证书校验失败";
                case 56:
                    advice = "多半是站点限流或网络抖动，稍等几分钟重试；被限流时工具会自动降并发。";
                    return "接收数据失败（连接被重置）";
                case 47:
                    advice = "重定向次数过多，通常是站点改版或需要 Cookie。";
                    return "重定向次数过多";
                case 22:
                    advice = "站点返回了错误状态码（常见 403/404/429）。403/429 多为限流或需要 Cookie。";
                    return "站点返回错误状态码";
                case 3:
                    advice = "这一般是程序内部的 URL/参数问题，不是站点问题 —— 请把日志贴到 issue。";
                    return "URL 格式错误";
            }

            // 退出码没命中时看文本特征
            var m = rawError ?? "";
            if (Contains(m, "schannel") || Contains(m, "SSL") || Contains(m, "TLS"))
            {
                advice = "站点/CDN 拒绝了 HTTPS 握手。换个站点通常就能用。";
                return "HTTPS 握手失败";
            }
            if (Contains(m, "无法解析") || Contains(m, "resolve") || Contains(m, "No such host"))
            {
                advice = "检查网络与 DNS。";
                return "域名解析失败";
            }
            if (Contains(m, "超时") || Contains(m, "timeout") || Contains(m, "timed out"))
            {
                advice = "稍后重试，或在「设置」里调大超时。";
                return "请求超时";
            }
            if (Contains(m, "访问太频繁") || Contains(m, "限流"))
            {
                advice = "站点限流。工具会自动退避并降并发；也可以把该站点的 workers 调小、间隔调大。";
                return "被站点限流";
            }
            advice = "";
            return "";
        }

        private static bool Contains(string hay, string needle)
        {
            return hay != null && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>从异常里尽力挖出 curl 退出码（Http 抛的异常文本里带 "curl 退出码 N"）</summary>
        public static int ExtractCurlExit(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            const string marker = "curl 退出码 ";
            int i = message.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += marker.Length;
            int j = i;
            while (j < message.Length && char.IsDigit(message[j])) j++;
            int code;
            if (j > i && int.TryParse(message.Substring(i, j - i), out code)) return code;
            return 0;
        }

        /// <summary>
        /// 对某个 URL 做一次轻量探活。
        /// 用**尽量小的请求**（只要响应头所在的那点内容）：目的是判断"通不通"，
        /// 不是拿数据，所以超时也给得比正常请求短，免得一个坏站点把界面卡住。
        /// </summary>
        public static ProbeResult Probe(string url, string referer = null, int timeoutSeconds = 12)
        {
            var r = new ProbeResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = Http.GetWithTimeoutAndRetries(url, referer, timeoutSeconds, 1);
                r.Ok = !string.IsNullOrEmpty(text);
                if (!r.Ok)
                {
                    r.Reason = "站点返回了空内容";
                    r.Advice = "可能是临时故障，稍后重试。";
                }
            }
            catch (Exception ex)
            {
                r.Ok = false;
                var msg = ex.Message ?? "";
                r.CurlExit = ExtractCurlExit(msg);
                string advice;
                var kind = Classify(r.CurlExit, msg, out advice);
                r.Reason = string.IsNullOrEmpty(kind) ? "请求失败" : kind;
                r.Advice = advice;
                if (r.CurlExit != 0) r.Reason += "（curl 退出码 " + r.CurlExit + "）";
            }
            finally
            {
                sw.Stop();
                r.ElapsedMs = sw.ElapsedMilliseconds;
            }
            return r;
        }

        /// <summary>拼成一行给界面用的话</summary>
        public static string Describe(ProbeResult r)
        {
            if (r == null) return "未检测";
            if (r.Ok) return "可用（" + r.ElapsedMs + "ms）";
            var s = r.Reason;
            if (!string.IsNullOrEmpty(r.Advice)) s += "。" + r.Advice;
            return s;
        }
    }

    /// <summary>
    /// 下载前的磁盘空间检查。
    ///
    /// 为什么需要：一本 2000 章的书正文约 20MB，加上 EPUB 和缓存，
    /// 多下几本很容易吃掉几个 G。而**磁盘写满是在 append 落盘的途中才暴露的**，
    /// 那时已经下了大半本，用户看到的是一句莫名其妙的 IO 异常。
    /// 提前算一下、提前说，成本几乎为零。
    /// </summary>
    public static class DiskCheck
    {
        /// <summary>安全余量：即使算出来够，也至少留这么多（留给 EPUB/缓存/系统）</summary>
        public const long SafetyMarginBytes = 64L * 1024 * 1024;

        /// <summary>估算结果</summary>
        public class Estimate
        {
            public long NeededBytes;
            public long FreeBytes;
            /// <summary>不够时为 false</summary>
            public bool Ok = true;
            public string Message = "";

            public string HumanNeeded { get { return Human(NeededBytes); } }
            public string HumanFree { get { return Human(FreeBytes); } }
        }

        /// <summary>
        /// 估算"下这些章要多少空间"。按每章的平均字节估 ——
        /// 网文一章普遍 2~4KB（UTF-8 中文），这里取 3KB，偏保守。
        /// 已下载的章不计入（续传时只下缺的）。
        /// </summary>
        public static long EstimateBytes(int chaptersToDownload, bool includeEpub)
        {
            if (chaptersToDownload < 0) chaptersToDownload = 0;
            long body = (long)chaptersToDownload * 3 * 1024;      // 正文
            body += (long)chaptersToDownload * 64;                 // 锚点行 + 标题 + 分隔线的开销
            if (includeEpub) body = body * 2;                      // EPUB 再占一份（压缩后其实更小，多算是保守）
            return body;
        }

        /// <summary>
        /// 检查目标目录所在盘的空间。目录不存在时向上找到最近的已存在父目录。
        /// 拿不到空间信息时一律返回 Ok=true —— **绝不因为查不到空间就不让用户下载**。
        /// </summary>
        public static Estimate Check(string targetDir, long neededBytes)
        {
            var e = new Estimate { NeededBytes = neededBytes };
            try
            {
                var dir = targetDir;
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;

                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (string.IsNullOrEmpty(root)) { e.Message = ""; return e; }

                var di = new DriveInfo(root);
                if (!di.IsReady) { e.Message = ""; return e; }
                e.FreeBytes = di.AvailableFreeSpace;

                // 需要量 + 安全余量 > 可用空间 → 警告
                if (neededBytes + SafetyMarginBytes > e.FreeBytes)
                {
                    e.Ok = false;
                    e.Message = string.Format(
                        "磁盘空间可能不足：预计需要 {0}，{1} 盘剩余 {2}（已预留 {3} 余量）。\n\n" +
                        "继续下载可能在写入过程中失败。建议先清理空间，或者少选一些章节。",
                        e.HumanNeeded, root.TrimEnd('\\'), e.HumanFree, Human(SafetyMarginBytes));
                }
            }
            catch
            {
                // 任何异常都当作"查不出来"，不阻塞下载
                e.Ok = true;
                e.Message = "";
            }
            return e;
        }

        public static string Human(long bytes)
        {
            if (bytes < 1024) return bytes + " 字节";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#") + " MB";
            return (bytes / 1073741824.0).ToString("0.##") + " GB";
        }
    }
}
