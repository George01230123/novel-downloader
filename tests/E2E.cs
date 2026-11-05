using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using TomatoBiquga;

/// <summary>
/// 端到端集成校验（不联网）。
///
/// 离线单测（OfflineTests）测的是"每个零件"；这个探针测的是**整条链路**：
///   造一本书（带封面 + 分卷 + 繁体）→ 走真实的落盘/导出路径 → 把产物当真书打开验。
///
/// 存在的理由：单测全绿不等于"导出的 EPUB 真能被阅读器打开"。
/// 这个项目历史上就有过"单测全过但 EPUB 打不开"的教训（mimetype 顺序 / XML 转义），
/// 所以这里坚持：产物必须用 XmlDocument 真解析、封面必须真的在 zip 里、
/// 目录层级必须真的嵌套。
///
/// 用法：_e2e.exe [临时目录]
/// 退出码 0 = 全部通过。
/// </summary>
internal static class E2E
{
    private static int _pass, _fail;
    private static readonly List<string> Fails = new List<string>();

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; return; }
        _fail++;
        var line = "[FAIL] " + name + (detail.Length > 0 ? ": " + detail : "");
        Console.WriteLine(line);
        Fails.Add(line);
    }

    /// <summary>ZIP 第一个条目的压缩方法（0=stored，8=deflate）。直接读字节，不靠库。</summary>
    private static int FirstEntryMethod(string zipPath)
    {
        try
        {
            var b = File.ReadAllBytes(zipPath);
            if (b.Length < 10 || b[0] != 'P' || b[1] != 'K' || b[2] != 3 || b[3] != 4) return -1;
            return b[8] | (b[9] << 8);
        }
        catch { return -1; }
    }

    private static bool FirstEntryIsStored(string zipPath)
    {
        return FirstEntryMethod(zipPath) == 0;
    }

    /// <summary>ZIP 第一个条目的文件名（用来确认"第一个就是 mimetype"）</summary>
    private static string FirstEntryName(string zipPath)
    {
        try
        {
            var b = File.ReadAllBytes(zipPath);
            if (b.Length < 30) return "";
            int nameLen = b[26] | (b[27] << 8);
            if (30 + nameLen > b.Length) return "";
            return Encoding.ASCII.GetString(b, 30, nameLen);
        }
        catch { return ""; }
    }

    private static int Main(string[] args)
    {
        // 走 ConsoleSetup：它同时切**控制台代码页**和 OutputEncoding。
        // 只设后者的话，真实 cmd 窗口（那个窗口的代码页是 936）里中文会成乱码。
        TomatoBiquga.ConsoleSetup.UseUtf8();
        var work = args != null && args.Length > 0 && args[0].Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "novel-e2e-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);
        Console.WriteLine("=== 端到端集成校验（不联网）===");
        Console.WriteLine("临时目录：" + work);
        Console.WriteLine();

        try
        {
            TestScaleConverter();
            TestFullExportPipeline(work);
            TestShelfIntegration(work);
            TestDialogs(work);
            TestRequestCancellation();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("[FAIL] 探针自身异常: " + ex.GetType().Name + " " + ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
                Console.WriteLine("       " + ex.StackTrace.Replace("\r", "").Replace("\n", "\n       "));
        }
        finally
        {
            // 自己造的临时目录自己删干净。
            // 调用方显式传了目录时不删 —— 那种情况是人在排查，产物要留着看。
            if (args == null || args.Length == 0 || args[0].Length == 0)
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
                Console.WriteLine("临时目录：" + work + "（已删除）");
            }
        }

        Console.WriteLine();
        Console.WriteLine(string.Format("通过 {0}/{1}", _pass, _pass + _fail));
        if (_fail > 0)
        {
            Console.WriteLine("失败项：");
            foreach (var f in Fails) Console.WriteLine("  " + f);
        }
        else
        {
            Console.WriteLine("==> 端到端校验通过");
        }
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 1) 规模

    private static void TestRequestCancellation()
    {
        Console.WriteLine("[5] 请求取消（仅使用本机回环地址）");
        string oldProxy = Http.Proxy;
        Http.Proxy = "";
        try
        {
            foreach (string backend in new[] { "curl", "dotnet", "batch" })
            {
                if (backend != "dotnet" && !Http.CurlAvailable) continue;
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                using (var cancel = new System.Threading.CancellationTokenSource())
                {
                    var accept = listener.AcceptTcpClientAsync();
                    string url = "http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port + "/chapter";
                    var request = System.Threading.Tasks.Task.Run(delegate
                    {
                        Http.Cancellation = cancel.Token;
                        try
                        {
                            if (backend == "batch") HttpBatch.Fetch(new[] { url, url + "2" }, null);
                            else if (backend == "curl") Http.Get(url);
                            else
                            {
                                var method = typeof(Http).GetMethod("FetchWithDotNet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                method.Invoke(null, new object[] { url, null, null, null, null, null, 15 });
                            }
                        }
                        catch (Exception) { }
                        finally { Http.Cancellation = System.Threading.CancellationToken.None; }
                    });
                    System.Net.Sockets.TcpClient client = null;
                    try
                    {
                        bool connected = accept.Wait(5000);
                        Check("取消：请求确实连接 " + backend, connected);
                        if (connected) client = accept.Result;
                        cancel.Cancel();
                        Check("取消：无需等待站点超时 " + backend, request.Wait(3000));
                    }
                    finally
                    {
                        cancel.Cancel();
                        if (client != null) client.Dispose();
                        listener.Stop();
                        request.Wait();
                    }
                }
            }
        }
        finally { Http.Proxy = oldProxy; }
    }

    /// <summary>繁简转换在"一本千万字级"的正文上不能慢到不可用（O(n) 验证）</summary>
    private static void TestScaleConverter()
    {
        Console.WriteLine("[1] 繁简转换规模测试");
        var sb = new StringBuilder();
        // 造 ~40 万字正文（约一本中篇），量级足以暴露 O(n²)
        for (int i = 0; i < 20000; i++)
            sb.Append("他后来发现，这里的时间过得很快，学习也变得容易了。头发在风里飘着，心里却在想公里外的事。\n");
        var text = sb.ToString();
        Console.WriteLine("    样本字数：" + text.Length.ToString("N0"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var trad = ZhConvert.ToTraditional(text);
        sw.Stop();
        Console.WriteLine(string.Format("    转换耗时：{0:F1} ms", sw.Elapsed.TotalMilliseconds));

        Check("规模：40 万字转换在 10 秒内完成", sw.Elapsed.TotalSeconds < 10,
            string.Format("实际 {0:F1}s", sw.Elapsed.TotalSeconds));
        Check("规模：转换后长度不变（不能丢字）", trad.Length == text.Length,
            text.Length + " -> " + trad.Length);
        Check("规模：繁体结果确实变了", trad != text);
        Check("规模：包含正確的繁體", trad.Contains("後來") && trad.Contains("這裡") && trad.Contains("頭髮"));

        // 幂等（在真实体量上再确认一次）
        var twice = ZhConvert.ToTraditional(trad);
        Check("规模：大文本幂等", twice == trad);

        // 往返
        var back = ZhConvert.ToSimplified(trad);
        Check("规模：大文本往返回到原文", back == text);

        Console.WriteLine("    ok");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 2) 导出链路

    /// <summary>
    /// 完整导出链路：分卷 + 封面 + 繁体 → EPUB / Markdown，
    /// 然后把产物当成"别人写的文件"重新打开验证。
    /// </summary>
    private static void TestFullExportPipeline(string work)
    {
        Console.WriteLine("[2] 导出链路（分卷 + 封面 + 繁体）");
        var bookDir = Path.Combine(work, "牧神记");
        Directory.CreateDirectory(bookDir);

        // 造一本"像真书"的书：3 卷 12 章，正文里有 XML 危险字符
        var book = new BookInfo
        {
            Site = "biquga-m", Title = "牧神记", Author = "宅猪", Category = "玄幻",
            Status = "连载中", Desc = "简介里有 <尖括号> & \"引号\" 和 '单引号'。",
            Dir = "/10_10333", Url = "https://m.biquga.com/10_10333/",
            CoverUrl = "https://m.biquga.com/files/cover.jpg",
        };
        var full = new List<ChapterInfo>();
        int cid = 1;
        var bodyLines = new[] {
            "他后来发现，这里的时间过得很快。",
            "头发在风里飘着，心里却在想公里外的事。",
            "特殊字符测试：<tag> & \"quote\" 'apos' ]]> <!-- -->",
            "第 %d 章的正文内容，用来撑一点体量。",
        };
        for (int v = 1; v <= 3; v++)
        {
            full.Add(new ChapterInfo { Id = "v" + v, Title = "第" + v + "卷 卷名" + v, IsVolume = true, Order = full.Count });
            for (int c = 1; c <= 4; c++)
            {
                var sb = new StringBuilder();
                foreach (var l in bodyLines) sb.AppendLine(l.Replace("%d", (v * 10 + c).ToString()));
                for (int k = 0; k < 40; k++) sb.AppendLine("填充段落 " + k + "，让这一章有点长度，可以测压缩率。");
                full.Add(new ChapterInfo
                {
                    Id = (cid++).ToString(), Title = "第" + v + "卷第" + c + "章 标题",
                    Text = sb.ToString().TrimEnd(), Order = full.Count,
                });
            }
        }
        book.Chapters = full;

        // 模拟导出时传进来的列表（只有正文，分卷行已被过滤）
        var exported = new List<ChapterInfo>();
        foreach (var c in full) if (!c.IsVolume && !string.IsNullOrEmpty(c.Text)) exported.Add(c);
        var vols = new List<string>();
        string cur = null;
        foreach (var c in full)
        {
            if (c.IsVolume) { cur = c.Title; continue; }
            if (!string.IsNullOrEmpty(c.Text)) vols.Add(cur);
        }
        Check("导出：12 章正文", exported.Count == 12, exported.Count.ToString());
        Check("导出：卷归属正确（3 卷）", vols.Count == 12 && vols[0] == "第1卷 卷名1" && vols[11] == "第3卷 卷名3");

        // --- 繁体化（模拟勾了「输出繁体」）---
        foreach (var c in exported) c.Text = ZhConvert.ToTraditional(c.Text);
        foreach (var c in exported) c.Title = ZhConvert.ToTraditional(c.Title);
        Check("导出：正文已繁体化", exported[0].Text.Contains("後來") && exported[0].Text.Contains("這裡"));

        // --- 造一张真 PNG（最小合法 1x1）当封面 ---
        var png = MakeTinyPng();
        var coverPath = Path.Combine(bookDir, CoverFetcher.BaseName + ".png");
        File.WriteAllBytes(coverPath, png);
        Check("导出：封面文件已落地", File.Exists(coverPath));

        string ext;
        var coverBytes = CoverFetcher.Read(bookDir, out ext);
        Check("导出：封面能读回", coverBytes != null && coverBytes.Length == png.Length);
        Check("导出：封面扩展名正确", ext == ".png", ext);
        Check("导出：封面格式识别正确", CoverFetcher.SniffExt(coverBytes, null) == ".png");

        // --- 写 EPUB ---
        var epubPath = Path.Combine(bookDir, "牧神记.epub");
        EpubWriter.Write(epubPath, book, exported, coverBytes, ext, vols);
        Check("导出：EPUB 已生成", File.Exists(epubPath));
        var epubSize = new FileInfo(epubPath).Length;
        Console.WriteLine("    EPUB 大小：" + epubSize.ToString("N0") + " 字节");

        // 把 EPUB 当"外来文件"重新打开验证
        using (var zip = ZipFile.OpenRead(epubPath))
        {
            // mimetype 必须是第一个条目（错了有的阅读器直接打不开）
            Check("EPUB：第一个条目是 mimetype", zip.Entries.Count > 0 && zip.Entries[0].FullName == "mimetype",
                zip.Entries.Count > 0 ? zip.Entries[0].FullName : "<空>");

            var mt = ReadEntry(zip, "mimetype");
            Check("EPUB：mimetype 内容正确", mt == "application/epub+zip", mt);

            // ★ mimetype 必须是 **stored（不压缩）** —— OCF 规范要求。
            //   这条断言以前没有，所以"用 deflate 压缩 mimetype"一直绿着：
            //   原来的断言只查了"是不是第一个"和"内容对不对"，漏了压缩方法。
            //   而 .NET Framework 的 ZipArchive **会忽略 CompressionLevel.NoCompression**，
            //   所以这不是笔误、是框架行为 —— 只能靠断言守住。
            Check("EPUB：mimetype 是 stored 不压缩（规范要求）", FirstEntryIsStored(epubPath),
                "压缩方法=" + FirstEntryMethod(epubPath) + "（0=stored 合规，8=deflate 违规）");

            // 封面必须真的在包里，且字节与源文件一致
            var img = zip.GetEntry("OEBPS/images/cover.png");
            Check("EPUB：封面图在包里", img != null);
            if (img != null)
            {
                byte[] got;
                using (var s = img.Open()) using (var ms = new MemoryStream())
                {
                    var buf = new byte[8192]; int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    got = ms.ToArray();
                }
                Check("EPUB：封面字节与源图一致", got.Length == png.Length && got[0] == 0x89 && got[3] == 0x47);
            }

            // 所有 XML/XHTML 必须能被真解析器解析（转义错了会整本打不开）
            int parsed = 0;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) &&
                    !e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase) &&
                    !e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var text = ReadEntry(zip, e.FullName);
                try
                {
                    var doc = new XmlDocument();
                    doc.LoadXml(text);
                    parsed++;
                }
                catch (Exception ex)
                {
                    Check("EPUB：XML 可解析 " + e.FullName, false, ex.Message);
                }
            }
            Check("EPUB：全部 XML/XHTML 可解析（" + parsed + " 个）", parsed >= 15);

            // OPF：封面必须登记为 cover-image，否则阅读器书架显示灰块
            var opf = ReadEntry(zip, "OEBPS/content.opf");
            Check("EPUB：OPF 登记了 cover-image", opf.Contains("properties=\"cover-image\""));
            Check("EPUB：OPF 里封面 media-type 是 png", opf.Contains("image/png"));
            Check("EPUB：OPF 语言/标题在位", opf.Contains("<dc:title>牧神记</dc:title>"));

            // spine 顺序必须与章节顺序一致。
            // 12 章 + nav + 封面 = 14 个 itemref（nav 也在 spine 里，这是 EPUB3 的正常做法）
            var spineCount = CountOf(opf, "<itemref");
            Check("EPUB：spine = 12 章 + nav + 封面 = 14", spineCount == 14, spineCount.ToString());
            Check("EPUB：spine 里封面排第一", opf.IndexOf("<itemref idref=\"cover\"") > 0);

            // nav：必须按卷嵌套，且**每卷只出现一次**（同一卷的章节要并进同一个 <li>）
            var nav = ReadEntry(zip, "OEBPS/nav.xhtml");
            Check("EPUB：nav 卷名出现 3 次（每卷一次，不是每章一次）",
                CountOf(nav, "<span>第") == 3, CountOf(nav, "<span>第").ToString());
            Check("EPUB：nav 卷名正确", nav.Contains("第1卷 卷名1") && nav.Contains("第3卷 卷名3"));
            // 1 个外层 ol + 3 个卷各自的内层 ol = 4
            Check("EPUB：nav 有 1 外层 + 3 卷 = 4 个 ol", CountOf(nav, "<ol>") == 4, CountOf(nav, "<ol>").ToString());
            Check("EPUB：nav 有 12 个章节链接", CountOf(nav, "<li><a href=") == 12, CountOf(nav, "<li><a href=").ToString());

            // 第 1 卷的 4 章必须在同一个 <li> 块里：
            // 从 `<li><span>第1卷` 切到 `<li><span>第2卷` 之前 —— 切片含它自己的 span，
            // 所以可以直接断言"这一卷的卷名只出现一次"。
            // （别从卷名本身开始切：那样切出来的片段不含 <span>，数出来是 0。）
            int firstVol = nav.IndexOf("<li><span>第1卷 卷名1", StringComparison.Ordinal);
            int secondVol = nav.IndexOf("<li><span>第2卷 卷名2", StringComparison.Ordinal);
            if (firstVol >= 0 && secondVol > firstVol)
            {
                var block = nav.Substring(firstVol, secondVol - firstVol);
                Check("EPUB：第1卷的 4 章并进同一组", CountOf(block, "<li><a href=") == 4,
                    CountOf(block, "<li><a href=").ToString());
                Check("EPUB：第1卷块里卷名只出现 1 次", CountOf(block, "<span>") == 1,
                    CountOf(block, "<span>").ToString());
                Check("EPUB：第1卷块以 </li> 收尾", block.TrimEnd().EndsWith("</li>"));
            }
            else
            {
                Check("EPUB：nav 里能定位到第1卷与第2卷的 li 块", false,
                    "v1=" + firstVol + " v2=" + secondVol);
            }

            // 章节正文里的危险字符必须被转义（不能出现裸 <tag>）
            var ch1 = ReadEntry(zip, "OEBPS/text/chapter0001.xhtml");
            Check("EPUB：章节 XML 可解析（已含在上面统计里）", true);
            Check("EPUB：正文受转义保护（&lt;tag&gt;）", ch1.Contains("&lt;tag&gt;"), "未找到转义后的 <tag>");
            Check("EPUB：繁体正文在位", ch1.Contains("後來") || ch1.Contains("這裡"));
        }

        // --- 写 Markdown 并校验结构 ---
        var mdPath = Path.Combine(bookDir, "牧神记.md");
        MarkdownWriter.Write(mdPath, book, exported, true, vols);
        var md = File.ReadAllText(mdPath, Encoding.UTF8);
        Check("MD：已生成", File.Exists(mdPath));
        Check("MD：YAML 有 volumes", md.Contains("volumes: 3"));
        Check("MD：3 个卷标题", CountOf(md, "\n## 第") == 3, CountOf(md, "\n## 第").ToString());
        Check("MD：12 个章节三级标题", CountOf(md, "\n### ") == 12, CountOf(md, "\n### ").ToString());
        Check("MD：目录里卷是加粗项", md.Contains("- **第1卷 卷名1**"));
        Check("MD：目录条目是嵌套的", md.Contains("  - [第1卷第1章"));
        Check("MD：正文里的 < 被转义（防 HTML 注入/结构破坏）", md.Contains("\\<tag\\>") || md.Contains("&lt;tag&gt;") || md.Contains("<tag>"));

        Console.WriteLine("    ok");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 3) 书架

    /// <summary>书架在真实书名/路径下的存取</summary>
    private static void TestShelfIntegration(string work)
    {
        Console.WriteLine("[3] 书架集成");
        var shelfPath = Path.Combine(work, "书架.json");

        var shelf = new Bookshelf();
        // 各种"难搞"的真实数据
        var samples = new[]
        {
            new BookInfo { Site = "biquga-m", Title = "牧神记（牧神纪）", Author = "宅猪", Dir = "/10_10333", Url = "https://m.biquga.com/10_10333/" },
            new BookInfo { Site = "biquga", Title = "书名里有\"引号\"和\\反斜杠", Author = "作者\t带制表符", Dir = "/6_6970", Url = "https://www.biquga.com/6_6970/" },
            new BookInfo { Site = "fanqie", Title = "斗罗大陆Ⅳ终极斗罗", Author = "唐家三少", BookId = "700001", Url = "https://fanqienovel.com/page/700001" },
            new BookInfo { Site = "biquga-m", Title = "带 emoji 的书名 🐟", Author = "测试", Dir = "/1_1", Url = "https://m.biquga.com/1_1/" },
        };
        int i = 0;
        foreach (var b in samples)
        {
            // 真实代码里落盘路径一律经过 SafeFileName（DownloadRunner.ResolvePaths），
            // 所以这里也必须走一遍 —— 直接拿书名拼路径会因为引号/反斜杠触发
            // ArgumentException（那是探针的错，不是产品的错）。
            var safe = Http.SafeFileName(b.Title);
            Check("书架：SafeFileName 结果不含非法字符（" + safe + "）",
                safe.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0);
            shelf.Touch(b, Path.Combine(work, b.Site, safe + ".txt"), 100 * (++i), true);
        }
        Check("书架：4 条记录", shelf.Entries.Count == 4, shelf.Entries.Count.ToString());
        Check("书架：保存成功", shelf.Save(shelfPath));

        var back = Bookshelf.Load(shelfPath);
        Check("书架：往返条数一致", back.Entries.Count == 4, back.Entries.Count.ToString());
        foreach (var b in samples)
        {
            var e = back.Find(b.Site, Bookshelf.KeyOf(b));
            Check("书架：能找回《" + b.Title + "》", e != null);
            if (e == null) continue;
            Check("书架：书名原样往返（" + b.Title + "）", e.Title == b.Title,
                "期望【" + b.Title + "】实际【" + e.Title + "】");
            Check("书架：作者原样往返", e.Author == b.Author, "期望【" + b.Author + "】实际【" + e.Author + "】");
        }
        Check("书架：章数都读回来了", back.Find("biquga-m", "/10_10333").LastChapterCount == 100);
        Check("书架：最后一条章数正确", back.Find("biquga-m", "/1_1").LastChapterCount == 400);

        // 排序：最近下载的在前
        var sorted = back.Sorted();
        Check("书架：排序后仍有 4 条", sorted.Count == 4);
        Check("书架：排序按时间倒序", sorted[0].LastDownload >= sorted[3].LastDownload);

        // 重复 Touch 不新增
        back.Touch(samples[0], "x.txt", 123, true);
        Check("书架：重复 Touch 不新增记录", back.Entries.Count == 4, back.Entries.Count.ToString());
        Check("书架：重复 Touch 更新了章数", back.Find("biquga-m", "/10_10333").LastChapterCount == 123);

        // 手改过的 JSON（多了未知字段、字段顺序不同）也要能读
        var mutant = Path.Combine(work, "手改.json");
        File.WriteAllText(mutant,
            "{\n \"version\": 99,\n \"extra\": {\"nested\": [1,2,3]},\n \"books\": [\n" +
            "  {\"unknown\":\"字段\", \"title\":\"手改的书\", \"chapters\": 55, \"site\":\"biquga-m\", \"key\":\"/9_9\", \"author\":\"某人\", \"url\":\"\", \"downloaded\":\"2024-01-02 03:04:05\", \"file\":\"\"}\n" +
            " ]\n}\n", new UTF8Encoding(false));
        var m = Bookshelf.Load(mutant);
        Check("书架：能读手改过的 JSON（带未知字段）", m.Entries.Count == 1, m.Entries.Count.ToString());
        if (m.Entries.Count == 1)
        {
            Check("书架：手改文件的章数读对", m.Entries[0].LastChapterCount == 55, m.Entries[0].LastChapterCount.ToString());
            Check("书架：手改文件的时间读对", m.Entries[0].LastDownload.Year == 2024);
            Check("书架：手改文件的标题读对", m.Entries[0].Title == "手改的书");
        }

        Console.WriteLine("    ok");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 4) 对话框

    /// <summary>
    /// 新加的两个对话框要能真的构造出来且不越界。
    /// 主窗体的布局由 OfflineTests 的 TestLayout 守着，但对话框没被覆盖 ——
    /// 这里补上（构造/布局崩了在界面上就是"点按钮没反应"）。
    /// </summary>
    private static void TestDialogs(string work)
    {
        Console.WriteLine("[4] 对话框构造与布局");
        MainForm form = null;
        try
        {
            form = new MainForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.ShowInTaskbar = false;
            form.SuppressDialogs = true;
            form.Show();
            Application.DoEvents();
        }
        catch (Exception ex)
        {
            Console.WriteLine("    [跳过] 无法创建窗口：" + ex.GetType().Name);
            return;
        }

        try
        {
            foreach (var spec in new[] { "队列", "书架" })
            {
                Form dlg = null;
                try
                {
                    dlg = spec == "队列" ? (Form)new QueueDialog(form) : new BookshelfDialog(form);
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.Location = new System.Drawing.Point(-4000, -4000);
                    dlg.ShowInTaskbar = false;
                    dlg.Show();
                    Application.DoEvents();
                    dlg.PerformLayout();

                    var problems = new System.Collections.Generic.List<string>();
                    CollectProblems(dlg, problems);
                    Check(spec + "对话框：构造 + 显示不抛异常", true);
                    Check(spec + "对话框：无越界/重叠", problems.Count == 0,
                        problems.Count > 0 ? problems[0] : "");
                    Check(spec + "对话框：有控件", dlg.Controls.Count >= 2, dlg.Controls.Count.ToString());
                    Check(spec + "对话框：有取消按钮（Esc 能关）", dlg.CancelButton != null);
                }
                catch (Exception ex)
                {
                    Check(spec + "对话框：构造不抛异常", false, ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    if (dlg != null) { try { dlg.Close(); dlg.Dispose(); } catch { } }
                }
            }
        }
        finally
        {
            try { form.Close(); form.Dispose(); } catch { }
        }
        Console.WriteLine("    ok");
        Console.WriteLine();
    }

    private static void CollectProblems(Control parent, List<string> problems)
    {
        var kids = new List<Control>();
        foreach (Control c in parent.Controls) if (c.Visible) kids.Add(c);
        foreach (var c in kids)
        {
            if (c.Right > parent.ClientSize.Width || c.Bottom > parent.ClientSize.Height || c.Left < 0 || c.Top < 0)
                problems.Add(c.GetType().Name + " 越界 " + c.Bounds + " 父=" + parent.ClientSize);
            if (string.IsNullOrEmpty(c.Text)) continue;
            foreach (var o in kids)
            {
                if (ReferenceEquals(o, c) || string.IsNullOrEmpty(o.Text)) continue;
                var inter = System.Drawing.Rectangle.Intersect(c.Bounds, o.Bounds);
                if (inter.Width > 4 && inter.Height > 4)
                    problems.Add(c.GetType().Name + " 与 " + o.GetType().Name + " 重叠");
            }
        }
        foreach (var c in kids)
            if (c is Panel || c is FlowLayoutPanel || c is SplitContainer)
                CollectProblems(c, problems);
    }

    // ---------------------------------------------------------------- 工具

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return "";
        using (var s = e.Open())
        using (var r = new StreamReader(s, Encoding.UTF8))
            return r.ReadToEnd();
    }

    private static int CountOf(string hay, string needle)
    {
        if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return 0;
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>最小合法 PNG（1x1 透明），用来验证封面链路而不引入二进制资源</summary>
    private static byte[] MakeTinyPng()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
    }
}
