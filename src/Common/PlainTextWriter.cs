using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 「导出纯文本 TXT」：**不含任何内部标记**的干净小说 txt。
    ///
    /// 为什么要单独一个导出，而不是直接把下载得到的 txt 给人：
    ///   下载用的那本 txt 里必须留两样东西 ——
    ///     · `#header-zone:2048`：表头的定长占位（后面要原地重写"本次下载 N 章"）；
    ///     · `<!--c:id-->`：每章的锚点（更新新章节要插对位置、补齐缺章要定位、断点续传要认）。
    ///   删掉它们，"更新新章节 / 补齐缺章 / 续传"会**静默失效** —— 所以下载文件不能动。
    ///   但用户拿去阅读/传阅/丢进阅读器时，这些标记是噪音。
    ///   于是"另存一份干净的"是正解：原文件不动，导出一份纯文本。
    ///
    /// 格式（和常见的小说 txt 一样）：
    ///   书名
    ///   作者：xxx
    ///   （空行）
    ///   第一章 标题
    ///   （空行）
    ///   正文…
    ///   （空行）
    ///   第二章 标题
    ///   ...
    /// </summary>
    internal static class PlainTextWriter
    {
        /// <summary>写一份干净的 txt。返回写入的章数。</summary>
        public static int Write(string path, BookInfo book, IList<ChapterInfo> chapters,
            IList<string> volumeTitles)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 不能为空");
            if (book == null) throw new ArgumentException("book 不能为空");

            var items = new List<ChapterInfo>();
            var vols = new List<string>();
            for (int i = 0; i < chapters.Count; i++)
            {
                var c = chapters[i];
                if (c == null || c.IsVolume) continue;
                if (string.IsNullOrEmpty(c.Text)) continue;
                items.Add(c);
                string v = null;
                if (volumeTitles != null && i < volumeTitles.Count) v = volumeTitles[i];
                vols.Add(string.IsNullOrEmpty(v) ? null : v.Trim());
            }
            if (items.Count == 0)
                throw new Exception("这本书还没有任何已下载的正文，先下载再导出。");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder(1 << 20);
            sb.Append(book.Title).Append("\r\n");
            sb.Append("作者：").Append(string.IsNullOrEmpty(book.Author) ? "未知" : book.Author).Append("\r\n");
            sb.Append("\r\n");

            string lastVol = null;
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                string vol = vols[i];
                if (vol != null && !string.Equals(vol, lastVol, StringComparison.Ordinal))
                {
                    lastVol = vol;
                    sb.Append("【").Append(vol).Append("】").Append("\r\n\r\n");
                }

                sb.Append(ChapterTitle(c)).Append("\r\n\r\n");
                sb.Append(CleanBody(c.Text)).Append("\r\n\r\n");
            }

            // 写到临时文件再换名：中途失败不会留下半截文件
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return items.Count;
        }

        /// <summary>
        /// 章节标题：去掉标题里可能夹带的站名后缀 / 多余空白。
        /// 例："第一章 我有三个相宫" 原样；"第一章 xxx_笔尖中文" → "第一章 xxx"
        /// </summary>
        internal static string ChapterTitle(ChapterInfo c)
        {
            if (c == null) return "";
            string t = c.Title ?? "";
            int bar = t.IndexOf('_');
            if (bar > 0) t = t.Substring(0, bar);
            t = t.Replace("\r", " ").Replace("\n", " ").Trim();
            while (t.IndexOf("  ", StringComparison.Ordinal) >= 0)
                t = t.Replace("  ", " ");
            return t;
        }

        /// <summary>
        /// 正文清洗：统一换行、去掉每行首尾空白、压缩连续空行，**并丢掉纯标记行**。
        ///
        /// ★ 最后一条是端到端实测抓出来的（不是在单测里想到的）：
        ///   导出 30 章后检查产物，发现里面还有 `&lt;!--c:12345--&gt;`。
        ///   原因是 `ParseTxtIntoChapters` 反解已下载的 txt 时，
        ///   锚点行会被并进**上一章的正文末尾** —— 于是"干净副本"里带着内部标记，
        ///   用户会以为文件坏了。
        ///
        /// **不做**任何"删广告"式的正文替换：那会误杀正文（这个项目踩过）。
        /// </summary>
        internal static string CleanBody(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r\n", "\n").Replace('\r', '\n');

            // ★ 先把**行内**的标记摘掉，再按行处理。
            //   断言抓到的：`#progress-ok:1838:3619 正文` 这种"标记后面还跟着正文"的行，
            //   如果按"整行以 #progress 开头就丢"，会把正文一起丢掉（实测期望"正文"、实际空）。
            //   所以分两步：标记后面还有内容 → 只摘标记；整行就是标记 → 丢整行。
            s = System.Text.RegularExpressions.Regex.Replace(s, "#progress[-\\w]*:[^\\s]*", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, "#header-zone:\\d+", "");

            var lines = s.Split('\n');
            var sb = new StringBuilder(s.Length);
            int blanks = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (IsMarkerLine(line)) continue;      // 剩下"纯标记行"直接丢
                if (line.Length == 0)
                {
                    blanks++;
                    // 连续空行压成一行
                    if (blanks > 1) continue;
                }
                else blanks = 0;
                sb.Append(line).Append("\r\n");
            }
            // 两端都要裁：摘掉行内标记后，本来"只有标记"的那一行会变成空行留在开头
            return sb.ToString().Trim('\r', '\n');
        }

        /// <summary>这一行是不是"只由内部标记构成"（章节锚点 / 表头占位 / 进度标记）</summary>
        internal static bool IsMarkerLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string t = line.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("<!--c:", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal))
                return true;
            if (t.StartsWith("#header-zone:", StringComparison.Ordinal)) return true;
            if (t.StartsWith("#progress", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
