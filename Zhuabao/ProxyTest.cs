using System;
using System.Net;
using Zhuabao.Core;

namespace Zhuabao
{
    /// <summary>
    /// --proxytest：验证系统代理接管闭环（不启动 GUI）。
    /// TakeOver → 检查注册表 → 检查 WinINET 视角 → Restore → 检查已还原。
    /// </summary>
    internal static class ProxyTest
    {
        public static int Run()
        {
            int fails = 0;
            void Check(bool ok, string name)
            {
                Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name);
                if (!ok) fails++;
            }

            var orig = SystemProxy.ReadCurrent();
            Console.WriteLine($"原始: Enable={orig.Enabled} Server={orig.Server ?? "(无)"}");

            // 1. 接管
            SystemProxy.TakeOver("127.0.0.1:8899");
            var now = SystemProxy.ReadCurrent();
            Check(now.Enabled && now.Server == "127.0.0.1:8899", "接管后注册表已指向 127.0.0.1:8899");

            // 2. WinINET 视角（本进程内读取系统代理，InternetSetOption 刷新后立即生效）
            var wp = WebRequest.GetSystemWebProxy();
            var target = new Uri("http://127.0.0.1:18080/x");
            var via = wp.GetProxy(target);
            Console.WriteLine("WinINET GetProxy -> " + via);
            Check(via.ToString().Contains(":8899"), "WinINET 认为应经 8899 代理");

            // 3. 恢复
            SystemProxy.Restore();
            var back = SystemProxy.ReadCurrent();
            Check(back.Enabled == orig.Enabled && back.Server == orig.Server, "恢复后与原始设置一致");

            Console.WriteLine(fails == 0 ? "ALL PROXY TESTS PASSED" : fails + " PROXY TEST(S) FAILED");
            return fails == 0 ? 0 : 1;
        }
    }
}
