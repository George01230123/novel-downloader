using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 极简配置：一个 settings.ini（键=值），放在 exe 同目录。
    /// 为什么不用 json/注册表：.NET Framework 自带的配置体系太重，
    /// 而这个工具只有几个数字要存，纯文本用户还能自己改。
    /// 读取失败/文件损坏一律回退默认值，绝不因为配置问题起不来。
    /// </summary>
    public class AppSettings
    {
        public const string FileName = "settings.ini";

        // ---- 默认值（实测调出来的，见 README 的实测数据一节）----
        /// <summary>移动版「离线模式」并发线程：整本正文预抓，8 线程实测 1000 章约 10 分钟</summary>
        public int BiqugaOfflineWorkers = 8;
        /// <summary>移动版「非离线」并发线程</summary>
        public int BiqugaOnlineWorkers = 6;
        /// <summary>PC 版并发线程（PC 版正文要串行走 prev 链，主要影响目录遍历）</summary>
        public int BiqugaPcWorkers = 6;
        /// <summary>每次请求之间的最小/最大随机间隔（毫秒），避免把站点打疼</summary>
        public int MinDelayMs = 60;
        public int MaxDelayMs = 180;
        /// <summary>整本首次遍历目录的超时保护（分钟），超过就中止</summary>
        public int CrawlTimeoutMinutes = 60;
        /// <summary>失败章节自动重试轮数：整本下完后，只对失败的那几章再跑一遍</summary>
        public int RetryPasses = 1;
        /// <summary>
        /// HTTP 代理（形如 http://127.0.0.1:7897），空 = 直连。
        ///
        /// ★ 以前 Http.Proxy 字段**存在但没人能设置**（没有 ini 键、没有界面）——
        ///   等于死代码。而实测：27 个小说站里 19 个连不上/403，
        ///   很可能就是网络问题而不是站点问题。接上它，用户挂个代理就能多几个源。
        ///   只接受 http/https（SOCKS 由 curl 后端支持；.NET 后端不支持 SOCKS）。
        /// </summary>
        public string Proxy = "";

        /// <summary>
        /// 输出繁体：写盘时把正文从简体转成繁体（港台读者/阅读器用）。
        /// 为什么放在全局设置而不是站点参数：这是**读者偏好**，跟数据来自哪个站无关 ——
        /// 同一本书换个源，用户想要的还是繁体。
        /// </summary>
        public bool OutputTraditional = false;

        // ---- AI 裁决错字（默认全关：不填就用不了，也绝不会偷偷联网）----

        /// <summary>
        /// 是否启用 AI 裁决。**默认 false**。
        /// 为什么不默认开：开了意味着正文片段会被发到外部服务，
        /// 而"不把内容交给第三方"是这个工具一直守着的姿态 —— 必须用户显式同意。
        /// </summary>
        public bool AiEnabled = false;

        /// <summary>后端：ollama（本地，不联网）/ openai（云端，兼容 OpenAI 格式）</summary>
        public string AiBackend = "ollama";

        /// <summary>服务地址。Ollama 默认 127.0.0.1:11434</summary>
        public string AiBaseUrl = "http://127.0.0.1:11434";

        /// <summary>模型名，例如 qwen2.5:7b 或 deepseek-chat</summary>
        public string AiModel = "";

        /// <summary>
        /// 云端 API Key（本地 Ollama 留空）。
        /// 说明：**以明文存在 settings.ini 里**，设置界面会明确提示这一点 ——
        /// 这个项目零依赖、没有安全存储可用，与其假装安全不如说清楚。
        /// </summary>
        public string AiApiKey = "";

        /// <summary>一次请求塞几条差异（越大越省请求数，但单次响应也越容易跑格式）</summary>
        public int AiBatchSize = 10;

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        private static AppSettings _current;

        /// <summary>当前生效的配置（首次访问时从磁盘加载）</summary>
        public static AppSettings Current
        {
            get
            {
                if (_current == null) _current = Load(DefaultPath);
                return _current;
            }
        }

        public static AppSettings Load(string path)
        {
            var s = new AppSettings();
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = t.Substring(0, eq).Trim().ToLowerInvariant();
                        var v = t.Substring(eq + 1).Trim();

                        // 布尔键先判：它们的值是 true/false，走 int.TryParse 会全部被丢掉
                        if (k == "outputtraditional" || k == "aienabled")
                        {
                            bool b;
                            bool val;
                            if (bool.TryParse(v, out b)) val = b;
                            else if (v == "1") val = true;
                            else if (v == "0") val = false;
                            else continue;
                            if (k == "outputtraditional") s.OutputTraditional = val;
                            else s.AiEnabled = val;
                            continue;
                        }

                        // 字符串键（AI 配置都是字符串，不能让它们走 int 解析被丢掉）
                        if (k == "aibackend") { s.AiBackend = v; continue; }
                        if (k == "aibaseurl") { s.AiBaseUrl = v; continue; }
                        if (k == "aimodel") { s.AiModel = v; continue; }
                        if (k == "aiapikey") { s.AiApiKey = v; continue; }
                        if (k == "proxy") { s.Proxy = v; continue; }

                        int n;
                        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                        s.Set(k, n);
                    }
                }
            }
            catch { /* 配置坏了就用默认值，不影响启动 */ }
            s.Clamp();
            return s;
        }

        /// <summary>把值夹到安全范围：并发 0 或 999 都不是用户想要的</summary>
        public void Clamp()
        {
            BiqugaOfflineWorkers = ClampInt(BiqugaOfflineWorkers, 1, 32);
            BiqugaOnlineWorkers = ClampInt(BiqugaOnlineWorkers, 1, 32);
            BiqugaPcWorkers = ClampInt(BiqugaPcWorkers, 1, 32);
            MinDelayMs = ClampInt(MinDelayMs, 0, 5000);
            MaxDelayMs = ClampInt(MaxDelayMs, MinDelayMs, 5000);
            CrawlTimeoutMinutes = ClampInt(CrawlTimeoutMinutes, 1, 600);
            RetryPasses = ClampInt(RetryPasses, 0, 3);
            AiBatchSize = ClampInt(AiBatchSize, 1, 50);
            if (string.IsNullOrEmpty(AiBackend)) AiBackend = "ollama";
            if (string.IsNullOrEmpty(AiBaseUrl)) AiBaseUrl = "http://127.0.0.1:11434";
            if (AiModel == null) AiModel = "";
            if (AiApiKey == null) AiApiKey = "";
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private void Set(string key, int value)
        {
            switch (key)
            {
                case "biqugaofflineworkers": BiqugaOfflineWorkers = value; break;
                case "biqugaonlineworkers": BiqugaOnlineWorkers = value; break;
                case "biqugapcworkers": BiqugaPcWorkers = value; break;
                case "mindelayms": MinDelayMs = value; break;
                case "maxdelayms": MaxDelayMs = value; break;
                case "crawltimeoutminutes": CrawlTimeoutMinutes = value; break;
                case "retrypasses": RetryPasses = value; break;
                // ★ 每个"数值键"都必须在这里有分支，否则 Load 里解析出来也会被丢掉、
                //   悄悄退回默认值。这个错真犯过：AiBatchSize 在 Load 里解析了、
                //   Set 里忘了接，于是配 7 也永远生效成 10 —— 而且不报任何错，
                //   只能靠"设置往返"断言发现。
                case "aibatchsize": AiBatchSize = value; break;
                // 未知键忽略：老版本写的键，新版本不该因此报错
            }
        }

        public void Save()
        {
            Save(DefaultPath);
        }

        public void Save(string path)
        {
            Clamp();
            var sb = new StringBuilder();
            sb.AppendLine("# 小说下载器配置（这个文件可以手改，改完重启程序生效）");
            sb.AppendLine("# 并发数越大越快，但太大会被站点限速；8 是实测比较稳的值。");
            sb.AppendLine("BiqugaOfflineWorkers=" + BiqugaOfflineWorkers);
            sb.AppendLine("BiqugaOnlineWorkers=" + BiqugaOnlineWorkers);
            sb.AppendLine("BiqugaPcWorkers=" + BiqugaPcWorkers);
            sb.AppendLine("MinDelayMs=" + MinDelayMs);
            sb.AppendLine("MaxDelayMs=" + MaxDelayMs);
            sb.AppendLine("CrawlTimeoutMinutes=" + CrawlTimeoutMinutes);
            sb.AppendLine("RetryPasses=" + RetryPasses);
            sb.AppendLine("# 代理：形如 proxy=http://127.0.0.1:7897（留空 = 直连）。只能填 http/https。");
            sb.AppendLine("Proxy=" + Proxy);
            sb.AppendLine("# OutputTraditional=true 时，写盘/导出会把正文转成繁体（本地转换，不联网）");
            sb.AppendLine("OutputTraditional=" + (OutputTraditional ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# AI 裁决错字（默认全关）");
            sb.AppendLine("#");
            sb.AppendLine("# 作用：把「检测错字」里**两个源写法不同、不知道哪个对**的那几处差异");
            sb.AppendLine("#       交给大模型判断。注意只发差异点前后十几个字，不发整章、不发整本。");
            sb.AppendLine("#");
            sb.AppendLine("# AiBackend=ollama  本地模型，不联网、不花钱（推荐，需要先装 Ollama）");
            sb.AppendLine("# AiBackend=openai  云端接口，兼容 OpenAI 格式的那一类");
            sb.AppendLine("#                   （DeepSeek / Kimi / 通义 / 硅基流动 / OpenAI 本身…）");
            sb.AppendLine("#");
            sb.AppendLine("# ⚠ 选 openai 时，差异片段会被发送到该服务商；");
            sb.AppendLine("#   AiApiKey 以**明文**保存在这个文件里，请不要把本文件分享给别人。");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("AiEnabled=" + (AiEnabled ? "true" : "false"));
            sb.AppendLine("AiBackend=" + AiBackend);
            sb.AppendLine("AiBaseUrl=" + AiBaseUrl);
            sb.AppendLine("AiModel=" + AiModel);
            sb.AppendLine("AiApiKey=" + AiApiKey);
            sb.AppendLine("AiBatchSize=" + AiBatchSize);
            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                _current = this;
            }
            catch (Exception ex)
            {
                throw new Exception("保存配置失败：" + ex.Message + "（目录可能不可写，例如装在 Program Files 下）");
            }
        }

        public override string ToString()
        {
            var c = new List<string>();
            c.Add("离线并发=" + BiqugaOfflineWorkers);
            c.Add("在线并发=" + BiqugaOnlineWorkers);
            c.Add("PC并发=" + BiqugaPcWorkers);
            c.Add("间隔=" + MinDelayMs + "~" + MaxDelayMs + "ms");
            c.Add("重试=" + RetryPasses);
            return string.Join("，", c.ToArray());
        }
    }
}
