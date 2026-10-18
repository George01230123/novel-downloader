using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TomatoBiquga
{
    /// <summary>
    /// 搜索缓存 + 本地书名索引。
    ///
    /// ============================================================
    /// 为什么需要
    /// ============================================================
    /// 原来每次搜索都**实时打站点**，而且搜过的结果不留任何痕迹 ——
    /// 同一个书名搜两次就是两次网络请求，而且搜过的书下次还得再搜一遍。
    ///
    /// ============================================================
    /// 为什么不用 SQLite FTS5 / Meilisearch / Elasticsearch
    /// ============================================================
    /// 那些是**全文检索**方案，用来在长文本里找词。而这里要索引的是
    /// **书名 + 作者**这种极短字段，总量也就几百到几千条 ——
    /// 用全文检索引擎检索短字段是杀鸡用牛刀，而且：
    ///
    ///   · 引 SQLite 就打破了本项目"零第三方依赖、免安装单文件 exe"的承诺；
    ///   · FTS5 的中文分词对"书名"这种短串本来就不擅长；
    ///   · Meilisearch / Elasticsearch 要额外起服务进程，用户得装环境。
    ///
    /// 所以这里就用**内存索引 + 一个 JSON 文件**：几百 KB、零依赖、
    /// 单文件 exe 不受影响，而且对这种数据量完全够用。
    ///
    /// ============================================================
    /// 两级设计
    /// ============================================================
    ///   ① 按关键词缓存整次搜索结果 —— 搜过的词直接秒回，不再打站点
    ///   ② 累积书名索引 —— 凡是"见过"的书（搜到过 / 下过载）都在里面，
    ///      可以用书名或作者本地模糊查，**完全不联网**
    ///
    /// 已知限制（必须说清楚）：索引是**靠搜过的结果慢慢积累**的，
    /// 不是全站书目。biquga 没有全站书目接口，所以"不联网搜全站"做不到；
    /// 能保证的是"你搜过/下过的书，以后都能本地秒查到"。
    /// </summary>
    public static class SearchIndex
    {
        public const string FileName = "搜索索引.json";

        /// <summary>搜索结果的缓存有效期（小时）。过了就重查，免得书站改版了还一直给旧结果</summary>
        public static double MaxAgeHours = 24;

        private static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        // ---------------------------------------------------------- 数据结构

        /// <summary>一次搜索的缓存</summary>
        public class QueryHit
        {
            public string keyword { get; set; }

            /// <summary>
            /// 这次搜索是在哪个站点做的（SiteRegistry 的 Key）。
            ///
            /// ★ 为什么必须存：这本书里搜出来的书是**那个站点**的书 ——
            ///   它的 Dir/Url 只在那站点有效。缓存不带站点维度的话，
            ///   在 A 站搜「沧元图」、切到 B 站再搜，会**直接返回 A 站的结果**，
            ///   于是"载入目录"拿着 A 站的地址去请求 B 站。
            ///   3 个站点时这是潜在 bug，站点一多就是必然出错。
            ///
            /// 老索引文件里没有这个字段 → 反序列化后是 null，
            /// 由 <see cref="SiteKeyCompatible"/> 决定"能不能用"，不会被误当成命中。
            /// </summary>
            public string site { get; set; }

            public string savedAt { get; set; }
            public List<Entry> books { get; set; }
        }

        /// <summary>索引里的一本书（只存检索和展示要用的字段，不存章节）</summary>
        public class Entry
        {
            public string site { get; set; }
            public string title { get; set; }
            public string author { get; set; }
            public string category { get; set; }
            public string url { get; set; }
            public string dir { get; set; }
            public string bookId { get; set; }
            /// <summary>备注：字数 · 状态 · 更新时间（v1.6.1 新增，见 SiteRule.SearchDescColumns）</summary>
            public string desc { get; set; }
            public string savedAt { get; set; }
            /// <summary>归一化后的书名，用于匹配（省得每次现算）</summary>
            public string key { get; set; }

            public BookInfo ToBook()
            {
                return new BookInfo
                {
                    Site = site ?? "",
                    Title = title ?? "",
                    Author = author ?? "",
                    Category = category ?? "",
                    Url = url ?? "",
                    Dir = dir ?? "",
                    BookId = bookId ?? "",
                    Desc = desc ?? "",
                };
            }

            public static Entry FromBook(BookInfo b)
            {
                var e = new Entry
                {
                    site = b.Site ?? "",
                    title = b.Title ?? "",
                    author = b.Author ?? "",
                    category = b.Category ?? "",
                    url = b.Url ?? "",
                    dir = b.Dir ?? "",
                    bookId = b.BookId ?? "",
                    desc = b.Desc ?? "",
                    savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                e.key = Normalize(e.title);
                return e;
            }
        }

        private class Store
        {
            public List<QueryHit> queries { get; set; }
            public List<Entry> books { get; set; }

            /// <summary>
            /// 缓存格式版本。**读到老版本就整份丢弃**（当作没有缓存）。
            ///
            /// ★ 为什么需要：v1.6.1 给搜索结果补上了作者/备注字段。
            ///   老缓存里 author 是空串（当年 RuleSite 根本不填作者），
            ///   继续用的话用户升到新版本后**仍然看不到作者** ——
            ///   还会以为"改了半天没用"。字段补全后老数据像 bug，宁可让用户重搜一次。
            /// </summary>
            public int version { get; set; }
        }

        /// <summary>当前缓存格式版本。加/改字段就 +1。</summary>
        internal const int SchemaVersion = 2;

        private static readonly object _lock = new object();
        private static Store _cache;

        // ---------------------------------------------------------- 读写

        private static Store Load()
        {
            if (_cache != null) return _cache;
            lock (_lock)
            {
                if (_cache != null) return _cache;
                var s = new Store { queries = new List<QueryHit>(), books = new List<Entry>(), version = SchemaVersion };
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath, Encoding.UTF8);
                        var parsed = new JavaScriptSerializer().Deserialize<Store>(json);
                        if (parsed != null)
                        {
                            // ★ 版本不符：整份丢弃（见 Store.version 的注释）。
                            //   不删文件，Save 时会被新格式覆盖。
                            if (parsed.version != SchemaVersion)
                            {
                                Log.Line("搜索索引格式版本不符（文件 " + parsed.version
                                    + " / 程序 " + SchemaVersion + "），已忽略旧缓存并重建。文件=" + FilePath);
                            }
                            else
                            {
                            if (parsed.queries != null) s.queries = parsed.queries;
                            if (parsed.books != null) s.books = parsed.books;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ★ 原来这里是 `catch { }` —— 文件读不了/JSON 坏了就**静默**当空索引，
                    //   于是"本地查"突然什么都查不到，而用户和排查的人都没有任何线索。
                    //   现在照旧降级（不能因为缓存坏了让搜索不可用），但必须留证据。
                    Log.Line("搜索索引读取失败，本次按空索引处理：" + ex.GetType().Name
                        + " / " + ex.Message + "  文件=" + FilePath);
                }
                _cache = s;
                return _cache;
            }
        }

        /// <summary>立即落盘（搜索完就写，这样关掉程序也不丢）</summary>
        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var s = _cache ?? new Store { queries = new List<QueryHit>(), books = new List<Entry>() };
                    var json = new JavaScriptSerializer().Serialize(s);
                    File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    // 同样：写入失败不影响这次搜索，但必须留证据 ——
                    // 否则表现为"搜到了但下次就没了/本地查永远空"，无从查起。
                    // 只读介质（U 盘写保护）、磁盘满、杀软锁文件都会走到这里。
                    Log.Line("搜索索引写入失败（本次结果仍会显示，但不会留住）："
                        + ex.GetType().Name + " / " + ex.Message + "  文件=" + FilePath);
                }
            }
        }

        // ---------------------------------------------------------- ① 关键词缓存

        /// <summary>取关键词的缓存结果；没有、过期、或**不是这个站点搜的**则返回 null</summary>
        public static List<BookInfo> GetQuery(string keyword, string siteKey)
        {
            if (string.IsNullOrEmpty(keyword)) return null;
            var s = Load();
            var k = keyword.Trim();
            lock (_lock)
            {
                foreach (var q in s.queries)
                {
                    if (!string.Equals(q.keyword, k, StringComparison.OrdinalIgnoreCase)) continue;
                    // ★ 站点必须对得上 —— 否则会把别的站点搜出来的书当作本站结果
                    if (!SiteKeyCompatible(q.site, siteKey)) continue;
                    var at = ParseTime(q.savedAt);
                    // 过期就当没有 —— 书站会加新书，旧结果不能一直用
                    if (at.HasValue && (DateTime.Now - at.Value).TotalHours > MaxAgeHours) return null;
                    var list = new List<BookInfo>();
                    if (q.books != null) foreach (var e in q.books) list.Add(e.ToBook());
                    return list;
                }
            }
            return null;
        }

        /// <summary>
        /// 缓存里记的站点和现在要用的站点是否兼容。
        ///
        /// 老索引（加站点维度之前写的）没有 site 字段 → 读出来是 null。
        /// 那种记录**不知道**是哪个站搜的，所以一律不用（当作未命中，让它重新搜一次）。
        /// 宁可贵一次搜索，也不能把 A 站的书当成 B 站的结果 ——
        /// 后者会让"载入目录"拿着错的地址去请求，用户看到的是莫名其妙的失败。
        /// </summary>
        internal static bool SiteKeyCompatible(string cachedSite, string wantedSite)
        {
            if (string.IsNullOrEmpty(cachedSite)) return false;
            if (string.IsNullOrEmpty(wantedSite)) return false;
            return string.Equals(cachedSite, wantedSite, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>存一次搜索结果（同时把书并进累积索引）。siteKey = 这次是在哪个站点搜的。</summary>
        public static void PutQuery(string keyword, List<BookInfo> books, string siteKey)
        {
            if (string.IsNullOrEmpty(keyword) || books == null) return;
            var s = Load();
            var k = keyword.Trim();
            lock (_lock)
            {
                QueryHit hit = null;
                // ★ 同一个关键词在不同站点是**不同的**缓存条目，所以查找也要带站点。
                foreach (var q in s.queries)
                    if (string.Equals(q.keyword, k, StringComparison.OrdinalIgnoreCase)
                        && SiteKeyCompatible(q.site, siteKey)) { hit = q; break; }
                if (hit == null)
                {
                    hit = new QueryHit { keyword = k, books = new List<Entry>() };
                    s.queries.Add(hit);
                }
                hit.site = siteKey;
                hit.savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                hit.books = new List<Entry>();
                foreach (var b in books) { hit.books.Add(Entry.FromBook(b)); AddBookLocked(s, b); }

                // 关键词缓存留最近 200 条就够，别无限长
                while (s.queries.Count > 200) s.queries.RemoveAt(0);
            }
            Save();
        }

        // ---------------------------------------------------------- ② 累积书名索引

        /// <summary>把一本书并进索引（下载过的书也该进，这样能本地查到）</summary>
        public static void AddBook(BookInfo b)
        {
            if (b == null || string.IsNullOrEmpty(b.Title)) return;
            var s = Load();
            lock (_lock) { AddBookLocked(s, b); }
            Save();
        }

        private static void AddBookLocked(Store s, BookInfo b)
        {
            var e = Entry.FromBook(b);
            if (string.IsNullOrEmpty(e.key)) return;
            for (int i = 0; i < s.books.Count; i++)
            {
                var old = s.books[i];
                // 同一本站点 + 同一本书 → 覆盖（补全作者/分类等信息）
                if (string.Equals(old.site, e.site, StringComparison.OrdinalIgnoreCase)
                    && SameBook(old, e))
                {
                    s.books[i] = e;
                    return;
                }
            }
            s.books.Add(e);
            while (s.books.Count > 5000) s.books.RemoveAt(0);
        }

        private static bool SameBook(Entry a, Entry b)
        {
            if (!string.IsNullOrEmpty(a.bookId) && !string.IsNullOrEmpty(b.bookId))
                return a.bookId == b.bookId;
            if (!string.IsNullOrEmpty(a.dir) && !string.IsNullOrEmpty(b.dir))
                return a.dir == b.dir;
            return a.key == b.key;
        }

        public static int BookCount
        {
            get { lock (_lock) { return Load().books.Count; } }
        }

        // ---------------------------------------------------------- 模糊检索

        /// <summary>
        /// 本地模糊检索（**不联网**）。返回按相关度排序的书。
        ///
        /// 匹配分三档，按优先级：
        ///   1000  书名归一化后完全相同
        ///    800  书名以关键词开头
        ///    600  书名包含关键词
        ///    400  作者包含关键词
        ///    0~200 书名是关键词的子序列（容忍中间插字，比如"沧图"匹配"沧元图"）
        /// </summary>
        public static List<BookInfo> Search(string keyword, int limit)
        {
            var res = new List<BookInfo>();
            if (string.IsNullOrEmpty(keyword)) return res;
            var s = Load();
            var q = Normalize(keyword);
            if (q.Length == 0) return res;

            var scored = new List<KeyValuePair<int, Entry>>();
            lock (_lock)
            {
                foreach (var e in s.books)
                {
                    int score = Score(e, q);
                    if (score > 0) scored.Add(new KeyValuePair<int, Entry>(score, e));
                }
            }
            scored.Sort(delegate(KeyValuePair<int, Entry> x, KeyValuePair<int, Entry> y)
            {
                if (y.Key != x.Key) return y.Key.CompareTo(x.Key);
                return string.Compare(x.Value.title, y.Value.title, StringComparison.Ordinal);
            });

            for (int i = 0; i < scored.Count && i < limit; i++)
                res.Add(scored[i].Value.ToBook());
            return res;
        }

        private static int Score(Entry e, string q)
        {
            var t = e.key ?? "";
            if (t.Length == 0) return 0;
            if (t == q) return 1000;
            if (t.StartsWith(q, StringComparison.Ordinal)) return 800;
            if (t.IndexOf(q, StringComparison.Ordinal) >= 0) return 600;
            if (!string.IsNullOrEmpty(e.author) && Normalize(e.author).IndexOf(q, StringComparison.Ordinal) >= 0)
                return 400;
            return Subsequence(t, q) ? 200 : 0;
        }

        /// <summary>q 是不是 t 的子序列（按顺序出现、允许中间插字）</summary>
        private static bool Subsequence(string t, string q)
        {
            if (q.Length == 0 || q.Length > t.Length) return false;
            int j = 0;
            for (int i = 0; i < t.Length && j < q.Length; i++)
                if (t[i] == q[j]) j++;
            return j == q.Length;
        }

        /// <summary>
        /// 归一化：去掉书名里那些"看着不一样其实是同一本"的干扰 ——
        /// 书名号、空格、全角标点、以及常见的站名后缀（"最新章节"之类）。
        /// 这样"《沧元图》" "沧元图 " "沧元图最新章节" 都能匹配到一起。
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '《' || ch == '》' || ch == '（' || ch == '）' || ch == '(' || ch == ')'
                    || ch == '[' || ch == ']' || ch == '【' || ch == '】' || ch == '·'
                    || ch == '，' || ch == ',' || ch == '。' || ch == '.' || ch == '、'
                    || ch == '：' || ch == ':' || ch == '；' || ch == ';' || ch == '！' || ch == '!'
                    || ch == '？' || ch == '?' || ch == '-' || ch == '_' || ch == '—' || ch == '"'
                    || ch == '\'' || ch == '“' || ch == '”' || ch == '‘' || ch == '’')
                    continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            var r = sb.ToString();
            // 站名后缀：搜索结果标题常带这些，去掉才能和用户输入的书名对上
            foreach (var suf in new[] { "最新章节", "免费阅读", "全文阅读", "无弹窗", "笔趣阁", "小说" })
            {
                if (r.Length > suf.Length && r.EndsWith(suf, StringComparison.Ordinal))
                    r = r.Substring(0, r.Length - suf.Length);
            }
            return r;
        }

        private static DateTime? ParseTime(string s)
        {
            DateTime t;
            if (DateTime.TryParse(s, out t)) return t;
            return null;
        }

        /// <summary>
        /// 把书架里的书一次性并进索引。
        ///
        /// 为什么要这一步：索引如果只从"搜索过的结果"积累，那么已经下载了一堆书的老用户
        /// 打开「本地查」会**查不到任何东西** —— 因为那些书是以前下的、从没被索引过。
        /// 书架里正好有书名和作者，导进来就能立刻用。
        ///
        /// 每次启动只做一遍（用 _shelfSynced 挡住）：书架几百条时也不慢，
        /// 但没必要每次检索都重扫一遍。
        /// </summary>
        public static void SyncBookshelf()
        {
            lock (_lock)
            {
                if (_shelfSynced) return;
                _shelfSynced = true;
            }
            try
            {
                var shelf = Bookshelf.Load();
                if (shelf == null) return;
                var s = Load();
                lock (_lock)
                {
                    foreach (var e in shelf.Entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.Title)) continue;
                        // ★ 书架的 Key 对两种站点含义不同（见 Bookshelf.KeyOf）：
                        //   biquga 是目录（/10_10333），番茄是 bookId（7256784068786785336）。
                        //   不能一律塞进 Dir —— 番茄那本导进来后 Dir 会变成一串数字，
                        //   以后再点开这本书就找不到目录了。按形状分派。
                        var key = e.Key ?? "";
                        var isDir = key.StartsWith("/", StringComparison.Ordinal);
                        AddBookLocked(s, new BookInfo
                        {
                            Site = e.Site ?? "",
                            Title = e.Title,
                            Author = e.Author ?? "",
                            Url = e.Url ?? "",
                            Dir = isDir ? key : "",
                            BookId = isDir ? "" : key,
                        });
                    }
                }
                Save();
            }
            catch { /* 书架读不出来不该影响搜索功能 */ }
        }

        private static bool _shelfSynced;
        /// <summary>供设置界面显示/清空</summary>
        public static int QueryCount
        {
            get { lock (_lock) { return Load().queries.Count; } }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _cache = new Store { queries = new List<QueryHit>(), books = new List<Entry>() };
            }
            Save();
        }
    }
}
