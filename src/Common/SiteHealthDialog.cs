using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TomatoBiquga
{
    /// <summary>
    /// 「站点健康」小窗：把**实测到的**各站情况摊开给用户看，而不是让他从日志里猜。
    ///
    /// ★ 为什么需要：
    ///   现在开下载前根本不知道哪个站能用 —— 只有"下到一半发现这个源不可用"。
    ///   而"这本书哪个源能下、大概多快"恰恰是最该先知道的事：
    ///   笔尖中文 79ms/章 vs 笔趣阁 PC 457ms/章，选错源就是 5 倍时间。
    ///
    /// 数据来源：<see cref="SiteRater"/> 的实测结果（有缓存就用缓存，
    /// 也可以点「重新实测」现场联网）。**本窗口自己不写任何配置**。
    /// </summary>
    internal class SiteHealthDialog : Form
    {
        private readonly ListView _list;
        private readonly Button _btnMeasure;
        private readonly Button _btnClose;
        private readonly Label _hint;
        private readonly Action<string> _log;
        private IList<SiteScore> _scores;
        private bool _busy;

        /// <summary>测试用：当前表格里的行数</summary>
        internal int RowCountForTest { get { return _list == null ? 0 : _list.Items.Count; } }

        /// <summary>测试用：第 i 行第 col 列的文本</summary>
        internal string CellForTest(int row, int col)
        {
            if (_list == null || row < 0 || row >= _list.Items.Count) return null;
            return _list.Items[row].SubItems[col].Text;
        }

        public SiteHealthDialog(IList<SiteScore> scores, Action<string> log)
        {
            _scores = scores;
            _log = log;

            Text = "站点健康（实测）";
            StartPosition = FormStartPosition.CenterParent;
            Width = 760;
            Height = 420;
            MinimumSize = new Size(560, 300);
            Font = new Font("Microsoft YaHei UI", 9F);
            // 高 DPI 下别用固定像素硬顶
            AutoScaleMode = AutoScaleMode.Dpi;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(0, 0),   // 由父容器宽度决定
                Text = "「可用」= 搜索 + 目录 + 正文三项都测通；耗时是**单章**耗时，越小越快。",
            };
            root.Controls.Add(_hint, 0, 0);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                MultiSelect = false,
                HideSelection = false,
            };
            _list.Columns.Add("站点", 150);
            _list.Columns.Add("搜索", 70, HorizontalAlignment.Right);
            _list.Columns.Add("目录", 70, HorizontalAlignment.Right);
            _list.Columns.Add("正文/章", 80, HorizontalAlignment.Right);
            _list.Columns.Add("目录章数", 80, HorizontalAlignment.Right);
            _list.Columns.Add("结论", 200);
            _list.Columns.Add("实测时间", 110);
            root.Controls.Add(_list, 0, 1);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
            };
            _btnMeasure = new Button { Text = "重新实测（联网）", AutoSize = true };
            _btnMeasure.Click += (s, e) => StartMeasure();
            _btnClose = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };
            bar.Controls.Add(_btnMeasure);
            bar.Controls.Add(_btnClose);
            root.Controls.Add(bar, 0, 2);

            Controls.Add(root);
            CancelButton = _btnClose;
            AcceptButton = _btnMeasure;

            Fill(_scores);
        }

        /// <summary>用一批评分刷新表格（空 → 显示一行提示，而不是空表）</summary>
        internal void Fill(IList<SiteScore> scores)
        {
            _scores = scores;
            if (_list == null) return;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                if (scores == null || scores.Count == 0)
                {
                    var it = new ListViewItem("（还没有实测数据）");
                    it.SubItems.Add("—"); it.SubItems.Add("—"); it.SubItems.Add("—");
                    it.SubItems.Add("—");
                    it.SubItems.Add("点下面的「重新实测（联网）」");
                    it.SubItems.Add("—");
                    _list.Items.Add(it);
                    return;
                }
                foreach (var s in SiteRater.Rank(scores))
                {
                    var it = new ListViewItem(string.IsNullOrEmpty(s.DisplayName) ? s.Key : s.DisplayName);
                    it.SubItems.Add(Ms(s.SearchMeasured ? s.SearchMs : double.PositiveInfinity));
                    it.SubItems.Add(Ms(s.TocMeasured ? s.TocMs : double.PositiveInfinity));
                    it.SubItems.Add(Ms(s.ChapterMeasured ? s.ChapterMs : double.PositiveInfinity));
                    it.SubItems.Add(s.TocCount > 0 ? s.TocCount.ToString() : "—");
                    it.SubItems.Add(s.Ok ? "可用" : ("不可用：" + (s.Error ?? "未知")));
                    it.SubItems.Add(s.MeasuredAt == DateTime.MinValue ? "—" : s.MeasuredAt.ToString("MM-dd HH:mm"));
                    if (!s.Ok) it.ForeColor = Color.FromArgb(150, 150, 150);   // 不可用的灰掉
                    _list.Items.Add(it);
                }
            }
            finally { _list.EndUpdate(); }
        }

        private static string Ms(double v)
        {
            if (double.IsInfinity(v) || v <= 0) return "∞";
            return ((int)v) + " ms";
        }

        /// <summary>后台实测全部站点（不阻塞界面）</summary>
        private void StartMeasure()
        {
            if (_busy) return;
            _busy = true;
            _btnMeasure.Enabled = false;
            _hint.Text = "正在实测（每站 3 个请求：搜索 / 目录 / 一张正文）…";
            var t = new Thread(delegate ()
            {
                List<SiteScore> result = null;
                string err = null;
                try
                {
                    result = SiteRater.MeasureAll(delegate (string line)
                    {
                        try { if (_log != null) _log(line); } catch { }
                    });
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        _btnMeasure.Enabled = true;
                        if (err != null)
                        {
                            _hint.Text = "实测失败：" + err;
                            return;
                        }
                        try { SiteRater.Save(result, SiteProfileStore.DefaultPath); }
                        catch { }
                        Fill(result);
                        _hint.Text = "实测完成（" + (result == null ? 0 : result.Count) + " 个站点）。"
                            + "「可用」= 搜索 + 目录 + 正文三项都测通。";
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Name = "SiteHealth";
            t.Start();
        }

    }
}
