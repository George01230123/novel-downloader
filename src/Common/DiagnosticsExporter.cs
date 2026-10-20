using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 「生成诊断包」：把排查问题需要的东西打成**一个 zip**，用户发过来就能看。
    ///
    /// ★ 为什么需要（真实成本）：
    ///   这次开发里我无数次说"请把日志发我 / 请告诉我你的设置 / 哪个站搜不到"，
    ///   每一轮都要用户截图、找文件、描述现象。而有这个包，一句话就够了。
    ///
    /// ★ 隐私是**硬要求**（这个包是要发出去的）：
    ///   · `Cookie` / `token` / 密码 / API Key 一律打码；
    ///   · 默认**不含书名**（只写"书架里有 N 本"）—— 书单本身是隐私；
    ///   · 用户下载的**正文一个字都不打包**（只统计行数/字符数）。
    ///
    /// ★ 不含：`cache\`（可能是几十 MB 的网页）、`下载\`、`搜索索引.json`。
    ///   它们对诊断没帮助，却会让包大到发不出去。
    /// </summary>
    internal static class DiagnosticsExporter
    {
        /// <summary>打码后替换成的文本</summary>
        internal const string Mask = "***（已隐藏）";

        /// <summary>这些名字的键，值必须打码（大小写不敏感，子串匹配）</summary>
        internal static readonly string[] SecretKeys = new string[]
        {
            "cookie", "token", "password", "passwd", "pwd", "secret",
            "apikey", "api_key", "authorization", "auth", "session", "sign", "proxy",
        };

        /// <summary>
        /// 把一行 `key=value` 或 `key: value` 里的敏感值打码。
        /// 不是键值形式、或者键名不含敏感词 → 原样返回。
        /// 纯函数，便于离线断言。
        /// </summary>
        internal static string SanitizeLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;

            // 先按 = 切（ini / 查询串），再按 : 切（请求头）
            int eq = line.IndexOf('=');
            int co = line.IndexOf(':');
            int at = -1;
            char sep = '\0';
            if (eq >= 0 && (co < 0 || eq < co)) { at = eq; sep = '='; }
            else if (co >= 0) { at = co; sep = ':'; }
            if (at < 0) return line;

            string key = line.Substring(0, at).Trim();
            if (!IsSecretKey(key)) return line;

            string value = line.Substring(at + 1);
            // 空值不用打码（"cookie=" 这种反而说明没配，是有用信息）
            if (value.Trim().Length == 0) return line;
            return line.Substring(0, at + 1) + " " + Mask;
        }

        /// <summary>键名是否属于"值要打码"的那一类</summary>
        internal static bool IsSecretKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string k = key.Trim().ToLowerInvariant();
            // 去掉 ini 的节名包装："[xbiquwk] cookie" 之类
            int br = k.LastIndexOf(']');
            if (br >= 0) k = k.Substring(br + 1).Trim();
            foreach (string s in SecretKeys)
                if (k.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>把整段配置文本逐行打码</summary>
        internal static string SanitizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines) sb.AppendLine(SanitizeLine(line));
            return sb.ToString();
        }

        /// <summary>
        /// 生成诊断包。返回 zip 路径；失败抛异常（调用方负责展示）。
        /// </summary>
        /// <param name="zipPath">输出的 zip 路径</param>
        /// <param name="appDir">程序目录（读 settings.ini / 站点配置.ini）</param>
        /// <param name="logText">界面日志框里的全文</param>
        /// <param name="scores">站点评分（可为 null）</param>
        /// <param name="extra">"当前状态"的补充行（可为 null）</param>
        public static string Build(string zipPath, string appDir, string logText,
            IEnumerable<string> extra, IList<SiteScore> scores)
        {
            if (string.IsNullOrEmpty(zipPath)) throw new ArgumentException("没有指定输出路径");
            string tmp = Path.Combine(Path.GetTempPath(),
                "nd_diag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "环境与设置.txt"),
                    BuildEnvText(appDir, extra), new UTF8Encoding(true));

                // ★ 日志也要打码。这一点是**测试抓出来的**：原来只给配置文件打码，
                //   日志原样进包 —— 而日志里完全可能出现 "Cookie: xxx"
                //   （调试输出 / 用户粘贴 / 错误消息回显请求头）。
                //   这个包是要发出去的，漏一次就等于泄漏 Cookie。
                File.WriteAllText(Path.Combine(tmp, "日志.txt"),
                    string.IsNullOrEmpty(logText) ? "（空）" : SanitizeText(logText),
                    new UTF8Encoding(true));

                File.WriteAllText(Path.Combine(tmp, "站点评分.txt"),
                    BuildScoreText(scores), new UTF8Encoding(true));

                // 配置文件：**打码后**再放进去
                CopySanitized(Path.Combine(appDir, "settings.ini"),
                    Path.Combine(tmp, "settings.ini.txt"));
                CopySanitized(Path.Combine(appDir, "站点配置.ini"),
                    Path.Combine(tmp, "站点配置.ini.txt"));

                File.WriteAllText(Path.Combine(tmp, "怎么读这个包.txt"),
                    ReadmeText(), new UTF8Encoding(true));

                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tmp, zipPath, CompressionLevel.Optimal, false);
                return zipPath;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        private static void CopySanitized(string src, string dst)
        {
            if (!File.Exists(src)) { File.WriteAllText(dst, "（没有这个文件）", new UTF8Encoding(true)); return; }
            string text;
            try { text = File.ReadAllText(src, Encoding.UTF8); }
            catch { try { text = File.ReadAllText(src); } catch { text = "（读取失败）"; } }
            File.WriteAllText(dst, SanitizeText(text), new UTF8Encoding(true));
        }

        /// <summary>环境与设置（**不含**任何密钥；不含书名）</summary>
        private static string BuildEnvText(string appDir, IEnumerable<string> extra)
        {
            var sb = new StringBuilder();
            sb.AppendLine("novel-downloader 诊断包");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("【程序】");
            sb.AppendLine("  版本：" + UpdateChecker.CurrentVersionText);
            sb.AppendLine("  目录：" + appDir);
            sb.AppendLine("  后端：" + (Http.CurlAvailable ? "curl（" + Http.CurlPath + "）" : ".NET 内置"));
            sb.AppendLine("  回放模式：" + (!string.IsNullOrEmpty(Http.ReplayDir) ? Http.ReplayDir : "（关）"));
            sb.AppendLine();
            sb.AppendLine("【系统】");
            try
            {
                sb.AppendLine("  操作系统：" + Environment.OSVersion);
                sb.AppendLine("  .NET：" + Environment.Version);
                sb.AppendLine("  64 位进程：" + Environment.Is64BitProcess);
                sb.AppendLine("  处理器数：" + Environment.ProcessorCount);
                sb.AppendLine("  区域：" + System.Globalization.CultureInfo.CurrentCulture.Name);
                sb.AppendLine("  当前目录：" + Environment.CurrentDirectory);
            }
            catch { sb.AppendLine("  （读系统信息失败）"); }
            sb.AppendLine();
            sb.AppendLine("【运行状态】");
            sb.AppendLine("  限流计数：" + Http.ThrottleHits);
            sb.AppendLine("  Retry-After：" + Http.LastRetryAfterSeconds + " 秒");
            sb.AppendLine("  最近状态码：" + Http.LastStatusCode);
            try
            {
                var s = AppSettings.Current;
                if (s != null)
                {
                    // ToString 里可能带 API Key —— 逐行打码
                    sb.AppendLine("  设置：" + SanitizeLine("settings=" + s.ToString()));
                }
            }
            catch { }
            sb.AppendLine();
            sb.AppendLine("【书架】");
            try
            {
                var shelf = Bookshelf.Load();
                int n = shelf == null || shelf.Entries == null ? 0 : shelf.Entries.Count;
                // ★ 只报数量，不列书名：书单是隐私，而这个包是要发出去的。
                sb.AppendLine("  共 " + n + " 本（书名已省略）");
            }
            catch (Exception ex) { sb.AppendLine("  读取失败：" + ex.Message); }
            sb.AppendLine();
            sb.AppendLine("【磁盘】");
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(appDir));
                sb.AppendLine("  程序所在盘 " + d.Name + "：" + d.DriveType + "，剩余 "
                    + (d.IsReady ? (d.AvailableFreeSpace / 1024 / 1024) + " MB" : "（未就绪）"));
            }
            catch { }
            if (extra != null)
            {
                sb.AppendLine();
                sb.AppendLine("【补充】");
                foreach (string line in extra)
                    if (!string.IsNullOrEmpty(line)) sb.AppendLine("  " + SanitizeLine(line));
            }
            return sb.ToString();
        }

        private static string BuildScoreText(IList<SiteScore> scores)
        {
            if (scores == null || scores.Count == 0) return "（还没有实测过站点）\r\n";
            var sb = new StringBuilder();
            sb.AppendLine("站点实测（单位 ms；∞ = 该项没测通）");
            sb.AppendLine("站点\t搜索\t目录\t正文\t章数\t结论\t实测时间");
            try
            {
                foreach (var s in SiteRater.Rank(scores))
                {
                    sb.AppendLine(string.Join("\t", new string[]
                    {
                        (s.DisplayName ?? s.Key ?? "?"),
                        Ms(s.SearchMeasured ? s.SearchMs : double.PositiveInfinity),
                        Ms(s.TocMeasured ? s.TocMs : double.PositiveInfinity),
                        Ms(s.ChapterMeasured ? s.ChapterMs : double.PositiveInfinity),
                        s.TocCount.ToString(),
                        s.Ok ? "可用" : ("不可用：" + (s.Error ?? "未知")),
                        s.MeasuredAt == DateTime.MinValue ? "?" : s.MeasuredAt.ToString("yyyy-MM-dd HH:mm"),
                    }));
                }
            }
            catch (Exception ex) { sb.AppendLine("（整理失败：" + ex.Message + "）"); }
            return sb.ToString();
        }

        private static string Ms(double v)
        {
            return double.IsInfinity(v) || v <= 0 ? "∞" : ((int)v).ToString();
        }

        private static string ReadmeText()
        {
            return
                "这个包里是什么\r\n" +
                "==============\r\n" +
                "  环境与设置.txt    程序/系统/运行状态/书架数量（不含书名）\r\n" +
                "  日志.txt          界面日志框里的全文\r\n" +
                "  站点评分.txt      上次实测各站的搜索/目录/正文耗时（如果有）\r\n" +
                "  settings.ini.txt        设置（Cookie/Token/API Key 已打码）\r\n" +
                "  站点配置.ini.txt        站点参数（Cookie 已打码）\r\n" +
                "\r\n" +
                "包里**没有**：下载的正文、网页缓存、书名清单。\r\n";
        }
    }
}
