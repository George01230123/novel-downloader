using System;
using System.Collections.Generic;

namespace TomatoBiquga
{
    /// <summary>
    /// 规则表站点的登记处。
    ///
    /// 加一个站点 = 在这里加一条 SiteRule，别处都不用动。
    /// **每条规则都必须是实测跑通过的** —— 里面的每个正则都对应一段真实抓下来的
    /// HTML（校准记录见 docs/站点规则实测.md），不写"看起来对"的正则。
    ///
    /// ============================================================
    /// 实测被排除的站点（别加回来，除非重新实测通过）
    /// ============================================================
    ///   书海阁      搜索返回「错误提示」页（1444 B）
    ///   香书小说    搜索返回「警告」页，最终落到 183.192.65.101
    ///   鸟书网      搜索端点 404（规则域名已换成 99wx.info，仍未通）
    ///   悠久小说网  搜索端点 404
    ///   阅读库      搜索端点 404
    ///   顶点小说    搜索返回 403（反爬）
    ///   梦书中文    搜索**忽略关键词**，永远返回同一本书 → 接进去只会让用户搜到错书
    ///   笔趣阁22    域名已停放（页面加载 lander.parity.domains 脚本）
    ///   燃文小说网  TLS 被 RST（与 m.biquga.com 同款 SNI 拦截）
    /// </summary>
    public static class SiteRules
    {
        /// <summary>规则表站点的登记入口，由 SiteRegistry 的静态构造调用一次。</summary>
        internal static void RegisterInto()
        {
            foreach (SiteRule rule in All())
            {
                rule.Validate();       // 规则不自洽就地炸
                SiteRegistry.Register(new SiteDescriptor
                {
                    Key = rule.Key,
                    DisplayName = rule.DisplayName,
                    Factory = MakeFactory(rule),
                    Caps = rule.Caps,
                    DefaultBase = rule.Base,
                    Rule = rule,
                });
            }
        }

        /// <summary>
        /// 用闭包固定住 rule，避免循环变量捕获的经典错误
        /// （C# 5 的 foreach 变量是**捕获同一个**变量，不是每次迭代一个新变量）。
        /// </summary>
        private static Func<ISite> MakeFactory(SiteRule rule)
        {
            SiteRule captured = rule;
            return delegate { return new RuleSite(captured); };
        }

        private static List<SiteRule> _all;

        /// <summary>全部规则表站点</summary>
        public static List<SiteRule> All()
        {
            if (_all != null) return _all;
            var list = new List<SiteRule>();
            list.Add(Xbiquwk());
            list.Add(Biquge365());
            list.Add(Pmzw());
            list.Add(Piaotia());
            _all = list;
            return _all;
        }

        /// <summary>按 Key 取规则；未知返回 null</summary>
        public static SiteRule ByKey(string key)
        {
            foreach (SiteRule r in All())
                if (string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        // ==================================================================
        //  笔尖中文
        //
        //  为什么它值得优先推荐：目录**一页给全**。实测《剑来》详情页里有
        //  1317 个链接、1314 章，975 ms 拿到。笔趣阁 PC 版走 772 页串行链
        //  要约 10 分钟 —— 所以这个源把"载入目录"从分钟级压到秒级。
        //
        //  实测（2026-10-14，本机 curl）：
        //    搜索 662 ms | 目录 975 ms（1314 章）| 正文 394 ms（《剑来》第一章）
        //
        //  URL 注意：上游 so-novel 的规则写的是 xbiquzw.net，
        //  实际域名已跳转到 xbiquwk.com（搜索端点也是 /modules/article/search.php）。
        // ==================================================================
        private static SiteRule Xbiquwk()
        {
            return new SiteRule
            {
                Key = "xbiquwk",
                DisplayName = "笔尖中文",
                Base = "http://www.xbiquwk.com/",
                Caps = SiteCapabilities.Search | SiteCapabilities.AddressIntake
                     | SiteCapabilities.TocInDetailPage,

                // 搜索表格：<td class="odd"><a href="/1_1245/">剑来</a></td>
                SearchUrl = "http://www.xbiquwk.com/modules/article/search.php",
                SearchIsPost = true,
                SearchForm = new[]
                {
                    new[] { "searchkey", "%s" },
                },
                // ★ 这里**不能**开 SearchResultMustContainKeyword。
                //   实测（2026-10-16，用户报"搜不到万妖圣祖"）：
                //     搜「万妖圣祖」→ 页面里**有 50 条书籍链接**，
                //                     但页面不回显关键词 → 被整批误杀成"无结果"
                //     搜「万相之王」→ 页面恰好回显了关键词 → 能用
                //   于是表现为"同一个站有的词能搜有的不能"，用户完全无法理解。
                //   判断依据不该是"整页有没有这几个字"：那是把"站点回显搜索框内容"
                //   误当成"站点按关键词检索了"。详见 SiteRule 里该字段的注释。
                SearchResultMustContainKeyword = false,
                BookLinkPatterns = new[]
                {
                    "<td class=\"odd\"><a href=\"(/\\d+_\\d+/)\"[^>]*>",
                    "<a href=\"(/\\d+_\\d+/)\"",
                },
                // 搜索表格的列序（实测 2026-10-18）：
                //   0 书名 | 1 最新章节 | 2 作者 | 3 字数 | 4 更新 | 5 状态
                // 以前只取了书名，作者/字数/状态全被丢掉 → 结果列表「作者」列永远是空的。
                SearchAuthorColumn = 2,
                SearchDescColumns = new[] { 3, 5, 4 },   // 字数 · 状态 · 更新

                // 书名/作者/封面/简介全在 og: 元信息里，比抓 #info 的 DOM 稳
                BookTitlePattern = "og:novel:book_name\"\\s+content=\"([^\"]*)\"",
                BookAuthorPattern = "og:novel:author\"\\s+content=\"([^\"]*)\"",
                BookIntroPattern = "og:description\"\\s+content=\"([^\"]*)\"",
                BookCoverPattern = "og:image\"\\s+content=\"([^\"]*)\"",

                // 目录：详情页即目录页。<div id="list"><dl><dd><a href="603316.html">
                // 必须先框住 #list 容器：页面上还有 list/1_1.html（分类页）和
                // paihangbang_allvisit/1.html（排行榜）共 7 个干扰链接。
                TocUrlTemplate = null,
                TocBlockPattern = "<div[^>]*id=\"list\"[^>]*>([\\s\\S]*?)</div>",
                // 捕获组 2 = 章节标题（有了它就不必再去猜标题文字）
                TocLinkPattern = "<dd>\\s*<a href=\"([^\"]+?\\.html)\"[^>]*>([^<]{0,120})</a>",
                TocLinkBase = null,          // 相对详情页：/1_1245/ + 603316.html
                TocLinksAreAbsolute = false,
                TocIsDescending = false,

                // 正文在 <div id="content">；实测该块没有广告行，不需要 strip
                ContentPattern = "<div[^>]*id=\"content\"[^>]*>([\\s\\S]*?)</div>",
                ContentIsBase64 = false,
                ContentStripPattern = null,
                // 章节页是扁平相对地址（/1_1245/603316.html），id 可直接推算 → 可批量
                ChapterPagesAddressableByKey = true,

                KnownTitles = new[] { "剑来" },
            };
        }

        // ==================================================================
        //  笔趣阁365
        //
        //  ★ 关键坑：详情页 <ul class="newchapter"> **只给最近 9~21 章**，
        //    不是完整目录（实测该书有 300 章，详情页只有 21 个链接）。
        //    完整目录在另一个地址 /newbook/{id}/ 的 <ul class="info"> 里（实测 300 章）。
        //    所以这里必须用 TocUrlTemplate + %i，不能只靠详情页。
        //
        //  实测（2026-10-14，本机 curl）：
        //    搜索 647 ms | 目录 795 ms | 正文 650 ms（2208 字）
        //    稳定性：搜索**时好时坏**（3 次里 1 次搜不到）—— 评分会把它排在后面，
        //    自动模式自然少用它。
        //
        //  正文首行是站点水印「一秒记住【笔趣阁】biquge365.net，更新快，无弹窗！」，
        //  用 ContentStripPattern 剔掉。
        // ==================================================================
        private static SiteRule Biquge365()
        {
            return new SiteRule
            {
                Key = "biquge365",
                DisplayName = "笔趣阁365",
                Base = "https://www.biquge365.net/",
                Caps = SiteCapabilities.Search | SiteCapabilities.AddressIntake
                     | SiteCapabilities.TocInDetailPage,

                SearchUrl = "https://www.biquge365.net/s.php",
                SearchIsPost = true,
                SearchForm = new[]
                {
                    new[] { "type", "articlename" },
                    new[] { "s", "%s" },
                },
                // 站点对搜索有 15 秒间隔限制；命中不了会返回"搜索"XXX"的结果"空页。
                // 不强制校验关键词（有些站会把词改写），但要求页面确实是结果页。
                SearchIsResultPattern = "搜索[\\s\\S]{0,20}的结果",
                BookLinkPatterns = new[]
                {
                    "<span class=\"name\"><a href=\"(/book/\\d+/)\"[^>]*>",
                    "<a href=\"(/book/\\d+/)\"[^>]*>",
                },

                BookTitlePattern = "og:novel:book_name\"\\s+content=\"([^\"]*)\"",
                BookAuthorPattern = "og:novel:author\"\\s+content=\"([^\"]*)\"",
                BookIntroPattern = "og:description\"\\s+content=\"([^\"]*)\"",
                BookCoverPattern = "og:image\"\\s+content=\"([^\"]*)\"",

                // 详情页只有最近几章 → 必须另取完整目录页。
                // %i = 路径里第一段数字（/book/417022/ → 417022）
                TocUrlTemplate = "https://www.biquge365.net/newbook/%i/",
                TocBlockPattern = "<ul[^>]*class=['\"]info['\"][^>]*>([\\s\\S]*?)</ul>",
                // ★ 必须要求 title 属性：不加的话 div.gongneng 里的
                //   `<a href="/chapter/417022/685327301.html">开始阅读</a>`
                //   会被当成第 1 章抓进来。实测 ul.info 里 300 个章节链接**全部**带
                //   title，所以这条约束是安全的。捕获组 2 = 章节标题。
                TocLinkPattern = "<li>\\s*<a href=\"([^\"]+?\\.html)\"[^>]*\\btitle=[^>]*>([^<]{0,120})</a>",
                TocLinksAreAbsolute = false,
                TocIsDescending = false,     // 实测 ul.info 是第1章在前，升序

                ContentPattern = "<div[^>]*id=\"txt\"[^>]*>([\\s\\S]*?)</div>",
                ContentIsBase64 = false,
                // 水印整行（含行尾换行）剔掉；用 [^\n]* 限定在行内，不会吃到正文
                ContentStripPattern = "[ \\t]*一秒记住[^\\n]*\\n?",
                // 章节地址形如 /chapter/{bookid}/{cid}.html，book id 是路径里的数字 → 可推算
                ChapterPagesAddressableByKey = true,

                KnownTitles = new[] { "沧元图" },
            };
        }

        /// <summary>
        /// 排名中文网（pmzw.net）—— 2026-10-19 实测通过。
        ///
        /// 为什么加它：现有 5 个站点里只有「笔尖中文」稳定，
        /// 笔趣阁365 时好时坏（同一次搜索 6 条或 0 条），
        /// 笔趣阁 PC 单请求 4.7~13 秒还间歇 502，番茄没有搜索接口。
        /// 换句话说：**能用的源只有一个**，这是这个项目最大的短板。
        ///
        /// 实测：搜索 POST /search.html（表单字段 s）；书页 /shu/796/ 有
        /// og:novel 元数据 + 1837 章（大结局）；目录 index_1..index_19；
        /// 正文 31 段 base64，解码正常。
        ///
        /// ★ 两个新能力是为它加的（见 SiteRule 里的注释）：
        ///   · ContentBase64Pattern：正文散在 document.writeln(qsbs.bb('…')) 里，
        ///     一章 30+ 段，必须按顺序解码拼接（顺序错了正文就是乱的）；
        ///   · TocPageUrlTemplate / TocMaxPages：目录切成 index_N.html，一页 100 章，
        ///     只读第一页**不会报错**，用户会以为这本书只有 100 章。
        /// </summary>
        /// <summary>
        /// 飘天文学（ptwxz.com / piaotia.com）—— 2026-10-20 实测通过（**需要代理**）。
        ///
        /// 为什么加它：现有 3 个站里笔尖中文最稳但《万相之王》1838 章，
        /// 而飘天有**第 1851 章番外**（更全）。它直连 403，走 `proxy=` 才通 ——
        /// 正好用上刚接进来的代理配置。
        ///
        /// 实测（走 127.0.0.1:7897）：
        ///   · 搜索：POST /modules/article/search.php，字段 searchtype=articlename & searchkey；
        ///     **必须 GBK 编码**（UTF-8 提交 200 但 0 命中）；命中后返回的是**详情页**
        ///     （不是结果列表），里面有「查看全部章节」指向 html/13/13191/index.html。
        ///   · 目录页 30KB，章节链接是**相对路径**（`9042777.html`），
        ///     以目录页为 base 解析正好。
        ///   · 正文页：正文是**裸文本**（没有 id="content" 这种容器），
        ///     紧跟在一个 `</table><br>` 之后，到该 div 结束；用 `<br>` 分段
        ///     （Http.StripTags 会把 `<br>` 转成换行）。
        /// </summary>
        private static SiteRule Piaotia()
        {
            return new SiteRule
            {
                Key = "piaotia",
                DisplayName = "飘天文学",
                Base = "https://www.piaotia.com/",
                Caps = SiteCapabilities.Search | SiteCapabilities.AddressIntake
                     | SiteCapabilities.TocInDetailPage,

                SearchUrl = "https://www.piaotia.com/modules/article/search.php",
                SearchIsPost = true,
                SearchForm = new[]
                {
                    new[] { "searchtype", "articlename" },
                    new[] { "searchkey", "%s" },
                },
                // ★ 这站是 GBK；发 UTF-8 会"200 但 0 命中"（实测）
                SearchFormEncoding = "gbk",
                SearchReferer = "https://www.piaotia.com/",
                SearchIsResultPattern = "飘天文学",
                // ★ 不开关键词校验（沿用笔尖中文的教训：站点不回显关键词时会误杀整页结果）。
                //   这里不需要它：BookLinkPatterns 要求 `title="点击阅读"`，
                //   而这个属性只出现在**真正的书页**上，侧栏推荐里没有 —— 已经足够精确。
                SearchResultMustContainKeyword = false,
                // 详情页的「点击阅读」是**绝对地址**；列表页则是 /bookinfo/N/M.html
                BookLinkPatterns = new[]
                {
                    "<a[^>]*title=\"点击阅读[^\"]*\"[^>]*href=\"(https?://[^\"]+/html/\\d+/\\d+/)\"",
                    "<a[^>]*href=\"(https?://[^\"]+/html/\\d+/\\d+/)\"[^>]*title=\"点击阅读",
                    "href=\"(/bookinfo/\\d+/\\d+\\.html)\"",
                },

                // <title>万相之王最新章节,天蚕土豆作品选-飘天文学</title>
                BookTitlePattern = "<title>([^,<]*?)(?:最新章节)?[,<]",
                // 页面上是「作&nbsp;&nbsp;&nbsp; 者：天蚕土豆」
                BookAuthorPattern = "者[：:]\\s*(?:&nbsp;)*([^<&\\s]{1,20})",
                BookIntroPattern = "<meta name=\"description\" content=\"([^\"]*)\"",

                // ★ 目录地址在详情页里（%i 会取错，取到分类号 13）
                TocUrlFromDetailPattern = "href=\"(https?://[^\"]+/html/\\d+/\\d+/index\\.html)\"",
                // 目录页里是相对链接：<a href="9042777.html">第1章 …</a>
                TocLinkPattern = "<a[^>]*href=\"(\\d+\\.html)\"[^>]*>([^<]{0,120})</a>",
                TocLinksAreAbsolute = false,
                TocIsDescending = false,

                // 正文是裸文本，锚定在字号按钮表格之后
                ContentPattern = "</table>\\s*<br>\\s*([\\s\\S]*?)</div>",
                ContentIsBase64 = false,
                // 站点水印/导航整行（正文里不该出现，防串味）
                ContentStripPattern = "(?im)^\\s*(PT文学|书籍介绍|章节目录|我的书架|加入书架|加入书签|推薦本書|推荐本书|收藏本书|繁體中文|上一章|返回目录|下一章|返回书页)\\s*$\\s*",

                KnownTitles = new[] { "万相之王" },
            };
        }

        private static SiteRule Pmzw()
        {
            return new SiteRule
            {
                Key = "pmzw",
                DisplayName = "排名中文网",
                Base = "https://www.pmzw.net/",
                Caps = SiteCapabilities.Search | SiteCapabilities.AddressIntake
                     | SiteCapabilities.TocInDetailPage,

                // 实测：<form id="novel_search" method="post" action="/search.html">
                //         <input name="s" placeholder="输入书名/作者进行搜索，宁少字勿错字">
                SearchUrl = "https://www.pmzw.net/search.html",
                SearchIsPost = true,
                SearchForm = new[]
                {
                    new[] { "s", "%s" },
                },
                SearchReferer = "https://www.pmzw.net/",
                // 结果页有表头「文章名称 / 最新章节 / 作者 / 更新」——
                // 用它区分"确实是结果页"和"错误页被当成没搜到"。
                SearchIsResultPattern = "文章名称",
                // 沿用笔尖中文的教训：**不开**关键词校验
                // （有些站不回显关键词，一校验就把整页结果误杀成"无结果"）。
                SearchResultMustContainKeyword = false,
                // 结果行四列：0 书名 | 1 最新章节 | 2 作者 | 3 更新
                SearchAuthorColumn = 2,
                SearchDescColumns = new[] { 1, 3 },
                BookLinkPatterns = new[]
                {
                    "<td class=\"odd\"[^>]*>\\s*<a href=\"(/shu/\\d+/)\"",
                    "<a href=\"(/shu/\\d+/)\"",
                    "<a href=\"(/books/\\d+/)\"",
                },

                BookTitlePattern = "og:novel:book_name\"\\s+content=\"([^\"]*)\"",
                // ★ 这个站的 og 标签写坏了：content 没有闭合引号
                //   （`content="天蚕土豆/><meta property=`），所以不能用 `[^"]*`，
                //   否则会把后面半段 HTML 吃进作者名。停在 `<` 更稳。
                BookAuthorPattern = "og:novel:author\"\\s+content=\"([^\"<]*)",
                BookIntroPattern = "og:description\"\\s+content=\"([^\"]*)\"",
                BookCoverPattern = "og:image\"\\s+content=\"([^\"]*)\"",

                // 目录分页：/shu/796/index_1.html … index_19.html
                // ★ 目录首页必须是**书籍页**（/shu/796/）：它的 dl.zjlist 里有第 1~99 章，
                //   而 index_N 是从第 100 章才开始的 —— 用 index_1 当首页会**丢掉前 99 章**。
                TocUrlTemplate = "https://www.pmzw.net/shu/%i/",
                TocLinkPattern = "<a href=\"(/shu/\\d+/\\d+\\.html)\"[^>]*>([^<]{0,120})</a>",
                TocLinksAreAbsolute = false,
                TocIsDescending = false,
                TocPageUrlTemplate = "https://www.pmzw.net/shu/%i/index_%p.html",
                TocMaxPages = 40,
                // 这个站同一本书被**重复上传过**（实测 3008 个 id 只对应 1830 个章号，
                // 第1204章 有 4 个不同 URL）。不去重会把同一章下 4 遍，
                // 而"下载成功、章数更多"看起来完全正常 —— 用户几乎不可能发现。
                TocSortByNumber = true,

                // 正文：<div id="content"> 里一堆 document.writeln(qsbs.bb('…'));
                ContentPattern = "<div[^>]*id=\"content\"[^>]*>([\\s\\S]*?)</div>",
                ContentBase64Pattern = "qsbs\\.bb\\('([^']+)'\\)",
                ContentIsBase64 = false,
                // 站点水印：正文开头「【】c0，更新快，无弹窗！」之类
                ContentStripPattern = "【[^】]{0,20}】[^\\n]{0,30}更新快[^\\n]*|[ \\t]*一秒记住[^\\n]*",

                KnownTitles = new[] { "万相之王" },
            };
        }
    }
}
