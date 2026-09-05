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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Node;
using cloud.charging.open.protocols.S2.Session;
using cloud.charging.open.protocols.S2.Tests;
using cloud.charging.open.protocols.S2.WebSockets;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Python
{

    /// <summary>
    /// An s2-python resource manager (tools/s2-python-harness/s2_rm.py, built on s2-python's
    /// S2AsyncConnection and ResourceManagerHandler) connected to a WWCP_S2 CEM WebSocket
    /// server in plain S2-JSON-over-WebSocket mode: the Handshake exchange announcing
    /// "0.0.2-beta", ResourceManagerDetails, control type selection, the FRBC system
    /// description and statuses, an instruction with its InstructionStatusUpdate, the
    /// ReceptionStatus rules and the termination of the session.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Python)]
    public sealed class PythonRMAgainstWWCPCEMTests
    {

        #region Fixture

        /// <summary>
        /// A WWCP_S2 CEM WebSocket server (plain mode) with one connected s2-python RM.
        /// </summary>
        private sealed class CEMFixture : IAsyncDisposable
        {

            public IPPort                                     Port        { get; private init; }
            public String                                     Token       { get; private set; } = "";
            public S2WebSocketServer                          Server      { get; private init; } = null!;
            public S2Session                                  Session     { get; private set; } = null!;
            public ExternalProcess                            RM          { get; private set; } = null!;
            public TrafficRecorder                            Recorder    { get; private set; } = null!;
            public FRBCEnergyManager                          FRBC        { get; } = new ();
            public List<ResourceManagerDetails>               Details     { get; } = [];
            public List<InstructionStatusUpdate>              Updates     { get; } = [];
            public TaskCompletionSource<S2Session>            Started     { get; } = new (TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<S2CloseReason>        Ended       { get; } = new (TaskCreationOptions.RunContinuationsAsynchronously);

            private CEMFixture(IPPort Port, S2WebSocketServer Server)
            {
                this.Port    = Port;
                this.Server  = Server;
            }

            public static async Task<CEMFixture> StartAsync(IReadOnlyList<String>?  SupportedVersions   = null,
                                                            Boolean                 ConnectRM           = true)
            {

                var port      = Interop.FreePort();
                var versions  = SupportedVersions ?? Version.S2JSONVersions;

                var server    = new S2WebSocketServer(
                                    IPv4Address.Localhost,
                                    port,
                                    EnergyManagementRole.CEM,
                                    SessionOptionsFactory: (connection, identity) => new S2SessionOptions {
                                                                                         Role               = EnergyManagementRole.CEM,
                                                                                         Mode               = S2SessionMode.Plain,
                                                                                         SupportedVersions  = versions
                                                                                     }
                                );

                var fixture   = new CEMFixture(port, server);

                server.OnSessionStarted += (timestamp, sender, session, identity) => {
                                               fixture.Attach(session);
                                               fixture.Started.TrySetResult(session);
                                               return Task.CompletedTask;
                                           };

                server.OnSessionEnded   += (timestamp, sender, session, reason) => {
                                               fixture.Ended.TrySetResult(reason);
                                               return Task.CompletedTask;
                                           };

                await server.Start();

                fixture.Token = server.TokenStore.Issue("s2-python-rm");

                if (ConnectRM)
                    await fixture.ConnectRMAsync();

                return fixture;

            }

            public async Task ConnectRMAsync()
            {
                RM       = await PythonHarness.StartAsync("s2_rm.py", "--url", $"ws://127.0.0.1:{Port}/", "--token", Token);
                await RM.WaitForEventAsync("connected", TimeSpan.FromSeconds(30));
                Session  = await Interop.WaitAsync(Started.Task, Description: "the CEM session");
            }

            private void Attach(S2Session Session)
            {

                Recorder = new TrafficRecorder(Session);

                Session.RegisterControlType(FRBC);

                Session.On<ResourceManagerDetails>((session, details, ct) => {
                    lock (Details)
                        Details.Add(details);
                    return Task.FromResult<ReceptionStatusValue?>(null);
                });

                FRBC.OnInstructionStatusUpdate += (session, update, ct) => {
                    lock (Updates)
                        Updates.Add(update);
                    return Task.CompletedTask;
                };

            }

            /// <summary>
            /// Wait until s2-python received the ReceptionStatus of the last message of the given
            /// type it sent (s2-python routes every received ReceptionStatus through its awaiter,
            /// which the driver instruments; the message_id comes from the "sent" event).
            /// </summary>
            public async Task<JObject> WaitForReceptionStatusAsync(String MessageType, String Status = "OK")
            {

                var sent = await RM.WaitForEventAsync(e => e.Value<String>("event") == "sent" && e["message"]?.Value<String>("message_type") == MessageType,
                                                      Description: $"the RM sending a {MessageType}");

                var messageId = sent["message"]!.Value<String>("message_id");

                return await RM.WaitForEventAsync(e => e.Value<String>("event")              == "reception_status_received" &&
                                                       e.Value<String>("subject_message_id") == messageId                   &&
                                                       e.Value<String>("status")             == Status,
                                                  Description: $"the {Status} ReceptionStatus for the RM's {MessageType} {messageId}");

            }

            public async ValueTask DisposeAsync()
            {

                if (RM is not null)
                    await RM.DisposeAsync();

                Recorder?.Dispose();

                try
                {
                    await Server.Shutdown();
                }
                catch (Exception)
                {
                    // Best effort.
                }

            }

        }

        [OneTimeSetUp]
        public Task Setup()
            => Interop.RequirePythonAsync();

        #endregion


        #region Handshake_AnnouncesTheLegacyVersion_AndTheCEMSelectsIt()

        [Test]
        [S2C("Interop.s2-python.Handshake")]
        public async Task Handshake_AnnouncesTheLegacyVersion_AndTheCEMSelectsIt()
        {

            await using var f = await CEMFixture.StartAsync();

            await Interop.WaitUntilAsync(() => f.Session.State == S2SessionState.WebSocketConnected, Description: "the Handshake exchange");

            Assert.That(f.Session.NegotiatedVersion, Is.EqualTo(Version.S2JSONLegacyVersion), "s2-python announces 0.0.2-beta, which the CEM must select");

            // s2-python saw the CEM Handshake and the HandshakeResponse selecting 0.0.2-beta …
            var response = await f.RM.WaitForEventAsync(e => e.Value<String>("event") == "received" && e["message"]?.Value<String>("message_type") == "HandshakeResponse",
                                                        Description: "the HandshakeResponse at the RM");

            Assert.That(response["message"]!.Value<String>("selected_protocol_version"), Is.EqualTo(Version.S2JSONLegacyVersion));

            // … and got its own Handshake acknowledged (it awaits that ReceptionStatus).
            await f.WaitForReceptionStatusAsync("Handshake");

            // The RM sends its details only after the HandshakeResponse.
            await Interop.WaitUntilAsync(() => f.Details.Count == 1, Description: "the ResourceManagerDetails");
            await f.WaitForReceptionStatusAsync("ResourceManagerDetails");

            Assert.That(f.Details[0].AvailableControlTypes, Does.Contain(ControlType.FillRateBasedControl));

            f.Recorder.AssertAllValid();

        }

        #endregion

        #region FRBC_Scenario_EndToEnd()

        [Test]
        [S2C("Interop.s2-python.FRBC")]
        public async Task FRBC_Scenario_EndToEnd()
        {

            await using var f = await CEMFixture.StartAsync();

            await Interop.WaitUntilAsync(() => f.Details.Count == 1, Description: "the ResourceManagerDetails");

            // The CEM selects FRBC; s2-python activates its control type and sends the
            // system description, the storage status and the actuator status.
            var outcome = await f.Session.SendAndAwaitReceptionStatusAsync(new SelectControlType(ControlType.FillRateBasedControl));
            Assert.That(outcome.IsOK, Is.True, outcome.ReceptionStatus?.DiagnosticLabel);

            var activated = await f.RM.WaitForEventAsync("activated");
            Assert.That(activated.Value<String>("control_type"), Is.EqualTo("FILL_RATE_BASED_CONTROL"));

            await Interop.WaitUntilAsync(() => f.FRBC.LastSystemDescription is not null &&
                                               f.FRBC.LastStorageStatus     is not null &&
                                               f.FRBC.LastActuatorStatus    is not null,
                                         Description: "the FRBC system description, storage status and actuator status");

            foreach (var messageType in new[] { "FRBC.SystemDescription", "FRBC.StorageStatus", "FRBC.ActuatorStatus" })
                await f.WaitForReceptionStatusAsync(messageType);

            Assert.That(f.Session.ActiveControlType, Is.EqualTo(ControlType.FillRateBasedControl));

            // An instruction for the "charging" operation mode of the s2-python actuator.
            var actuator     = f.FRBC.LastSystemDescription!.Actuators[0];
            var instruction  = new FRBC_Instruction(Instruction_Id.NewRandom,
                                                    actuator.Id,
                                                    actuator.OperationModes[1].Id,
                                                    1.0,
                                                    DateTimeOffset.UtcNow,
                                                    AbnormalCondition: false);

            outcome = await f.Session.SendAndAwaitReceptionStatusAsync(instruction);
            Assert.That(outcome.IsOK, Is.True, outcome.ReceptionStatus?.DiagnosticLabel);

            var received = await f.RM.WaitForEventAsync("instruction");
            Assert.Multiple(() => {
                Assert.That(received.Value<String>("instruction_id"),  Is.EqualTo(instruction.Id.ToString()));
                Assert.That(received.Value<String>("operation_mode"),  Is.EqualTo(actuator.OperationModes[1].Id.ToString()));
            });

            await Interop.WaitUntilAsync(() => f.Updates.Any(update => update.InstructionId == instruction.Id && update.StatusType == InstructionStatus.New),
                                         Description: "the InstructionStatusUpdate NEW");
            await f.WaitForReceptionStatusAsync("InstructionStatusUpdate");

            // Exactly one ReceptionStatus per message in both directions, everything schema valid.
            Assert.That(f.RM.Events.Where(e => e.Value<String>("event") == "reception_status_duplicate"), Is.Empty, "s2-python received two ReceptionStatus messages for one message");
            f.Recorder.AssertAllValid();

            // The CEM terminates the session; s2-python acknowledges and closes the socket.
            outcome = await f.Session.SendAndAwaitReceptionStatusAsync(new SessionRequest(SessionRequestType.Terminate, "test finished"));
            Assert.That(outcome.IsOK, Is.True, outcome.ReceptionStatus?.DiagnosticLabel);

            var request = await f.RM.WaitForEventAsync("session_request");
            Assert.That(request.Value<String>("request"), Is.EqualTo("TERMINATE"));

            await f.RM.WaitForEventAsync("stopped");
            await Interop.WaitAsync(f.Ended.Task, Description: "the end of the CEM session");
            Assert.That(f.Session.State, Is.EqualTo(S2SessionState.Disconnected));

        }

        #endregion

        #region InvalidJSON_IsAnsweredWithInvalidData_AndTheNullMessageId()

        [Test]
        [S2C("ReceptionStatus.InvalidData")]
        public async Task InvalidJSON_IsAnsweredWithInvalidData_AndTheNullMessageId()
        {

            await using var f = await CEMFixture.StartAsync();

            await Interop.WaitUntilAsync(() => f.Details.Count == 1, Description: "the ResourceManagerDetails");

            await f.RM.SendAsync(new JObject { ["cmd"] = "send_raw", ["text"] = "this is not JSON" });

            var status = await f.RM.WaitForEventAsync(e => e.Value<String>("event") == "reception_status_received" && e.Value<String>("status") == "INVALID_DATA",
                                                      Description: "the INVALID_DATA ReceptionStatus");

            Assert.That(status.Value<String>("subject_message_id"), Is.EqualTo(Message_Id.Null.ToString()));

        }

        #endregion

        #region UnknownMessageType_IsAnsweredWithInvalidMessage()

        [Test]
        [S2C("ReceptionStatus.InvalidMessage")]
        public async Task UnknownMessageType_IsAnsweredWithInvalidMessage()
        {

            await using var f = await CEMFixture.StartAsync();

            await Interop.WaitUntilAsync(() => f.Details.Count == 1, Description: "the ResourceManagerDetails");

            var messageId = Message_Id.NewRandom;

            await f.RM.SendAsync(new JObject {
                                     ["cmd"]   = "send_raw",
                                     ["text"]  = new JObject { ["message_type"] = "FRBC.Bogus", ["message_id"] = messageId.ToString() }.ToString(Formatting.None)
                                 });

            var status = await f.RM.WaitForEventAsync(e => e.Value<String>("event") == "reception_status_received" && e.Value<String>("subject_message_id") == messageId.ToString(),
                                                      Description: "the ReceptionStatus for the bogus message");

            Assert.That(status.Value<String>("status"), Is.EqualTo("INVALID_MESSAGE"));

        }

        #endregion

        #region ControlTypeMessage_BeforeSelection_IsAnsweredWithInvalidContent()

        [Test]
        [S2C("ReceptionStatus.OutOfState")]
        public async Task ControlTypeMessage_BeforeSelection_IsAnsweredWithInvalidContent()
        {

            await using var f = await CEMFixture.StartAsync();

            await Interop.WaitUntilAsync(() => f.Details.Count == 1, Description: "the ResourceManagerDetails");

            // No control type selected yet: an FRBC.StorageStatus is out of state.
            await f.RM.SendAsync(new JObject {
                                     ["cmd"]      = "send",
                                     ["message"]  = SampleMessages.FRBC_StorageStatus().ToJSON()
                                 });

            var status = await f.RM.WaitForEventAsync(e => e.Value<String>("event") == "reception_status" && e.Value<String>("message_type") == "FRBC.StorageStatus",
                                                      Description: "the ReceptionStatus for the premature FRBC.StorageStatus");

            Assert.That(status.Value<String>("status"), Is.EqualTo("INVALID_CONTENT"), "an out-of-state message is ignored with INVALID_CONTENT, never PERMANENT_ERROR");
            Assert.That(f.Session.State, Is.EqualTo(S2SessionState.WebSocketConnected), "the session survives");

        }

        #endregion

        #region NoCommonVersion_TheCEMTerminatesTheSession()

        [Test]
        [S2C("Handshake.NoCommonVersion")]
        public async Task NoCommonVersion_TheCEMTerminatesTheSession()
        {

            // A CEM that only speaks v1.0.0 cannot serve an RM announcing 0.0.2-beta only: it sends
            // SessionRequest TERMINATE and closes. s2-python may lose the TERMINATE when the socket
            // closes right behind it (its handler task is cancelled once the receive loop ends), so
            // the evidence is taken from either side.
            await using var f = await CEMFixture.StartAsync(SupportedVersions: [ Version.S2JSONVersion ]);

            var reason = await Interop.WaitAsync(f.Ended.Task, TimeSpan.FromSeconds(30), "the end of the CEM session");

            await Task.WhenAny(f.RM.WaitForEventAsync("stopped", TimeSpan.FromSeconds(30)), f.RM.Exited);

            var terminateSeenByPython  = f.RM.Events.Any(e => e.Value<String>("event") == "session_request" && e.Value<String>("request") == "TERMINATE" ||
                                                              e.Value<String>("event") == "received" && e["message"]?.Value<String>("message_type") == "SessionRequest");
            var terminateSentByCEM     = f.Recorder.Sent.Any(entry => entry.MessageType == "SessionRequest" && entry.JSON.Value<String>("request") == "TERMINATE");

            Assert.Multiple(() => {
                Assert.That(terminateSeenByPython || terminateSentByCEM, Is.True, $"the CEM must terminate the session with SessionRequest TERMINATE (close reason: {reason.Description})");
                Assert.That(f.Details, Is.Empty, "no ResourceManagerDetails after a failed Handshake");
            });

        }

        #endregion

    }

}
