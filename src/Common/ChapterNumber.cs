using System;

namespace TomatoBiquga
{
    /// <summary>
    /// 从章节标题里解析**章节号**。
    ///
    /// 为什么需要（源自 bookCapture 的做法）：
    ///   · **缺失报告**现在只列"第 N 章 &lt;标题&gt;"（N 是列表序号，不是章节号），
    ///     用户看不出"到底缺了哪几章"。有了章节号就能写"缺 12~15、40、88"。
    ///   · **目录顺序校验**：站点偶尔给逆序/乱序，现在只有 <see cref="SiteRule.TocIsDescending"/>
    ///     这个"整体翻转"开关，遇到乱序就静默照抄 → 落盘顺序错乱且没人发现。
    ///   · 判重、补章对齐也需要"第 12 章 == 第十二章 == 第壹拾贰章 == 第１２章"。
    ///
    /// 设计原则：
    ///   · **纯函数**，不读文件不联网，离线可测；
    ///   · 解析不出来就返回 false（楔子/番外/序 这类没有章号的必须返回 false，
    ///     调用方据此**保持原顺序**，绝不参与数值排序）；
    ///   · 不做任何"猜测式修复"（例如把"第二百零一章"补成 201 之外的别的值）。
    ///
    /// 只支持"第N章/节/回/集/卷/篇"这一种壳；壳外内容一律忽略。
    /// </summary>
    internal static class ChapterNumber
    {
        /// <summary>章号上限：再大基本就是脏数据（"第 99999999 章"）</summary>
        internal const int MaxNumber = 100000;

        private static readonly char[] Units = new char[] { '章', '节', '回', '集', '卷', '篇' };

        /// <summary>
        /// 解析章节号。成功返回 true 且 number ≥ 1。
        /// 没有章号（楔子/序/番外/公告）或数字不合法 → false，out 为 0。
        /// </summary>
        public static bool TryParse(string title, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(title)) return false;
            string t = title.Trim();
            if (t.Length == 0) return false;

            // 找"第"；找不到就不是我们认识的形态
            int at = t.IndexOf('第');
            if (at < 0) return false;

            // 从"第"之后扫到第一个单位字（章/节/回/集/卷/篇）
            int from = at + 1;
            int to = -1;
            for (int i = from; i < t.Length; i++)
            {
                if (Array.IndexOf(Units, t[i]) >= 0) { to = i; break; }
                // 中间不允许出现空白之外的"新壳"，否则多半是"第三卷 第一章"这类复合标题
                if (t[i] == '第') return false;
            }
            if (to < 0) return false;                 // 没有单位字 → 不是章节标题

            string inner = t.Substring(from, to - from).Trim();
            if (inner.Length == 0 || inner.Length > 12) return false;   // 太短/太长都不认

            int v;
            if (TryParseArabic(inner, out v) || TryParseChinese(inner, out v))
            {
                if (v <= 0 || v > MaxNumber) return false;
                number = v;
                return true;
            }
            return false;
        }

        /// <summary>全角数字 → 半角；其它字符原样</summary>
        internal static string HalfWidthDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(c >= '０' && c <= '９' ? (char)('0' + (c - '０')) : c);
            return sb.ToString();
        }

        /// <summary>纯阿拉伯数字（允许前后空白、全角）</summary>
        internal static bool TryParseArabic(string s, out int number)
        {
            number = 0;
            string h = HalfWidthDigits(s).Trim();
            if (h.Length == 0) return false;
            foreach (char c in h) if (c < '0' || c > '9') return false;
            return int.TryParse(h, out number);
        }

        /// <summary>
        /// 中文数字（含大写）。支持 零〇一二三四五六七八九十百千万两 + 壹贰叁肆伍陆柒捌玖拾佰仟。
        /// 用"按位累加"而不是查表：查表只能覆盖到几十，而"第一千二百三十四"必须有算法。
        /// </summary>
        internal static bool TryParseChinese(string s, out int number)
        {
            number = 0;
            if (string.IsNullOrEmpty(s)) return false;

            long total = 0;     // 已结算的"万"以上部分
            long section = 0;   // 当前小段（千百十）
            int digit = 0;      // 待结算的个位
            bool any = false;

            foreach (char raw in s)
            {
                char c = raw;
                int d = DigitOf(c);
                if (d >= 0) { digit = d; any = true; continue; }

                int unit = UnitOf(c);       // 十=10 / 百=100 / 千=1000
                if (unit > 0)
                {
                    if (digit == 0) digit = 1;      // "十二" = 12，"一百一十" 的"十"同理
                    section += (long)digit * unit;
                    digit = 0;
                    any = true;
                    continue;
                }

                if (c == '万')
                {
                    section += digit;
                    digit = 0;
                    if (section == 0) section = 1;  // "万" 单用按 10000
                    total += section * 10000;
                    section = 0;
                    any = true;
                    continue;
                }

                if (c == '零' || c == '〇') { digit = 0; any = true; continue; }

                return false;                        // 不认识的字 → 不认这个标题
            }

            if (!any) return false;
            long v = total + section + digit;
            if (v <= 0 || v > MaxNumber) return false;
            number = (int)v;
            return true;
        }

        private static int DigitOf(char c)
        {
            switch (c)
            {
                case '零': case '〇': return 0;
                case '一': case '壹': return 1;
                case '二': case '贰': case '两': case '兩': return 2;
                case '三': case '叁': case '參': return 3;
                case '四': case '肆': return 4;
                case '五': case '伍': return 5;
                case '六': case '陆': case '陸': return 6;
                case '七': case '柒': return 7;
                case '八': case '捌': return 8;
                case '九': case '玖': return 9;
                default: return -1;
            }
        }

        private static int UnitOf(char c)
        {
            switch (c)
            {
                case '十': case '拾': return 10;
                case '百': case '佰': return 100;
                case '千': case '仟': return 1000;
                default: return 0;
            }
        }

        /// <summary>
        /// 把一组章节号压成"缺号摘要"，例如 [12,13,14,40,88,89] → "12~14、40、88~89"。
        /// 已排序或未排序都接受；重复号码会被合并。
        /// </summary>
        public static string DescribeRanges(System.Collections.Generic.IList<int> numbers)
        {
            if (numbers == null || numbers.Count == 0) return "";
            var sorted = new System.Collections.Generic.List<int>(numbers);
            sorted.Sort();
            var sb = new System.Text.StringBuilder();
            int i = 0;
            while (i < sorted.Count)
            {
                int start = sorted[i], end = start;
                int j = i + 1;
                while (j < sorted.Count)
                {
                    if (sorted[j] == end) { j++; continue; }        // 重复
                    if (sorted[j] == end + 1) { end = sorted[j]; j++; continue; }
                    break;
                }
                if (sb.Length > 0) sb.Append('、');
                if (end == start) sb.Append(start);
                else if (end == start + 1) sb.Append(start).Append('、').Append(end);
                else sb.Append(start).Append('~').Append(end);
                i = j;
            }
            return sb.ToString();
        }
    }
}
