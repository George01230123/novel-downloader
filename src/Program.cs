using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TomatoBiquga
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        /// <summary>
        /// 把本进程挂到父进程的控制台上（-1 = ATTACH_PARENT_PROCESS）。
        /// GUI 子系统进程默认没有控制台，`--selftest` 要看输出就必须先挂上去。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        private static void Main(string[] args)
        {
            // ★ 命令行自测：`小说下载器.exe --selftest`
            //
            // 这段必须用 #if SELFTEST 隔离 —— 不是保险，是必需：
            // Program.cs 被 7 个编译目标共用（GUI + _selftest + _edgetest + _layoutprobe
            // + _textfit + _aimock + _liveprobe），而 OfflineTests.cs 只编进 GUI 主程序
            // 与 _offlinetests.exe。Program.cs 本身是共用的，所以它**不能引用**只在某一个
            // 目标里存在的类型。实测连踩两次：
            //   ① 直接调 OfflineTests.Run → _selftest.exe 报 CS0103 找不到 OfflineTests；
            //   ② 挪进 SelfTestEntry.cs     → 又报找不到 SelfTestEntry（同一原因）。
            // 正解：只有 GUI 目标带 SELFTEST 常量（build.bat 与 *.csproj **两处**都要改）。
            //
            // 收益：**"测试跑通的那个二进制"和"用户手里这个二进制"从此是同一个文件**。
#if SELFTEST
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                // ★ GUI 子系统（winexe）的进程**默认没有控制台**，
                //   Console.set_OutputEncoding 会抛 IOException（"句柄无效"，退出码 0xE0434352）。
                //   必须先 attach 到父进程（调用它的那个 cmd/PowerShell）的控制台。
                try { AttachConsole(-1); } catch { }
                // ★ 编码由 ConsoleSetup 统一处理：它既切控制台代码页（真实控制台那条路径），
                //   也设 Console.OutputEncoding（重定向那条路径）。原来只做后者，
                //   结果用户在 cmd 里看到的中文全是乱码。
                ConsoleSetup.UseUtf8();

                // ★ GUI(winexe) 进程默认没有控制台，Windows 还会把它的 stdout 句柄置空 ——
                //   于是"在 cmd 里跑 --selftest"什么也看不到（退出码 0、输出 0 字节）。
                //   能挂到父控制台就挂；挂不上就把结果**同时**写一份日志文件，
                //   免得用户以为程序没跑。
                string logPath = null;
                if (!ConsoleSetup.StdoutIsRedirected() && !ConsoleSetup.AttachForOutput())
                {
                    logPath = ConsoleSetup.ResolveResultFile("自测结果.txt");
                    if (ConsoleSetup.TeeToFile(logPath) == null) logPath = null;
                }

                // ★ 跳过「布局体检」那一条 —— 详见 OfflineTests.Run 里 --skip-layout 的注释：
                //   它会真的 new 一个 MainForm 去量控件，量出来的尺寸依赖进程的 DPI 环境，
                //   于是同一个断言在控制台进程通过、在 GUI 进程失败。
                //   布局另有 _layoutprobe.exe / _textfit.exe 两个专门的探针在 CI 里守着。
                var inner = new string[(args == null ? 0 : args.Length) + 1];
                if (args != null) Array.Copy(args, inner, args.Length);
                inner[inner.Length - 1] = "--skip-layout";

                int code;
                try { code = OfflineTests.Run(inner); }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine("自测异常：" + ex); } catch { }
                    code = 1;
                }

                // 既没有控制台、也没有重定向时，用弹窗把结论告诉用户 ——
                // 否则他（双击 exe 的情形）跑完只看到"什么都没发生"。
                //
                // ★ 弹窗的判据要够硬：**只有"输出真的无处可去"时才弹**。
                //   踩过两次坑：
                //     ① 只判 Environment.UserInteractive → 这台机器上恒为 true，
                //        于是从 cmd 里跑也会弹窗；
                //     ② 弹窗是**模态**的，无人值守时没人在屏幕前点"确定"，
                //        会永久阻塞、把自动化流程挂死（实测挂了 573 秒才被强杀）。
                //   所以要求：没有控制台窗口（双击才有这情形）+ 没有重定向 + 有交互桌面。
                //   从 cmd 里跑时用户已在窗口里看得到结果，不需要弹窗。
                bool nowhereToShow = logPath != null
                    && !ConsoleSetup.HasConsoleWindow()
                    && !ConsoleSetup.StdoutIsRedirected()
                    && Environment.UserInteractive
                    && SystemInformation.UserInteractive;
                if (nowhereToShow)
                {
                    try
                    {
                        MessageBox.Show(
                            (code == 0 ? "自测通过。" : "自测失败（退出码 " + code + "）。") +
                            "\n\n完整结果已写入：\n" + logPath,
                            "小说下载器 · 自测",
                            MessageBoxButtons.OK,
                            code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    }
                    catch { }
                }

                Environment.ExitCode = code;
                return;
            }
#endif

            // ★ 录制 / 回放（开发与调规则用，正常启动不传参数时行为完全不变）
            //
            //   小说下载器.exe --record D:\rec    抓到的每个响应存进 D:\rec
            //   小说下载器.exe --replay D:\rec    只读 D:\rec，命中就不联网
            //   小说下载器.exe --replay D:\rec --replay-strict   未命中直接报错
            //
            //   为什么放在这里（而不是只给探针用）：探针是独立进程、自己 new 的，
            //   写到 Http.RecordDir 就够；但**程序本身**也要能录 —— 真实下载一遍
            //   才能录到"下载路径"用到的那些请求。
            try { ApplyRecordReplayArgs(args); }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("录制/回放参数解析失败（已忽略）：" + ex.Message); } catch { }
            }

            EnableHighDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序出错：" + ex.Message, "小说下载器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 解析 `--record &lt;dir&gt;` / `--replay &lt;dir&gt;` / `--replay-strict`。
        ///
        /// 抽成独立方法是为了**可测**：参数解析错了不会崩，但"静默不生效"最难查
        /// （录了半小时发现录的是默认目录）。所以逻辑单独拎出来，离线单测直接调。
        /// </summary>
        internal static void ApplyRecordReplayArgs(string[] args)
        {
            if (args == null) return;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] == null ? "" : args[i].Trim();
                if (string.Equals(a, "--record", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-record", StringComparison.OrdinalIgnoreCase))
                {
                    string d = i + 1 < args.Length ? (args[i + 1] == null ? "" : args[i + 1].Trim()) : "";
                    if (d.Length > 0) { Http.RecordDir = d; i++; }
                }
                else if (string.Equals(a, "--replay", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(a, "-replay", StringComparison.OrdinalIgnoreCase))
                {
                    string d = i + 1 < args.Length ? (args[i + 1] == null ? "" : args[i + 1].Trim()) : "";
                    if (d.Length > 0) { Http.ReplayDir = d; i++; }
                }
                else if (string.Equals(a, "--replay-strict", StringComparison.OrdinalIgnoreCase))
                {
                    Http.ReplayStrict = true;
                }
                  // --proxy http://127.0.0.1:7897：命令行覆盖 settings.ini 里的 proxy（给探针/CI 用）
                  else if (string.Equals(a, "--proxy", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(a, "-proxy", StringComparison.OrdinalIgnoreCase))
                  {
                      string p = i + 1 < args.Length ? (args[i + 1] == null ? "" : args[i + 1].Trim()) : "";
                      if (p.Length > 0) { Http.Proxy = p; i++; }
                  }
            }
        }

        /// <summary>
        /// 高分屏适配。build.bat 用 csc 直接编译时不带清单（app.manifest 只在
        /// dotnet build 那一路上），所以代码里再兜一次，保证两条编译路径表现一致。
        /// </summary>
        private static void EnableHighDpi()
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();
            }
            catch { /* 老系统上失败不影响运行 */ }
        }
    }
}
