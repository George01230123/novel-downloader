using System;

namespace TomatoBiquga
{
    /// <summary>
    /// 进度文本：百分比 / 状态行 / 窗口标题。
    ///
    /// ★ 为什么单独抽出来：这几行字以前是**拼在界面回调里**的，
    ///   于是"百分比对不对、比 0 章和 0 总量会不会崩、标题会不会残留"都没法离线断言。
    ///   抽成纯函数后，边界（done=0 / total=0 / done>total / 时长很大）全都能钉住。
    ///
    /// 为什么还要把进度写进**窗口标题**：
    ///   状态栏在最底部，日志区一拉大或者窗口一小，它就看不见了 ——
    ///   用户的第一反应是"没有进度条"。标题栏永远在最上面，一眼就能看到。
    /// </summary>
    internal static class ProgressText
    {
        /// <summary>百分比（0~100，越界自动夹住；total ≤ 0 时返回 0）</summary>
        public static int Percent(int done, int total)
        {
            if (total <= 0) return 0;
            if (done <= 0) return 0;
            if (done >= total) return 100;
            // 四舍五入：44.5% 显示 45%，别让用户看到"44%"卡在最后一章
            return (int)Math.Round(done * 100.0 / total, MidpointRounding.AwayFromZero);
        }

        /// <summary>下载状态行。例：下载中 812/1838（44%）　已用 2分03秒，当前 5.2 章/秒，预计还需 1分12秒　（并发 12）</summary>
        public static string DownloadLine(int done, int total, TimeSpan used,
            double chaptersPerSecond, int workers)
        {
            if (total <= 0) return "下载中…";
            if (done <= 0) return string.Format("开始下载 0/{0} 章…", total);

            string rate = chaptersPerSecond > 0
                ? string.Format("，当前 {0:F1} 章/秒", chaptersPerSecond)
                : "";
            string eta = "";
            if (done < total && used.TotalSeconds > 0)
            {
                double per = used.TotalSeconds / done;
                var left = TimeSpan.FromSeconds(per * (total - done));
                eta = "，预计还需 " + Fmt(left);
            }
            return string.Format("下载中 {0}/{1}（{2}%）　已用 {3}{4}{5}　（并发 {6}）",
                done, total, Percent(done, total), Fmt(used), rate, eta, workers);
        }

        /// <summary>
        /// 无精确总数的阶段（载入目录 / 遍历整站）：只报"在做什么"。
        /// 这类操作**没有可靠的总数**（目录翻几页要翻到才知道），
        /// 所以这里不编造百分比 —— 编一个假的 37% 比"不定进度"更让人困惑。
        /// </summary>
        public static string PhaseLine(string phase, string detail = null)
        {
            string p = string.IsNullOrEmpty(phase) ? "处理中" : phase;
            return string.IsNullOrEmpty(detail) ? p + "…" : p + "…" + detail;
        }

        /// <summary>
        /// 窗口标题。phase 为空 → 原样返回；否则在标题后加「（下载 44%）」这类后缀。
        /// </summary>
        public static string Title(string baseTitle, string phase, int done, int total)
        {
            if (string.IsNullOrEmpty(baseTitle)) return "";
            if (string.IsNullOrEmpty(phase)) return baseTitle;
            if (total > 0)
                return string.Format("{0}（{1} {2}/{3}　{4}%）", baseTitle, phase, done, total, Percent(done, total));
            return string.Format("{0}（{1}…）", baseTitle, phase);
        }

        /// <summary>时长格式化：3分20秒 / 1小时02分 / 45秒</summary>
        public static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1) return string.Format("{0}小时{1:D2}分", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}分{1:D2}秒", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0:F0}秒", t.TotalSeconds);
        }
    }
}
