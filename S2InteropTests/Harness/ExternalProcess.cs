/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of the WWCP S2 conformance tests <https://github.com/OpenChargingCloud/S2ConformanceTests>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Diagnostics;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// A driver process of a reference implementation (a Python script or a Rust binary)
    /// that talks JSON lines: every stdout line that is a JSON object is an event the tests
    /// can wait for, every other stdout line and all of stderr is kept as diagnostics.
    /// Commands are written to stdin as JSON lines. Disposing kills the process.
    /// </summary>
    public sealed class ExternalProcess : IAsyncDisposable
    {

        #region Data

        private readonly Process                      process;
        private readonly List<JObject>                events    = [];
        private readonly List<String>                 log       = [];
        private readonly List<TaskCompletionSource>   waiters   = [];
        private readonly Lock                         gate      = new ();
        private readonly TaskCompletionSource<Int32>  exited    = new (TaskCreationOptions.RunContinuationsAsynchronously);
        private          Boolean                      disposed;

        #endregion

        #region Properties

        /// <summary>
        /// A short name for messages.
        /// </summary>
        public String   Name        { get; }

        /// <summary>
        /// Whether the process has exited.
        /// </summary>
        public Boolean  HasExited
            => process.HasExited;

        /// <summary>
        /// A task completing with the exit code.
        /// </summary>
        public Task<Int32>  Exited
            => exited.Task;

        /// <summary>
        /// All JSON events received so far.
        /// </summary>
        public IReadOnlyList<JObject>  Events
        {
            get
            {
                lock (gate)
                    return [.. events];
            }
        }

        /// <summary>
        /// stderr and the non-JSON stdout lines, for failure messages.
        /// </summary>
        public String  Diagnostics
        {
            get
            {
                lock (gate)
                    return String.Join(Environment.NewLine, log);
            }
        }

        #endregion

        #region Constructor(s)

        private ExternalProcess(String   Name,
                                Process  Process)
        {
            this.Name     = Name;
            this.process  = Process;
        }

        #endregion


        #region (static) Start(Name, FileName, Arguments, WorkingDirectory = null, Environment = null)

        /// <summary>
        /// Start a driver process.
        /// </summary>
        /// <param name="Name">A short name for messages.</param>
        /// <param name="FileName">The executable.</param>
        /// <param name="Arguments">The arguments.</param>
        /// <param name="WorkingDirectory">An optional working directory.</param>
        /// <param name="Environment">Optional additional environment variables.</param>
        public static ExternalProcess Start(String                                Name,
                                            String                                FileName,
                                            IEnumerable<String>                   Arguments,
                                            String?                               WorkingDirectory   = null,
                                            IReadOnlyDictionary<String, String>?  Environment        = null)
        {

            var arguments  = Arguments.ToList();

            var startInfo  = new ProcessStartInfo(FileName) {
                                 UseShellExecute         = false,
                                 CreateNoWindow          = true,
                                 RedirectStandardInput   = true,
                                 RedirectStandardOutput  = true,
                                 RedirectStandardError   = true,
                                 StandardInputEncoding   = new UTF8Encoding(false),
                                 StandardOutputEncoding  = Encoding.UTF8,
                                 StandardErrorEncoding   = Encoding.UTF8
                             };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            if (WorkingDirectory is not null)
                startInfo.WorkingDirectory = WorkingDirectory;

            if (Environment is not null)
                foreach (var (key, value) in Environment)
                    startInfo.Environment[key] = value;

            var process  = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var wrapper  = new ExternalProcess(Name, process);

            process.OutputDataReceived += (sender, e) => wrapper.OnStdOut(e.Data);
            process.ErrorDataReceived  += (sender, e) => wrapper.OnStdErr(e.Data);
            process.Exited             += (sender, e) => {
                                              try
                                              {
                                                  wrapper.exited.TrySetResult(process.ExitCode);
                                              }
                                              catch (Exception)
                                              {
                                                  wrapper.exited.TrySetResult(-1);
                                              }
                                              wrapper.WakeWaiters();
                                          };

            TestContext.Progress.WriteLine($"[{Name}] starting: {FileName} {String.Join(' ', arguments)}");

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return wrapper;

        }

        #endregion


        #region (private) OnStdOut / OnStdErr / WakeWaiters

        private void OnStdOut(String? Line)
        {

            if (Line is null)
                return;

            var text = Line.Trim();

            if (text.StartsWith('{') && text.EndsWith('}'))
            {
                try
                {

                    var json = JsonText.ParseObject(text);

                    lock (gate)
                        events.Add(json);

                    TestContext.Progress.WriteLine($"[{Name}] {text}");
                    WakeWaiters();
                    return;

                }
                catch (JsonException)
                {
                    // Not JSON after all: keep as a log line.
                }
            }

            lock (gate)
                log.Add("out: " + Line);

            TestContext.Progress.WriteLine($"[{Name}] {Line}");

        }

        private void OnStdErr(String? Line)
        {

            if (Line is null)
                return;

            lock (gate)
                log.Add("err: " + Line);

            TestContext.Progress.WriteLine($"[{Name}!] {Line}");

        }

        private void WakeWaiters()
        {

            List<TaskCompletionSource> pending;

            lock (gate)
            {
                pending = [.. waiters];
                waiters.Clear();
            }

            foreach (var waiter in pending)
                waiter.TrySetResult();

        }

        #endregion


        #region SendAsync(Command) / SendLineAsync(Line)

        /// <summary>
        /// Write a JSON command as one line to stdin.
        /// </summary>
        /// <param name="Command">The command.</param>
        public Task SendAsync(JObject Command)
            => SendLineAsync(Command.ToString(Formatting.None));

        /// <summary>
        /// Write one line to stdin.
        /// </summary>
        /// <param name="Line">The line (without a line break).</param>
        public async Task SendLineAsync(String Line)
        {
            TestContext.Progress.WriteLine($"[{Name}<] {Line}");
            await process.StandardInput.WriteAsync(Line + "\n");
            await process.StandardInput.FlushAsync();
        }

        #endregion

        #region WaitForEventAsync(Predicate, Timeout = null, Description = null)

        /// <summary>
        /// Wait for the first event (already received or arriving later) matching the predicate.
        /// Throws a TimeoutException with the diagnostics of the process otherwise.
        /// </summary>
        /// <param name="Predicate">The event filter.</param>
        /// <param name="Timeout">An optional timeout (default: 15 seconds).</param>
        /// <param name="Description">What is awaited, for the timeout message.</param>
        public async Task<JObject> WaitForEventAsync(Func<JObject, Boolean>  Predicate,
                                                     TimeSpan?               Timeout       = null,
                                                     String?                 Description   = null)
        {

            var timeout   = Timeout ?? TimeSpan.FromSeconds(15);
            var deadline  = DateTimeOffset.UtcNow + timeout;
            var index     = 0;

            while (true)
            {

                TaskCompletionSource waiter;

                lock (gate)
                {

                    for (; index < events.Count; index++)
                        if (Predicate(events[index]))
                            return events[index];

                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Add(waiter);

                }

                if (process.HasExited)
                {

                    // The last lines may still be in flight after the exit: drain for a moment.
                    for (var attempt = 0; attempt < 20; attempt++)
                    {

                        lock (gate)
                            for (; index < events.Count; index++)
                                if (Predicate(events[index]))
                                    return events[index];

                        await Task.Delay(100);

                    }

                    throw new InvalidOperationException($"[{Name}] exited with code {process.ExitCode} before '{Description ?? "the awaited event"}' arrived.{Environment.NewLine}Events:{Environment.NewLine}{String.Join(Environment.NewLine, Events.Select(e => e.ToString(Formatting.None)))}{Environment.NewLine}{Diagnostics}");

                }

                var remaining = deadline - DateTimeOffset.UtcNow;

                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException($"[{Name}] '{Description ?? "the awaited event"}' did not arrive within {timeout}.{Environment.NewLine}Events:{Environment.NewLine}{String.Join(Environment.NewLine, Events.Select(e => e.ToString(Formatting.None)))}{Environment.NewLine}{Diagnostics}");

                await Task.WhenAny(waiter.Task, Task.Delay(remaining));

            }

        }

        /// <summary>
        /// Wait for the first event with the given "event" property value.
        /// </summary>
        /// <param name="EventName">The event name.</param>
        /// <param name="Timeout">An optional timeout (default: 15 seconds).</param>
        public Task<JObject> WaitForEventAsync(String     EventName,
                                               TimeSpan?  Timeout   = null)
            => WaitForEventAsync(e => e.Value<String>("event") == EventName, Timeout, $"event '{EventName}'");

        #endregion

        #region WaitForExitAsync(Timeout = null)

        /// <summary>
        /// Wait for the process to exit and return its exit code.
        /// </summary>
        /// <param name="Timeout">An optional timeout (default: 15 seconds).</param>
        public async Task<Int32> WaitForExitAsync(TimeSpan? Timeout = null)
        {

            var timeout = Timeout ?? TimeSpan.FromSeconds(15);

            try
            {
                return await exited.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"[{Name}] did not exit within {timeout}.{Environment.NewLine}{Diagnostics}");
            }

        }

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Kill the process (and its children) unless it already exited.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            if (disposed)
                return;

            disposed = true;

            try
            {

                if (!process.HasExited)
                {

                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // The pipe may already be closed.
                    }

                    await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(2)));

                }

                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

            }
            catch (Exception)
            {
                // Best effort.
            }

            try
            {
                process.WaitForExit(2000);
            }
            catch (Exception)
            {
                // Best effort.
            }

            process.Dispose();

        }

        #endregion

    }

}
