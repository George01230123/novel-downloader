using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TomatoBiquga
{
    /// <summary>
    /// HTTP 取数层。
    ///
    /// 为什么不用 .NET 自带的 HttpClient/WebRequest：
    /// biquga.com 只提供 TLS 1.3 且会拒绝 .NET(Schannel via SslStream) 的握手，
    /// .NET Framework 4.8 与 .NET 10 都试过，一律 “Received an unexpected EOF”。
    /// 而 Windows 自带的 curl.exe（Win10 1803+ 系统内置）能正常访问，
    /// 所以这里用 curl.exe 作为取数后端，工具本身保持免安装、零依赖。
    /// curl 不可用时自动回退到 .NET 原生请求（番茄等站点原生可用）。
    /// </summary>
    public static class Http
    {
        public static string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        public static int TimeoutSeconds = 25;
        public static int MaxRetries = 3;
        public static int MinDelayMs = 120;
        public static int MaxDelayMs = 500;

        /// <summary>
        /// HTTP 代理（空 = 不用代理）。直接交给 curl.exe 的 -x，
        /// 所以 http / https / socks5 三种写法都能用，不用自己实现协议。
        /// </summary>
        public static string Proxy = "";

        /// <summary>Cookie 请求头（空 = 不带）。整串原样发送，不做解析。</summary>
        public static string Cookie = "";

        /// <summary>
        /// 重试退避上限（毫秒）。指数退避 500 → 1000 → 2000…，封顶在这里，
        /// 免得一个站点抽风时把整本书卡死。
        /// </summary>
        public static int MaxBackoffMs = 20000;

        /// <summary>
        /// 触发「冷却」的正文特征（小写匹配）。站点限流时往往**返回 200 但正文
        /// 是一句警告**（见 docs/同类工具调研.md 4.1：有的站并发 1 也会被限流并把
        /// 正文替换成警告语），所以光看状态码不够，还要认这些特征词。
        /// </summary>
        public static string[] RateLimitMarkers = new string[]
        {
            "访问太频繁", "访问过于频繁", "请求过于频繁", "操作太频繁",
            "请稍后重试", "请30秒后", "请稍候再试", "访问速度过快",
            "too many requests", "rate limit",
        };

        /// <summary>
        /// 连续命中限流的次数。降并发靠它：站点一限流就把并发往下降，
        /// 成功一段时间后再慢慢升回去（见 <see cref="AdaptiveWorkers"/>）。
        /// </summary>
        private static int _throttleHits;

        /// <summary>最近一次请求用的是哪个后端（用于日志展示）</summary>
        public static string LastBackend = "";

        /// <summary>最近一次收到的 HTTP 状态码（0 = 未知/非 HTTP 后端）</summary>
        public static int LastStatusCode;

        private static readonly Random Rnd = new Random();

        /// <summary>
        /// Random 不是线程安全的：多个下载线程同时 Rnd.Next() 会让它内部状态坏掉、
        /// 之后**永远返回 0**（.NET Framework 的已知行为），于是"随机间隔"变成固定值、
        /// 抖动退化成 0 —— 并发下载正是最容易踩到它的场景。所有取随机数都走这里。
        /// </summary>
        private static int NextRandom(int maxExclusive)
        {
            lock (Rnd)
                return Rnd.Next(maxExclusive);
        }
        private static string _curlPath;
        private static bool _curlChecked;

        /// <summary>
        /// 应用用户设置里的请求间隔。注意 MinDelayMs 必须 ≥1：
        /// Polite() 里是 Rnd.Next(MaxDelay-MinDelay)，差值为 0 会抛 ArgumentOutOfRange。
        /// </summary>
        public static void Configure(int minDelayMs, int maxDelayMs)
        {
            if (minDelayMs < 1) minDelayMs = 1;
            if (maxDelayMs < minDelayMs) maxDelayMs = minDelayMs;
            MinDelayMs = minDelayMs;
            MaxDelayMs = maxDelayMs;
        }

        /// <summary>
        /// curl.exe 的路径（找不到就是 null，请求会走 .NET 回退）。
        ///
        /// ★ 这里必须"先探好路径、最后才置 _curlChecked"：
        /// 原来的写法是**先置位再探测**，于是并发抓取时（下载/目录页并发）
        /// 只要线程 A 刚置完位就被切走，B~Z 就会看到"已检查过但路径还是 null"，
        /// 直接判定 curl 不可用 → 全部走 .NET 回退；而 biquga 恰恰**拒绝 .NET 的
        /// TLS 握手**，每个请求要白等 25 秒超时才算完 —— 表现就是"并发了反而更慢"。
        /// 这个竞态是并发下载实测抓出来的（6 个线程同时取路径，5 个拿到 null）。
        /// </summary>
        public static string CurlPath
        {
            get
            {
                if (!_curlChecked)
                {
                    lock (CurlLock)
                    {
                        if (!_curlChecked)
                        {
                            string found = null;
                            foreach (var p in new[]
                            {
                                Path.Combine(Environment.SystemDirectory, "curl.exe"),
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "curl.exe"),
                            })
                            {
                                if (File.Exists(p)) { found = p; break; }
                            }
                            _curlPath = found;
                            _curlChecked = true;      // ★ 最后才置位：置位就意味着上面的值已经可见
                        }
                    }
                }
                return _curlPath;
            }
        }

        private static readonly object CurlLock = new object();

        public static bool CurlAvailable { get { return CurlPath != null; } }

        /// <summary>抓一个页面（自动重试）</summary>
        public static string Get(string url, string referer = null)
        {
            return Request(url, null, referer);
        }

        /// <summary>提交表单</summary>
        public static string Post(string url, string body, string referer = null)
        {
            return Request(url, body, referer);
        }

        /// <summary>
        /// 取 GitHub API 的 JSON（检查更新用）。
        /// 单独开一个方法的原因：api.github.com **会拒绝没有 User-Agent 的请求**（403），
        /// 而且要求 Accept 带上版本号。走普通 Request 的话，用户只要在站点配置里
        /// 把 UA 清空就会莫名其妙 403，排查起来很费劲。
        /// </summary>
        public static string GetForApi(string url)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (CurlAvailable) return FetchWithCurl(url, null, null, ApiUserAgent, ApiAccept);
                    return FetchWithDotNet(url, null, null, ApiUserAgent, ApiAccept);
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 0) Thread.Sleep(500);
                }
            }
            throw new Exception(last == null ? "请求失败" : last.Message);
        }

        private const string ApiUserAgent = "novel-downloader-update-check";
        private const string ApiAccept = "application/vnd.github+json";

        /// <summary>
        /// 带自定义超时的 GET（探测本地服务用，例如 Ollama 的 /api/tags）。
        ///
        /// 为什么单独一个：探测要**快**（3~5 秒就够），而全局 TimeoutSeconds 是给抓书页
        /// 用的（25 秒）。拿 25 秒的超时去问"Ollama 在不在"会让界面白等。
        /// 同样**不重试** —— 探测失败就是不在，重试只是浪费时间。
        /// </summary>
        public static string GetWithTimeout(string url, int timeoutSeconds)
        {
            int oldTimeout = TimeoutSeconds;
            int oldRetries = MaxRetries;
            try
            {
                TimeoutSeconds = Math.Max(1, timeoutSeconds);
                MaxRetries = 1;
                if (CurlAvailable) return FetchWithCurl(url, null, null);
                return FetchWithDotNet(url, null, null);
            }
            finally
            {
                TimeoutSeconds = oldTimeout;
                MaxRetries = oldRetries;
            }
        }

        /// <summary>
        /// 发一个 JSON POST（AI 接口用）。
        ///
        /// 为什么单独开：普通 Post() 硬编码了
        /// `Content-Type: application/x-www-form-urlencoded`，也没有自定义头的口子，
        /// 而 AI 接口（OpenAI 兼容 / Ollama）都要求 JSON body，
        /// 云端还要 `Authorization: Bearer ...`。
        ///
        /// 注意两个后端都要走一遍：这里的 header 参数是给 curl 用的，
        /// .NET 回退那条路也要把同样的头带上，否则一换后端就 401。
        /// </summary>
        public static string PostJson(string url, string jsonBody, Dictionary<string, string> headers,
            int timeoutSeconds = 0)
        {
            int oldTimeout = TimeoutSeconds;
            int oldRetries = MaxRetries;
            try
            {
                // AI 调用常常要几十秒（尤其本地小模型），而且**不要自动重试** ——
                // 重试会让一次慢请求变成三次慢请求，用户会以为程序卡死。
                if (timeoutSeconds > 0) TimeoutSeconds = timeoutSeconds;
                MaxRetries = 1;

                try
                {
                    if (CurlAvailable) return FetchWithCurl(url, jsonBody, null, null, "application/json", headers);
                    return FetchWithDotNet(url, jsonBody, null, null, "application/json", headers);
                }
                catch (Exception ex)
                {
                    // 后端回退：curl 不通时试 .NET（本地 Ollama 有时候 curl 会有代理干扰）
                    if (CurlAvailable)
                    {
                        try { return FetchWithDotNet(url, jsonBody, null, null, "application/json", headers); }
                        catch { }
                    }
                    throw new Exception("AI 接口请求失败：" + ex.Message);
                }
            }
            finally
            {
                TimeoutSeconds = oldTimeout;
                MaxRetries = oldRetries;
            }
        }

        /// <summary>
        /// 抓二进制内容（封面图片用）。
        /// 为什么单独开一个：Request 走的是 Decode()（按 charset 解成字符串），
        /// 图片按那条路会被 UTF-8 解码毁掉字节。
        /// 失败返回 null —— 封面抓不到不该让整本书下载失败。
        /// </summary>
        public static byte[] GetBytes(string url, string referer = null)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    byte[] data;
                    if (CurlAvailable) data = FetchBytesWithCurl(url, referer);
                    else data = FetchBytesWithDotNet(url, referer);
                    if (data != null && data.Length > 0) return data;
                }
                catch { }
                if (attempt == 0) Thread.Sleep(300);
            }
            return null;
        }

        private static byte[] FetchBytesWithCurl(string url, string referer)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "tb_img_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var args = new StringBuilder();
                args.Append("-s -L --max-time ").Append(TimeoutSeconds);
                args.Append(" -A \"").Append(UserAgent).Append('"');
                if (!string.IsNullOrEmpty(referer))
                    args.Append(" -H \"Referer: ").Append(referer).Append('"');
                if (!string.IsNullOrEmpty(Cookie))
                    args.Append(" -b \"").Append(Cookie.Replace("\"", "\\\"")).Append('"');
                if (!string.IsNullOrEmpty(Proxy))
                    args.Append(" -x \"").Append(Proxy).Append('"');
                args.Append(" -o \"").Append(tmp).Append('"');
                args.Append(" \"").Append(url).Append('"');

                var psi = new ProcessStartInfo(CurlPath, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit((TimeoutSeconds + 15) * 1000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    if (p.ExitCode != 0) return null;
                }
                if (!File.Exists(tmp)) return null;
                return File.ReadAllBytes(tmp);
            }
            catch { return null; }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static byte[] FetchBytesWithDotNet(string url, string referer)
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.UserAgent = UserAgent;
                req.Timeout = TimeoutSeconds * 1000;
                req.ReadWriteTimeout = TimeoutSeconds * 1000;
                req.KeepAlive = false;
                req.AllowAutoRedirect = true;
                if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
                if (!string.IsNullOrEmpty(Cookie)) req.Headers["Cookie"] = Cookie;
                if (!string.IsNullOrEmpty(Proxy))
                {
                    var pu = ParseHttpProxy(Proxy);
                    if (pu != null) req.Proxy = new System.Net.WebProxy(pu);
                }
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[16384];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        private static string Request(string url, string body, string referer)
        {
            Exception last = null;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    // ★ 每轮开始先把状态码清成"未知"。
                    //   否则上一轮留下的状态码会漏到这一轮：比如第 1 次是 502、
                    //   第 2 次其实成功了，判据读到的却还是 502，于是把一个正常响应
                    //   当成错误丢掉。状态码是"逐次"的，不是"粘住"的。
                    LastStatusCode = 0;

                    string text;
                    if (CurlAvailable)
                    {
                        text = FetchWithCurl(url, body, referer);
                        LastBackend = "curl";
                    }
                    else
                    {
                        text = FetchWithDotNet(url, body, referer);
                        LastBackend = ".NET";
                    }
                    if (string.IsNullOrEmpty(text)) throw new Exception("返回内容为空");

                    // 状态码先判：429/502/503 是"服务器现在不行"，要退避重试；
                    // ★ 502 是实测加的：笔趣阁的源站会间歇性回 nginx 502
                    //   （实测 20 次里出现过、也出现过连续 502），
                    //   它**不是**限流（别降并发），但确实该重试。
                    if (IsRetryableStatus(LastStatusCode))
                    {
                        NoteThrottle();
                        throw new ThrottledException(string.Format("站点限流（HTTP {0}）", LastStatusCode));
                    }
                    if (LastStatusCode == 403)
                    {
                        NoteThrottle();
                        throw new ThrottledException("被站点拒绝（HTTP 403，可能需要 Cookie 或代理）");
                    }

                    // 再看正文：限流页常常是 200 + 一句警告
                    if (LooksRateLimited(text))
                    {
                        NoteThrottle();
                        throw new ThrottledException(string.Format("站点限流（正文特征是「{0}」）", FirstMarker(text)));
                    }

                    NoteSuccess();
                    return text;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // curl 失败时尝试原生后端（限流不适用：换后端解决不了限流）
                    //
                    // ★ 但**已知拒绝 .NET 握手的站点**要跳过这次回退：
                    //   biquga 就是（Http.cs 顶部注释写了它只收 TLS 1.3 且会把
                    //   Schannel 的握手拒掉）。对这类站点回退是**稳输**的，
                    //   而且每次要白等满 25 秒超时（TimeOutSeconds），
                    //   再乘上重试次数 —— 一个 502 就能白等一分多钟。
                    //   判据按"主机"记：别的域名没这个毛病，不该被连累。
                    if (CurlAvailable && attempt == 0 && !(ex is ThrottledException)
                        && !SchannelRejectedHost(url))
                    {
                        try
                        {
                            var t = FetchWithDotNet(url, body, referer);
                            if (!string.IsNullOrEmpty(t)) { LastBackend = ".NET(回退)"; return t; }
                        }
                        catch (Exception ex2)
                        {
                            // ★ 这里必须**保留原来的 curl 错误**，不能只用 ex2 覆盖。
                            // curl 的失败原因是有诊断价值的（退出码 → TLS/DNS/超时），
                            // 而 .NET 回退失败往往只是一句笼统的"连接被意外关闭"。
                            // 早先直接 last = ex2，结果站点探活只能报出"请求失败"，
                            // 完全帮不到用户。现在两个都留着。
                            last = new Exception(ex.Message + "；换用内置 .NET 请求也失败：" + ex2.Message);
                            NoteSchannelRejected(url, ex2.Message);
                        }
                    }
                    if (attempt < MaxRetries - 1) SleepBackoff(attempt, ex is ThrottledException);

                    // ★ 超时不重试：重试一个已经超时的站点 = 再等一个超时。
                    //   30 秒 × 3 次 = 90 秒白等（用户看到的就是"卡住然后弹窗"）。
                    if (ex is TimeoutLikeException)
                    {
                        last = ex;
                        break;
                    }
                }
            }
            // ★ 超时不再报"已重试 N 次"——那会误导用户以为程序很努力，
            //   其实是白等了 N 个超时周期。如实说清楚。
            string reason = last == null ? "未知错误" : last.Message;
            if (last is TimeoutLikeException)
                throw new Exception(Shorten(url) + "\n  " + reason);
            throw new Exception(string.Format("请求失败（已重试 {0} 次）：{1}\n  {2}",
                MaxRetries, Shorten(url), reason));
        }

        // ---------------------------------------------------------- 退避与限速自适应

        /// <summary>
        /// curl 非零退出码 → 人话异常。
        ///
        /// ★ 为什么需要：用户报过一个弹窗，上面写着
        ///   「请求失败（已重试 3 次）：https://www.biquga.com/search.html curl 退出码 28」。
        ///   "退出码 28" 对用户毫无意义 —— 那是 curl 的 `OPERATION TIMEDOUT`，
        ///   真实含义是**站点在超时时间内没有响应**（笔趣阁 PC 版实测单次 4.7~13 秒，
        ///   慢的时候直接超 30 秒）。
        ///
        /// ★ 超时**不重试**：重试一个已经超时的站点，等于再等一个超时 ——
        ///   30 秒 × 3 次 = 90 秒白等，然后才弹窗。用户体感就是"卡死"。
        ///   所以超时直接失败，由调用方决定要不要换个源。
        /// </summary>
        internal static Exception CurlFailure(int exitCode, string stderr)
        {
            string detail = string.IsNullOrEmpty(stderr) ? "" : "：" + stderr.Trim();
            switch (exitCode)
            {
                case 28:
                    return new TimeoutLikeException(
                        "请求超时 —— 站点在 " + TimeoutSeconds + " 秒内没有响应（站点太慢或网络不通）" + detail);
                case 6:
                    return new Exception("域名解析失败（DNS 查不到这个站）" + detail);
                case 7:
                    return new Exception("连不上站点（连接被拒绝/被墙）" + detail);
                case 35:
                    return new Exception("TLS 握手失败（站点可能只收特定 TLS 版本）" + detail);
                case 56:
                    return new Exception("接收数据中断（连接被重置）" + detail);
                default:
                    return new Exception("请求失败（curl 退出码 " + exitCode + "）" + detail);
            }
        }

        /// <summary>"超时"这一类失败：**不值得重试**（重试只是再等一次超时）。</summary>
        public class TimeoutLikeException : Exception
        {
            public TimeoutLikeException(string msg) : base(msg) { }
        }

        /// <summary>站点限流专用异常：让调用方能区分「限流」和「网络坏了」。</summary>
        public class ThrottledException : Exception
        {
            public ThrottledException(string msg) : base(msg) { }
        }

        /// <summary>
        /// 这个状态码是不是"服务器现在不行，值得退避重试"。
        ///
        /// · 429 太频繁 —— 站点明确要求慢下来
        /// · 502 网关错误 —— 实测笔趣阁源站会间歇性回这个（nginx 502）
        /// · 503 服务不可用 —— 通常是临时过载
        ///
        /// **403 故意不算**：那是"站点不让你来"（要 Cookie / 要代理），
        /// 重试只会白等，而且会被当成攻击。它走 ThrottledException 那条路。
        /// 做成独立方法是为了能离线单测 —— 这个判据以前只在真实网络下才走到。
        /// </summary>
        internal static bool IsRetryableStatus(int code)
        {
            return code == 429 || code == 502 || code == 503;
        }

        /// <summary>
        /// 已知会拒绝 .NET(Schannel) TLS 握手的主机。
        ///
        /// 为什么要记：`FetchWithDotNet` 回退对这类站点是**稳输**的，
        /// 而且每次要等满 25 秒超时。实测 biquga 就是这样 ——
        /// 一个 502 触发的回退，白等 25 秒，再乘 3 次重试。
        /// 按主机记，别的域名不受影响。
        /// </summary>
        private static readonly HashSet<string> _schannelRejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _schannelLock = new object();

        private static bool SchannelRejectedHost(string url)
        {
            string host = HostOf(url);
            if (host == null) return false;
            lock (_schannelLock) return _schannelRejected.Contains(host);
        }

        /// <summary>
        /// 记下"这个主机的 .NET 回退也失败了"，下次就别再试。
        /// 只在错误信息确实指向 TLS/协议层时才记 —— 普通超时不该被当成
        /// "永远别用 .NET"，那会让回退机制彻底失效。
        /// </summary>
        private static void NoteSchannelRejected(string url, string err)
        {
            if (string.IsNullOrEmpty(err)) return;
            string e = err.ToLowerInvariant();
            bool tlsLike = e.Contains("handshake") || e.Contains("schannel") || e.Contains("ssl")
                        || e.Contains("tls") || e.Contains("eof") || e.Contains("protocol");
            if (!tlsLike) return;
            string host = HostOf(url);
            if (host == null) return;
            lock (_schannelLock) _schannelRejected.Add(host);
        }

        /// <summary>仅供离线单测：把一个主机标记成"拒绝 Schannel"</summary>
        internal static void NoteSchannelRejectedForTest(string url, string err)
        {
            NoteSchannelRejected(url, err);
        }

        /// <summary>仅供离线单测：查询某个主机是否已被标记</summary>
        internal static bool SchannelRejectedForTest(string url)
        {
            return SchannelRejectedHost(url);
        }

        private static string HostOf(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return null;
                var u = new Uri(url, UriKind.Absolute);
                return u.Host;
            }
            catch { return null; }
        }

        /// <summary>
        /// 指数退避 + 抖动。限流场景退避更狠（起点 2 秒），因为「立刻重试」
        /// 正是站点想惩罚的行为。
        /// 固定 500ms 重试对限流完全无效 —— 这是本轮改动的主要动机。
        /// </summary>
        private static void SleepBackoff(int attempt, bool throttled)
        {
            long ms = throttled ? 2000L << attempt : 500L << attempt;
            if (ms > MaxBackoffMs) ms = MaxBackoffMs;
            // 抖动：避免多个并发线程退避后同时回来（惊群）
            ms += NextRandom((int)Math.Max(1, ms / 4));
            Thread.Sleep((int)Math.Min(ms, int.MaxValue));
        }

        /// <summary>正文是否像限流页。只匹配短文本，避免把小说正文误判。</summary>
        private static bool LooksRateLimited(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 2000) return false;
            var low = text.ToLowerInvariant();
            foreach (var m in RateLimitMarkers)
            {
                if (!string.IsNullOrEmpty(m) && low.IndexOf(m.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>找出命中的那个特征词（给日志用，让用户知道发生了什么）</summary>
        private static string FirstMarker(string text)
        {
            var low = text.ToLowerInvariant();
            foreach (var m in RateLimitMarkers)
            {
                if (!string.IsNullOrEmpty(m) && low.IndexOf(m.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    return m;
            }
            return "未知";
        }

        private static void NoteThrottle()
        {
            if (_throttleHits < 1000) _throttleHits++;
        }

        private static void NoteSuccess()
        {
            // 成功一次就消一点，让并发能缓慢恢复；连续失败才会真正把并发压下来
            if (_throttleHits > 0) _throttleHits--;
        }

        /// <summary>当前建议的并发数（配合 <see cref="AdaptiveWorkers"/> 用）</summary>
        public static int ThrottleHits { get { return _throttleHits; } }

        /// <summary>清空限流计数（每本书开始时调用，免得上一本的账算到这一本头上）</summary>
        public static void ResetThrottle()
        {
            _throttleHits = 0;
        }

        /// <summary>
        /// 按限流情况收缩并发：每命中 2 次限流减 1 线程，最低降到 1。
        /// 站点被限流时「降并发」比「加重试」有用得多。
        /// </summary>
        public static int AdaptiveWorkers(int configured)
        {
            int w = configured - (_throttleHits / 2);
            if (w < 1) w = 1;
            if (w > configured) w = configured;
            return w;
        }

        // ---------------------------------------------------------- curl 后端

        private static string FetchWithCurl(string url, string body, string referer,
            string userAgent = null, string accept = null,
            Dictionary<string, string> extraHeaders = null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "tb_" + Guid.NewGuid().ToString("N") + ".html");
            try
            {
                var args = new StringBuilder();
                args.Append("-s -L --max-time ").Append(TimeoutSeconds);
                args.Append(" --compressed");
                args.Append(" -A \"").Append(string.IsNullOrEmpty(userAgent) ? UserAgent : userAgent).Append('"');
                args.Append(" -H \"Accept-Language: zh-CN,zh;q=0.9\"");
                if (!string.IsNullOrEmpty(accept))
                    args.Append(" -H \"Accept: ").Append(accept).Append('"');
                if (!string.IsNullOrEmpty(referer))
                    args.Append(" -H \"Referer: ").Append(referer).Append('"');
                if (!string.IsNullOrEmpty(Cookie))
                    args.Append(" -b \"").Append(Cookie.Replace("\"", "\\\"")).Append('"');
                if (!string.IsNullOrEmpty(Proxy))
                    args.Append(" -x \"").Append(Proxy).Append('"');
                // 自定义头（AI 接口的 Authorization 等）
                bool jsonBody = false;
                if (extraHeaders != null)
                {
                    foreach (var kv in extraHeaders)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            jsonBody = kv.Value != null &&
                                       kv.Value.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
                            continue;   // Content-Type 交给下面 body 分支统一处理
                        }
                        args.Append(" -H \"").Append(kv.Key).Append(": ")
                            .Append((kv.Value ?? "").Replace("\"", "\\\"")).Append('"');
                    }
                }
                if (body != null)
                {
                    args.Append(" -X POST -H \"Content-Type: ")
                        .Append(jsonBody ? "application/json" : "application/x-www-form-urlencoded")
                        .Append('"');
                    args.Append(" --data-binary \"").Append(body.Replace("\"", "\\\"")).Append('"');
                }
                args.Append(" -o \"").Append(tmp).Append('"');
                // 状态码走 -w（curl 把它写到 stdout），响应体走 -o（写进文件）。
                // ★ 绝不可以用 `> 文件` 这种 shell 重定向：UseShellExecute=false 时
                //   ProcessStartInfo 是**直接启动 curl.exe**，不经过 cmd.exe，
                //   `>` 会被当成 curl 的参数 → curl 退出码 3（URL 格式错误），
                //   所有请求全部失败。这个错犯过一次，被联网实测抓出来（见 docs）。
                args.Append(" -w \"%{http_code}\"");
                args.Append(" \"").Append(url).Append('"');

                var psi = new ProcessStartInfo(CurlPath, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                string stdout;
                using (var p = Process.Start(psi))
                {
                    stdout = p.StandardOutput.ReadToEnd();
                    var err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit((TimeoutSeconds + 15) * 1000))
                    {
                        try { p.Kill(); } catch { }
                        throw new Exception("curl 超时");
                    }
                    if (p.ExitCode != 0)
                        throw CurlFailure(p.ExitCode, err);
                }

                if (!File.Exists(tmp)) throw new Exception("curl 没有产生输出文件");
                LastStatusCode = ParseHttpCode(stdout);
                var bytes = File.ReadAllBytes(tmp);
                if (bytes.Length == 0) throw new Exception("curl 返回空内容");
                return Decode(bytes);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// 从 curl 的 stdout 里解出 HTTP 状态码。
        ///
        /// curl 的 `-w "%{http_code}"` 把状态码写到 stdout，响应体则由 `-o` 写进文件，
        /// 所以正常情况下 stdout 就是裸的 "200"。
        /// 但要容忍两种意外（都实测见过）：
        ///   · 跟随重定向时可能出现多段数字，例如 "200000000" —— 取**最后一段**三位数；
        ///   · 老 curl 不认 %{http_code} 时会原样吐出字面量，这时按"未知(0)"处理。
        /// 解不出来一律返回 0（未知），绝不因为状态码解析失败就让请求失败 ——
        /// 状态码只是"锦上添花"的判断依据，正文才是结果。
        /// </summary>
        internal static int ParseHttpCode(string stdout)
        {
            if (string.IsNullOrEmpty(stdout)) return 0;
            var s = stdout.Trim();
            if (s.Length == 0) return 0;

            // 从右往左找第一段连续数字，取它最后三位（重定向链会拼在一起）
            int end = -1;
            for (int i = s.Length - 1; i >= 0; i--)
                if (char.IsDigit(s[i])) { end = i; break; }
            if (end < 0) return 0;
            int start = end;
            while (start > 0 && char.IsDigit(s[start - 1])) start--;

            var run = s.Substring(start, end - start + 1);
            // 三段以上的数字串（重定向拼接）：取最后三位
            if (run.Length > 3) run = run.Substring(run.Length - 3);

            int code;
            if (int.TryParse(run, out code) && code >= 100 && code < 600) return code;
            return 0;
        }

        // ---------------------------------------------------------- 原生后端

        private static string FetchWithDotNet(string url, string body, string referer,
            string userAgent = null, string accept = null,
            Dictionary<string, string> extraHeaders = null)
        {
            LastStatusCode = 0;
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method = body == null ? "GET" : "POST";
            req.UserAgent = string.IsNullOrEmpty(userAgent) ? UserAgent : userAgent;
            req.Timeout = TimeoutSeconds * 1000;
            req.ReadWriteTimeout = TimeoutSeconds * 1000;
            req.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            req.Accept = string.IsNullOrEmpty(accept)
                ? "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
                : accept;
            req.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";
            req.KeepAlive = false;
            req.AllowAutoRedirect = true;
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            if (!string.IsNullOrEmpty(Cookie)) req.Headers["Cookie"] = Cookie;

            // 自定义头（AI 接口的 Authorization 等）
            bool jsonBody = false;
            if (extraHeaders != null)
            {
                foreach (var kv in extraHeaders)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonBody = kv.Value != null &&
                                   kv.Value.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
                        continue;   // 交给下面的 body 分支
                    }
                    try { req.Headers[kv.Key] = kv.Value ?? ""; }
                    catch { /* 受限头（如 Host）设不进去就跳过，不能让整次请求失败 */ }
                }
            }

            // 代理：http/https 走 WebProxy；socks 只有 curl 后端支持（.NET 4.8 原生不支持
            // SOCKS），这种情况下静默忽略，由 curl 后端承担 —— 不要在这里抛异常，
            // 否则用户开了 SOCKS 代理反而连直连都用不了。
            if (!string.IsNullOrEmpty(Proxy))
            {
                var pu = ParseHttpProxy(Proxy);
                if (pu != null) req.Proxy = new System.Net.WebProxy(pu);
            }

            if (body != null)
            {
                var b = Encoding.UTF8.GetBytes(body);
                req.ContentType = jsonBody
                    ? "application/json; charset=UTF-8"
                    : "application/x-www-form-urlencoded; charset=UTF-8";
                req.ContentLength = b.Length;
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            try
            {
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    LastStatusCode = (int)resp.StatusCode;
                    var buf = new byte[16384];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return Decode(ms.ToArray(), resp.ContentType);
                }
            }
            catch (System.Net.WebException we)
            {
                // 4xx/5xx 在 .NET 里是异常，但状态码是有价值的信息（429/503 要冷却）
                var hr = we.Response as System.Net.HttpWebResponse;
                if (hr != null) LastStatusCode = (int)hr.StatusCode;
                throw;
            }
        }

        /// <summary>
        /// 把用户填的代理串转成 WebProxy。只接受 http/https —— SOCKS 交给 curl。
        /// 支持 "127.0.0.1:7890" 这种不带协议的简写（按 http 处理）。
        /// </summary>
        private static Uri ParseHttpProxy(string proxy)
        {
            try
            {
                var s = proxy.Trim();
                if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "http://" + s;
                if (s.StartsWith("socks", StringComparison.OrdinalIgnoreCase)) return null;
                var u = new Uri(s);
                if (u.Scheme != "http" && u.Scheme != "https") return null;
                return u;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------- 编码

        private static string Decode(byte[] raw, string contentType = null)
        {
            string enc = null;
            if (!string.IsNullOrEmpty(contentType))
            {
                var m = Regex.Match(contentType, @"charset=([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) enc = m.Groups[1].Value;
            }
            if (enc == null)
            {
                var head = Encoding.ASCII.GetString(raw, 0, Math.Min(3072, raw.Length));
                var m = Regex.Match(head, @"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) enc = m.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(enc)) enc = "utf-8";
            if (Regex.IsMatch(enc, "^gb(2312|k|18030)$", RegexOptions.IgnoreCase))
            {
                try { return Encoding.GetEncoding(enc).GetString(raw); }
                catch { }
            }
            return Encoding.UTF8.GetString(raw);
        }

        private static string Shorten(string s)
        {
            return s != null && s.Length > 90 ? s.Substring(0, 90) + "..." : s;
        }

        public static void Polite()
        {
            Thread.Sleep(MinDelayMs + NextRandom(Math.Max(1, MaxDelayMs - MinDelayMs)));
        }

        // ---------------------------------------------------------- HTML 工具

        public static string HtmlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"&#x([0-9a-fA-F]+);", m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));
            s = Regex.Replace(s, @"&#(\d+);", m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value)));
            s = s.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&mdash;", "—")
                 .Replace("&ndash;", "–").Replace("&hellip;", "…").Replace("&amp;", "&");
            return s;
        }

        public static string StripTags(string html)
        {
            if (html == null) return "";
            html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(p|div|li|h\d)>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", "");
            return HtmlDecode(html);
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "未命名";
            var sb = new StringBuilder();
            foreach (var c in name)
            {
                if ("\\/:*?\"<>|\r\n\t".IndexOf(c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length == 0 ? "未命名" : s;
        }
    }
}
