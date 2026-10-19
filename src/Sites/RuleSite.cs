using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TomatoBiquga
{
    /// <summary>
    /// 由规则表（<see cref="SiteRule"/>）驱动的通用站点实现。
    ///
    /// 一个类吃下所有规则表站点：加站点 = 在 SiteRules.cs 里加一条规则，
    /// 不用再写一个站点类。站点专属的知识全在规则里。
    ///
    /// 编码交给 <see cref="Http.Get"/> —— 它内部的 Decode() 已经会按
    /// Content-Type 或页面里的 charset 自动判 utf-8 / gb18030，
    /// 所以本类不需要自己处理编码（实测笔尖中文的正文页就是 GBK）。
    /// </summary>
    public class RuleSite : ISite, IProbeable, IRuleSite, IChapterUrlProvider
    {
        private readonly SiteRule _r;

        /// <summary>本次载入的书籍路径（TocUrlTemplate 的 %s 用它）</summary>
        private string _currentBookPath;

        /// <summary>本次载入的章节地址表：章节 id → 绝对地址。
        /// 批量路径（IChapterUrlProvider）靠它把 id 精确还原成 URL，
        /// 而不是去猜站点的命名规律。</summary>
        private readonly Dictionary<string, string> _chapterUrls =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly object _lock = new object();

        public RuleSite(SiteRule rule)
        {
            if (rule == null) throw new ArgumentNullException("rule");
            rule.Validate();          // 规则不自洽就地炸，别等用户搜不到书
            _r = rule;
        }

        public string Key { get { return _r.Key; } }
        public string Name { get { return _r.DisplayName; } }
        public SiteRule Rule { get { return _r; } }

        // ============================================================
        //  探活
        // ============================================================

        public string Probe()
        {
            try
            {
                var html = Http.Get(_r.Base, _r.Base);
                if (string.IsNullOrEmpty(html)) return "首页返回空内容";
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ============================================================
        //  搜索
        // ============================================================

        public List<BookInfo> Search(string keyword, Action<string> log)
        {
            var result = new List<BookInfo>();
            if (string.IsNullOrEmpty(keyword)) return result;

            string referer = string.IsNullOrEmpty(_r.SearchReferer) ? _r.Base : _r.SearchReferer;
            string html;
            if (_r.SearchIsPost)
            {
                var body = BuildSearchBody(keyword);
                html = Http.Post(_r.SearchUrl, body, referer);
            }
            else
            {
                string url = _r.SearchUrl.IndexOf("%s", StringComparison.Ordinal) >= 0
                    ? _r.SearchUrl.Replace("%s", Uri.EscapeDataString(keyword))
                    : _r.SearchUrl;
                html = Http.Get(url, referer);
            }

            if (string.IsNullOrEmpty(html))
            {
                if (log != null) log(_r.DisplayName + "：搜索返回空内容");
                return result;
            }

            // "站点忽略了搜索词、返回固定页" 是最阴的失败（梦书中文实测如此）。
            // 开了这个开关就要求结果页确实含关键词，否则如实报"无结果"。
            if (_r.SearchResultMustContainKeyword && html.IndexOf(keyword, StringComparison.Ordinal) < 0)
            {
                if (log != null) log(_r.DisplayName + "：搜索结果页不含关键词，判定为无结果（站点可能忽略了搜索词）");
                return result;
            }

            if (!string.IsNullOrEmpty(_r.SearchIsResultPattern) &&
                !Regex.IsMatch(html, _r.SearchIsResultPattern))
            {
                if (log != null) log(_r.DisplayName + "：搜索结果页不像结果页（未匹配验证规则）");
                return result;
            }

            var links = ExtractBookLinks(html);
            if (log != null)
                log(_r.DisplayName + "：搜索「" + keyword + "」得到 " + links.Count + " 条结果");

            // ★ 0 条时**说清原因**，别只丢一句"没搜到"。
            //   真实原因至少三种，处理方式完全不同：
            //     站点确实没这本书 / 被限流 / **改版导致规则失效**。
            //   以前分不清，只能手工 curl 一遍遍试（我为此浪费过很多时间）。
            if (links.Count == 0 && log != null)
            {
                try
                {
                    log("   · 诊断：" + SiteDiagnostics.DiagnoseEmptySearch(
                        html, 0, _r.SearchIsResultPattern));
                }
                catch { }
            }

            foreach (var link in links)
            {
                string abs = Absolutize(link, _r.SearchUrl);
                string title = null;
                try { title = BookTitleFromSearch(html, link); } catch { }
                if (string.IsNullOrEmpty(title)) title = "（载入后取书名）";

                // ★ 从**这一行**里把作者/字数/状态抠出来。
                //   以前这里只留了书名，作者永远空 —— 用户抱怨"连是谁写的都看不到"，
                //   而站点其实给了（见 SiteRule.SearchAuthorColumn 的注释）。
                var row = CellsOfResultRow(html, link);
                string author = PickCell(row, _r.SearchAuthorColumn);
                string desc = BuildDesc(row, _r.SearchDescColumns);

                result.Add(new BookInfo
                {
                    Site = _r.Key,
                    Title = title,
                    Author = author,
                    Desc = desc,
                    Dir = PathOf(abs),
                    Url = abs,
                });
            }
            return result;
        }

        /// <summary>
        /// 取"包含这个书籍链接的那一行"里的所有单元格纯文本。
        ///
        /// 做法：先定位 href 在这次搜索页里的位置，再向**前**找最近的 `&lt;tr`、
        /// 向**后**找最近的 `&lt;/tr&gt;`，把这中间的所有 `&lt;td&gt;…&lt;/td&gt;` 抽成文本。
        /// 不做整页解析：站点结构五花八门，而"链接所在的那一行"是共通的。
        /// 拿不到就返回空表 —— 调用方会把作者留空，不会崩。
        /// </summary>
        private List<string> CellsOfResultRow(string html, string href)
        {
            var cells = new List<string>();
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(href)) return cells;
            try
            {
                int at = html.IndexOf("href=\"" + href + "\"", StringComparison.OrdinalIgnoreCase);
                if (at < 0) at = html.IndexOf(href, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return cells;

                int trStart = html.LastIndexOf("<tr", at, StringComparison.OrdinalIgnoreCase);
                if (trStart < 0) return cells;
                int trEnd = html.IndexOf("</tr>", at, StringComparison.OrdinalIgnoreCase);
                if (trEnd < 0) trEnd = Math.Min(html.Length, at + 4000);
                string rowHtml = html.Substring(trStart, trEnd - trStart);

                foreach (Match m in Regex.Matches(rowHtml, "<td[^>]*>([\\s\\S]*?)</td>",
                                                  RegexOptions.IgnoreCase))
                {
                    string t = Http.StripTags(m.Groups[1].Value);
                    t = Regex.Replace(t, "\\s+", " ").Trim();
                    cells.Add(t);
                }
            }
            catch { }
            return cells;
        }

        /// <summary>按下标取单元格；下标非法就返回空串（不是 null，列表列更整齐）</summary>
        private static string PickCell(List<string> cells, int index)
        {
            if (cells == null || index < 0 || index >= cells.Count) return "";
            string v = cells[index];
            if (string.IsNullOrEmpty(v)) return "";
            if (v.Length > 40) v = v.Substring(0, 40);
            return v;
        }

        /// <summary>把指定列拼成备注，例如「758K · 连载 · 17-08-05」</summary>
        private static string BuildDesc(List<string> cells, int[] cols)
        {
            if (cells == null || cols == null || cols.Length == 0) return "";
            var parts = new List<string>();
            foreach (int i in cols)
            {
                string v = PickCell(cells, i);
                if (v.Length == 0) continue;
                parts.Add(v);
            }
            string s = string.Join(" · ", parts.ToArray());
            if (s.Length > 80) s = s.Substring(0, 80);
            return s;
        }

        /// <summary>POST 表单体：字段值里的 %s 换成关键词，其余按 urlencoded 拼</summary>
        private string BuildSearchBody(string keyword)
        {
            var sb = new StringBuilder();
            foreach (string[] f in _r.SearchForm)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(f[0]));
                sb.Append('=');
                string v = f[1] == null ? "" : f[1];
                if (v.IndexOf("%s", StringComparison.Ordinal) >= 0)
                    v = v.Replace("%s", keyword);
                sb.Append(Uri.EscapeDataString(v));
            }
            return sb.ToString();
        }

        /// <summary>按序尝试各 BookLinkPatterns，第一个有命中的胜出（去重、保序）</summary>
        private List<string> ExtractBookLinks(string html)
        {
            foreach (string pat in _r.BookLinkPatterns)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var list = new List<string>();
                foreach (Match m in Regex.Matches(html, pat))
                {
                    if (m.Groups.Count < 2) continue;
                    string v = m.Groups[1].Value;
                    if (string.IsNullOrEmpty(v)) continue;
                    v = Http.HtmlDecode(v).Trim();
                    if (v.Length == 0 || v.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (seen.Add(v)) list.Add(v);
                }
                if (list.Count > 0) return list;
            }
            return new List<string>();
        }

        /// <summary>
        /// 从搜索结果页尽力取书名：优先用"链接自己的文字"，因为详情页要多发一次请求，
        /// 搜索结果里能拿到就不用多这一次往返。取不到就交给 LoadBook 去详情页补。
        /// </summary>
        private string BookTitleFromSearch(string html, string href)
        {
            string esc = Regex.Escape(href);
            var m = Regex.Match(html, "<a[^>]*href=\"\\s*" + esc + "\\s*\"[^>]*>([\\s\\S]{0,120}?)</a>",
                                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string t = Http.StripTags(m.Groups[1].Value);
                t = Regex.Replace(t, "\\s+", " ").Trim();
                // 书名里带换行的多半是"书名 + 最新章节"两行拼在一起，取第一行
                int nl = t.IndexOf('\n');
                if (nl > 0) t = t.Substring(0, nl).Trim();
                if (t.Length > 0 && t.Length <= 60) return t;
            }
            return null;
        }

        // ============================================================
        //  载入目录
        // ============================================================

        public BookInfo LoadBook(BookInfo item, Action<string> log)
        {
            if (item == null || string.IsNullOrEmpty(item.Url))
                throw new Exception("载入目录需要书籍地址");

            var book = new BookInfo
            {
                Site = _r.Key,
                Url = item.Url,
                Dir = item.Dir,
                Title = item.Title,
                Author = item.Author,
                Category = item.Category,
            };

            if (log != null) log("读取书籍信息…");
            string html = Http.Get(item.Url, _r.Base);
            if (string.IsNullOrEmpty(html))
                throw new Exception("详情页返回空内容：" + item.Url);

            string title = Group(html, _r.BookTitlePattern);
            if (!string.IsNullOrEmpty(title)) book.Title = title;
            string author = Group(html, _r.BookAuthorPattern);
            if (!string.IsNullOrEmpty(author)) book.Author = CleanAuthor(author);
            string intro = Group(html, _r.BookIntroPattern);
            if (!string.IsNullOrEmpty(intro)) book.Desc = Regex.Replace(Http.StripTags(intro), "\\s+", " ").Trim();
            string cover = Group(html, _r.BookCoverPattern);
            if (!string.IsNullOrEmpty(cover)) book.CoverUrl = CoverFetcher.Absolutize(cover, item.Url);

            if (string.IsNullOrEmpty(book.Title))
                throw new Exception("详情页取不到书名，站点结构可能已变化：" + item.Url
                    + "\n（规则 " + _r.Key + ".BookTitlePattern 需要更新）");

            // 目录页：多数站点详情页就是目录页；少数要另取一个地址
            string tocUrl = item.Url;
            if (!string.IsNullOrEmpty(_r.TocUrlTemplate))
            {
                string path = PathOf(item.Url);
                tocUrl = ExpandTemplate(_r.TocUrlTemplate, path);
                if (!string.Equals(tocUrl, item.Url, StringComparison.OrdinalIgnoreCase))
                {
                    if (log != null) log("读取目录页…");
                    string tocHtml = Http.Get(tocUrl, item.Url);
                    if (!string.IsNullOrEmpty(tocHtml)) html = tocHtml;
                }
            }
            _currentBookPath = PathOf(item.Url);

            var chapters = ExtractChapters(html, tocUrl);
            if (chapters.Count == 0)
            {
                // ★ 目录为 0 也要说清原因（限流 / 下架 / 改版），别只丢一句"结构可能变了"。
                string why;
                try { why = SiteDiagnostics.DiagnoseEmptyToc(html, 0); }
                catch { why = "原因不明"; }
                throw new Exception("没抓到任何章节：" + why + "  地址=" + tocUrl);
            }

            book.Chapters = chapters;
            if (log != null)
                log("目录完成：共 " + chapters.Count + " 章（" + chapters[0].Title + " → "
                    + chapters[chapters.Count - 1].Title + "）");
            return book;
        }

        /// <summary>抽章节链接；建索引并把 id→URL 记下来给批量路径用</summary>
        private List<ChapterInfo> ExtractChapters(string html, string tocUrl)
        {
            string linkBase = !string.IsNullOrEmpty(_r.TocLinkBase) ? _r.TocLinkBase : tocUrl;

            // 先把目录容器框出来。页面上到处是 `xxx.html`（分类页/排行榜/侧栏），
            // 不框定就会把 list/1_1.html 这种当成章节抓进来
            // —— 实测笔尖中文详情页上就有 7 个这种干扰链接。
            string scope = html;
            if (!string.IsNullOrEmpty(_r.TocBlockPattern))
            {
                var bm = Regex.Match(html, _r.TocBlockPattern, RegexOptions.IgnoreCase);
                if (bm.Success && bm.Groups.Count >= 2 && bm.Groups[1].Value.Length > 0)
                    scope = bm.Groups[1].Value;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ChapterInfo>();
            int order = 0;

            foreach (Match m in Regex.Matches(scope, _r.TocLinkPattern))
            {
                if (m.Groups.Count < 2) continue;
                string href = Http.HtmlDecode(m.Groups[1].Value).Trim();
                if (href.Length == 0 || href.StartsWith("#", StringComparison.Ordinal)) continue;

                string abs = Absolutize(href, linkBase);
                string id = IdOf(abs);
                if (id.Length == 0 || !seen.Add(id)) continue;

                // 标题：**只用匹配到的那一小段**去取，绝不在整页里再搜一次 URL。
                //
                // ★ 这里踩过一个很隐蔽的坑：原来是在整页里用
                //   `<a[^>]*href="该URL"[^>]*>([\s\S]{0,160}?)</a>` 再搜一遍。
                //   而 href 前面是 `[^>]*` —— 允许跨过 `>`，于是：
                //     ① 取到的文字常常是**前一个链接**的文字；
                //     ② 当页面上更早的位置有**同一个 URL** 的导航链接时，
                //        那一个会先被匹到。笔趣阁365 的 `/newbook/` 页正是如此：
                //        div.gongneng 里的「开始阅读」指向第 1 章，于是目录第一项
                //        的标题变成了「开始阅读」。
                //   更阴的是：Python 里同一个正则**不会**这样匹配（它的 [^>] 语义
                //   与 .NET 在这里的表现不同），所以拿 Python 验证会得出"没问题"。
                //   现在改成从 m 自己的匹配文本里取 —— 逻辑上不可能取错。
                string title = null;
                if (m.Groups.Count >= 3 && m.Groups[2].Success)
                {
                    // 首选：规则用第二个捕获组直接给出链接文字
                    title = Regex.Replace(Http.StripTags(m.Groups[2].Value), "\\s+", " ").Trim();
                }
                else
                {
                    // 兜底：在**这一小段匹配**里找 <a ...>文字</a>
                    var tm = Regex.Match(m.Value, ">([^<]{1,160})</a>", RegexOptions.IgnoreCase);
                    if (tm.Success)
                        title = Regex.Replace(Http.StripTags(tm.Groups[1].Value), "\\s+", " ").Trim();
                }
                if (!string.IsNullOrEmpty(title) && title.Length > 80) title = title.Substring(0, 80);
                if (string.IsNullOrEmpty(title)) title = "第 " + (order + 1) + " 章";

                list.Add(new ChapterInfo { Id = id, Title = title, Order = order++ });
                lock (_lock) _chapterUrls[id] = abs;
            }

            if (_r.TocIsDescending) list.Reverse();
            for (int i = 0; i < list.Count; i++) list[i].Order = i;
            return list;
        }

        // ============================================================
        //  抓正文
        // ============================================================

        public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
        {
            if (book == null || chapter == null) return null;
            string url = ChapterUrlFor(book, chapter);
            if (string.IsNullOrEmpty(url))
            {
                if (log != null) log("算不出这一章的地址：" + (chapter == null ? "?" : chapter.Id));
                return null;
            }

            string html;
            try { html = Http.Get(url, book.Url); }
            catch (Exception ex)
            {
                if (log != null) log("抓正文失败：" + ex.Message);
                return null;
            }
            if (string.IsNullOrEmpty(html)) return null;
            return ChapterTextFromHtml(html);
        }

        /// <summary>
        /// 从正文页 HTML 抽正文。逐章路径与批量路径**共用这一份**实现 ——
        /// 两条路径必须产出完全一致的内容，否则批量反而更糟。
        /// </summary>
        public string ChapterTextFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var m = Regex.Match(html, _r.ContentPattern, RegexOptions.IgnoreCase);
            if (!m.Success || m.Groups.Count < 2) return null;

            string block = m.Groups[1].Value;

            if (_r.ContentIsBase64)
            {
                string raw = Regex.Replace(Http.StripTags(block), "\\s+", "");
                block = DecodeBase64Text(raw);
                if (block == null) return null;
            }

            string text = Http.StripTags(block);

            if (!string.IsNullOrEmpty(_r.ContentStripPattern))
            {
                try { text = Regex.Replace(text, _r.ContentStripPattern, "", RegexOptions.IgnoreCase); }
                catch { /* 剔除失败不该毁掉正文 */ }
            }

            text = NormalizeText(text);
            return text.Length == 0 ? null : text;
        }

        /// <summary>规范化：统一换行、去掉行首空白、压缩连续空行</summary>
        private static string NormalizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            s = Regex.Replace(s, "[\\t\\u00a0\\u3000 ]+\n", "\n");
            s = Regex.Replace(s, "\n[\\t\\u00a0\\u3000 ]+", "\n");
            s = Regex.Replace(s, "\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>尽力把 base64 解成文字（先 UTF-8 再 GB18030）</summary>
        private static string DecodeBase64Text(string b64)
        {
            try
            {
                string s = b64.Replace('-', '+').Replace('_', '/');
                s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
                byte[] raw = Convert.FromBase64String(s);
                try { return Encoding.UTF8.GetString(raw); }
                catch { }
                try { return Encoding.GetEncoding("gb18030").GetString(raw); }
                catch { return null; }
            }
            catch { return null; }
        }

        private string CleanAuthor(string a)
        {
            a = Regex.Replace(a, "\\s+", " ").Trim();
            a = a.Replace("作者：", "").Replace("作者:", "").Trim();
            return a;
        }

        // ============================================================
        //  IChapterUrlProvider —— 让批量并发抓取可用
        // ============================================================

        /// <summary>
        /// 能否走批量并发抓取。只有规则明确声明"章内页地址可按 key 推算"
        /// 才返回 true —— 否则批量只能抓到第一页，产出**被静默截断的正文**。
        /// </summary>
        public bool SupportsBatch { get { return _r.ChapterPagesAddressableByKey; } }

        public string ChapterUrlFor(BookInfo book, ChapterInfo chapter)
        {
            if (chapter == null || string.IsNullOrEmpty(chapter.Id)) return null;
            lock (_lock)
            {
                string u;
                if (_chapterUrls.TryGetValue(chapter.Id, out u)) return u;
            }
            // 目录是用缓存载入的（没走过 ExtractChapters）时没有地址表，
            // 这时不猜 —— 返回 null，调用方逐章走 LoadChapter。
            return null;
        }

        public string ChapterRefererFor(BookInfo book)
        {
            return book != null && !string.IsNullOrEmpty(book.Url) ? book.Url : _r.Base;
        }

        public string NextPageKeyOf(string html, ChapterInfo chapter) { return null; }
        public string NextPageKeyAfter(string html, string cid, int curPage) { return null; }

        /// <summary>
        /// 批量路径的防错位闸门。规则表站点是"一个章节 id 对应一个确定地址"，
        /// 地址由我们自己的表给出、不是从页面里二次解析的，所以不存在配对错位；
        /// 但**内容非空**还是要求一下，空页直接丢掉让调用方逐章退回。
        /// </summary>
        public bool HtmlMatchesChapter(string html, ChapterInfo chapter)
        {
            return !string.IsNullOrEmpty(html);
        }

        // ============================================================
        //  工具
        // ============================================================

        private static string Group(string html, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!m.Success || m.Groups.Count < 2) return null;
            string v = Http.StripTags(m.Groups[1].Value);
            v = Regex.Replace(v, "\\s+", " ").Trim();
            return v.Length == 0 ? null : v;
        }

        /// <summary>
        /// 展开目录页模板：%s → 书籍路径，%i → 路径里第一段数字。
        /// 顺序要紧：先把 %i 换掉再换 %s，否则替换出来的路径又会被 %s 规则碰到。
        /// </summary>
        private static string ExpandTemplate(string tpl, string bookPath)
        {
            string s = tpl;
            if (s.IndexOf("%i", StringComparison.Ordinal) >= 0)
            {
                var m = Regex.Match(bookPath ?? "", "\\d+");
                s = s.Replace("%i", m.Success ? m.Value : "");
            }
            if (s.IndexOf("%s", StringComparison.Ordinal) >= 0)
                s = s.Replace("%s", bookPath ?? "");
            return s;
        }

        /// <summary>取 URL 的"书籍路径"，例如 /1_1245/ 或 /book/417022/</summary>
        public static string PathOf(string url)        {
            if (string.IsNullOrEmpty(url)) return "";
            string s = url;
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            if (!s.EndsWith("/", StringComparison.Ordinal)) s += "/";
            var m = Regex.Match(s, "^(https?://[^/]+)(/.*)$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[2].Value : s;
        }

        /// <summary>章节 id = 地址里最后一段的文件名（不含扩展名）</summary>
        public static string IdOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string s = url;
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            s = s.TrimEnd('/');
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s;
        }

        /// <summary>
        /// 相对地址 → 绝对地址（按 HTML/HTTP 的**页面相对**语义，RFC 3986）。
        ///
        /// ★ 不能复用 CoverFetcher.Absolutize：那个是给**封面**写的，语义是
        ///   "相对站点根" —— 它把所有不以 / 开头的地址都拼到域名后面，于是
        ///   `603316.html` 会变成 /603316.html（**丢掉 /1_1245/ 这一层**，
        ///   而实测笔尖中文的章节链接正是这种扁平相对写法），
        ///   并且会把 `../` 直接吃掉。
        ///   章节地址拼错**不会报错**，只会 404 或抓到别的页 —— 最难查的那种。
        /// </summary>
        public static string Absolutize(string url, string pageUrl)
        {
            return ResolveUrl(url, pageUrl);
        }

        /// <summary>标准相对地址解析（先归一 . / ..，再按 base 的目录拼）</summary>
        public static string ResolveUrl(string url, string baseUrl)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = Http.HtmlDecode(url).Trim();

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("//", StringComparison.Ordinal))
                return SchemeOf(baseUrl) + ":" + url;

            UrlBase b;
            if (!TryParseBase(baseUrl, out b)) return url;

            if (url.StartsWith("/", StringComparison.Ordinal))
                return b.Origin + url;

            // 页面相对：基准是 base 的**目录**
            return b.Origin + CollapseDots(b.Path + url);
        }

        private struct UrlBase
        {
            public string Origin;   // http://host
            public string Path;     // /1_1245/   （base 的目录，以 / 结尾）
        }

        private static string SchemeOf(string baseUrl)
        {
            return (!string.IsNullOrEmpty(baseUrl) &&
                    baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ? "https" : "http";
        }

        private static bool TryParseBase(string baseUrl, out UrlBase b)
        {
            b = new UrlBase();
            if (string.IsNullOrEmpty(baseUrl)) return false;
            var m = Regex.Match(baseUrl.Trim(), "^(https?://[^/]+)(/[^?#]*)?", RegexOptions.IgnoreCase);
            if (!m.Success) return false;
            b.Origin = m.Groups[1].Value;
            string p = m.Groups[2].Success ? m.Groups[2].Value : "/";
            int slash = p.LastIndexOf('/');
            b.Path = slash >= 0 ? p.Substring(0, slash + 1) : "/";
            return true;
        }

        /// <summary>归一路径里的 . 与 ..（不会越过域名根）</summary>
        private static string CollapseDots(string path)
        {
            bool absolute = path.StartsWith("/", StringComparison.Ordinal);
            var stack = new List<string>();
            foreach (var seg in path.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..")
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(seg);
            }
            return (absolute ? "/" : "") + string.Join("/", stack.ToArray());
        }
    }
}
