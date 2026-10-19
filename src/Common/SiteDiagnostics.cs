using System;
using System.Text.RegularExpressions;

namespace TomatoBiquga
{
    /// <summary>
    /// 站点"空结果"的原因诊断。
    ///
    /// ★ 为什么需要（吃了很多次亏）：站点返回 0 条时，程序以前只会说
    ///   "没搜到这本书" —— 但真实原因至少有三种，处理方式完全不同：
    ///     ① 站点确实没有这本书      → 换个源
    ///     ② 站点限流/被拦           → 等一会儿或降并发
    ///     ③ **站点改版，规则失效**  → 必须改 SiteRules，重试一万次也没用
    ///   我以前就卡在这里：笔趣阁365 搜「万妖圣祖」返回 0 条，
    ///   到底是限流还是改版？只能靠手工 curl 一遍遍试。
    ///
    /// 做法：拿**已经抓下来的 HTML** 做几个便宜的判断（不发额外请求），
    /// 给出可执行的原因。纯函数，便于用录制的页面做离线断言。
    ///
    /// 这是 Scrapling"自适应选择器"的廉价版：它会在改版后自动重新定位元素，
    /// 我们做不到（规则是正则），但至少能**立刻说清"规则可能失效了"**，
    /// 而不是让用户对着 0 条发呆。
    /// </summary>
    internal static class SiteDiagnostics
    {
        /// <summary>页面里出现这些词，说明站点**明确**表示"没有结果"（不是规则坏了）</summary>
        internal static readonly string[] NoResultMarkers = new string[]
        {
            "没有找到", "未找到", "找不到", "无搜索结果", "没有搜索到",
            "抱歉，没有", "搜索结果为空", "no results", "not found",
            "搜索\"\"的结果", "搜索“”的结果",       // 笔趣阁365 关键词没传进去时的形态
        };

        /// <summary>页面里出现这些词，说明被拦/限流</summary>
        internal static readonly string[] BlockedMarkers = new string[]
        {
            "访问过于频繁", "请求过于频繁", "请稍后再试", "访问受限",
            "验证码", "人机验证", "cloudflare", "attention required",
            "403 forbidden", "access denied", "too many requests",
        };

        /// <summary>
        /// 诊断"搜索返回 0 条"的原因，返回一句给用户看的话。
        /// 永远不返回 null（最差也能说"原因不明"）。
        /// </summary>
        /// <param name="html">已经抓下来的搜索页 HTML（null 表示根本没拿到页面）</param>
        /// <param name="linkCount">规则从这一页里抽到了几条书籍链接（通常是 0）</param>
        /// <param name="expectedPattern">这条规则期望能匹配到的模式（可为 null）</param>
        public static string DiagnoseEmptySearch(string html, int linkCount, string expectedPattern)
        {
            if (html == null) return "没拿到页面（网络失败或被拦），不是规则问题";
            if (html.Length == 0) return "站点返回了空页面（可能被拦）";
            if (linkCount > 0) return "抽到了 " + linkCount + " 条链接";

            string low = html.ToLowerInvariant();
            bool blocked = ContainsAny(low, BlockedMarkers);
            bool noResult = ContainsAny(low, NoResultMarkers);

            if (blocked)
                return "页面像是**被拦/限流**（含验证码或访问受限字样）—— 等几分钟或降低并发再试";
            if (noResult)
                return "站点**明确表示没有结果**（含\"没有找到/搜索结果为空\"之类）—— 换关键词或换源";

            // 页面不小、也没说"没结果"、也没被拦，却一条链接都抽不到 → 极可能改版
            if (html.Length > 2000)
            {
                string extra = "";
                if (!string.IsNullOrEmpty(expectedPattern))
                {
                    try { if (Regex.IsMatch(html, expectedPattern, RegexOptions.IgnoreCase)) extra = "（但结果页表头还能匹配，说明页面结构没坏）"; }
                    catch { }
                }
                // ★ 文案必须带上"搜索冷却"这一种。实测（排名中文网）：短时间搜太多次后，
                //   站点返回**正常的空结果页**（表头在、没有结果行），从 HTML 上跟"改版"分不开。
                //   只说"改版"会让用户白折腾；所以要如实说二选一，并给出可执行的下一步。
                return "页面正常返回（" + html.Length + " 字符）却一条链接都没匹配到 —— "
                     + "**要么站点改版了（规则要更新），要么是搜太频繁被冷却**"
                     + extra + "。先等 1~2 分钟再搜一次；若一直空，才是改版。";
            }

            return "页面只有 " + html.Length + " 字符，内容太少 —— 可能被拦或站点异常";
        }

        /// <summary>
        /// 诊断"目录为空"的原因。和搜索版同一套思路。
        /// </summary>
        public static string DiagnoseEmptyToc(string html, int chapterCount)
        {
            if (html == null) return "没拿到目录页（网络失败或被拦）";
            if (html.Length == 0) return "目录页返回空内容（可能被拦）";
            if (chapterCount > 0) return "目录里有 " + chapterCount + " 章";

            string low = html.ToLowerInvariant();
            if (ContainsAny(low, BlockedMarkers))
                return "目录页像是**被拦/限流** —— 等几分钟或降低并发再试";
            if (ContainsAny(low, NoResultMarkers))
                return "站点明确说这个页面没有内容 —— 这本书可能下架了";

            if (html.Length > 2000)
                return "目录页正常返回（" + html.Length + " 字符）却解析不出任何章节 —— "
                     + "**站点很可能改版了，目录规则需要更新**";
            return "目录页只有 " + html.Length + " 字符，内容太少 —— 可能被拦或站点异常";
        }

        /// <summary>
        /// 诊断"这一章抓到的正文太短"（&lt; 40 字符）到底是什么情况。
        ///
        /// ★ 以前一律写"站点侧空内容/公告"，但短文本至少有两种：
        ///   ① 站点真的没内容（"正在手打中，请稍等片刻"）→ 换源也没用，认了；
        ///   ② **被拦/限流的提示页**（"访问过于频繁"）→ 等一会儿重试就有。
        ///   混在一起用户要么白等、要么白换源，所以这里分清楚。
        ///
        /// 返回 null 表示"看不出特殊原因"（调用方回落到默认文案）。
        /// </summary>
        public static string DiagnoseShortChapter(string text)
        {
            if (text == null) return "抓到的正文是空的";
            string t = text.Trim();
            if (t.Length == 0) return "抓到的正文是空的";

            string low = t.ToLowerInvariant();
            if (ContainsAny(low, BlockedMarkers))
                return "疑似被拦/限流（正文里出现验证码或\"访问过于频繁\"之类）—— 等几分钟重试，不必换源";

            // 站点常见的"这章还没写完"提示
            if (low.IndexOf("正在手打", StringComparison.Ordinal) >= 0
                || low.IndexOf("手打中", StringComparison.Ordinal) >= 0
                || low.IndexOf("请稍等", StringComparison.Ordinal) >= 0
                || low.IndexOf("本章未完", StringComparison.Ordinal) >= 0
                || low.IndexOf("待更新", StringComparison.Ordinal) >= 0
                || low.IndexOf("正在更新", StringComparison.Ordinal) >= 0)
                return "站点这一章本身还没内容（\"正在手打中/待更新\"）—— 换源也没用，过几天再来";

            if (t.Length <= 12)
                return "正文只有 " + t.Length + " 个字符（疑似占位提示）—— 稍后重试看看";

            return null;
        }

        private static bool ContainsAny(string lowHtml, string[] markers)
        {
            if (string.IsNullOrEmpty(lowHtml) || markers == null) return false;
            foreach (string m in markers)
            {
                if (string.IsNullOrEmpty(m)) continue;
                if (lowHtml.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}
