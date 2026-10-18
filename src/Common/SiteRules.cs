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
    }
}
