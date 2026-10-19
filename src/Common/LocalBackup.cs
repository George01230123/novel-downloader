using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 启动时把**不可重建**的文件备份到固定盘（默认 `我的文档\novel-downloader-备份\`）。
    ///
    /// ★ 为什么需要（真实事故）：
    ///   程序把 `书架.json` / `站点配置.ini` / `settings.ini` 都写在**程序目录**里，
    ///   而用户把程序放在一个**可拔插的 U 盘**上。这个 U 盘在开发过程中掉过两次线
    ///   （一次正好在升级中间，目录整个"不存在"）。
    ///   下载中途被拔 → 文件写坏；U 盘丢了 → **书架和几十本书的记录一起没了**。
    ///
    /// 设计原则（都为了"绝不添乱"）：
    ///   · **只读源、只写备份目录**，绝不改程序目录里任何文件；
    ///   · 任何失败都只记日志，**绝不影响启动**（备份是保险，不是前置条件）；
    ///   · 同一份快照 30 分钟内不重复做（避免频繁重启把磁盘刷满）；
    ///   · 只保留最近 5 份（旧的自动删）。
    ///
    /// 不备份 `搜索索引.json` 和 `cache\`：它们能从网上重建，而且体积会涨。
    /// </summary>
    internal static class LocalBackup
    {
        /// <summary>要备份的文件（按文件名，位于程序目录）。只挑**丢了就找不回来**的。</summary>
        internal static readonly string[] FilesToBackup = new string[]
        {
            "书架.json",        // 书架 + 队列（Bookshelf.SaveQueue 也写在它里面）
            "站点配置.ini",     // 并发/间隔/超时/UA/编码
            "settings.ini",     // 保存路径、编码、AI 配置等
        };

        /// <summary>保留最近几份快照</summary>
        internal const int KeepSnapshots = 5;

        /// <summary>两次快照之间至少隔多少分钟</summary>
        internal const int MinIntervalMinutes = 30;

        /// <summary>快照目录名的前缀（后面接 yyyy-MM-dd_HHmmss）</summary>
        internal const string SnapshotPrefix = "";

        /// <summary>默认备份根目录：我的文档（在固定盘上）</summary>
        public static string DefaultRoot
        {
            get
            {
                try
                {
                    string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    if (!string.IsNullOrEmpty(docs)) return Path.Combine(docs, "novel-downloader-备份");
                }
                catch { }
                try
                {
                    // 极端情况（没有"我的文档"）：退回本地应用数据
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "novel-downloader-备份");
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// 该不该现在做快照。纯函数，便于离线断言。
        /// lastAt = 最近一份快照的时间；null 表示还没有。
        /// </summary>
        internal static bool ShouldSnapshot(DateTime now, DateTime? lastAt, int minMinutes)
        {
            if (!lastAt.HasValue) return true;
            if (minMinutes <= 0) return true;
            return (now - lastAt.Value).TotalMinutes >= minMinutes;
        }

        /// <summary>
        /// 关掉自动备份（**只有自动化测试会设**）。
        ///
        /// ★ 为什么需要：离线测试里会 new MainForm 来量布局/结果栏，
        ///   而 MainForm 构造末尾会触发一次备份 —— 于是跑一次测试就
        ///   往用户的「我的文档」里塞一份快照，还在后台多了几个线程。
        ///   测试不该在仓库外面留东西，也不该引入额外线程。
        /// </summary>
        internal static bool Disabled;

        /// <summary>
        /// 做一次快照。成功返回快照目录；没有可备份的文件、或频率太高、或出错都返回 null。
        /// **不抛异常**（备份失败不该让程序起不来）。
        /// </summary>
        public static string Run(string appDir, string root, Action<string> log)
        {
            if (Disabled) return null;
            try
            {
                if (string.IsNullOrEmpty(appDir) || string.IsNullOrEmpty(root)) return null;
                if (!Directory.Exists(appDir)) return null;
                // 备份目录不能在程序目录里面（否则"程序目录整个没了"时备份也跟着没）
                if (IsInside(root, appDir)) return null;

                // 有没有可备份的文件？
                var todo = new List<string>();
                foreach (string name in FilesToBackup)
                {
                    string p = Path.Combine(appDir, name);
                    if (File.Exists(p)) todo.Add(name);
                }
                if (todo.Count == 0) return null;

                // 频率控制：看最新一份快照的时间
                DateTime? last = NewestSnapshotTime(root);
                if (!ShouldSnapshot(DateTime.Now, last, MinIntervalMinutes)) return null;

                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string snap = Path.Combine(root, SnapshotPrefix + stamp);
                Directory.CreateDirectory(snap);

                int copied = 0;
                foreach (string name in todo)
                {
                    try
                    {
                        File.Copy(Path.Combine(appDir, name), Path.Combine(snap, name), true);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log("备份 " + name + " 失败：" + ex.Message);
                    }
                }
                if (copied == 0)
                {
                    try { Directory.Delete(snap, true); } catch { }
                    return null;
                }

                PruneOld(root, KeepSnapshots);
                if (log != null) log("已备份 " + copied + " 个文件到 " + snap);
                return snap;
            }
            catch (Exception ex)
            {
                if (log != null) log("备份失败（不影响使用）：" + ex.Message);
                return null;
            }
        }

        /// <summary>后台跑一次备份（不阻塞启动）</summary>
        public static void RunAsync(string appDir, Action<string> log)
        {
            try
            {
                string root = DefaultRoot;
                if (string.IsNullOrEmpty(root)) return;
                var t = new System.Threading.Thread(delegate ()
                {
                    try { Run(appDir, root, log); } catch { }
                });
                t.IsBackground = true;
                t.Name = "LocalBackup";
                t.Start();
            }
            catch { }
        }

        /// <summary>最新一份快照的时间；没有返回 null</summary>
        internal static DateTime? NewestSnapshotTime(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return null;
                DateTime? best = null;
                foreach (string d in Directory.GetDirectories(root))
                {
                    DateTime t;
                    if (!TryParseStamp(Path.GetFileName(d), out t)) continue;
                    if (!best.HasValue || t > best.Value) best = t;
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>只保留最近 keep 份快照（按目录名里的时间戳排序，不认识的名字不动）</summary>
        internal static int PruneOld(string root, int keep)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(root) || keep < 1) return 0;
                var snaps = new List<KeyValuePair<DateTime, string>>();
                foreach (string d in Directory.GetDirectories(root))
                {
                    DateTime t;
                    if (!TryParseStamp(Path.GetFileName(d), out t)) continue;
                    snaps.Add(new KeyValuePair<DateTime, string>(t, d));
                }
                if (snaps.Count <= keep) return 0;
                snaps.Sort(delegate (KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b)
                {
                    return a.Key.CompareTo(b.Key);      // 旧 → 新
                });
                int remove = snaps.Count - keep;
                for (int i = 0; i < remove; i++)
                {
                    try { Directory.Delete(snaps[i].Value, true); deleted++; }
                    catch { }
                }
            }
            catch { }
            return deleted;
        }

        /// <summary>目录名 → 时间戳（`yyyy-MM-dd_HHmmss`）</summary>
        internal static bool TryParseStamp(string dirName, out DateTime when)
        {
            when = DateTime.MinValue;
            if (string.IsNullOrEmpty(dirName)) return false;
            string s = dirName.Trim();
            if (SnapshotPrefix.Length > 0 && s.StartsWith(SnapshotPrefix, StringComparison.Ordinal))
                s = s.Substring(SnapshotPrefix.Length);
            return DateTime.TryParseExact(s, "yyyy-MM-dd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out when);
        }

        /// <summary>child 是否在 parent 里面（含相等）—— 用来避免把备份写进程序目录</summary>
        internal static bool IsInside(string child, string parent)
        {
            try
            {
                if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
                string a = Path.GetFullPath(child).TrimEnd('\\', '/');
                string b = Path.GetFullPath(parent).TrimEnd('\\', '/');
                if (a.Length == 0 || b.Length == 0) return false;
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
