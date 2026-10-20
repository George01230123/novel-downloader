using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TomatoBiquga
{
    /// <summary>
    /// 「后台下载不减速 / 不中断」的两个系统调用。
    ///
    /// 用户反馈：**应用放到后台下载会停止或速度特别慢**。
    /// Windows 上这有两个真实机制（都不是程序 bug）：
    ///
    ///   ① **Power Throttling（EcoQoS）**：从 Win10 1709 起，系统会把"不在前台"的进程
    ///      标记为低功耗，CPU 频率和调度优先级都可能被压低。纯网络 I/O 的负载受影响
    ///      通常不大，但如果同时在做解压/清洗/写盘（我们确实在做），就会明显变慢。
    ///      解法：`SetProcessInformation(ProcessPowerThrottling)` 把
    ///      `EXECUTION_SPEED` 关掉，等于告诉系统"别把我当后台任务压"。
    ///   ② **系统睡眠/待机**：下载跑几十分钟，中间没人动键鼠，机器可能进睡眠，
    ///      网络中断 → 下载"停了"。解法：`SetThreadExecutionState`
    ///      带 `ES_SYSTEM_REQUIRED | ES_CONTINUOUS`，下载期间阻止自动睡眠；
    ///      下载结束立刻恢复（绝不能长期持有，那等于改了用户的电源策略）。
    ///
    /// 设计红线：
    ///   · **任何失败都静默降级** —— 老系统（Win7）没有这些 API、受限权限也可能失败，
    ///     但绝不能让程序起不来，也不能弹窗吓用户；
    ///   · 阻止睡眠是**成对**调用的（Begin/End），用计数支持嵌套，绝不能漏 End；
    ///   · 不修改任何系统设置、不写注册表 —— 只影响**本进程**运行期。
    /// </summary>
    internal static class AppPower
    {
        // ---------------- P/Invoke ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;        // 必须 = 1
            public uint ControlMask;    // 要改哪些位
            public uint StateMask;      // 改成什么（0 = 关闭）
        }

        private const int ProcessPowerThrottling = 4;
        private const uint THROTTLING_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        // ---------------- 状态（只读给测试/日志用） ----------------

        private static int _awakeDepth;          // Begin/End 嵌套计数
        private static readonly object _lock = new object();

        /// <summary>是否已经成功关掉执行速度节流（false = 不支持/被拒绝/尚未调用）</summary>
        internal static bool ThrottlingDisabled { get; private set; }

        /// <summary>是否正处于"阻止系统睡眠"状态</summary>
        internal static bool IsKeepingAwake { get { lock (_lock) { return _awakeDepth > 0; } } }

        /// <summary>当前嵌套层数（测试用）</summary>
        internal static int AwakeDepthForTest { get { lock (_lock) { return _awakeDepth; } } }

        /// <summary>最后一次失败原因（只进日志，不弹窗）</summary>
        internal static string LastError { get; private set; }

        /// <summary>
        /// 关掉本进程的 Power Throttling（EcoQoS）。**幂等**，可重复调用。
        /// 返回是否成功；失败也安全（只是保持系统默认行为）。
        /// </summary>
        internal static bool DisablePowerThrottling()
        {
            if (ThrottlingDisabled) return true;
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = THROTTLING_VERSION,
                    // 同时管"执行速度"和"定时器分辨率"：
                    // 后者会让我们依赖的短 Sleep（进度刷新、重试间隔）被拉长成 15.6ms 的倍数。
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                                | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    StateMask = 0,       // 0 = 关闭这些节流
                };
                bool ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                    ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
                if (ok) { ThrottlingDisabled = true; LastError = null; return true; }

                LastError = "SetProcessInformation 失败（错误码 " + Marshal.GetLastWin32Error()
                          + "，系统可能太旧或权限受限）";
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 开始"下载期间别让系统睡觉"。可嵌套；必须与 <see cref="KeepAwakeEnd"/> 成对。
        /// 失败静默（老系统不支持就算了，不能因此影响下载）。
        /// </summary>
        internal static bool KeepAwakeBegin()
        {
            lock (_lock)
            {
                try
                {
                    if (_awakeDepth == 0)
                    {
                        // ES_CONTINUOUS：让这个状态一直有效，直到我们再调一次把它清掉
                        // ES_SYSTEM_REQUIRED：阻止系统自动睡眠
                        // 不加 ES_DISPLAY_REQUIRED：不阻止息屏（用户可能就是要关屏挂着下）
                        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                    }
                    _awakeDepth++;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>结束"阻止睡眠"。计数归零时恢复系统默认（只传 ES_CONTINUOUS）。</summary>
        internal static bool KeepAwakeEnd()
        {
            lock (_lock)
            {
                if (_awakeDepth > 0) _awakeDepth--;
                try
                {
                    if (_awakeDepth == 0) SetThreadExecutionState(ES_CONTINUOUS);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>测试用：把计数清零（不影响系统状态）</summary>
        internal static void ResetForTest()
        {
            lock (_lock) { _awakeDepth = 0; }
        }
    }
}
