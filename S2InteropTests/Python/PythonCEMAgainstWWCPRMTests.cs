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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Node;
using cloud.charging.open.protocols.S2.Session;
using cloud.charging.open.protocols.S2.Tests;
using cloud.charging.open.protocols.S2.WebSockets;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Python
{

    /// <summary>
    /// A WWCP_S2 resource manager (S2WebSocketClient in plain mode with the FRBCResourceManager
    /// control type) connected to an s2-python CEM (tools/s2-python-harness/s2_cem.py, an
    /// S2AsyncConnection on a websockets server): the Handshake with version selection, the
    /// bearer token check, ResourceManagerDetails, control type selection, the FRBC system
    /// description, an instruction from the CEM with the RM's InstructionStatusUpdate, and the
    /// termination by the CEM.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Python)]
    public sealed class PythonCEMAgainstWWCPRMTests
    {

        #region Fixture

        /// <summary>
        /// An s2-python CEM server with a connected WWCP_S2 RM session.
        /// </summary>
        private sealed class RMFixture : IAsyncDisposable
        {

            public IPPort                               Port           { get; private init; }
            public String                               Token          { get; private init; } = "";
            public ExternalProcess                      CEM            { get; private init; } = null!;
            public S2WebSocketClient                    Client         { get; private set; }  = null!;
            public S2Session                            Session        { get; private set; }  = null!;
            public TrafficRecorder                      Recorder       { get; private set; }  = null!;
            public FRBCResourceManager                  FRBC           { get; }
            public List<FRBC_Instruction>               Instructions   { get; } = [];
            public TaskCompletionSource<S2CloseReason>  Closed         { get; } = new (TaskCreationOptions.RunContinuationsAsynchronously);

            private RMFixture(IPPort Port, String Token, ExternalProcess CEM)
            {

                this.Port   = Port;
                this.Token  = Token;
                this.CEM    = CEM;

                FRBC = new FRBCResourceManager(() => SampleMessages.FRBC_SystemDescription(DateTimeOffset.UtcNow, Message_Id.NewRandom));

                FRBC.OnInstruction += (session, instruction, ct) => {
                    lock (Instructions)
                        Instructions.Add(instruction);
                    return Task.FromResult<ReceptionStatusValue?>(null);
                };

            }

            public static async Task<RMFixture> StartAsync(String   Select           = "FILL_RATE_BASED_CONTROL",
                                                           String?  AcceptVersions   = null)
            {

                var port   = Interop.FreePort();
                var token  = "interop-" + Guid.NewGuid().ToString("N");

                var arguments = new List<String> { "--port", port.ToString(), "--expect-token", token, "--select", Select };
                if (AcceptVersions is not null)
                    arguments.AddRange([ "--accept-versions", AcceptVersions ]);

                var cem = await PythonHarness.StartAsync("s2_cem.py", [.. arguments]);
                await cem.WaitForEventAsync("listening", TimeSpan.FromSeconds(30));

                return new RMFixture(port, token, cem);

            }

            public S2WebSocketClient CreateClient(String?                 Token               = null,
                                                  IReadOnlyList<String>?  SupportedVersions   = null)
                => new (URL.Parse($"ws://127.0.0.1:{Port}/"),
                        Token ?? this.Token,
                        new S2SessionOptions {
                            Role               = EnergyManagementRole.RM,
                            Mode               = S2SessionMode.Plain,
                            SupportedVersions  = SupportedVersions ?? Version.S2JSONVersions
                        });

            public async Task ConnectAsync(IReadOnlyList<String>? SupportedVersions = null)
            {

                Client   = CreateClient(SupportedVersions: SupportedVersions);
                Session  = await Client.ConnectSessionAsync();

                // The recorder and the control type are attached right after the session started;
                // the RM's own Handshake (sent while starting) is therefore not recorded.
                Recorder = new TrafficRecorder(Session);
                Session.RegisterControlType(FRBC);
                Session.OnClosed += (timestamp, session, reason) => {
                                        Closed.TrySetResult(reason);
                                        return Task.CompletedTask;
                                    };

            }

            public async ValueTask DisposeAsync()
            {

                Recorder?.Dispose();

                if (Client is not null)
                {
                    try
                    {
                        await Client.CloseSessionAsync("fixture disposed");
                    }
                    catch (Exception)
                    {
                        // The peer may already be gone.
                    }
                }

                await CEM.DisposeAsync();

            }

        }

        [OneTimeSetUp]
        public Task Setup()
            => Interop.RequirePythonAsync();

        #endregion


        #region FRBC_Scenario_EndToEnd()

        [Test]
        [S2C("Interop.s2-python.CEM.FRBC")]
        public async Task FRBC_Scenario_EndToEnd()
        {

            await using var f = await RMFixture.StartAsync();
            await f.ConnectAsync();

            // Handshake: the RM offers v1.0.0 and 0.0.2-beta, s2-python only knows 0.0.2-beta.
            await Interop.WaitUntilAsync(() => f.Session.State == S2SessionState.WebSocketConnected, Description: "the Handshake exchange");
            Assert.That(f.Session.NegotiatedVersion, Is.EqualTo(Version.S2JSONLegacyVersion));

            var handshake = await f.CEM.WaitForEventAsync("handshake");
            Assert.Multiple(() => {
                Assert.That(handshake["offered"]!.Values<String>(), Is.EqualTo(Version.S2JSONVersions));
                Assert.That(handshake.Value<String>("selected"),   Is.EqualTo(Version.S2JSONLegacyVersion));
            });

            // ResourceManagerDetails: acknowledged, and answered with SelectControlType FRBC.
            var outcome = await f.Session.SendAndAwaitReceptionStatusAsync(SampleMessages.ResourceManagerDetails(Message_Id.NewRandom));
            Assert.That(outcome.IsOK, Is.True, outcome.ReceptionStatus?.DiagnosticLabel);

            var details = await f.CEM.WaitForEventAsync("resource_manager_details");
            Assert.That(details.Value<String>("selected"), Is.EqualTo("FILL_RATE_BASED_CONTROL"));

            await Interop.WaitUntilAsync(() => f.Session.ActiveControlType == ControlType.FillRateBasedControl, Description: "the activation of FRBC");

            // The FRBCResourceManager sends its system description on activation; s2-python parses it.
            var systemDescription = await f.CEM.WaitForEventAsync("system_description");
            Assert.That(systemDescription["operation_mode_ids"]!.Values<String>().Count(), Is.EqualTo(2));

            var sentDescription = f.Recorder.Sent.Single(entry => entry.MessageType == "FRBC.SystemDescription");
            await Interop.WaitUntilAsync(() => f.Recorder.ReceivedReceptionStatuses(Message_Id.Parse(sentDescription.JSON.Value<String>("message_id")!)).Any(),
                                         Description: "the ReceptionStatus for the system description");
            Assert.That(f.Recorder.ReceivedReceptionStatuses(Message_Id.Parse(sentDescription.JSON.Value<String>("message_id")!)).Single().Value<String>("status"), Is.EqualTo("OK"));

            // An instruction from the CEM is handed to the control type and acknowledged with NEW.
            await f.CEM.SendAsync(new JObject { ["cmd"] = "instruction", ["factor"] = 1.0 });

            var sent = await f.CEM.WaitForEventAsync("instruction_sent");
            await Interop.WaitUntilAsync(() => f.Instructions.Count == 1, Description: "the instruction at the RM");

            Assert.That(f.Instructions[0].Id.ToString(), Is.EqualTo(sent.Value<String>("instruction_id")));

            var update = await f.CEM.WaitForEventAsync(e => e.Value<String>("event") == "received" && e["message"]?.Value<String>("message_type") == "InstructionStatusUpdate",
                                                       Description: "the InstructionStatusUpdate at the CEM");
            Assert.Multiple(() => {
                Assert.That(update["message"]!.Value<String>("instruction_id"), Is.EqualTo(f.Instructions[0].Id.ToString()));
                Assert.That(update["message"]!.Value<String>("status_type"),    Is.EqualTo("NEW"));
            });

            await f.CEM.WaitForEventAsync(e => e.Value<String>("event") == "reception_status" && e.Value<String>("message_type") == "FRBC.Instruction" && e.Value<String>("status") == "OK",
                                          Description: "the OK for the instruction");

            f.Recorder.AssertAllValid();

            // The CEM terminates the session.
            await f.CEM.SendAsync(new JObject { ["cmd"] = "terminate", ["label"] = "test finished" });

            var reason = await Interop.WaitAsync(f.Closed.Task, Description: "the end of the RM session");
            Assert.That(reason.Description, Does.Contain("TERMINATE"));
            Assert.That(f.Session.State, Is.EqualTo(S2SessionState.Disconnected));

            await f.CEM.WaitForEventAsync("client_disconnected");

        }

        #endregion

        #region WrongBearerToken_IsRejectedWith401()

        [Test]
        [S2C("Communication.WebSocket.Authentication")]
        public async Task WrongBearerToken_IsRejectedWith401()
        {

            await using var f = await RMFixture.StartAsync();

            var client     = f.CreateClient(Token: "not-the-token");
            var exception  = Assert.ThrowsAsync<S2WebSocketConnectException>(async () => await client.ConnectSessionAsync());

            Assert.That(exception!.Response.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.Unauthorized));

            var rejected = await f.CEM.WaitForEventAsync("rejected");
            Assert.That(rejected.Value<String>("authorization"), Is.EqualTo("Bearer not-the-token"));

        }

        #endregion

        #region CEMAcceptingV100_SelectsThePreferredVersion()

        [Test]
        [S2C("Handshake.VersionSelection")]
        public async Task CEMAcceptingV100_SelectsThePreferredVersion()
        {

            // s2-python's CEM driver additionally accepts v1.0.0; the RM lists it first.
            await using var f = await RMFixture.StartAsync(AcceptVersions: Version.S2JSONVersion);
            await f.ConnectAsync();

            await Interop.WaitUntilAsync(() => f.Session.State == S2SessionState.WebSocketConnected, Description: "the Handshake exchange");

            Assert.That(f.Session.NegotiatedVersion, Is.EqualTo(Version.S2JSONVersion));

            var handshake = await f.CEM.WaitForEventAsync("handshake");
            Assert.That(handshake.Value<String>("selected"), Is.EqualTo(Version.S2JSONVersion));

        }

        #endregion

        #region NoCommonVersion_TheCEMTerminates()

        [Test]
        [S2C("Handshake.NoCommonVersion")]
        public async Task NoCommonVersion_TheCEMTerminates()
        {

            await using var f = await RMFixture.StartAsync();
            await f.ConnectAsync(SupportedVersions: [ Version.S2JSONVersion ]);

            var handshake = await f.CEM.WaitForEventAsync("handshake");
            Assert.That(handshake["selected"]!.Type, Is.EqualTo(JTokenType.Null), "s2-python only knows 0.0.2-beta");

            await Interop.WaitAsync(f.Closed.Task, Description: "the end of the RM session");
            Assert.That(f.Session.State, Is.EqualTo(S2SessionState.Disconnected));

        }

        #endregion

    }

}
