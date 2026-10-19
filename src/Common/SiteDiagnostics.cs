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
                    try { if (Regex.IsMatch(html, expectedPattern, RegexOptions.IgnoreCase)) extra = "（但验证规则还能匹配，可能只是页面上没结果）"; }
                    catch { }
                }
                return "页面正常返回（" + html.Length + " 字符）却一条链接都没匹配到 —— "
                     + "**站点很可能改版了，规则需要更新**" + extra;
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
