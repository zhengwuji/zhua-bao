using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jint;
using Jint.Native;

namespace Zhuabao.Core
{
    /// <summary>
    /// 脚本引擎：JS 通过 zb.request / zb.response 对象改包（仿 Fatbeans 脚本模型）。
    /// </summary>
    public sealed class JsScriptEngine : IDisposable
    {
        private Engine _engine;

        public JsScriptEngine()
        {
            _engine = new Engine(o => o.LimitRecursion(64).TimeoutInterval(TimeSpan.FromSeconds(3)));
        }

        public void RunRequestScript(string code, HttpRequest req, HttpResponse resp, Dictionary<string, string> vars)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var engine = NewEngine();
            var reqObj = new JsHttpMessage(engine, req);
            engine.SetValue("zb", new
            {
                request = reqObj.Api,
                response = (object)null,
                vars = (object)(vars ?? new Dictionary<string, string>()),
                log = new Action<object>(o => Log.I("脚本", o?.ToString() ?? ""))
            });
            engine.Execute(code);
        }

        public void RunResponseScript(string code, HttpRequest req, HttpResponse resp, Dictionary<string, string> vars)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var engine = NewEngine();
            engine.SetValue("zb", new
            {
                request = req == null ? null : new JsHttpMessage(engine, req).Api,
                response = resp == null ? null : new JsHttpMessage(engine, resp).Api,
                vars = (object)(vars ?? new Dictionary<string, string>()),
                log = new Action<object>(o => Log.I("脚本", o?.ToString() ?? ""))
            });
            engine.Execute(code);
        }

        private Engine NewEngine() => _engine ??= new Engine(o => o.LimitRecursion(64).TimeoutInterval(TimeSpan.FromSeconds(3)));

        public void Dispose() { _engine = null; }
    }

    internal sealed class JsHttpMessage
    {
        private readonly Engine _engine;
        private readonly HttpMessage _msg;
        public JsValue Api { get; }

        public JsHttpMessage(Engine engine, HttpMessage msg)
        {
            _engine = engine;
            _msg = msg;

            dynamic o = new JObjectWrapper();
            var obj = new
            {
                getHeader = new Func<string, string>(h => msg.Headers.TryGetValue(h, out var v) ? v : null),
                setHeader = new Action<string, string>((h, v) => msg.Headers[h] = v),
                removeHeader = new Action<string>(h => msg.Headers.Remove(h)),
                getBodyText = new Func<string>(() => msg.GetBodyText()),
                setBodyText = new Action<string>(t => { msg.SetBody(t); msg.Headers["Content-Length"] = msg.Body.Length.ToString(); }),
                headers = msg.Headers,
                body = msg.GetBodyText()
            };
            Api = JsValue.FromObject(engine, obj);
        }

        private class JObjectWrapper { }
    }
}
