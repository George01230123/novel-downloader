using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TomatoBiquga
{
    /// <summary>
    /// 番茄小说（fanqienovel.com）网页端实现。
    /// 页面里的 window.__INITIAL_STATE__ 带完整目录和正文；
    /// 但正文把常用字替换成了私用区字符（配一个 bytetos 反爬字体），
    /// 这里用内置的“私用区 → 真字”映射表还原。
    /// </summary>
    public class FanqieSite : ISite, IProbeable
    {
        public const string Origin = "https://fanqienovel.com";
        public string Name { get { return "番茄小说"; } }

        /// <summary>
        /// 探活：番茄的目录 JSON 接口很轻，直接探它 ——
        /// 探首页也能通，但目录接口才是这个工具真正要用的那一个，
        /// 而番茄的**风控恰好卡在正文接口上**，目录接口通常是通的。
        /// 这里只判断"站点在不在"，风控留到真正下载时再报（那时提示更准确）。
        /// </summary>
        public string Probe()
        {
            var r = NetDiag.Probe(Origin + "/api/reader/directory/detail?bookId=1", Origin + "/");
            // 番茄对不存在的 bookId 会返回 200 + 错误 JSON，所以只要"请求成功"就算站点可用，
            // 内容是不是有效目录不在这里判断
            return r.Ok ? null : NetDiag.Describe(r);
        }

        /// <summary>为 true 时忽略本地目录缓存</summary>
        public bool ForceRefresh = false;

        /// <summary>
        /// 番茄没有可公开调用的中文搜索接口（搜索结果是浏览器里异步加载的，
        /// 私有接口 /api/author/search/search_book/v1 需要签名，直接请求返回空）。
        /// 所以这里的“搜索”只负责把 book_id / 链接解析出来；
        /// 想按中文书名找书，请用界面上的「浏览器搜索」按钮跳到官网搜索页。
        /// </summary>
        public List<BookInfo> Search(string keyword, Action<string> log)
        {
            var bookId = ExtractBookId(keyword);
            if (bookId == null)
            {
                if (log != null) log("番茄站没有公开的中文搜索接口，请用「浏览器搜索」按钮去官网找到书，再把链接粘贴回来。");
                return new List<BookInfo>();
            }
            var book = new BookInfo
            {
                Site = "fanqie",
                BookId = bookId,
                Title = "番茄书籍 " + bookId,
                Url = Origin + "/page/" + bookId,
            };
            return new List<BookInfo> { book };
        }

        /// <summary>番茄官网搜索页地址（用默认浏览器打开，就是官方的中文搜索）</summary>
        public static string SearchPageUrl(string keyword)
        {
            return Origin + "/search/" + Uri.EscapeDataString((keyword ?? "").Trim());
        }

        /// <summary>
        /// 从各种粘贴内容里抠出 book_id：
        /// 纯数字、/page/xxx、?book_id=xxx、分享口令文本都能认。
        /// 另外 reader/<章节id> 这种链接要换成书籍页再取 book_id（需要联网解析）。
        /// </summary>
        public static string ExtractBookId(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var s = text.Trim();

            // ?book_id=xxx / bookId=xxx / /page/xxx / 纯数字
            var m = Regex.Match(s, @"(?:^|[?&#/])(?:book_id|bookId)=?(\d{6,25})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(s, @"^(\d{6,25})$");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(s, @"/page/(\d{6,25})");
            if (m.Success) return m.Groups[1].Value;

            // reader/<章节id>：需要通过页面解析拿到 book_id
            m = Regex.Match(s, @"/reader/(\d{6,25})");
            if (m.Success) return ResolveBookIdFromChapter(m.Groups[1].Value);

            // 兜底：文本里任意一个长数字（分享口令等）
            m = Regex.Match(s, @"(\d{15,25})");
            if (m.Success) return m.Groups[1].Value;
            return null;
        }

        /// <summary>
        /// 从章节页反查 book_id：Linux 内核那种“章节页”里带 bookId 字段。
        /// 拿不到就返回 null（调用方会提示用书籍页链接）。
        /// </summary>
        public static string ResolveBookIdFromChapter(string chapterId)
        {
            try
            {
                var html = Http.Get(Origin + "/reader/" + chapterId, Origin + "/");
                var m = Regex.Match(html, "\"bookId\":\"?(\\d{6,25})\"?");
                if (m.Success) return m.Groups[1].Value;
            }
            catch
            {
                // 网络/风控失败，交给上层提示
            }
            return null;
        }

        public BookInfo LoadBook(BookInfo item, Action<string> log)
        {
            // 0) 目录缓存
            if (!ForceRefresh)
            {
                var cached = DirCache.Load("fanqie", item.BookId);
                if (cached != null)
                {
                    var at = DirCache.CachedAt("fanqie", item.BookId);
                    var age = at.HasValue ? (DateTime.Now - at.Value).TotalHours : 999;
                    if (age <= DirCache.MaxAgeHours)
                    {
                        if (log != null) log(string.Format("使用本地目录缓存（缓存于 {0}，共 {1} 章）",
                            at.HasValue ? at.Value.ToString("MM-dd HH:mm") : "未知", cached.Chapters.Count));
                        return cached;
                    }
                }
            }

            if (log != null) log("读取番茄书籍信息（" + item.BookId + "）…");

            var book = new BookInfo
            {
                Site = "fanqie",
                BookId = item.BookId,
                Url = Origin + "/page/" + item.BookId,
            };

            // 1) 目录走站点自己的 JSON 接口（没有字符替换，干净）
            var dirUrl = Origin + "/api/reader/directory/detail?bookId=" + item.BookId;
            var dirJson = Http.Get(dirUrl, Origin + "/");
            var dirRoot = ParseJson(dirJson);
            var dirData = GetObj(dirRoot, "data");
            if (dirData == null)
            {
                var keys = dirRoot == null ? "(空)" : string.Join(",", new List<string>(dirRoot.Keys).ToArray());
                throw new Exception("番茄目录接口返回异常（顶层键：" + keys + "），可能改版了");
            }

            var volumes = GetList(dirData, "chapterListWithVolume");
            var volumeNames = new List<object>();
            var vnRaw = GetList(dirData, "volumeNameList");
            if (vnRaw != null) foreach (var x in vnRaw) volumeNames.Add(x);
            if (volumes == null) throw new Exception("番茄目录接口里没有 chapterListWithVolume");
            bool anyVolume = false;

            int order = 0;
            int volIndex = 0;
            foreach (var volObj in volumes)
            {
                var vol = volObj as System.Collections.IEnumerable;
                if (vol == null) continue;
                var items = new List<object>();
                foreach (var x in vol) items.Add(x);
                if (items.Count == 0) continue;
                anyVolume = true;
                var vname = volIndex < volumeNames.Count
                    ? Convert.ToString(volumeNames[volIndex]) : ("第 " + (volIndex + 1) + " 卷");
                volIndex++;
                book.Chapters.Add(new ChapterInfo
                {
                    Id = "",
                    Title = "【" + vname + "】",
                    IsVolume = true,
                    Selected = false,
                    Order = order++,
                });
                foreach (var entry in items)
                {
                    var c = entry as Dictionary<string, object>;
                    if (c == null) continue;
                    var cid = GetStr(c, "itemId");
                    if (string.IsNullOrEmpty(cid)) continue;
                    book.Chapters.Add(new ChapterInfo
                    {
                        Id = cid,
                        Title = CleanTitle(GetStr(c, "title"), book.Chapters.Count),
                        Order = order++,
                    });
                }
            }

            // 兜底：没有分卷结构时用 allItemIds
            if (!anyVolume)
            {
                var ids = GetList(dirData, "allItemIds");
                if (ids != null)
                {
                    order = 0;
                    foreach (var it in ids)
                    {
                        var cid = Convert.ToString(it);
                        if (string.IsNullOrEmpty(cid)) continue;
                        book.Chapters.Add(new ChapterInfo
                        {
                            Id = cid,
                            Title = "第 " + (book.Chapters.Count + 1) + " 章",
                            Order = order++,
                        });
                    }
                }
            }

            if (book.Chapters.Count == 0) throw new Exception("没解析到番茄章节目录");
            int volumeCount = volIndex;

            // 2) 书名/作者/简介从页面取（这几项是正常文字）
            try
            {
                var html = Http.Get(book.Url, Origin + "/");
                var stateText = ExtractBalanced(html, "__INITIAL_STATE__");
                if (stateText != null)
                {
                    var page = GetObj(ParseJson(stateText), "page");
                    if (page != null)
                    {
                        book.Title = GetStr(page, "bookName");
                        book.Author = GetStr(page, "author");
                        book.Desc = GetStr(page, "abstract");
                        book.Category = GetStr(page, "category");
                        book.Status = GetStr(page, "creationStatus") == "1" ? "已完结" : "连载中";

                        // 封面：番茄的字段名是 thumbUri / thumbUrl（实测 2026-10 的详情页里
                        // 两个都在，thumbUri 是站内相对路径风格、thumbUrl 是绝对地址）。
                        // audioThumbUrl / coverUrl 是给别的书型留的兜底，实测没命中。
                        var cover = GetStr(page, "thumbUrl");
                        if (string.IsNullOrEmpty(cover)) cover = GetStr(page, "thumbUri");
                        if (string.IsNullOrEmpty(cover)) cover = GetStr(page, "audioThumbUrl");
                        if (string.IsNullOrEmpty(cover)) cover = GetStr(page, "coverUrl");
                        if (!string.IsNullOrEmpty(cover))
                            book.CoverUrl = CoverFetcher.Absolutize(cover, book.Url);

                        // 字数：番茄在 page 里给 wordNumber，能省掉本地统计
                        var wn = GetStr(page, "wordNumber");
                        long words;
                        if (!string.IsNullOrEmpty(wn) && long.TryParse(wn, out words)) book.WordCount = words;
                    }
                }
            }
            catch { /* 拿不到就用兜底名字 */ }
            if (string.IsNullOrEmpty(book.Title)) book.Title = "番茄书籍 " + item.BookId;

            int chapterCount = 0;
            foreach (var c in book.Chapters) if (!c.IsVolume) chapterCount++;
            if (log != null) log(string.Format("《{0}》 {1} —— 共 {2} 章（{3} 卷）",
                book.Title, string.IsNullOrEmpty(book.Author) ? "未知" : book.Author, chapterCount, volumeCount));

            DirCache.Save(book);
            if (log != null) log("目录已缓存到本地（番茄目录接口很轻，缓存主要为离线查看）。");
            return book;
        }

        /// <summary>标题里若混入私用区字符，尽量清掉</summary>
        private static string CleanTitle(string title, int index)
        {
            if (string.IsNullOrEmpty(title)) return "第 " + index + " 章";
            var cleaned = FontMap.Decode(title);
            var sb = new StringBuilder();
            foreach (var c in cleaned)
            {
                if (c >= '\uE000' && c <= '\uF8FF') continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim();
            return s.Length == 0 ? ("第 " + index + " 章") : s;
        }

        public string LoadChapter(BookInfo book, ChapterInfo chapter, Action<string> log)
        {
            if (string.IsNullOrEmpty(chapter.Id)) return "";
            var html = Http.Get(Origin + "/reader/" + chapter.Id, book.Url);

            // 番茄对正文页有风控：请求频繁时会返回“验证码中间页”
            if (html.Contains("验证码中间页") || html.Contains("captcha/index.js"))
            {
                throw new Exception(
                    "番茄返回了风控验证页（正文接口需要过验证码）。" +
                    "这通常是因为短时间内请求太多，等几分钟到几小时会自行恢复；" +
                    "目录可以正常获取，正文建议少量多次下载，或改用番茄官方客户端/RSS。");
            }

            var stateText = ExtractBalanced(html, "__INITIAL_STATE__");
            if (stateText == null) throw new Exception("正文页面结构变了（没有 __INITIAL_STATE__），番茄可能改版");
            var state = ParseJson(stateText);
            var reader = GetObj(state, "reader");
            if (reader == null) throw new Exception("正文页面没有 reader 数据");
            var data = GetObj(reader, "chapterData");
            if (data == null) throw new Exception("正文页面没有 chapterData");

            var content = GetStr(data, "content");
            if (string.IsNullOrEmpty(content)) throw new Exception("该章正文为空（可能需要付费或已下架）");

            // 番茄对未登录 / 高频访问只给“试读片段”（实测约 200 字），必须明确告知，
            // 否则会把残缺内容当成完整章节存下来。
            var plainLen = Regex.Replace(content, "<[^>]+>", "").Trim().Length;
            if (plainLen < 400)
            {
                throw new Exception(string.Format(
                    "番茄这一章只返回了 {0} 字的试读片段（完整正文需要登录或官方接口）。已跳过，避免保存残缺章节。",
                    plainLen));
            }

            // 注意：番茄网页版会对常见字做“换成形近字”的保护处理（例：在->茌、特->恃），
            // 字体是固定的一套，浏览器里能正常阅读，但程序取到的就是这些形近字本身。
            var decoded = FontMap.Decode(content);
            var text = HtmlToText(decoded);
            var t = GetStr(data, "title");
            if (!string.IsNullOrEmpty(t)) chapter.Title = CleanTitle(t, chapter.Order + 1);
            return text;
        }

        // ------------------------------------------------------ 工具

        private static string HtmlToText(string html)
        {
            var withBreaks = Regex.Replace(html, @"</p>\s*<p[^>]*>", "\n", RegexOptions.IgnoreCase);
            var text = Http.StripTags(withBreaks).Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = Regex.Replace(raw, @"[ \t\u3000]+", " ").Trim();
                if (line.Length == 0) continue;
                lines.Add(line);
            }
            return string.Join("\n", lines);
        }

        /// <summary>从 HTML 里抠出 `__INITIAL_STATE__ = { ... }` 的平衡大括号片段</summary>
        public static string ExtractBalanced(string html, string marker)
        {
            int i = html.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = html.IndexOf('{', i);
            if (start < 0) return null;
            int depth = 0;
            bool inStr = false, esc = false;
            char quote = '"';
            for (int j = start; j < html.Length; j++)
            {
                char c = html[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == quote) inStr = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inStr = true; quote = c; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return html.Substring(start, j - start + 1);
                }
            }
            return null;
        }

        public static Dictionary<string, object> ParseJson(string json)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 200;
            return ser.Deserialize<Dictionary<string, object>>(json);
        }

        public static Dictionary<string, object> GetObj(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        public static System.Collections.IEnumerable GetList(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as System.Collections.IEnumerable;
            return null;
        }

        public static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

    }
}
