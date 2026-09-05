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

using System.Net.Sockets;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// Shared helpers of the interoperability fixtures: toolchain gates, known-issue
    /// markers, free ports and polling waits.
    /// </summary>
    public static class Interop
    {

        #region RequirePythonAsync() / RequireRustAsync(Binary = null)

        /// <summary>
        /// Make sure s2-python is usable; skips the fixture (Assert.Ignore) when the toolchain
        /// is missing and S2_INTEROP_REQUIRE is not set.
        /// </summary>
        public static async Task RequirePythonAsync()
        {
            try
            {
                await PythonHarness.EnsureReadyAsync();
                TestContext.Progress.WriteLine($"[python] s2-python {PythonHarness.S2PythonVersion}");
            }
            catch (ToolchainMissingException e) when (!Toolchains.Required)
            {
                Assert.Ignore("s2-python tests skipped: " + e.Message);
            }
        }

        /// <summary>
        /// Make sure the s2-rust harness is built for the given driver; skips the fixture
        /// (Assert.Ignore) when cargo (or, for the S2 Connect drivers on Windows, WSL with
        /// cargo) is missing and S2_INTEROP_REQUIRE is not set.
        /// </summary>
        /// <param name="Binary">The driver that will be used (default: the message-layer drivers).</param>
        public static async Task RequireRustAsync(String? Binary = null)
        {
            try
            {
                await RustHarness.EnsureBuiltAsync(Binary);
            }
            catch (ToolchainMissingException e) when (!Toolchains.Required)
            {
                Assert.Ignore("s2-rust tests skipped: " + e.Message);
            }
        }

        #endregion

        #region KnownIssue(Description, StillPresent, Detail)

        /// <summary>
        /// Records a known incompatibility between WWCP_S2 and a reference implementation as a
        /// strict "expected failure": while the issue is present the test passes with a warning
        /// naming it; once the observed behaviour is correct, the test fails so that the marker
        /// gets removed and the fix recorded. (NUnit keeps a caught assertion failure on the
        /// test result, so the observation is passed in as a Boolean instead of an assertion.)
        /// </summary>
        /// <param name="Description">The known issue.</param>
        /// <param name="StillPresent">Whether the wrong behaviour was observed.</param>
        /// <param name="Detail">What was observed, for the warning.</param>
        public static void KnownIssue(String   Description,
                                      Boolean  StillPresent,
                                      String?  Detail   = null)
        {

            if (StillPresent)
            {
                Assert.Warn($"KNOWN ISSUE (still present): {Description}{(Detail is null ? "" : Environment.NewLine + Detail)}");
                return;
            }

            Assert.Fail($"The known issue '{Description}' no longer reproduces: remove the Interop.KnownIssue marker and record the fix.");

        }

        #endregion

        #region FreePort()

        /// <summary>
        /// A free TCP port on loopback.
        /// </summary>
        public static IPPort FreePort()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint) listener.LocalEndpoint).Port;
            listener.Stop();
            return IPPort.Parse((UInt16) port);
        }

        #endregion

        #region WaitUntilAsync(Probe, Timeout = null, Description = null)

        /// <summary>
        /// Poll the probe until it is true.
        /// </summary>
        /// <param name="Probe">The condition.</param>
        /// <param name="Timeout">An optional timeout (default: 15 seconds).</param>
        /// <param name="Description">What is awaited, for the timeout message.</param>
        public static async Task WaitUntilAsync(Func<Boolean>  Probe,
                                                TimeSpan?      Timeout       = null,
                                                String?        Description   = null)
        {

            var timeout   = Timeout ?? TimeSpan.FromSeconds(15);
            var deadline  = DateTimeOffset.UtcNow + timeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Probe())
                    return;
                await Task.Delay(20);
            }

            throw new TimeoutException($"'{Description ?? "the condition"}' was not met within {timeout}!");

        }

        #endregion

        #region WaitAsync(Task, Timeout = null, Description = null)

        /// <summary>
        /// Await a task with a timeout.
        /// </summary>
        /// <param name="Task">The task.</param>
        /// <param name="Timeout">An optional timeout (default: 15 seconds).</param>
        /// <param name="Description">What is awaited, for the timeout message.</param>
        public static async Task<T> WaitAsync<T>(Task<T>    Task,
                                                 TimeSpan?  Timeout       = null,
                                                 String?    Description   = null)
        {

            var timeout = Timeout ?? TimeSpan.FromSeconds(15);

            try
            {
                return await Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"'{Description ?? "the awaited task"}' did not complete within {timeout}!");
            }

        }

        #endregion

    }

}
