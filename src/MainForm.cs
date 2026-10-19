using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TomatoBiquga
{
    public class MainForm : Form
    {
        private ComboBox cboSite;
        private TextBox txtKeyword;
        private Button btnSearch;
        private Button btnLocal;   // 本地索引检索（不联网）
        private Button btnLoad;
        private Button btnReload;
        private Button btnWebSearch;
        private ListView lstBooks;
        private ListView lstChapters;
        private Button btnSelectAll, btnSelectNone, btnInvert;
        private Button btnDownload, btnCancel, btnOpenFolder;
        private Button btnDownloadAll;
        private Label lblSel;
        private Button btnOfficial, btnCoreSetup;
        private ProgressBar progress;
        private Label lblStatus;
        private TextBox txtLog;
        private TextBox txtOutput;
        private Button btnBrowse;
        private Button btnSettings;

        private ISite _site;
        private SplitContainer _split;
        private CheckBox chkOffline;
        private CheckBox chkAutoSite;
        private AppSettings _settings;     // 并发/间隔/重试，来自 exe 同目录的 settings.ini
        private SiteProfile _profile;      // 当前站点的连接参数（站点配置.ini，可手改）
        private string _lastOfficialFile;   // 官方工具最近一次的输出文件（用于「打开保存目录」）
        private FlowLayoutPanel _rowFanqie; // 番茄专用的第三行（平时隐藏）
        private FlowLayoutPanel _row1, _row2; // 顶部前两行（用来算顶部面板该多高）
        private Panel _kwHost;              // 关键词输入框的容器（宽度随窗口伸缩）
        private FlowLayoutPanel _chapFlow;   // 章节栏第一行按钮
        private FlowLayoutPanel _chapFlow2;  // 章节栏第二行按钮（导出类）
        private Button btnUpdate;           // 更新已下载的书（只补新章节）
        private Button btnExportEpub;       // 导出 EPUB
        private Button btnExportMd;
        private Button btnExportTxt;         // 导出 Markdown
        private Button btnFillMissing;      // 补齐缺章
        private Button btnShelf;            // 本地书架（下载记录 + 一键追更）
        private Button btnQueue;            // 任务队列（一次下多本）
        private Button btnCheckUpdate;      // 检查更新
        private Button btnProbeSites;       // 检测站点可用性
        private Button btnAiSettings;       // AI 裁决错字的设置
        private Button btnDetectTypos;      // 错字检测（双源比对）
        private CheckBox chkTraditional;    // 输出繁体（繁简转换）

        /// <summary>
        /// 把分隔条位置夹到合法范围内，并保证**首次显示时有一个稳定、两边都能用的默认位置**。
        ///
        /// ★ 为什么重写了：原来只在 SizeChanged / HandleCreated 里算，而那两个时刻
        ///   窗体的最终尺寸还没定，`_preferredSplit` 也还没被用户设过 ——
        ///   于是默认位置变成了"看运气"：实测同一个 exe 连续启动两次，
        ///   一次结果区占满、一次章节区占满（用户截图里就是这个现象：
        ///   两张图中间区域的高度完全不同，还抱怨"显示有问题"）。
        ///
        ///   现在的规则：
        ///     · 用户拖过分隔条（_preferredSplit > 0）→ 尊重用户的值
        ///     · 没拖过 → 用**固定比例 45%**，并保证
        ///         结果区 ≥ 170px（能看见 5~6 行搜索结果）
        ///         章节区 ≥ 240px（能看见 8~9 行章节）
        ///       两个都放不下时（窗口特别矮）优先保结果区，因为"搜完看不见结果"
        ///       比"章节列表短"更让人困惑。
        /// </summary>
        private void ClampSplitter()
        {
            if (_split == null) return;
            try
            {
                int h = _split.Height;
                if (h <= 0) return;

                const int WantResults = 170;    // 结果区理想最小高度
                const int WantChapters = 240;   // 章节区理想最小高度

                int min = _split.Panel1MinSize;                                  // 80
                int max = h - _split.Panel2MinSize - _split.SplitterWidth;       // h-106
                if (max < min) return;             // 空间实在不够，先不动

                int want;
                if (_userMovedSplit && _preferredSplit > 0)
                {
                    want = _preferredSplit;        // 用户真拖过才听用户的
                }
                else
                {
                    // 没拖过：固定 45%，再用"两边都够用"的目标高度夹一次
                    want = (int)(h * 0.45);
                    if (want < WantResults) want = WantResults;
                    if (h - want - _split.SplitterWidth < WantChapters)
                        want = h - _split.SplitterWidth - WantChapters;
                }

                if (want < min) want = min;
                if (want > max) want = max;
                if (_split.SplitterDistance != want) _split.SplitterDistance = want;
            }
            catch (Exception)
            {
                // 布局竞态时忽略，下一次尺寸变化会再夹一次
            }
        }

        private int _preferredSplit;

        /// <summary>用户是否**真的**拖过分隔条。false 时每次布局都按比例重算。</summary>
        private bool _userMovedSplit;

        /// <summary>
        /// 窗体是否已经完成首次显示（Shown 之后置 true）。
        /// 用来区分"布局期间程序自己触发的事件"和"用户之后的操作"。
        /// </summary>
        private bool _layoutDone;
        private BookInfo _currentBook;
        private volatile bool _cancel;
        private volatile bool _busy;

        public MainForm()
        {
            // ★ 第一行就定下"要不要弹窗/联网" —— 因为构造函数末尾会 BeginInvoke 探活。
            //   测试套件会在 new 之前把 SuppressByDefault 置 true（见 OfflineTests.Run）。
            SuppressDialogs = SuppressByDefault;

            // 标题避开第三方商标：产品名用 ASCII 的 novel-downloader（= 仓库名），中文名只作说明。
            // ★ 版本号从程序集读，不写死在字符串里 —— 之前这里硬编码 "v1.0.5"，
            //   发到 1.2.0 之后标题还显示 1.0.5（用户看截图会以为装的是旧版）。
            Text = "小说下载器 v" + UpdateChecker.CurrentVersionText + "（免安装单文件版）";
            Width = 1000;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            MinimumSize = new Size(820, 600);
            // 用 Font 缩放而不是 Dpi 缩放：本项目控件宽度是写死的数字（按钮 40~112px），
            // Dpi 模式只会缩放窗体尺寸，不会缩放这些数字 → 在 125% 的机器上按钮文字会被切掉。
            // Font 模式按字体比例缩放**所有**尺寸（含控件宽高），布局才跟得上。
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 15F);   // 9pt 微软雅黑在 96dpi 下的高度
            // 先加载设置再建界面：BuildUi 会在日志里打印当前设置
            _settings = AppSettings.Current;

            // ★ 把 Common 层的日志接到界面日志区。
            //   原因：Log.Line 原来只写 Console，而 GUI(winexe) 进程没有控制台 ——
            //   Common 层写的诊断信息在界面里**根本看不到**，排查真实故障时一片黑
            //   （"搜索索引读取失败"这种关键线索就是这么消失的）。
            //
            //   ★★ 必须直接追加到控件，**不能**绕回 MainForm.Log：
            //   一旦 Log() 内部（现在或以后）又调了 Log.Line，就变成
            //   Log.Line → Sink → MainForm.Log → Log.Line → … 无限递归。
            //   直接追加是这条链路的终点，最安全。
            //
            //   这里要写全限定名：MainForm 自己有个 Log(string) 方法，
            //   直接写 `Log.Sink` 会被解析成那个方法（实测编译报 CS0119）。
            TomatoBiquga.Log.Sink = delegate (string line) { AppendToLogBox(line + Environment.NewLine); };

            BuildUi();
        }

        /// <summary>
        /// 只往日志控件追加文本（跨线程安全）。**不写 Console、不碰 Log.Line** ——
        /// 它是日志链路的终点，往上再绕一圈就会递归。
        /// </summary>
        private void AppendToLogBox(string text)
        {
            try
            {
                if (txtLog == null || txtLog.IsDisposed) return;
                var box = txtLog;
                Action append = delegate ()
                {
                    try
                    {
                        box.AppendText(text);
                        if (box.TextLength > 400000)
                            box.Text = box.Text.Substring(box.TextLength - 200000);
                    }
                    catch { }
                };
                if (box.InvokeRequired) { try { box.BeginInvoke(append); } catch { } }
                else append();
            }
            catch { }
        }

        // ------------------------------------------------------------ 界面

        private void BuildUi()
        {
            // ============================================================
            //  布局原则（这一版重构的原因：老代码全是硬编码 Location，
            //  结果 1000px 宽时最右边按钮被切掉、"（还没载入目录）"被按钮盖住）：
            //    · 每行用 Dock 排：按钮靠右、输入框/文字用 Fill 吃掉剩余宽度
            //    · 控件一律 AutoSize（文字宽度由 TextRenderer 实测过，见 docs/界面布局.md）
            //    · 顶部两行高度固定，内容底部留 3px，避免中文按钮的下边被裁
            //    · 最小窗口 820x600 时所有控件必须完整可见（有 _layoutprobe 与离线单测守着）
            // ============================================================

            // 高度 = 各行高度之和 + 上下内边距。
            // 行1 32 + 行2 30 + 行4 30 = 92，加 Padding(10,6,10,4) 的 10 → 102。
            var top = new Panel { Dock = DockStyle.Top, Height = 102, Padding = new Padding(10, 6, 10, 4) };
            var row1 = _row1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 32, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, 0, 4),
            };
            var row2 = _row2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 30, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 3, 0, 0),
            };

            var lblSite = new Label { Text = "站点：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) };
            cboSite = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                // 站点名可能更长（"笔趣阁（移动版·快）" / "笔尖中文"），150 会截断
                Width = 176,
                Margin = new Padding(0, 1, 10, 0),
            };
            // 只把站点名填进下拉框 —— 这里**不能**走 ReloadSiteCombo：
            // 那个会调用 UpdateSiteHint，而本方法在构造函数很靠前的位置执行，
            // 那时 _settings / _profile / chkOffline 等还没建好，会空引用崩掉
            // （实测直接把全部布局体检测试打挂了）。真正的初始化在构造函数末尾。
            PopulateSiteCombo(CoreSiteKey);

            var lblKw = new Label { Text = "书名 / 链接：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) };
            // 关键词框吃掉剩余宽度：窗口再宽也不会留一块空白（宽度在 LayoutInlineTweaks 里算）
            var kwHost = new Panel { Width = 240, Height = 26, Margin = new Padding(0, 1, 6, 0) };
            txtKeyword = new TextBox { Dock = DockStyle.Fill };
            txtKeyword.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); } };
            kwHost.Controls.Add(txtKeyword);
            _kwHost = kwHost;

            btnSearch = MakeButton("搜索", 40, (s, e) => DoSearch());
            // 本地查：在"搜过 / 下过"的书里模糊检索，**不联网**。
            // 放在搜索旁边，因为用户的心智是"找书" —— 联网找不到时顺手点这个。
            btnLocal = MakeButton("本地查", 52, (s, e) => DoLocalSearch());
            new ToolTip().SetToolTip(btnLocal,
                "只在你**搜过或下过的书**里做模糊检索，不联网。\n" +
                "索引靠用过的记录积累，不是全站书目 —— 找不到就点「搜索」联网查（结果会自动入库）。");
            btnLoad = MakeButton("载入目录", 64, (s, e) => DoLoadBook());
            btnReload = MakeButton("刷新目录", 64, (s, e) => DoLoadBookRefresh());

            // 「自动选最快」放在**第二行**，不放站点选择旁边：
            // 第一行在最小窗口宽度（820）下已经排满，再塞一个复选框会把
            // 「刷新目录」挤出右边界（实测布局体检直接报"Button 越界"）。
            // 第二行本来就是设置类控件（保存到 / 浏览 / 设置），还有富余。
            // 标签刻意用最短的「自动」：第 2 行在最小窗口宽度（820px）下只剩
            // 约 60px 富余，写「自动选最快」会把「离线模式」挤出右边界（实测越界）。
            // 完整含义放在 tooltip 和日志里。
            chkAutoSite = new CheckBox
            {
                Text = "自动最优",
                AutoSize = true,
                Margin = new Padding(10, 5, 0, 0),
            };
            new ToolTip().SetToolTip(chkAutoSite,
                "【自动选最优】搜索之后自动比较各站，选「同名书里下完最快」的那本。\n" +
                "判断依据：预计总时长 = 章节数 × 每章耗时（越小越好）。\n" +
                "  · 只认书名**完全一致**的，相近书名不参与；\n" +
                "  · 章节数从搜索结果的「最新章节」里估，不额外抓目录；\n" +
                "  · 每章耗时取「检测站点」的实测值。\n" +
                "比较最多 8 秒、最多 4 个站；选中别的站会自动切过去并载入目录。\n" +
                "启动时仍会在后台实测一次站点速度（缓存 24 小时）。");
            chkAutoSite.CheckedChanged += (s, e) =>
            {
                _autoSite = chkAutoSite.Checked;
                if (_autoSite) EnsureScores();
                ApplySiteSelection();
                if (_autoSite) Log("已开启「自动选最快」：当前使用「" + CurrentDescriptor.DisplayName + "」。");
                else Log("已关闭「自动选最快」，改回手动选择站点。");
            };

            row1.Controls.AddRange(new Control[] { lblSite, cboSite, lblKw, kwHost, btnSearch, btnLocal, btnLoad, btnReload });

            var lblOut = new Label { Text = "保存到：", AutoSize = true, Margin = new Padding(0, 6, 2, 0) };
            // 宽度从 230 收到 190：给「自动最优」这四个字让出位置。
            // 布局断言在 820px 最小窗口下守着 —— 加宽复选框会把「离线模式」顶出去（实测过一次）。
            var outHost = new Panel { Width = 190, Height = 24, Margin = new Padding(0, 1, 6, 0) };
            txtOutput = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载"),
            };
            outHost.Controls.Add(txtOutput);
            btnBrowse = MakeButton("浏览…", 50, (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择小说保存目录";
                    if (d.ShowDialog() == DialogResult.OK) txtOutput.Text = d.SelectedPath;
                }
            });
            btnSettings = MakeButton("设置", 40, (s, e) => DoSettings());
            new ToolTip().SetToolTip(btnSettings,
                "并发线程数、请求间隔、失败重试轮数。\n" +
                "存成 exe 同目录的 settings.ini，也可以手改。");

            chkOffline = new CheckBox
            {
                Text = "离线模式（先把正文抓进内存，下载时不再联网）",
                AutoSize = true,
                Margin = new Padding(6, 3, 0, 0),
            };
            var tipOff = new ToolTip();
            tipOff.SetToolTip(chkOffline,
                "勾上后：点「载入目录」会真的遍历整本站点（约 1~2 分钟），\n" +
                "期间把每一章正文都抓下来存在内存里；之后点下载就直接写文件，\n" +
                "不再发生任何网络请求（下 700 章只需几秒）。\n" +
                "不勾：目录能秒开（走本地缓存），但下载时要再联网抓一遍正文。");

            // 自动选最快：放在与站点**同一行**的语义位置（站点选择区），
            // 但物理上排在第 2 行 —— 第 1 行在 820px 下已排满，塞不下。
            row2.Controls.AddRange(new Control[] { lblOut, outHost, btnBrowse, btnSettings, chkAutoSite, chkOffline });

            // 第 4 行（固定显示）：书架 / 任务队列 / 检查更新 / 繁简转换。
            // 为什么不塞进第 2 行：最小窗口 820px 时第 2 行可用宽只有 780px，
            // 现有内容已经占约 700px，再塞 4 个控件必然越界（有布局断言守着）。
            var row4 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 30, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 3, 0, 0),
            };
            btnShelf = MakeButton("书架", 44, (s, e) => DoOpenShelf(), 2);
            new ToolTip().SetToolTip(btnShelf,
                "打开本地书架：列出下载过的书，可以一键「更新新章节」、导出、打开目录。\n" +
                "记录存在 exe 同目录的 " + Bookshelf.FileName + "（可手改）。");
            btnQueue = MakeButton("任务队列", 60, (s, e) => DoOpenQueue(), 2);
            new ToolTip().SetToolTip(btnQueue,
                "一次排多本书依次下载（每行一个书名或站点链接）。\n" +
                "适合睡前挂机：走的是和单个下载完全相同的流程，失败了也不影响后面的书。");
            btnCheckUpdate = MakeButton("检查更新", 60, (s, e) => DoCheckUpdate(), 8);
            btnProbeSites = MakeButton("检测站点", 60, (s, e) => DoProbeSites(), 2);
            btnAiSettings = MakeButton("AI 设置", 60, (s, e) => DoAiSettings(), 0);
            new ToolTip().SetToolTip(btnAiSettings,
                "配置 AI 裁决错字（默认关闭）。\n" +
                "开了之后，「检测错字」会把**两个源写法不同**的那几处差异交给大模型判断\n" +
                "哪个写法对 —— 只发差异点前后十几个字，不发整章。\n\n" +
                "本地后端（Ollama）不联网、不花钱；云端后端会把片段发给服务商。");

            // 繁简转换：转换在**写盘时**做，所以 TXT / EPUB / Markdown 都会跟着变。
            chkTraditional = new CheckBox
            {
                Text = "输出繁体",
                AutoSize = true,
                Margin = new Padding(0, 3, 0, 0),
            };
            new ToolTip().SetToolTip(chkTraditional,
                "把导出的正文从简体转成繁体（港台阅读器/读者用）。\n" +
                "转换在本地完成，不联网；对已经下载好的书重新导出即可生效。");
            chkTraditional.CheckedChanged += (s, e) =>
            {
                _settings.OutputTraditional = chkTraditional.Checked;
                try { _settings.Save(); } catch { /* 存不下不影响本次导出 */ }
                Log(chkTraditional.Checked ? "输出繁体：已开启（导出时转换）" : "输出繁体：已关闭");
            };

            row4.Controls.AddRange(new Control[] { btnShelf, btnQueue, btnCheckUpdate, btnProbeSites, btnAiSettings, chkTraditional });

            // 第 3 行：番茄专用（平时整行隐藏，切到「番茄小说」才出现）
            //   —— 这几个按钮在老代码里被硬编码在 x=700/876，正是被切掉/盖住文字的那批
            var row3 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 30, WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 3, 0, 0),
                Visible = false,
            };
            _rowFanqie = row3;
            btnWebSearch = MakeButton("浏览器搜索", 76, (s, e) => DoWebSearch(), 2);
            new ToolTip().SetToolTip(btnWebSearch,
                "番茄站没有公开的中文搜索接口。\n点这里会用默认浏览器打开番茄官网的搜索页，\n找到书后把地址栏链接复制回来粘到输入框即可。");
            btnOfficial = MakeButton("用第三方工具下载", 112, (s, e) => DoOfficialDownload(), 2);
            btnCoreSetup = MakeButton("设置核心", 72, (s, e) => DoConfigureCore());
            var tip = new ToolTip();
            tip.SetToolTip(btnOfficial,
                "调用你指定的第三方番茄下载器（TomatoNovelDownloader）的本地 API 下载番茄小说。\n" +
                "它走官方接口 + 解密，正文干净完整（网页版有验证码风控和形近字替换）。\n" +
                "首次使用请先点右边「设置核心」。");
            tip.SetToolTip(btnCoreSetup, "指定第三方番茄下载器的 exe 位置（番茄下载靠它完成）");
            row3.Controls.AddRange(new Control[] { btnWebSearch, btnOfficial, btnCoreSetup });

            // 注意顺序：Dock=Top 是按添加顺序从外往内排的。
            // row4 永远显示，放在最外侧；row3 只有番茄站点才显示。
            top.Controls.Add(row4);
            top.Controls.Add(row3);
            top.Controls.Add(row2);
            top.Controls.Add(row1);
            Controls.Add(top);

            // 底部：进度条 + 状态 + 日志
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 218, Padding = new Padding(10, 4, 10, 8) };

            progress = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
            lblStatus = new Label { Dock = DockStyle.Top, Height = 22, Text = "就绪。", TextAlign = ContentAlignment.MiddleLeft };

            txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(250, 250, 250),
                // ★ 不能用 Consolas：它不含汉字字形，中文会渲染成方框（tofu）。
                //   实测用户界面上日志区整片方框、而 ASCII 正常 —— 就是缺字形，
                //   不是编码问题（编码错的话 ASCII 也会坏）。见 UiFont 的注释。
                Font = UiFont.Mono(8.5F),
            };
            // ★ 日志框的右键菜单 —— 这里放"出问题时最需要"的三件事。
            //
            //   为什么挂在日志框而不是工具栏：工具栏已经很挤（再塞按钮，
            //   小窗口下会换行、直接顶坏布局体检），而"我要报问题"这个动作
            //   天然发生在**看着日志的时候** —— 右键正是那个位置。
            txtLog.ContextMenuStrip = BuildLogMenu();

            var logBox = new Panel { Dock = DockStyle.Fill };
            logBox.Controls.Add(txtLog);
            bottom.Controls.Add(logBox);
            bottom.Controls.Add(lblStatus);
            bottom.Controls.Add(progress);            Controls.Add(bottom);

            // 中间：书籍列表 + 章节列表
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                Panel1MinSize = 80,
                Panel2MinSize = 100,
            };
            _split = split;
            // SplitContainer 在窗体还没完成布局时尺寸可能还是默认值，
            // 此时直接给 SplitterDistance 会抛 “必须在 Panel1MinSize 和 Width - Panel2MinSize 之间”，
            // 所以这里每次都按当前尺寸夹一次，并在窗口尺寸变化时重新夹。
            split.SizeChanged += (s, e) => ClampSplitter();
            split.HandleCreated += (s, e) => ClampSplitter();
            // ★ 只有**窗体已经显示完、且用户真的拖了**才记住位置。
            //
            //   原来这里无条件写 `_preferredSplit = SplitterDistance`，有两个坑：
            //     ① 程序自己改 SplitterDistance（ClampSplitter、窗体尺寸变化）
            //        也会触发 SplitterMoved → 把自己的中间值当成"用户的选择"，
            //        此后不再按比例重算。实测固定落在 Panel1=469（应是 306），
            //        而且同一个 exe 每次启动都是这个错值。
            //     ② 更早的版本把 _preferredSplit 做成 **static**，于是同一次运行里
            //        打开第二个窗口会继承上一个窗口的布局 ——
            //        "第一次打开和第二次打开不一样"就是这么来的。
            //
            //   判据用 _layoutDone（Shown 之后才置位）：布局期间的
            //   SplitterMoved 全部是程序自己触发的，一律不记。
            //   用户之后拖分隔条时 _layoutDone 已为 true，照常记住。
            split.SplitterMoved += (s, e) =>
            {
                if (!_layoutDone) return;           // 布局期间的自动触发，不算用户选择
                _userMovedSplit = true;
                _preferredSplit = split.SplitterDistance;
            };

            lstBooks = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            lstBooks.Columns.Add("书名", 300);
            lstBooks.Columns.Add("作者", 140);
            lstBooks.Columns.Add("分类", 100);
            // 「来源」列：跨站搜索时一眼看出这本书来自哪个站。
            // 起因：用户搜《万妖圣祖》，笔尖中文没有、笔趣阁有 ——
            // 结果列表必须能直接标出来，否则他只能一个个站点手动试。
            lstBooks.Columns.Add("来源", 96);
            lstBooks.Columns.Add("备注", 300);
            lstBooks.DoubleClick += (s, e) => DoLoadBook();

            var bookBar = new Panel { Dock = DockStyle.Top, Height = 24 };
            // 标题从"搜索结果（双击载入目录）"改成能带计数的 —— 见 lblBooksBar 的注释
            lblBooksBar = new Label
            {
                Text = "搜索结果（双击载入目录）",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                AutoEllipsis = true,
            };
            bookBar.Controls.Add(lblBooksBar);
            split.Panel1.Controls.Add(lstBooks);
            split.Panel1.Controls.Add(bookBar);

            lstChapters = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
            };
            lstChapters.Columns.Add("章节", 620);
            lstChapters.Columns.Add("章节ID", 180);
            lstChapters.ItemChecked += (s, e) => UpdateSelLabel();

            // 章节栏：左边按钮按内容宽度排（FlowLayoutPanel 不会互相压），
            // 右边状态文字用 Fill 吃掉剩余宽度 —— 老代码把状态文字硬编码在 x=606，
            // x=700 的按钮正好压上去，这就是用户截图里"看不清"的那处。
            var chapBar = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(0, 3, 0, 3) };
            // 按钮一行放不下了（11 个），拆成两行。
            // 每行宽度都按"文字实测宽 + 内边距"预先算过：两行各约 640px，
            // 而最小窗口（820px）的可用宽是 780px，留有余量。
            _chapFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 2, 0, 0),
            };
            _chapFlow2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 2, 0, 0),
            };
            btnSelectAll = MakeButton("全选", 44, (s, e) => SetAllChecks(true), 2);
            btnSelectNone = MakeButton("全不选", 56, (s, e) => SetAllChecks(false), 2);
            btnInvert = MakeButton("反选", 44, (s, e) => InvertChecks(), 6);
            btnDownload = MakeButton("下载选中", 68, (s, e) => DoDownload(), 2);
            btnDownloadAll = MakeButton("下载全部", 68, (s, e) => DoDownloadAll(), 2);
            btnUpdate = MakeButton("更新新章节", 80, (s, e) => DoUpdateExisting(), 2);
            new ToolTip().SetToolTip(btnUpdate,
                "检查这本书有没有新章节，只把新的补上去（不动旧内容）。\n" +
                "有章节锚点的文件会把新章节插到正确位置；旧文件退化为追加到末尾。");
            btnFillMissing = MakeButton("补齐缺章", 68, (s, e) => DoFillMissing(), 2);
            new ToolTip().SetToolTip(btnFillMissing,
                "检查这个文件里缺了哪些章（站点侧空内容、或上次中断），只重新抓那几章，\n" +
                "按章节顺序插回正确位置 —— 已有正文一个字都不会动。\n" +
                "需要文件带章节锚点（v1.0.5 之后下载的）；旧文件会提示重新下载。");
            btnCancel = MakeButton("取消", 44, (s, e) => { _cancel = true; Log("已请求取消，正在收尾…"); }, 0);
            btnCancel.Enabled = false;
            btnExportEpub = MakeButton("导出 EPUB", 72, (s, e) => DoExportEpub(), 2);
            new ToolTip().SetToolTip(btnExportEpub,
                "把这本书已下载的正文导出成 EPUB（手机阅读器、Kindle 都能直接打开）。\n" +
                "只导出已经下载到本地的章节，不会联网。");
            btnExportMd = MakeButton("导出 Markdown", 84, (s, e) => DoExportMarkdown(), 2);
            new ToolTip().SetToolTip(btnExportMd,
                "把已下载的正文导出成 Markdown（带 YAML 头信息和目录锚点）：\n" +
                "方便用 pandoc 转 EPUB/PDF，或者丢进笔记软件、静态站点生成器。");
            btnExportTxt = MakeButton("导出纯文本", 84, (s, e) => DoExportPlainText(), 2);
            btnOpenFolder = MakeButton("打开目录", 60, (s, e) => OpenFolder());
            new ToolTip().SetToolTip(btnOpenFolder, "打开保存这本书的文件夹");
            // 错字检测放这一行而不是第一行：第一行在最小窗口（820px）已经排满，
            // 而这一行（导出类）内容才 260px 左右，有足够余量。
            btnDetectTypos = MakeButton("检测错字", 72, (s, e) => DoDetectTypos(), 0);
            new ToolTip().SetToolTip(btnDetectTypos,
                "双源比对：用**另一个源**重新抓这本书的正文，和本地正文逐字对比。\n" +
                "两个源同时错成同一个字的概率极低，所以列出来的位置至少有一边是错的，\n" +
                "两边写法都会摆出来供你判断。结果写成 错字检测报告.txt / .csv。\n\n" +
                "局限（报告里也会写明）：两个源都错同一个字时检测不出来。");
            lblSel = new Label
            {
                Text = "（还没载入目录）",
                Dock = DockStyle.Fill,
                AutoSize = false,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                AutoEllipsis = true,      // 窗口太窄时显示"…"，不会盖住按钮
            };
            _chapFlow.Controls.AddRange(new Control[] { btnSelectAll, btnSelectNone, btnInvert, btnDownload, btnDownloadAll, btnUpdate, btnFillMissing, btnCancel });
            _chapFlow2.Controls.AddRange(new Control[] { btnExportEpub, btnExportMd, btnDetectTypos, btnOpenFolder });
            // Dock=Top 的添加顺序 = 从上往下的顺序
            chapBar.Controls.Add(_chapFlow2);
            chapBar.Controls.Add(_chapFlow);
            chapBar.Controls.Add(lblSel);
            split.Panel2.Controls.Add(lstChapters);
            split.Panel2.Controls.Add(chapBar);

            Controls.Add(split);
            // z-order 很关键：split 是 Dock=Fill，会盖住它下面所有兄弟控件。
            // 所以必须把它压到最底层，让 Dock=Top/Bottom 的顶部栏和底部日志区浮在上面。
            // 之前这里写的是 split.BringToFront()（反了），只是恰好因为顶部面板宽度更大才看起来正常；
            // 一旦顶部面板变矮，整个搜索区就被 split 盖没了 —— 是布局渲染截图抓到的。
            split.SendToBack();
            top.BringToFront();
            bottom.BringToFront();

            // 等窗体尺寸确定后再设置分隔位置
            Shown += (s, e) =>
            {
                ClampSplitter();
                _layoutDone = true;     // 之后再来的 SplitterMoved 才算"用户拖的"
                LayoutInlineTweaks();
            };
            Resize += (s, e) => LayoutInlineTweaks();

            // 下拉框选中变化 → 把选择同步给"自动选最快"复选框（用户在手动接管），
            // 然后重建站点对象。真正的重建逻辑都在 ApplySiteSelection 里。
            cboSite.SelectedIndexChanged += (s, e) =>
            {
                if (_reloadingSites) return;
                if (_autoSite && chkAutoSite != null && chkAutoSite.Checked)
                {
                    // 自动模式下用户不需要手动选站：把勾去掉，交回手动
                    chkAutoSite.Checked = false;
                }
                ApplySiteSelection();
            };
            ReloadSiteCombo(null);      // 内部会调用 UpdateSiteHint
            ApplySettings();
            Log("工具已就绪。");
            Log("取数后端：" + (Http.CurlAvailable ? "系统自带 curl（" + Http.CurlPath + "）" : "内置 .NET 请求（未找到 curl.exe）"));
            // 启动后在后台探活全部站点，把不可用的标注出来。
            // 不阻塞启动、也不改动用户的选择（只标注 + 在日志里给降级建议）。
            if (!SuppressDialogs && IsHandleCreated)
            {
                BeginInvoke(new Action(ProbeSitesAsync));
                // 「自动选最快」还没测过就先测一次（缓存新鲜则不重复测）。
                // 放在后台：每站 3 个请求，绝不能让窗口卡着不出来。
                if (_autoSite) BeginInvoke(new Action(delegate { RateSitesAsync(false); }));
            }
            Log("当前设置：" + _settings + "（改这些点上面的「设置」按钮，或直接编辑 " + AppSettings.FileName + "）");
            if (!System.IO.File.Exists(SiteProfileStore.DefaultPath))
            {
                SiteProfileStore.SaveSample(SiteProfileStore.DefaultPath, SiteProfileStore.Defaults());
                Log("已生成站点参数样例：" + SiteProfileStore.DefaultPath + "（只放并发/间隔/超时/重试/UA/编码，可手改）");
            }
            if (!string.IsNullOrEmpty(SiteProfileStore.LastError)) Log("站点配置提醒：" + SiteProfileStore.LastError);
            Log("站点参数：" + (_profile != null ? _profile.ToString() : "（默认）"));
            // ★ 启动时备份"丢了就找不回来"的文件（书架 / 站点配置 / settings）。
            //   真实事故：程序放在可拔插 U 盘上，那个盘在开发过程中掉过两次线 ——
            //   一次正好在升级中间，目录整个"不存在"。
            //   下载被拔 → 文件写坏；U 盘丢了 → 书架和几十本书的记录一起没了。
            //   备份在后台线程做，失败只记日志，绝不影响启动。
            if (!SuppressDialogs)
            {
                try
                {
                    LocalBackup.RunAsync(AppDomain.CurrentDomain.BaseDirectory, Log);
                }
                catch { }
            }
            Log("· 笔趣阁：直接输入中文书名搜索（例如：沧元图），双击结果载入目录后勾选章节下载。");
            Log("· 番茄小说：番茄没有公开的中文搜索接口，请点「浏览器搜索」去官网找到书，");
            Log("  再把书籍链接（.../page/数字）或 book_id 粘贴到输入框即可自动载入目录。");
            Log("  说明：番茄网页版正文有风控（验证码 / 200 字试读），正文下载可能受限。");
        }

        /// <summary>
        /// 仅供自动化测试/布局探针使用：切换站点下拉框。
        /// 两个站点模式下的界面行数不一样（番茄多一行），所以布局必须两种都量。
        /// </summary>
        internal void SelectSiteForTest(int index)
        {
            if (cboSite == null) return;
            if (index < 0 || index >= cboSite.Items.Count) return;
            cboSite.SelectedIndex = index;
            PerformLayout();
            LayoutInlineTweaks();
        }

        /// <summary>
        /// 仅供自动化测试：喂一份**假的**评分表并打开「自动选最快」，
        /// 用来验证"自动模式确实选了分最低的那个站点"。**不联网**。
        /// </summary>
        internal void ApplyAutoSiteForTest(List<SiteScore> scores, bool on)
        {
            _siteScores = scores;
            _autoSite = on;
            if (chkAutoSite != null) chkAutoSite.Checked = on;
            ApplySiteSelection();
        }

        /// <summary>仅供自动化测试：当前实际生效的站点 Key</summary>
        internal string EffectiveSiteKeyForTest()
        {
            return EffectiveSiteKey();
        }

        /// <summary>仅供自动化测试：拿到结果区/章节区之间的分隔条容器（量布局用）</summary>
        internal SplitContainer SplitterForTest()
        {
            return _split;
        }

        /// <summary>仅供自动化测试：填搜索结果（验证列表与兜底摘要）</summary>
        internal void FillBooksForTest(List<BookInfo> list, string keyword)
        {
            FillBooks(list, keyword);
        }

        /// <summary>仅供自动化测试：结果区标题栏当前文字</summary>
        internal string BooksBarTextForTest
        {
            get { return _lastBooksSummary ?? ""; }
        }

        /// <summary>
        /// 自检用：把界面里所有可见控件的布局位置报出来（相对窗体客户区）。
        /// 离线单测用它验证"任何情况下控件都不越界、不互相重叠"——
        /// 这条规则是被真实用户反馈逼出来的：按钮压住了状态文字、最右按钮被切掉。
        /// </summary>
        internal List<string> CollectLayoutProblems()
        {
            var problems = new List<string>();
            CollectLayoutProblems(this, problems);
            return problems;
        }

        /// <summary>自检用：新建设置对话框、摆好布局、报告它的布局问题（不弹出来）</summary>
        internal static List<string> CollectSettingsDialogProblems(out Form dialog)
        {
            NumericUpDown[] nums;
            var dlg = BuildSettingsDialog(new AppSettings(), new Font("Microsoft YaHei UI", 9F), out nums);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(-4000, -4000);
            dlg.Show();
            Application.DoEvents();
            dlg.PerformLayout();
            var problems = new List<string>();
            CollectLayoutProblems(dlg, problems);
            dialog = dlg;
            return problems;
        }

        private static void CollectLayoutProblems(Control parent, List<string> problems)
        {
            var kids = new List<Control>();
            foreach (Control c in parent.Controls) if (c.Visible) kids.Add(c);

            foreach (var c in kids)
            {
                if (c.Right > parent.ClientSize.Width || c.Bottom > parent.ClientSize.Height || c.Left < 0 || c.Top < 0)
                    problems.Add(string.Format("{0} 越界：控件 {1},{2} {3}x{4}，父容器可显示 {5}x{6}",
                        c.GetType().Name, c.Left, c.Top, c.Width, c.Height,
                        parent.ClientSize.Width, parent.ClientSize.Height));

                if (string.IsNullOrEmpty(c.Text)) continue;
                foreach (var o in kids)
                {
                    if (ReferenceEquals(o, c) || string.IsNullOrEmpty(o.Text)) continue;
                    var inter = Rectangle.Intersect(c.Bounds, o.Bounds);
                    if (inter.Width > 4 && inter.Height > 4)
                    {
                        string a = c.GetType().Name + "('" + Clip(c.Text) + "')";
                        string b = o.GetType().Name + "('" + Clip(o.Text) + "')";
                        if (string.CompareOrdinal(a, b) < 0)
                            problems.Add(string.Format("{0} 与 {1} 重叠 {2}x{3}", a, b, inter.Width, inter.Height));
                    }
                }
            }

            foreach (var c in kids)
                if (c is SplitContainer || c is Panel || c is FlowLayoutPanel || c is GroupBox)
                    CollectLayoutProblems(c, problems);
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 20 ? s.Substring(0, 20) + "…" : s;
        }

        private void UpdateSiteHint()
        {
            var desc = CurrentDescriptor;
            string key = desc == null ? CoreSiteKey : desc.Key;
            bool fanqie = key == "fanqie";
            bool canSearch = desc != null && desc.Has(SiteCapabilities.Search);

            txtKeyword.Text = "";
            WatermarkExt.SetHint(txtKeyword,
                fanqie ? "粘贴番茄书籍链接或 book_id"
                       : (canSearch ? "输入中文书名，例如：牧神记" : "粘贴书籍地址"),
                this);
            _site = desc != null ? desc.Factory() : SiteRegistry.Create(CoreSiteKey);
            _profile = SiteProfileStore.Get(SiteKeyOf(_site));
            ApplySettings();
            if (btnWebSearch != null) btnWebSearch.Enabled = fanqie;
            if (btnOfficial != null) btnOfficial.Enabled = fanqie;
            // 番茄专用的那一行整行出现/隐藏（隐藏时不留空行，顶部面板自己收高度）
            if (_rowFanqie != null)
            {
                _rowFanqie.Visible = fanqie;
                var topPanel = _rowFanqie.Parent;
                if (topPanel != null)
                {
                    // 高度必须等于「各行高度之和 + 上下 Padding」，否则最下面一行会被裁掉一半。
                    // 注意：隐藏时控件的 Height 会变成 0，所以这里用固定行高常量算，
                    // 不能读 _rowFanqie.Height（第一版就栽在这上面，探针报"下边被切掉"）。
                    const int Row1H = 32, Row2H = 30, Row3H = 30, Row4H = 30;
                    int rows = Row1H + Row2H + Row4H + (fanqie ? Row3H : 0);
                    topPanel.Height = rows + topPanel.Padding.Top + topPanel.Padding.Bottom;
                }
                // 注意：本方法在构造函数里就会被调用，那时窗口句柄还没创建，
                // 直接 BeginInvoke 会抛“在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke”
                // ——程序直接起不来（这处 bug 也是布局探针抓到的）。有句柄才投递，没有就当场算。
                if (IsHandleCreated) BeginInvoke(new Action(LayoutInlineTweaks));
                else LayoutInlineTweaks();
            }

            if (fanqie)
            {
                var core = new TomatoCore { ExePath = TomatoCore.GuessDefaultExePath() };
                bool bundled = core.ExePath != null && TomatoCore.BundledExe() != null &&
                               string.Equals(core.ExePath, TomatoCore.BundledExe(), StringComparison.OrdinalIgnoreCase);
                if (core.IsRunning())
                    Log("番茄：检测到番茄核心服务已在运行（" + core.BaseUrl + "），可直接点「用官方工具下载（番茄）」。");
                else if (core.ExePath == null)
                    Log("番茄：还没装番茄核心，请点「设置官方工具路径」选择 TomatoNovelDownloader 的 exe（会复制进本工具目录）。");
                else
                    Log("番茄：核心已就绪" + (bundled ? "（本工具自带，自包含）" : "（外部工具）") +
                        "：" + core.ExePath);
                Log("提示：番茄的搜索用「浏览器搜索」，下载用「用第三方工具下载」，在第三行。");

                // 本地索引：把书架里的书并进来（不联网，很快）
                SyncLocalIndex();
                // 版本检测：放到后台线程，**绝不阻塞界面启动** ——
                // 这个查询要走 GitHub，国内经常要好几秒甚至超时，
                // 放在主线程会让窗口卡住不出。
                if (core.ExePath != null)
                {
                    var exeForCheck = core.ExePath;
                    var th = new System.Threading.Thread(delegate() { CheckFanqieVersion(exeForCheck); });
                    th.IsBackground = true;
                    th.Start();
                }
            }
        }

        /// <summary>
        /// 检查番茄核心是不是旧版，旧了就提示一次。
        ///
        /// 设计上的克制：
        ///   · **只提示，不自动下载、不自动替换** —— 这是本项目的既有约定
        ///     （见 docs/网络健壮性与书架.md：明确不做自动更新），
        ///     而且替换一个正在用的 exe 风险太高。给个发布页链接让用户自己决定。
        ///   · 联网查不到就退回内置的 <see cref="TomatoCore.KnownLatestVersion"/>，
        ///     所以**断网也能告诉你手上这份是不是旧的**。
        ///   · 在后台线程跑，查询超时也不影响主界面。
        /// </summary>
        private void CheckFanqieVersion(string exePath)
        {
            try
            {
                var local = TomatoCore.VersionOf(exePath);
                var latest = TomatoCore.QueryLatestVersion(6);
                bool fromNet = !string.IsNullOrEmpty(latest);
                if (!fromNet) latest = TomatoCore.KnownLatestVersion;

                if (local == null)
                {
                    SafeLog("番茄：读不出核心版本号（" + System.IO.Path.GetFileName(exePath) + "）。");
                    return;
                }
                if (!TomatoCore.IsNewer(latest, local))
                {
                    SafeLog("番茄：核心版本 v" + local + " 已是最新。");
                    return;
                }

                SafeLog("番茄：核心 v" + local + " 已过时，最新是 v" + latest +
                        (fromNet ? "" : "（内置信息，未联网核对）") + "。");
                SafeLog("      新版有：Web UI 模式（--server）、自更新、短链接解析、IID 网络诊断等。");
                SafeLog("      下载页：" + TomatoCore.ReleasePage);
                SafeLog("      换法：下载后点「设置官方工具路径」选新的 exe（会复制进工具目录）。");
            }
            catch (Exception ex)
            {
                SafeLog("番茄：版本检测失败（" + ex.Message + "），不影响使用。");
            }
        }

        /// <summary>
        /// 把书架里的书并进本地搜索索引（启动时做一次，不联网、不阻塞界面）。
        ///
        /// 为什么放在启动：老用户书架里已经有一堆书，索引如果只从"新搜索的结果"积累，
        /// 他们点「本地查」会什么都查不到 —— 那些书从没被索引过。
        /// 书架里正好有书名和作者，导进来立刻可用。
        /// </summary>
        private void SyncLocalIndex()
        {
            try
            {
                SearchIndex.SyncBookshelf();
                Log("本地索引：已收录 " + SearchIndex.BookCount + " 本（来自书架与搜过的书），点「本地查」可不联网检索。");
            }
            catch (Exception ex) { Log("本地索引初始化失败（不影响使用）：" + ex.Message); }
        }
        /// <summary>给后台线程用的日志（切回 UI 线程，避免跨线程改控件）</summary>
        private void SafeLog(string msg)
        {
            try
            {
                if (IsHandleCreated && InvokeRequired) BeginInvoke(new Action<string>(SafeLog), msg);
                else Log(msg);
            }
            catch { }
        }

        // ------------------------------------------------------------ 交互

        /// <summary>
        /// 把搜索结果填进书籍列表。抽出来是因为现在有**两个来源**会填它：
        /// 联网搜索的结果、以及本地缓存/索引命中的结果 —— 两条路径必须
        /// 用同一段代码填，否则"缓存命中的那批书"显示出来会和联网的不一样。
        /// </summary>
        private Label lblBooksBar;
        private string _lastBooksSummary = "搜索结果（双击载入目录）";

        /// <summary>更新结果区标题栏的摘要文字（它兼作"兜底显示"，见 FillBooks 里的注释）。</summary>
        private void SetBooksBarText(string text)
        {
            _lastBooksSummary = text;
            try { if (lblBooksBar != null) lblBooksBar.Text = text; } catch { }
        }

        private void FillBooks(List<BookInfo> list) { FillBooks(list, null); }

        /// <summary>
        /// 填搜索结果。keyword 是"用来算相关度的词"，由调用方**明确传进来**，
        /// 而不是从 txtKeyword 现读 —— 本地查/缓存命中时用户可能已经改了输入框，
        /// 那时按新词排序会把列表排得莫名其妙。
        /// 传 null 表示"不排序，按站点原顺序显示"。
        /// </summary>
        private void FillBooks(List<BookInfo> list, string keyword)
        {
            // ★ 先按"跟搜索词的相关度"排序，再填。
            //
            //   起因（用户实测）：搜「万妖圣祖」，笔尖中文返回 50 条，
            //   前几条是《圣妖之祖》《万妖之祖》《万妖剑祖》《万骨妖祖》
            //   《蜀山万妖之祖》《妖祖》《大明圣祖》《人道圣祖》《棋道圣祖》——
            //   用户问"这些跟万妖圣祖有什么关系吗"。
            //
            //   答案是：**没关系**。那个站点是按单字模糊匹配的，把含"妖"或"祖"
            //   的书全倒出来了。而《万妖圣祖》真在那站上时，会被埋在几十条
            //   弱相关结果里，用户根本翻不到 —— 看起来就像"搜不到"。
            //
            //   站点不给相关度排序，我们自己排。排序稳定（同分保持站点原顺序），
            //   所以结果不会跳来跳去。
            string kwForSort = keyword == null ? "" : keyword.Trim();
            var ranked = RankByRelevance(list, kwForSort);

            UiInvoke(() =>
            {
                lstBooks.Items.Clear();
                if (ranked == null) return;
                foreach (var b in ranked)
                {
                    var it = new ListViewItem(b.Title);
                    it.SubItems.Add(b.Author);
                    it.SubItems.Add(b.Category);
                    it.SubItems.Add(SiteLabelOfBook(b));
                    it.SubItems.Add(b.Desc);
                    it.Tag = b;

                    // ★ 只沾边的结果（站点单字模糊匹配的产物）用灰字显示。
                    //   起因：用户搜「万妖圣祖」，笔尖中文倒了 50 条
                    //   《圣妖之祖》《大明圣祖》《人道圣祖》…… 用户问
                    //   "这些跟万妖圣祖有什么关系吗"。
                    //   排序能把它压下去，但**压下去不等于看得出来** ——
                    //   灰掉它，用户一眼就知道"这几条不是我要的，这站没有"。
                    int sc = TitleScore(b == null ? null : b.Title, kwForSort);
                    if (kwForSort.Length > 0 && sc > 0 && sc < 600)
                        it.ForeColor = Color.FromArgb(150, 150, 150);

                    lstBooks.Items.Add(it);
                }
                if (lstBooks.Items.Count > 0) lstBooks.Items[0].Selected = true;

                // ★ 兜底显示：把"搜到几条、第一条是什么"写进标题栏。
                //   用户两次反馈"搜到了但中间一片空白"，而日志说列表有 N 项。
                //   我在这边复现不出来（控件 Visible、句柄、坐标、DrawToBitmap 全正常），
                //   所以不能只依赖 ListView 的渲染 —— 标题栏也要能说清结果。
                //   即使列表没画出来，用户至少知道"搜到了哪本书、能不能继续"。
                int total = ranked == null ? 0 : ranked.Count;
                if (total == 0)
                {
                    SetBooksBarText("搜索结果：没有结果（换个书名或换个源）");
                }
                else
                {
                    var top = ranked[0];
                    string extra = "";
                    if (top != null && !string.IsNullOrEmpty(top.Author)) extra += " " + top.Author;
                    SetBooksBarText(string.Format("搜索结果 {0} 条 —— 最相关：《{1}》{2}（双击载入目录）",
                        total, top == null ? "?" : top.Title, extra));
                }

                // ★ 强制重绘一次。
                //   起因同上：数据确实进了控件，但个别环境下没刷出来。
                //   Invalidate + Update 是"标记无效并立刻重画"，比单靠自动重绘稳。
                try
                {
                    lstBooks.Invalidate(true);
                    lstBooks.Update();
                }
                catch { }

                // ★ 落库后立刻回读一次并写日志。
                //   起因：用户两次反馈"日志说搜到 N 条、列表却是空的"，
                //   而我在本地怎么都复现不出来。加这一句之后，用户那边的日志
                //   就能直接告诉我们是"项根本没加进去"还是"加进去了但没显示"
                //   （后者要连带看下面的几何信息）。
                int shown = lstBooks.Items.Count;
                int nonEmpty = 0;
                for (int i = 0; i < shown; i++)
                    if (!string.IsNullOrEmpty(lstBooks.Items[i].Text)) nonEmpty++;
                Log(string.Format("结果列表已更新：收到 {0} 条 → 列表 {1} 项（其中标题非空 {2} 项）",
                    ranked == null ? 0 : ranked.Count, shown, nonEmpty));
                Log(string.Format("  · 结果区高度 {0}px / 分隔条位置 {1}（{2}）",
                    lstBooks.Height, _split == null ? -1 : _split.SplitterDistance,
                    lstBooks.Height >= 40 ? "能看见" : "!! 被挤没了，请往上拖分隔条"));

                // ★ 把"相关度分布"写出来。起因：用户搜「万妖圣祖」看到
                //   《圣妖之祖》《大明圣祖》这种毫不相干的结果，问"这些有什么关系吗"。
                //   原因是站点按单字模糊匹配。这里如实告诉用户：
                //   有几条真命中、几条只是沾边、一条都不沾的有多少。
                if (ranked != null && ranked.Count > 0 && kwForSort.Length > 0)
                {
                    int exact = 0, contains = 0, fuzzy = 0;
                    foreach (var b in ranked)
                    {
                        int sc = TitleScore(b == null ? null : b.Title, kwForSort);
                        if (sc >= 1000) exact++;
                        else if (sc >= 600) contains++;
                        else fuzzy++;
                    }
                    Log(string.Format("  · 与「{0}」的相关度：完全同名 {1} 条、标题含该词 {2} 条、仅沾边 {3} 条",
                        kwForSort, exact, contains, fuzzy));
                    if (exact == 0 && contains == 0)
                        Log("  ⚠ 这一站没有标题含「" + kwForSort +
                            "」的书 —— 下面全是按字沾边的。换个源再搜，或把书名写全。");
                }
                if (ranked != null && ranked.Count > 0 && shown == 0)
                    Log("  !! 收到了结果但一项都没进列表 —— 这是程序 bug，请把这段日志发给我");
            });
        }

        /// <summary>
        /// 按标题与搜索词的相关度排序（**稳定排序**：同分保持站点原顺序）。
        ///
        /// 为什么要自己排：站点不给相关度，只给一堆按单字匹配的结果。
        /// 实测笔尖中文搜「万妖圣祖」，前 8 条是
        /// 《圣妖之祖》《万妖之祖》《万妖剑祖》《万骨妖祖》《蜀山万妖之祖》
        /// 《妖祖》《大明圣祖》《人道圣祖》—— 精确匹配（如果有）会被埋在后面，
        /// 用户翻不到，看起来就是"搜不到"。
        /// </summary>
        internal static List<BookInfo> RankByRelevance(List<BookInfo> list, string keyword)
        {
            if (list == null || list.Count == 0) return list;
            string kw = (keyword ?? "").Trim();
            if (kw.Length == 0) return list;

            // 稳定排序：ListView 里同分的保持站点给的顺序，结果不会跳来跳去
            var idx = new Dictionary<BookInfo, int>();
            for (int i = 0; i < list.Count; i++) if (!idx.ContainsKey(list[i])) idx[list[i]] = i;

            var copy = new List<BookInfo>(list);
            copy.Sort(delegate (BookInfo a, BookInfo b)
            {
                int sa = TitleScore(a == null ? null : a.Title, kw);
                int sb = TitleScore(b == null ? null : b.Title, kw);
                if (sa != sb) return sb.CompareTo(sa);       // 分高的在前
                int ia = idx.ContainsKey(a) ? idx[a] : 0;
                int ib = idx.ContainsKey(b) ? idx[b] : 0;
                return ia.CompareTo(ib);                     // 同分按原顺序
            });
            return copy;
        }

        /// <summary>
        /// 标题与搜索词的相关度分数（越大越相关）。纯函数，便于测试。
        ///
        ///   1000   标题与搜索词完全相同（去书名号/空格/大小写后）
        ///    800   标题去掉书名号后**以**搜索词开头
        ///    600   标题**包含**搜索词（整词，不是单字）
        ///    400   搜索词包含标题（用户多打了几位）
        ///  100~200 只沾边：按"搜索词里有几个字出现在标题里"的比例给分
        ///      0   重合不到一半，当噪音
        ///
        /// ★ 为什么要有"按重合比例"这一档：站点是**单字**模糊匹配的。
        ///   实测搜「万妖圣祖」，笔尖中文返回的前几条是
        ///   《圣妖之祖》（命中"妖""祖"）《万妖之祖》（命中"万""妖""祖"）
        ///   《万妖剑祖》《万骨妖祖》…… 而《大明圣祖》只命中"圣""祖"。
        ///   它们都不是用户要的书，但**沾边程度不一样** —— 得排在不沾边的前面、
        ///   真命中的后面。
        ///
        /// ★ 注意别要求"搜索词每个字都出现"：《圣妖之祖》里**没有"万"字**，
        ///   那样会被判 0 分。我第一版就是这么写的，被测试抓出来了。
        /// </summary>
        internal static int TitleScore(string title, string keyword)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(keyword)) return 0;
            string t = NormBookTitle(title);
            string k = NormBookTitle(keyword);
            if (t.Length == 0 || k.Length == 0) return 0;

            if (t == k) return 1000;
            if (t.StartsWith(k, StringComparison.Ordinal)) return 800;
            if (t.IndexOf(k, StringComparison.Ordinal) >= 0) return 600;
            if (k.IndexOf(t, StringComparison.Ordinal) >= 0) return 400;

            // 只沾边：按"搜索词里有几个字出现在标题里"计分；重合不到一半算噪音。
            //
            // ★ 实测值（搜「万妖圣祖」，4 个字）：
            //     《圣妖之祖》命中「妖」「圣」「祖」= 3/4 → 175
            //     《万妖之祖》命中「万」「妖」「祖」= 3/4 → 175
            //     《妖祖》    命中「妖」「祖」      = 2/4 → 150
            //     《大明圣祖》命中「圣」「祖」      = 2/4 → 150
            //     《大主宰》  命中 0                → 0（噪音）
            //
            // ★ 曾经想再加一档"标题是搜索词的连续一段"（想给《妖祖》更高分），
            //   实测**没有区分度**：《大明圣祖》里的"圣祖"同样连续包含于
            //   "万妖圣祖"，于是噪音也被提上来。已删掉。
            //
            // ★ 别凭感觉改阈值：这些期望值是在 tests/OfflineTests.cs 里跑出来的，
            //   我手数错了三次。改这里之前先跑测试。
            int hit = 0;
            foreach (char c in k)
                if (t.IndexOf(c) >= 0) hit++;
            if (hit * 2 < k.Length) return 0;
            int partial = 100 + (hit * 100) / k.Length;      // 100 ~ 200
            return partial > 200 ? 200 : partial;
        }

        /// <summary>标题归一化：去书名号/书名点/空白，转小写</summary>
        private static string NormBookTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '《' || c == '》' || c == '〈' || c == '〉' || c == '「' || c == '」'
                    || c == '·' || c == '・' || char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 本地书名索引检索（**完全不联网**）。
        ///
        /// 索引来源有两处：搜索过的书、下载过的书（书架里那本会并进来）。
        /// 所以"我下过的书"和"我搜过的书"都能这样秒查到，
        /// 不用再打一次站点 —— 这也是本项目唯一做得到的"本地搜索"。
        ///
        /// 说清楚限制：它不是全站书目。biquga 没有全站书目接口，
        /// 索引只能靠用过的记录慢慢积累。
        /// </summary>
        private void DoLocalSearch()
        {
            var kw = txtKeyword.Text.Trim();
            if (kw.Length == 0) { MessageBox.Show("先在搜索框里输入书名或作者，再点「本地查」"); return; }

            var list = SearchIndex.Search(kw, 200);
            // 本地查也用同一个词排相关度 —— 索引里命中的同样可能一堆沾边的
            FillBooks(list, kw);
            if (list.Count > 0)
            {
                Log("本地索引命中 " + list.Count + " 条（共索引 " + SearchIndex.BookCount + " 本，未联网）。");
            }
            else
            {
                Log("本地索引里没有「" + kw + "」（共索引 " + SearchIndex.BookCount + " 本）。");
                Log("本地索引只包含你**搜过或下过**的书 —— biquga 没有全站书目接口，");
                Log("所以「没收录」不等于「这本书不存在」，点「搜索」联网找一下即可（结果会自动入库）。");
            }
        }

        /// <summary>输入看起来是不是一个书籍地址（http/https 开头）</summary>
        internal static bool LooksLikeBookAddress(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把"某一章的地址"归一成"书籍主页地址"：
        ///   http://x/11_11763/7394980.html → http://x/11_11763/
        ///   末尾补斜杠；去掉 ?query 和 #hash。
        ///
        /// ★ 为什么要这一步：用户复制的一般是浏览器地址栏里**当前正在看的那一章**，
        ///   而不是书籍主页。直接拿章节页去 LoadBook，解析出的是章节内容、没有目录。
        /// </summary>
        internal static string NormalizeBookUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string s = url.Trim();
            int q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s.Substring(0, q);
            // 最后一段是 xxx.html / xxx.htm → 砍掉，回到书籍目录
            var m = System.Text.RegularExpressions.Regex.Match(
                s, "^(.*/)[^/]*\\.html?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) s = m.Groups[1].Value;
            if (!s.EndsWith("/", StringComparison.Ordinal)) s += "/";
            return s;
        }

        /// <summary>按域名找站点描述；没有对得上的返回 null</summary>
        internal static SiteDescriptor MatchSiteForUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            Uri parsed;
            try { parsed = new Uri(url.Trim()); }
            catch { return null; }
            foreach (var d in SiteRegistry.All)
            {
                if (string.IsNullOrEmpty(d.DefaultBase)) continue;
                Uri b;
                try { b = new Uri(d.DefaultBase); }
                catch { continue; }
                if (string.Equals(parsed.Host, b.Host, StringComparison.OrdinalIgnoreCase)) return d;
            }
            return null;
        }

        /// <summary>
        /// 尝试把输入当书籍地址打开。**成功接管返回 true**（调用方不要再走搜索）。
        /// 只接管规则站；其他站点或不认识的域名返回 false，让原流程继续。
        /// </summary>
        private bool TryOpenByAddress(string url)
        {
            var d = MatchSiteForUrl(url);
            if (d == null)
            {
                Log("「" + url + "」看起来是个链接，但没有已注册站点的域名对得上，仍按关键词搜索。");
                return false;
            }
            ISite s;
            try { s = d.Factory(); }
            catch { return false; }
            if (!(s is RuleSite)) return false;      // 只接管规则站，别抢其他站的处理权

            string bookUrl = NormalizeBookUrl(url);
            var book = new BookInfo
            {
                Site = d.Key,
                Url = bookUrl,
                Dir = RuleSite.PathOf(bookUrl),
                Title = "",
            };
            Log("按地址直连：" + d.DisplayName + "  " + bookUrl
                + (string.Equals(bookUrl, url.Trim(), StringComparison.OrdinalIgnoreCase)
                   ? "" : "（已从章节地址归一为书籍地址）"));
            SetBooksBarText("按地址直连：" + bookUrl + "（正在载入目录）");
            var site2 = s;
            var book2 = book;
            RunBackground("载入目录中…", () => DoLoadBookCore(book2, site2));
            return true;
        }

        private void DoSearch()
        {
            if (_busy) { BusyNotice(Text); return; }
            var kw = txtKeyword.Text.Trim();
            if (kw.Length == 0) { MessageBox.Show("请输入书名或链接"); return; }

            // ★ 输入是**书籍地址**时直接载入目录，不再当关键词去搜。
            //
            //   起因：输入框上写着「书名 / 链接」，而 `SiteCapabilities.AddressIntake`
            //   这个能力**声明了却全项目没有任何地方检查** —— 又是一处死代码。
            //   所以粘一个链接进来按搜索，等于"拿整条 URL 当关键词去站内搜"，
            //   结果自然乱七八糟。
            //
            //   只对规则站生效：它们的 LoadBook 只要 Url 就能工作、路径规则也统一；
            //   其他站点保持原样（万一它们自己的 Search 已经认地址，不能抢权）。
            if (LooksLikeBookAddress(kw) && TryOpenByAddress(kw)) return;

            var site = _site;
            // 本次搜索实际用的站点 Key —— 缓存要按站点分开存，否则切站后会
            // 直接返回上一个站点的结果（拿到错的书籍地址去请求另一个站）。
            string searchSiteKey = SiteKeyOf(site);
            RunBackground("搜索中…", () =>
            {
                // ① 先查本地缓存：搜过的词直接秒回，不再打站点
                //    （缓存带站点维度：同词不同站是两条记录）
                var cached = SearchIndex.GetQuery(kw, searchSiteKey);
                if (cached != null)
                {
                    Log("搜索「" + kw + "」—— 命中本地缓存（" + cached.Count + " 条），未联网。");
                    FillBooks(cached, kw);
                    return;
                }

                var list = site.Search(kw, Log);
                // ★ 站点实现有可能返回 null（比如被限流时直接放弃）。
                //   以前这里直接 `list.Count` → 空引用，用户看到的是"搜索崩了"而不是"没搜到"。
                if (list == null) list = new List<BookInfo>();
                // ② 结果落盘：这次搜索下次能重放，同时把书并进累积索引
                if (list.Count > 0) SearchIndex.PutQuery(kw, list, searchSiteKey);
                FillBooks(list, kw);

                // ★ 开了「自动选最优」就比一比别的站：同名书里谁总时长最短。
                //   放在这里（而不是下载时）的原因：用户下一步就是"载入目录 → 勾章节"，
                //   选站必须发生在**载入之前**，否则等于已经用错源把目录抓完了。
                //   只在"本站确实有同名书"时比较 —— 一个都没有的情况下面那段
                //   本来就会去跨站找，别重复扫两遍。
                if (list.Count > 0 && _autoSite) TryAutoPickBestSite(kw, list);

                if (list.Count == 0)
                {
                    if (site.Name == "番茄小说")
                    {
                        Log("番茄没有公开的中文搜索接口。请点「浏览器搜索」去官网找书，再把链接或 book_id 粘回来。");
                        var r = MessageBox.Show(
                            "番茄小说不提供可用的中文搜索接口（官方搜索是浏览器里异步加载的）。\n\n" +
                            "要按中文书名找书，请点「浏览器搜索」按钮：\n" +
                            "它会打开番茄官网搜索页，你把找到的书籍链接粘回来即可。\n\n现在就去打开吗？",
                            "番茄站：改用浏览器搜索", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                        if (r == DialogResult.Yes) DoWebSearch();
                    }
                    else
                    {
                        Log("没有搜到这本书，换个书名或只输入书名关键字再试。");
                    }
                    return;
                }
                // 番茄站只有一条结果，直接载入目录
                if (list.Count == 1 && site.Name == "番茄小说")
                {
                    _currentBook = null;
                    DoLoadBookCore(list[0], site);
                    return;
                }

                // ★ 搜到了、但**一条都不沾边**时，顺手问问别的站有没有。
                //
                //   起因（用户实测）：他搜《万妖圣祖》，工具默认用笔尖中文（评分最快），
                //   而那站**根本没有这本书** —— 返回 50 条按单字匹配的
                //   《圣妖之祖》《大明圣祖》《人道圣祖》。用户翻遍了也找不到，
                //   结论是"连搜都搜不出来"。
                //   实测《万妖圣祖》只在笔趣阁上有（PC/移动版第一条就是它）。
                //
                //   站点之间收录差异是常态，工具不该让用户自己去猜哪个站有。
                //   这里的判据很保守：**一个"整词命中"都没有**才去问别的站。
                bool anyGood = false;
                if (list != null)
                    foreach (var b in list)
                        if (b != null && TitleScore(b.Title, kw) >= 600) { anyGood = true; break; }
                if (!anyGood && list != null && list.Count > 0)
                {
                    Log("⚠ 「" + SiteRegistry.LabelOf(searchSiteKey) + "」返回的 " + list.Count
                        + " 条里没有一条标题含「" + kw + "」—— 换个源找找看…");
                    // ★ 这趟是**附加服务**，绝不能因为它失败就弹模态框。
                    //   用户报过一个弹窗：「请求失败（已重试 3 次）：…biquga.com/search.html
                    //   curl 退出码 28」—— 他明明选的是笔趣阁365，报错的却是笔趣阁 PC 版，
                    //   因为跨站扫描扫到了那个慢站。搜索本身是成功的，弹窗纯属噪音。
                    List<SiteBookHit> hits = null;
                    try { hits = ScanOtherSitesForExact(kw, searchSiteKey); }
                    catch (Exception ex) { Log("   · 跨站查找中断（不影响上面的结果）：" + ex.Message); }
                    if (hits != null && hits.Count > 0)
                    {
                        foreach (var h in hits)
                            Log("   · 「" + h.SiteLabel + "」有：" + h.Book.Title
                                + (string.IsNullOrEmpty(h.Book.Author) ? "" : "  作者：" + h.Book.Author));
                        Log("   → 在「站点」下拉框里切到该站，再点「搜索」即可。");
                    }
                    else
                    {
                        Log("   · 其他站点也没找到标题含「" + kw + "」的书。");
                    }
                }
            });
        }

        /// <summary>
        /// 这本书来自哪个站，用于结果列表的「来源」列。
        /// BookInfo.Site 为空时退到"当前站点"。
        /// </summary>
        private string SiteLabelOfBook(BookInfo b)
        {
            // 直接用 SiteRegistry.LabelOf：它对"注册表里没有的老 key"也会原样返回，
            // 和 BookshelfDialog 里那个同名私有方法行为一致。
            // （不要去调那个私有方法 —— 它是对话框的，MainForm 这里够不着。）
            if (b == null || string.IsNullOrEmpty(b.Site)) return SiteRegistry.LabelOf(SiteKeyOf(_site));
            return SiteRegistry.LabelOf(b.Site);
        }

        /// <summary>
        /// 按**书自己所属的站点**造一个站点实例。
        ///
        /// ★ 为什么要这样：跨站搜索的结果可能来自别的站，而 `_site` 是当前下拉框选的站。
        ///   如果拿 `_site` 去 LoadBook 一本取自别的站的书，就会用错的站点规则
        ///   去解析它的页面 —— 结果是"载入失败"或"载入成别的书"。
        ///   书自己带着 Site 字段，就该按它来。
        /// </summary>
        private ISite SiteForBook(BookInfo b)
        {
            string key = (b == null || string.IsNullOrEmpty(b.Site)) ? null : b.Site;
            if (!string.IsNullOrEmpty(key))
            {
                var d = SiteRegistry.ByKey(key);
                if (d != null)
                {
                    var s = d.Factory();
                    if (s != null) return s;
                }
            }
            return _site;
        }

        /// <summary>
        /// 到**其他站点**去找标题精确命中 kw 的书（不含当前站）。
        ///
        /// 为什么单独做这趟：站点之间收录差异很大，实测《万妖圣祖》只在笔趣阁上有，
        /// 而"评分最快"的笔尖中文没有 —— 用户按提示换源之前，先替他确认一下哪个站有，
        /// 比让他一个个手动试要省事得多。
        ///
        /// 保守之处：只回报**标题含整词**（score ≥ 600）的结果，避免把
        /// "按单字沾边"的那一堆噪音也报成"这个站有"。
        /// </summary>
        /// <summary>自动选站进行中（防止"切站 → 再搜索 → 又切站"绕圈）</summary>
        private bool _autoPicking;

        /// <summary>
        /// 「自动选最优」：同名书里，按 **章节数 × 每章耗时**（预计总时长）选最小的那个站。
        ///
        /// 用户的要求是"同名 + 最快 + 章数最多"。这三条会打架，所以合成一个目标：
        /// 总时长越小越好（详见 SitePicker 的注释）。
        ///
        /// 为什么要跨站扫：**只有比较过才知道谁最优**。所以即使当前站搜到了，
        /// 也要去别的站问一遍 —— 但给**预算**（8 秒）和**命中上限**（4 个），
        /// 免得为了"选站"让用户干等。
        ///
        /// 为什么不用抓目录拿真实章数：pmzw 实测一次目录 25 秒、笔趣阁 PC 更久。
        /// 搜索表格里自带「最新章节」，从里面解析章号就够比较了（见 SitePicker.EstimateChapters）。
        /// </summary>
        private void TryAutoPickBestSite(string kw, List<BookInfo> currentList)
        {
            if (!_autoSite || _autoPicking) return;
            if (string.IsNullOrEmpty(kw)) return;
            _autoPicking = true;
            try
            {
                string curKey = SiteKeyOf(_site);
                var cands = new List<SitePicker.Candidate>();

                // 本站在当前结果里的最佳同名书
                BookInfo own = BestSameTitle(currentList, kw);
                if (own != null) cands.Add(MakeCandidate(curKey, SiteRegistry.LabelOf(curKey), own, kw));

                // 其他站（有预算、有上限）
                List<SiteBookHit> hits = null;
                try
                {
                    Log("· 自动选最优：正在比较其他站点（最多 8 秒）…");
                    hits = ScanSitesForExact(kw, curKey, Log, delegate () { return _cancel; }, 4, 8000);
                }
                catch (Exception ex) { Log("   · 跨站比较中断（不影响当前结果）：" + ex.Message); }
                if (hits != null)
                {
                    foreach (var h in hits)
                    {
                        if (h == null || h.Book == null) continue;
                        cands.Add(MakeCandidate(h.SiteKey, h.SiteLabel, h.Book, kw));
                    }
                }

                string reason;
                var best = SitePicker.Pick(cands, out reason);
                if (cands.Count >= 2) Log(SitePicker.Describe(cands, best));
                Log("   · " + reason);
                if (best == null) return;

                if (string.Equals(best.SiteKey, curKey, StringComparison.OrdinalIgnoreCase))
                {
                    Log("   → 当前站点已经是最优的，不用切换。");
                    return;
                }

                // 换到最优站点，并把那本书载入目录（用户接下来直接勾章节就行）
                Log("   → 自动切到「" + best.SiteLabel + "」并载入《" + best.Title + "》…");
                SelectSiteKey(best.SiteKey);
                var target = FindBookByTitle(best.Title, best.SiteKey, kw);
                if (target != null) BeginInvoke(new Action(delegate { DoLoadBookSpecific(target, kw); }));
            }
            catch (Exception ex)
            {
                Log("自动选最优失败（不影响手动操作）：" + ex.Message);
            }
            finally { _autoPicking = false; }
        }

        /// <summary>把一条搜索结果变成候选项（章数从「最新章节」估，速度取站点评分缓存）</summary>
        internal SitePicker.Candidate MakeCandidate(string siteKey, string label, BookInfo b, string kw)
        {
            var c = new SitePicker.Candidate
            {
                SiteKey = siteKey,
                SiteLabel = label,
                Title = b != null ? b.Title : "",
                Exact = b != null && SitePicker.IsSameTitle(b.Title, kw),
            };
            // 章数：优先用搜索行里的「最新章节」
            if (b != null && !string.IsNullOrEmpty(b.Desc))
            {
                int n = SitePicker.EstimateChapters(b.Desc);
                if (n > 0) { c.Chapters = n; c.ChaptersSource = "按最新章节估"; }
            }
            // 速度：站点评分缓存里有就用
            if (_siteScores != null)
            {
                foreach (var s in _siteScores)
                {
                    if (s == null || !string.Equals(s.Key, siteKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (s.ChapterMeasured && s.ChapterMs > 0 && !double.IsInfinity(s.ChapterMs))
                        c.MsPerChapter = s.ChapterMs;
                    break;
                }
            }
            return c;
        }

        /// <summary>结果列表里标题与关键词完全一致的那一条（没有就 null）</summary>
        private BookInfo BestSameTitle(List<BookInfo> list, string kw)
        {
            if (list == null) return null;
            BookInfo best = null;
            int bestCount = -1;
            foreach (var b in list)
            {
                if (b == null || string.IsNullOrEmpty(b.Title)) continue;
                if (!SitePicker.IsSameTitle(b.Title, kw)) continue;
                int n = string.IsNullOrEmpty(b.Desc) ? 0 : SitePicker.EstimateChapters(b.Desc);
                if (best == null || n > bestCount) { best = b; bestCount = n; }
            }
            return best;
        }

        /// <summary>按站点 key 切换下拉框（切换会触发 ApplySiteSelection 重建站点对象）</summary>
        private void SelectSiteKey(string key)
        {
            if (cboSite == null || string.IsNullOrEmpty(key)) return;
            int idx = SiteRegistry.IndexOf(key);
            if (idx < 0 || idx >= cboSite.Items.Count) return;
            _reloadingSites = false;          // 允许 SelectedIndexChanged 触发真正的切换
            cboSite.SelectedIndex = idx;
        }

        /// <summary>在某个站的最新搜索结果里按书名找一本（用于切站后载入）</summary>
        private BookInfo FindBookByTitle(string title, string siteKey, string kw)
        {
            try
            {
                var d = SiteRegistry.ByKey(siteKey);
                if (d == null || d.Factory == null) return null;
                var s = d.Factory();
                var found = s.Search(kw, null);
                if (found == null) return null;
                BookInfo best = null;
                foreach (var b in found)
                {
                    if (b == null || string.IsNullOrEmpty(b.Title)) continue;
                    if (!SitePicker.IsSameTitle(b.Title, title)) continue;
                    best = b; break;
                }
                if (best != null && string.IsNullOrEmpty(best.Site)) best.Site = siteKey;
                return best;
            }
            catch { return null; }
        }

        /// <summary>载入指定搜索结果（自动选站切过去之后用）</summary>
        private void DoLoadBookSpecific(BookInfo target, string kw)
        {
            if (target == null) return;
            try
            {
                // 列表里只放这一本并选中它 —— DoLoadBook 读的是"选中项"，这样最稳
                FillBooks(new List<BookInfo> { target }, kw);
                if (lstBooks != null && lstBooks.Items.Count > 0)
                {
                    lstBooks.Items[0].Selected = true;
                    lstBooks.Items[0].Focused = true;
                }
                DoLoadBook();
            }
            catch (Exception ex) { Log("载入最优站点的书失败：" + ex.Message); }
        }

        private List<SiteBookHit> ScanOtherSitesForExact(string kw, string skipSiteKey)
        {
            if (_cancel) return new List<SiteBookHit>();
            // ★ 把取消回调**传进去**。以前只在这一趟开始前查一次 _cancel，
            //   一旦开始扫，用户点「取消」要等 4 个站跑完（每站最长 30 秒 × 重试）
            //   才停下来 —— 界面像是卡死了。现在每扫完一个站就检查一次。
            return ScanSitesForExact(kw, skipSiteKey,
                delegate (string s) { Log(s); },
                delegate () { return _cancel; },
                2,
                12000);
        }

        /// <summary>
        /// 跨站扫描的**纯逻辑**（不依赖窗体，便于自动化测试）。
        ///
        /// 只回报"标题含整词"（score ≥ 600）的命中 —— 不能把"按单字沾边"
        /// 的那一堆噪音报成"这个站有"，否则用户白高兴一场。
        ///
        /// canceled：每扫完一个站就调用一次，返回 true 就立刻收工。
        /// maxHits：找到这么多站就够了，不必把剩下的站都打一遍。
        /// budgetMs：总时间预算。超过就停 —— 这趟是**附加**的网络开销，
        ///   绝不能让它把一次搜索拖成几分钟（笔趣阁 PC 版单站就可能超时 30 秒）。
        /// </summary>
        internal static List<SiteBookHit> ScanSitesForExact(string kw, string skipSiteKey, Action<string> log)
        {
            return ScanSitesForExact(kw, skipSiteKey, log, null, 0, 0);
        }

        internal static List<SiteBookHit> ScanSitesForExact(string kw, string skipSiteKey,
            Action<string> log, Func<bool> canceled, int maxHits)
        {
            return ScanSitesForExact(kw, skipSiteKey, log, canceled, maxHits, 0);
        }

        internal static List<SiteBookHit> ScanSitesForExact(string kw, string skipSiteKey,
            Action<string> log, Func<bool> canceled, int maxHits, int budgetMs)
        {
            var hits = new List<SiteBookHit>();
            if (string.IsNullOrEmpty(kw)) return hits;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
            foreach (var d in SiteRegistry.All)
            {
                if (canceled != null && canceled())
                {
                    if (log != null) log("   · 已取消跨站查找");
                    break;
                }
                if (maxHits > 0 && hits.Count >= maxHits) break;
                if (budgetMs > 0 && sw.ElapsedMilliseconds > budgetMs)
                {
                    if (log != null) log("   · 跨站查找已超时（" + (budgetMs / 1000) + " 秒上限），先不找了");
                    break;
                }
                if (string.Equals(d.Key, skipSiteKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Factory == null) continue;
                try
                {
                    var s = d.Factory();
                    if (s == null) continue;
                    var found = s.Search(kw, null);      // log 传 null：这趟是探测，别刷屏
                    if (found == null)
                    {
                        if (log != null) log("   · 「" + d.DisplayName + "」搜索无结果");
                        continue;
                    }
                    BookInfo best = null;
                    int bestScore = 0;
                    foreach (var b in found)
                    {
                        if (b == null || string.IsNullOrEmpty(b.Title)) continue;
                        int sc = TitleScore(b.Title, kw);
                        if (sc < 600) continue;
                        if (best == null || sc > bestScore) { best = b; bestScore = sc; }
                    }
                    if (best != null)
                    {
                        // 记住它来自哪个站 —— 载入/下载要按这个站去取
                        best.Site = d.Key;
                        hits.Add(new SiteBookHit { SiteKey = d.Key, SiteLabel = d.DisplayName, Book = best });
                        if (log != null)
                            log("   · 「" + d.DisplayName + "」有：《" + best.Title + "》（" + found.Count + " 条里命中）");
                    }
                    else if (log != null)
                    {
                        log("   · 「" + d.DisplayName + "」" + found.Count + " 条，没有标题含「" + kw + "」的");
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("   · 「" + d.DisplayName + "」搜索失败：" + ex.Message);
                }
            }
            }
            catch (Exception ex)
            {
                // 兜底：这趟是附加服务，任何意外都只记日志，绝不往上抛成弹窗
                if (log != null) log("   · 跨站查找出错（不影响正常搜索）：" + ex.Message);
            }
            return hits;
        }

        /// <summary>番茄站的中文搜索：没有公开接口，改为打开官网搜索页，再让用户把链接贴回来</summary>
        private void DoWebSearch()
        {
            var kw = txtKeyword.Text.Trim();
            var url = FanqieSite.SearchPageUrl(kw.Length > 0 ? kw : "");
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                Log("打开浏览器失败：" + ex.Message);
                Clipboard.SetText(url);
                MessageBox.Show("没能自动打开浏览器，搜索页地址已复制到剪贴板：\n" + url,
                    "浏览器搜索", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Log("已用默认浏览器打开番茄搜索页：" + url);
            Log("步骤：1) 在网页里找到那本书并点进去  2) 复制地址栏链接（形如 .../page/7406592861791063064）  3) 粘贴到输入框，点「搜索」或「载入目录」");
            MessageBox.Show(
                "已打开番茄官网搜索页。\n\n" +
                "接下来：\n" +
                "1) 在网页里找到你要的书，点进书籍详情页\n" +
                "2) 复制地址栏的链接（形如 https://fanqienovel.com/page/7406592861791063064）\n" +
                "3) 回到本工具，把链接粘贴到输入框\n" +
                "4) 点「搜索」——工具会自动取出 book_id 并载入目录\n\n" +
                "（也可以直接粘贴 book_id 数字，或章节页链接）",
                "浏览器搜索 · 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 「刷新目录」按钮：如果这本书有遍历断点，就先问一句"接着上次走，还是从头来"。
        ///
        /// 为什么让用户选而不是自动续：续爬省时间，但**从上次的位置接着走**意味着
        /// 如果站点在这期间改了目录，拿到的会是"新旧拼接"的结果。
        /// 用户自己知道自己是"昨天没爬完"还是"想要最新目录"，所以由他决定。
        /// </summary>
        private void DoLoadBookRefresh()
        {
            if (_busy) { BusyNotice("刷新目录"); return; }
            var book = lstBooks.SelectedItems.Count > 0 ? lstBooks.SelectedItems[0].Tag as BookInfo : null;
            if (book != null && !string.IsNullOrEmpty(book.Dir) && CrawlResume.Exists("biquga", book.Dir))
            {
                if (!SuppressDialogs)
                {
                    var r = MessageBox.Show(this,
                        "这本书上次的目录遍历没有走完，本地留着一份断点。\n\n" +
                        "「是」= 从断点继续（省时间，接着上次的进度走）\n" +
                        "「否」= 从头重新遍历（目录最完整，但要重新走一遍）\n\n" +
                        "如果离上次遍历已经很久、站点更新了不少章节，建议选「否」。",
                        "继续上次的遍历？", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return;
                    if (r == DialogResult.Yes)
                    {
                        _resumeBookDir = book.Dir;
                        Log("已选择「继续上次遍历」：" + _resumeBookDir);
                        DoLoadBook(true);
                        _resumeBookDir = null;
                        return;
                    }
                }
            }
            _resumeBookDir = null;
            DoLoadBook(true);
        }

        private void DoLoadBook(bool forceRefresh = false)
        {
            if (_busy) { BusyNotice(Text); return; }
            if (forceRefresh) Log("已选择“刷新目录”：忽略本地缓存，重新遍历站点。");

            if (lstBooks.SelectedItems.Count == 0)
            {
                // 番茄站允许直接用手输的 book_id
                if (_site.Name == "番茄小说")
                {
                    var list = _site.Search(txtKeyword.Text.Trim(), Log);
                    if (list.Count == 0) { MessageBox.Show("请先输入番茄 book_id 或分享链接"); return; }
                    var s = _site;
                    SetRefresh(s, forceRefresh);
                    RunBackground("载入目录中…", () => DoLoadBookCore(list[0], s));
                    return;
                }
                MessageBox.Show("请先在列表里选一本书");
                return;
            }
            var book = lstBooks.SelectedItems[0].Tag as BookInfo;

            // ★ 用**书自己所属的站点**，不是当前下拉框选的那个。
            //   跨站搜索结果里可能混着别的站的书 —— 拿当前站点的规则去解析
            //   别的站的页面，会载入失败或载入成别的书。
            var site = SiteForBook(book);
            string bookSiteKey = book == null || string.IsNullOrEmpty(book.Site) ? SiteKeyOf(site) : book.Site;
            if (!string.Equals(bookSiteKey, SiteKeyOf(_site), StringComparison.OrdinalIgnoreCase))
                Log("这本书来自「" + SiteRegistry.LabelOf(bookSiteKey) + "」，将用该站点的规则载入目录。");

            // 载入目录前先确认**这本书所属的站点**可用：不可用就提示换源
            // （探活结果由启动时/手动检测得到）
            if (string.Equals(bookSiteKey, SiteKeyOf(_site), StringComparison.OrdinalIgnoreCase))
            {
                if (!EnsureSiteUsable()) return;
                site = _site;       // EnsureSiteUsable 可能切了站点，重新取一次
            }
            SetRefresh(site, forceRefresh);
            RunBackground("载入目录中…", () => DoLoadBookCore(book, site));
        }

        /// <summary>把“强制刷新 / 离线模式 / 续爬”标志透传给站点实现</summary>
        private void SetRefresh(ISite site, bool force)
        {
            bool offline = chkOffline != null && chkOffline.Checked;
            var bq = site as BiqugaSite;
            if (bq != null)
            {
                bq.ForceRefresh = force || offline;
                bq.Offline = offline;
                // 注意：这里**没有**并发设置 —— 笔趣阁 PC 版的目录遍历是
                // 反向链条（下一页地址写在当前页 HTML 里），结构上不能并发。
                // 想快就换源（笔尖中文一页给全目录）。见 BiqugaSite.cs 顶部注释。
                // 续爬：只有在"这本书确实有断点"时才打开，否则会白读一次不存在的断点文件
                bq.Resume = _resumeBookDir != null &&
                            string.Equals(_resumeBookDir, bq.CurrentDir, StringComparison.Ordinal);
                if (offline && !_offlineHinted)
                {
                    _offlineHinted = true;
                    Log("离线模式已开启：载入目录会连正文一起抓下来，之后下载不再联网（下完可取消勾选）。");
                }
            }
            var bm = site as BiqugaMobileSite;
            if (bm != null)
            {
                bm.ForceRefresh = force || offline;
                bm.PrefetchText = offline;
                bm.Workers = offline ? _settings.BiqugaOfflineWorkers : _settings.BiqugaOnlineWorkers;
                if (offline && !_offlineHinted)
                {
                    _offlineHinted = true;
                    Log(string.Format("离线模式已开启：载入目录时会用 {0} 个线程并发把整本正文抓下来，之后下载不再联网"
                        + "（移动版下 1000 章约 10 分钟）。", bm.Workers));
                }
            }
            var fq = site as FanqieSite;
            if (fq != null) fq.ForceRefresh = force;
        }

        /// <summary>
        /// 把 settings.ini 里的值真正作用到取数层与站点实现上。
        /// 任何路径都要经过这里，否则改了设置不生效（历史上就吃过“改了没反应”的亏）。
        /// </summary>
        private void ApplySettings()
        {
            Http.Configure(_settings.MinDelayMs, _settings.MaxDelayMs);

            // 繁简开关：从设置回填到界面（设置对话框/ini 改过之后要同步勾选状态）
            if (chkTraditional != null && chkTraditional.Checked != _settings.OutputTraditional)
                chkTraditional.Checked = _settings.OutputTraditional;

            // 站点级参数（站点配置.ini）优先于全局设置：联网参数（并发/间隔/超时/重试/UA）按站点走，
            // 这样"某个站点被限速要调慢"不用动全局。文件不存在时用内置默认值。
            var prof = _profile ?? SiteProfileStore.Get(SiteKeyOf(_site));
            SiteProfileStore.Apply(prof);

            var bm = _site as BiqugaMobileSite;
            if (bm != null)
                bm.Workers = chkOffline != null && chkOffline.Checked
                    ? _settings.BiqugaOfflineWorkers : _settings.BiqugaOnlineWorkers;

            // 站点配置里明确写了 workers 就用它（用户手改的优先级最高）。
            //
            // ★ 只作用于**移动版**（它的目录页能并发抓）。
            //   PC 版的目录遍历是"反向链条"：第 N-1 页的地址只写在第 N 页的 HTML 里
            //   （`var uiiekp0do`），必须先取到第 N 页才知道下一页取什么 ——
            //   **结构上无法并发**。原来这里给 BiqugaSite.CrawlWorkers 赋值，
            //   但那个字段全项目没有任何地方读它，是个骗人的旋钮
            //   （docs/架构.md 还照着它写了"PC 版目录遍历用 CrawlWorkers"，
            //   描述的是一段不存在的行为）。该字段已删除。
            if (prof != null && prof.Workers > 0)
            {
                if (bm != null) bm.Workers = prof.Workers;
            }
        }

        /// <summary>把当前站点映射到站点配置里的 key</summary>
        private static string SiteKeyOf(ISite site)
        {
            // 站点列表的唯一真相来源是 SiteRegistry。这里以前是一串 if，
            // 每加一个站点就会漏掉一处（而且 BiqugaMobileSite 的顺序错了会静默归到 PC 版）。
            string k = SiteRegistry.KeyOf(site);
            return k == null ? CoreSiteKey : k;
        }

        // ============================================================
        //  站点注册表接入
        // ============================================================

        /// <summary>找不到站点时的兜底（只在注册表被改坏时才会用到）</summary>
        private const string CoreSiteKey = "biquga";

        /// <summary>重填下拉框期间不要触发事件（否则会递归）</summary>
        private bool _reloadingSites;

        /// <summary>「自动选最快」开关</summary>
        private bool _autoSite;

        /// <summary>实测出来的评分表；null = 还没测过</summary>
        private List<SiteScore> _siteScores;

        /// <summary>正在实测评分</summary>
        private bool _rating;

        /// <summary>去掉"（当前不可用）"后缀的纯站点名，解决"名字前后空格对不上"</summary>
        private static string CleanSiteLabel(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf("（当前不可用）", StringComparison.Ordinal);
            if (i < 0) i = s.IndexOf("(当前不可用)", StringComparison.Ordinal);
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        /// <summary>当前该用哪个站点 Key（考虑"自动选最快"）</summary>
        private string EffectiveSiteKey()
        {
            if (_autoSite)
            {
                string fast = SiteRater.FastestKey(_siteScores);
                if (!string.IsNullOrEmpty(fast)) return fast;
                // 评分还没有（或全不可用）→ 回退到内置笔趣阁 PC 版，
                // 也就是"自动"打开之前的老行为，不会让用户突然没得用。
                return CoreSiteKey;
            }
            int idx = cboSite == null ? -1 : cboSite.SelectedIndex;
            var all = SiteRegistry.All;
            if (idx >= 0 && idx < all.Count) return all[idx].Key;
            return CoreSiteKey;
        }

        /// <summary>当前生效的站点描述（永远非 null）</summary>
        private SiteDescriptor CurrentDescriptor
        {
            get
            {
                var d = SiteRegistry.ByKey(EffectiveSiteKey());
                if (d == null) d = SiteRegistry.ByKey(CoreSiteKey);
                return d;
            }
        }

        /// <summary>
        /// 站点列表变了、探活结果变了、或"自动"开关动了之后，把下拉框与
        /// 当前站点对象重新对齐。**所有**改站点的路径最后都走这里。
        /// </summary>
        private void ApplySiteSelection()
        {
            if (cboSite == null) return;
            string key = EffectiveSiteKey();
            int idx = SiteRegistry.IndexOf(key);

            _reloadingSites = true;
            try
            {
                if (idx >= 0 && idx < cboSite.Items.Count && cboSite.SelectedIndex != idx)
                    cboSite.SelectedIndex = idx;
            }
            finally { _reloadingSites = false; }

            UpdateSiteHint();
        }

        /// <summary>
        /// **只**填下拉框条目并定位到 selectKey，不碰任何别的状态。
        /// 构造函数早期用它（那时其他字段还没建好，不能调 UpdateSiteHint）。
        /// </summary>
        private void PopulateSiteCombo(string selectKey)
        {
            if (cboSite == null) return;
            int keep = SiteRegistry.IndexOf(selectKey);
            if (keep < 0) keep = Math.Max(0, SiteRegistry.IndexOf(CoreSiteKey));

            _reloadingSites = true;
            try
            {
                cboSite.BeginUpdate();
                try
                {
                    cboSite.Items.Clear();
                    foreach (var d in SiteRegistry.All)
                        cboSite.Items.Add(d.DisplayName);
                    if (cboSite.Items.Count > 0)
                        cboSite.SelectedIndex = Math.Min(keep, cboSite.Items.Count - 1);
                }
                finally { cboSite.EndUpdate(); }
            }
            finally { _reloadingSites = false; }
        }

        /// <summary>
        /// 重建下拉框内容。站点列表变了、或探活结果变了都走这里 ——
        /// 这样"下拉框里有哪些站"永远和 SiteRegistry 一致。
        ///
        /// selectKey 传 null = 沿用当前生效的站点。
        /// </summary>
        private void ReloadSiteCombo(string selectKey)
        {
            if (cboSite == null) return;
            PopulateSiteCombo(selectKey ?? EffectiveSiteKey());
            UpdateSiteHint();       // 不依赖 SelectedIndexChanged（它可能因为下标没变而不触发）
        }

        /// <summary>
        /// **下载阶段**的并发数（并发抓取正文 + 按目录顺序落盘，见 DownloadRunner.Workers）。
        ///
        /// ★ 注意这只影响下载，不影响载入目录：
        ///   下载时**所有章节地址事先都知道**（目录已经拿到了），所以能并发；
        ///   而笔趣阁 PC 版的目录遍历是反向链条，结构上串行（见 BiqugaSite.cs）。
        ///   所以这个值调大，是"下得快"，不是"目录载入得快"。
        ///
        /// 取值规则：站点配置.ini 里写了 workers 就用它（用户手改优先），
        /// 否则按站点取设置里的并发值。
        ///
        /// 番茄保持 1（串行）：它的正文风控对并发最敏感，而且正文本来就要靠第三方核心，
        /// 并发下载只会更快撞验证码，不会更快拿到正文。
        /// </summary>
        private int DownloadWorkersFor(ISite site)
        {
            var prof = _profile ?? SiteProfileStore.Get(SiteKeyOf(site));
            if (prof != null && prof.Workers > 0) return prof.Workers;
            if (site is BiqugaSite) return _settings.BiqugaPcWorkers;
            if (site is BiqugaMobileSite)
                return (chkOffline != null && chkOffline.Checked)
                    ? _settings.BiqugaOfflineWorkers : _settings.BiqugaOnlineWorkers;
            return 1;
        }

        // ============================================================
        //  书架 / 任务队列 / 检查更新
        // ============================================================

        /// <summary>按站点 key 建一个能用的 ISite（书架追更时用：那时候没有界面上下文）</summary>
        internal static ISite MakeSite(string siteKey)
        {
            if (string.Equals(siteKey, "fanqie", StringComparison.OrdinalIgnoreCase)) return new FanqieSite();
            if (string.Equals(siteKey, "biquga", StringComparison.OrdinalIgnoreCase)) return new BiqugaSite();
            return new BiqugaMobileSite();
        }

        /// <summary>把当前这本书记进书架（下载/更新成功后调用）</summary>
        private void RememberInShelf(BookInfo book, string filePath, bool downloaded)
        {
            try
            {
                if (book == null || string.IsNullOrEmpty(book.Title)) return;
                var shelf = Bookshelf.Load();
                shelf.Touch(book, filePath, book.Chapters != null ? book.Chapters.Count : 0, downloaded);
                if (!shelf.Save())
                    Log("提示：书架写入失败（目录可能不可写），不影响下载结果。");
            }
            catch (Exception ex)
            {
                Log("提示：记书架时出错（不影响下载）：" + ex.Message);
            }
        }

        /// <summary>「书架」：列出下载过的书，支持一键追更/导出/打开目录。</summary>
        private void DoOpenShelf()
        {
            using (var dlg = new BookshelfDialog(this))
            {
                dlg.ShowDialog(this);
            }
        }

        /// <summary>「任务队列」：粘贴多本书名，依次下载。</summary>
        private void DoOpenQueue()
        {
            if (_busy) { BusyNotice("任务队列"); return; }
            using (var dlg = new QueueDialog(this))
            {
                dlg.ShowDialog(this);
            }
        }

        /// <summary>「检查更新」：问 GitHub 有没有新版本。只提示，不自动替换自己。</summary>
        private void DoCheckUpdate()
        {
            if (_busy) { BusyNotice("检查更新"); return; }
            if (!SuppressDialogs) btnCheckUpdate.Enabled = false;
            RunBackground("正在检查更新…", () =>
            {
                var r = UpdateChecker.Check(Log);
                UiInvoke(() =>
                {
                    lblStatus.Text = r.Ok
                        ? (r.HasUpdate ? "发现新版本 " + r.Latest : "已是最新版本")
                        : "检查更新失败";
                    if (!SuppressDialogs) btnCheckUpdate.Enabled = true;
                });

                if (SuppressDialogs) return;
                if (r.Ok && !r.HasUpdate)
                {
                    MessageBox.Show(string.Format("当前已是最新版本（{0}）。", r.Current),
                        "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var text = r.Ok
                    ? string.Format("发现新版本！\n\n当前版本：{0}\n最新版本：{1}\n\n更新说明：\n{2}\n\n要打开下载页吗？",
                        r.Current, r.Latest,
                        string.IsNullOrWhiteSpace(r.Notes) ? "（这次发布没有写说明）" : r.Notes)
                    : r.Message;
                var cap = r.Ok ? "发现新版本" : "检查更新失败";
                var icon = r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning;

                if (!r.Ok)
                {
                    MessageBox.Show(text, cap, MessageBoxButtons.OK, icon);
                    return;
                }
                if (MessageBox.Show(text, cap, MessageBoxButtons.YesNo, icon) == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.DownloadUrl) { UseShellExecute = true }); }
                    catch (Exception ex) { Log("打不开浏览器：" + ex.Message + "\n请手动访问：" + r.DownloadUrl); }
                }
            });
        }

        /// <summary>取当前站点该用的输出编码（站点配置里可指定 gbk）</summary>
        internal System.Text.Encoding CurrentFileEncoding()
        {
            var prof = _profile ?? SiteProfileStore.Get(SiteKeyOf(_site));
            return prof == null ? new System.Text.UTF8Encoding(true) : prof.FileEncoding();
        }

        /// <summary>
        /// 构建设置对话框（不 Show，方便自动化测试量它的布局）。
        /// 布局用 TableLayoutPanel + Dock，不再硬编码坐标 —— 和主界面同一套原则，
        /// 见 docs/界面布局.md。返回值里带上输入控件，调用方负责 ShowDialog 与取结果。
        /// </summary>
        internal static Form BuildSettingsDialog(AppSettings s, Font font, out NumericUpDown[] nums)
        {
            var dlg = new Form
            {
                Text = "设置（并发与重试）",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                Font = font,
                ClientSize = new Size(470, 330),
                AutoScaleMode = AutoScaleMode.Font,
                AutoScaleDimensions = new SizeF(7F, 15F),
            };

            var tip = new Label
            {
                Text = "并发越大越快，但太大容易触发站点限速；8 是实测比较稳的值。改完立即生效。",
                Dock = DockStyle.Top,
                Height = 34,
                ForeColor = Color.DimGray,
            };

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(0),
                AutoSize = false,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));

            string[] labels = { "移动版·离线模式并发", "移动版·普通下载并发", "PC 版并发", "每章请求间隔(毫秒)", "失败章节自动重试(轮)" };
            string[] hints =
            {
                "整本正文预抓的线程数（离线模式用）",
                "普通下载的线程数（没勾离线模式时用）",
                "PC 版站点的线程数",
                "每次请求之间的随机间隔",
                "整本下完后，只对失败章节再跑几轮（0=不重试）",
            };
            int[] values = { s.BiqugaOfflineWorkers, s.BiqugaOnlineWorkers, s.BiqugaPcWorkers, s.MinDelayMs, s.RetryPasses };
            int[] mins = { 1, 1, 1, 1, 0 };
            int[] maxs = { 32, 32, 32, 5000, 3 };

            nums = new NumericUpDown[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                var row = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 2,
                    Margin = new Padding(0, 0, 0, 6),
                };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
                row.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
                row.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

                var lbl = new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
                nums[i] = new NumericUpDown
                {
                    Dock = DockStyle.Fill,
                    Minimum = mins[i],
                    Maximum = maxs[i],
                    Value = Math.Min(Math.Max(values[i], mins[i]), maxs[i]),
                    Margin = new Padding(4, 0, 0, 0),
                };
                var hint = new Label
                {
                    Text = hints[i],
                    Dock = DockStyle.Fill,
                    ForeColor = Color.Gray,
                    Font = new Font(font.FontFamily, font.Size - 1F),
                };
                row.Controls.Add(lbl, 0, 0);
                row.Controls.Add(nums[i], 1, 0);
                row.Controls.Add(hint, 0, 1);
                row.SetColumnSpan(hint, 2);

                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
                table.Controls.Add(row, 0, i);
            }

            var path = new Label
            {
                Text = "配置文件：" + AppSettings.DefaultPath,
                Dock = DockStyle.Bottom,
                Height = 20,
                ForeColor = Color.Gray,
            };

            var ok = new Button { Text = "保存", Width = 84, Height = 28, DialogResult = DialogResult.OK, Margin = new Padding(4, 4, 4, 4) };
            var cancel = new Button { Text = "取消", Width = 84, Height = 28, DialogResult = DialogResult.Cancel, Margin = new Padding(4, 4, 4, 0) };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 4, 4, 4),
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            dlg.Controls.Add(table);
            dlg.Controls.Add(path);
            dlg.Controls.Add(buttons);
            dlg.Controls.Add(tip);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            return dlg;
        }

        /// <summary>把对话框里的值写回设置对象（与界面解耦，方便测试）</summary>
        internal static void ReadSettingsDialog(NumericUpDown[] nums, AppSettings s)
        {
            if (nums == null || s == null) return;
            s.BiqugaOfflineWorkers = (int)nums[0].Value;
            s.BiqugaOnlineWorkers = (int)nums[1].Value;
            s.BiqugaPcWorkers = (int)nums[2].Value;
            s.MinDelayMs = (int)nums[3].Value;
            s.MaxDelayMs = Math.Max(s.MinDelayMs, s.MinDelayMs * 2);
            s.RetryPasses = (int)nums[4].Value;
            s.Clamp();
        }

        /// <summary>设置对话框：并发数 / 请求间隔 / 重试轮数（改完立即生效并写盘）</summary>
        private void DoSettings()
        {
            var s = new AppSettings
            {
                BiqugaOfflineWorkers = _settings.BiqugaOfflineWorkers,
                BiqugaOnlineWorkers = _settings.BiqugaOnlineWorkers,
                BiqugaPcWorkers = _settings.BiqugaPcWorkers,
                MinDelayMs = _settings.MinDelayMs,
                MaxDelayMs = _settings.MaxDelayMs,
                CrawlTimeoutMinutes = _settings.CrawlTimeoutMinutes,
                RetryPasses = _settings.RetryPasses,
            };

            NumericUpDown[] nums;
            using (var dlg = BuildSettingsDialog(s, Font, out nums))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ReadSettingsDialog(nums, s);
            }

            _settings = s;
            ApplySettings();
            try
            {
                _settings.Save();
                Log("设置已保存：" + _settings);
            }
            catch (Exception ex)
            {
                Log("设置保存失败（本次仍然生效）：" + ex.Message);
                MessageBox.Show(ex.Message + "\n\n本次修改已经生效，只是没能写进配置文件。",
                    "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool _offlineHinted;

        /// <summary>
        /// 本次载入要从这个书目录的遍历断点续爬（null = 不续）。
        /// 由「刷新目录」按钮在用户选择"继续上次遍历"时设置，用完立刻清掉。
        /// </summary>
        private string _resumeBookDir;
        private DateTime _dlStart;

        /// <summary>
        /// 最近若干章完成的时刻，用来算**实时速率**。
        ///
        /// 为什么不直接用"已用时间 / 已完成章数"：那是全程均值 ——
        /// 站点中途限速（或恢复）时它对当前快慢毫无反应，
        /// 而用户对"现在快不快"的感知比"平均下来多少"直接得多。
        /// 用最近 20 章的滑动窗口，限速一发生数字马上就掉下来。
        /// </summary>
        private readonly Queue<DateTime> _dlRecent = new Queue<DateTime>();
        private const int DlRateWindow = 20;

        /// <summary>
        /// 本次下载实际用的并发数（显示在状态栏）。
        /// 为什么要显示：并发数藏在 settings.ini 和 站点配置.ini **两个地方**，
        /// 用户改完根本不知道到底生效了几路 —— 只能靠猜。
        /// </summary>
        private int _dlWorkersShown;

        /// <summary>把时长格式化成 3分20秒 这样的形式</summary>
        private static string Fmt(TimeSpan t)
        {
            if (t.TotalHours >= 1) return string.Format("{0}小时{1}分", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}分{1}秒", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0:F0}秒", t.TotalSeconds);
        }

        private void DoLoadBookCore(BookInfo item, ISite site)
        {
            var full = site.LoadBook(item, Log);
            _currentBook = full;
            UiInvoke(() =>
            {
                lstChapters.BeginUpdate();
                lstChapters.Items.Clear();
                foreach (var c in full.Chapters)
                {
                    var it = new ListViewItem(c.Title) { Tag = c, Checked = true };
                    it.SubItems.Add(c.Id);
                    if (c.IsVolume)
                    {
                        it.Checked = false;
                        it.ForeColor = Color.FromArgb(0, 90, 160);
                        it.Font = new Font(lstChapters.Font, FontStyle.Bold);
                    }
                    lstChapters.Items.Add(it);
                }
                lstChapters.EndUpdate();
                lblStatus.Text = full.ToString();
                UpdateSelLabel();
            });
            Log("目录已载入：" + full.ToString());

            // 目录刚载入完（尤其离线模式下正文都在内存里），主动问一句，避免用户以为“爬完就没事了”
            bool offlineNow = chkOffline != null && chkOffline.Checked;
            if (lstChapters.Items.Count > 0)
            {
                var ask = MessageBox.Show(
                    string.Format("目录已载入：{0}\n\n" +
                        "⚠ 注意：载入目录≠下载，还要点下载按钮才会生成 txt。\n\n" +
                        "现在要下载全部 {1} 章吗？\n" +
                        "· 点「是」＝ 立即开始下载全部\n" +
                        "· 点「否」＝ 先自己勾选章节，再点右下角「下载选中章节」\n\n{2}",
                        full.ToString(), full.Chapters.Count,
                        offlineNow
                            ? "（离线模式：正文已在内存里，下载会很快，不再联网）"
                            : "（当前没开离线模式：下载时还要联网抓正文，1067 章大约要 30~60 分钟；\n　 想快就先关掉弹窗、勾上「离线模式」再点「刷新目录」）"),
                    "目录已载入 —— 还需要点下载", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes) DoDownload();
            }
        }

        private void SetAllChecks(bool value)
        {
            lstChapters.BeginUpdate();
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c != null && c.IsVolume) { it.Checked = false; continue; }
                it.Checked = value;
            }
            lstChapters.EndUpdate();
            UpdateSelLabel();
        }

        /// <summary>常驻显示“已勾选 N 章”，避免用户以为载入目录就等于下载了</summary>
        private void UpdateSelLabel()
        {
            if (lblSel == null) return;
            int total = 0, sel = 0;
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c == null || c.IsVolume) continue;
                total++;
                if (it.Checked) sel++;
            }
            lblSel.Text = total == 0
                ? "（还没载入目录）"
                : string.Format("共 {0} 章，已勾选 {1} 章 → 点右边按钮才会真正下载", total, sel);
            lblSel.ForeColor = sel > 0 ? Color.FromArgb(0, 100, 0) : Color.DimGray;
            if (btnDownload != null) btnDownload.Enabled = sel > 0 && !_busy;
            if (btnDownloadAll != null) btnDownloadAll.Enabled = total > 0 && !_busy;
        }

        /// <summary>一键下载全部章节（省得先全选再点下载）</summary>
        private void DoDownloadAll()
        {
            if (_busy) { BusyNotice(Text); return; }
            SetAllChecks(true);
            DoDownload();
        }

        private void InvertChecks()
        {
            lstChapters.BeginUpdate();
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c != null && c.IsVolume) { it.Checked = false; continue; }
                it.Checked = !it.Checked;
            }
            lstChapters.EndUpdate();
            UpdateSelLabel();
        }

        // ------------------------------------------------------------ 番茄：调用官方工具

        /// <summary>设置/安装番茄核心：把它复制进本工具目录，实现自包含</summary>
        private void DoConfigureCore()
        {
            var bundled = TomatoCore.BundledExe();
            if (bundled != null)
            {
                var r = MessageBox.Show(
                    "本工具已经自带番茄核心（自包含，不再需要外面的工具目录）：\n" + bundled + "\n\n" +
                    "番茄的下载文件会保存在：\n" + Path.Combine(TomatoCore.BundledDir, "下载") + "\n\n" +
                    "要继续用这个，直接点「否」；想换成别的副本就点「是」。",
                    "番茄核心已就位", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.No) return;
            }

            var current = TomatoCore.GuessDefaultExePath();
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择原版 TomatoNovelDownloader 的 exe（会被复制进本工具目录）";
                dlg.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                try
                {
                    if (!string.IsNullOrEmpty(current))
                    {
                        dlg.InitialDirectory = Path.GetDirectoryName(current);
                        dlg.FileName = Path.GetFileName(current);
                    }
                }
                catch { }
                if (dlg.ShowDialog() != DialogResult.OK) return;

                try
                {
                    var installed = TomatoCore.InstallBundled(dlg.FileName, Log);
                    TomatoCore.SaveExePath(installed);
                    Log("番茄核心已装进本工具目录，之后不再依赖原来的位置。");
                    MessageBox.Show(
                        "番茄核心已装好，本工具现在是自包含的。\n\n" +
                        "核心程序：" + installed + "\n" +
                        "下载输出：" + Path.Combine(TomatoCore.BundledDir, "下载") + "\n\n" +
                        "之后点「用官方工具下载（番茄）」，它会自动启动这个自带核心。",
                        "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    Log("安装番茄核心失败：" + ex.Message);
                    MessageBox.Show("安装失败：" + ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        /// <summary>用原版工具下载当前番茄书籍（拿干净完整正文）</summary>
        private void DoOfficialDownload()
        {
            if (_busy) { BusyNotice(Text); return; }

            // 确定 book_id：优先用已载入的书，其次从输入框解析
            string bookId = null;
            string title = null;
            if (_currentBook != null && _currentBook.Site == "fanqie" && !string.IsNullOrEmpty(_currentBook.BookId))
            {
                bookId = _currentBook.BookId;
                title = _currentBook.Title;
            }
            else
            {
                bookId = FanqieSite.ExtractBookId(txtKeyword.Text);
                if (bookId == null)
                {
                    MessageBox.Show("请先切到「番茄小说」站点，粘贴书籍链接或 book_id，\n" +
                        "或者先载入一本书的目录，再点这个按钮。",
                        "用官方工具下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            var core = new TomatoCore { ExePath = TomatoCore.GuessDefaultExePath() };
            var bid = bookId;
            RunBackground("调用官方工具下载中…", () => RunOfficialDownload(core, bid, title));
        }

        private void RunOfficialDownload(TomatoCore core, string bookId, string knownTitle)
        {
            bool bundled = TomatoCore.BundledExe() != null &&
                           string.Equals(core.ExePath, TomatoCore.BundledExe(), StringComparison.OrdinalIgnoreCase);
            Log(bundled
                ? "使用本工具自带的番茄核心：" + core.ExePath
                : "使用外部番茄工具：" + core.ExePath);

            core.EnsureRunning(Log);

            long jobId = core.SubmitJob(bookId, Log);
            UiInvoke(() => { progress.Minimum = 0; progress.Maximum = 100; progress.Value = 0; });

            var lastText = "";
            bool canceled = false;
            var final = core.WaitJob(jobId, st =>
            {
                var text = string.Format("官方工具进度：{0}/{1} 章", st.SavedChapters, st.TotalChapters);
                if (text != lastText)
                {
                    lastText = text;
                    UiInvoke(() =>
                    {
                        lblStatus.Text = text + (string.IsNullOrEmpty(st.Title) ? "" : "  《" + st.Title + "》");
                        if (st.TotalChapters > 0)
                        {
                            progress.Maximum = st.TotalChapters;
                            progress.Value = Math.Min(st.SavedChapters, st.TotalChapters);
                        }
                    });
                }
            }, ref canceled);

            var title = !string.IsNullOrEmpty(final.Title) ? final.Title : knownTitle;
            Log(string.Format("任务 #{0} 状态：{1}　成功 {2}/{3} 章",
                jobId, final.State, final.SavedChapters, final.TotalChapters));
            if (!string.IsNullOrEmpty(final.Message)) Log("官方工具消息：" + final.Message);

            var saveDir = core.GetSaveDir();
            var file = core.FindOutputFile(title, saveDir);

            if (final.State == "done")
            {
                Log("=== 官方工具下载完成 ===");
                if (!string.IsNullOrEmpty(file))
                {
                    var fi = new FileInfo(file);
                    Log(string.Format("书名：{0}　作者：{1}", title, final.Author));
                    Log(string.Format("大小：{0:N0} 字节（{1:N1} MB），修改时间 {2}",
                        fi.Length, fi.Length / 1048576.0, fi.LastWriteTime));
                    Log(string.Format("章节：成功 {0}/{1} 章", final.SavedChapters, final.TotalChapters));
                    Log("保存位置：" + file);
                    _lastOfficialFile = file;
                    Log("（「打开保存目录」按钮现在会打开这个官方工具的目录）");
                }
                else
                {
                    Log("已完成，但没在 " + saveDir + " 里找到输出文件，请手动查看该目录。");
                    if (!string.IsNullOrEmpty(saveDir)) _lastOfficialFile = Path.Combine(saveDir, ".");
                }
            }
            else
            {
                Log("官方工具任务没有正常完成（状态：" + final.State + "）。");
                if (!string.IsNullOrEmpty(file)) Log("目录下最新文件：" + file);
            }

            core.ShutdownIfOwned();
        }

        /// <summary>
        /// <summary>
        /// 建一个**自动宽度**的按钮：宽度由文字实测宽度决定。
        ///
        /// ★ 为什么必须 AutoSize，不能写死 Width：
        ///   之前这里是 `Width = width` + `AutoEllipsis = true`，
        ///   结果在**中文系统 + 高 DPI / 非 100% 缩放**下（WinForms 的
        ///   AutoScaleMode.Font 会把控件按字体缩放，但那个写死的 width 是设计值），
        ///   实测宽度不够，按钮文字被压成「搜…」「载入…」「设…」——
        ///   用户完全看不懂按钮是干什么的。截图见 issue 反馈。
        ///
        ///   AutoSize 让按钮自己按**实际字体**量文字，所以换 DPI、换系统字体、
        ///   换语言都不会被切字。原有的 width 参数保留只是为了不改所有调用点，
        ///   现在只当"最小宽度"用（有些按钮太窄不好点，比如「全选」）。
        /// </summary>
        private Button MakeButton(string text, int width, Action<object, EventArgs> onClick, int rightMargin = 4)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                // GrowAndShrink：AutoSize 只负责"撑到够放文字"，不会把按钮拉到别的尺寸
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(width, 26),
                Height = 26,
                Margin = new Padding(0, 0, rightMargin, 0),
                // 关掉省略号：AutoSize 之后文字一定放得下，再开着它反而会在
                // 极端缩放下"有省略号但看不出来被截断"，掩盖真正的布局问题。
                AutoEllipsis = false,
            };
            if (onClick != null) b.Click += (s, e) => onClick(s, e);
            return b;
        }

        /// <summary>
        /// 窗口尺寸变化时的那点弹性活儿：
        ///  · 关键词框吃掉第 1 行的剩余宽度（窗口拉宽不留空白，窗口变窄自动缩）
        ///  · 状态文字后面的空白区跟着窗口走（它用 Fill，不用管，但仍要保证不压按钮）
        /// </summary>
        private void LayoutInlineTweaks()
        {
            if (_kwHost == null || txtKeyword == null) return;
            var row = _kwHost.Parent as FlowLayoutPanel;
            if (row == null || row.ClientSize.Width <= 0) return;

            int used = 0;
            foreach (Control c in row.Controls)
            {
                if (ReferenceEquals(c, _kwHost)) continue;
                used += c.Width + c.Margin.Left + c.Margin.Right;
            }
            int avail = row.ClientSize.Width - row.Padding.Left - row.Padding.Right - used
                        - _kwHost.Margin.Left - _kwHost.Margin.Right;
            int want = Math.Max(150, avail);
            if (_kwHost.Width != want) _kwHost.Width = want;
        }

        /// <summary>正忙时点按钮：明确告诉用户，别静默吞掉</summary>
        private void BusyNotice(string action)
        {
            lblStatus.Text = "正在执行上一个任务…";
            MessageBox.Show("现在正在执行上一个任务（" + action + "），请等它结束再操作。\n\n" +
                "任务进度看下面的进度条和状态行；日志区会滚出每一章的明细。",
                "请稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoDownload()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            int totalRows = 0, volumeRows = 0;
            var picked = new List<ChapterInfo>();
            foreach (ListViewItem it in lstChapters.Items)
            {
                var c = it.Tag as ChapterInfo;
                if (c == null) continue;
                totalRows++;
                if (c.IsVolume) { volumeRows++; continue; }
                if (it.Checked) picked.Add(c);
            }
            Log(string.Format("选中情况：列表共 {0} 项（其中分卷标题 {1} 项），勾选 {2} 章", totalRows, volumeRows, picked.Count));

            if (picked.Count == 0)
            {
                MessageBox.Show("请先勾选要下载的章节。\n\n" +
                    string.Format("（当前列表 {0} 项，勾选 0 章）\n", totalRows) +
                    "如果列表本来就是空的，说明目录还没载入成功，请先点「载入目录」。",
                    "没有选中章节", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var book = _currentBook;
            // ★ 下载也用**书自己所属的站点**（跨站搜来的书可能不是当前选中的站）。
            //   和 DoLoadBook 同一个理由：站点规则不能张冠李戴。
            var site = SiteForBook(book);
            var root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            var chapters = picked;

            RunBackground("下载中…", () => DownloadCore(site, book, chapters, root));
        }

        /// <summary>
        /// 界面「开始下载选中章节」的真正执行体（不含 UI 弹窗部分），
        /// 单独抽出来是为了让自动化测试能跑**和界面完全一样**的代码路径。
        /// </summary>
        internal void DownloadCore(ISite site, BookInfo book, List<ChapterInfo> chapters, string root)
        {
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);
            Log("准备写入：" + txtPath);
            Log("（如果这个路径不是你想要的，请用上面的「浏览…」改「保存到」）");

            // 磁盘空间预检：磁盘写满是在 **append 落盘的途中**才暴露的，
            // 那时已经下了大半本，用户只看到一句莫名其妙的 IO 异常。
            // 提前算一下、提前说，成本几乎为零。查不出来（网络盘/权限）时一律放行。
            try
            {
                int todo = 0;
                foreach (var c in chapters) if (c != null && !c.IsVolume) todo++;
                var est = DiskCheck.Check(root, DiskCheck.EstimateBytes(todo, true));
                if (!est.Ok)
                {
                    Log("⚠ " + est.Message.Replace("\n\n", " ").Replace("\n", " "));
                    if (!SuppressDialogs)
                    {
                        var r = MessageBox.Show(this, est.Message + "\n\n仍要继续下载吗？",
                            "磁盘空间检查", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (r != DialogResult.Yes)
                        {
                            Log("用户因磁盘空间不足取消了下载。");
                            return;
                        }
                    }
                }
                else if (est.FreeBytes > 0)
                {
                    Log(string.Format("磁盘空间检查：预计需要约 {0}，目标盘剩余 {1}", est.HumanNeeded, est.HumanFree));
                }
            }
            catch (Exception ex) { Log("磁盘空间检查跳过（" + ex.Message + "）"); }

            var runner = new DownloadRunner
            {
                Site = site,
                Book = book,
                Chapters = chapters,
                RootDir = root,
                Log = Log,
                RetryPasses = _settings.RetryPasses,
                Workers = _dlWorkersShown = DownloadWorkersFor(site),
                OutputEncoding = CurrentFileEncoding(),
                OutputTraditional = _settings.OutputTraditional,
                IsCanceled = () => _cancel,
                OnProgress = (done, total) =>
                {
                        // 记录开始时间用于估算剩余时间
                        if (done == 0) { _dlStart = DateTime.Now; _dlRecent.Clear(); }
                        UiInvoke(() =>
                        {                            progress.Minimum = 0;
                            progress.Maximum = total;
                            progress.Value = Math.Min(done, total);
                            if (done == 0 || _dlStart == default(DateTime))
                            {
                                lblStatus.Text = string.Format("开始下载 0/{0} 章…", total);
                                return;
                            }

                            var used = DateTime.Now - _dlStart;

                            // 实时速率：最近 DlRateWindow 章的滑动窗口
                            _dlRecent.Enqueue(DateTime.Now);
                            while (_dlRecent.Count > DlRateWindow) _dlRecent.Dequeue();
                            string rate = "";
                            if (_dlRecent.Count >= 3)
                            {
                                var span = (DateTime.Now - _dlRecent.Peek()).TotalSeconds;
                                if (span > 0.5)
                                {
                                    double perSec = (_dlRecent.Count - 1) / span;
                                    rate = string.Format("，当前 {0:F2} 章/秒", perSec);
                                }
                            }

                            // ETA 仍用全程均值：它更稳，不会因为刚下的几章快慢而乱跳
                            string eta = "";
                            if (done > 0 && done < total)
                            {
                                var per = used.TotalSeconds / done;
                                var left = TimeSpan.FromSeconds(per * (total - done));
                                eta = string.Format("，预计还需 {0}", Fmt(left));
                            }
                            lblStatus.Text = string.Format("下载中 {0}/{1} 章　已用 {2}{3}{4}　（并发 {5}）",
                                done, total, Fmt(used), rate, eta, _dlWorkersShown);
                        });
                },
            };
            runner.Run();

            UiInvoke(() =>
            {
                lblStatus.Text = string.Format("下载结束：成功 {0}，跳过 {1}，失败 {2}",
                    runner.Ok, runner.Skipped, runner.Failed);
            });
            Log("=== 下载结束 ===");
            Log(string.Format("成功 {0} 章，跳过 {1} 章（站点公告/空内容），失败 {2} 章",
                runner.Ok, runner.Skipped, runner.Failed));
            Log("保存位置：" + runner.OutputFile);
            if (runner.Failed > 0) Log("失败的章节可重新勾选后再下一次（已下载内容会覆盖为完整版本）。");
            if (!string.IsNullOrEmpty(runner.ReportFile)) Log("缺失章节明细：" + runner.ReportFile);
            try
            {
                var fi = new FileInfo(runner.OutputFile);
                Log(string.Format("文件大小：{0:N0} 字节，修改时间 {1}", fi.Length, fi.LastWriteTime));

                // 明确弹窗告知，避免“以为没生成”
                if (!SuppressDialogs)
                {
                    var miss = new StringBuilder();
                    if (runner.FailedChapters.Count > 0)
                    {
                        miss.AppendLine();
                        miss.AppendLine(string.Format("⚠ 有 {0} 章失败（网络/风控）：", runner.FailedChapters.Count));
                        for (int i = 0; i < runner.FailedChapters.Count && i < 5; i++)
                            miss.AppendLine("   · 第 " + runner.FailedChapters[i].Order + " 章 " + runner.FailedChapters[i].Title);
                        if (runner.FailedChapters.Count > 5) miss.AppendLine("   · …");
                        miss.AppendLine("   已自动重试过，还是失败；可重新勾选这几章再下一次。");
                    }
                    if (runner.SkippedChapters.Count > 0)
                    {
                        miss.AppendLine();
                        miss.AppendLine(string.Format("ℹ 有 {0} 章被跳过：站点这一章本身就是空的（“正在手打中，请稍等片刻”），",
                            runner.SkippedChapters.Count));
                        miss.AppendLine("   不是工具的 bug，换源也一样没有内容。");
                    }
                    if (!string.IsNullOrEmpty(runner.ReportFile))
                        miss.AppendLine().AppendLine("缺哪些章看这个文件：").AppendLine(runner.ReportFile);

                    MessageBox.Show(
                        string.Format("下载完成！\n\n成功 {0} 章，跳过 {1} 章，失败 {2} 章\n" +
                                      "文件大小：{3:N0} 字节（{4:N2} MB）\n\n文件位置：\n{5}\n{6}\n" +
                                      "点「打开保存目录」可直接打开所在文件夹。",
                            runner.Ok, runner.Skipped, runner.Failed, fi.Length, fi.Length / 1048576.0,
                            runner.OutputFile, miss.ToString()),
                        "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { Log("读取文件信息失败：" + ex.Message); }
        }

        /// <summary>自动化测试时置 true，避免弹窗卡住测试</summary>
        internal bool SuppressDialogs { get; set; }

        /// <summary>
        /// "构造出来就默认不弹窗、不联网"。
        ///
        /// ★ 为什么需要：离线测试要 `new MainForm()` 来量布局/结果栏，而
        ///   `SuppressDialogs` 是**构造完**才赋值的 —— 但构造函数末尾已经在
        ///   `BeginInvoke(ProbeSitesAsync)`：
        ///     ① 于是"离线测试"实际会真的去探活所有站点（慢、吃内存、不离线）；
        ///     ② 探活失败还可能弹窗，测试就挂在那儿。
        ///   这个静态标志在**构造之前**由测试套件置位，构造函数第一行读它。
        /// </summary>
        internal static bool SuppressByDefault;

        // ------------------------------------------------------------ 增量更新 / EPUB 导出

        /// <summary>
        /// 「更新已下载的书」：检查站点上这本书有没有新章节，只把新的补上去。
        /// 判断依据是文件表头里的进度标记（累计章数 + 前 N 章 id 指纹）。
        /// </summary>
        private void DoUpdateExisting()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            var book = _currentBook;
            string root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");

            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);
            if (!File.Exists(txtPath))
            {
                MessageBox.Show("这本书还没有下载过（找不到文件）：\n" + txtPath +
                    "\n\n请先点「下载全部章节」。", "更新已下载的书",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int done; string fp; int bodyBytes;
            if (!DownloadRunner.TryReadProgress(txtPath, out done, out fp, out bodyBytes))
            {
                MessageBox.Show("这个文件是旧版本下载的，里面没有进度标记，没法安全判断『哪些章是新的』。\n\n" +
                    "没有标记就只能猜，猜错会把正文顺序弄乱 —— 所以宁可不做。\n\n" +
                    "想用增量更新：先点「下载全部章节」重新下一本（会写成带标记的新格式）。",
                    "更新已下载的书", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 指纹校验：前 done 章的 id 若已变化（站点改了目录/换了源），追加会让正文错位
            var idsNow = new StringBuilder();
            for (int i = 0; i < done && i < book.Chapters.Count; i++)
                idsNow.Append(book.Chapters[i].Id).Append('|');
            if (DownloadRunner.Fingerprint(idsNow.ToString()) != fp)
            {
                MessageBox.Show("这本书的目录已经变了（站点可能改了章节顺序或换了内容源），\n" +
                    "直接追加新章节会让正文顺序错乱。\n\n" +
                    "建议点「刷新目录」，再点「下载全部章节」重新下一遍。",
                    "目录已变化，不能安全追加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (book.Chapters.Count <= done)
            {
                MessageBox.Show(string.Format("已经是最新的了：文件里累计 {0} 章，站点上也是 {1} 章。\n\n{2}",
                    done, book.Chapters.Count, txtPath), "更新已下载的书",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var fresh = new List<ChapterInfo>();
            for (int i = done; i < book.Chapters.Count; i++)
            {
                var c = book.Chapters[i];
                if (c == null || c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                fresh.Add(c);
            }
            if (fresh.Count == 0)
            {
                MessageBox.Show(string.Format("站点上多了 {0} 个条目，但都是分卷标题，没有新章节。",
                    book.Chapters.Count - done), "更新已下载的书",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log(string.Format("=== 增量更新：《{0}》 已有 {1} 章，发现 {2} 章新章节 ===", book.Title, done, fresh.Count));
            Log("新章节会追加到文件末尾，已有正文不会被改动。");

            var site = _site;
            var startCumulative = done;
            RunBackground("更新中…", () =>
            {
                var extra = new DownloadRunner
                {
                    Site = site,
                    Book = book,
                    Chapters = fresh,
                    RootDir = root,
                    Log = Log,
                    RetryPasses = _settings.RetryPasses,
                    Workers = DownloadWorkersFor(site),
                    AppendToExistingFile = true,
                    CumulativeOkCount = startCumulative,
                    OutputTraditional = _settings.OutputTraditional,
                    IsCanceled = () => _cancel,
                    OnProgress = (n, total) => UiInvoke(() =>
                    {
                        progress.Minimum = 0;
                        progress.Maximum = total;
                        progress.Value = Math.Min(n, total);
                        lblStatus.Text = string.Format("更新中 {0}/{1} 章（新章节）…", n, total);
                    }),
                };
                extra.Run();
                UiInvoke(() => lblStatus.Text = string.Format("更新结束：新增 {0} 章，累计 {1} 章",
                    extra.Ok, startCumulative + extra.Ok));
                ReportDownloadFinished(extra);
            });
        }

        /// <summary>
        /// 「导出 EPUB」：把这本书**已经下载到本地**的正文导出成 epub（不联网）。
        /// 优先用内存里的正文；没有就按章节标题从已生成的 txt 里解析回来。
        /// </summary>
        private void DoExportEpub()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            var book = _currentBook;
            string root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);

            var chapters = new List<ChapterInfo>();
            foreach (var c in book.Chapters)
            {
                if (c == null || c.IsVolume) continue;
                if (!string.IsNullOrEmpty(c.Text)) chapters.Add(c);
            }

            if (chapters.Count == 0)
            {
                if (!File.Exists(txtPath))
                {
                    MessageBox.Show("这本书还没有下载过，也没有可导出的正文。\n\n请先点「下载全部章节」。",
                        "导出 EPUB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                chapters = ParseTxtIntoChapters(txtPath, book);
                Log(string.Format("从 txt 解析出 {0} 章正文用于导出。", chapters.Count));
            }

            if (chapters.Count == 0)
            {
                MessageBox.Show("没能从文件里解析出正文（文件可能是空的）。", "导出 EPUB",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var epubPath = Path.Combine(bookDir, Http.SafeFileName(book.Title) + ".epub");
            try
            {
                Cursor = Cursors.WaitCursor;

                // 封面：优先用书目录里已经抓好的图（下载阶段落的），
                // 没有就趁现在有网补抓一次。两条路都失败也只是没封面，不影响导出。
                // 注意 C# 5：不能写 out var，必须先把变量声明出来。
                string coverExt;
                var coverBytes = CoverFetcher.Read(bookDir, out coverExt);
                if (coverBytes == null && !string.IsNullOrEmpty(book.CoverUrl))
                {
                    var p = CoverFetcher.Ensure(book.CoverUrl, bookDir, book.Url, Log);
                    if (p != null) coverBytes = CoverFetcher.Read(bookDir, out coverExt);
                }

                var exportChapters = ChaptersForExport(chapters);
                EpubWriter.Write(epubPath, book, exportChapters, coverBytes, coverExt, VolumeTitlesFor(book, exportChapters));
                var size = new FileInfo(epubPath).Length;
                Log(string.Format("EPUB 已生成：{0}（{1:N0} 字节，{2} 章{3}）", epubPath, size, chapters.Count,
                    coverBytes != null ? "，含封面" : "，无封面"));
                if (!SuppressDialogs)
                    MessageBox.Show(string.Format("EPUB 导出完成！\n\n章节数：{0}\n文件大小：{1:N0} 字节（{2:N2} MB）\n封面：{3}\n\n文件位置：\n{4}",
                        chapters.Count, size, size / 1048576.0,
                        coverBytes != null ? "有" : "无（站点没提供或抓取失败）", epubPath), "导出 EPUB",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("导出 EPUB 失败：" + ex.Message);
                if (!SuppressDialogs)
                    MessageBox.Show("导出失败：" + ex.Message, "导出 EPUB",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        /// <summary>
        /// 算出导出用的「卷标题」列表：与 chapters **一一对应**，
        /// 第 i 项 = 第 i 章所属的卷名（null = 不属于任何卷）。
        ///
        /// 为什么要从 book.Chapters 反推：导出时传进来的 chapters 可能只包含
        /// 「已经下载了正文的章」，分卷行（IsVolume）早被过滤掉了；
        /// 而 vol 标记本身是加在**分卷行自己**身上的，所以必须回原始目录去找
        /// 「这一章之前最近的那个分卷行是哪一卷」。
        ///
        /// 匹配方式：按 Id 建索引（Id 是站点给的稳定标识，比标题可靠）。
        /// 找不到对应关系（比如是从 txt 反向解析出来的、Id 是空的）就返回全 null，
        /// 效果等于不分卷 —— 宁可目录平一点，也不要按错误的卷去分组。
        /// </summary>
        private static List<string> VolumeTitlesFor(BookInfo book, List<ChapterInfo> chapters)
        {
            if (book == null || book.Chapters == null || chapters == null) return null;

            // 第一遍：原始目录里，每章 → 它属于哪一卷。
            // 同时按标题建一份索引当兜底：从 txt 反向解析出来的章节有可能拿不到 Id
            // （旧文件、手工拼的文件），那时只能靠标题认。
            var owner = new Dictionary<string, string>();
            var ownerByTitle = new Dictionary<string, string>();
            string current = null;
            bool anyVolume = false;
            foreach (var c in book.Chapters)
            {
                if (c == null) continue;
                if (c.IsVolume) { current = c.Title; anyVolume = true; continue; }
                if (current == null) continue;
                if (!string.IsNullOrEmpty(c.Id) && !owner.ContainsKey(c.Id)) owner[c.Id] = current;
                var t = (c.Title ?? "").Trim();
                if (t.Length > 0 && !ownerByTitle.ContainsKey(t)) ownerByTitle[t] = current;
            }
            if (!anyVolume) return null;

            // 第二遍：按传进来的 chapters 顺序取
            var result = new List<string>(chapters.Count);
            foreach (var c in chapters)
            {
                string v = null;
                if (c != null)
                {
                    if (!string.IsNullOrEmpty(c.Id)) owner.TryGetValue(c.Id, out v);
                    if (v == null && !string.IsNullOrEmpty(c.Title))
                        ownerByTitle.TryGetValue(c.Title.Trim(), out v);
                }
                result.Add(v);
            }
            return result;
        }

        /// <summary>
        /// 把已生成的 txt 反向解析成"章节 + 正文"（导出 EPUB 用）。
        /// 用书里已有的章节标题做锚点，比"猜分隔符"可靠；认不出来就返回空列表
        /// （宁可少导出，也不要导出一堆错乱内容）。
        /// </summary>
        /// <summary>
        /// 「补齐缺章」：检查文件里缺哪些章（站点空内容 / 上次中断），只重抓那几章，
        /// 按章节顺序插回正确位置。已有正文一个字都不动。
        /// </summary>
        private void DoFillMissing()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            var book = _currentBook;
            string root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);

            if (!File.Exists(txtPath))
            {
                MessageBox.Show("这本书还没有下载过（找不到文件）：\n" + txtPath +
                    "\n\n请先点「下载全部」。", "补齐缺章", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ChapterIndex.HasAnchors(txtPath))
            {
                MessageBox.Show(
                    "这个文件是 v1.0.4 之前下载的，正文里没有章节锚点，没法定位到具体某一章。\n\n" +
                    "（没有锚点就只能靠标题字符串猜位置，猜错会把正文插到错误的地方 —— 所以宁可不做。）\n\n" +
                    "想用补齐功能：请点「下载全部」重新下载一本（会写成带锚点的新格式）。",
                    "补齐缺章", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var site = _site;
            RunBackground("检查缺章…", () =>
            {
                var runner = new DownloadRunner
                {
                    Site = site,
                    Book = book,
                    Chapters = new List<ChapterInfo>(),
                    RootDir = root,
                    Log = Log,
                    Workers = DownloadWorkersFor(site),
                    OutputEncoding = CurrentFileEncoding(),
                    OutputTraditional = _settings.OutputTraditional,
                    OnProgress = (n, total) => UiInvoke(() =>
                    {
                        progress.Minimum = 0;
                        progress.Maximum = Math.Max(1, total);
                        progress.Value = Math.Min(n, total);
                        lblStatus.Text = string.Format("补齐缺章 {0}/{1}…", n, total);
                    }),
                };
                DownloadRunner.FillMissing(runner, txtPath, Log);
                UiInvoke(() => lblStatus.Text = string.Format("补齐结束：补上 {0} 章", runner.Ok));
                if (runner.Ok > 0) ReportDownloadFinished(runner);
                else if (!SuppressDialogs)
                    MessageBox.Show("检查完毕：这个文件没有缺章，不需要补。", "补齐缺章",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        /// <summary>「导出 Markdown」：把已下载的正文导出成 md（带 YAML 头 + 目录锚点）。</summary>
        /// <summary>
        /// 按「输出繁体」勾选状态准备要导出的章节。
        ///
        /// 为什么要这么做：勾选框原来只在**下载时**生效（DownloadRunner 写盘前转换），
        /// 所以对**已经下载好的书**它是死的 —— 用户勾上再导出，出来的还是简体，
        /// 想换繁体只能重新下一遍。现在在导出路径上也转一次：
        /// 勾上就转换后导出，不勾就按文件里本来的样子导出。
        ///
        /// 幂等安全性由 ZhConvert 保证（表里没有的字原样透传），
        /// 所以对"文件里本来就是繁体"的情况再转一次也不会坏。
        /// 返回的是**副本**，不动内存里的原正文。
        /// </summary>
        private List<ChapterInfo> ChaptersForExport(List<ChapterInfo> chapters)
        {
            if (chapters == null) return null;
            if (chkTraditional == null || !chkTraditional.Checked) return chapters;

            var list = new List<ChapterInfo>(chapters.Count);
            foreach (var c in chapters)
            {
                if (c == null) continue;
                list.Add(new ChapterInfo
                {
                    Id = c.Id,
                    Order = c.Order,
                    IsVolume = c.IsVolume,
                    Selected = c.Selected,
                    Title = ZhConvert.ToTraditional(c.Title ?? ""),
                    Text = ZhConvert.ToTraditional(c.Text ?? ""),
                });
            }
            Log("已按「输出繁体」转换 " + list.Count + " 章（转换后导出）。");
            return list;
        }

        /// <summary>「导出 Markdown」：把已下载的正文导出成 md（带 YAML 头 + 目录锚点）。</summary>
        /// <summary>
        /// 「导出纯文本 TXT」：另存一份**不含内部标记**的干净小说文本。
        ///
        /// 为什么不直接给下载得到的那本：它的表头占位（#header-zone）和章节锚点
        /// （&lt;!--c:id--&gt;）是"更新新章节 / 补齐缺章 / 续传"的必需品，删了就静默失效。
        /// 而用户阅读时那些是噪音 —— 所以"另存一份"。
        ///
        /// 取数逻辑与 Markdown 导出一致（内存优先，没有就从已下载的 txt 反解），
        /// 这样"能看到什么"和"导出的内容"永远对得上。
        /// </summary>
        private void DoExportPlainText()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            var book = _currentBook;
            string root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);

            var chapters = new List<ChapterInfo>();
            foreach (var c in book.Chapters)
            {
                if (c == null || c.IsVolume) continue;
                if (!string.IsNullOrEmpty(c.Text)) chapters.Add(c);
            }
            if (chapters.Count == 0)
            {
                if (!File.Exists(txtPath))
                {
                    MessageBox.Show("这本书还没有下载过，也没有可导出的正文。\n\n请先点「下载全部」。",
                        "导出纯文本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                chapters = ParseTxtIntoChapters(txtPath, book);
                Log(string.Format("从 txt 解析出 {0} 章正文用于导出。", chapters.Count));
            }
            if (chapters.Count == 0)
            {
                MessageBox.Show("没能从文件里解析出正文（文件可能是空的）。", "导出纯文本",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string outPath = Path.Combine(bookDir, Http.SafeFileName(book.Title) + ".纯文本.txt");
            try
            {
                Cursor = Cursors.WaitCursor;
                var exportChapters = ChaptersForExport(chapters);
                int n = PlainTextWriter.Write(outPath, book, exportChapters, VolumeTitlesFor(book, exportChapters));
                var size = new FileInfo(outPath).Length;
                Log(string.Format("纯文本已生成：{0}（{1:N0} 字节，{2} 章，无内部标记）", outPath, size, n));
                if (!SuppressDialogs)
                    MessageBox.Show(string.Format(
                        "纯文本导出完成！\n\n章节数：{0}\n文件大小：{1:N0} 字节\n\n文件位置：\n{2}\n\n" +
                        "（这是一份干净副本：没有表头占位和章节锚点，可以直接丢进阅读器。\n" +
                        "原 txt 没动 —— 更新新章节、补齐缺章还要靠它。）",
                        n, size, outPath), "导出纯文本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("导出纯文本失败：" + ex.Message);
                if (!SuppressDialogs)
                    MessageBox.Show("导出失败：" + ex.Message, "导出纯文本",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void DoExportMarkdown()
        {
            if (_busy) { BusyNotice(Text); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            var book = _currentBook;
            string root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);

            var chapters = new List<ChapterInfo>();
            foreach (var c in book.Chapters)
            {
                if (c == null || c.IsVolume) continue;
                if (!string.IsNullOrEmpty(c.Text)) chapters.Add(c);
            }
            if (chapters.Count == 0)
            {
                if (!File.Exists(txtPath))
                {
                    MessageBox.Show("这本书还没有下载过，也没有可导出的正文。\n\n请先点「下载全部」。",
                        "导出 Markdown", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                chapters = ParseTxtIntoChapters(txtPath, book);
                Log(string.Format("从 txt 解析出 {0} 章正文用于导出。", chapters.Count));
            }
            if (chapters.Count == 0)
            {
                MessageBox.Show("没能从文件里解析出正文（文件可能是空的）。", "导出 Markdown",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var mdPath = Path.Combine(bookDir, Http.SafeFileName(book.Title) + ".md");
            try
            {
                Cursor = Cursors.WaitCursor;
                var exportChapters = ChaptersForExport(chapters);
                MarkdownWriter.Write(mdPath, book, exportChapters, true, VolumeTitlesFor(book, exportChapters));
                var size = new FileInfo(mdPath).Length;
                Log(string.Format("Markdown 已生成：{0}（{1:N0} 字节，{2} 章）", mdPath, size, chapters.Count));
                if (!SuppressDialogs)
                    MessageBox.Show(string.Format("Markdown 导出完成！\n\n章节数：{0}\n文件大小：{1:N0} 字节\n\n文件位置：\n{2}",
                        chapters.Count, size, mdPath), "导出 Markdown",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("导出 Markdown 失败：" + ex.Message);
                if (!SuppressDialogs)
                    MessageBox.Show("导出失败：" + ex.Message, "导出 Markdown",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }
        internal static List<ChapterInfo> ParseTxtIntoChapters(string txtPath, BookInfo book)
        {
            var hits = new List<ChapterInfo>();
            var positions = new List<int>();
            try
            {
                var text = File.ReadAllText(txtPath, Encoding.UTF8);
                var order = new List<ChapterInfo>();
                foreach (var c in book.Chapters)
                    if (c != null && !c.IsVolume && !string.IsNullOrEmpty(c.Id)) order.Add(c);
                if (order.Count == 0) return hits;

                int searchFrom = 0;
                foreach (var c in order)
                {
                    string title = c.Title == null ? "" : c.Title.Trim();
                    if (title.Length == 0) continue;
                    int at = text.IndexOf("\n" + title + "\n", searchFrom, StringComparison.Ordinal);
                    if (at < 0) at = text.IndexOf(title, searchFrom, StringComparison.Ordinal);
                    if (at < 0) continue;
                    hits.Add(c);
                    positions.Add(at);
                    searchFrom = at + 1;
                }
                if (hits.Count == 0) return hits;

                var result = new List<ChapterInfo>();
                for (int i = 0; i < hits.Count; i++)
                {
                    int start = positions[i];
                    int end = (i + 1 < positions.Count) ? positions[i + 1] : text.Length;
                    var lines = text.Substring(start, end - start).Split('\n');
                    var body = new StringBuilder();
                    for (int li = 0; li < lines.Length; li++)
                    {
                        var line = lines[li].TrimEnd('\r').Trim();
                        if (line.Length == 0) continue;
                        if (li <= 2) continue;                                        // 标题行 + 分隔线
                        if (line.Length >= 6 && line.Trim('-').Length == 0) continue; // 破折号分隔线
                        body.AppendLine(line);
                    }
                    var bodyText = body.ToString().TrimEnd();
                    if (bodyText.Length == 0) continue;
                    result.Add(new ChapterInfo
                    {
                        Id = hits[i].Id,
                        Title = hits[i].Title,
                        Order = hits[i].Order,
                        Text = bodyText,
                    });
                }
                return result;
            }
            catch { return new List<ChapterInfo>(); }
        }

        /// <summary>
        /// 下载/更新结束后的统一收尾：打印统计、写缺失报告说明、弹窗告知文件位置。
        /// 普通下载和增量更新共用，避免两处报告写得不一致。
        /// </summary>
        internal void ReportDownloadFinished(DownloadRunner runner)
        {
            Log("=== 下载结束 ===");
            Log(string.Format("成功 {0} 章，跳过 {1} 章（站点公告/空内容），失败 {2} 章",
                runner.Ok, runner.Skipped, runner.Failed));
            Log("保存位置：" + runner.OutputFile);
            if (runner.Failed > 0) Log("失败的章节可重新勾选后再下一次（已下载内容会覆盖为完整版本）。");
            if (!string.IsNullOrEmpty(runner.ReportFile)) Log("缺失章节明细：" + runner.ReportFile);

            // 记进书架：下次可以直接在这里一键追更，不用重新搜书名。
            // 放在这里（而不是每个下载入口）是因为所有下载路径最后都会走到这个方法。
            if (runner.Book != null && !string.IsNullOrEmpty(runner.OutputFile))
                RememberInShelf(runner.Book, runner.OutputFile, true);
            try
            {
                var fi = new FileInfo(runner.OutputFile);
                Log(string.Format("文件大小：{0:N0} 字节，修改时间 {1}", fi.Length, fi.LastWriteTime));
                if (SuppressDialogs) return;

                var miss = new StringBuilder();
                if (runner.FailedChapters.Count > 0)
                {
                    miss.AppendLine();
                    miss.AppendLine(string.Format("⚠ 有 {0} 章失败（网络/风控）：", runner.FailedChapters.Count));
                    for (int i = 0; i < runner.FailedChapters.Count && i < 5; i++)
                        miss.AppendLine("   · 第 " + runner.FailedChapters[i].Order + " 章 " + runner.FailedChapters[i].Title);
                    if (runner.FailedChapters.Count > 5) miss.AppendLine("   · …");
                    miss.AppendLine("   已自动重试过，还是失败；可重新勾选这几章再下一次。");
                }
                if (runner.SkippedChapters.Count > 0)
                {
                    miss.AppendLine();
                    miss.AppendLine(string.Format("ℹ 有 {0} 章被跳过：站点这一章本身就是空的（“正在手打中，请稍等片刻”），",
                        runner.SkippedChapters.Count));
                    miss.AppendLine("   不是工具的 bug，换源也一样没有内容。");
                }
                if (!string.IsNullOrEmpty(runner.ReportFile))
                    miss.AppendLine().AppendLine("缺哪些章看这个文件：").AppendLine(runner.ReportFile);

                MessageBox.Show(
                    string.Format("下载完成！\n\n成功 {0} 章，跳过 {1} 章，失败 {2} 章\n" +
                                  "文件大小：{3:N0} 字节（{4:N2} MB）\n\n文件位置：\n{5}\n{6}\n" +
                                  "点「打开保存目录」可直接打开所在文件夹。",
                        runner.Ok, runner.Skipped, runner.Failed, fi.Length, fi.Length / 1048576.0,
                        runner.OutputFile, miss.ToString()),
                    "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("读取文件信息失败：" + ex.Message); }
        }
        private void OpenFolder()
        {
            // 刚用官方工具下过番茄小说，就打开它的目录（书存在那边）
            if (!string.IsNullOrEmpty(_lastOfficialFile))
            {
                try
                {
                    var dir = File.Exists(_lastOfficialFile)
                        ? Path.GetDirectoryName(_lastOfficialFile)
                        : _lastOfficialFile;
                    if (Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                        Log("已打开官方工具的保存目录：" + dir);
                        return;
                    }
                }
                catch (Exception ex) { Log("打开官方工具目录失败：" + ex.Message); }
            }

            var root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            try
            {
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + root + "\"");
            }
            catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
        }

        // ------------------------------------------------------------ 线程/Lo g

        private void RunBackground(string status, Action work)
        {
            if (_busy) { BusyNotice("上一个任务"); return; }
            _busy = true;
            _cancel = false;
            btnSearch.Enabled = btnLoad.Enabled = btnReload.Enabled = btnDownload.Enabled = false;
            if (btnDownloadAll != null) btnDownloadAll.Enabled = false;
            if (btnOfficial != null) btnOfficial.Enabled = false;
            btnCancel.Enabled = true;
            lblStatus.Text = status;
            var t = new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log("出错：" + ex.Message);
                    UiInvoke(() => MessageBox.Show(ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                }
                finally
                {
                    UiInvoke(() =>
                    {
                        _busy = false;
                        btnSearch.Enabled = btnLoad.Enabled = btnReload.Enabled = true;
                        if (btnOfficial != null) btnOfficial.Enabled = cboSite.SelectedIndex == 0;
                        UpdateSelLabel();
                        btnCancel.Enabled = false;
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Log(string msg)
        {
            if (msg == null) return;
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine;
            UiInvoke(() =>
            {
                txtLog.AppendText(line);
                if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200000);
            });
        }

        /// <summary>
        /// 跨线程**同步**取一个返回值。
        ///
        /// 为什么需要：`UiInvoke` 是 BeginInvoke（异步），适合"刷界面"；
        /// 但队列里要在下载前弹确认框——必须**等用户点完**再决定下不下，
        /// 那就只能是同步的 Invoke。
        /// 出任何异常都回退到 fallback，绝不把界面线程的问题变成队列崩溃。
        /// </summary>
        private T UiInvokeGet<T>(Func<T> f, T fallback)
        {
            if (f == null) return fallback;
            try
            {
                if (IsDisposed) return fallback;
                if (!InvokeRequired) return f();
                return (T)Invoke(f);
            }
            catch (Exception ex)
            {
                try { Log("界面回调失败（已回退）：" + ex.GetType().Name + " / " + ex.Message); } catch { }
                return fallback;
            }
        }

        private void UiInvoke(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                // ★ 跨线程这条路必须包一层"带日志"的委托。
                //   原来直接 BeginInvoke(a)：委托体在 UI 线程抛的异常会被
                //   WinForms 的未处理异常机制吃掉/弹框，**调用方完全不知道**，
                //   现象就是"日志说搜到了 N 条，可列表是空的" —— 排查时毫无线索。
                //   包一层之后至少能在日志里留下痕迹。
                try
                {
                    BeginInvoke((MethodInvoker)delegate ()
                    {
                        try { a(); }
                        catch (Exception ex)
                        {
                            try { Log("界面刷新出错（已忽略）：" + ex.GetType().Name + " / " + ex.Message); }
                            catch { }
                        }
                    });
                }
                catch { /* 窗体正在销毁，BeginInvoke 会抛 */ }
                return;
            }
            try { a(); }
            catch (Exception ex)
            {
                try { Log("界面刷新出错（已忽略）：" + ex.GetType().Name + " / " + ex.Message); }
                catch { }
            }
        }

        /// <summary>
        /// 输入是不是"直接可用的地址/标识"而不是书名。
        /// 认这几种：
        ///   https://www.biquga.com/10_10333/      → biquga，dir=/10_10333
        ///   /10_10333  或  10_10333              → biquga，dir=/10_10333
        ///   https://fanqienovel.com/page/123456  → 番茄，bookId=123456
        ///   123456（纯数字，番茄站点下）          → 番茄，bookId=123456
        /// </summary>
        internal static bool LooksLikeAddress(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            var t = s.Trim();
            if (t.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            if (Regex.IsMatch(t, @"^/?\d+_\d+/?$")) return true;              // biquga 的 dir
            if (Regex.IsMatch(t, @"^\d{6,}$")) return true;                  // 番茄 book_id
            if (Regex.IsMatch(t, @"fanqienovel\.com|biquga\.com", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        /// <summary>把地址解析成一个"可以直接 LoadBook 的" BookInfo。认不出来返回 null。</summary>
        private static BookInfo FromAddress(string s)
        {
            var t = (s ?? "").Trim();

            // 番茄：链接里的 /page/<id>，或者纯数字 id
            var fm = Regex.Match(t, @"fanqienovel\.com/(?:page|book)/(\d+)", RegexOptions.IgnoreCase);
            if (fm.Success)
                return new BookInfo { Site = "fanqie", BookId = fm.Groups[1].Value, Url = FanqieSite.Origin + "/page/" + fm.Groups[1].Value };
            if (Regex.IsMatch(t, @"^\d{6,}$"))
                return new BookInfo { Site = "fanqie", BookId = t, Url = FanqieSite.Origin + "/page/" + t };

            // 笔趣阁：/10_10333 这种目录形式
            var bm = Regex.Match(t, @"(\d+_\d+)");
            if (bm.Success)
            {
                var dir = "/" + bm.Groups[1].Value;
                return new BookInfo { Site = "biquga-m", Dir = dir, Url = BiqugaMobileSite.Host + dir + "/" };
            }
            return null;
        }

        // ============================================================
        //  站点探活与自动降级
        // ============================================================

        /// <summary>下拉框第 i 项的站点名（不带可用性后缀），来自注册表</summary>
        private static string SiteLabelAt(int index)
        {
            var all = SiteRegistry.All;
            if (index < 0 || index >= all.Count) return "";
            return all[index].DisplayName;
        }

        /// <summary>探活结果：站点下标 → 不可用原因（null = 可用）</summary>
        private readonly Dictionary<int, string> _siteDown = new Dictionary<int, string>();
        private bool _probing;

        /// <summary>
        /// 后台给三个站点各探活一次，把不可用的标注在下拉框里。
        ///
        /// 为什么值得做：实测 `m.biquga.com` 会被 CDN 按 SNI 拒掉 TLS 握手，
        /// 而界面上只会抛一句 `curl 退出码 35` —— 用户既不知道原因，
        /// 更不知道**换个站点就能用**。这里提前告诉他。
        ///
        /// 注意：探活**不改变用户的选择**，只标注。自动改选择会让用户困惑
        /// （"我明明选了移动版，怎么自己变了"）。降级建议由用户点按钮触发。
        /// </summary>
        internal void ProbeSitesAsync()
        {
            if (_probing) return;
            _probing = true;
            Log("正在检测各站点是否可用（各发 1 个请求）…");

            RunBackground("检测站点可用性…", () =>
            {
                var result = new Dictionary<int, string>();
                // 站点列表来自注册表，不再手写数组 —— 加站点不用改这里
                var all = SiteRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i].Factory() as IProbeable;
                    string reason = null;
                    if (p != null)
                    {
                        try { reason = p.Probe(); }
                        catch (Exception ex) { reason = ex.Message; }
                    }
                    result[i] = reason;
                }

                UiInvoke(() =>
                {
                    _probing = false;
                    _siteDown.Clear();
                    foreach (var kv in result) if (kv.Value != null) _siteDown[kv.Key] = kv.Value;
                    ApplySiteProbeLabels();

                    foreach (var kv in result)
                    {
                        if (kv.Value == null) Log("· " + SiteLabelAt(kv.Key) + "：可用");
                        else Log("· " + SiteLabelAt(kv.Key) + "：不可用 —— " + kv.Value);
                    }
                    // 当前生效的站点不可用 → 明确提示并给出降级建议
                    // （自动模式下"当前生效的"是实测最快的那个，不是下拉框停在哪）
                    int cur = SiteRegistry.IndexOf(EffectiveSiteKey());
                    if (cur >= 0 && _siteDown.ContainsKey(cur))
                        Log("⚠ 当前使用的「" + SiteLabelAt(cur) + "」不可用。" + DowngradeAdvice(cur));
                });
            });
        }

        /// <summary>把探活结果画进下拉框（可用性直接写在站名后面）</summary>
        private void ApplySiteProbeLabels()
        {
            int sel = cboSite.SelectedIndex;
            _reloadingSites = true;      // 改 item 文字会触发 SelectedIndexChanged，先挡住
            try
            {
                int n = Math.Min(cboSite.Items.Count, SiteRegistry.Count);
                for (int i = 0; i < n; i++)
                {
                    var text = SiteLabelAt(i);
                    if (_siteDown.ContainsKey(i)) text += "　（当前不可用）";
                    if (cboSite.Items[i] as string != text) cboSite.Items[i] = text;
                }
                if (sel >= 0 && sel < cboSite.Items.Count) cboSite.SelectedIndex = sel;
            }
            finally { _reloadingSites = false; }

            // 自动模式下还要把"现在实际用的是哪个站"重新标注一次，
            // 否则用户看到的是"下拉框停在某个站"，而实际用的是另一个。
            if (_autoSite) ApplySiteSelection();
        }

        /// <summary>
        /// 站点不可用时给一句能照做的建议。
        ///
        /// 以前是硬编码 1↔2 的"两个笔趣阁互切"，站点一多就完全失效。
        /// 现在按**能力**给建议：目录能一页拿全的站就推荐它（快得多），
        /// 反正是从注册表里挑一个当前探活可用的。
        /// </summary>
        private string DowngradeAdvice(int index)
        {
            int alt = PickAlternativeSiteIndex(index);
            if (alt < 0) return "";
            var d = SiteRegistry.All[alt];
            string name = d.DisplayName;
            if (d.Has(SiteCapabilities.TocInDetailPage))
                return "可以改用「" + name + "」：目录一次请求就能拿全，快很多。";
            return "可以改用「" + name + "」。";
        }

        /// <summary>
        /// 从注册表里挑一个"当前探活可用、且不是当前站"的替代站。
        ///
        /// 优先级：能一页拿全目录的（实测最快） &gt; 其余按注册顺序。
        /// 返回下拉框下标；没有可用替代则 -1。
        /// </summary>
        private int PickAlternativeSiteIndex(int excludeIndex)
        {
            var all = SiteRegistry.All;
            int bestFast = -1, bestAny = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (i == excludeIndex) continue;
                if (_siteDown.ContainsKey(i)) continue;          // 探活说不通就别推荐
                if (bestAny < 0) bestAny = i;
                if (bestFast < 0 && all[i].Has(SiteCapabilities.TocInDetailPage)) bestFast = i;
            }
            return bestFast >= 0 ? bestFast : bestAny;
        }

        /// <summary>「检测站点」按钮：重新探活一次</summary>
        /// <summary>
        /// 日志框的右键菜单：出问题时最需要的三件事。
        ///
        /// 为什么用右键菜单而不是工具栏按钮：工具栏在小窗口下已经很挤，
        /// 再塞按钮会换行、顶坏布局体检；而"我要报问题"这个动作天然发生在
        /// **看着日志的时候**，右键就在手边。
        /// </summary>
        private ContextMenuStrip BuildLogMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("复制全部日志", null, delegate (object s, EventArgs e)
            {
                try { Clipboard.SetText(txtLog.Text); Log("日志已复制到剪贴板。"); }
                catch (Exception ex) { Log("复制失败：" + ex.Message); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("生成诊断包…（打码后打包，发给作者最快）", null,
                delegate (object s, EventArgs e) { DoExportDiagnostics(); });
            menu.Items.Add("站点健康（实测哪个源能用）", null,
                delegate (object s, EventArgs e) { DoShowSiteHealth(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开备份目录", null, delegate (object s, EventArgs e)
            {
                try
                {
                    string dir = TomatoBiquga.LocalBackup.DefaultRoot;
                    if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                    {
                        Log("还没有备份目录（程序启动时会自动备份一次）。");
                        return;
                    }
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                }
                catch (Exception ex) { Log("打开备份目录失败：" + ex.Message); }
            });
            return menu;
        }

        /// <summary>
        /// 「生成诊断包」：环境 + 设置（打码）+ 日志 + 站点评分 → 一个 zip。
        /// 省掉"请把日志发我 / 你的设置是什么 / 哪个站不行"这三轮来回。
        /// </summary>
        private void DoExportDiagnostics()
        {
            string path = null;
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "保存诊断包";
                    dlg.Filter = "压缩包 (*.zip)|*.zip";
                    dlg.FileName = "novel-downloader-诊断-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
                    try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); }
                    catch { }
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    path = dlg.FileName;
                }

                var extra = new System.Collections.Generic.List<string>();
                extra.Add("当前站点：" + (cboSite != null && cboSite.SelectedItem != null
                    ? cboSite.SelectedItem.ToString() : "（未选）"));
                extra.Add("作品：" + (_currentBook != null ? _currentBook.Title : "（未载入）"));
                extra.Add("章节数：" + (_currentBook != null && _currentBook.Chapters != null ? _currentBook.Chapters.Count : 0));
                extra.Add("保存到：" + (txtOutput != null ? txtOutput.Text.Trim() : "?"));

                DiagnosticsExporter.Build(path, AppDomain.CurrentDomain.BaseDirectory,
                    txtLog != null ? txtLog.Text : "", extra, _siteScores);
                Log("诊断包已生成：" + path);
                Log("（Cookie/Token/API Key 已打码；不含书名和正文）");
            }
            catch (Exception ex)
            {
                Log("生成诊断包失败：" + ex.Message);
            }
        }

        /// <summary>「站点健康」：把实测到的各站情况摊开（可现场重新实测）。</summary>
        private void DoShowSiteHealth()
        {
            try
            {
                if (_siteScores == null) _siteScores = SiteRater.Load(SiteProfileStore.DefaultPath);
                using (var dlg = new SiteHealthDialog(_siteScores, Log))
                {
                    dlg.ShowDialog(this);
                    // 窗口里可能刚测完，把结果接回来给"自动选最快"用
                    _siteScores = SiteRater.Load(SiteProfileStore.DefaultPath) ?? _siteScores;
                }
            }
            catch (Exception ex)
            {
                Log("打开站点健康失败：" + ex.Message);
            }
        }

        private void DoProbeSites()
        {
            if (_probing) { Log("站点检测正在进行中…"); return; }
            if (_rating) { Log("站点实测正在进行中…"); return; }
            ProbeSitesAsync();
            // 勾了「自动选最快」就顺手重测评分 —— 用户点这个按钮的意图就是
            // "现在到底哪个站能用、哪个快"，只探活不测速等于只答了一半。
            if (_autoSite) RateSitesAsync(true);
        }

        // ============================================================
        //  「自动选最快站点」
        // ============================================================

        /// <summary>
        /// 确保 _siteScores 有值：先读缓存（站点配置.ini），没有就算了。
        /// **不联网**，所以可以放心在界面线程调用。
        /// </summary>
        private void EnsureScores()
        {
            if (_siteScores != null) return;
            _siteScores = SiteRater.Load(SiteProfileStore.DefaultPath);
            if (_siteScores != null && !SiteRater.IsFresh(_siteScores))
            {
                Log("站点评分已过期（超过 " + SiteRater.MaxAgeHours + " 小时），将重新实测。");
                _siteScores = null;   // 过期就当作没有，交给后台重测
            }
        }

        /// <summary>
        /// 后台实测各站点速度并排名。force=false 时若缓存还新鲜就直接用。
        ///
        /// 全部失败**不回退错误**：FastestKey 会返回 null，EffectiveSiteKey 会
        /// 退到内置笔趣阁 PC 版 —— 也就是"自动"打开之前的老行为。
        /// </summary>
        internal void RateSitesAsync(bool force)
        {
            if (_rating) return;
            if (SuppressDialogs) return;      // 自测/自动化下绝不联网

            EnsureScores();
            if (!force && SiteRater.IsFresh(_siteScores))
            {
                LogRanking("（用缓存的评分）");
                ApplySiteSelection();
                return;
            }

            _rating = true;
            Log("正在实测各站点速度（每站 3 个请求：搜索 / 目录 / 一章正文）…");
            RunBackground("实测站点速度…", () =>
            {
                List<SiteScore> scores = null;
                try { scores = SiteRater.MeasureAll(Log); }
                catch (Exception ex) { Log("实测站点速度出错：" + ex.Message); }

                UiInvoke(() =>
                {
                    _rating = false;
                    if (scores != null)
                    {
                        _siteScores = scores;
                        SiteRater.Save(scores, SiteProfileStore.DefaultPath);
                    }
                    LogRanking("");
                    ApplySiteSelection();
                });
            });
        }

        /// <summary>把排名写进日志（用户在日志区就能看到"为什么选了它"）</summary>
        private void LogRanking(string suffix)
        {
            if (_siteScores == null || _siteScores.Count == 0)
            {
                Log("站点速度排名：暂无实测数据。");
                return;
            }
            var ranked = SiteRater.Rank(_siteScores);
            var sb = new System.Text.StringBuilder();
            sb.Append("站点速度排名（综合分越小越快）").Append(suffix).Append("：");
            int n = 0;
            foreach (var s in ranked)
            {
                n++;
                // 不可用的站点**不能**打印成分数 —— 那会让人误以为"它只是慢"。
                // （实测笔趣阁 PC 版目录要 91 万毫秒，总分会是 ∞ 或超大值。）
                if (!s.Ok)
                {
                    sb.Append("\n  ").Append(n).Append(". ").Append(s.DisplayName)
                      .Append("：不可用（").Append(s.Error ?? "未知").Append("）");
                    continue;
                }
                sb.Append("\n  ").Append(n).Append(". ").Append(s.DisplayName)
                  .Append("：").Append(s.TotalMs.ToString("F0")).Append(" ms")
                  .Append("（搜索 ").Append(s.SearchMs.ToString("F0"))
                  .Append(" + 目录 ").Append(s.TocMs.ToString("F0"))
                  .Append(" + 正文 ").Append(s.ChapterMs.ToString("F0")).Append("）");
                if (s.TocCount > 0) sb.Append("，目录 ").Append(s.TocCount).Append(" 章");
                if (!string.IsNullOrEmpty(s.SampleTitle)) sb.Append("，《").Append(s.SampleTitle).Append("》");
            }
            string fast = SiteRater.FastestKey(_siteScores);
            sb.Append("\n  → 最快：").Append(fast == null ? "（没有可用站点，回退到笔趣阁 PC版）" : SiteRegistry.LabelOf(fast));
            Log(sb.ToString());
        }

        // ============================================================
        //  AI 设置
        // ============================================================

        // ============================================================
        //  AI 裁决错字 —— 统一的选择对话框
        // ============================================================

        /// <summary>
        /// 让用户选"本地还是云端"，并把参数配好。
        ///
        /// 出现的时机：**「检测错字」跑完双源比对之后、要送 AI 之前** ——
        /// 那时差异清单已经算好，可以顺便告诉他"有 37 处高可疑、约 4000 token"，
        /// 让他带着真实成本做选择。
        ///
        /// 交互取舍：
        ///   · **默认选中「本地」** —— 不花钱、不联网，最没负担。
        ///   · 本地下拉**列出你机器上真装了的模型**（探 /api/tags），
        ///     不用手打 `qwen2.5:7b` 这种容易写错的名字。
        ///   · 云端给预设（智谱有免费额度所以排第一），选完自动填好 baseurl 与模型名。
        ///   · 没装 Ollama 时如实说明，并引导去云端或去装。
        ///   · 记住上次的选择，第二次不再打扰。
        ///
        /// 返回 null = 用户选择不用 AI。
        /// </summary>
        private AiAdjudicator.Config AskAiChoice(int candidateCount, int approxTokens)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "用 AI 裁决错字";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(620, 470);

                var head = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 68,
                    Padding = new Padding(12, 12, 12, 0),
                    Text = string.Format(
                        "双源比对完成：有 {0} 处差异值得让 AI 判断哪个写法对。\n" +
                        "预计发出约 {1:N0} 字符（≈{1:N0} token）—— 只发差异点前后十几个字，不发整章。\n" +
                        "AI 也会判断错，重要的地方请自己复核。",
                        candidateCount, approxTokens),
                };

                // ---- 本地 ----
                var rdoLocal = new RadioButton { Text = "本地模型（不联网、不花钱）", AutoSize = true };
                var cboLocal = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 320 };
                var lblLocalInfo = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0), Text = "正在探测本机模型…" };

                var localBox = new GroupBox { Dock = DockStyle.Top, Height = 112, Padding = new Padding(12, 6, 12, 6) };
                var localFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var localRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                localRow.Controls.Add(cboLocal);
                localFlow.Controls.AddRange(new Control[] { rdoLocal, localRow, lblLocalInfo });
                localBox.Controls.Add(localFlow);

                // ---- 云端 ----
                var rdoCloud = new RadioButton { Text = "云端模型（快、效果好；差异片段会发给服务商）", AutoSize = true };
                var cboPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
                var txtUrl = new TextBox { Width = 430 };
                var txtModel = new TextBox { Width = 430 };
                var txtKey = new TextBox { Width = 430 };
                var lblPresetNote = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(560, 0) };

                var cloudBox = new GroupBox { Dock = DockStyle.Top, Height = 190, Padding = new Padding(12, 6, 12, 6) };
                var cloudFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var rowPreset = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                rowPreset.Controls.Add(cboPreset);
                var rowUrl = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                rowUrl.Controls.AddRange(new Control[] { new Label { Text = "地址：", AutoSize = true }, txtUrl });
                var rowModel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                rowModel.Controls.AddRange(new Control[] { new Label { Text = "模型：", AutoSize = true }, txtModel });
                var rowKey = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                rowKey.Controls.AddRange(new Control[] { new Label { Text = "密钥：", AutoSize = true }, txtKey });
                cloudFlow.Controls.AddRange(new Control[] { rdoCloud, rowPreset, lblPresetNote, rowUrl, rowModel, rowKey });
                cloudBox.Controls.Add(cloudFlow);

                var presets = AiAdjudicator.CloudPresets();
                foreach (var p in presets) cboPreset.Items.Add(p.Name);

                var bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 46,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(12, 8, 12, 8),
                    WrapContents = false,
                };
                var btnTest = new Button { Text = "测试连接", AutoSize = true };
                var btnOk = new Button { Text = "开始 AI 裁决", AutoSize = true };
                var btnSkip = new Button { Text = "不用 AI", AutoSize = true };
                bar.Controls.AddRange(new Control[] { btnTest, btnOk, btnSkip });

                AiAdjudicator.Config picked = null;

                // 预置：记住上次的选择
                bool cloud = _settings.AiEnabled && _settings.AiBackend == "openai";
                if (!string.IsNullOrEmpty(_settings.AiModel)) txtModel.Text = _settings.AiModel;
                if (cloud && !string.IsNullOrEmpty(_settings.AiBaseUrl)) txtUrl.Text = _settings.AiBaseUrl;
                if (!string.IsNullOrEmpty(_settings.AiApiKey)) txtKey.Text = _settings.AiApiKey;
                rdoCloud.Checked = cloud;
                rdoLocal.Checked = !cloud;

                // 本机模型探测放后台 —— 要发请求，不能让对话框卡住
                var probeThread = new System.Threading.Thread(() =>
                {
                    var found = AiAdjudicator.ListLocalModels(_settings.AiBaseUrl, 5);
                    try
                    {
                        dlg.BeginInvoke(new Action(() =>
                        {
                            cboLocal.Items.Clear();
                            foreach (var m in found) cboLocal.Items.Add(m);
                            if (found.Count > 0)
                            {
                                var prefer = _settings.AiModel;
                                cboLocal.SelectedItem = found.Contains(prefer) ? prefer : found[0];
                                lblLocalInfo.Text = "检测到本机 Ollama 已装 " + found.Count + " 个模型，直接选一个即可。";
                            }
                            else
                            {
                                cboLocal.Text = _settings.AiModel;
                                lblLocalInfo.Text = "没检测到本机 Ollama（未安装或未启动）。" +
                                    "装好之后执行 `ollama pull qwen2.5:7b`，再回来这里就能选到；也可以改用云端。";
                            }
                        }));
                    }
                    catch { }
                });
                probeThread.IsBackground = true;
                probeThread.Start();

                Action syncCloudFields = () =>
                {
                    var idx = cboPreset.SelectedIndex;
                    if (idx < 0 || idx >= presets.Count) return;
                    var p = presets[idx];
                    lblPresetNote.Text = p.Note;
                    if (p.BaseUrl.Length > 0) txtUrl.Text = p.BaseUrl;
                    if (p.Model.Length > 0) txtModel.Text = p.Model;
                };

                if (cboPreset.Items.Count > 0)
                {
                    int pick = 0;
                    for (int i = 0; i < presets.Count; i++)
                    {
                        var bu = presets[i].BaseUrl;
                        if (bu.Length > 0 && !string.IsNullOrEmpty(_settings.AiBaseUrl) &&
                            _settings.AiBaseUrl.IndexOf(bu, StringComparison.OrdinalIgnoreCase) >= 0)
                        { pick = i; break; }
                    }
                    cboPreset.SelectedIndex = pick;
                    syncCloudFields();
                }
                cboPreset.SelectedIndexChanged += (s, e) => syncCloudFields();

                Action syncEnabled = () =>
                {
                    bool c = rdoCloud.Checked;
                    cboLocal.Enabled = !c;
                    foreach (Control x in new Control[] { cboPreset, txtUrl, txtModel, txtKey }) x.Enabled = c;
                };
                rdoCloud.CheckedChanged += (s, e) => syncEnabled();
                rdoLocal.CheckedChanged += (s, e) => syncEnabled();
                syncEnabled();

                Func<AiAdjudicator.Config> read = () =>
                {
                    if (rdoLocal.Checked)
                        return new AiAdjudicator.Config
                        {
                            Backend = "ollama",
                            BaseUrl = string.IsNullOrEmpty(_settings.AiBaseUrl)
                                ? "http://127.0.0.1:11434" : _settings.AiBaseUrl,
                            Model = cboLocal.Text.Trim(),
                            BatchSize = _settings.AiBatchSize,
                        };
                    return new AiAdjudicator.Config
                    {
                        Backend = "openai",
                        BaseUrl = txtUrl.Text.Trim(),
                        Model = txtModel.Text.Trim(),
                        ApiKey = txtKey.Text.Trim(),
                        BatchSize = _settings.AiBatchSize,
                    };
                };

                btnTest.Click += (s, e) =>
                {
                    var cfg = read();
                    var bad = cfg.Validate();
                    if (bad != null)
                    {
                        MessageBox.Show(dlg, "配置不完整：\n\n" + bad, "AI 连接测试",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    btnTest.Enabled = false;
                    btnTest.Text = "测试中…";
                    dlg.Refresh();
                    Application.DoEvents();   // 先重绘，否则看起来像卡死
                    var res = AiAdjudicator.TestConnection(cfg);
                    btnTest.Text = "测试连接";
                    btnTest.Enabled = true;
                    MessageBox.Show(dlg, res, "AI 连接测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };

                btnOk.Click += (s, e) =>
                {
                    var cfg = read();
                    var bad = cfg.Validate();
                    if (bad != null)
                    {
                        MessageBox.Show(dlg, "还不能开始：\n\n" + bad, "AI 裁决",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (cfg.IsCloud)
                    {
                        var ok = MessageBox.Show(dlg,
                            "即将把**差异点前后的正文片段**发送到：\n" + cfg.BaseUrl +
                            "\n\n模型：" + cfg.Model +
                            "\n密钥会以**明文**保存到 settings.ini。\n\n确认继续吗？",
                            "内容会发送到第三方", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (ok != DialogResult.Yes) return;
                    }
                    picked = cfg;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };

                btnSkip.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

                dlg.Controls.Add(cloudBox);
                dlg.Controls.Add(localBox);
                dlg.Controls.Add(head);
                dlg.Controls.Add(bar);
                dlg.CancelButton = btnSkip;

                var ret = dlg.ShowDialog(this);
                if (ret != DialogResult.OK || picked == null) return null;

                // 记住这次选择（下次不再问）
                _settings.AiEnabled = true;
                _settings.AiBackend = picked.Backend;
                _settings.AiModel = picked.Model;
                if (picked.IsCloud)
                {
                    _settings.AiBaseUrl = picked.BaseUrl;
                    _settings.AiApiKey = picked.ApiKey;
                }
                else if (string.IsNullOrEmpty(_settings.AiBaseUrl))
                {
                    _settings.AiBaseUrl = "http://127.0.0.1:11434";
                }
                try { _settings.Save(); } catch { }
                Log("AI 裁决本次使用：" + picked.Describe());
                return picked;
            }
        }

        /// <summary>「AI 设置」按钮：配置后端、模型、Key，并能当场测连接</summary>
        private void DoAiSettings()
        {
            if (_busy) { BusyNotice("AI 设置"); return; }

            using (var dlg = new Form())
            {
                dlg.Text = "AI 裁决错字 —— 设置";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(560, 400);

                var tip = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 96,
                    Padding = new Padding(12, 12, 12, 0),
                    Text =
                        "AI 只做一件事：把「检测错字」里两个源写法不同、不知道哪个对的那几处\n" +
                        "交给大模型判断。**只发差异点前后十几个字**，不发整章、不发整本。\n\n" +
                        "· 本地后端（Ollama）：不联网、不花钱。需要先装 Ollama 并下载模型。\n" +
                        "· 云端后端：差异片段会发送到该服务商；API Key 以明文存在 settings.ini。\n\n" +
                        "默认关闭；不开就完全不会联网、不会产生任何费用。",
                };

                var grid = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    Height = 150,
                    ColumnCount = 2,
                    Padding = new Padding(12, 6, 12, 0),
                };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var chkEnabled = new CheckBox { Text = "启用 AI 裁决", AutoSize = true, Checked = _settings.AiEnabled };
                var cboBackend = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
                cboBackend.Items.AddRange(new object[] { "ollama", "openai" });
                cboBackend.SelectedItem = _settings.AiBackend == "openai" ? "openai" : "ollama";
                var txtUrl = new TextBox { Text = _settings.AiBaseUrl, Width = 420 };
                var txtModel = new TextBox { Text = _settings.AiModel, Width = 420 };
                var txtKey = new TextBox { Text = _settings.AiApiKey, Width = 420, UseSystemPasswordChar = false };
                var numBatch = new NumericUpDown { Minimum = 1, Maximum = 50, Value = Math.Max(1, Math.Min(50, _settings.AiBatchSize)), Width = 60 };

                grid.Controls.Add(new Label { Text = "总开关：", AutoSize = true }, 0, 0);
                grid.Controls.Add(chkEnabled, 1, 0);
                grid.Controls.Add(new Label { Text = "后端：", AutoSize = true }, 0, 1);
                grid.Controls.Add(cboBackend, 1, 1);
                grid.Controls.Add(new Label { Text = "服务地址：", AutoSize = true }, 0, 2);
                grid.Controls.Add(txtUrl, 1, 2);
                grid.Controls.Add(new Label { Text = "模型名：", AutoSize = true }, 0, 3);
                grid.Controls.Add(txtModel, 1, 3);
                grid.Controls.Add(new Label { Text = "API Key：", AutoSize = true }, 0, 4);
                grid.Controls.Add(txtKey, 1, 4);
                grid.Controls.Add(new Label { Text = "每批条数：", AutoSize = true }, 0, 5);
                grid.Controls.Add(numBatch, 1, 5);

                var hint = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 34,
                    Padding = new Padding(12, 4, 12, 0),
                    ForeColor = Color.DimGray,
                    Text = "本地示例：ollama / http://127.0.0.1:11434 / qwen2.5:7b（Key 留空）\n" +
                           "云端示例：openai / https://api.deepseek.com / deepseek-chat（填 Key）",
                };

                var bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 46,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(12, 8, 12, 8),
                    WrapContents = false,
                };
                var btnTest = new Button { Text = "测试连接", AutoSize = true };
                var btnSave = new Button { Text = "保存", AutoSize = true };
                var btnCancel = new Button { Text = "取消", AutoSize = true };
                bar.Controls.AddRange(new Control[] { btnTest, btnSave, btnCancel });

                // 切后端时自动填一份合理的默认地址，省得用户去查
                cboBackend.SelectedIndexChanged += (s, e) =>
                {
                    bool cloud = (string)cboBackend.SelectedItem == "openai";
                    if (cloud && txtUrl.Text.Trim() == "http://127.0.0.1:11434")
                        txtUrl.Text = "https://api.deepseek.com";
                    if (!cloud && txtUrl.Text.Trim().StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
                        txtUrl.Text = "http://127.0.0.1:11434";
                };

                btnTest.Click += (s, e) =>
                {
                    var cfg = ReadAiConfig(chkEnabled, cboBackend, txtUrl, txtModel, txtKey, numBatch);
                    btnTest.Enabled = false;
                    btnTest.Text = "测试中…";
                    dlg.Refresh();
                    // 本地小模型可能要几十秒，必须丢到后台，否则窗口假死
                    var res = AiAdjudicator.TestConnection(cfg);
                    btnTest.Text = "测试连接";
                    btnTest.Enabled = true;
                    MessageBox.Show(dlg, res, "AI 连接测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };

                btnSave.Click += (s, e) =>
                {
                    var cfg = ReadAiConfig(chkEnabled, cboBackend, txtUrl, txtModel, txtKey, numBatch);
                    if (chkEnabled.Checked)
                    {
                        var bad = cfg.Validate();
                        if (bad != null)
                        {
                            MessageBox.Show(dlg, "配置不完整：\n\n" + bad, "AI 设置",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        // 云端要再确认一次：这是"内容外发"的同意步骤
                        if (cfg.IsCloud)
                        {
                            var ok = MessageBox.Show(dlg,
                                "你选择了云端后端。\n\n" +
                                "「检测错字」跑 AI 时，**差异点前后的正文片段会被发送到**：\n" +
                                cfg.BaseUrl + "\n\n" +
                                "API Key 会以**明文**保存到 settings.ini。\n\n" +
                                "确认继续吗？",
                                "内容会发送到第三方", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if (ok != DialogResult.Yes) return;
                        }
                    }
                    _settings.AiEnabled = cfg != null && chkEnabled.Checked;
                    _settings.AiBackend = (string)cboBackend.SelectedItem;
                    _settings.AiBaseUrl = txtUrl.Text.Trim();
                    _settings.AiModel = txtModel.Text.Trim();
                    _settings.AiApiKey = txtKey.Text.Trim();
                    _settings.AiBatchSize = (int)numBatch.Value;
                    try
                    {
                        _settings.Save();
                        Log("AI 设置已保存：" + (_settings.AiEnabled
                            ? new AiAdjudicator.Config
                              {
                                  Backend = _settings.AiBackend, BaseUrl = _settings.AiBaseUrl,
                                  Model = _settings.AiModel, ApiKey = _settings.AiApiKey,
                              }.Describe()
                            : "已关闭（不会联网）"));
                    }
                    catch (Exception ex) { Log("保存 AI 设置失败：" + ex.Message); }
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };

                btnCancel.Click += (s, e) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

                dlg.Controls.Add(hint);
                dlg.Controls.Add(grid);
                dlg.Controls.Add(tip);
                dlg.Controls.Add(bar);
                dlg.CancelButton = btnCancel;

                dlg.ShowDialog(this);
            }
        }

        /// <summary>从 AI 设置对话框的控件里读配置（测试连接与保存共用，避免两处读法不一致）</summary>
        private static AiAdjudicator.Config ReadAiConfig(CheckBox enabled, ComboBox backend,
            TextBox url, TextBox model, TextBox key, NumericUpDown batch)
        {
            return new AiAdjudicator.Config
            {
                Backend = backend.SelectedItem == null ? "ollama" : (string)backend.SelectedItem,
                BaseUrl = url.Text.Trim(),
                Model = model.Text.Trim(),
                ApiKey = key.Text.Trim(),
                BatchSize = (int)batch.Value,
            };
        }

        /// <summary>
        /// 载入目录/下载前的前置检查：当前站点不可用就提示降级。
        /// 返回 true = 可以继续；false = 用户选择了放弃。
        /// </summary>
        private bool EnsureSiteUsable()
        {
            int cur = SiteRegistry.IndexOf(EffectiveSiteKey());
            if (cur < 0 || !_siteDown.ContainsKey(cur)) return true;

            var advice = DowngradeAdvice(cur);
            Log("⚠ 「" + SiteLabelAt(cur) + "」当前不可用：" + _siteDown[cur]);
            if (SuppressDialogs) return true;      // 自测/自动化下不弹窗
            if (_autoSite) return true;            // 自动模式不该弹"要不要换站"——它自己会挑

            // 以前这里是 cur==1?2:(cur==2?1:-1) —— 只会在两个笔趣阁之间对调，
            // 站点超过 3 个就彻底失效。现在从注册表里挑一个探活可用的替代站。
            int alt = PickAlternativeSiteIndex(cur);
            var text = "当前选中的「" + SiteLabelAt(cur) + "」不可用：\n\n" + _siteDown[cur] + "\n\n";
            if (alt >= 0)
            {
                text += "要改用「" + SiteLabelAt(alt) + "」继续吗？\n（" + advice + "）";
                var r = MessageBox.Show(this, text, "站点不可用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes)
                {
                    cboSite.SelectedIndex = alt;
                    Log("已切换到「" + SiteLabelAt(alt) + "」。");
                }
                return r == DialogResult.Yes;
            }

            text += advice;
            MessageBox.Show(this, text, "站点不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        // ============================================================
        //  错字检测（双源比对）
        // ============================================================

        /// <summary>
        /// 「检测错字」：用另一个源重新抓这本书的正文，逐字比对。
        /// 跑在后台，结果写 错字检测报告.txt / .csv 到书目录。
        /// </summary>
        private void DoDetectTypos()
        {
            if (_busy) { BusyNotice("错字检测"); return; }
            if (_currentBook == null) { MessageBox.Show("请先载入一本书的目录"); return; }

            var book = _currentBook;
            string root = txtOutput.Text.Trim();
            if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
            string bookDir, txtPath;
            DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);

            if (!File.Exists(txtPath))
            {
                MessageBox.Show("这本书还没有下载过，没有可比对的正文。\n\n请先点「下载全部章节」。",
                    "检测错字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 选对照源：和当前站点不同的那一个
            var options = new List<string>();
            var keys = new List<string>();
            if (!(CurrentSite() is BiqugaMobileSite)) { options.Add("笔趣阁（移动版·快）"); keys.Add("biquga-m"); }
            if (!(CurrentSite() is BiqugaSite)) { options.Add("笔趣阁（PC版·慢）"); keys.Add("biquga"); }
            if (options.Count == 0)
            {
                MessageBox.Show("需要两个不同的源才能比对。请先在站点下拉框里换一个源，再试。",
                    "检测错字", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var pick = PickTypoSource(options);
            if (pick < 0 || pick >= keys.Count) return;
            var otherKey = keys[pick];
            var otherName = otherKey == "biquga-m" ? "笔趣阁（移动版·快）" : "笔趣阁（PC版·慢）";

            // ★ 先探一下对照源可不可用。
            //   没有这一步的话，源挂掉时是**每章都"抓取失败→跳过"**，
            //   用户要等跑满 N 章之后才从报告里看出来"比对 10 章、全跳过" ——
            //   白等一场，而且提示很含糊（"对照源可能抓不到内容"）。
            //   探活只发 1 个请求，成本可以忽略。
            if (!SuppressDialogs)
            {
                var probeSite = MakeSite(otherKey) as IProbeable;
                if (probeSite != null)
                {
                    Log("先探一下对照源「" + otherName + "」是否可用…");
                    string why = null;
                    try { why = probeSite.Probe(); }
                    catch (Exception ex) { why = ex.Message; }
                    if (why != null)
                    {
                        Log("对照源不可用，已中止：" + why);
                        MessageBox.Show(this,
                            "对照源「" + otherName + "」当前不可用：\n\n" + why +
                            "\n\n换个源再试（下拉框里换一个站点重跑即可）。",
                            "检测错字", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Log("对照源可用，开始比对。");
                }
            }

            string nStr = PromptTypoCount();
            int limit;
            if (!int.TryParse(nStr, out limit) || limit <= 0) limit = 10;
            if (limit > book.Chapters.Count) limit = book.Chapters.Count;

            Log(string.Format("开始错字检测：用「{0}」重新抓 {1} 章与本地正文逐字比对…",
                otherName, limit));

            RunBackground("错字检测中…", () =>
            {
                var result = new TypoFinder.Result
                {
                    Title = book.Title,
                    OtherSource = otherName,
                };

                ISite other = MakeSite(otherKey);
                var bm = other as BiqugaMobileSite;
                if (bm != null) bm.Workers = _settings.BiqugaOnlineWorkers;
                // 笔趣阁 PC 版没有并发旋钮（目录遍历结构上串行，见 BiqugaSite.cs）

                int n = 0;
                int consecutiveEmpty = 0;      // 连续多少章拿不到内容
                foreach (var c in book.Chapters)
                {
                    if (c == null || c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                    if (n >= limit) break;
                    n++;

                    if (_cancel) { Log("已取消错字检测。"); break; }

                    string text = null;
                    try { text = other.LoadChapter(book, c, null); }
                    catch (Exception ex) { Log("  " + c.Title + " 抓取失败：" + ex.Message); }

                    result.ComparedChapters++;
                    if (string.IsNullOrEmpty(text))
                    {
                        result.SkippedChapters++;
                        consecutiveEmpty++;
                        Log("  " + c.Title + " —— 对照源没有内容，跳过");

                        // ★ 连着 5 章都拿不到 → 判定源已经不可用，直接中止。
                        //   否则用户会对着一个"每章都跳过"的进度条干等到跑满 N 章，
                        //   最后只拿到一份"全跳过"的报告 —— 那是白等。
                        //   探活已经先拦了一道，这里是兜底（源可能中途挂掉）。
                        if (consecutiveEmpty >= 5)
                        {
                            Log("★ 连续 " + consecutiveEmpty + " 章都拿不到内容，判定对照源已不可用，提前中止。");
                            result.AbortedEarly = true;
                            break;
                        }
                        continue;
                    }
                    consecutiveEmpty = 0;

                    var diffs = TypoFinder.CompareChapter(c.Title, c.Text, text);
                    if (diffs.Count == 0)
                    {
                        result.IdenticalChapters++;
                        Log("  " + c.Title + " —— 一致");
                    }
                    else
                    {
                        result.Diffs.AddRange(diffs);
                        result.PerChapter[c.Title] = diffs.Count;
                        Log("  " + c.Title + " —— " + diffs.Count + " 处差异");
                    }
                    UiInvoke(() => lblStatus.Text = string.Format("错字检测 {0}/{1} 章…", n, limit));
                }

                // ---- AI 裁决（可选）----
                // 时机刻意放在这里：双源比对刚跑完，差异清单已经算好，
                // 所以能在问用户"本地还是云端"的同时**给出真实成本**
                // （多少处高可疑、约多少 token）—— 让他带着数字做选择，
                // 而不是配置完一堆参数才发现要发多少东西。
                var aiStats = RunAiAdjudication(result, Log);

                // 写报告
                string repPath = null, csvPath = null;
                try
                {
                    Directory.CreateDirectory(bookDir);
                    repPath = Path.Combine(bookDir, "错字检测报告.txt");
                    csvPath = Path.Combine(bookDir, "错字检测报告.csv");
                    File.WriteAllText(repPath,
                        TypoFinder.BuildReport(book, result, txtPath, result.OtherSource),
                        new UTF8Encoding(true));
                    File.WriteAllText(csvPath, TypoFinder.BuildCsv(result), new UTF8Encoding(true));
                }
                catch (Exception ex) { Log("写报告失败：" + ex.Message); }

                Log(result.Summary());
                if (aiStats != null) Log(aiStats.Summary());
                if (repPath != null) Log("报告：" + repPath);

                if (SuppressDialogs) return;
                string tip;
                if (result.AbortedEarly)
                    tip = "★ 对照源中途不可用了（连续多章拿不到内容），已提前中止。\n" +
                          "这次的结果不完整，换个源重跑一次。";
                else if (result.ComparedChapters == 0)
                    tip = "一章都没比对上：对照源可能抓不到内容（站点风控或改版）。";
                else if (result.Diffs.Count == 0)
                    tip = "两个源逐字一致，没发现差异。";
                else
                    tip = string.Format("发现 {0} 处差异，分布在 {1} 章。\n\n" +
                        "注意：两个源同时错成同一个字时检测不出来；\n" +
                        "字数相差过大的章节会被标成「疑似站点差异」而不是错字。",
                        result.Diffs.Count, result.PerChapter.Count);

                MessageBox.Show(string.Format(
                    "错字检测完成！\n\n{0}\n\n{1}\n\n{2}报告位置：\n{3}",
                    result.Summary(),
                    aiStats != null && aiStats.Verdicts > 0 ? aiStats.Summary() + "\n" : "",
                    tip, repPath ?? "(写报告失败)"),
                    "检测错字", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        /// <summary>
        /// 「检测错字」跑完双源比对之后，问用户要不要用 AI 裁决，跑一轮，返回统计。
        /// 返回 null = 没跑（没有高可疑差异 / 用户选了「不用 AI」/ 配置不全）。
        ///
        /// 三条克制（都是刻意的）：
        ///   · **默认不跑**：不问就不联网、不花钱。没配过 AI 的人第一次会看到选择框。
        ///   · **带着真实成本问**：先算好有多少处、约多少 token，再让用户选后端。
        ///   · **失败不抛**：AiAdjudicator.Run 承诺不抛异常，AI 挂了报告照出。
        /// </summary>
        internal AiAdjudicator.SessionStats RunAiAdjudication(TypoFinder.Result result, Action<string> log)
        {
            if (result == null) return null;

            // 只挑"高可疑"的 —— 站点排版差异占了报告里的大头，但它们不是错字，
            // 送了既费钱又会干扰模型判断。
            var cand = AiAdjudicator.PickCandidates(result.Diffs);
            if (cand.Count == 0)
            {
                if (log != null)
                    log("AI：没有需要裁决的高可疑差异（其余属于站点排版差异，不值得送 AI）。");
                return null;
            }
            int chars, tokens;
            AiAdjudicator.Estimate(result.Diffs, cand, out chars, out tokens);

            // 选后端：本地 / 云端（含预设与自填），并把成本一并告诉他
            AiAdjudicator.Config cfg;
            if (SuppressDialogs)
            {
                // 自测/自动化下不弹窗：用已保存的配置；没配过就直接跳过
                if (!_settings.AiEnabled) return null;
                cfg = new AiAdjudicator.Config
                {
                    Backend = _settings.AiBackend, BaseUrl = _settings.AiBaseUrl,
                    Model = _settings.AiModel, ApiKey = _settings.AiApiKey,
                    BatchSize = _settings.AiBatchSize,
                };
                if (cfg.Validate() != null) return null;
            }
            else
            {
                cfg = AskAiChoice(cand.Count, tokens);
                if (cfg == null)
                {
                    if (log != null) log("用户选择了不用 AI，只出「双源比对」报告。");
                    return null;
                }
            }

            var verdicts = new AiAdjudicator.VerdictResult[result.Diffs.Count];
            var stats = new AiAdjudicator.SessionStats();
            AiAdjudicator.Run(result.Diffs, verdicts, cfg, log, () => _cancel, stats);

            // 注意：裁决已由 Run 写回 result.Diffs[i].Ai（报告/CSV 直接读那里），
            // 这里不要再抄一遍 —— 之前正因为"抄写"留给调用方，探针漏抄才出的 bug。
            return stats;
        }

        /// <summary>让用户选对照源（用最简的输入框，避免为一个小选择再写一个对话框）</summary>
        private int PickTypoSource(List<string> options)
        {
            if (options.Count == 1) return 0;
            var text = "用哪个源做对照？\n\n" + string.Join("\n", options.ToArray()) + "\n\n输入序号（1 起）：";
            var s = PromptInput(text, "1");
            int n;
            if (int.TryParse(s, out n) && n >= 1 && n <= options.Count) return n - 1;
            return -1;
        }

        private string PromptTypoCount()
        {
            return PromptInput("比对多少章？（建议先比 5~10 章看看效果）\n\n章数越多越慢，因为要逐章重新抓一遍。", "10");
        }

        /// <summary>极简输入框（.NET 自带的 InputBox 不在 Framework 里，这里手搓一个）</summary>
        private string PromptInput(string prompt, string def)
        {
            if (SuppressDialogs) return def;
            using (var dlg = new Form())
            {
                dlg.Text = "检测错字";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(420, 150);

                var lbl = new Label { Text = prompt, Dock = DockStyle.Top, Height = 90, Padding = new Padding(10, 10, 10, 0) };
                var box = new TextBox { Text = def, Dock = DockStyle.Top, Width = 380 };
                var host = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(10, 0, 10, 0) };
                host.Controls.Add(box);
                box.Dock = DockStyle.Fill;

                var bar = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(10, 5, 10, 5) };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Right, AutoSize = true };
                var no = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, AutoSize = true };
                bar.Controls.Add(no);
                bar.Controls.Add(ok);

                dlg.Controls.Add(host);
                dlg.Controls.Add(lbl);
                dlg.Controls.Add(bar);
                dlg.AcceptButton = ok;
                dlg.CancelButton = no;

                return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }

        /// <summary>当前选中的站点对象</summary>
        private ISite CurrentSite()
        {
            var d = CurrentDescriptor;
            return d == null ? SiteRegistry.Create(CoreSiteKey) : d.Factory();
        }

        /// <summary>
        /// 给对话框（书架/队列）用的入口：把一段工作丢到后台跑，并写主窗口的日志。
        /// 做成一个方法而不是让对话框直接摸 Log/RunBackground：
        /// 那两个是 private，而且对话框不该关心主窗口的内部结构。
        /// </summary>
        internal void RunQueueFromDialog(string status, System.Collections.Generic.List<string> keywords)
        {
            var canceled = new Func<bool>(() => _cancel);
            RunBackground(status, () => RunQueue(keywords, Log, canceled));
        }

        /// <summary>
        /// 对话框请求停止队列。走的是主窗口既有的取消标志，
        /// 所以「取消」按钮、关窗口、队列对话框里的停止是同一套语义。
        /// </summary>
        internal void RequestCancelFromDialog()
        {
            _cancel = true;
        }

        // ---- 给书架对话框用的公开包装（它是另一个类，摸不到 private 成员）----

        /// <summary>把一行写进主窗口日志</summary>
        internal void LogPublic(string msg) { Log(msg); }

        /// <summary>在 UI 线程上执行</summary>
        internal void UiInvokePublic(Action a) { UiInvoke(a); }

        /// <summary>把地址/标识解析成可直接 LoadBook 的 BookInfo（书架追更用）</summary>
        internal static BookInfo FromAddressPublic(string s) { return FromAddress(s); }

        /// <summary>把一段工作丢到后台跑（书架对话框的"检查所有更新"要用）</summary>
        internal void RunBackgroundPublic(string status, Action work) { RunBackground(status, work); }

        /// <summary>
        /// 「任务队列」的逐本下载。界面部分在 QueueDialog，
        /// 真正的活在主窗口这里 —— 因为下载要用主窗口的站点对象、日志、进度条和设置。
        ///
        /// 单本失败不影响后面的书（catch 住继续），这是挂机场景的硬要求：
        /// 睡前排 5 本，不能因为第 2 本站点抽风就整晚只下了一本。
        /// </summary>
        /// <summary>
        /// 从搜索结果里按**标题精确匹配**挑出用户要的那本；挑不出来返回 null。
        ///
        /// 为什么要它：任务队列里用户输入的是**书名**（不是选中项），
        /// 所以搜到多条时"替他挑第一条"就是静默下错书 —— 同名书、山寨书、
        /// 同作者不同作品全都可能排在前面。宁可跳过并让他写全书名。
        ///
        /// 匹配规则（从严到宽，命中即返回）：
        ///   ① 标题与关键词完全相同（忽略首尾空白与大小写）
        ///   ② 标题去掉书名号《》后与关键词相同 —— 站点常把《》写进标题
        ///   ③ 只有一条候选时直接用它（没有歧义）
        /// 其余情况一律返回 null，由调用方报告并列候选。
        /// </summary>
        internal static BookInfo PickByTitle(System.Collections.Generic.List<BookInfo> found, string keyword)
        {
            if (found == null || found.Count == 0) return null;
            if (found.Count == 1) return found[0];          // ③ 没有歧义

            string kw = NormTitle(keyword);
            if (kw.Length == 0) return null;

            foreach (var b in found)                        // ① 完全相同
                if (b != null && NormTitle(b.Title) == kw) return b;

            foreach (var b in found)                        // ② 去掉书名号后相同
                if (b != null && TrimBookMarks(NormTitle(b.Title)) == TrimBookMarks(kw)) return b;

            return null;
        }

        /// <summary>标题归一化：去首尾空白 + 转小写（英文书名大小写不该影响匹配）</summary>
        private static string NormTitle(string s)
        {
            return s == null ? "" : s.Trim().ToLowerInvariant();
        }

        /// <summary>去掉书名号与空格 —— 站点常把《》写进标题，用户输入的没有</summary>
        private static string TrimBookMarks(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("《", "").Replace("》", "")
                    .Replace("〈", "").Replace("〉", "").Replace(" ", "");
        }

        /// <summary>
        /// 队列里的"下载前确认"。返回 true = 可以下。
        ///
        /// 三种情况直接放行（不弹窗）：
        ///   · SuppressDialogs（自动化自测）—— 测试里弹窗会直接把流程挂住
        ///   · 用户在本队列里勾了「不再询问」
        ///   · 窗口已经销毁（没法弹）
        /// </summary>
        private bool ConfirmQueueBook(BookInfo book, int totalChapters, int todoChapters,
            bool willAppend, string bookDir, Action<string> log, out bool stopQueue)
        {
            stopQueue = false;
            string title = book == null ? "" : book.Title;
            string author = book == null ? "" : book.Author;
            string site = SiteRegistry.LabelOf(book == null ? "" : book.Site);

            if (!ShouldAskQueueConfirm(SuppressDialogs, _queueConfirmAll)) return true;

            // ★ C# 5 不允许在委托里用 out 参数（CS1628），所以先落到局部变量再赋值
            bool stop = false;
            bool ok = UiInvokeGet<bool>(delegate
            {
                try
                {
                    using (var dlg = new QueueConfirmDialog(title, author, site,
                        totalChapters, todoChapters, willAppend, bookDir))
                    {
                        var r = dlg.ShowDialog(this);
                        if (dlg.DontAskAgain) _queueConfirmAll = true;
                        stop = dlg.StopQueue;
                        return r == DialogResult.Yes;
                    }
                }
                catch (Exception ex)
                {
                    // 弹窗失败**不能**把队列搞崩：这里选择"放行"，因为用户的本意就是排队下载。
                    try { Log("确认框打不开（已按「下载」继续）：" + ex.Message); } catch { }
                    return true;
                }
            }, false);
            stopQueue = stop;

            if (log != null)
            {
                if (!ok && !stopQueue) log("  （确认框：跳过这本）");
                else if (ok && _queueConfirmAll) log("  （确认框：已选择本队列不再询问）");
            }
            return ok;
        }

        /// <summary>
        /// 队列确认要不要弹窗。**纯函数**，便于离线断言：
        /// 自动化（SuppressDialogs）或用户勾了"不再询问"就不弹。
        /// </summary>
        internal static bool ShouldAskQueueConfirm(bool suppressDialogs, bool dontAskAgain)
        {
            return !suppressDialogs && !dontAskAgain;
        }

        /// <summary>队列里"本次运行内不再询问确认框"（用户勾过一次就记住，直到队列结束）</summary>
        private bool _queueConfirmAll;

        private void RunQueue(System.Collections.Generic.List<string> keywords, Action<string> log, Func<bool> canceled)
        {
            int n = keywords.Count;
            for (int i = 0; i < n; i++)
            {
                if (canceled()) break;
                var kw = keywords[i] == null ? "" : keywords[i].Trim();
                if (kw.Length == 0) continue;

                try
                {
                    log(string.Format("===== 队列 [{0}/{1}]：{2} =====", i + 1, n, kw));

                    // 0) 可能是粘贴进来的地址（书架的追更走的就是这条路），
                    //    那就别去搜索 —— 直接构造出书籍标识。
                    BookInfo target = null;
                    if (LooksLikeAddress(kw))
                    {
                        target = FromAddress(kw);
                        if (target != null) log("  按地址直接载入：《" + target.Title + "》");
                    }

                    // 1) 找书
                    //
                    // ★ 这里原来是无脑 `target = found[0]` —— 只在日志里写一句
                    //   "搜到 N 条，取第一条"。用户输入的是**书名**、不是选中项，
                    //   所以搜到多条时"替他挑第一条"就是在**静默下错书**：
                    //   日志滚过去就看不见了，最后得到一本同名/山寨的书。
                    //   现在改成先按标题精确匹配消歧，消歧不了就**明说并跳过** ——
                    //   漏一本是看得见的失败，下错一本是看不见的失败。
                    if (target == null)
                    {
                        var found = _site.Search(kw, log);
                        // 缓存命中时 Search 可能不写日志，这里补一句，免得日志里什么都没有
                        if (found != null && found.Count > 0)
                            log(string.Format("  搜索返回 {0} 条。", found.Count));
                        if (found == null || found.Count == 0)
                        {
                            log("  没搜到这本书，跳过。");
                            continue;
                        }
                        target = PickByTitle(found, kw);
                        if (target == null)
                        {
                            log(string.Format("  ⚠ 搜到 {0} 条，但**没有一条标题和「{1}」精确对上**，为免下错书已跳过：",
                                found.Count, kw));
                            foreach (var cand in found)
                            {
                                log("      · 《" + cand.Title + "》" +
                                    (string.IsNullOrEmpty(cand.Author) ? "" : "  作者：" + cand.Author));
                            }
                            log("    请把书名写全（或直接把书籍链接粘进队列）再跑一次。");
                            continue;
                        }
                        if (found.Count > 1)
                            log(string.Format("  搜到 {0} 条，按标题精确匹配选中：《{1}》", found.Count, target.Title));
                    }

                    // 2) 目录
                    if (canceled()) break;
                    var book = _site.LoadBook(target, log);
                    if (book == null || book.Chapters == null || book.Chapters.Count == 0)
                    {
                        log("  目录为空，跳过。");
                        continue;
                    }

                    // 3) 只下没下过的章（续传语义，重复排同一本不会重头再下）
                    var todo = new System.Collections.Generic.List<ChapterInfo>();
                    foreach (var c in book.Chapters)
                    {
                        if (c == null || c.IsVolume || string.IsNullOrEmpty(c.Id)) continue;
                        if (string.IsNullOrEmpty(c.Text)) todo.Add(c);
                    }

                    string root = txtOutput.Text.Trim();
                    if (root.Length == 0) root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "下载");
                    string bookDir, txtPath;
                    DownloadRunner.ResolvePaths(root, book.Title, book.Site, out bookDir, out txtPath);
                    bool append = File.Exists(txtPath);

                    if (todo.Count == 0 && append)
                    {
                        log("  已经是最新的，没有新章节。");
                        RememberInShelf(book, txtPath, false);
                        continue;
                    }
                    if (todo.Count == 0) todo.AddRange(book.Chapters);

                    // 3.5) ★ 下载前先确认：把书名/作者/章节数摆给用户看，他点了才下。
                    //      队列是"一次排好几本"的用法，原来搜到就直接下 ——
                    //      万一是同名/山寨/搜索错，用户第二天才发现，白下几百章。
                    int totalChapters = 0;
                    foreach (var c in book.Chapters)
                        if (c != null && !c.IsVolume && !string.IsNullOrEmpty(c.Id)) totalChapters++;
                    bool stopQueue;
                    if (!ConfirmQueueBook(book, totalChapters, todo.Count, append, bookDir, log, out stopQueue))
                    {
                        if (stopQueue)
                        {
                            log("  用户选择停止队列，后面的书不再处理。");
                            break;
                        }
                        log("  用户跳过这本。");
                        continue;
                    }

                    // 4) 下载
                    var runner = new DownloadRunner
                    {
                        Site = _site,
                        Book = book,
                        Chapters = todo,
                        RootDir = root,
                        Log = log,
                        RetryPasses = _settings.RetryPasses,
                        Workers = DownloadWorkersFor(_site),
                        OutputEncoding = CurrentFileEncoding(),
                        OutputTraditional = _settings.OutputTraditional,
                        AppendToExistingFile = append,
                        IsCanceled = canceled,
                    };
                    runner.Run();
                    log(string.Format("  完成：成功 {0} 章，跳过 {1} 章，失败 {2} 章",
                        runner.Ok, runner.Skipped, runner.Failed));
                    if (!string.IsNullOrEmpty(runner.OutputFile)) RememberInShelf(book, runner.OutputFile, true);
                }
                catch (Exception ex)
                {
                    // 单本失败继续下一本：这是队列的核心价值，不能因为一本就整体中断
                    log(string.Format("  这本书失败，继续下一本：{0}", ex.Message));
                }
            }
            log("===== 队列结束 =====");
        }
    }

    /// <summary>
    /// 一次跨站搜索的命中：书 + 它来自哪个站点。
    ///
    /// ★ 必须放在**文件作用域**（这里），不能放进 MainForm 里面 ——
    ///   我第一版把它插在 MainForm 的方法中间，那等于把 MainForm 提前闭合，
    ///   后面所有方法都掉进了这个类的作用域，编译直接报一串
    ///   "The name 'SiteLabel' does not exist in the current context"。
    /// </summary>
    internal sealed class SiteBookHit
    {
        public string SiteKey;
        public string SiteLabel;
        public BookInfo Book;
    }

    /// <summary>
    /// 队列下载前的**确认框**：先把这本书是什么讲清楚（书名/作者/站点/多少章），
    /// 再问"要不要下"。
    ///
    /// 为什么需要（用户提出）：队列是"一次排好几本、睡前跑"的用法，
    /// 而原来的流程是"搜到就直接下" —— 万一名不对（同名/山寨/搜索错），
    /// 用户第二天才发现下错了书，还白下了几百章。
    /// 中间加一道确认，错误在下第一章之前就能拦住。
    ///
    /// 三个出口：
    ///   · 下载        → 继续
    ///   · 跳过这本    → 换下一本
    ///   · 停止队列    → 整个队列停掉（后面都不做了）
    /// 加一个「本队列不再询问」：5 本书弹 5 次太烦，勾上之后剩下的直接下。
    /// </summary>
    internal class QueueConfirmDialog : Form
    {
        /// <summary>用户选了"停止队列"</summary>
        public bool StopQueue;
        /// <summary>用户勾了"本队列不再询问"</summary>
        public bool DontAskAgain;

        private readonly CheckBox _chk;

        public QueueConfirmDialog(string bookTitle, string author, string site, int totalChapters,
            int todoChapters, bool willAppend, string bookDir)
        {
            Text = "确认下载这本？";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(500, 250);
            Font = UiFont.Mono(9.5F);

            var body = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 12, 14, 6),
                Text = Summarize(bookTitle, author, site, totalChapters, todoChapters, willAppend, bookDir),
            };

            var chkBar = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(14, 6, 14, 0) };
            _chk = new CheckBox { Text = "本队列不再询问（后面的书直接开始下）", AutoSize = true, Dock = DockStyle.Left };
            chkBar.Controls.Add(_chk);

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(14, 6, 14, 8) };
            var btnOk = new Button { Text = "下载", AutoSize = true, Dock = DockStyle.Right, DialogResult = DialogResult.Yes };
            var btnSkip = new Button { Text = "跳过这本", AutoSize = true, Dock = DockStyle.Right, DialogResult = DialogResult.No };
            var btnStop = new Button { Text = "停止队列", AutoSize = true, Dock = DockStyle.Right };
            btnStop.Click += delegate { StopQueue = true; DialogResult = DialogResult.Cancel; Close(); };
            bar.Controls.Add(btnOk);
            bar.Controls.Add(btnSkip);
            bar.Controls.Add(btnStop);

            Controls.Add(body);
            Controls.Add(chkBar);
            Controls.Add(bar);

            AcceptButton = btnOk;
            CancelButton = btnSkip;     // Esc = 跳过这本（不是停止，别让误按 Esc 终止整个队列）

            FormClosing += delegate { DontAskAgain = _chk != null && _chk.Checked; };
        }

        /// <summary>
        /// 确认框正文。**抽成纯函数**：内容能被离线单测直接断言，
        /// 不用真的弹窗（弹窗在自动化里会卡住）。
        /// </summary>
        internal static string Summarize(string bookTitle, string author, string site, int totalChapters,
            int todoChapters, bool willAppend, string bookDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("《" + (string.IsNullOrEmpty(bookTitle) ? "(取不到书名)" : bookTitle) + "》");
            sb.AppendLine();
            sb.AppendLine("作者：" + (string.IsNullOrEmpty(author) ? "未知" : author));
            if (!string.IsNullOrEmpty(site)) sb.AppendLine("站点：" + site);
            sb.AppendLine("目录共 " + totalChapters + " 章");
            if (todoChapters > 0 && todoChapters < totalChapters)
                sb.AppendLine("本次要下 " + todoChapters + " 章（其余已下过，会自动跳过）");
            else if (todoChapters > 0)
                sb.AppendLine("本次要下 " + todoChapters + " 章");
            if (willAppend) sb.AppendLine("已存在同名文件：新章节会接在原来的后面");
            if (!string.IsNullOrEmpty(bookDir)) sb.AppendLine("保存到：" + bookDir);
            sb.AppendLine();
            sb.Append("要下载这本吗？");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 「任务队列」对话框：粘贴多行书名，依次下载。
    ///
    /// 为什么直接复用主窗口的 RunQueue 而不是自己实现一遍下载：
    /// 项目里「界面 / 命令行自测」共用同一套 DownloadRunner 是既有原则
    /// （见 docs/架构.md），队列再写一套必然会出现"队列下出来的和手动下的不一样"。
    /// </summary>
    internal class QueueDialog : Form
    {
        private readonly MainForm _owner;
        private readonly TextBox _input;
        private readonly Button _run, _close;
        private readonly Label _hint;

        public QueueDialog(MainForm owner)
        {
            _owner = owner;
            Text = "任务队列 —— 依次下载多本书";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(560, 340);
            MinimumSize = new Size(480, 280);

            _hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(10, 8, 10, 0),
                Text = "每行一本（书名，或番茄书籍链接）。\n" +
                       "站点用主窗口上选的站点；也可以在某行前加前缀指定，例如「番茄：书名」或「biquga：书名」。\n" +
                       "已有章节会自动跳过（不会重下），单本失败不影响后面的书。",
            };

            _input = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                // ★ 同上：这个框里填的是**中文书名**，用 Consolas 会显示成方框。
                Font = UiFont.Mono(9.5F),
            };
            WatermarkExt.SetHint(_input, "牧神记\r\n全职高手\r\n番茄：某本番茄的书", this);
            // 带回去上次没下完的队列（队列是"睡前排 5 本"的用法，
            // 误点关闭或程序崩一次不该让排好的队全白费）
            try
            {
                var saved = Bookshelf.LoadQueue();
                if (saved.Count > 0)
                {
                    _input.Lines = saved.ToArray();
                    _hint.Text = "已带回上次保存的 " + saved.Count + " 项队列。\n" + _hint.Text;
                }
            }
            catch { }

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10, 6, 10, 6) };
            _run = new Button { Text = "开始排队下载", AutoSize = true, Dock = DockStyle.Right };
            _run.Click += (s, e) => StartRun();
            _close = new Button { Text = "关闭", AutoSize = true, Dock = DockStyle.Right };
            _close.Click += (s, e) => Close();
            bar.Controls.Add(_close);
            bar.Controls.Add(_run);

            Controls.Add(_input);
            Controls.Add(_hint);
            Controls.Add(bar);
            CancelButton = _close;

            // 用户关窗口 = 请求停止队列（当前这本下完就停）。用 FormClosing 而不是按钮点击，
            // 这样点右上角 ×、按 Alt+F4、按 Esc 三条路都能停 —— 否则"关掉了还在后台下"
            // 是最容易让人困惑的行为。
            // _submitted 用来区分两种关闭：用户关（要停）vs 点「开始」后程序自己关（不能停）。
            FormClosing += (s, e) =>
            {
                if (_running && !_submitted) _owner.RequestCancelFromDialog();
                // 用户关窗口时把当前内容存下来（下次打开带回）。
                // 点「开始」后的自动关闭不存 —— 那时队列已经在跑了，
                // 存下来会让下次打开又看到一份已经下完的清单。
                if (!_submitted) SaveQueueNow();
            };
        }

        /// <summary>把当前输入框内容存成队列文件（失败只记日志，不打扰用户）</summary>
        private void SaveQueueNow()
        {
            try
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (var raw in _input.Lines)
                {
                    var s = raw == null ? "" : raw.Trim();
                    if (s.Length > 0) list.Add(s);
                }
                Bookshelf.SaveQueue(list);
            }
            catch { }
        }

        private bool _running;
        private bool _submitted;

        private void StartRun()
        {
            var kw = new System.Collections.Generic.List<string>();
            foreach (var raw in _input.Lines)
            {
                var line = raw == null ? "" : raw.Trim();
                if (line.Length > 0) kw.Add(line);
            }
            if (kw.Count == 0)
            {
                MessageBox.Show(this, "请先粘贴至少一本书名（每行一本）。", "任务队列",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 站点前缀：允许「番茄：书名」「biquga：书名」覆盖主窗口的选择。
            // 做法是先切主窗口的站点下拉框，这样 RunQueue 里用的 _site 就是对的。
            var first = kw[0];
            int sep = first.IndexOf(':');
            if (sep < 0) sep = first.IndexOf('：');
            if (sep > 0)
            {
                var prefix = first.Substring(0, sep).Trim();
                int idx = SiteIndexForPrefix(prefix);
                if (idx >= 0) _owner.SelectSiteForTest(idx);
            }

            _run.Enabled = false;
            _running = true;
            _submitted = true;      // 先打标记再 Close()，否则 FormClosing 会把队列当场取消
            // 队列开始跑之后就把文件清掉：否则下次打开队列窗口会又看到一份已经下完的清单
            try { Bookshelf.SaveQueue(null); } catch { }
            _hint.Text = "正在排队下载…… 关闭本窗口会请求停止（当前这本下完就停）。";
            Close();

            // 交给主窗口跑：它已经有日志区、进度条和设置。
            // 这里用 BeginInvoke 是为了先让对话框关掉、界面刷新一下再开始。
            _owner.BeginInvoke(new Action(() => _owner.RunQueueFromDialog("队列下载中…", kw)));
        }

        private static int SiteIndexForPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return -1;
            var p = prefix.Trim().ToLowerInvariant();

            // 中文别名（"番茄" / "笔趣阁" 这种用户真会写的）
            if (p == "番茄" || p == "fanqie" || p == "tomato") return SiteRegistry.IndexOf("fanqie");
            if (p == "移动版" || p == "biquga-m") return SiteRegistry.IndexOf("biquga-m");
            if (p == "pc" || p == "pc版" || p == "biquga-pc") return SiteRegistry.IndexOf("biquga");
            if (p == "笔趣阁") return SiteRegistry.IndexOf("biquga");

            // 其余按注册表里的 Key 或显示名精确匹配 ——
            // 这样队列里写 "笔尖中文" 或 "xbiquwk" 都能切过去，加站点不用改这里。
            int byKey = SiteRegistry.IndexOf(prefix.Trim());
            if (byKey >= 0) return byKey;
            var all = SiteRegistry.All;
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].DisplayName, prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }

    /// <summary>
    /// 「书架」对话框：列出下载过的书，一键追更。
    ///
    /// 数据来自 exe 同目录的 书架.json（见 Bookshelf.cs）。追更时重新载入目录，
    /// 然后走和主界面「更新新章节」完全相同的路径（RunQueue 里的那段逻辑），
    /// 所以"书架追更"和"手动更新"结果一致。
    /// </summary>
    internal class BookshelfDialog : Form
    {
        private readonly MainForm _owner;
        private readonly ListView _list;
        private readonly Button _update, _open, _remove, _refresh, _close;
        private readonly Button _checkAll;
        private readonly TextBox _search;
        private Bookshelf _shelf;

        public BookshelfDialog(MainForm owner)
        {
            _owner = owner;
            Text = "本地书架 —— 下载过的书";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 420);
            MinimumSize = new Size(600, 320);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            _list.Columns.Add("书名", 240);
            _list.Columns.Add("作者", 110);
            _list.Columns.Add("站点", 100);
            _list.Columns.Add("已下载章数", 90);
            _list.Columns.Add("目录章数", 80);
            _list.Columns.Add("上次下载", 120);
            _list.DoubleClick += (s, e) => DoUpdate();

            // 搜索框：书多了（几十本）靠滚动找太难受
            var searchHost = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 4, 10, 2) };
            _search = new TextBox { Dock = DockStyle.Fill };
            _search.TextChanged += (s, e) => Reload();
            WatermarkExt.SetHint(_search, "输入书名/作者过滤…", this);
            searchHost.Controls.Add(_search);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10, 7, 10, 6),
                WrapContents = false,
            };
            _update = new Button { Text = "更新新章节", AutoSize = true };
            _update.Click += (s, e) => DoUpdate();
            _checkAll = new Button { Text = "检查所有更新", AutoSize = true };
            new ToolTip().SetToolTip(_checkAll,
                "逐本重新载入目录，对比「目录章数」和「已下载章数」，列出哪些书有新章节。\n" +
                "走目录缓存，所以很快；只检查，不下载。");
            _checkAll.Click += (s, e) => DoCheckAllUpdates();
            _open = new Button { Text = "打开目录", AutoSize = true };
            _open.Click += (s, e) => DoOpenFolder();
            _remove = new Button { Text = "从书架移除", AutoSize = true };
            _remove.Click += (s, e) => DoRemove();
            _refresh = new Button { Text = "刷新", AutoSize = true };
            _refresh.Click += (s, e) => Reload();
            _close = new Button { Text = "关闭", AutoSize = true };
            _close.Click += (s, e) => Close();
            bar.Controls.AddRange(new Control[] { _update, _checkAll, _open, _remove, _refresh, _close });

            Controls.Add(_list);
            Controls.Add(searchHost);
            Controls.Add(bar);
            CancelButton = _close;
            Reload();
        }

        private void Reload()
        {
            _shelf = Bookshelf.Load();
            var filter = _search == null ? "" : _search.Text.Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in _shelf.Sorted())
            {
                // 过滤：书名或作者里含关键词（不区分大小写）
                if (filter.Length > 0 &&
                    (e.Title ?? "").IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                    (e.Author ?? "").IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;

                // 「已下载章数」直接数文件里的锚点，而不是显示目录快照。
                // 原来的"章数"列其实是**上次载入目录时**的目录长度，
                // 和"这本实际下了多少章"不是一回事，很容易被误读。
                int have = CountDownloadedChapters(e.FilePath);

                var it = new ListViewItem(e.Title);
                it.SubItems.Add(string.IsNullOrEmpty(e.Author) ? "未知" : e.Author);
                it.SubItems.Add(SiteLabel(e.Site));
                it.SubItems.Add(have < 0 ? "?" : have.ToString());
                it.SubItems.Add(e.LastChapterCount.ToString());
                it.SubItems.Add(e.LastDownload == DateTime.MinValue ? "—" : e.LastDownload.ToString("yyyy-MM-dd HH:mm"));
                it.Tag = e;
                _list.Items.Add(it);
            }
            _list.EndUpdate();

            if (_list.Items.Count == 0)
            {
                _list.Items.Add(new ListViewItem(filter.Length > 0
                    ? "（没有匹配「" + filter + "」的书）"
                    : "（书架还是空的 —— 下载一本书之后就会自动记进来）"));
            }
        }

        /// <summary>
        /// 数已下载的章数：直接扫 txt 里的章节锚点。
        /// 数不出来返回 -1（旧格式文件没有锚点，或文件已被删除/移动）。
        /// </summary>
        private static int CountDownloadedChapters(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return -1;
                return ChapterIndex.Scan(filePath).Count;
            }
            catch { return -1; }
        }

        /// <summary>
        /// 「检查所有更新」：逐本重新载入目录，对比目录章数与已下载章数，列出有新章的书。
        /// 只检查不下载。走目录缓存，所以对大多数书是秒回。
        /// </summary>
        private void DoCheckAllUpdates()
        {
            if (_owner == null || _owner.IsDisposed) return;
            var entries = _shelf.Sorted();
            if (entries.Count == 0) { MessageBox.Show(this, "书架是空的。", "检查所有更新"); return; }

            _checkAll.Enabled = false;
            var lines = new List<string>();
            _owner.RunBackgroundPublic("检查所有书的更新…", () =>
            {
                int withNew = 0;
                foreach (var e in entries)
                {
                    try
                    {
                        var site = MainForm.MakeSite(e.Site);
                        var key = !string.IsNullOrEmpty(e.Url) ? e.Url : e.Key;
                        if (string.IsNullOrEmpty(key)) continue;

                        var stub = MainForm.FromAddressPublic(key);
                        if (stub == null) continue;

                        var book = site.LoadBook(stub, null);
                        if (book == null || book.Chapters == null) continue;

                        int dirCount = 0;
                        foreach (var c in book.Chapters) if (!c.IsVolume && !string.IsNullOrEmpty(c.Id)) dirCount++;

                        int have = CountDownloadedChapters(e.FilePath);
                        if (have < 0) continue;   // 旧格式或文件不在，跳过

                        if (dirCount > have)
                        {
                            withNew++;
                            var line = string.Format("《{0}》有新章节：已下载 {1} 章，站点现在 {2} 章（+{3}）",
                                book.Title, have, dirCount, dirCount - have);
                            lines.Add(line);
                            _owner.LogPublic(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        var line = string.Format("《{0}》检查失败：{1}", e.Title, ex.Message);
                        lines.Add(line);
                        _owner.LogPublic(line);
                    }
                }

                _owner.LogPublic(string.Format("检查完成：{0} 本书，其中 {1} 本有新章节。", entries.Count, withNew));
                _owner.UiInvokePublic(() =>
                {
                    _checkAll.Enabled = true;
                    if (lines.Count == 0)
                        MessageBox.Show(this, "所有书都检查过了，没有发现新章节。", "检查所有更新",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(this,
                            string.Join("\n", lines.ToArray()) + "\n\n（要继续下载，选中那一本后点「更新新章节」，或在主界面点「书架」→ 双击）",
                            "检查所有更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            });
        }

        private static string SiteLabel(string key)
        {
            // 书架里的 Site 字符串可能来自更老的版本（"biquga-pc" 之类），
            // 注册表里没有就原样显示，别把它吞掉。
            return SiteRegistry.LabelOf(key);
        }

        private Bookshelf.Entry Selected
        {
            get
            {
                if (_list.SelectedItems.Count == 0) return null;
                return _list.SelectedItems[0].Tag as Bookshelf.Entry;
            }
        }

        private void DoOpenFolder()
        {
            var e = Selected;
            if (e == null) { MessageBox.Show(this, "请先在列表里选一本书。", "书架"); return; }
            try
            {
                var dir = string.IsNullOrEmpty(e.FilePath) ? null : Path.GetDirectoryName(e.FilePath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show(this, "找不到这本书的目录（可能已经被移动或删除）。", "书架",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败：" + ex.Message, "书架");
            }
        }

        private void DoRemove()
        {
            var e = Selected;
            if (e == null) { MessageBox.Show(this, "请先在列表里选一本书。", "书架"); return; }
            if (MessageBox.Show(this, "只从书架移除记录，**不会删除已下载的文件**。\n\n确定移除《" + e.Title + "》？",
                    "书架", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _shelf.Remove(e.Site, e.Key);
            _shelf.Save();
            Reload();
        }

        /// <summary>追更：重新拉目录 → 只补新章节。复用主窗口的队列下载逻辑。</summary>
        private void DoUpdate()
        {
            var e = Selected;
            if (e == null) { MessageBox.Show(this, "请先在列表里选一本书。", "书架"); return; }
            if (_owner == null || _owner.IsDisposed) return;

            // 不能直接用书名搜：站点上可能有重名书，搜出来的第一条未必是这本。
            // 有详情页地址时优先用它（biquga 的记录一定带 Url，番茄带 book_id）。
            var key = !string.IsNullOrEmpty(e.Url) ? e.Url : e.Key;
            if (string.IsNullOrEmpty(key))
            {
                MessageBox.Show(this, "这条记录里没有可用的地址，请用主界面重新搜一次这本书。", "书架");
                return;
            }

            // 把主窗口切到这本书所属的站点，这样 _site / _profile 都是对的。
            // 老书架记录里的 Key 可能已经不在注册表里了（比如站点被下线）——
            // 那时退回第 0 项，不当场报错，让用户自己重新选。
            int idx = SiteRegistry.IndexOf(e.Site);
            if (idx < 0) idx = 0;
            _owner.SelectSiteForTest(idx);

            _update.Enabled = false;
            Hide();
            _owner.BeginInvoke(new Action(() => _owner.RunQueueFromDialog("书架追更中…",
                new System.Collections.Generic.List<string> { key })));
            Close();
        }
    }

    /// <summary>
    /// 给空文本框加水印提示。
    ///
    /// 老实现是在窗体上摆一个灰色 Label 假装水印，问题有两个：
    ///   1. 标签是按坐标硬算的，窗口一缩放/换 DPI 就和别的控件重叠（用户截图里那处遮挡就是它）；
    ///   2. 标签其实是独立控件，点它/它盖住谁都不受输入框控制。
    /// 现在改用系统原生能力 EM_SETCUEBANNER（Vista+ 的"提示横幅"），
    /// 由控件自己绘制，不产生任何额外控件 —— 从根上不可能再遮挡。
    /// 保留原方法名，调用方不用改。
    /// </summary>
    internal static class WatermarkExt
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>owner 参数保留是为了兼容老调用；原生水印不需要它</summary>
        public static void SetHint(TextBox box, string text, Form owner)
        {
            if (box == null) return;
            box.HandleCreated -= OnHandleCreated;
            box.HandleCreated += OnHandleCreated;
            box.Tag = text;                 // 记住文案，句柄重建后要重新设置
            if (box.IsHandleCreated) Apply(box);
        }

        private static void OnHandleCreated(object sender, EventArgs e)
        {
            Apply(sender as TextBox);
        }

        private static void Apply(TextBox box)
        {
            if (box == null || !box.IsHandleCreated) return;
            var text = box.Tag as string;
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                // true = 控件获得焦点时也显示（和常见水印行为一致）
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            }
            catch { /* 老系统不支持就静默降级：没有水印，但绝不遮挡任何东西 */ }
        }
    }
}
