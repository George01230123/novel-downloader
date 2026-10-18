using System;
using System.Collections.Generic;

namespace TomatoBiquga
{
    /// <summary>
    /// 一个"规则表站点"的描述 —— 一个站点所需的全部站点专属知识都在这里，
    /// 不写 C# 抓取代码（那部分是共用的 RuleSite）。
    ///
    /// 为什么用正则而不是 CSS 选择器：
    /// 上游 so-novel 的规则是 Jsoup 的 CSS 选择器（`div:nth-child(4) > ul > li > a`），
    /// 要照搬就得自己写一个 CSS 选择器引擎 —— 几百行、边界很多、还容易在
    /// `:nth-child` / 属性选择器 / XPath 上出错。而本项目零第三方依赖
    /// （本地没有 HtmlAgilityPack/AngleSharp，也不打算加）。
    /// 所以这里用正则：保住"加站点只填一张表"的全部好处，不背选择器引擎的风险，
    /// 而且和 BiqugaSite 现有写法一致。
    ///
    /// 每个字段都必须对着**真实抓下来的 HTML** 校准过 —— 不写"看起来对"的正则。
    /// </summary>
    public sealed class SiteRule
    {
        // ---------------- 身份 ----------------

        /// <summary>稳定标识：BookInfo.Site / 书架 / DirCache / CrawlResume / 站点配置.ini 段名</summary>
        public string Key;
        /// <summary>界面显示名</summary>
        public string DisplayName;
        /// <summary>站点根地址，必须以 / 结尾</summary>
        public string Base;
        /// <summary>能力标志</summary>
        public SiteCapabilities Caps;

        // ---------------- 搜索 ----------------

        /// <summary>搜索地址。含 %s 时用关键词替换（GET 用）</summary>
        public string SearchUrl;
        /// <summary>true = POST 表单，false = GET</summary>
        public bool SearchIsPost;
        /// <summary>
        /// POST 表单字段：每项 {name, value}；value 里含 %s 时替换成关键词。
        /// C# 5 没有元组语法糖可用得这么随意，所以用 string[2]。
        /// </summary>
        public string[][] SearchForm;
        /// <summary>搜索请求的 Referer，空则用 Base</summary>
        public string SearchReferer;

        /// <summary>
        /// 从搜索结果页抽"书籍详情页链接"的正则，按序尝试，第一个有命中的胜出。
        /// 捕获组 1 = 链接。
        /// </summary>
        public string[] BookLinkPatterns;

        /// <summary>
        /// 站点把搜索词当**字面**匹配、命中不了就返回首页/分类页时，
        /// 用这个正则确认"这一页确实是搜索结果"（例如标题含"搜索"）。
        /// 空 = 不做校验。
        /// </summary>
        public string SearchIsResultPattern;

        /// <summary>
        /// true = 搜索结果页的 HTML **必须含**关键词，否则判为"无结果"。
        ///
        /// ── 这个开关很危险，默认必须是 false ────────────────────
        /// 它原本是为了挡"站点忽略搜索词、返回固定页"这种最阴的失败
        /// （梦书中文实测：不管搜什么永远返回《长乐歌》）。
        ///
        /// 但实测发现它**会整批误杀真结果**：
        ///     笔尖中文 搜「万妖圣祖」→ 页面里**有 50 条书籍链接**，
        ///     但页面**没有回显关键词**（结果标题里也不含"万妖圣祖"这几个字，
        ///     站点是模糊匹配的）→ 被这个检查判成"无结果"，50 条全丢。
        ///     而搜「万相之王」时页面恰好回显了关键词 → 又能用。
        ///   结果就是"同一个站，有的词能搜、有的词不能"，用户完全无法理解。
        ///
        /// 判断依据本来就不该是"整页 HTML 里有没有这几个字" ——
        /// 那是把"站点回显了搜索框内容"误当成"站点按关键词检索了"。
        /// 所以：**只在确认某个站真的会忽略搜索词时才开**，而且要在
        /// docs/站点规则实测.md 里记下是哪次实测发现的。
        /// </summary>
        public bool SearchResultMustContainKeyword;

        /// <summary>
        /// 搜索结果所在表格行里，**作者在第几列**（0 基，和结果行里的 `&lt;td&gt;` 顺序一致）。
        /// -1 = 这一站没这列 / 不知道。
        ///
        /// ★ 为什么需要：以前 `RuleSite.Search` 只从结果页里抠出"链接 + 书名"，
        ///   作者、字数、状态、最新章节**全都丢掉了** —— 于是结果列表的
        ///   「作者」列永远是空的，用户抱怨"连是谁写的都看不到"。
        ///   而站点其实是给了的。实测笔尖中文（2026-10-18）：
        ///     &lt;td class="odd"&gt;&lt;a href="/35_35050/"&gt;圣妖之祖&lt;/a&gt;&lt;/td&gt;   ← 0 书名
        ///     &lt;td class="even"&gt;&lt;a ...&gt;第一百六十三章&lt;/a&gt;&lt;/td&gt;            ← 1 最新章节
        ///     &lt;td class="odd"&gt;灯鱼&lt;/td&gt;                                  ← 2 作者 ✓
        ///     &lt;td class="even"&gt;758K&lt;/td&gt;                                 ← 3 字数
        ///     &lt;td class="odd"&gt;17-08-05&lt;/td&gt;                              ← 4 更新
        ///     &lt;td class="even"&gt;连载&lt;/td&gt;                                 ← 5 状态
        ///   所以笔尖中文填 2。
        ///
        /// ★ 不要靠"猜第几列是作者"的启发式（比如"最短的那格"）：
        ///   连载/完本、字数、日期都很短，会误判，而且错得很隐蔽。
        /// </summary>
        public int SearchAuthorColumn = -1;

        /// <summary>
        /// 结果行里这些列要不要拼进「备注」（字数、状态、更新时间那类）。
        /// 空 = 不拼。拼之前会去掉书名列和作者列。
        /// </summary>
        public int[] SearchDescColumns;

        // ---------------- 详情页 ----------------

        public string BookTitlePattern;       // 捕获组 1 = 书名
        public string BookAuthorPattern;      // 捕获组 1 = 作者
        public string BookIntroPattern;       // 捕获组 1 = 简介
        public string BookCoverPattern;       // 捕获组 1 = 封面地址

        // ---------------- 目录 ----------------

        /// <summary>
        /// 目录页地址模板。空 = 详情页本身就是目录页（绝大多数情况）。
        ///   %s → 书籍路径（例如 /book/417022/）
        ///   %i → 书籍路径里的**第一段数字**（例如 417022）
        ///
        /// 为什么需要 %i：笔趣阁365 的详情页只给最近 21 章（`ul.newchapter`），
        /// 完整目录在**另一个地址** `/newbook/{id}/`。而那个地址要的是纯数字 id，
        /// 不是 /book/417022/ 这种路径，所以 %s 不够用。
        /// </summary>
        public string TocUrlTemplate;

        /// <summary>从目录页抽章节链接的正则，捕获组 1 = 链接</summary>
        public string TocLinkPattern;

        /// <summary>
        /// 先把目录**容器**框出来的正则（捕获组 1 = 容器 HTML），可空。
        ///
        /// 为什么需要它：章节链接正则是在整个页面上跑的，而页面上到处是
        /// `xxx.html`（分类页、排行榜、"最新章节"侧栏）。不先框定容器，
        /// 就会把 `list/1_1.html`、`paihangbang_allvisit/1.html` 当成章节抓进来
        /// —— 实测笔尖中文详情页上就有 7 个这种干扰链接。
        /// 框出容器之后只在容器内匹配，干扰自然消失，也不会误伤真正的章节。
        /// </summary>
        public string TocBlockPattern;

        /// <summary>目录页章节链接是绝对地址（http 开头）</summary>
        public bool TocLinksAreAbsolute;

        /// <summary>
        /// 目录链接相对于哪个地址。空 = 目录页地址。
        /// 笔尖中文的目录链接是 `603316.html` 这种**同级相对**地址，
        /// 必须相对详情页（/1_1245/）而不是相对站点根，否则会拼错。
        /// </summary>
        public string TocLinkBase;

        /// <summary>倒序目录（最新章节在前）—— 需要反转成阅读顺序</summary>
        public bool TocIsDescending;

        // ---------------- 正文 ----------------

        /// <summary>从正文页抽正文块的正则，捕获组 1 = 正文 HTML 片段</summary>
        public string ContentPattern;
        /// <summary>正文是 base64（捕获组 1 解出来的内容是 base64），需要解码</summary>
        public bool ContentIsBase64;
        /// <summary>剔除广告/水印行的正则，可空</summary>
        public string ContentStripPattern;
        /// <summary>正文页里"下一页"链接的正则（章内分页），可空</summary>
        public string ContentNextPagePattern;

        // ---------------- 基准测试 ----------------

        /// <summary>
        /// 用于"自动选最快站点"评分的固定书名，必须**实测能在这个站搜到**。
        /// 第一个是首选。空列表 = 该站不参与评分。
        /// </summary>
        public string[] KnownTitles;

        /// <summary>
        /// 章内分页时，正文页地址是否由**章节 id 直接算出**（像 biquga 的
        /// `cid.html` / `cid_1.html`）。
        ///
        /// 为什么这个标志关系到"会不会写错内容"：
        /// 批量并发抓取（HttpBatch）必须能**事先算出**每一页的地址。
        /// 如果站点是"下一页地址写在当前页 HTML 里"（例如 22biqu 的 #next_url），
        /// 那么批量路径只能抓到第一页，结果是**被静默截断的正文**
        /// （章节读起来首尾都正常，最难发现）。
        /// 所以只有**实测确认章内不分页**、或地址可按 id 推算的站点才置 true；
        /// 否则 RuleSite 不实现 IChapterUrlProvider，下载走逐章路径，宁可慢不写错。
        /// </summary>
        public bool ChapterPagesAddressableByKey;

        // ---------------- 自检 ----------------

        /// <summary>
        /// 校验规则表本身是否自洽。有问题就抛 —— 让错误在启动/自测暴露，
        /// 而不是等用户搜不到书才发现。
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrEmpty(Key)) throw new ArgumentException("SiteRule.Key 不能为空");
            if (string.IsNullOrEmpty(DisplayName)) throw new ArgumentException(Key + ": DisplayName 不能为空");
            if (string.IsNullOrEmpty(Base)) throw new ArgumentException(Key + ": Base 不能为空");
            if (!Base.EndsWith("/")) throw new ArgumentException(Key + ": Base 必须以 / 结尾");
            if (string.IsNullOrEmpty(SearchUrl)) throw new ArgumentException(Key + ": SearchUrl 不能为空");
            if (BookLinkPatterns == null || BookLinkPatterns.Length == 0)
                throw new ArgumentException(Key + ": BookLinkPatterns 不能为空");
            if (string.IsNullOrEmpty(TocLinkPattern))
                throw new ArgumentException(Key + ": TocLinkPattern 不能为空");
            if (string.IsNullOrEmpty(ContentPattern))
                throw new ArgumentException(Key + ": ContentPattern 不能为空");

            // 每个正则都必须能编译 —— 写错一个字符要让构建期就炸
            foreach (string p in BookLinkPatterns) MustCompile(p, "BookLinkPatterns");
            MustCompileIfSet(SearchIsResultPattern, "SearchIsResultPattern");
            MustCompileIfSet(BookTitlePattern, "BookTitlePattern");
            MustCompileIfSet(BookAuthorPattern, "BookAuthorPattern");
            MustCompileIfSet(BookIntroPattern, "BookIntroPattern");
            MustCompileIfSet(BookCoverPattern, "BookCoverPattern");
            MustCompile(TocLinkPattern, "TocLinkPattern");
            MustCompileIfSet(TocBlockPattern, "TocBlockPattern");
            MustCompile(ContentPattern, "ContentPattern");
            MustCompileIfSet(ContentStripPattern, "ContentStripPattern");
            MustCompileIfSet(ContentNextPagePattern, "ContentNextPagePattern");

            if (SearchIsPost && (SearchForm == null || SearchForm.Length == 0))
                throw new ArgumentException(Key + ": SearchIsPost=true 但 SearchForm 为空");

            if (SearchForm != null)
                foreach (string[] f in SearchForm)
                {
                    if (f == null || f.Length != 2)
                        throw new ArgumentException(Key + ": SearchForm 每项必须是 {name, value}");
                    if (string.IsNullOrEmpty(f[0]))
                        throw new ArgumentException(Key + ": SearchForm 字段名不能为空");
                }
        }

        private void MustCompile(string pattern, string field)
        {
            try { new System.Text.RegularExpressions.Regex(pattern); }
            catch (Exception ex)
            {
                throw new ArgumentException(Key + ": " + field + " 正则无法编译 —— " + ex.Message);
            }
        }

        private void MustCompileIfSet(string pattern, string field)
        {
            if (!string.IsNullOrEmpty(pattern)) MustCompile(pattern, field);
        }
    }
}
