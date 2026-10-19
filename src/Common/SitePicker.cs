using System;
using System.Collections.Generic;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// 「自动选最优站点」。
    ///
    /// 用户的要求原话是：**同名 + 下载速度最快 + 章节数最多**。
    /// 这三条会打架（最快的站可能少 500 章），所以不能简单排序，得合成一个目标：
    ///
    ///   预计总时长 = 章节数 × 每章耗时   ← **越小越好**
    ///
    /// 为什么用"总时长"而不是"先比章数、再比速度"：
    ///   · 一本 1800 章的书，快站 80ms/章 → 2.4 分钟；
    ///     慢站 460ms/章 → 13.8 分钟。差 5 倍，用户真正在意的是这个；
    ///   · 少 300 章的站哪怕快一倍，也是"下完还得再补"，不是最优。
    ///   总时长把两个维度自动合成，而且**能在日志里用一句话讲清楚**。
    ///
    /// 硬性前提：**书名必须"一样"**（归一化后完全相等），不做模糊匹配 ——
    ///   用户说的是"搜索名称一样的"，模糊匹配会把《万相之王》和
    ///   《万相之王（同人）》当成一本书，选错了比选慢更糟。
    /// </summary>
    internal static class SitePicker
    {
        /// <summary>站点速度未知时用的保守估计（ms/章）。宁可低估慢站，也不让它靠"未知"取胜。</summary>
        internal const double UnknownMsPerChapter = 600.0;

        /// <summary>
        /// "足够全"的门槛：章数 ≥ 最多者的这个比例才算同样完整。
        /// 0.95 的意思是"少 5% 以内视为一样全"（1800 章的书里差 90 章以内）。
        /// </summary>
        internal const double CompleteEnoughRatio = 0.95;

        /// <summary>章节数未知时用的估计：直接判"无法比较"，不参与夺冠。</summary>
        internal const int UnknownChapters = 0;

        /// <summary>一个候选站点上的同名书</summary>
        internal sealed class Candidate
        {
            public string SiteKey;
            public string SiteLabel;
            public string Title;

            /// <summary>章节数（0 = 未知）</summary>
            public int Chapters;
            /// <summary>章节数是怎么来的：搜索行里的"最新章节" / 目录实测 / 未知</summary>
            public string ChaptersSource = "未知";

            /// <summary>每章耗时 ms（≤0 = 未知）</summary>
            public double MsPerChapter;

            /// <summary>书名是否与关键词完全一致（归一化后）</summary>
            public bool Exact;
        }

        /// <summary>预计总时长（ms）；无法估计返回 PositiveInfinity</summary>
        public static double EstimatedTotalMs(Candidate c)
        {
            if (c == null) return double.PositiveInfinity;
            if (c.Chapters <= 0) return double.PositiveInfinity;
            double per = c.MsPerChapter > 0 ? c.MsPerChapter : UnknownMsPerChapter;
            return c.Chapters * per;
        }

        /// <summary>
        /// 挑出最优候选。没有可用候选返回 null，并在 <paramref name="reason"/> 里给一句人话。
        /// </summary>
        public static Candidate Pick(IList<Candidate> candidates, out string reason)
        {
            reason = "";
            if (candidates == null || candidates.Count == 0)
            {
                reason = "没有任何站点有这本书";
                return null;
            }

            // ① 硬过滤：必须同名
            var exact = new List<Candidate>();
            int unknownTitle = 0;
            foreach (var c in candidates)
            {
                if (c == null) continue;
                if (!c.Exact) { unknownTitle++; continue; }
                exact.Add(c);
            }
            if (exact.Count == 0)
            {
                reason = "没有书名完全一致的站点（有 " + unknownTitle + " 个站只有相近的，不算）";
                return null;
            }

            // ② 章数未知的不能夺冠（无法比较总时长），除非全都没章数
            var scored = new List<Candidate>();
            foreach (var c in exact) if (c.Chapters > 0) scored.Add(c);
            if (scored.Count == 0)
            {
                // 全都没章数：退回"谁快选谁"，都没有速度就按注册顺序第一个
                Candidate fastest = null;
                foreach (var c in exact)
                {
                    if (c.MsPerChapter <= 0) continue;
                    if (fastest == null || c.MsPerChapter < fastest.MsPerChapter) fastest = c;
                }
                if (fastest == null) fastest = exact[0];
                reason = "各站都没拿到章节数，退化为按速度选：" + fastest.SiteLabel;
                return fastest;
            }

            // ③ 两段式决策（**不是**单纯比总时长）
            //
            //   单纯比"总时长最短"会选错：A 站 1000 章 × 80ms = 80 秒，
            //   B 站 1800 章 × 60ms = 108 秒 —— A 更快，但**少 800 章**，下完还得补。
            //   而单纯比"章数最多"也会选错：只多 6 章却慢 6 倍的站不该赢。
            //
            //   所以：
            //     第一段 —— 章数达到"最多者"的 95% 才算**足够全**（少 5% 以内视为同样完整）；
            //     第二段 —— 在足够全的里面，选**预计总时长最短**的。
            int maxCh = 0;
            foreach (var c in scored) if (c.Chapters > maxCh) maxCh = c.Chapters;
            long need = (long)Math.Ceiling(maxCh * CompleteEnoughRatio);

            Candidate best = null;
            double bestMs = double.PositiveInfinity;
            foreach (var c in scored)
            {
                if (c.Chapters < need) continue;          // 不够全，不参与
                double ms = EstimatedTotalMs(c);
                if (best == null) { best = c; bestMs = ms; continue; }
                if (ms < bestMs - 1) { best = c; bestMs = ms; continue; }
                if (Math.Abs(ms - bestMs) <= 1)
                {
                    if (c.Chapters > best.Chapters) { best = c; bestMs = ms; }
                    else if (c.Chapters == best.Chapters && c.MsPerChapter > 0
                             && (best.MsPerChapter <= 0 || c.MsPerChapter < best.MsPerChapter))
                    { best = c; bestMs = ms; }
                }
            }
            if (best == null)
            {
                best = scored[0];                          // 理论上不会走到（need ≤ maxCh）
                bestMs = EstimatedTotalMs(best);
            }

            reason = string.Format("选中「{0}」：{1} 章 × {2} ≈ {3}（最全 {4} 章，够全门槛 {5} 章）",
                best.SiteLabel, best.Chapters,
                best.MsPerChapter > 0 ? ((int)best.MsPerChapter) + " ms/章" : "速度未知(按 " + (int)UnknownMsPerChapter + " 估)",
                FormatDuration(bestMs), maxCh, need);
            return best;
        }

        /// <summary>把候选和结论排成一张人看的表（写进日志）</summary>
        public static string Describe(IList<Candidate> candidates, Candidate best)
        {
            var sb = new StringBuilder();
            if (candidates == null || candidates.Count == 0)
            {
                sb.AppendLine("   · 没有可比较的站点");
                return sb.ToString();
            }
            sb.AppendLine(string.Format("   {0,-12} {1,-6} {2,-12} {3,-10} {4}", "站点", "章数", "每章", "预计总时长", "说明"));
            sb.AppendLine("   " + new string('-', 62));
            foreach (var c in candidates)
            {
                if (c == null) continue;
                string mark = (best != null && ReferenceEquals(c, best)) ? " ← 选它" : "";
                if (!c.Exact) mark += "（书名不一致，不参与）";
                sb.AppendLine(string.Format("   {0,-12} {1,-6} {2,-12} {3,-10} {4}{5}",
                    c.SiteLabel, c.Chapters > 0 ? c.Chapters.ToString() : "?",
                    c.MsPerChapter > 0 ? ((int)c.MsPerChapter) + " ms" : "未知",
                    c.Chapters > 0 ? FormatDuration(EstimatedTotalMs(c)) : "—",
                    c.Chapters > 0 ? c.ChaptersSource : "", mark));
            }
            return sb.ToString();
        }

        /// <summary>毫秒 → "2 分 24 秒"</summary>
        internal static string FormatDuration(double ms)
        {
            if (double.IsInfinity(ms) || ms <= 0) return "—";
            int totalSec = (int)Math.Round(ms / 1000.0);
            if (totalSec < 60) return totalSec + " 秒";
            int min = totalSec / 60, sec = totalSec % 60;
            if (min < 60) return min + " 分" + (sec > 0 ? " " + sec + " 秒" : "");
            int hour = min / 60; min = min % 60;
            return hour + " 小时" + (min > 0 ? " " + min + " 分" : "");
        }

        /// <summary>
        /// 从搜索行的"最新章节"里**估**章节数。
        ///
        /// 为什么能这么估：站点的搜索表格里就有"最新章节"，而它通常写着
        /// 「第一千八百三十七章 大结局」—— 章号就是这个站的**最大章号**，
        /// 拿来当"章节数"足够用来比较（误差只在缺章/番外上）。
        /// 好处是**完全不用抓目录**：抓一次目录要 10~25 秒（pmzw 实测 25 秒），
        /// 为"选站"付这个代价不可接受。
        /// </summary>
        public static int EstimateChapters(string latestChapterTitle)
        {
            int n;
            if (ChapterNumber.TryParse(latestChapterTitle, out n)) return n;
            return UnknownChapters;
        }

        /// <summary>书名归一化：只比"书本身"，忽略《》、空白、站点后缀</summary>
        internal static string NormalizeTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == '《' || c == '》' || c == '「' || c == '」' || c == '"' || c == '"' || c == '"') continue;
                sb.Append(c);
            }
            string t = sb.ToString();
            // 去掉常见后缀（"最新章节"、"全文阅读"、"无弹窗"…）
            string[] tails = new string[]
            {
                "最新章节", "全文阅读", "无弹窗", "免费阅读", "在线阅读", "全文免费阅读", "笔趣阁",
            };
            foreach (string tail in tails)
            {
                int at = t.IndexOf(tail, StringComparison.Ordinal);
                if (at > 0) { t = t.Substring(0, at); break; }
            }
            return t.Trim();
        }

        /// <summary>两个书名是不是"一样的"（归一化后完全相等）</summary>
        public static bool IsSameTitle(string a, string b)
        {
            string na = NormalizeTitle(a), nb = NormalizeTitle(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            return string.Equals(na, nb, StringComparison.Ordinal);
        }
    }
}
