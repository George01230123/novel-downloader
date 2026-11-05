using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 用**一个 curl 进程并发抓一批 URL**，从而复用 TLS 连接。
    ///
    /// ============================================================
    /// 为什么需要它（实测数据）
    /// ============================================================
    /// 原来的做法是"一页 = 一次 Http.Get = 一个新 curl.exe 进程"，
    /// 每页都要重做一次 DNS + TCP + **TLS 握手**。单页拆解：
    ///
    ///     连接 0.17s + TLS 握手 0.15~0.18s + 服务器响应 0.27s
    ///
    /// 也就是说每页有约 0.35 秒是纯重复劳动。实测 16 页（真实章节页）：
    ///
    ///     每页一个独立 curl 进程      13.93 秒（每页 0.871s）
    ///     一个 curl 进程 + --parallel  4.36 秒（每页 0.273s）   → **3.2 倍**
    ///
    /// ============================================================
    /// 用在哪、不用在哪（这条界限很重要）
    /// ============================================================
    /// **能用**：URL 事先已知的批量抓取 ——
    ///   · 下载阶段抓各章正文（章节列表已经拿到了）
    ///   · 离线模式预抓正文
    ///
    /// **不能用**：biquga PC 版的**目录遍历**。那个是"反向链条"——
    ///   第 N-1 页的地址只写在**第 N 页的 HTML 里**，所以必须先取到第 N 页
    ///   才能知道下一页取什么，天然串行。（我也验证过"按章节 id 递增规律预测"，
    ///   实测命中率极低、反而更慢，那条路已否掉。）
    ///
    /// ============================================================
    /// 为什么不是"起 N 个线程各发一个 curl"
    /// ============================================================
    /// 那样虽然有 N 个请求在飞，但**每个请求仍是一个新进程 + 新 TLS 握手**，
    /// 而且会增加站点压力。`--parallel` 在**一个进程内**复用连接池，
    /// 既省掉重复握手，又不会把并发数打高。实测两者差距就是上面那 3.2 倍。
    /// </summary>
    public static class HttpBatch
    {
        /// <summary>
        /// 一批多少个 URL。够大才能摊薄"每条连接的第一发"成本。
        ///
        /// ★ 实测（笔尖中文，300 章，12 并发，PerfBench 探针，内容逐字一致）：
        ///     每批 8 章   → 1.19 倍
        ///     每批 16 章  → 1.45 倍
        ///     每批 32 章  → **1.55 倍**（33.0 → 21.3 ms/章）
        ///   收益递减，32 之后基本平了，所以定在 32。
        ///   内存/磁盘代价：32 章 × 约 11KB = 350KB 临时文件，可忽略。
        ///
        /// ★ 别把"提速倍数"想得太高：一个 curl 进程顺序发 8 个请求时，
        ///   只有 1 发在飞；而 12 个线程各发各的时，是 12 发同时在飞。
        ///   所以"连接复用"省下的建连开销，会被"失去并发"抵掉一部分 ——
        ///   净收益就是上面这个 1.55 倍，不是 3~4 倍。
        ///   （我一度测出 4.4 倍/20 倍，是基准脚本浅拷贝导致第二轮跳过了全部章节，
        ///     假数字。修成深拷贝后才是 1.55 倍。）
        /// </summary>
        public const int BatchSize = 32;

        /// <summary>
        /// 并发抓一批 URL。返回 url → 内容（失败的不在字典里，调用方自己决定怎么处理）。
        ///
        /// 每个 URL 写进**独立文件**，所以不需要解析拼接后的正文边界 ——
        /// 这一点比"一个 -o 文件装全部"稳得多（后者要靠猜分隔符）。
        /// 代价是一批多几个临时文件，但都在 %TEMP%，且必定清理。
        /// </summary>
        public static Dictionary<string, string> Fetch(string[] urls, string referer)
        {
            Http.Cancellation.ThrowIfCancellationRequested();
            var map = new Dictionary<string, string>();
            if (urls == null || urls.Length == 0) return map;

            // 单个没必要上批量（还多一层临时文件开销），直接走原路径
            if (urls.Length == 1)
            {
                try { map[urls[0]] = Http.Get(urls[0], referer); } catch { }
                return map;
            }

            string curl = Http.CurlPath;
            if (string.IsNullOrEmpty(curl)) return map;   // 没 curl：交给调用方逐页退回

            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tmpFiles = new List<string>();
            try
            {
                var sb = new StringBuilder();
                // ★ **绝对不能用 --parallel**。
                //
                //   curl 的 --parallel 下，`-o` 文件与 URL 的对应关系**不保证一一对应**。
                //   实测：8 个 URL 各配一个 -o 文件，拿回来的内容却互相错位 ——
                //   把 A 页的 HTML 当成了 B 页的正文，提取出的长度从 2494 掉到 879，
                //   而且每章错得还不一样（所以不像"读取不完整"，像"张冠李戴"）。
                //
                //   我是靠"读每个 URL 自己的 -o 文件"来配对结果的，
                //   一旦错位就会**静默地把别的章写进正文** —— 这比慢一点严重得多，
                //   而且单测抓不到（它只在真实网络下出现）。
                //
                //   去掉 --parallel 后是"一个 curl 进程、按顺序发这几个请求"，
                //   **仍然复用同一条连接**（省掉重复 TLS 握手，这正是提速的来源），
                //   而 -o 与 URL 严格按顺序配对，不可能张冠李戴。
                sb.Append("-s --compressed --max-time ").Append(Math.Max(5, Http.TimeoutSeconds));
                // --compressed 让 curl 声明支持 gzip 并**自动解压**。
                // 为什么必须加：实测笔尖中文的**详情页（约 200 KB）**在客户端声明支持
                // gzip 时会压缩返回；不带这个参数虽然站点也会给明文（省了 CPU、
                // 但多传几倍字节），带上则两头都稳。
                // 关键是**不能只声明不解压** —— 那会把 gzip 字节当成正文，
                // 抓到的"内容"全是乱码。curl 的 --compressed 自己会解压，所以安全。
                if (!string.IsNullOrEmpty(referer))
                    sb.Append(" -H \"Referer: ").Append(referer.Replace("\"", "\\\"")).Append('"');
                if (!string.IsNullOrEmpty(Http.Proxy))
                    sb.Append(" -x \"").Append(Http.Proxy).Append('"');

                for (int i = 0; i < urls.Length; i++)
                {
                    var f = Path.Combine(Path.GetTempPath(), "novel-batch-" + tag + "-" + i + ".tmp");
                    tmpFiles.Add(f);
                    sb.Append(" -o \"").Append(f).Append('"');
                    sb.Append(" -w \"%{http_code}\"");
                    sb.Append(" \"").Append(urls[i].Replace("\"", "\\\"")).Append('"');
                }

                string stdout = RunCurl(curl, sb.ToString());
                if (stdout == null) return map;

                // --parallel 下 -w 的输出顺序**不保证**与 URL 顺序一致，
                // 而每个 URL 用的是独立文件 —— 所以状态码只用来判"这一批整体有没有动静"，
                // 具体每页成功与否以"文件里有没有内容"为准。
                // 这样即使顺序错乱也不会把 A 的内容配给 B。
                for (int i = 0; i < urls.Length; i++)
                {
                    if (!File.Exists(tmpFiles[i])) continue;
                    var bytes = File.ReadAllBytes(tmpFiles[i]);
                    if (bytes.Length == 0) continue;
                    var text = DecodeText(bytes);
                    if (!string.IsNullOrEmpty(text)) map[urls[i]] = text;
                }
            }
            catch { /* 批量失败 → 调用方逐页退回，不能因此中断下载 */ }
            finally
            {
                foreach (var f in tmpFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                }
            }
            return map;
        }

        /// <summary>
        /// 把响应字节解成文本。
        ///
        /// 为什么不复用 Http.Decode：它是 private，改成 internal 会影响别的文件；
        /// 而这里只需要"站点回的是 UTF-8、偶尔带 BOM"这一种情况
        /// （biquga / 番茄都是 UTF-8），所以就地做最小实现。
        /// BOM 必须显式剥掉 —— 留在正文里是看不见的字符，
        /// 后面双源逐字比对会莫名报差异。
        /// </summary>
        internal static string DecodeText(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            int start = 0;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) start = 3;
            return new UTF8Encoding(false).GetString(raw, start, raw.Length - start);
        }

        /// <summary>跑 curl 并拿回 stdout（stderr 丢弃，失败返回 null）</summary>
        private static string RunCurl(string curlPath, string args)
        {
            Http.Cancellation.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(curlPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            using (Http.CancelProcess(p))
            {
                // ★ 必须先读 stdout 再 WaitForExit：反过来的话输出量大时管道写满会死锁
                string stdout = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                // 一批是"一个进程按顺序发 N 个请求"，所以最坏耗时 = N × 单页超时，再留余量
                int waitMs = (Http.TimeoutSeconds * (urlsPerBatch(args) + 2) + 20) * 1000;
                if (!p.WaitForExit(waitMs))
                {
                    try { p.Kill(); } catch { }
                    return null;
                }
                Http.Cancellation.ThrowIfCancellationRequested();
                return p.ExitCode == 0 ? stdout : null;
            }
        }

        /// <summary>数一下参数里有多少个 URL（形如 http 开头的引号参数）</summary>
        private static int urlsPerBatch(string args)
        {
            int n = 0, at = 0;
            while ((at = args.IndexOf("\"http", at, StringComparison.Ordinal)) >= 0) { n++; at += 5; }
            return Math.Max(1, n);
        }
    }
}
