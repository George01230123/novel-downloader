using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// EPUB 3 导出器。
    ///
    /// 为什么零依赖手写：这个项目的卖点是"免安装单文件、零第三方依赖"，
    /// 引一个 EPUB 库（比如 EpubSharp）就要跟着带 dll，不符合目标。
    /// 而 EPUB 本身只是个有约定的 zip（mimetype + container.xml + OPF + XHTML），
    /// .NET Framework 4.5+ 自带 ZipArchive，够用了。
    ///
    /// 规范上必须注意的两点（踩了就有的阅读器打不开）：
    ///   1. `mimetype` 必须是**第一个**条目，且**不压缩**存放（NoCompression）；
    ///   2. 所有 XML/XHTML 必须是 UTF-8 且转义正确，否则解析直接失败。
    /// </summary>
    public static class EpubWriter
    {
        /// <summary>写一本 EPUB。chapters 里 Text 为空的章节会被跳过（未下载的章）。</summary>
        public static void Write(string path, BookInfo book, IList<ChapterInfo> chapters, byte[] coverBytes, string coverExt)
        {
            Write(path, book, chapters, coverBytes, coverExt, null);
        }

        /// <summary>
        /// 写一本 EPUB，并**按分卷分组目录**。
        ///
        /// volumeTitles：与 chapters 一一对应，非 null 表示"这一项是卷标题"。
        /// 为什么单独给一个参数：ChapterInfo.IsVolume 只在**全量目录**里有意义，
        /// 而导出时传进来的往往是"已经下载了正文的那些章"（分卷行被过滤掉了），
        /// 这时靠单看 chapters 已经推不出卷边界了。所以由调用方把原始信息带进来。
        /// 传 null 就是不分卷（和不带这个参数的重载一样）。
        /// </summary>
        public static void Write(string path, BookInfo book, IList<ChapterInfo> chapters,
            byte[] coverBytes, string coverExt, IList<string> volumeTitles)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 不能为空");
            if (book == null) throw new ArgumentException("book 不能为空");
            if (chapters == null) throw new ArgumentException("chapters 不能为空");

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
            if (items.Count == 0) throw new Exception("这本书还没有任何已下载的正文，先下载再导出 EPUB。");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string bookId = "urn:uuid:" + MakeUuid(book);

            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // 1) mimetype —— 必须第一个、必须不压缩
                AddText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);

                // 2) 容器描述
                AddText(zip, "META-INF/container.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
                    "  <rootfiles>\n" +
                    "    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n" +
                    "  </rootfiles>\n" +
                    "</container>\n", CompressionLevel.Optimal);

                // 3) 章节 XHTML
                var names = new List<string>();
                for (int i = 0; i < items.Count; i++)
                {
                    string name = string.Format("OEBPS/text/chapter{0:D4}.xhtml", i + 1);
                    names.Add(name);
                    AddText(zip, name, ChapterXhtml(book, items[i]), CompressionLevel.Optimal);
                }

                // 4) 目录（nav）—— 有分卷就按卷嵌套
                AddText(zip, "OEBPS/nav.xhtml", NavXhtml(book, items, names, vols), CompressionLevel.Optimal);

                // 5) 样式
                AddText(zip, "OEBPS/style.css",
                    "body { line-height: 1.6; margin: 1em; }\n" +
                    "h1 { font-size: 1.4em; margin: 1.2em 0 0.8em; text-align: center; }\n" +
                    "p { text-indent: 2em; margin: 0.5em 0; }\n" +
                    ".meta { color: #666; font-size: 0.9em; text-align: center; }\n" +
                    ".cover { text-align: center; margin: 2em 0; }\n" +
                    ".cover img { max-width: 100%; }\n",
                    CompressionLevel.Optimal);

                // 6) 封面（有才写）
                bool hasCover = coverBytes != null && coverBytes.Length > 0;
                string coverName = null, coverType = null;
                if (hasCover)
                {
                    coverExt = NormalizeImageExt(coverExt, coverBytes);
                    coverType = coverExt == "png" ? "image/png" : "image/jpeg";
                    coverName = "OEBPS/images/cover." + coverExt;
                    AddBytes(zip, coverName, coverBytes);
                    AddText(zip, "OEBPS/text/cover.xhtml",
                        CoverXhtml(book, coverName.Substring("OEBPS/".Length), coverType),
                        CompressionLevel.Optimal);
                }

                // 7) OPF（清单 + 阅读顺序 + 元数据）
                AddText(zip, "OEBPS/content.opf",
                    Opf(book, items, names, bookId, now, coverName, coverType, hasCover),
                    CompressionLevel.Optimal);
            }

            // ★ mimetype 必须是 **stored（不压缩）**（OCF 规范；epubcheck 会因此报错，
            //   个别严格的阅读器会判成损坏文件）。
            //
            //   上面虽然写了 CompressionLevel.NoCompression，但 **.NET Framework 的
            //   ZipArchive 会忽略它** —— 实测导出的 epub 里第一个条目 method=8 (deflate)。
            //   这是框架行为不是笔误（我单独做过最小复现：只写 mimetype 的 zip，仍然是 8）。
            //   所以这里在收尾时手工把第一个条目重写成 stored。
            ForceStoredFirstEntry(tmp);

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>
        /// 把 ZIP 的**第一个条目**（本项目的 epub 里就是 mimetype）改写成 stored（不压缩）。
        ///
        /// 为什么要手工做：.NET Framework 的 ZipArchive 忽略 NoCompression（见上）。
        /// 做法：解出原始字节 → 用 method=0 重建第一段 → 后面所有偏移整体平移 →
        ///       同步修改中央目录里每个条目的 offset、以及第一个条目的 method/size →
        ///       最后改 EOCD 里的"中央目录起始偏移"。
        ///
        /// 安全性上刻意做了几件事：
        ///   · 只认第一个条目叫 `mimetype`（名字/内容不符就原样退出，绝不动别的 zip）；
        ///   · 已经是 stored 就直接返回；
        ///   · 改完**必须能重新被 ZipFile 打开**，打不开就放弃修改（宁可 epubcheck 报警，
        ///     也不能交出一个损坏的文件）；
        ///   · 任何异常都吞掉并保留原文件 —— 这是个"锦上添花"的合规修复，
        ///     不该让导出失败。
        /// </summary>
        private static void ForceStoredFirstEntry(string zipPath)
        {
            try
            {
                var b = File.ReadAllBytes(zipPath);
                if (b.Length < 30) return;
                if (b[0] != 'P' || b[1] != 'K' || b[2] != 3 || b[3] != 4) { return; }

                int method = b[8] | (b[9] << 8);
                if (method == 0) return;                                  // 已经是 stored

                int nameLen = b[26] | (b[27] << 8);
                int extraLen = b[28] | (b[29] << 8);
                if (30 + nameLen > b.Length) return;
                var name = Encoding.ASCII.GetString(b, 30, nameLen);
                if (name != "mimetype") return;                           // 只动 mimetype

                long compSize = BitConverter.ToUInt32(b, 18);
                long uncompSize = BitConverter.ToUInt32(b, 22);
                int dataStart = 30 + nameLen + extraLen;
                if (compSize <= 0 || dataStart + compSize > b.Length) return;

                // 解出原始内容（mimetype 内容固定，很小）
                byte[] raw = Inflate(b, dataStart, (int)compSize);
                if (raw == null || raw.Length == 0) return;
                if (raw.Length != uncompSize) { /* 大小不符也不致命，继续用 raw.Length */ }

                // 新前缀：本地头（method=0、size=raw.Length）+ 名字 + 空 extra + 原始数据
                var head = new byte[30];
                head[0] = 0x50; head[1] = 0x4B; head[2] = 3; head[3] = 4;   // PK\3\4
                head[4] = b[4]; head[5] = b[5];                            // version needed
                head[6] = b[6]; head[7] = b[7];                            // flags
                head[8] = 0; head[9] = 0;                                  // method = stored
                // 时间/日期沿用原来的
                for (int i = 10; i < 14; i++) head[i] = b[i];
                PutU32(head, 14, Crc32(raw));                              // crc32
                PutU32(head, 18, (uint)raw.Length);                        // comp size
                PutU32(head, 22, (uint)raw.Length);                        // uncomp size
                head[26] = (byte)(nameLen & 0xFF); head[27] = (byte)(nameLen >> 8);
                head[28] = 0; head[29] = 0;                                // extra len = 0

                var nameBytes = Encoding.ASCII.GetBytes("mimetype");
                var newFirst = new byte[head.Length + nameBytes.Length + raw.Length];
                Buffer.BlockCopy(head, 0, newFirst, 0, head.Length);
                Buffer.BlockCopy(nameBytes, 0, newFirst, head.Length, nameBytes.Length);
                Buffer.BlockCopy(raw, 0, newFirst, head.Length + nameBytes.Length, raw.Length);

                int oldFirstLen = dataStart + (int)compSize;
                int delta = newFirst.Length - oldFirstLen;                 // 后续内容要平移这么多

                // 拼出新文件
                var result = new byte[b.Length + delta];
                Buffer.BlockCopy(newFirst, 0, result, 0, newFirst.Length);
                Buffer.BlockCopy(b, oldFirstLen, result, newFirst.Length, b.Length - oldFirstLen);

                // 找到 EOCD，把中央目录整体重定位。
                //
                // ★ 关键：**EOCD 自己也搬家了**。
                //   EOCD 在第一个条目后面，所以它跟着 delta 一起平移；
                //   而它内部记的"CD 起始偏移"是**绝对偏移**，也必须一起 +delta。
                //   我第一版忘了修这个字段，于是拿着旧的 CD 偏移去新数组里找中央目录头，
                //   落在别处、签名对不上，白白放弃修复（文件保持 deflate）。
                //
                //   顺序很重要：先把 CD 偏移改成新值，再按新值去走中央目录。
                int eocd = FindEocd(result);
                if (eocd < 0) { RestoreOriginal(zipPath, b); return; }
                long cdAt = BitConverter.ToUInt32(result, eocd + 16) + delta;
                if (cdAt < 0 || cdAt + 46 > eocd) { RestoreOriginal(zipPath, b); return; }
                PutU32(result, eocd + 16, (uint)cdAt);
                if (!PatchCentralDirectory(result, eocd, (int)cdAt, delta)) { RestoreOriginal(zipPath, b); return; }

                // ★ 改完必须还能被正常打开，否则放弃（宁可不合规，也不能交损坏文件）
                File.WriteAllBytes(zipPath, result);
                try
                {
                    using (var z = ZipFile.OpenRead(zipPath))
                    {
                        if (z.Entries.Count == 0 || z.Entries[0].FullName != "mimetype")
                        { RestoreOriginal(zipPath, b); return; }            // 回滚
                        using (var s = z.Entries[0].Open())
                        {
                            var buf = new byte[64];
                            int n = s.Read(buf, 0, buf.Length);
                            var ok = n > 0 && Encoding.ASCII.GetString(buf, 0, n).StartsWith("application/epub+zip",
                                StringComparison.Ordinal);
                            if (!ok) File.WriteAllBytes(zipPath, b);            // 回滚
                        }
                    }
                }
                catch (Exception) { try { File.WriteAllBytes(zipPath, b); } catch { } }
            }
            catch (Exception) { }
        }

        /// <summary>把文件恢复成原始字节（改动不可信时的回滚路径）</summary>
        private static void RestoreOriginal(string zipPath, byte[] original)
        {
            try { File.WriteAllBytes(zipPath, original); } catch { }
        }

        private static void PutU32(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v & 0xFF);
            b[at + 1] = (byte)((v >> 8) & 0xFF);
            b[at + 2] = (byte)((v >> 16) & 0xFF);
            b[at + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static int FindEocd(byte[] b)
        {
            for (int i = b.Length - 22; i >= 0 && i > b.Length - 22 - 65536; i--)
                if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 5 && b[i + 3] == 6) return i;
            return -1;
        }

        /// <summary>修中央目录里每个条目的本地头偏移；第一个条目（mimetype）另把 method/size 改成 stored</summary>
        /// <summary>
        /// 走一遍中央目录：把每个条目的"本地头偏移"平移 delta；
        /// 第一个条目（mimetype）另外把压缩方法改成 stored。
        /// <paramref name="cdAt"/> 是**已经平移好**的中央目录起始偏移。
        /// </summary>
        private static bool PatchCentralDirectory(byte[] b, int eocd, int cdAt, int delta)
        {
            int count = b[eocd + 10] | (b[eocd + 11] << 8);
            int off = cdAt;
            for (int i = 0; i < count; i++)
            {
                // 边界检查必须做全：读条目长度字段前确认 46 字节够，
                // 算完条目总长还要确认没越过 EOCD —— 否则越界会踩坏 EOCD 记录，
                // ZipFile 复读时报"不支持拆分或跨区的存档"。
                if (off < 0 || off + 46 > eocd) return false;
                if (!(b[off] == 0x50 && b[off + 1] == 0x4B && b[off + 2] == 1 && b[off + 3] == 2)) return false;

                int nameLen = b[off + 28] | (b[off + 29] << 8);
                int extraLen = b[off + 30] | (b[off + 31] << 8);
                int cmtLen = b[off + 32] | (b[off + 33] << 8);
                int entryLen = 46 + nameLen + extraLen + cmtLen;
                if (entryLen <= 46 || off + entryLen > eocd) return false;

                if (i == 0)
                {
                    // mimetype：压缩方法改 stored，两个 size 都改成**未压缩**长度
                    // （未压缩长度在中央目录的 +24；+20 是压缩长度）
                    uint rawLen = BitConverter.ToUInt32(b, off + 24);
                    b[off + 10] = 0; b[off + 11] = 0;                 // method = 0 (stored)
                    PutU32(b, off + 20, rawLen);
                    PutU32(b, off + 24, rawLen);
                }
                else
                {
                    // 第一个条目之后的内容都整体平移了 delta，本地头偏移要跟着改
                    uint local = BitConverter.ToUInt32(b, off + 42);
                    PutU32(b, off + 42, (uint)(local + delta));
                }
                off += entryLen;
            }
            return true;
        }

        /// <summary>
        /// 解出第一段的数据。
        ///
        /// ★ 两种 deflate 包装都要试：
        ///   · **raw deflate**（无头）—— .NET 的 ZipArchive 用的是这一种；
        ///   · **zlib 包装**（2 字节头）—— 有些 zip 实现会带。
        /// 我第一版只按 zlib 写（跳过 2 字节），结果对自家产物直接解压失败 ——
        /// ZipArchive 写的是 raw deflate，跳 2 字节就把流读坏了。
        /// 现在先按 raw 试，失败再按 zlib 试。
        /// </summary>
        private static byte[] Inflate(byte[] b, int start, int len)
        {
            var raw = InflateAt(b, start, len);
            if (raw != null) return raw;
            return InflateAt(b, start + 2, len - 2);
        }

        private static byte[] InflateAt(byte[] b, int start, int len)
        {
            if (len <= 0 || start < 0 || start + len > b.Length) return null;
            try
            {
                using (var ms = new MemoryStream(b, start, len))
                using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    var buf = new byte[4096];
                    int n;
                    while ((n = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
                    var r = outMs.ToArray();
                    return r.Length > 0 ? r : null;
                }
            }
            catch { return null; }
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[i] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            foreach (var by in data) crc = _crcTable[(crc ^ by) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>EPUB 里章节标题用普通文本即可（在 XHTML 里再转义）</summary>
        public static string ChapterTitle(ChapterInfo c)
        {
            if (c == null) return "";
            var t = c.Title;
            if (string.IsNullOrEmpty(t)) return "（无标题）";
            return t.Trim();
        }

        // ------------------------------------------------------------ 内部

        private static string Opf(BookInfo book, List<ChapterInfo> items, List<string> names,
            string bookId, string now, string coverName, string coverType, bool hasCover)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"zh-CN\">\n");
            sb.Append("  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n");
            sb.Append("    <dc:identifier id=\"bookid\">").Append(X(bookId)).Append("</dc:identifier>\n");
            sb.Append("    <dc:title>").Append(X(book.Title)).Append("</dc:title>\n");
            sb.Append("    <dc:language>zh-CN</dc:language>\n");
            sb.Append("    <dc:creator>").Append(X(string.IsNullOrEmpty(book.Author) ? "未知" : book.Author)).Append("</dc:creator>\n");
            // dc:date：EPUB3 只要求 dcterms:modified，但 Calibre / KOReader 这类书库
            // 是按 dc:date 显示"更新于"并据此排序的 —— 缺了它书架里那一列就是空的。
            sb.Append("    <dc:date>").Append(X(now)).Append("</dc:date>\n");
            if (!string.IsNullOrEmpty(book.Desc))
                sb.Append("    <dc:description>").Append(X(Shorten(book.Desc, 1000))).Append("</dc:description>\n");
            if (!string.IsNullOrEmpty(book.Url))
                sb.Append("    <dc:source>").Append(X(book.Url)).Append("</dc:source>\n");
            sb.Append("    <meta property=\"dcterms:modified\">").Append(X(now)).Append("</meta>\n");
            sb.Append("    <meta name=\"generator\" content=\"novel-downloader\"/>\n");
            sb.Append("  </metadata>\n");

            sb.Append("  <manifest>\n");
            sb.Append("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n");
            sb.Append("    <item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
            if (hasCover)
            {
                sb.Append("    <item id=\"cover-image\" href=\"").Append(X(coverName.Substring("OEBPS/".Length)))
                  .Append("\" media-type=\"").Append(coverType).Append("\" properties=\"cover-image\"/>\n");
                sb.Append("    <item id=\"cover\" href=\"text/cover.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            }
            for (int i = 0; i < items.Count; i++)
                sb.Append("    <item id=\"c").Append(i + 1).Append("\" href=\"text/chapter").Append((i + 1).ToString("D4"))
                  .Append(".xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            sb.Append("  </manifest>\n");

            sb.Append("  <spine>\n");
            if (hasCover) sb.Append("    <itemref idref=\"cover\"/>\n");
            sb.Append("    <itemref idref=\"nav\"/>\n");
            for (int i = 0; i < items.Count; i++)
                sb.Append("    <itemref idref=\"c").Append(i + 1).Append("\"/>\n");
            sb.Append("  </spine>\n");
            sb.Append("</package>\n");
            return sb.ToString();
        }

        private static string NavXhtml(BookInfo book, List<ChapterInfo> items, List<string> names)
        {
            return NavXhtml(book, items, names, null);
        }

        /// <summary>
        /// 目录页。vols 里有非空值时就按卷嵌套：
        ///
        ///   <ol>
        ///     <li><span>第一卷 …</span><ol><li><a>第一章</a></li>…</ol></li>
        ///     <li><a>第 N 章</a></li>            ← 卷外的散章
        ///   </ol>
        ///
        /// 为什么不给卷生成单独的 xhtml 页面：EPUB3 的 nav 允许用 &lt;span&gt; 表示
        /// "不可跳转的分组节点"，阅读器会把它当层级标题显示。生成一个只有一行标题的
        /// 页面反而会在翻页时多出一页空白。
        /// </summary>
        private static string NavXhtml(BookInfo book, List<ChapterInfo> items, List<string> names, List<string> vols)
        {
            // 先按卷把章节切成若干段。
            //
            // 正确做法分两步，别把两步揉进一个循环：
            //   1) 先把每章的"有效卷名"算出来（章没写卷名就沿用上一个卷）；
            //   2) 再把"有效卷名相同"的连续章节折叠成一组。
            //
            // 踩过的坑：第一版在单循环里"遇到卷名就另起一组"，结果**每章各开一组**，
            // 生成的目录变成"每章前面挂一个卷标题"（12 章 → 12 个卷标题），
            // 而扁平目录模式下所有断言都还是绿的 —— 是 tests/E2E.cs 里
            // "同一卷的 4 章必须在同一个 <li> 里"这条断言把它抓出来的。
            var eff = new string[items.Count];
            string carry = null;
            for (int i = 0; i < items.Count; i++)
            {
                var v = (vols != null && i < vols.Count) ? vols[i] : null;
                if (!string.IsNullOrEmpty(v)) carry = v.Trim();
                eff[i] = carry;
            }

            var groups = new List<KeyValuePair<string, List<int>>>();
            for (int i = 0; i < items.Count; i++)
            {
                var key = eff[i];
                // 与上一组同名（含"都还没有卷"）就并进去，否则开新组
                if (groups.Count == 0 || groups[groups.Count - 1].Key != key)
                    groups.Add(new KeyValuePair<string, List<int>>(key, new List<int>()));
                groups[groups.Count - 1].Value.Add(i);
            }

            var sb = new StringBuilder();
            // nav.xhtml 在 OEBPS 根下 → style.css 是同层
            sb.Append(Head(book.Title + " - 目录", "style.css"));
            sb.Append("<body>\n");
            sb.Append("  <nav epub:type=\"toc\" id=\"toc\">\n    <h1>目录</h1>\n    <ol>\n");

            bool anyVolume = false;
            foreach (var g in groups) if (g.Key != null) { anyVolume = true; break; }

            if (!anyVolume)
            {
                // 没有分卷信息：保持原来的扁平目录，输出和以前逐字节一致
                for (int i = 0; i < items.Count; i++)
                    sb.Append("      <li><a href=\"").Append(X(HrefOf(names[i])))
                      .Append("\">").Append(X(ChapterTitle(items[i]))).Append("</a></li>\n");
            }
            else
            {
                foreach (var g in groups)
                {
                    if (g.Key == null)
                    {
                        foreach (var i in g.Value)
                            sb.Append("      <li><a href=\"").Append(X(HrefOf(names[i])))
                              .Append("\">").Append(X(ChapterTitle(items[i]))).Append("</a></li>\n");
                        continue;
                    }
                    sb.Append("      <li><span>").Append(X(g.Key)).Append("</span>\n        <ol>\n");
                    foreach (var i in g.Value)
                        sb.Append("          <li><a href=\"").Append(X(HrefOf(names[i])))
                          .Append("\">").Append(X(ChapterTitle(items[i]))).Append("</a></li>\n");
                    sb.Append("        </ol>\n      </li>\n");
                }
            }

            sb.Append("    </ol>\n  </nav>\n</body>\n</html>\n");
            return sb.ToString();
        }

        /// <summary>OEBPS/ 前缀在 nav.xhtml 里要去掉（它自己就在 OEBPS 根下）</summary>
        private static string HrefOf(string name)
        {
            return name.StartsWith("OEBPS/", StringComparison.Ordinal) ? name.Substring("OEBPS/".Length) : name;
        }

        private static string ChapterXhtml(BookInfo book, ChapterInfo c)
        {
            var sb = new StringBuilder();
            // 章节在 OEBPS/text/ 下 → style.css 在上一层
            sb.Append(Head(ChapterTitle(c), "../style.css"));
            sb.Append("<body>\n");
            sb.Append("  <h1>").Append(X(ChapterTitle(c))).Append("</h1>\n");
            foreach (var raw in (c.Text ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                sb.Append("  <p>").Append(X(line.Trim())).Append("</p>\n");
            }
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        private static string CoverXhtml(BookInfo book, string href, string mime)
        {
            var sb = new StringBuilder();
            sb.Append(Head("封面", "../style.css"));
            sb.Append("<body>\n  <div class=\"cover\">\n");
            sb.Append("    <img src=\"").Append(X(href)).Append("\" alt=\"封面\"/>\n");
            sb.Append("    <h1>").Append(X(book.Title)).Append("</h1>\n");
            sb.Append("    <p class=\"meta\">").Append(X(string.IsNullOrEmpty(book.Author) ? "" : "作者：" + book.Author)).Append("</p>\n");
            sb.Append("  </div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        /// <summary>每个 XHTML 的相对样式表路径不同（nav 在根、章节在 text/），所以由调用方传</summary>
        private static string Head(string title, string cssHref)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   "<!DOCTYPE html>\n" +
                   "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"zh-CN\">\n" +
                   "<head>\n  <meta charset=\"utf-8\"/>\n  <title>" + X(title) + "</title>\n" +
                   "  <link rel=\"stylesheet\" type=\"text/css\" href=\"" + X(cssHref) + "\"/>\n</head>\n";
        }

        private static void AddText(ZipArchive zip, string name, string text, CompressionLevel level)
        {
            var e = zip.CreateEntry(name, level);
            using (var s = e.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false))) // EPUB 一律 UTF-8 无 BOM
                w.Write(text);
        }

        private static void AddBytes(ZipArchive zip, string name, byte[] bytes)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open()) s.Write(bytes, 0, bytes.Length);
        }

        /// <summary>XML 文本转义（正文里出现 &lt; &amp; 之类不能把 XHTML 弄坏）</summary>
        internal static string X(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default:
                        // 去掉 XML 1.0 不允许的控制字符（正文里偶尔会有站点带进来的垃圾字符）
                        if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') break;
                        if (ch == '\uFFFE' || ch == '\uFFFF') break;
                        sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Shorten(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>用书名+作者生成稳定的 UUID（同一本书每次导出是一样的，方便阅读器识别为同一本）</summary>
        private static string MakeUuid(BookInfo book)
        {
            var key = (book.Title ?? "") + "|" + (book.Author ?? "") + "|" + (book.Dir ?? "") + "|" + (book.BookId ?? "");
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                return new Guid(hash).ToString();
            }
        }

        private static string NormalizeImageExt(string ext, byte[] bytes)
        {
            if (bytes != null && bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50) return "png";
            if (bytes != null && bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "jpg";
            if (string.IsNullOrEmpty(ext)) return "jpg";
            ext = ext.TrimStart('.').ToLowerInvariant();
            if (ext == "jpeg") ext = "jpg";
            return (ext == "png" || ext == "jpg" || ext == "gif" || ext == "webp") ? ext : "jpg";
        }
    }
}
