using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TomatoBiquga
{
    /// <summary>
    /// 下载执行器：界面和命令行自测都用这一份代码，保证“测过的就是你会用到的”。
    /// 负责：逐章下载 → 边下边存 → 失败重试 → 最后修正表头 → 报告路径。
    /// </summary>
    public class DownloadRunner
    {
        public ISite Site;
        public BookInfo Book;
        // 初始化成空列表：以前这里是 null，程序化调用（自测/脚本/未来的 CLI）先 Add 再 Run
        // 会直接空引用炸掉；Run() 里本来就有"没有选中任何章节"的明确报错。
        public List<ChapterInfo> Chapters = new List<ChapterInfo>();
        public string RootDir;          // 保存的根目录
        public Action<string> Log = delegate { };
        public Action<int, int> OnProgress = delegate { };   // (done, total)
        public Func<bool> IsCanceled = () => false;

        public int Ok, Skipped, Failed;
        public string OutputFile;
        public string BookDir;
        public int FromCacheCount;      // 有多少章是直接用目录遍历时抓到的缓存
        public string ReportFile;       // 缺失章节报告（只有真的缺章时才生成）

        /// <summary>
        /// 有多少章**因为文件里已经有正文**而直接跳过（既没联网、也没重写文件）。
        ///
        /// 这就是「增量」：以前只会"补齐"（知道哪章在哪，却还是全部重抓一遍），
        /// 现在重复下载同一本书几乎不花时间，也不会把文件写大。
        /// </summary>
        public int SkippedDownloaded;

        /// <summary>跳过/失败的章节（按实际顺序），用于收尾报告“到底缺了哪几章”</summary>
        public readonly List<ChapterInfo> SkippedChapters = new List<ChapterInfo>();
        public readonly List<ChapterInfo> FailedChapters = new List<ChapterInfo>();
        /// <summary>cid → 失败原因（超时/404/风控提示…）</summary>
        public readonly Dictionary<string, string> FailReasons = new Dictionary<string, string>();

        /// <summary>失败章节自动重试轮数（0 = 不重试）。界面“设置”里可改。</summary>
        public int RetryPasses = 1;

        /// <summary>
        /// 下载阶段的并发抓取线程数。**默认 1 = 老的串行行为**，
        /// 保证命令行自测 / 脚本路径的行为一字不变；界面下载时会按站点设置填 6~8。
        ///
        /// 并发的是"取正文"这一步（网络，彼此独立）；"记账 + 落盘"依然在主线程上
        /// 按目录顺序做，所以输出顺序、增量落盘、缺章补齐的按位置插入全都不受影响。
        /// 实测《沧元图》前 30 章（联网，走的就是界面这条路径）：
        /// 串行 218.4 秒 → 8 线程 39.2 秒（**5.57×**），两份产物逐字节一致。
        /// </summary>
        public int Workers = 1;

        // 增量写盘用：攒够一批就 append，避免每次都重写整个文件
        private StringBuilder _pending = new StringBuilder();
        private bool _headerWritten;
        private long _appendedBytes;    // 本次追加了多少字节（更新模式要用它算累计正文长度）
        /// <summary>写表头用的编码（GBK 时不能带 BOM，否则老阅读器会把 BOM 当正文）</summary>
        private Encoding HeaderEncoding()
        {
            if (OutputEncoding != null && OutputEncoding.CodePage != 65001)
                return Encoding.GetEncoding(OutputEncoding.CodePage);   // 不带 BOM
            return new UTF8Encoding(true);
        }

        /// <summary>追加内容时的编码：UF8 不带 BOM（文件头已经写过了），GBK 直接用 GBK</summary>
        private Encoding AppendEncoding()
        {
            if (OutputEncoding != null && OutputEncoding.CodePage != 65001) return OutputEncoding;
            return new UTF8Encoding(false);
        }

        private long _baseBodyBytes;    // 更新模式下文件的原始大小
        private Dictionary<string, ChapterSpan> _existing = new Dictionary<string, ChapterSpan>();
        private bool _replaceMissing;    // 是否把新章节插到文件里的正确位置（有锚点时为 true）
        private int _inserted;           // 本次实际插入到文件里的章数

        /// <summary>算出一本书的保存目录与 txt 路径（不确定站点时用这个）</summary>
        public static void ResolvePaths(string rootDir, string bookTitle, out string bookDir, out string txtPath)
        {
            ResolvePaths(rootDir, bookTitle, null, out bookDir, out txtPath);
        }

        /// <summary>
        /// 算出一本书的保存目录与 txt 路径，并处理**跨站点同名书**的撞车。
        ///
        /// 为什么需要 siteKey：两本书可能来自不同站点却**书名完全相同**
        /// （聚合站之间本来就互相转载）。不带站点维度的话它们会写进
        /// 同一个目录、同一个 .txt，互相覆盖 —— 用户看到的是"下完了一本，
        /// 另一本变成两半"。
        ///
        /// 但也不能无条件加后缀：老用户已经下好一堆 `书名\书名.txt`，
        /// 一改命名就等于"全部没下过"，会重新下一遍。
        /// 所以**只在真的撞车时**才加站点后缀：
        ///   · 目录还不存在                → 用干净的名字（保持老行为）
        ///   · 目录存在、且书架说它属于本站 → 用干净的名字（继续增量更新）
        ///   · 目录存在、但属于别的站点     → 加后缀 `书名 [站点名]`
        ///   · 目录存在、却查不到是谁的     → 也加后缀（宁可多一个目录，不要覆盖别人的书）
        /// </summary>
        public static void ResolvePaths(string rootDir, string bookTitle, string siteKey,
            out string bookDir, out string txtPath)
        {
            string safe = Http.SafeFileName(bookTitle);
            bookDir = Path.Combine(rootDir, safe);
            txtPath = Path.Combine(bookDir, safe + ".txt");

            if (string.IsNullOrEmpty(siteKey)) return;
            try
            {
                if (!Directory.Exists(bookDir)) return;          // 没撞车，保持干净名字

                // Bookshelf 的主键是 站点+Key(Dir/BookId)，不是书名；所以这里按
                // 记录里记下的 FilePath 反查这个目录到底是谁的。
                //
                // ★ 判据必须**只有**这一条：书架明确说这个目录属于**别的**站点。
                //   早先我还加了一条"目录里有 .txt 但查不到归属 → 也当撞车"，
                //   结果把"没有任何书架记录、但目录里已经下过书"的普通情况
                //   也判成撞车 —— 表现是**再次下载会写到一个新目录里去**
                //   （增量/续传/补齐全废，实测一次打挂 22 条测试）。
                //   而那种情况本来就不该改名：没有证据说明它是别人的书，
                //   改名只会让用户已有的产物变成孤儿。
                var shelf = Bookshelf.Load();
                string owner = OwnerSiteOfDir(shelf, bookDir);
                if (owner == null) return;                       // 归属不明 → 保持老行为
                if (string.Equals(owner, siteKey, StringComparison.OrdinalIgnoreCase))
                    return;                                      // 本来就是本站的目录 → 继续增量更新

                string alt = Http.SafeFileName(bookTitle + " [" + SiteRegistry.LabelOf(siteKey) + "]");
                bookDir = Path.Combine(rootDir, alt);
                txtPath = Path.Combine(bookDir, alt + ".txt");
            }
            catch { /* 书架读不出来就用干净名字，保持老行为 */ }
        }

        /// <summary>
        /// 这个目录的书架归属是哪个站点；查不到返回 null。
        /// 判据是记录里的 FilePath 的**父目录**和这个目录相同。
        /// </summary>
        private static string OwnerSiteOfDir(Bookshelf shelf, string dir)
        {
            if (shelf == null || shelf.Entries == null || string.IsNullOrEmpty(dir)) return null;
            string want;
            try { want = Path.GetFullPath(dir).TrimEnd('\\', '/'); }
            catch { return null; }

            foreach (var e in shelf.Entries)
            {
                if (e == null || string.IsNullOrEmpty(e.FilePath)) continue;
                try
                {
                    var parent = Path.GetDirectoryName(Path.GetFullPath(e.FilePath));
                    if (parent == null) continue;
                    if (string.Equals(parent.TrimEnd('\\', '/'), want, StringComparison.OrdinalIgnoreCase))
                        return e.Site;
                }
                catch { }
            }
            return null;
        }

        public void Run()
        {
            if (Site == null || Book == null || Chapters == null) throw new Exception("下载器没有初始化");
            if (Chapters.Count == 0) throw new Exception("没有选中任何章节");
            // ★ 下载期间**别让系统睡着**：用户挂一夜，机器进睡眠 = 网络断 = "下载停了"。
            //   成对调用；即使这里之后抛异常，Run 跑在后台线程上，线程结束时系统也会自动清掉这个标记。
            AppPower.KeepAwakeBegin();

            // ★ 慢请求归因：单章超过 2 秒就记一行 DNS/连接/TLS/首字节。
            //   以前只报"总数"，用户只能描述成"有时候特别慢"，而真相可能是
            //   TLS 握手（https 站 0.3~0.6s）或站点自身首字节慢。
            Http.SlowRequestLogger = delegate (string s) { Log("  慢章：" + s); };

            var root = string.IsNullOrEmpty(RootDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载")
                : RootDir;

            ResolvePaths(root, Book.Title, Book.Site, out BookDir, out OutputFile);
            Log("保存根目录：" + root);
            Log("本书目录：" + BookDir);
            Directory.CreateDirectory(BookDir);
            if (!Directory.Exists(BookDir))
                throw new Exception("创建目录失败：" + BookDir + "（可能没有写入权限）");

            // 每本书从零开始算限流账：上一本被限流不该拖累这一本，
            // 否则下载完一本被限流的书之后，后面每本都会以 1 线程爬。
            Http.ResetThrottle();

            // 封面：趁现在有详情页 URL 可以当 Referer（防盗链），落到书目录里，
            // 之后导出 EPUB 就不用再联网。失败不影响正文。
            if (!string.IsNullOrEmpty(Book.CoverUrl))
            {
                try { CoverFetcher.Ensure(Book.CoverUrl, BookDir, Book.Url, Log); }
                catch { /* Ensure 内部已经兜住了，这里只是双保险 */ }
            }

            // 计数器与列表每次都从零开始（同一个 runner 对象被复用也不会串数据）
            Ok = Skipped = Failed = FromCacheCount = SkippedDownloaded = 0;
            SkippedChapters.Clear();
            FailedChapters.Clear();
            FailReasons.Clear();
            _failedPass.Clear();
            _pending.Length = 0;
            _headerWritten = false;
            _appendedBytes = 0;
            int total = Chapters.Count;

            // 更新已有文件：记住它原来的大小，并准备"文件里已经有哪些章"的索引。
            // 有锚点就**按章节位置插入**（这样缺章补齐、断点续传都能落到正确位置）；
            // 旧文件没有锚点则退化成"追加到末尾"。
            if (AppendToExistingFile && File.Exists(OutputFile))
            {
                _baseBodyBytes = new FileInfo(OutputFile).Length;
                _headerWritten = true;      // 别重写表头
                _existing = ChapterIndex.ById(ChapterIndex.Scan(OutputFile));
                _replaceMissing = _existing.Count > 0;
                Log(string.Format("增量/补齐模式：已有文件 {0:N0} 字节，识别出 {1} 章{2}。",
                    _baseBodyBytes, _existing.Count,
                    _replaceMissing ? "（新章节会插入到正确位置）" : "（旧格式无锚点，只能追加到末尾）"));
            }
            else if (AppendToExistingFile)
            {
                // 调用方要求"追加"，但文件其实不存在（比如用户在下载过程中删了文件，
                // 或者目录缓存说是旧的、文件却没了）。这时按普通下载处理：
                // 正常写表头，收尾也走 FixHeaderNow —— 否则会生成一个没有表头的文件。
                AppendToExistingFile = false;
                Log("注意：文件不存在，本次按普通下载处理（会重新写表头）。");
            }

            // 并发抓取流水线：Workers > 1 时把"取正文"并发起来。
            // 注意默认 Workers = 1（串行），所以自测/脚本路径与改动前完全一致。
            var pipeline = (Workers > 1 && total > 1) ? new FetchPipeline(this, total) : null;
            if (pipeline != null)
                Log(string.Format("并发下载：{0} 线程抓取正文，按目录顺序落盘（站点限流时会自动降并发）。", Workers));

            for (int i = 0; i < total; i++)
            {
                if (IsCanceled())
                {
                    Log("已取消，正在保存已下载的部分…");
                    break;
                }
                var c = Chapters[i];
                OnProgress(i, total);
                if (pipeline != null) pipeline.WaitAndCommit(i);
                else FetchInto(c);
                OnProgress(i + 1, total);

                // 每 20 章把新增内容追加到文件（append 而不是重写整个文件，
                // 否则 1800 章的书要反复重写十几 MB，写入阶段会拖到十几分钟）
                if ((i + 1) % 20 == 0 || i == total - 1)
                {
                    try { FlushIncremental(); }
                    catch (Exception ex) { Log("写文件失败：" + ex.Message); }
                }
                // 串行路径照旧每章歇一下；并发路径的节流在抓取线程里（Http.Request 自带退避），
                // 这里再睡就把并发的好处睡没了
                if (pipeline == null) Http.Polite();
            }
            if (pipeline != null) pipeline.Stop();

            // 把这本书并进本地搜索索引 —— 这样「本地查」能查到**下过的书**，
            // 不只是搜过的书。失败不影响下载结果，所以整段兜住。
            try { if (Book != null && !string.IsNullOrEmpty(Book.Title)) SearchIndex.AddBook(Book); }
            catch { }
            // 自动重试：只对失败的那几章再跑一遍（站点偶尔抽风，重试一遍通常就好了）
            if (RetryPasses > 0 && FailedChapters.Count > 0 && !IsCanceled())
            {
                var retryList = new List<ChapterInfo>(FailedChapters);
                for (int pass = 1; pass <= RetryPasses; pass++)
                {
                    Log(string.Format("=== 自动重试第 {0}/{1} 轮：{2} 章 ===", pass, RetryPasses, retryList.Count));
                    var still = new List<ChapterInfo>();
                    _failedPass.Clear();
                    foreach (var c in retryList)
                    {
                        if (IsCanceled()) break;
                        FetchInto(c, true);
                        if (!string.IsNullOrEmpty(c.Text)) { c.Text = ""; continue; }   // 重试成功
                        still.Add(c);
                    }
                    FlushIncremental();
                    retryList = still;
                    if (retryList.Count == 0) { Log("重试后已无失败章节。"); break; }
                    Log(string.Format("重试第 {0} 轮结束，仍有 {1} 章失败。", pass, retryList.Count));
                    if (pass < RetryPasses) System.Threading.Thread.Sleep(1500);
                }
                if (retryList.Count > 0)
                    Log(string.Format("仍有 {0} 章没下到（多为站点侧空内容或风控），详见报告文件。", retryList.Count));
            }

            // 收尾：更新表头统计，并把“缺了哪几章”落成独立报告
            FlushIncremental();
            if (AppendToExistingFile)
            {
                // 增量更新：不能整块重写表头（会顶掉旧正文），改成"原位替换统计那一行"，
                // 并把进度标记（累计章数 + 指纹）写进去，供下次更新判断"哪些是新的"。
                // 统计要反映"文件里实际有多少章有正文"，而不是"本次下了几章"：
                //   · 断点续传：原进度 + 本次成功
                //   · 缺章补齐：文件里原本的章数 + 本次补上的（累计不变多，但进度标记要更新）
                int cumulative = CountChaptersWithBody(OutputFile);
                if (cumulative <= 0) cumulative = (CumulativeOkCount >= 0 ? CumulativeOkCount : 0) + Ok;
                int bodyBytes = (int)Math.Min(int.MaxValue, _baseBodyBytes + _appendedBytes);
                if (InjectHeaderStatistics(OutputFile, Book, cumulative, Skipped, Failed, Chapters, bodyBytes))
                    Log(string.Format("表头统计已更新：累计 {0} 章（本次新增 {1}，跳过 {2}，失败 {3}）",
                        cumulative, Ok, Skipped, Failed));
                else
                    Log("注意：这个文件的表头没有预留区（旧版本下载的），累计统计没写进去；正文不受影响。");
            }
            else
            {
                FixHeaderNow();
            }
            WriteMissingReport();
            if (Failed > 0 || Skipped > 0) LogMissingSummary();

            var fi = new FileInfo(OutputFile);
            Log(string.Format("文件已生成：{0}（{1:N0} 字节）", OutputFile, fi.Length));
            if (FromCacheCount > 0)
                Log(string.Format("其中 {0}/{1} 章直接复用了目录遍历时已抓到的正文（未重复请求站点）", FromCacheCount, Ok));
            Log(string.Format("本次下载：成功 {0} 章，跳过 {1} 章（其中已下载过 {3} 章），失败 {2} 章", Ok, Skipped, Failed, SkippedDownloaded));
            // 下载结束：恢复系统默认电源行为（必须在最后，且与上面的 Begin 成对）
            AppPower.KeepAwakeEnd();
        }

        /// <summary>文件里"有正文"的章节数（统计和进度标记用；没有锚点时返回 0）</summary>
        public static int CountChaptersWithBody(string path)
        {
            try
            {
                int n = 0;
                foreach (var s in ChapterIndex.Scan(path)) if (s.HasBody) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 缺章补齐 / 章节级续传的核心：找出文件里**缺失或空掉的章节**，
        /// 只重新抓这些章，并按目录顺序插回正确位置（不是追加到末尾）。
        ///
        /// 两个功能共用这一份实现，区别只是调用方的意图：
        ///   · 断点续传：上次中断，文件里后面还缺一大段 → 缺章列表就是"剩下的"
        ///   · 缺章补齐：整本已下完，中间有站点空内容 → 缺章列表就是中间那几个洞
        /// </summary>
        public static void FillMissing(DownloadRunner runner, string filePath, Action<string> log)
        {
            if (runner == null || string.IsNullOrEmpty(filePath)) return;
            if (log == null) log = delegate { };

            if (!File.Exists(filePath)) { log("文件不存在，没法补齐：" + filePath); return; }

            var spans = ChapterIndex.Scan(filePath);
            if (spans.Count == 0)
            {
                log("这个文件没有章节锚点（v1.0.4 之前下载的），没法定位到具体某一章；");
                log("请点「下载全部章节」重新下载一本（会写成带锚点的新格式）。");
                return;
            }

            var have = new Dictionary<string, ChapterSpan>();
            foreach (var s in spans)
            {
                // 只有标题没有正文的章也算"缺"，需要重抓
                if (!string.IsNullOrEmpty(s.Id) && s.HasBody && !have.ContainsKey(s.Id)) have[s.Id] = s;
            }

            var missing = new List<ChapterInfo>();
            foreach (var c in runner.Book.Chapters)
            {
                if (c == null || c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                if (!have.ContainsKey(c.Id)) missing.Add(c);
            }

            if (missing.Count == 0)
            {
                log(string.Format("检查完毕：文件里 {0} 章都有正文，没有需要补齐的。", have.Count));
                return;
            }

            log(string.Format("检查完毕：文件里 {0} 章完整，缺 {1} 章；开始只抓这 {2} 章（已有的不动）。",
                have.Count, missing.Count, missing.Count));

            runner.Chapters = missing;
            runner.AppendToExistingFile = true;
            runner.RetryPasses = 0;                  // 补齐只跑一遍，失败的下次再来（避免长时间卡住）
            // 保存根目录从文件路径反推（文件一定是「根目录\书名\书名.txt」）：
            // 这样调用方忘了设 RootDir 也不会崩（离线单测就踩过这个）
            if (string.IsNullOrEmpty(runner.RootDir))
            {
                var bookDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                var rootDir = (bookDir == null) ? null : Path.GetDirectoryName(bookDir);
                runner.RootDir = string.IsNullOrEmpty(rootDir) ? bookDir : rootDir;
            }
            runner.Run();

            log(string.Format("补齐结束：新补上 {0} 章，插入位置已按目录顺序排好。", runner.Ok));
            if (runner.Failed > 0)
                log(string.Format("仍有 {0} 章抓不到（多为站点侧空内容），下次可以再点一次补齐。", runner.Failed));
        }

        /// <summary>失败原因按“这一轮”记账（重试成功后要把上一轮的原因清掉）</summary>
        private readonly Dictionary<string, string> _failedPass = new Dictionary<string, string>();

        /// <summary>
        /// 抓一章并追加到待写缓冲。成功/跳过/失败的计数与列表都在这里维护，
        /// 这样“重试”走的是完全相同的代码路径（测过的就是会跑的）。
        /// </summary>
        private void FetchInto(ChapterInfo c, bool isRetry = false)
        {
            // ★ 增量：文件里已经有这一章的正文 → **既不联网、也不重写文件**。
            //
            //   在这之前，_existing 只被用来决定"新章节插到哪个位置"（见 L566），
            //   却从来没用来跳过已下载的章节 —— 所以重复下一本书时，
            //   每一章还是会被重新抓一遍、再写进文件一遍。
            //   实测：同一本 12 章连下两次，第二轮仍然跑满 78.6 秒（第一轮 94.4 秒），
            //   只快了 17%，而不是接近 0。
            //
            //   判据用 HasBody（那一段里真的有正文），**不是"只要 id 出现过"** ——
            //   站点侧空内容/公告的章节要留着让它重试，那才是"缺失补齐"的用途。
            if (!isRetry && _replaceMissing && c != null && !string.IsNullOrEmpty(c.Id))
            {
                ChapterSpan span;
                if (_existing.TryGetValue(c.Id, out span) && span != null && span.HasBody)
                {
                    SkippedDownloaded++;
                    return;
                }
            }

            Fetched f = null;
            Exception err = null;
            try { f = FetchTextOnly(c); }
            catch (Exception ex) { err = ex; }
            Commit(c, f, err, isRetry);
        }

        /// <summary>一次取数的结果（是否来自本地缓存要留给记账那一步用）</summary>
        private class Fetched
        {
            public string Text;
            public bool FromCache;
        }

        /// <summary>
        /// 只负责取一章正文：本地缓存优先，没有就联网。**不碰计数器、不碰文件**，
        /// 所以可以并发调用（并发流水线就是这么用的）。异常原样抛给调用方记账。
        /// </summary>
        private Fetched FetchTextOnly(ChapterInfo c)
        {
            var f = new Fetched();
            // 目录遍历时顺手抓到的正文（biquga 会把整本正文缓存下来），这里直接用
            var text = c.Text;
            if (string.IsNullOrEmpty(text))
            {
                var provider = Site as ITextCacheProvider;
                if (provider != null) text = provider.GetCachedText(c.Id);
            }
            // 批量预取缓存（见 PrefetchBatch 的注释）。
            // 顺序：先查批量缓存；没命中且是首次就顺手发起一批（预取给后面几章用）。
            if (string.IsNullOrEmpty(text))
            {
                text = TakeFromBatch(c.Id);
                if (string.IsNullOrEmpty(text)) PrefetchBatch();
            }
            if (!string.IsNullOrEmpty(text)) f.FromCache = true;
            else text = Site.LoadChapter(Book, c, null);

            // 繁简转换（可选）：放在这里而不是落盘前，是因为**内存里的正文也要跟着转** ——
            // 「导出 EPUB / Markdown」直接用 c.Text，如果只在写 txt 时转，
            // 就会出现「txt 是繁体、epub 是简体」这种自相矛盾的结果。
            f.Text = ApplyScript(text);
            return f;
        }

        /// <summary>
        /// 批量预取来的正文（章节 id → 正文）。
        /// 只被 <see cref="PrefetchBatch"/> 写入、被 <see cref="TakeFromBatch"/> 取走。
        /// </summary>
        private readonly Dictionary<string, string> _batchText = new Dictionary<string, string>();
        private readonly object _batchLock = new object();
        private int _batchCursor;          // 下次从 Chapters 的第几个开始批量取

        /// <summary>
        /// 是否启用批量并发抓取（默认开）。关掉就退回"每章一次独立请求"的老行为 ——
        /// 留这个开关是为了能 A/B 对比实测提速，以及万一站点讨厌并发时可以关掉。
        /// </summary>
        /// <summary>
        /// 是否启用批量并发抓取。**默认关闭**；规则站（RuleSite）由代码自动启用。
        ///
        /// ★ 为什么笔趣阁那条路仍然关着：我实现了它、也测出它快 3 倍，
        ///   但在**产物逐字比对**里发现批量路径抓到的正文和逐章路径**不一致**
        ///   （同一章：逐章 105 行 / 批量只有 37 行，内容被截断）。
        ///   id 校验能过、HTML 长度也正常，所以这不是"拿错页"，而是更隐蔽的截断，
        ///   根因我还没定位到。宁可不快，也不能让用户拿到被截断的正文。
        ///
        /// ★ 为什么规则站可以是例外（2026-10 实测确认）：
        ///   截断的根源是**多页章节** —— 笔趣阁把一章切成 cid.html / cid_1.html /
        ///   cid_2.html，批量只取第一页就少了后半段。而规则站（RuleSite）
        ///   结构上**没有多页章**：<see cref="RuleSite.NextPageKeyOf"/> 恒返回 null，
        ///   <see cref="RuleSite.LoadChapter"/> 也只发 1 个请求。
        ///   两条路径还共用同一个 <see cref="RuleSite.ChapterTextFromHtml"/>，
        ///   所以正文不可能不一致。（有 BatchDiag 探针做逐字对拍守着。）
        /// </summary>
        public bool BatchFetchEnabled = false;

        /// <summary>
        /// 仅供测试/基准：强制指定是否走批量。
        /// null = 按站点自动判断（规则站开、笔趣阁关）。A/B 对比时用。
        /// </summary>
        public bool? BatchFetchOverride = null;

        /// <summary>
        /// 这个站点能不能走批量。规则站可以（无多页章），笔趣阁不行（截断根因未定位）。
        /// </summary>
        private bool BatchAllowed
        {
            get
            {
                if (BatchFetchOverride.HasValue) return BatchFetchOverride.Value;
                return BatchFetchEnabled || Site is RuleSite;
            }
        }

        /// <summary>仅供测试：这个站点当前会不会走批量</summary>
        internal bool BatchAllowedForTest { get { return BatchAllowed; } }

        /// <summary>仅供测试：同时在飞的批次数上限</summary>
        internal int BatchMaxInFlightForTest { get { return BatchMaxInFlight; } }

        private int _batchInFlight;        // 同时在飞的批次数

        /// <summary>
        /// 同时在飞的批次数上限 = 工作线程数。
        ///
        /// ★ 这个上限是刻意这么定的：一批内部是**串行**的 8 个请求，
        ///   所以「同时在途的请求数」仍然等于工作线程数 —— **对站点的压力没变**，
        ///   变的只是"每个线程复用同一条连接"。
        ///   实测同一个站：每章一个 curl 进程 476ms；一个进程连续抓（复用连接）
        ///   首章 429ms、之后每章 139ms。12 线程 × 139ms ≈ 14.6ms/章，
        ///   而改动前是 39.7ms/章 —— 约 2.7 倍。
        /// </summary>
        private int BatchMaxInFlight
        {
            get { return Workers < 1 ? 1 : Math.Min(Workers, 16); }
        }

        /// <summary>从批量缓存里取一章（取走即删，避免长期占内存）</summary>
        private string TakeFromBatch(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return null;
            lock (_batchLock)
            {
                string t;
                if (_batchText.TryGetValue(cid, out t))
                {
                    _batchText.Remove(cid);
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            return null;
        }

        /// <summary>
        /// 用**一个 curl 进程顺序抓一小批**章节正文，填进 <see cref="_batchText"/>。
        ///
        /// 为什么值得：原来每章都是一次独立的 <see cref="Http"/> 请求 ——
        /// 也就是**一个新 curl 进程 + 一次全新连接**。实测同一个站（笔尖中文，HTTP）：
        ///     每章一个进程   476 ms（DNS 11 + 建连 165 + 服务端 323）
        ///     一个进程连续抓  首章 429 ms、之后每章 **139 ms** → 复用连接省掉建连
        /// 所以真正的收益来自**连接复用**，不是"并发更多请求"。
        ///
        /// 为什么可以批量：**下载阶段所有章节 id 事先都知道**（目录已经拿到了），
        /// 不需要猜任何 URL —— 这和目录遍历（下一页地址只在该页 HTML 里）完全不同。
        ///
        /// 设计上的克制：
        ///   · 在飞批次数 = 工作线程数（见 <see cref="BatchMaxInFlight"/>），
        ///     所以放在站点的在途请求数**没有变多**，只是每线程复用连接；
        ///   · 批次失败**不影响正确性** —— 调用方拿不到就退回逐章请求；
        ///   · 命中即从字典移除，不会把整本书的正文都攒在内存里。
        /// </summary>
        private void PrefetchBatch()
        {
            // 站点得先声明"我能算出正文 URL"（番茄那一路算不出来，直接跳过批量）
            var urlProvider = Site as IChapterUrlProvider;
            if (urlProvider == null) return;
            if (!BatchAllowed) return;

            lock (_batchLock)
            {
                if (_batchInFlight >= BatchMaxInFlight) return;
                if (_batchCursor >= Chapters.Count) return;
                _batchInFlight++;
            }

            try
            {
                var ids = new List<string>();
                var urls = new List<string>();
                lock (_batchLock)
                {
                    int start = _batchCursor;
                    int i = start;
                    for (; i < Chapters.Count && ids.Count < HttpBatch.BatchSize; i++)
                    {
                        var ch = Chapters[i];
                        if (ch == null || string.IsNullOrEmpty(ch.Id)) continue;
                        if (_batchText.ContainsKey(ch.Id)) continue;
                        var u = urlProvider.ChapterUrlFor(Book, ch);
                        if (string.IsNullOrEmpty(u)) continue;
                        ids.Add(ch.Id);
                        urls.Add(u);
                    }
                    // ★ 游标推进到"扫描到哪"，而不是"取到了几条"。
                    //   老写法 `_batchCursor += ids.Count` 在跳过若干章时会原地打转，
                    //   多批并发时更明显（反复扫同一段、白白占着在飞名额）。
                    _batchCursor = i;
                }
                if (urls.Count == 0) return;

                var got = HttpBatch.Fetch(urls.ToArray(), urlProvider.ChapterRefererFor(Book));
                if (got.Count == 0) return;

                var referer = urlProvider.ChapterRefererFor(Book);
                var filled = new Dictionary<string, string>();

                for (int i = 0; i < urls.Count && i < ids.Count; i++)
                {
                    string html;
                    if (!got.TryGetValue(urls[i], out html)) continue;
                    var ch = new ChapterInfo { Id = ids[i] };
                    // ★ 防错位闸门：HTML 里的章节 id 必须和请求的一致。
                    //   批量是"一次交多个 URL、一次收多个文件"，万一配对错位
                    //   就会**静默地把别的章写进正文**。对不上就丢掉这条，
                    //   让调用方逐章退回 —— 宁可慢，绝不写错内容。
                    if (!urlProvider.HtmlMatchesChapter(html, ch)) continue;

                    var parts = new List<string>();
                    var first = urlProvider.ChapterTextFromHtml(html);
                    if (!string.IsNullOrEmpty(first)) parts.Add(first);

                    // ★ 多页章节：有些章被切成 cid.html / cid_1.html / cid_2.html…
                    //   只抓第一页会得到**被静默截断的正文**（实测 2494 字符只剩 879）。
                    //   这里按"逐页推进"续抓，和站点 FetchChapterText 的循环形状一致：
                    //   每取回一页都再问一次"后面还有没有下一页"，直到没有为止。
                    var nextKey = urlProvider.NextPageKeyOf(html, ch);
                    for (int guard = 0; nextKey != null && guard < 29; guard++)
                    {
                        var pageCh = new ChapterInfo { Id = nextKey };
                        var pageUrl = urlProvider.ChapterUrlFor(Book, pageCh);
                        if (string.IsNullOrEmpty(pageUrl)) break;
                        string pageHtml = null;
                        try { pageHtml = Http.Get(pageUrl, referer); } catch { }
                        if (string.IsNullOrEmpty(pageHtml)) break;
                        var pt = urlProvider.ChapterTextFromHtml(pageHtml);
                        if (!string.IsNullOrEmpty(pt)) parts.Add(pt);
                        // 从"当前页码 + 1"继续往后找，而不是每次重新从 1 找 ——
                        // 否则三页的章节只拼得到两页
                        int curPage = 0;
                        int us = nextKey.LastIndexOf('_');
                        if (us >= 0) int.TryParse(nextKey.Substring(us + 1), out curPage);
                        nextKey = urlProvider.NextPageKeyAfter(pageHtml, ids[i], curPage);
                    }

                    if (parts.Count == 0) continue;
                    filled[ids[i]] = string.Join("\n\n", parts.ToArray()).Trim();
                }

                if (filled.Count == 0) return;
                lock (_batchLock)
                {
                    foreach (var kv in filled)
                        if (!_batchText.ContainsKey(kv.Key)) _batchText[kv.Key] = kv.Value;
                }
            }
            catch { /* 批量是加速手段，失败就让调用方逐章退回 */ }
            finally { lock (_batchLock) { if (_batchInFlight > 0) _batchInFlight--; } }
        }

        /// <summary>
        /// 记账 + 落盘：成功/跳过/失败的计数与列表都在这里维护，
        /// 这样“重试”和“并发抓取”走的都是完全相同的代码路径（测过的就是会跑的）。
        ///
        /// ★ 永远在主线程上、按目录顺序调用（串行路径直接调，并发路径由
        ///   FetchPipeline.WaitAndCommit 按序调）—— 所以这里不需要加锁。
        /// </summary>
        private void Commit(ChapterInfo c, Fetched f, Exception err, bool isRetry)
        {
            string prefix = isRetry ? "[重试] " : "";
            if (err != null)
            {
                if (isRetry) Failed--;                                   // 计入本轮的失败
                Failed++;
                c.Text = "";
                _failedPass[c.Id] = err.Message;
                FailReasons[c.Id] = err.Message;
                if (!FailedChapters.Contains(c)) FailedChapters.Add(c);
                Log(string.Format("{0}失败 {1}：{2}", prefix, c.Title, err.Message));
                return;
            }

            var text = f == null ? null : f.Text;
            if (f != null && f.FromCache) FromCacheCount++;

            if (string.IsNullOrEmpty(text) || text.Length < 40)
            {
                Skipped++;
                c.Text = "";
                if (!SkippedChapters.Contains(c)) SkippedChapters.Add(c);
                // ★ 原因要**分开**：以前一律写"站点侧空内容/公告"。
                //   但短文本可能是"正在手打中"（书真的没内容），
                //   也可能是**被拦/限流的提示页**（等一会儿重试就有）。
                //   两者处理方式完全不同，混在一起用户会白等或白换源。
                string why = null;
                try { why = SiteDiagnostics.DiagnoseShortChapter(text); } catch { }
                if (string.IsNullOrEmpty(why)) why = "站点侧空内容/公告";
                SkipReasons[c.Id] = why;
                Log(string.Format("{0}跳过：{1} —— {2}", prefix, c.Title, why));
            }
            else
            {
                Ok++;
                // ★ 清洗在**这里**做（"进内存"的那一刻），而不是在写 txt 那一步。
                //
                //   原来只清洗 txt：ChapterIndex.BuildChapterBlock → TextCleaner.CleanBody，
                //   而 EPUB / Markdown 直接用 c.Text。于是同一本书会出现
                //   **txt 干净、epub 里广告还在** —— 而且这个 bug 只在
                //   "下载完当次直接导出"时暴露（关掉程序再从 txt 反解就正常了），
                //   所以一直没被发现。
                //
                //   现在 c.Text 本身就是干净的，txt / EPUB / Markdown / 字数统计 /
                //   错字检测**全部看到同一份正文**。这就是"单一咽喉点"：
                //   将来改清洗规则只用改一处，不会出现两个出口不一致。
                c.Text = TextCleaner.CleanBody(text);
                // 章节块统一由 ChapterIndex 生成：里面会先写一行锚点 <!--c:id-->，
                // 「缺章补齐」和「章节级续传」靠它定位到具体某一章（见 ChapterIndex.cs）
                var block = ChapterIndex.BuildChapterBlock(c.Id, c.Title, c.Text);
                if (_replaceMissing)
                {
                    // 插入模式：直接落到文件里的正确位置（不是简单追加）
                    if (ChapterIndex.InsertChapter(OutputFile, Book, c, text)) _inserted++;
                    else { _pending.Append(block); Log("（插入失败，退化为追加）" + c.Title); }
                }
                else
                {
                    _pending.Append(block);
                }
                if (isRetry && Failed > 0) Failed--;                 // 重试成功：把上一轮的失败计数还回去
                FailReasons.Remove(c.Id);
                FailedChapters.Remove(c);
                Log(string.Format("{0}完成 {1}（{2} 字）", prefix, c.Title, text.Length));
            }
        }

        /// <summary>
        /// 「并发抓取、按序提交」的流水线。
        ///
        /// 一章正文要经过「取数（网络，慢）→ 记账 → 写文件（快）」，
        /// 只有取数是瓶颈、而且彼此独立；记账和写文件必须按目录顺序。
        /// 所以把取数丢进线程池（最多 Workers 个在飞），主线程按 i=0,1,2… 顺序
        /// 取结果并走**和串行版完全相同**的 Commit —— 输出一字不差，只是快得多。
        ///
        /// 在飞窗口每次提交时都用 Http.AdaptiveWorkers 现算：站点一旦限流
        /// （429 / 正文变成“访问太频繁”），窗口会自动收缩，最低 1（退化成串行）。
        /// </summary>
        private class FetchPipeline
        {
            private class Outcome
            {
                public Fetched Result;
                public Exception Error;
            }

            private readonly DownloadRunner _owner;
            private readonly int _total;
            private readonly Outcome[] _outcomes;      // 每个下标只被"抓它的那个线程"写一次
            private readonly ManualResetEventSlim[] _ready;
            private int _next;                          // 下一个要提交的下标（只在主线程改）
            private int _inFlight;
            private int _lastWindow;                    // 上一次的自适应并发（用于"变化时记一行"）
            private bool _stopped;

            public FetchPipeline(DownloadRunner owner, int total)
            {
                _owner = owner;
                _total = total;
                _outcomes = new Outcome[total];
                _ready = new ManualResetEventSlim[total];
                for (int i = 0; i < total; i++) _ready[i] = new ManualResetEventSlim(false);
            }

            /// <summary>把能提交的都提交出去（受"在飞窗口"限制）；只在主线程调用</summary>
            private void SubmitAhead()
            {
                int window = Math.Max(1, Http.AdaptiveWorkers(_owner.Workers));
                // ★ 并发变化要让用户**看得见**。
                //   以前自适应是"悄悄发生"的：下载变慢了但日志里没有任何解释，
                //   用户只会觉得"今天怎么这么卡"。现在每次变化都记一行原因。
                if (window != _lastWindow)
                {
                    if (_lastWindow > 0 && _owner.Log != null)
                    {
                        try
                        {
                            _owner.Log(string.Format("并发自动调整：{0} → {1}（{2}）", _lastWindow, window,
                                window < _lastWindow
                                    ? "检测到站点限流，先降速；站点恢复后会自动升回"
                                    : "站点恢复正常，逐步提速"));
                        }
                        catch { }
                    }
                    _lastWindow = window;
                }
                while (!_stopped && _next < _total && Volatile.Read(ref _inFlight) < window)
                {
                    int idx = _next++;
                    var chapter = _owner.Chapters[idx];
                    Interlocked.Increment(ref _inFlight);
                    Task.Run(() =>
                    {
                        var o = new Outcome();
                        try { o.Result = _owner.FetchTextOnly(chapter); }
                        catch (Exception ex) { o.Error = ex; }
                        _outcomes[idx] = o;
                        Interlocked.Decrement(ref _inFlight);
                        try { _ready[idx].Set(); } catch { }
                    });
                }
            }

            /// <summary>等第 i 章的结果到齐，然后在主线程上按序记账、落盘。</summary>
            public void WaitAndCommit(int i)
            {
                SubmitAhead();
                while (!_ready[i].IsSet)
                {
                    if (_owner.IsCanceled())
                    {
                        _stopped = true;          // 取消：别再提交新的，也不等没到的了
                        return;
                    }
                    _ready[i].Wait(50);
                }
                var o = _outcomes[i];
                _owner.Commit(_owner.Chapters[i], o.Result, o.Error, false);
            }

            /// <summary>下载循环结束（或取消）时调用：不再提交新的抓取</summary>
            public void Stop()
            {
                _stopped = true;
            }
        }

        /// <summary>
        /// 收尾报告：把“缺了哪几章”单独写一个文件，比塞进表头可靠
        /// （表头是原地改写的，越写越长会盖到正文上）。
        /// </summary>
        /// <summary>跳过的章 + 原因（缺号摘要 / 报告里用）</summary>
        public Dictionary<string, string> SkipReasons = new Dictionary<string, string>();

        /// <summary>把一章的章节号收进"缺号"列表（解析不出来就跳过）</summary>
        private static void AddChapterNo(ChapterInfo c, List<int> into)
        {
            if (c == null || into == null) return;
            int n;
            if (ChapterNumber.TryParse(c.Title, out n)) into.Add(n);
        }

        public void WriteMissingReport()
        {
            if (SkippedChapters.Count == 0 && FailedChapters.Count == 0) { ReportFile = null; return; }
            // 报告只是“锦上添花”，缺任何前提条件都不该让整个下载流程炸掉
            if (Book == null || string.IsNullOrEmpty(BookDir)) { ReportFile = null; return; }
            try
            {
                ReportFile = Path.Combine(BookDir, Http.SafeFileName(Book.Title) + ".缺失章节.txt");
                var sb = new StringBuilder();
                sb.AppendLine("《" + Book.Title + "》缺失章节报告");
                sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章，共处理 {3} 章",
                    Ok, Skipped, Failed, Chapters.Count));
                sb.AppendLine(new string('=', 46));
                sb.AppendLine();
                sb.AppendLine("【失败 " + FailedChapters.Count + " 章】—— 网络/站点风控导致，重新勾选这几章再下一次通常就好了");
                int n = 0;
                foreach (var c in FailedChapters)
                {
                    n++;
                    string reason;
                    if (!FailReasons.TryGetValue(c.Id, out reason) || string.IsNullOrEmpty(reason)) reason = "未知";
                    sb.AppendLine(string.Format("{0}. 第 {1} 章 {2}", n, c.Order, c.Title));
                    sb.AppendLine("   原因：" + reason);
                    sb.AppendLine("   地址：" + ChapterUrl(c));
                }
                if (FailedChapters.Count == 0) sb.AppendLine("（无）");
                sb.AppendLine();
                sb.AppendLine("【跳过 " + SkippedChapters.Count + " 章】—— 站点这一章本身就是空的（“正在手打中，请稍等片刻”），");
                sb.AppendLine("不是工具的 bug，换别的站点/源也不会有内容。");
                n = 0;
                foreach (var c in SkippedChapters)
                {
                    n++;
                    sb.AppendLine(string.Format("{0}. 第 {1} 章 {2}", n, c.Order, c.Title));
                    string why;
                    if (SkipReasons.TryGetValue(c.Id, out why) && !string.IsNullOrEmpty(why))
                        sb.AppendLine("   原因：" + why);
                    sb.AppendLine("   地址：" + ChapterUrl(c));
                }
                if (SkippedChapters.Count == 0) sb.AppendLine("（无）");

                // ★ 缺号摘要：把"缺了哪几章"压缩成「12~15、40、88~89」。
                //   以前报告里只有"第 N 章 <标题>"，而那个 N 是**列表序号**不是章节号，
                //   用户根本看不出到底缺哪几章（尤其缺得多的时候）。
                var missingNos = new List<int>();
                foreach (var c in FailedChapters) AddChapterNo(c, missingNos);
                foreach (var c in SkippedChapters) AddChapterNo(c, missingNos);
                if (missingNos.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("【缺号摘要】" + ChapterNumber.DescribeRanges(missingNos));
                    sb.AppendLine("（按标题里的章节号解析；「楔子/番外/序」这类没有章号的不计入）");
                }
                File.WriteAllText(ReportFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log("写缺失章节报告失败：" + ex.Message);
                ReportFile = null;
            }
        }

        private string ChapterUrl(ChapterInfo c)
        {
            try
            {
                if (Book != null && Book.Site == "fanqie")
                    return "https://fanqienovel.com/reader/" + c.Id;
                var dir = Book == null ? "" : Book.Dir;
                if (string.IsNullOrEmpty(dir)) return "(无)";
                if (!dir.StartsWith("/")) dir = "/" + dir;
                if (!dir.EndsWith("/")) dir += "/";
                return "https://www.biquga.com" + dir + c.Id + ".html";
            }
            catch { return "(无)"; }
        }

        /// <summary>日志里也报一遍（取前 8 章，剩下的让用户看报告文件）</summary>
        public void LogMissingSummary()
        {
            if (FailedChapters.Count > 0)
            {
                Log(string.Format("失败章节（{0} 章，重新勾选这几章再下一次通常就好了）：", FailedChapters.Count));
                for (int i = 0; i < FailedChapters.Count && i < 8; i++)
                    Log(string.Format("  · 第 {0} 章 {1}", FailedChapters[i].Order, FailedChapters[i].Title));
                if (FailedChapters.Count > 8) Log(string.Format("  · …其余 {0} 章见报告文件", FailedChapters.Count - 8));
            }
            if (SkippedChapters.Count > 0)
            {
                Log(string.Format("跳过章节（{0} 章，站点侧本身就是空内容）：", SkippedChapters.Count));
                for (int i = 0; i < SkippedChapters.Count && i < 8; i++)
                    Log(string.Format("  · 第 {0} 章 {1}", SkippedChapters[i].Order, SkippedChapters[i].Title));
                if (SkippedChapters.Count > 8) Log(string.Format("  · …其余 {0} 章见报告文件", SkippedChapters.Count - 8));
            }
            if (!string.IsNullOrEmpty(ReportFile)) Log("缺失章节报告：" + ReportFile);
        }

        /// <summary>表头区预留的字节数（表头本身只有几百字节，留 2KB 足够宽裕）</summary>
        internal const int ReservedHeaderBytes = 2048;

        /// <summary>表头区标记：有了它，收尾时才知道这个文件预留了多少字节可以原地改写</summary>
        internal const string HeaderZoneMarker = "#header-zone:";

        /// <summary>
        /// 进度标记（写进表头）：记录"已经成功写入正文的章节数"和这些章节 id 的指纹。
        /// 增量更新靠它判断"哪些章是新的" —— 不用去解析文件名，也不怕标题被站点改过。
        /// 格式故意用 key:value，方便机器读，同时人类看也不刺眼。
        /// </summary>
        internal const string ProgressOkMarker = "#progress-ok:";

        /// <summary>
        /// 是否往已有文件**追加**（增量更新模式）。
        /// 这种模式下不重写表头（否则会把已有正文顶掉），只 append 新章节，
        /// 收尾再用 InjectHeaderStatistics 原位更新统计。
        /// </summary>
        /// <summary>输出文件编码（站点配置里可设为 gbk；默认 utf-8 带 BOM）</summary>
        public Encoding OutputEncoding = new UTF8Encoding(true);

        /// <summary>
        /// 输出繁体：把抓到的正文转成繁体再落盘/导出。
        /// 默认 false —— 站点正文绝大多数本来就是简体，只有港台读者需要开。
        /// </summary>
        public bool OutputTraditional = false;

        /// <summary>
        /// 按设置做繁简转换。转换表缺失（ZhConvert 不可用）时原样返回，
        /// 绝不因为转换问题让正文写不出去。
        /// </summary>
        private string ApplyScript(string text)
        {
            if (!OutputTraditional || string.IsNullOrEmpty(text)) return text;
            try { return ZhConvert.ToTraditional(text); }
            catch { return text; }
        }

        public bool AppendToExistingFile;

        /// <summary>本次是"更新已有文件"：表头统计里显示累计章数而不是本次新增数</summary>
        public int CumulativeOkCount = -1;

        /// <summary>章节 id 列表的短指纹（MD5 前 12 位十六进制）</summary>
        internal static string Fingerprint(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 从已有文件里读出进度：txt 里已经有多少章、指纹是什么、正文有多少字符。
        /// 读不到（旧版本文件、手工拼的文件）就返回 -1，调用方走"整本重新下载"。
        /// </summary>
        public static bool TryReadProgress(string path, out int okCount, out string fingerprint, out int bodyChars)
        {
            okCount = -1; fingerprint = null; bodyChars = -1;
            try
            {
                if (!File.Exists(path)) return false;
                var bytes = File.ReadAllBytes(path);
                int bom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                int scan = Math.Min(16384, bytes.Length - bom);
                if (scan <= 0) return false;
                var text = Encoding.ASCII.GetString(bytes, bom, scan);
                int i = text.IndexOf(ProgressOkMarker, StringComparison.Ordinal);
                if (i < 0) return false;
                int end = text.IndexOf('\n', i);
                var payload = text.Substring(i + ProgressOkMarker.Length,
                    (end < 0 ? text.Length : end) - i - ProgressOkMarker.Length).Trim();
                var parts = payload.Split(':');
                if (parts.Length < 2) return false;
                int ok;
                if (!int.TryParse(parts[0].Trim(), out ok) || ok < 0) return false;
                okCount = ok;
                fingerprint = parts[1].Trim();
                int bc;
                if (parts.Length >= 3 && int.TryParse(parts[2].Trim(), out bc)) bodyChars = bc;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把「表头 + 补白 + 标记行」拼成**正好 zoneBytes 字节**的一块。
        ///
        /// ★ 补白用**行尾空格**，不用换行。
        ///   起因（用户实测截图反馈"下载的txt前面不要空这么多行"）：
        ///   原来补白是 `new string('\n', pad)` —— 一个 58MB 的成品 txt 里，
        ///   前 300 行有 **293 行是空的**，打开文件第一眼就是一大片空白。
        ///   换成空格接在 46 个等号那一行的末尾：**字节数完全一样**
        ///   （正文偏移不变，「增量续写」「原位改表头」的所有不变量都保住），
        ///   但**不会产生空白行** —— 行尾空格在编辑器里看不见，也不影响阅读。
        ///
        /// 返回 null 表示"放不下"（调用方应保持原样，绝不越界写）。
        /// </summary>
        private static byte[] BuildHeaderZoneBytes(string head, int zoneBytes, Encoding enc)
        {
            if (head == null || enc == null) return null;
            string markerLine = HeaderZoneMarker + ReservedHeaderBytes + "\n";
            string headNoEol = head.TrimEnd('\r', '\n');
            string eol = head.Substring(headNoEol.Length);
            int fixedBytes = enc.GetByteCount(headNoEol) + enc.GetByteCount(eol)
                           + enc.GetByteCount(markerLine);
            int pad = zoneBytes - fixedBytes;
            if (pad < 0) return null;
            return enc.GetBytes(headNoEol + new string(' ', pad) + eol + markerLine);
        }

        /// <summary>表头 + 补白 + 表头区标记（正文从这之后才开始，所以正文永远不会被表头挤到）</summary>
        internal string BuildHeaderZone()
        {
            var head = BuildHeader();
            var enc = Encoding.UTF8;
            string markerLine = HeaderZoneMarker + ReservedHeaderBytes + "\n";
            // 标记要**自成一行**：否则增量更新时"往前找行首"会命中补白，
            // 把「下载时间 / 46 个等号」误当成补白填掉（真实 bug，等号行会消失）。
            string headNoEol = head.TrimEnd('\r', '\n');
            string headEol = head.Substring(headNoEol.Length);
            int fixedBytes = enc.GetByteCount(headNoEol) + enc.GetByteCount(headEol)
                           + enc.GetByteCount(markerLine);
            int pad = ReservedHeaderBytes - fixedBytes;
            if (pad < 0) pad = 0;
            // 补白是**行尾空格**（不是换行）—— 换行会在成品 txt 开头留下几百个空白行。
            return headNoEol + new string(' ', pad) + headEol + markerLine;
        }

        /// <summary>
        /// 增量落盘：第一次写表头 + **预留表头区**（带 UTF-8 BOM），之后只把新增的正文 append 上去。
        /// 预留表头区是为了让收尾时的“原位改表头”永远有地方写：
        /// 否则统计行一长（例如多出“缺失 N 章”那一行），新表头占的字节比旧的多，
        /// 就会盖掉正文开头几个字 —— 这是离线单测真实抓出来的 bug。
        ///
        /// AppendToExistingFile=true（更新已有文件）时跳过写表头：表头已经在那儿了，
        /// 重写一次会从文件开头覆盖，把已有正文顶掉。收尾用 InjectHeaderStatistics 更新统计。
        /// </summary>
        private void FlushIncremental()
        {
            if (_pending.Length == 0 && (_headerWritten || AppendToExistingFile)) return;
            if (!_headerWritten && !AppendToExistingFile)
            {
                using (var w = new StreamWriter(OutputFile, false, HeaderEncoding()))
                    w.Write(BuildHeaderZone());
                _headerWritten = true;
            }
            if (_pending.Length > 0)
            {
                var chunk = _pending.ToString();
                using (var w = new StreamWriter(OutputFile, true, AppendEncoding()))
                    w.Write(chunk);
                // 记下追加的字节数（更新模式算累计正文长度用；UTF-8 下等于字节数）
                _appendedBytes += Encoding.UTF8.GetByteCount(chunk);
                _pending.Length = 0;
            }
        }

        /// <summary>
        /// 在一段可显示区域里，把「本次下载：成功 …」那一行换成带进度标记的新统计，
        /// 用换行补齐到原来的长度。文件长度不变 → 不会顶掉后面的正文。
        ///
        /// 这是增量更新的收尾步骤：新章节 append 完之后，表头要反映"累计多少章"。
        /// 判据是 zoneLen（表头区大小，由 #header-zone: 标记给出），信息不全就不改。
        /// </summary>
        public static bool InjectHeaderStatistics(string path, BookInfo book, int cumulativeOk, int skipped, int failed,
            IList<ChapterInfo> chapters, int bodyChars)
        {
            // ============================================================
            //  做法：把「表头起点 → 正文起点」这一整块（表头 + 补白 + 标记行）
            //  **整个重建**后写回，总字节数严格守恒。
            //
            //  为什么重建而不是"只替换统计行"：表头里既有中文（字符≠字节）又有多行，
            //  只替换一行要做到"覆盖长度精确"很脆 —— 我已经在这上面栽了三次：
            //    1) 把 ASCII 解码后的字符下标当字节偏移用（覆盖长度算大 2 倍多）；
            //    2) "往后找行首"命中补白里的换行，把后面几行当补白填掉；
            //    3) 定长补齐与"放不下就放弃"互相打架，统计永远更新不了。
            //  重建法只有一个约束：新表头 + 补白 + 标记行 ≤ 原来的表头区字节数，否则整体放弃。
            // ============================================================
            try
            {
                if (!File.Exists(path)) return false;
                var bytes = File.ReadAllBytes(path);
                int bom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                int scan = Math.Min(16384, bytes.Length - bom);
                if (scan <= 0) return false;

                // 表头区总字节数 = 从（BOM 后的）文件开头到 #header-zone 标记那一行的行尾。
                // 标记行本身的字节数 = 标记 + 数字 + 换行（数字位数会变，所以**不能写死**，要量出来）。
                int markerAt = IndexOfBytes(bytes, bom, scan, Encoding.UTF8.GetBytes(HeaderZoneMarker));
                if (markerAt < 0) return false;
                int markerLineEnd = IndexOfByte(bytes, markerAt, scan - (markerAt - bom), (byte)'\n');
                if (markerLineEnd < 0) return false;
                int zoneBytes = markerLineEnd + 1 - bom;         // 要守恒的字节数
                int markerLineBytes = markerLineEnd + 1 - markerAt;

            var head = BuildHeaderFor(book, chapters, cumulativeOk, skipped, failed);
            // ★ 统一走 BuildHeaderZoneBytes：补白是**行尾空格**而不是换行。
            //   （原来这里也是写 '\n'，于是每次更新都会把几百个空白行重新填回去。）
            var block = BuildHeaderZoneBytes(head, zoneBytes, Encoding.UTF8);
            if (block == null) return false;                 // 放不下就整体放弃，绝不越界
            if (block.Length != zoneBytes) return false;     // 保险：字节数必须一模一样

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Position = bom;
                fs.Write(block, 0, block.Length);
            }
            return true;
            }
            catch { return false; }
        }
        /// <summary>在字节数组里从 to 往前找某个字节（用来定位"某一行的行首"）</summary>
        internal static int IndexOfByteBackward(byte[] haystack, int from, int to, byte value)
        {
            if (haystack == null) return -1;
            int lo = Math.Max(0, from);
            int hi = Math.Min(haystack.Length - 1, to);
            for (int i = hi; i >= lo; i--) if (haystack[i] == value) return i;
            return -1;
        }
        internal static int IndexOfByte(byte[] haystack, int from, int len, byte value)
        {
            if (haystack == null) return -1;
            int end = Math.Min(haystack.Length, from + len);
            for (int i = from; i < end; i++) if (haystack[i] == value) return i;
            return -1;
        }
        /// <summary>在字节数组的 [from, from+len) 区间里找一段字节序列（中文不能用字符下标定位）</summary>
        internal static int IndexOfBytes(byte[] haystack, int from, int len, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0) return -1;
            int end = from + len - needle.Length;
            for (int i = from; i <= end; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private void WriteWithHeader(string body)
        {
            // 整文件重写路径（自测用）：同样带上预留表头区，保证后续 FixHeaderNow 行为一致
            var tmp = OutputFile + ".tmp";
            using (var w = new StreamWriter(tmp, false, HeaderEncoding()))
            {
                w.Write(BuildHeaderZone());
                w.Write(body);
            }
            if (File.Exists(OutputFile)) File.Delete(OutputFile);
            File.Move(tmp, OutputFile);
        }

        /// <summary>
        /// 把开头的表头替换成准确统计，而不必把整本书（可能几 MB）重写一遍。
        /// 表头与正文的分界是那一行 46 个等号（纯 ASCII，所以可以按字节定位，
        /// 不受中文多字节影响）。
        ///
        /// 安全规则（两轮实测 + 离线单测逼出来的）：
        ///  · 新格式文件里有 "#header-zone:N" 标记，说明表头区预留了 N 字节 → 随便改，随便补白；
        ///  · 老格式文件（本工具 1.0 之前下载的、或手工拼的文件）没有标记 →
        ///    只在“新表头不比旧的占字节多”时才原位改写，否则宁可不改表头，
        ///    也绝不覆盖正文（正文比统计数字重要得多）。
        /// </summary>
        internal void FixHeaderNow()
        {
            const int HeaderMax = 16384;
            var bytes = File.ReadAllBytes(OutputFile);
            int bom = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            int scanLen = Math.Min(HeaderMax, bytes.Length - bom);
            if (scanLen <= 0) return;

            // 表头里中英文混排，但等号行、标记行都是 ASCII，按字节找位置是安全的
            var ascii = Encoding.ASCII.GetString(bytes, bom, scanLen);
            int sep = ascii.IndexOf(new string('=', 46), StringComparison.Ordinal);
            if (sep < 0) return;                                  // 没有表头（例如用户自己粘的 txt）→ 不动
            int end = ascii.IndexOf('\n', sep);
            if (end < 0) return;                                  // 表头被截断 → 不动，避免乱写
            int cutBytes = end + 1;

            int zone = 0;
            int mk = ascii.IndexOf(HeaderZoneMarker, StringComparison.Ordinal);
            if (mk > 0)
            {
                int eol = ascii.IndexOf('\n', mk);
                var num = ascii.Substring(mk + HeaderZoneMarker.Length,
                    (eol < 0 ? ascii.Length : eol) - mk - HeaderZoneMarker.Length).Trim();
                int.TryParse(num, out zone);
            }
            bool hasZone = zone >= cutBytes + 256;                // 标记可信（且确实比表头区大）

            var headerBytes = HeaderEncoding().GetBytes(BuildHeader());
            if (!hasZone && headerBytes.Length > cutBytes)
            {
                Log("注意：这是旧格式文件（表头区没有预留空间），新统计比原来长，"
                    + "为保证正文不被覆盖，本次不更新文件头统计。");
                return;
            }

            // ★ 补白用**行尾空格**，不用换行 —— 换行会让成品 txt 开头多出几百个空白行
            //   （用户实测反馈）。字节数照样精确填满到 `zone`，正文偏移因此永远不变。
            int zoneTarget = hasZone ? zone : cutBytes;
            var enc = HeaderEncoding();
            string head = BuildHeader();
            string headNoEol = head.TrimEnd('\r', '\n');
            string headEol = head.Substring(headNoEol.Length);
            int fixedBytes = enc.GetByteCount(headNoEol) + enc.GetByteCount(headEol);
            int pad = zoneTarget - fixedBytes;
            if (pad < 0)
            {
                Log("注意：新统计放不进表头区，本次不更新文件头统计（正文不受影响）。");
                return;
            }
            var block = enc.GetBytes(headNoEol + new string(' ', pad) + headEol);
            if (block.Length != zoneTarget) return;      // 保险：必须正好填满
            using (var fs = new FileStream(OutputFile, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Position = bom;
                fs.Write(block, 0, block.Length);
            }
        }

        public string BuildHeader()
        {
            return BuildHeader(Ok, Skipped, Failed);
        }

        /// <summary>按指定统计生成表头（测试和正式流程共用）</summary>
        /// <summary>
        /// 静态版表头构造（增量更新收尾时用：那时只有一个 DownloadRunner 实例，
        /// 但重建表头需要按"累计统计"而不是"本次统计"来写，所以单独抽一个能传统计的入口）。
        /// 与实例版 BuildHeader 共用同一份实现，避免两处写得不一致。
        /// </summary>
        internal static string BuildHeaderFor(BookInfo book, IList<ChapterInfo> chapters, int ok, int skipped, int failed)
        {
            var r = new DownloadRunner { Book = book };
            if (chapters != null) foreach (var c in chapters) r.Chapters.Add(c);
            return r.BuildHeader(ok, skipped, failed);
        }
        public string BuildHeader(int ok, int skipped, int failed)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Book.Title);
            sb.AppendLine("作者：" + (string.IsNullOrEmpty(Book.Author) ? "未知" : Book.Author));
            // 来源写**真实站点名**。以前是 `fanqie ? "番茄小说" : "笔趣阁"` 的二选一 ——
            // 站点一多，笔尖中文/笔趣阁365 都会被印成"笔趣阁"，产物里的出处就是错的。
            sb.AppendLine("来源：" + SiteRegistry.LabelOf(Book.Site) + "　" + Book.Url);
            if (!string.IsNullOrEmpty(Book.Desc))
            {
                var desc = Book.Desc.Replace("\n", " ");
                if (desc.Length > 300) desc = desc.Substring(0, 300) + "…";
                sb.AppendLine("简介：" + desc);
            }
            // 统计行里带上进度标记（累计章数 + 章节 id 指纹），供「更新已下载的书」判断哪些是新的
            sb.AppendLine(BuildStatisticsLine(ok, skipped, failed, Chapters));
            // 缺章时在表头补一句（只加一行、体积可控，正文不会被打乱；明细在“缺失章节.txt”）
            if (skipped + failed > 0)
                sb.AppendLine(string.Format("缺失 {0} 章（跳过 {1} + 失败 {2}），明细见同名「.缺失章节.txt」",
                    skipped + failed, skipped, failed));
            sb.AppendLine("下载时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 46));
            return sb.ToString();
        }


        /// <summary>
        /// 统计行文本（含进度标记、补齐到固定字节数）。表头初次生成、收尾更新、增量更新
        /// 三处都用它，保证格式唯一 —— 否则标记写法一变，老文件就读不出进度了。
        /// 读取端（TryReadProgress）按"标记 → 行尾"取值，末尾补的空格会被 Trim 掉。
        /// </summary>
        internal static string BuildStatisticsLine(int ok, int skipped, int failed, IList<ChapterInfo> chapters)
        {
            string fp = Fingerprint(IdsOf(chapters, ok));
            var line = string.Format("本次下载：成功 {0} 章，跳过 {1} 章，失败 {2} 章  {3}{0}:{4}",
                ok, skipped, failed, ProgressOkMarker, fp);
            // 不做定长补齐：表头区里「统计行 + 补白」的整体长度是守恒的，
            // 收尾注入时会把这段区域重新填满到 #header-zone 标记之前，
            // 所以统计行长短变化不会让标记行/正文起点漂移（见 InjectHeaderStatistics）。
            return line;
        }

        /// <summary>前 n 章的 id 拼起来（进度指纹用）</summary>
        private static string IdsOf(IList<ChapterInfo> chapters, int n)
        {
            if (chapters == null || n <= 0) return "";
            var sb = new StringBuilder();
            int end = Math.Min(n, chapters.Count);
            for (int i = 0; i < end; i++)
            {
                var c = chapters[i];
                if (c == null) continue;
                sb.Append(c.Id).Append('|');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 正文清洗：去掉站点插进正文的广告/导航行。
    ///
    /// ★ 最重要的一条原则：**宁可漏删广告，不可误删正文。**
    ///   两者代价完全不对等 ——
    ///   · 少删一行广告：用户一眼就看见，手动删掉即可；
    ///   · 误删/截断一行正文：用户**看不出来**，会以为是作者就这么写的。
    ///     这是**静默的数据损坏**，比广告严重得多。
    ///
    /// 历史教训（本类的前一版就是这么错的）：
    ///   「他还在为找不到回家的路而发愁。」→ 被切成「他」
    ///   「他能够一秒记住整页内容，过目不忘。」→ 整行被删
    /// 根因：当时只判「标记是否出现」，不判「这一段到底像不像广告」。
    /// 现在每条规则都多一道"它真的像广告吗"的第二判据。
    /// </summary>
    public static class TextCleaner
    {
        /// <summary>强标记：整行几乎不可能是正文（站点固定广告语）</summary>
        private static readonly string[] StrongMarkers = new[]
        {
            "送你一个现金红包", "请关闭浏览器阅读模式", "本站所有内容来源于互联网",
            "最新网址", "请记住本站", "一秒记住",
        };

        /// <summary>弱标记：正文里也可能出现（比如“求推荐票”），只有整行很短才当广告</summary>
        private static readonly string[] WeakMarkers = new[]
        {
            "推荐票", "月票", "加入书签", "手机版", "笔趣阁",
        };

        /// <summary>短行判定阈值：弱标记行超过这个长度就认为是正文</summary>
        private const int WeakMarkerMaxLen = 24;

        /// <summary>
        /// 移动版会把广告**直接拼在正文句子末尾**（不是独立行），例如：
        ///   “……他心中纳闷。还在为找不到的最新章节苦恼？安利一个公众号：rd444?…”
        /// 所以要先按标记把行尾那一段切掉，否则整行过滤会连正文一起丢掉。
        /// </summary>
        private static readonly string[] TrailingAdMarkers = new[]
        {
            "还在为找不到", "安利一个公众号", "不然搜不到哦",
            "关注公众号", "全网免费", "最新章节请", "请记住本书首发",
            // 移动版常见：正文句尾直接拼「有最新章节更新及时」
            // 注意**不能**加裸的「最新章节」—— 「想知道最新章节」是正常表达
            "有最新章节更新",
        };

        /// <summary>
        /// **专指度高的长标记**：这些词组几乎只可能是站点广告，出现即可切，
        /// 不必再判"后面那一段像不像广告"。
        ///
        /// 与 <see cref="TrailingAdMarkers"/> 分两档的原因：标记本身的歧义程度不同。
        ///   · 「一秒记住」「关注公众号」「最新网址」这类**短且会在正常句子里出现**
        ///     （"他能够一秒记住整页内容"），必须再看后文像不像广告；
        ///   · 「帮你找书陪你尬聊」「真人小姐姐在线服务」这种**一整个专属短语**，
        ///     正常小说里不会出现，直接切是安全的。
        /// 一刀切（全部都要第二判据）会漏掉后者的广告；全都不判则误杀前者。
        /// </summary>
        private static readonly string[] UnambiguousAdMarkers = new[]
        {
            "帮你找书", "陪你尬聊", "真人小姐姐在线服务", "小姐姐在线服务",
        };

        /// <summary>
        /// 广告特征词。切下来的那一段里必须有它，才认为"切对了"。
        /// 这是误杀修复的核心：光看"标记出现了"不够，还要看这一段像不像广告。
        /// </summary>
        private static readonly string[] AdWords = new[]
        {
            "公众号", "公众", "公·众", "公.众", "微信", "weixin", "wechat", "vx",
            "书友", "红包", "关注", "加群", "领取", "免费", "最新章节", "网址",
            "看热门", "福利", "点币", "抽奖",
        };

        /// <summary>「公众号」的各种变体写法（移动版故意插 . · 空格 来绕过滤）</summary>
        private static readonly System.Text.RegularExpressions.Regex GzhVariant =
            new System.Text.RegularExpressions.Regex(
                @"(关注|添加|搜索)?\s*(vx|v\.x|VX|微信|weixin|wechat|qq|QQ)?\s*[.·、]?\s*公\s*[.·]?\s*众\s*[.·]?\s*[号號]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 整行就是广告的形态（命中即整行丢弃）。
        ///
        /// ★ 每一条都必须**锚定词首**、且用足够长的固定搭配。
        ///
        ///   我第一版把这条写成 `^[★#\s]*【?\s*(领|送|抽|收集|看书福利)`，
        ///   结果从**用户已下载的真实 txt** 里删掉了这些正文：
        ///     「领取丹药宝物的族人们都吃惊的很，一名妇人牵着七岁女儿的手…」
        ///     「领域笼罩释放，让毒潭妖王仿佛陷入泥沼当中。」
        ///     「送君千里，终须一别。圣临山从山头到山脚没有千里…」
        ///     「送来资料的户部官员道：…」
        ///     「送了周泽揩下去后，司仪抹了抹那一脑门的汗。」
        ///   "领/送/抽" 是**小说正文里极常见的动词**（领取、领域、送君、送来、送了…），
        ///   拿它当词首判据等于随机删正文。
        ///
        ///   这是本次改动里最严重的一次自伤 —— 单测语料没覆盖到，
        ///   靠"把清洗器拿真实 txt 跑一遍"才抓出来。README 里那句
        ///   "任何清洗规则改动，先拿真实语料过一遍"就是这个意思。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex[] AdLinePatterns = new[]
        {
            // ，最快更新神级高手在都市最新章节！
            new System.Text.RegularExpressions.Regex(@"^[，,、]?\s*最快更新.{0,40}?最新章节",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            // 【领红包】… 【送红包】… 【收集免费好书】… 【看书福利】… 【书友福利】…
            // ★ 必须有【】—— 裸的"领取…""送了…"是正常正文。
            // ★ 也不能只认"领/送/抽"打头：【书友福利】【看书领现金】一样常见
            //   （实测从《超神宠兽店》漏出来 6 条，就是因为词首是"书友""看书"）。
            //   改成"方括号内前 12 字里出现广告关键词"。
            new System.Text.RegularExpressions.Regex(
                @"^[★#\s]*【[^】]{0,12}(红包|福利|好书|现金|点币|抽奖|领取|书友)",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            // #送888现金红包#… （# 包裹的话题式广告）
            new System.Text.RegularExpressions.Regex(@"^#\s*(送|领|抽)",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex(@"^本书由.{0,12}(整理|制作)",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex(@"^（?本章未完",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex(@"^交流好书",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex(@"^没钱看小说[？?]",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            new System.Text.RegularExpressions.Regex(@"^大家好，我们公众[.·]?号",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            // 站点招牌广告在**标点被剥掉之后**的样子（实测《超神宠兽店》有这种）：
            //   "大家好我们公众号每天都会发现金、点币红包只要关注就可以领取年末最后一次福利请大家抓住机会公众号"
            // 特征：整行没有句末标点 + 极短的行里塞满广告关键词。
            // ★ 判据用"关键词命中 ≥3 处"而不是"包含某个词" ——
            //   作者的话里也常出现"公众号""书友"，但不会在一行里堆三四个。
            new System.Text.RegularExpressions.Regex(
                @"^(?=(?:[^。！？…]*?(公众号|红包|点币|关注|领取|福利)){3})[^。！？…]{0,150}$",
                System.Text.RegularExpressions.RegexOptions.Compiled),
            // 整行就是站点招牌（这些词组本身不会出现在正常小说叙事里）
            new System.Text.RegularExpressions.Regex(@"^(请记住本站|最新网址[:：]|一秒记住|请关闭浏览器阅读模式)"),
        };

        /// <summary>
        /// 反爬水印：实体还原之后才现形的标签（站点把 &lt;span&gt; 写进正文）。
        ///
        /// ★ 要容忍残缺形态，实测见过三种：
        ///   · `&lt;span style='display:none'&gt;…&lt;/span&gt;` —— 正常
        ///   · `&lt;spanstyle='display:none'&gt;…`        —— 标签名和属性之间**没有空格**
        ///   · `&lt;span style='display:none'&gt;…&lt;span` —— 正文末尾被截断，收尾的 `&gt;` 丢了
        /// 所以：标签名后空白可有可无，末尾的 `&gt;` 也允许缺失（截断形态）。
        /// 仍然只认**白名单标签名**，不会碰到「x &lt; y」这种数学比较。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex RevealedTag =
            new System.Text.RegularExpressions.Regex(
                @"<\s*/?\s*(span|div|p|a|font|b|i|u|em|strong|br|script|style)\s*[^>]{0,400}>" +
                @"|<\s*/?\s*(span|div|p|a|font|b|i|u|em|strong|br|script|style)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Multiline
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>纯 base64 负载（整行只有 base64 字符且够长）—— 水印的第二种形态</summary>
        private static readonly System.Text.RegularExpressions.Regex Base64Blob =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9+/]{40,}={0,2}$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool ContainsAny(string s, string[] words)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var w in words)
                if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>行内有句末标点 → 更像正文（"那本书的笔趣阁版本比这个全。"）</summary>
        private static bool HasSentenceEnd(string t)
        {
            return t.IndexOf('。') >= 0 || t.IndexOf('！') >= 0 || t.IndexOf('？') >= 0
                || t.IndexOf('…') >= 0 || t.IndexOf('”') >= 0
                || t.IndexOf('!') >= 0 || t.IndexOf('?') >= 0;
        }

        private static bool HasUrlOrAdWord(string t)
        {
            if (t.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (t.IndexOf(".com", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (GzhVariant.IsMatch(t)) return true;
            return ContainsAny(t, AdWords);
        }

        /// <summary>
        /// 作者本人写在章节里的话。
        /// 《牧神记》里「关注下我的公众号"宅猪"」是**真的作者留言**，不是站点广告；
        /// 这类行不做整行丢弃，否则就是把作者的话删掉。
        /// </summary>
        private static bool IsAuthorNote(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            if (t.StartsWith("————", StringComparison.Ordinal)) return true;
            if (t.StartsWith("——", StringComparison.Ordinal)) return true;
            if (t.StartsWith("ps：", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("ps:", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("PS：", StringComparison.Ordinal)) return true;
            if (t.StartsWith("作者的话", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>每丢弃/截断一行就回调一次（默认 null = 不记录）。给"清洗审计"用。</summary>
        public static Action<string, string> OnDrop;

        private static void Note(string why, string line)
        {
            var h = OnDrop;
            if (h != null) { try { h(why, line); } catch { } }
        }

        public static string CleanBody(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var raw in s.Split('\n'))
            {
                var orig = raw.Trim();
                if (orig.Length == 0) continue;

                var t = StripWatermark(StripPageMark(StripTrailingAd(orig)));
                if (t.Length == 0) { Note("整行切成空", orig); continue; }
                if (IsAdLine(t)) { Note("命中广告行规则", orig); continue; }
                if (t != orig) Note("行尾广告被切", orig);
                sb.Append(t).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// 切掉行尾拼接的广告。
        ///
        /// ★ 判据分两档，不再"见到标记就切"：
        ///   · <see cref="UnambiguousAdMarkers"/>（专指长短语）→ 出现即切；
        ///   · <see cref="TrailingAdMarkers"/>（短、正常句里也可能有）→ 还要看
        ///     **标记之后那一段像不像广告**（行首则整行是广告）。
        /// 于是：
        ///   「……安利一个公众号：rd444」含"公众号" → 切 ✓
        ///   「他还在为找不到回家的路而发愁。」整段是正常话 → 不切 ✓
        /// 第三道兜底：切完不能让正文只剩两三个字，那多半是切错了。
        /// </summary>
        public static string StripTrailingAd(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;

            // 第一档：专指长短语，出现即切
            foreach (var m in UnambiguousAdMarkers)
                if (CutAt(ref line, m, true)) return line;

            // 第二档：短标记，要第二判据
            foreach (var m in TrailingAdMarkers)
                if (CutAt(ref line, m, false)) return line;

            return line;
        }

        /// <summary>
        /// 在标记处切断。返回 true 表示"这行已经处理完，可以直接返回"。
        /// </summary>
        private static bool CutAt(ref string line, string marker, bool unambiguous)
        {
            int i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return false;

            if (!unambiguous && !LooksLikeAppendedAd(line, i)) return false;

            var t = i > 0 ? line.Substring(0, i) : "";
            // 只收掉广告前那些"没写完"的标点；句号/感叹号/问号是句子正常结尾，必须留下
            t = t.TrimEnd(' ', '\u3000', '，', ',', '；', ';', '、', '：', ':');

            if (t.Length == 0) { line = ""; return true; }
            // 切完只剩两三个字 → 多半切错了，宁可留着这行广告
            if (t.Trim().Length < 4) return false;

            line = t;
            return true;
        }

        /// <summary>
        /// 判断"第 i 位这个标记"是不是**站点追加在正文后面的广告**，
        /// 而不是**正文本身正好用到了这个词**。
        ///
        /// 这一条是误杀修复的核心。三条判据，逐步收紧：
        ///
        ///   ① 标记在行首 → 整行就是广告；
        ///   ② 标记之后那一段带**高专指度**的广告特征（链接 / 公众号变体）。
        ///      ★ 刻意**不用宽泛的 AdWords**：它含「免费」「网址」「最新章节」这些正常词，
        ///      拿它判会把「别信什么全网免费的说法。」「他想看看最新章节请往下翻。」
        ///      这种正常句子切掉 —— 两条都是实测踩过的误杀。
        ///   ③ 标记之后**还剩成句的内容**且整行以句末标点收尾 → 是正文用词，别动。
        ///      广告是"贴"在句子末尾的，标记往往就在行尾；
        ///      而「她低声问道：你在关注公众号吗？」标记后面还有"吗？"、整行是正常问句。
        /// </summary>
        private static bool LooksLikeAppendedAd(string line, int i)
        {
            if (i == 0) return true;

            // ★ 核心判据：**标记之前的内容本身像不像一句说完了的话**。
            //
            //   正常句子：「她低声问道：你在关注公众号吗？」
            //             标记前是"她低声问道：你在" —— 既没有句末标点、也没有逗号断句，
            //             读不成一句完整的话 → 标记是句子的一部分，不能切。
            //   拼接广告：「他心中纳闷。还在为找不到的最新章节苦恼？安利一个公众号：rd444」
            //             「他心中纳闷，还在为找不到的最新章节苦恼」
            //             标记前分别是"他心中纳闷。"和"他心中纳闷，" —— 都成句
            //             → 后面全是站点贴上去的，切掉。
            //
            //   这条比"看标点落在哪"稳得多：它问的是
            //   「切完之后剩下的还是不是一句好话」，而那正是我们真正在意的事。
            var head = line.Substring(0, i);
            if (!HasSentenceEnd(head) && head.IndexOf('，') < 0 && head.IndexOf(',') < 0)
                return false;

            var tail = line.Substring(i);
            if (tail.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tail.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (tail.IndexOf(".com", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (GzhVariant.IsMatch(tail)) return true;
            // 「还在为找不到**最新章节**苦恼」这类专属广告语。
            // ★ 刻意只认这个具体搭配，**不认**裸的"最新章节" ——
            //   「他想看看最新章节请往下翻。」是正常句子，认了就会误杀。
            if (ContainsAny(tail, AdOnlyPhrases)) return true;

            // 没有上述特征时：标记基本就在行尾 → 当广告
            int after = line.Length - i - 1;
            if (after <= 1) return true;

            // 其余含糊情况：**宁可漏删广告**，放过
            return false;
        }

        /// <summary>
        /// **只可能出现在广告里**的搭配（不是单个词，是没法当正常表达用的短语）。
        /// 与宽泛的 <see cref="AdWords"/> 分开，就是为了避免误杀正文。
        /// </summary>
        private static readonly string[] AdOnlyPhrases = new[]
        {
            "最新章节苦恼", "最新章节请", "最新网址", "一秒记住", "请记住本站",
        };

        /// <summary>
        /// 站点自己的页码残留，形如 --1120dmabgioie1777198--&gt;
        /// （移动版偶尔会在正文里插这种标记，必须清掉）
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex PageMark =
            new System.Text.RegularExpressions.Regex(@"--\d{2,6}[a-z]{6,16}\d{4,12}-->",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string StripPageMark(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf("--", StringComparison.Ordinal) < 0) return line;
            var t = PageMark.Replace(line, "");
            // 整行只有这个标记 → 丢弃
            if (t.Trim().Length == 0) return "";
            return t;
        }

        /// <summary>
        /// 清掉反爬水印。
        ///
        /// 站点把水印写成**转义后的标签**（`&amp;lt;span …&amp;gt;`）+ 一段 base64 塞进正文。
        /// 原来的顺序是「先去标签 → 最后才 HtmlDecode」，于是转义写法在去标签那一步
        /// 还是个 `&amp;lt;span&amp;gt;` 文本、没被删；等实体还原之后它才变成真标签，
        /// 而那时已经没有人再扫一遍了 —— 水印就留在正文里。
        ///
        /// 两道护栏（防止误伤正常正文）：
        ///   · 正则只认**白名单标签名**（span/div/p/a/…），不是 `&lt;\S+&gt;` 通吃，
        ///     所以「他算出了 x &lt; y 的结果。」这种不会碰到这些词；
        ///   · base64 判定要求**整行都是** base64 字符且 ≥ 40 位，
        ///     正常中文句子永远不满足（中文不是 base64 字符集）。
        /// </summary>
        public static string StripWatermark(string line)
        {
            if (string.IsNullOrEmpty(line)) return line;
            var t = line;
            if (t.IndexOf('<') >= 0) t = RevealedTag.Replace(t, "");
            t = t.Trim();
            if (t.Length == 0) return "";
            if (Base64Blob.IsMatch(t)) return "";
            return t;
        }

        /// <summary>这一行是不是"整行都是广告"</summary>
        public static bool IsAdLine(string t)
        {
            if (string.IsNullOrEmpty(t)) return true;

            // 作者的话豁免：那是作者写的，删掉就是毁书
            if (IsAuthorNote(t)) return false;

            // 整行形态匹配（最快最准的一类）
            foreach (var re in AdLinePatterns)
                if (re.IsMatch(t)) return true;

            // 强标记：也要看整行像不像广告 —— 标记在行首、或**行里带明确的广告特征**
            foreach (var m in StrongMarkers)
            {
                int i = t.IndexOf(m, StringComparison.Ordinal);
                if (i < 0) continue;
                if (i == 0) return true;

                // 标记在行中：只有"整行没有任何句末标点 + 含链接或公众号变体"才算广告。
                // ★ 这里刻意**不用宽泛的 AdWords**（它含"网址""最新章节"这些正常词）——
                //   否则「他查到了最新网址，记在了本子上。」「他想看看最新章节请往下翻。」
                //   这种正常句子会被整行删掉。这两条都是实测踩过的误杀。
                if (!HasSentenceEnd(t) &&
                    (t.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.IndexOf(".com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     GzhVariant.IsMatch(t)))
                    return true;
                // 其余情况当正文（「他能够一秒记住整页内容，过目不忘。」就是这种）
            }

            // 「最新网址」这类站点暗号：长行 + 无句末标点 + 含网址特征，才算广告
            if (t.Length > WeakMarkerMaxLen && !HasSentenceEnd(t) &&
                t.IndexOf("最新网址", StringComparison.Ordinal) >= 0)
                return true;

            // 「公众号」变体：整行短、且没有句末标点，才算广告
            if (t.Length <= WeakMarkerMaxLen && !HasSentenceEnd(t) && GzhVariant.IsMatch(t))
                return true;

            // 弱标记：短行 + 没有句末标点 + 标记占比够大，三条同时成立才算广告。
            //
            // ★ 判据是"标记占整行的比例"，不是"标记是否出现"。
            //   「求推荐票」  3/4 = 75% ✓ 广告
            //   「投月票支持作者」2/8 = 25% ✗ 按"宁可漏删"原则放过
            //   「我要给你投月票」2/7 = 29% ✗ 放过 —— 这本来就是正常句子
            //
            // 后两条我**故意**判成正文：它们读起来就是正常小说句子，
            // 而"宁可漏删广告，不可误删正文"是这个类的最高原则。
            // 漏掉一行"投月票支持作者"，用户看得见、手动删一下就好；
            // 删掉一行正文，用户永远不知道自己少了字。
            if (t.Length <= WeakMarkerMaxLen && !HasSentenceEnd(t))
            {
                foreach (var m in WeakMarkers)
                {
                    int i = t.IndexOf(m, StringComparison.Ordinal);
                    if (i < 0) continue;
                    if (m.Length * 3 >= t.Length) return true;
                }
            }
            return false;
        }
    }
}
