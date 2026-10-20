using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TomatoBiquga
{
    /// <summary>
    /// 笔趣阁（biquga.com）站点实现。
    /// 站点自己的目录页 index_N/dindex_N 是坏的（会串到别的书），
    /// 所以这里顺着每章页面里的“下一章”链接（var kkehvov）走完整本。
    /// 正文是 base64 存在 document.writeln(qsbs.bb('...')) 里。
    /// </summary>
    public class BiqugaSite : ISite, ITextCacheProvider, IProbeable, IChapterUrlProvider
    {
        public const string Origin = "https://www.biquga.com";
        public string Name { get { return "笔趣阁"; } }

        /// <summary>探活：抓一次站点首页（域名/DNS/TLS 这一层通就算可用）</summary>
        public string Probe()
        {
            var r = NetDiag.Probe(Origin + "/", Origin + "/");
            return r.Ok ? null : NetDiag.Describe(r);
        }

        /// <summary>目录遍历时顺路抓到的正文：key -> 文本</summary>
        private readonly Dictionary<string, string> _parsedText = new Dictionary<string, string>();
        /// <summary>章节正文缓存：cid -> 文本</summary>
        private readonly Dictionary<string, string> _textCache = new Dictionary<string, string>();
        private readonly object _lock = new object();

        // ★ 这里原来有一个 `public int CrawlWorkers = 6;`，已删除。
        //   它被 MainForm 赋了 4 次值，却**全项目没有任何地方读它** ——
        //   是个骗人的旋钮：用户在「设置」里把"PC并发"从 6 调到 32，
        //   目录遍历一点都不会变快（docs/架构.md 还照着它写了
        //   "PC 版目录遍历用 CrawlWorkers"，描述的是一段不存在的行为）。
        //
        //   为什么不改成真的并发：**结构上做不到**。目录遍历是"反向链条"——
        //   第 N-1 页的地址只写在**第 N 页的 HTML** 里（`var uiiekp0do`），
        //   必须先取到第 N 页才知道下一页取什么，天然串行。
        //   （项目里也验证过"按 id 递增规律预测"，命中率极低、反而更慢。）
        //
        //   真正快的做法是**换源**：笔尖中文的目录一页给全 1300+ 章、
        //   实测 0.65 秒，比走 772 页串行链快三个数量级。见 SiteRules.cs。

        /// <summary>为 true 时忽略本地目录缓存，强制重新遍历站点</summary>
        public bool ForceRefresh = false;

        /// <summary>
        /// 当前正在遍历/载入的书目录（如 /10_10333）。
        /// 给界面判断"这本书有没有遍历断点"用 —— 断点是按书目录存的。
        /// </summary>
        public string CurrentDir = "";

        /// <summary>
        /// 离线模式：目录遍历时把整本正文留在内存，下载阶段完全不联网。
        /// 代价是内存（一本 300 万字约 6MB）和“目录缓存不再生效”（每次都要真遍历）。
        /// </summary>
        public bool Offline = false;

        // ---------------------------------------------------------- 搜索

        public List<BookInfo> Search(string keyword, Action<string> log)
        {
            var result = new List<BookInfo>();
            var html = Http.Post(Origin + "/search.html", "s=" + Uri.EscapeDataString(keyword), Origin + "/");
            foreach (Match m in Regex.Matches(html, @"<li>([\s\S]*?)</li>"))
            {
                var chunk = m.Groups[1].Value;
                var link = Regex.Match(chunk, "<span class=\"s2\"><a href=\"([^\"]+)\"[^>]*>([\\s\\S]*?)</a>");
                if (!link.Success) continue;
                var dir = link.Groups[1].Value.TrimEnd('/');
                if (!Regex.IsMatch(dir, @"^/\d+_\d+$")) continue;
                var author = Regex.Match(chunk, "<span class=\"s3\"><a[^>]*>([\\s\\S]*?)</a>");
                var cat = Regex.Match(chunk, "<span class=\"s1\">\\[([\\s\\S]*?)\\]</span>");
                var latest = Regex.Match(chunk, "<span class=\"s4\"><a[^>]*>([\\s\\S]*?)</a>");
                result.Add(new BookInfo
                {
                    Site = "biquga",
                    Title = Http.StripTags(link.Groups[2].Value).Trim(),
                    Author = author.Success ? Http.StripTags(author.Groups[1].Value).Trim() : "",
                    Category = cat.Success ? Http.StripTags(cat.Groups[1].Value).Trim() : "",
                    Desc = latest.Success ? "最新：" + Http.StripTags(latest.Groups[1].Value).Trim() : "",
                    Dir = dir,
                    Url = Origin + dir + "/",
                });
            }
            if (log != null) log(string.Format("搜索“{0}”得到 {1} 条结果", keyword, result.Count));
            return result;
        }

        // ---------------------------------------------------------- 目录

        /// <summary>
        /// 为 true 时**跳过读取**目录缓存（但照常写缓存）。
        ///
        /// 和 ForceRefresh 的区别：ForceRefresh 是"用户点了刷新目录"，
        /// 语义是"我要最新的"；SuppressDirCache 是"我要测真实速度"——
        /// 把缓存命中当成"这个站很快"会让速度排名完全失真。
        /// 「自动选最快」用它来保证测的是真实遍历成本。
        /// </summary>
        public bool SuppressDirCache = false;

        public BookInfo LoadBook(BookInfo item, Action<string> log)
        {
            // 0) 目录缓存：命中就免去几分钟的遍历（离线模式下必须真遍历，否则拿不到正文）
            var cacheKey = (item.Dir ?? "").Replace("/", "");
            if (!ForceRefresh && !SuppressDirCache && !Offline)
            {
                var cached = DirCache.Load("biquga", cacheKey);
                if (cached != null)
                {
                    var at = DirCache.CachedAt("biquga", cacheKey);
                    var age = at.HasValue ? (DateTime.Now - at.Value).TotalHours : 999;
                    if (age <= DirCache.MaxAgeHours)
                    {
                        if (log != null) log(string.Format("使用本地目录缓存（缓存于 {0}，共 {1} 章，跳过站点遍历）",
                            at.HasValue ? at.Value.ToString("MM-dd HH:mm") : "未知", cached.Chapters.Count));
                        return cached;
                    }
                    if (log != null) log("本地目录缓存已过期（超过 " + DirCache.MaxAgeHours + " 小时），重新遍历站点…");
                }
            }

            var book = new BookInfo
            {
                Site = "biquga",
                Dir = item.Dir,
                Url = Origin + item.Dir + "/",
                Title = item.Title,
                Author = item.Author,
                Category = item.Category,
            };

            if (log != null) log("读取书籍信息…");
            var html = Http.Get(book.Url, Origin + "/");
            book.Title = FirstGroup(html, "og:novel:book_name\" content=\"([^\"]*)\"", book.Title);
            book.Author = FirstGroup(html, "og:novel:author\" content=\"([^\"]*)\"", book.Author);
            book.Category = FirstGroup(html, "og:novel:category\" content=\"([^\"]*)\"", book.Category);
            book.Status = FirstGroup(html, "og:novel:status\" content=\"([^\"]*)\"", "");
            book.Desc = CleanDesc(Http.StripTags(FirstGroup(html, "og:description\" content=\"([^\"]*)\"", "")));
            if (string.IsNullOrEmpty(book.Desc))
                book.Desc = CleanDesc(Http.StripTags(FirstGroup(html, "<div class=\"desc[^\"]*\">([\\s\\S]*?)</div>", "")));

            // 封面：og:image 优先，退回详情页第一张 book 图（PC 版常见 /files/article/image/…）
            var cover = FirstGroup(html, "og:image\" content=\"([^\"]*)\"", "");
            if (string.IsNullOrEmpty(cover))
                cover = FirstGroup(html, "og:novel:image\" content=\"([^\"]*)\"", "");
            if (string.IsNullOrEmpty(cover))
                cover = FirstGroup(html, "<img[^>]+(?:data-src|src)=\"([^\"]*(?:cover|image|files)[^\"]*)\"", "");
            if (!string.IsNullOrEmpty(cover))
                book.CoverUrl = CoverFetcher.Absolutize(cover, book.Url);

            // 起始点 = 最后一章：详情页的章节列表里，最新的一章排在最前面。
            // 站点详情页只列最新/最早各 100 章，这里取“最后一章”，然后顺着 prev 往回走。
            var seq = new List<string>();
            foreach (Match m in Regex.Matches(html, "href=\"(" + Regex.Escape(item.Dir) + "/(\\d+)\\.html)\""))
            {
                var cid = m.Groups[2].Value;
                if (!seq.Contains(cid)) seq.Add(cid);
            }
            if (seq.Count == 0) throw new Exception("详情页里没找到任何章节链接，站点结构可能变了");

            // 详情页顺序是“最新章节在前、然后章节列表”，取 id 最大的那个作为最后一章
            string lastCid = null;
            long maxId = -1;
            foreach (var cid in seq)
            {
                long v;
                if (long.TryParse(cid, out v) && v > maxId) { maxId = v; lastCid = cid; }
            }
            if (lastCid == null) lastCid = seq[seq.Count - 1];

            CurrentDir = book.Dir;      // 给界面判断"这本书有没有断点"
            if (log != null) log("《" + book.Title + "》 " + book.Author + " —— 从最后一章往回遍历目录（顺“上一页”链，一章不漏，全书约 1~2 分钟）…");

            book.Chapters = CrawlChapters(book.Dir, lastCid, log);            if (book.Chapters.Count == 0) throw new Exception("没抓到任何章节，站点结构可能变了");
            if (log != null) log(string.Format("目录完成：共 {0} 章（{1} → {2}）", book.Chapters.Count,
                book.Chapters[0].Title, book.Chapters[book.Chapters.Count - 1].Title));

            // 离线模式：把遍历时抢到的正文全部留进内存，下载阶段就完全不联网了
            if (Offline)
            {
                PromoteAllTextToCache();
                int withText = 0;
                lock (_lock) withText = _textCache.Count;
                if (log != null) log(string.Format("离线模式：已把 {0} 章的正文留在内存里，点下载会直接写文件、不再请求站点。", withText));
            }

            DirCache.Save(book);
            if (log != null) log("目录已缓存到本地，下次载入这本书会立刻打开。");
            return book;
        }

        /// <summary>
        /// 抓取完整目录。
        ///
        /// 站点的“下一章”链接(var kkehvov)是不可靠的：同一页面多次请求会给出不同的 next
        /// （实测 10032109 的第 3 页，next 有时指第 4 页、有时指第 1 页），
        /// 顺着 next 走整本会成环、漏章（实测：只凑出 495 章，而实际有 772 章）。
        ///
        /// 而“上一页”链接(var uiiekp0do)完全可靠：从最后一章一路 prev 走回目录页。
        /// 实测《沧元图》走完是 772 页 / 774 章（页数含章内分页，所以比章数略少）。
        /// 注意**页数因书而异**，不要在代码里假设某个固定值 —— 这里写出来只是留个量级参考。
        /// 章节 id 单调递增，所以最后按 id 升序排就是正确阅读顺序。
        /// </summary>
        private List<ChapterInfo> CrawlChapters(string dir, string firstCid, Action<string> log)
        {
            var byCid = new Dictionary<string, string>();     // cid -> 标题
            var parsed = new Dictionary<string, string>();    // key -> 正文
            var order = new List<string>();                   // 按发现顺序（倒序）
            var visited = new HashSet<string>();

            string key = firstCid;      // 起始 = 最后一章
            int pages = 0, failures = 0;

            // ---- 断点续爬：有上次的断点就从断点接着走 ----
            // 链是确定的（每页的 prev 唯一指向前一页），所以"从某页接着走"等价于
            // "从最后一章一路走到那一页"。这样两小时的遍历中断后不用整本重来。
            var resume = Resume ? CrawlResume.Load(SiteKey, dir, firstCid) : null;
            if (resume != null && resume.Ids.Count > 0)
            {
                for (int i = 0; i < resume.Ids.Count; i++)
                {
                    var id = resume.Ids[i];
                    if (string.IsNullOrEmpty(id) || byCid.ContainsKey(id)) continue;
                    byCid[id] = i < resume.Titles.Count ? resume.Titles[i] : "";
                    order.Add(id);
                }
                key = resume.NextKey;
                pages = resume.Pages;
                if (log != null)
                    log(string.Format("  从上次的断点继续（已收录 {0} 章 / 走过 {1} 页，断点保存于 {2}）",
                        byCid.Count, resume.Pages,
                        resume.SavedAt == DateTime.MinValue ? "未知" : resume.SavedAt.ToString("MM-dd HH:mm")));
            }
            else if (log != null && Resume)
            {
                log("  没有可用的遍历断点（或断点不属于这本书），从最后一章开始遍历。");
            }

            int sinceCheckpoint = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 进度日志单独计时：sinceCheckpoint 会被落盘逻辑提前清零，不能拿来算速度
            var logSw = System.Diagnostics.Stopwatch.StartNew();
            int lastLogPages = 0;

            while (!string.IsNullOrEmpty(key) && pages < 20000)
            {
                if (visited.Contains(key)) break;
                visited.Add(key);
                pages++;
                sinceCheckpoint++;

                string html = null;
                for (int attempt = 0; attempt < 3 && html == null; attempt++)
                {
                    try { html = Http.Get(UrlOf(dir, key), Origin + dir + "/"); }
                    catch (Exception ex)
                    {
                        if (attempt == 2 && log != null) log("  第 " + key + " 页抓取失败：" + ex.Message);
                    }
                }
                if (html == null) { failures++; if (failures > 20) break; continue; }

                var p = ParseChapterHtml(html);
                bool isHead = key.IndexOf('_') < 0;
                if (isHead)
                {
                    if (!byCid.ContainsKey(key)) { byCid[key] = p.Title; order.Add(key); }
                }
                if (!string.IsNullOrEmpty(p.Text)) parsed[key] = p.Text;

                // 上一页：到目录页就结束
                var prevPath = Regex.Match(html, "var uiiekp0do='([^']+)'");
                if (!prevPath.Success || !Regex.IsMatch(prevPath.Groups[1].Value, @"^/\d+_\d+/\d+"))
                {
                    if (log != null) log("  已回到目录页，全书遍历结束");
                    break;
                }
                var pk = ParseNextPath(prevPath.Groups[1].Value);   // 复用解析（同样支持 _N 分页）
                if (pk == null) break;
                key = KeyOf(pk.Item1, pk.Item2);

                if (log != null && pages % 50 == 0)
                {
                    // ★ 不要在这里写"共约 X 页"。这个遍历是**从最后一章往回走**的，
                    //   在走回目录页之前**根本不可能知道总共有多少页**。
                    //   上一版这里写死了 772（我测《沧元图》时看到的页数），
                    //   结果别的书跑起来就是"已收录 800 章（共约 772 页待走）"——
                    //   数字自己打自己，用户当然会怀疑。
                    //   现在如实报三件**真实测得**的事：收录了多少章、实际走过多少页、
                    //   最近这段的速度。总页数等走到目录页那一刻才知道。
                    //
                    //   速度用**本次日志间隔**单独计时，不能复用 sinceCheckpoint ——
                    //   那个变量会被"页数或 45 秒"的落盘逻辑提前清零，算出来是错的。
                    double logMins = logSw.Elapsed.TotalMinutes;
                    string speed = "";
                    if (logMins >= 0.5)
                        speed = string.Format("，约 {0:F0} 页/分", (pages - lastLogPages) / logMins);
                    log(string.Format("  目录遍历中：已收录 {0} 章（走过 {1} 页{2}）",
                        byCid.Count, pages, speed));
                    logSw.Restart();
                    lastLogPages = pages;
                }
                if (pages % 3 == 0) Http.Polite();

                // 定期落一次断点。间隔按"页数 + 时间"双条件：
                // 页数保证不会太频繁写盘，时间保证**慢网络下也不会太久不落**
                // （站点限速时 50 页可能要十几分钟，那就跨得太久了）。
                if (sinceCheckpoint >= 50 || (sinceCheckpoint > 0 && sw.Elapsed.TotalSeconds >= 45))
                {
                    sinceCheckpoint = 0;
                    sw.Restart();
                    SaveCheckpoint(dir, firstCid, key, pages, byCid, order);
                }
            }

            // 章节 id 单调递增 → 按 id 升序即阅读顺序
            var ids = new List<string>(byCid.Keys);            ids.Sort(delegate (string a, string b)
            {
                long x, y;
                bool ax = long.TryParse(a, out x), by = long.TryParse(b, out y);
                if (ax && by) return x.CompareTo(y);
                return string.CompareOrdinal(a, b);
            });

            var list = new List<ChapterInfo>();
            int idx = 0;
            foreach (var cid in ids)
            {
                list.Add(new ChapterInfo
                {
                    Id = cid,
                    Title = string.IsNullOrEmpty(byCid[cid]) ? ("第 " + (idx + 1) + " 章") : byCid[cid],
                    Order = idx++,
                });
            }

            // 遍历时顺路抓到的正文，下载阶段直接复用（脚本里的分页正文按 key 分别存，这里合并）
            _parsedText.Clear();
            foreach (var cid in ids)
            {
                var parts = new List<string>();
                string t;
                if (parsed.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) parts.Add(t);
                for (int page = 2; page <= 30; page++)
                {
                    if (parsed.TryGetValue(cid + "_" + page, out t) && !string.IsNullOrEmpty(t)) parts.Add(t);
                    else break;
                }
                if (parts.Count > 0)
                {
                    var joined = string.Join("\n\n", parts).Trim();
                    if (joined.Length >= 15) _parsedText[cid] = joined;
                }
            }

            if (log != null && failures > 0) log("  期间有 " + failures + " 页抓取失败（已跳过）");

            // 遍历正常走到底 → 断点没用了，删掉。
            // 不删的话下次载入会"续"到一份已经走完的断点，白跑一趟。
            CrawlResume.Clear(SiteKey, dir);

            return list;
        }

        /// <summary>
        /// 为 true 时，载入目录会先尝试从上次的遍历断点继续。
        /// 默认 false：**不改变原有行为**，由界面上的「继续遍历」按钮显式打开。
        /// </summary>
        public bool Resume = false;

        /// <summary>断点/目录缓存用的站点 key</summary>
        protected virtual string SiteKey { get { return "biquga"; } }

        /// <summary>
        /// 落一次遍历断点。正文**不**写进断点（体积太大），
        /// 所以续爬后重新走的那段只为拿到更靠前的章节，正文在下载阶段按需再抓。
        /// </summary>
        private void SaveCheckpoint(string dir, string firstCid, string nextKey, int pages,
            Dictionary<string, string> byCid, List<string> order)
        {
            try
            {
                if (string.IsNullOrEmpty(nextKey)) return;
                var s = new CrawlResume.State
                {
                    Site = SiteKey,
                    BookDir = dir,
                    StartKey = firstCid,
                    NextKey = nextKey,
                    Pages = pages,
                };
                // order 是"发现顺序"（从新到旧），断点里原样保留；
                // 续爬时按同样顺序塞回 byCid/order，最终仍按 id 排序，所以顺序不影响正确性
                foreach (var cid in order)
                {
                    s.Ids.Add(cid);
                    string t;
                    s.Titles.Add(byCid.TryGetValue(cid, out t) ? (t ?? "") : "");
                }
                CrawlResume.Save(s);
            }
            catch { /* 落断点失败不能影响遍历本身 */ }
        }

        // ---------------------------------------------------------- 正文

        public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
        {
            lock (_lock)
            {
                string cached;
                if (_textCache.TryGetValue(chapter.Id, out cached) && !string.IsNullOrEmpty(cached)) return cached;
                if (_parsedText.TryGetValue(chapter.Id, out cached) && !string.IsNullOrEmpty(cached))
                {
                    _textCache[chapter.Id] = cached;
                    return cached;
                }
            }
            if (Offline) return null;   // 离线模式：没有缓存就不发请求
            var text = FetchChapterText(book.Dir, chapter.Id, book.Url);
            if (!string.IsNullOrEmpty(text)) lock (_lock) _textCache[chapter.Id] = text;
            return text;
        }

        /// <summary>
        /// 取“目录遍历时顺路抓到的正文”，不发新请求。
        /// 从本地缓存载入目录时没有这份数据，返回 null，由调用方走网络。
        /// </summary>
        public string GetCachedText(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return null;
            lock (_lock)
            {
                string t;
                if (_textCache.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) return t;
                if (_parsedText.TryGetValue(cid, out t) && !string.IsNullOrEmpty(t)) return t;
            }
            return null;
        }

        /// <summary>把整本书的正文都放进内存缓存（用于“离线模式”）</summary>
        internal void PromoteAllTextToCache()
        {
            lock (_lock)
            {
                foreach (var kv in _parsedText)
                    if (!string.IsNullOrEmpty(kv.Value)) _textCache[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// 取一整章的正文（把 `cid.html` / `cid_1.html` / `cid_2.html`… 拼起来）。
        ///
        /// ============================================================
        /// 为什么这里值得优化（实测）
        /// ============================================================
        /// 这些章**本身就是多页的**，而且页数不少。实测某一章：
        ///     /10_10333/37684582.html    → 879 字符
        ///     /10_10333/37684582_1.html  → 818 字符
        ///     /10_10333/37684582_2.html  → 793 字符
        /// 合计 2490 ≈ 逐章拿到的 2494。
        /// 也就是说**下载慢的主因是"页数"，不是连接握手** ——
        /// 我一开始误判成 TLS 握手开销，方向错了。
        ///
        /// ============================================================
        /// 做法：先串行取第 1 页（为了知道有几页、且页号未必连续），
        ///       再把所有续页**并发抓回来**，最后按页号拼。
        /// ============================================================
        /// 为什么这样是安全的：
        ///   · 续页之间**没有依赖** —— 页 1 只用来回答"后面还有哪些页"，
        ///     和目录遍历那种"下一页地址写在当前页里"的环形依赖完全不同；
        ///   · 拼接顺序仍由**页号**决定，不依赖网络返回顺序；
        ///   · 并发只发生在**同一章内部**（最多几个页），
        ///     不像"整本书一起并发"那样把站点打爆；
        ///   · 拿不到某一页就**停下来、不越过去**（保守，宁可少拼也不乱拼）。
        ///
        /// ★ 改这个方法时务必先跑 tools/PgSample.cs 存基线、改完再逐字比对 ——
        ///   拼接一旦错位，正文会被**静默截断**，用户不会立刻发现。
        /// </summary>
        private string FetchChapterText(string dir, string cid, string referer)
        {
            // ---- 第 1 页（串行：它是"有几页"的唯一依据）----
            var firstUrl = string.Format("{0}{1}/{2}.html", Origin, dir, cid);
            var firstHtml = Http.Get(firstUrl, referer);
            var parts = new List<string>();
            var firstText = ParseChapterHtml(firstHtml).Text;
            if (!string.IsNullOrEmpty(firstText)) parts.Add(firstText);

            int p1 = NextPageNumberAt(firstHtml, cid, 1);
            if (p1 == 0) return string.Join("\n\n", parts).Trim();

            // ---- 续页并发抓 ----
            var nums = new List<int>();
            var urls = new List<string>();
            for (int page = p1; page <= 30 && nums.Count < 8; page++)
            {
                nums.Add(page);
                urls.Add(string.Format("{0}{1}/{2}_{3}.html", Origin, dir, cid, page));
            }

            // ---- 续页：**串行**逐页取 ----
            //
            // ★ 这里试过并发，**并发更慢**，所以退回串行。实测（同样 12 章、三轮交替）：
            //     续页串行   60.6s / 70.4s / 52.0s
            //     续页并发   79.3s / 75.9s / 84.2s   → 慢 1.31x / 1.08x / 1.62x
            //   也试过用 HttpBatch 把续页塞进一个 curl 进程：
            //     Http.Get 单页 0.58s，而 HttpBatch 取 3 页要 3.20s（1.07s/页）——
            //     多付的 curl 进程启动 + 临时文件开销，抵不过它省下的握手时间。
            //
            //   结论：**这个站点对并发请求不友好**（并发反而更慢）。
            //   续页就老老实实一页一页取，这也是唯一被实测支持的做法。
            bool advanced = false;
            for (int i = 0; i < nums.Count; i++)
            {
                string html = null;
                try { html = Http.Get(urls[i], referer); } catch { }
                if (html == null) break;      // 拿不到就停，不跳过这一页

                var t = ParseChapterHtml(html).Text;
                if (!string.IsNullOrEmpty(t)) parts.Add(t);
                advanced = true;

                // 这一页没提示"还有下一页" → 到此为止
                if (NextPageNumberAt(html, cid, nums[i] + 1) == 0) break;
            }
            if (advanced) Http.Polite();

            return string.Join("\n\n", parts).Trim();
        }

        // ---------------------------------------------------------- 解析

        internal class ParsedChapter
        {
            public string Text = "";
            public string Title = "";
            public string NextPath = "";
        }

        internal static ParsedChapter ParseChapterHtml(string html)
        {
            var sb = new StringBuilder();
            foreach (Match m in Regex.Matches(html, @"document\.writeln\(\s*qsbs\.bb\('([^']*)'\)\s*\)"))
            {
                var block = DecodeBb(m.Groups[1].Value);
                foreach (var raw in Http.StripTags(block).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                {
                    var line = Regex.Replace(raw, @"[ \t\u3000]+", " ").Trim();
                    if (line.Length == 0) continue;
                    if (line == "送你一个现金红包！") continue;
                    if (line.StartsWith("请关闭浏览器阅读模式")) continue;
                    if (line.StartsWith("本站所有内容来源于互联网")) continue;
                    sb.Append(line).Append('\n');
                }
            }

            var titleM = Regex.Match(html, "<title>([\\s\\S]*?)</title>");
            var title = titleM.Success ? Http.StripTags(titleM.Groups[1].Value).Split('_')[0].Trim() : "";
            var nextM = Regex.Match(html, "var kkehvov='([^']+)'");

            return new ParsedChapter
            {
                Text = sb.ToString().Trim(),
                Title = title,
                NextPath = nextM.Success ? nextM.Groups[1].Value : "",
            };
        }

        internal static string DecodeBb(string b64)
        {
            var clean = Regex.Replace(b64, "[^A-Za-z0-9+/=]", "");
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(clean)); }
            catch { return ""; }
        }

        internal static string UrlOf(string dir, string key)
        {
            int i = key.IndexOf('_');
            if (i < 0) return string.Format("{0}{1}/{2}.html", Origin, dir, key);
            var cid = key.Substring(0, i);
            int page = int.Parse(key.Substring(i + 1));
            return string.Format("{0}{1}/{2}_{3}.html", Origin, dir, cid, page - 1);
        }

        // ---------------------------------------------------------- 批量抓取支持

        /// <summary>
        /// 一章正文的 URL（供 HttpBatch 批量并发抓取用）。
        ///
        /// 这里**能算**而不需要**猜**：下载阶段章节 id 全部已知
        /// （目录已经拿到了），所以 URL 是确定的。
        /// 这也是为什么批量抓取只用在下载阶段 —— 目录遍历那边
        /// 下一页地址只写在当前页 HTML 里，算不出来。
        /// </summary>
        public string ChapterUrlFor(BookInfo book, ChapterInfo chapter)
        {
            if (chapter == null) return null;
            var dir = BookDirOf(book);
            return (dir == null || string.IsNullOrEmpty(chapter.Id)) ? null : UrlOf(dir, chapter.Id);
        }

        /// <summary>批量抓取时要带的 Referer（指向这本书的目录页）</summary>
        public string ChapterRefererFor(BookInfo book)
        {
            var dir = BookDirOf(book);
            return dir == null ? Origin : Origin + dir + "/";
        }

        /// <summary>从 book.Dir 里取出 /10_10333 这种目录段（兼容带/不带结尾斜杠）</summary>
        private static string BookDirOf(BookInfo book)
        {
            if (book == null || string.IsNullOrEmpty(book.Dir)) return null;
            var d = book.Dir.TrimEnd('/');
            if (d.Length == 0) return null;
            return d.StartsWith("/") ? d : "/" + d;
        }

        /// <summary>
        /// 从抓下来的 HTML 里取出正文。
        ///
        /// **必须复用 ParseChapterHtml** —— 那是 LoadChapter 走的同一份提取逻辑
        /// （解 base64、去标签、过滤广告行）。复用才能保证
        /// "批量抓的正文"和"逐章抓的正文"逐字一致。
        ///
        /// 我第一版批量路径直接用了原始 HTML，结果把整页 HTML 写进了 txt：
        /// 单章 2516 字符变成 9476 字符，产物从 193KB 涨到 298KB ——
        /// 靠 A/B 对比逐章长度才发现。
        /// </summary>
        public string ChapterTextFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var p = ParseChapterHtml(html);
            return string.IsNullOrEmpty(p.Text) ? null : p.Text;
        }

        /// <summary>
        /// 页面里的 `shuqian(bookId,chapterId,...)` 带着**本章自己的 id**，
        /// 拿它和请求的 id 比，就能确认"这份 HTML 确实是这一章的"。
        ///
        /// 这是批量抓取的**防错位闸门**：批量是"一次交多个 URL、一次收多个文件"，
        /// 万一配对错位就会把别的章静默写进正文 —— 那比慢一点严重得多。
        /// 所以宁可靠它挡住、退回逐章请求。
        /// </summary>
        public bool HtmlMatchesChapter(string html, ChapterInfo chapter)
        {
            if (string.IsNullOrEmpty(html) || chapter == null || string.IsNullOrEmpty(chapter.Id)) return false;
            var m = Regex.Match(html, @"shuqian\(\s*\d+\s*,\s*(\d+)");
            if (!m.Success) return false;          // 找不到标记 → 不敢信
            return string.Equals(m.Groups[1].Value, chapter.Id, StringComparison.Ordinal);
        }

        /// <summary>
        /// 从 `fromPage` 号开始往后找第一个"确实存在的续页"，返回它的页码（1 表示 `cid_1.html`）；
        /// 后面没有续页则返回 0。
        ///
        /// **必须和 FetchChapterText 的判定一致**：那边是**迭代式**的 ——
        /// 每取回一页都再问一次"还有没有下一页"。
        /// 所以这里要**从当前页码往后扫**，而不是每次都只看 `cid_1.html`。
        /// 我第一版就写成"只看第一页里有没有 cid_1.html"，
        /// 结果三页的章节只拼到两页（实测 2494 字符只拿到 1760）。
        /// </summary>
        private static int NextPageNumberAt(string html, string cid, int fromPage)
        {
            if (string.IsNullOrEmpty(html)) return 0;
            for (int page = Math.Max(1, fromPage); page <= 30; page++)
            {
                if (html.IndexOf(cid + "_" + page + ".html", StringComparison.Ordinal) >= 0)
                    return page;
            }
            return 0;
        }

        /// <summary>
        /// 这一章还有没有下一页。返回**下一页的 key**（`cid_N`），没有则 null。
        /// 供批量抓取复用，保证和逐章路径拼出**完全相同**的正文。
        /// </summary>
        public string NextPageKeyOf(string html, ChapterInfo chapter)
        {
            if (string.IsNullOrEmpty(html) || chapter == null || string.IsNullOrEmpty(chapter.Id)) return null;
            int p = NextPageNumberAt(html, chapter.Id, 1);
            return p == 0 ? null : chapter.Id + "_" + p;
        }

        /// <summary>
        /// 当前页是 `cid_{curPage}.html` 时，后面还有没有下一页。
        /// 批量续抓循环用它逐页推进（和 FetchChapterText 的循环形状一致）。
        /// </summary>
        public string NextPageKeyAfter(string html, string cid, int curPage)
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(cid)) return null;
            int p = NextPageNumberAt(html, cid, curPage + 1);
            return p == 0 ? null : cid + "_" + p;
        }

        internal static string KeyOf(string cid, int page)
        {
            return page == 1 ? cid : cid + "_" + page;
        }


        /// <summary>
        /// 解析 /10_10333/10032108_2.html 这样的路径 -> (cid, 页码)
        /// 注意：第一页是 10032108.html（无后缀），第 N 页是 10032108_{N-1}.html
        /// </summary>
        internal static Tuple<string, int> ParseNextPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var m = Regex.Match(path, @"/(\d+)(?:_(\d+))?\.html$");
            if (!m.Success) return null;
            int page = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) + 1 : 1;
            return Tuple.Create(m.Groups[1].Value, page);
        }

        private static string FirstGroup(string input, string pattern, string fallback)
        {
            var m = Regex.Match(input, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : fallback;
        }

        /// <summary>
        /// 清理站点给的“简介”。
        /// 有些书的简介字段被站点塞了导航残留（例如 og:description 直接是
        /// `ahref="#begin"立即阅读/a`），这种要当作“没有简介”，不能写进 txt。
        /// </summary>
        internal static string CleanDesc(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return "";
            var s = desc.Trim();
            if (s.Length < 8) return "";
            // 站点导航/按钮残留
            string[] junk = { "立即阅读", "ahref=", "#begin", "加入书架", "开始阅读", "章节目录", "javascript:" };
            foreach (var j in junk)
                if (s.Contains(j) && s.Length < 60) return "";
            // 明显的半截 HTML
            if (s.StartsWith("a href", StringComparison.OrdinalIgnoreCase)) return "";
            if (s.StartsWith("<") || s.EndsWith("/a")) return "";
            return s;
        }
    }
}
