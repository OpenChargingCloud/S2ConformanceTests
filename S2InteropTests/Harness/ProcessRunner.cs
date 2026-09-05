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

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// The result of a finished command.
    /// </summary>
    /// <param name="ExitCode">The exit code.</param>
    /// <param name="StandardOutput">Everything written to stdout.</param>
    /// <param name="StandardError">Everything written to stderr.</param>
    public sealed record ProcessResult(Int32   ExitCode,
                                       String  StandardOutput,
                                       String  StandardError)
    {

        /// <summary>
        /// Whether the command exited with 0.
        /// </summary>
        public Boolean IsSuccess
            => ExitCode == 0;

        /// <summary>
        /// stdout and stderr, trimmed, for error messages.
        /// </summary>
        public String Output
            => (StandardOutput + Environment.NewLine + StandardError).Trim();

    }


    /// <summary>
    /// Runs one-shot commands (interpreter probes, pip, cargo) to completion.
    /// </summary>
    public static class ProcessRunner
    {

        /// <summary>
        /// Run the given command and wait for it to exit.
        /// </summary>
        /// <param name="FileName">The executable.</param>
        /// <param name="Arguments">The arguments.</param>
        /// <param name="WorkingDirectory">An optional working directory.</param>
        /// <param name="Timeout">An optional timeout (default: 2 minutes); the process is killed afterwards.</param>
        /// <param name="Environment">Optional additional environment variables.</param>
        public static async Task<ProcessResult> RunAsync(String                                FileName,
                                                         IEnumerable<String>                   Arguments,
                                                         String?                               WorkingDirectory   = null,
                                                         TimeSpan?                             Timeout            = null,
                                                         IReadOnlyDictionary<String, String>?  Environment        = null)
        {

            var timeout    = Timeout ?? TimeSpan.FromMinutes(2);

            var startInfo  = new ProcessStartInfo(FileName) {
                                 UseShellExecute         = false,
                                 CreateNoWindow          = true,
                                 RedirectStandardOutput  = true,
                                 RedirectStandardError   = true,
                                 StandardOutputEncoding  = Encoding.UTF8,
                                 StandardErrorEncoding   = Encoding.UTF8
                             };

            foreach (var argument in Arguments)
                startInfo.ArgumentList.Add(argument);

            if (WorkingDirectory is not null)
                startInfo.WorkingDirectory = WorkingDirectory;

            if (Environment is not null)
                foreach (var (key, value) in Environment)
                    startInfo.Environment[key] = value;

            using var process = new Process { StartInfo = startInfo };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (sender, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived  += (sender, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone.
                }

                lock (stderr)
                    stderr.AppendLine($"[the command did not finish within {timeout} and was killed]");

                return new ProcessResult(-1, stdout.ToString(), stderr.ToString());

            }

            // Flush the asynchronous readers.
            process.WaitForExit();

            lock (stdout)
                lock (stderr)
                    return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());

        }

    }

}
