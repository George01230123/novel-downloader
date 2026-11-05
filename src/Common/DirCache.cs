using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TomatoBiquga
{
    /// <summary>
    /// 目录缓存：把已经遍历过的章节目录存到本地，二次载入或换电脑重启后可直接复用，
    /// 免得每次都要重新走一遍站点（biquga 走完一本 700 章的书要 7~8 分钟）。
    /// </summary>
    public static class DirCache
    {
        /// <summary>缓存多久算过期（小时）；超期仍会询问式复用，由调用方决定</summary>
        public static double MaxAgeHours = 72;

        private static string CacheDir
        {
            get
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string PathFor(string site, string key)
        {
            return Path.Combine(CacheDir, site + "_" + Http.SafeFileName(key) + ".json");
        }

        /// <summary>
        /// 以共享方式读文本，并在被占用时重试几次。
        /// 慢一点没关系 —— 读缓存是"顺路优化"，绝不能因为它抛异常把主流程带崩。
        /// </summary>
        internal static string ReadAllTextShared(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (IOException)
                {
                    if (attempt >= 4) throw;
                    System.Threading.Thread.Sleep(120 * (attempt + 1));
                }
            }
        }

        private class CachedChapter
        {
            public string id { get; set; }
            public string title { get; set; }
            public bool vol { get; set; }
        }

        private class CachedBook
        {
            public string site { get; set; }
            public string title { get; set; }
            public string author { get; set; }
            public string category { get; set; }
            public string status { get; set; }
            public string desc { get; set; }
            public string url { get; set; }
            public string dir { get; set; }
            public string bookId { get; set; }
            public string savedAt { get; set; }
            // 封面与字数也要缓存：否则"目录缓存命中"的那次载入会把它们丢成空，
            // 表现为"第一次下载有封面、第二次（走缓存）就没封面了"。
            // 老缓存文件里没有这两个字段 → 反序列化后为 null/0，是安全的降级。
            public string coverUrl { get; set; }
            public long wordCount { get; set; }
            public List<CachedChapter> chapters { get; set; }
        }

        /// <summary>读取缓存；不存在或解析失败返回 null</summary>
        public static BookInfo Load(string site, string key)
        {
            try
            {
                var path = PathFor(site, key);
                if (!File.Exists(path)) return null;
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                // 用 FileShare.ReadWrite 打开：缓存文件可能正被**另一个进程**写入
                // （比如界面在跑离线预抓、同时命令行工具在读），
                // File.ReadAllText 默认只允许共享读，会直接抛
                // "文件正由另一进程使用"。共享打开 + 失败重试更稳。
                var cb = ser.Deserialize<CachedBook>(ReadAllTextShared(path));
                if (cb == null || cb.chapters == null || cb.chapters.Count == 0) return null;

                var book = new BookInfo
                {
                    Site = cb.site,
                    Title = cb.title,
                    Author = cb.author,
                    Category = cb.category,
                    Status = cb.status,
                    Desc = cb.desc,
                    Url = cb.url,
                    Dir = cb.dir,
                    BookId = cb.bookId,
                    CoverUrl = cb.coverUrl ?? "",
                    WordCount = cb.wordCount,
                };
                int i = 0;
                foreach (var c in cb.chapters)
                {
                    book.Chapters.Add(new ChapterInfo
                    {
                        Id = c.id ?? "",
                        Title = c.title ?? "",
                        IsVolume = c.vol,
                        Selected = !c.vol,
                        Order = i++,
                    });
                }
                book.Chapters.Sort(delegate (ChapterInfo a, ChapterInfo b) { return a.Order.CompareTo(b.Order); });
                // 旧缓存里可能存着站点给的坏简介（例如 og:description 是导航残留），读出来时清一次
                book.Desc = SanitizeDesc(book.Desc);
                return book;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把缓存时间一起返回（用于在界面上显示“缓存于 …”）</summary>
        public static DateTime? CachedAt(string site, string key)
        {
            try
            {
                var path = PathFor(site, key);
                if (!File.Exists(path)) return null;
                return File.GetLastWriteTime(path);
            }
            catch { return null; }
        }

        public static void Save(BookInfo book)
        {
            try
            {
                var cb = new CachedBook
                {
                    site = book.Site,
                    title = book.Title,
                    author = book.Author,
                    category = book.Category,
                    status = book.Status,
                    desc = book.Desc,
                    url = book.Url,
                    dir = book.Dir,
                    bookId = book.BookId,
                    savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    coverUrl = book.CoverUrl,
                    wordCount = book.WordCount,
                    chapters = new List<CachedChapter>(),
                };
                foreach (var c in book.Chapters)
                    cb.chapters.Add(new CachedChapter { id = c.Id, title = c.Title, vol = c.IsVolume });

                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var key = KeyFor(book);
                var path = PathFor(book.Site, key);
                AtomicFile.WriteAllText(path, ser.Serialize(cb), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 缓存失败不影响主流程
            }
        }

        public static void Clear()
        {
            try
            {
                foreach (var f in Directory.GetFiles(CacheDir, "*.json")) File.Delete(f);
            }
            catch { }
        }

        public static int Count
        {
            get
            {
                try { return Directory.GetFiles(CacheDir, "*.json").Length; }
                catch { return 0; }
            }
        }

        /// <summary>缓存键：biquga 用书籍目录，番茄用 book_id</summary>
        public static string KeyFor(BookInfo book)
        {
            if (book.Site == "fanqie") return book.BookId;
            return (book.Dir ?? "").Replace("/", "");
        }

        /// <summary>清掉站点塞进来的垃圾简介（导航残留等），保持 txt 表头干净</summary>
        private static string SanitizeDesc(string desc)
        {
            if (string.IsNullOrEmpty(desc)) return "";
            var s = desc.Trim();
            var cleaned = BiqugaSite.CleanDesc(s);
            return cleaned;
        }
    }
}
