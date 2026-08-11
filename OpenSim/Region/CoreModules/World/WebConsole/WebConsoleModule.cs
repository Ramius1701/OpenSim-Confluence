using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;

namespace OpenSim.Region.CoreModules.World.WebConsole
{
    // Web-based region console (task #26 from the WhiteCore-Dev re-audit's
    // "all of it" list) - lets a grid admin run console commands against a
    // specific region from the WebInterface instead of needing shell/RDP
    // access to that region's own physical console. WhiteCore's own
    // equivalent page is a documented stub ("hardcodes 'not yet
    // implemented' despite calling MainConsole.Instance.RunCommand()" - see
    // FEATURES_VS_MASTER.md); this is a genuinely working implementation,
    // not matching that gap.
    //
    // Security note - this is the single most sensitive endpoint added this
    // session: successful auth here means arbitrary console command
    // execution on this region process, equivalent to physical/RDP console
    // access. Shipped disabled by default and refuses to enable at all
    // unless a non-empty SharedSecret is configured (an empty/missing
    // secret is treated as "not configured", not "no auth required") -
    // deliberately stricter than the no-auth-at-all precedent this
    // session's other Robust<->region internal HTTP endpoints
    // (/griduser, /mutelist) already use, because read/write-a-record and
    // arbitrary-command-execution are not the same risk tier.
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WebConsoleModule")]
    public class WebConsoleModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_enabled;
        private string m_sharedSecret = string.Empty;
        private bool m_registered;

        public string Name => "WebConsoleModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["WebConsole"];
            if (config == null)
                return;

            if (!config.GetBoolean("Enabled", false))
                return;

            m_sharedSecret = config.GetString("SharedSecret", string.Empty);
            if (string.IsNullOrEmpty(m_sharedSecret))
            {
                m_log.Error("[WEB CONSOLE]: Enabled = true but no SharedSecret configured - refusing to start an unauthenticated remote command endpoint. Set [WebConsole] SharedSecret to a real value.");
                return;
            }

            m_enabled = true;
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled || m_registered)
                return;

            try
            {
                // One shared HTTP handler per process, not per-region - the
                // same MainServer.Instance is shared across every region in
                // this process, matching the pattern ConfluenceCurrencyModule's
                // /currency.php registration already uses.
                MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/consoleweb", HandleConsoleWeb));
                m_registered = true;

                m_log.Info("[WEB CONSOLE]: Enabled at /consoleweb");
            }
            catch (Exception e)
            {
                m_log.Error("[WEB CONSOLE]: DIAGNOSTIC - exception registering handler", e);
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
        }

        private void HandleConsoleWeb(IOSHttpRequest request, IOSHttpResponse response)
        {
            string providedSecret = request.Headers["x-console-secret"];
            if (string.IsNullOrEmpty(providedSecret) || providedSecret != m_sharedSecret)
            {
                response.StatusCode = 403;
                response.RawBuffer = Encoding.UTF8.GetBytes("Forbidden");
                return;
            }

            string body;
            using (StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();

            Dictionary<string, object> parsed = ServerUtils.ParseQueryString(body);
            string command = parsed.TryGetValue("command", out object cmdObj) ? cmdObj?.ToString() : null;

            if (string.IsNullOrWhiteSpace(command))
            {
                response.StatusCode = 400;
                response.RawBuffer = Encoding.UTF8.GetBytes("No command given");
                return;
            }

            string output = RunCapturedCommand(command);

            response.StatusCode = 200;
            response.ContentType = "text/plain";
            response.RawBuffer = Encoding.UTF8.GetBytes(output);
        }

        // Swaps MainConsole.Instance for a non-interactive, output-capturing
        // wrapper around the SAME already-populated Commands registry for
        // the duration of one command, then restores the original - this is
        // the only way to get a command's output back as a string, since
        // nothing in the existing console framework supports that directly
        // (LocalConsole.Output never fires ICommandConsole.OnOutput, and
        // there's no TextWriter-redirectable sink anywhere in it).
        private static string RunCapturedCommand(string command)
        {
            ICommandConsole original = MainConsole.Instance;
            if (original == null)
                return "(no console available in this process)";

            CapturingConsole capture = new CapturingConsole(original);
            MainConsole.Instance = capture;

            try
            {
                capture.RunCommand(command);
            }
            catch (Exception e)
            {
                capture.Output("Exception running command: " + e.Message);
            }
            finally
            {
                MainConsole.Instance = original;
            }

            return capture.CapturedText;
        }

        // Non-interactive by design: every Prompt/ReadLine overload returns
        // immediately with a safe default rather than blocking, since there
        // is no human on the other end of a web request to answer a
        // confirmation prompt - a command that would normally pause to ask
        // "are you sure? (yes/no)" must not hang this HTTP request forever.
        // Output is captured AND forwarded to the real console, so an
        // operator watching the physical console still sees every command
        // run through this endpoint - not a silent side channel.
        private class CapturingConsole : ICommandConsole
        {
            private readonly ICommandConsole m_inner;
            private readonly StringBuilder m_buffer = new StringBuilder();

            public CapturingConsole(ICommandConsole inner)
            {
                m_inner = inner;
            }

            public string CapturedText => m_buffer.ToString();

            public event OnOutputDelegate OnOutput
            {
                add { }
                remove { }
            }

            public ICommands Commands => m_inner.Commands;

            public string DefaultPrompt
            {
                get => m_inner.DefaultPrompt;
                set { }
            }

            public IScene ConsoleScene
            {
                get => m_inner.ConsoleScene;
                set { }
            }

            public void RunCommand(string cmd)
            {
                Commands.Resolve(Parser.Parse(cmd));
            }

            public void Output(string format)
            {
                m_buffer.AppendLine(format);
                m_inner.Output(format);
            }

            public void Output(string format, params object[] components)
            {
                string formatted = components == null || components.Length == 0
                        ? format
                        : string.Format(format, components);
                m_buffer.AppendLine(formatted);
                m_inner.Output(formatted);
            }

            public void Prompt()
            {
            }

            public string Prompt(string p) => string.Empty;
            public string Prompt(string p, string def) => def;
            public string Prompt(string p, List<char> excludedCharacters) => string.Empty;
            public string Prompt(string p, string def, List<char> excludedCharacters, bool echo = true) => def;
            public string Prompt(string prompt, string defaultresponse, List<string> options) => defaultresponse;

            public string ReadLine(string p, bool isCommand, bool e) => string.Empty;

            public void ReadConfig(IConfigSource configSource)
            {
            }

            public void SetCntrCHandler(OnCntrCCelegate handler)
            {
            }
        }
    }
}
