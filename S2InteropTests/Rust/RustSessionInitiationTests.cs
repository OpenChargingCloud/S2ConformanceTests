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
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Node;
using cloud.charging.open.protocols.S2.Session;
using cloud.charging.open.protocols.S2.Tests;
using cloud.charging.open.protocols.S2.WebSockets;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Rust
{

    /// <summary>
    /// S2 Connect session initiation, the WebSocket session and the S2 JSON messages on it
    /// against s2-rust (s2energy-connection::communication with s2energy-messaging's
    /// S2Connection) over real TLS, in both directions: the WWCP_S2 SessionInitiationClient
    /// against the s2-rust communication server, and the s2-rust communication client against
    /// the WWCP_S2 SessionInitiationServerAPI and S2WebSocketServer. Both sides start from a
    /// seeded pairing (a shared access token) so that the tests do not depend on the pairing
    /// tests. The s2-rust side confirms every message it receives and sends what the test
    /// tells it to send (see tools/s2-rust-harness/src/pump.rs).
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Rust)]
    public sealed class RustSessionInitiationTests
    {

        #region Data

        private const String CommunicationServer  = "s2-comm-server";
        private const String CommunicationClient  = "s2-comm-client";

        #endregion

        #region Setup

        [OneTimeSetUp]
        public Task BuildHarness()
            => Interop.RequireRustAsync(CommunicationServer);

        private static String BindAddress(String Binary)
            => RustHarness.RunsInWSL(Binary) ? "0.0.0.0" : "127.0.0.1";

        private static Task<JObject> WaitForReceivedAsync(ExternalProcess Driver, String MessageType)
            => Driver.WaitForEventAsync(e => e.Value<String>("event") == "received" && e["message"]?.Value<String>("message_type") == MessageType,
                                        Description: $"s2-rust receiving a {MessageType}");

        private static Task SendAsync(ExternalProcess Driver, IS2Message Message)
            => Driver.SendAsync(new JObject { ["cmd"] = "send", ["message"] = Message.ToJSON() });

        #endregion


        #region WWCPClient_OpensAnS2SessionWithTheS2RustCommunicationServer()

        [Test]
        [S2C("SessionInitiation.WWCPClient.S2RustServer")]
        public async Task WWCPClient_OpensAnS2SessionWithTheS2RustCommunicationServer()
        {

            using var certificate = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);

            var port          = Interop.FreePort();
            var serverNodeId  = Node_Id.NewRandom;
            var clientNodeId  = Node_Id.NewRandom;
            var accessToken   = TokenGenerator.NewAccessToken();

            await using var server = await RustHarness.StartAsync(CommunicationServer,
                                                                  "--bind",            BindAddress(CommunicationServer),
                                                                  "--port",            port.ToString(),
                                                                  "--cert",            RustHarness.PathForDriver(CommunicationServer, certificate.CertificatePem),
                                                                  "--key",             RustHarness.PathForDriver(CommunicationServer, certificate.KeyPem),
                                                                  "--base-url",        $"localhost:{port}",
                                                                  "--server-node-id",  serverNodeId.ToString(),
                                                                  "--client-node-id",  clientNodeId.ToString(),
                                                                  "--access-token",    accessToken.Value);

            await server.WaitForEventAsync("listening", TimeSpan.FromSeconds(30));

            // The WWCP_S2 RM, already paired with the s2-rust CEM.
            var sessionUrl  = S2BaseURL.Parse($"https://localhost:{port}/");
            var endpoint    = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop RM endpoint"), Deployment.LAN, S2BaseURL.Parse("https://wwcp-rm.local/pairing/"));
            var node        = endpoint.AddNode(new NodeDescription(clientNodeId, "GraphDefined", "EV charger", "WWCP_S2", EnergyManagementRole.RM));
            var store       = new InMemoryS2Store();

            await store.AddOrReplacePairingAsync(new Pairing(clientNodeId,
                                                             new NodeDescription(serverNodeId, "s2-rust interop", "test node", "s2-rust-harness", EnergyManagementRole.CEM),
                                                             new EndpointDescription("s2-rust interop endpoint"),
                                                             CommunicationRole.CommunicationClient,
                                                             accessToken,
                                                             DateTimeOffset.UtcNow,
                                                             sessionUrl));

            await using var client = new SessionInitiationClient(sessionUrl,
                                                                 endpoint,
                                                                 store,
                                                                 new SessionInitiationClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) },
                                                                 WebSocketCertificateValidator: Certificates.AcceptAny<IWebSocketClient>());

            var instructions  = new List<FRBC_Instruction>();
            var frbc          = new FRBCResourceManager(() => SampleMessages.FRBC_SystemDescription(DateTimeOffset.UtcNow, Message_Id.NewRandom));

            frbc.OnInstruction += (session, instruction, ct) => {
                lock (instructions)
                    instructions.Add(instruction);
                return Task.FromResult<ReceptionStatusValue?>(null);
            };

            // Session initiation (version index, initiateSession, confirmAccessToken) and the WebSocket.
            var connect = await client.ConnectAsync(node, serverNodeId);

            Assert.That(connect.IsSuccess, Is.True, () => $"{connect.Initiation}{Environment.NewLine}{server.Diagnostics}");

            await using var s2   = connect.Session!;
            var session          = s2.Session;

            session.RegisterControlType(frbc);
            using var recorder   = new TrafficRecorder(session);

            var connected = await server.WaitForEventAsync("connected");

            Assert.Multiple(() => {
                Assert.That(connected.Value<String>("client"),            Is.EqualTo(clientNodeId.ToString()));
                Assert.That(connected.Value<String>("server"),            Is.EqualTo(serverNodeId.ToString()));
                Assert.That(connected.Value<String>("message_version"),   Is.EqualTo(session.NegotiatedVersion), "both sides agree on the negotiated S2 JSON version");
                Assert.That(connect.Initiation.SelectedS2MessageVersion,  Is.EqualTo(connected.Value<String>("message_version")));
            });

            // Access token rotation: the token confirmed in step 7 is the active one on both sides.
            var rotated     = await server.WaitForEventAsync("access_token");
            var candidates  = await store.GetAccessTokenCandidatesAsync(clientNodeId, serverNodeId);

            Assert.Multiple(() => {
                Assert.That(candidates,          Is.Not.Empty);
                Assert.That(candidates[0].Value, Is.EqualTo(rotated.Value<String>("token")), "the rotated access token is active on both sides");
                Assert.That(candidates[0],       Is.Not.EqualTo(accessToken), "the token was rotated");
            });

            // S2 messages: the RM announces itself, the s2-rust CEM selects FRBC.
            var outcome = await session.SendAndAwaitReceptionStatusAsync(SampleMessages.ResourceManagerDetails(Message_Id.NewRandom));
            Assert.That(outcome.IsOK, Is.True, outcome.ReceptionStatus?.DiagnosticLabel);
            await WaitForReceivedAsync(server, "ResourceManagerDetails");

            var select = new SelectControlType(ControlType.FillRateBasedControl);
            await SendAsync(server, select);

            await Interop.WaitUntilAsync(() => session.ActiveControlType == ControlType.FillRateBasedControl, Description: "the activation of FRBC");

            var selectStatus = await server.WaitForEventAsync(e => e.Value<String>("event") == "reception_status" && e.Value<String>("subject_message_id") == select.MessageId.ToString(),
                                                              Description: "the ReceptionStatus for the SelectControlType");
            Assert.That(selectStatus.Value<String>("status"), Is.EqualTo("OK"));

            // The FRBCResourceManager sends its system description on activation; s2-rust parses it.
            var systemDescription = await WaitForReceivedAsync(server, "FRBC.SystemDescription");
            var actuator          = systemDescription["message"]!["actuators"]![0]!;

            // An instruction from the s2-rust CEM is handed to the control type and acknowledged with NEW.
            var instruction = new FRBC_Instruction(Instruction_Id.NewRandom,
                                                   Actuator_Id.Parse(actuator.Value<String>("id")!),
                                                   OperationMode_Id.Parse(actuator["operation_modes"]![1]!.Value<String>("id")!),
                                                   1.0,
                                                   DateTimeOffset.UtcNow,
                                                   AbnormalCondition: false);

            await SendAsync(server, instruction);

            await Interop.WaitUntilAsync(() => instructions.Count == 1, Description: "the instruction at the RM");
            Assert.That(instructions[0].Id, Is.EqualTo(instruction.Id));

            var update = await WaitForReceivedAsync(server, "InstructionStatusUpdate");

            Assert.Multiple(() => {
                Assert.That(update["message"]!.Value<String>("instruction_id"), Is.EqualTo(instruction.Id.ToString()));
                Assert.That(update["message"]!.Value<String>("status_type"),    Is.EqualTo("NEW"));
            });

            recorder.AssertAllValid();

            // The RM closes the session; s2-rust sees the transport close.
            await s2.CloseAsync("test finished");
            await server.WaitForEventAsync("closed");

        }

        #endregion

        #region WWCPClient_UnpairsAtTheS2RustCommunicationServer()

        [Test]
        [S2C("Unpairing.WWCPClient.S2RustServer")]
        public async Task WWCPClient_UnpairsAtTheS2RustCommunicationServer()
        {

            using var certificate = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);

            var port          = Interop.FreePort();
            var serverNodeId  = Node_Id.NewRandom;
            var clientNodeId  = Node_Id.NewRandom;
            var accessToken   = TokenGenerator.NewAccessToken();

            await using var server = await RustHarness.StartAsync(CommunicationServer,
                                                                  "--bind",            BindAddress(CommunicationServer),
                                                                  "--port",            port.ToString(),
                                                                  "--cert",            RustHarness.PathForDriver(CommunicationServer, certificate.CertificatePem),
                                                                  "--key",             RustHarness.PathForDriver(CommunicationServer, certificate.KeyPem),
                                                                  "--base-url",        $"localhost:{port}",
                                                                  "--server-node-id",  serverNodeId.ToString(),
                                                                  "--client-node-id",  clientNodeId.ToString(),
                                                                  "--access-token",    accessToken.Value);

            await server.WaitForEventAsync("listening", TimeSpan.FromSeconds(30));

            var sessionUrl  = S2BaseURL.Parse($"https://localhost:{port}/");
            var endpoint    = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop RM endpoint"), Deployment.LAN, S2BaseURL.Parse("https://wwcp-rm.local/pairing/"));
            var node        = endpoint.AddNode(new NodeDescription(clientNodeId, "GraphDefined", "EV charger", "WWCP_S2", EnergyManagementRole.RM));
            var store       = new InMemoryS2Store();

            await store.AddOrReplacePairingAsync(new Pairing(clientNodeId,
                                                             new NodeDescription(serverNodeId, "s2-rust interop", "test node", "s2-rust-harness", EnergyManagementRole.CEM),
                                                             new EndpointDescription("s2-rust interop endpoint"),
                                                             CommunicationRole.CommunicationClient,
                                                             accessToken,
                                                             DateTimeOffset.UtcNow,
                                                             sessionUrl));

            await using var client = new SessionInitiationClient(sessionUrl, endpoint, store,
                                                                 new SessionInitiationClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.UnpairAsync(node.Id, serverNodeId);

            Assert.That(result.IsSuccess, Is.True, () => $"{result}{Environment.NewLine}{server.Diagnostics}");

            await server.WaitForEventAsync("unpaired");

            Assert.That(await store.GetPairingAsync(clientNodeId, serverNodeId), Is.Null, "the local pairing is removed");

            // A second session initiation is answered with NoLongerPaired.
            var again = await client.ConnectAsync(node, serverNodeId);
            Assert.That(again.IsSuccess, Is.False);

        }

        #endregion


        #region S2RustClient_OpensAnS2SessionWithTheWWCPCommunicationServer()

        [Test]
        [S2C("SessionInitiation.S2RustClient.WWCPServer")]
        public async Task S2RustClient_OpensAnS2SessionWithTheWWCPCommunicationServer()
        {

            await Interop.RequireRustAsync(CommunicationClient);

            if (!await RustHarness.CanReachThisProcessAsync(CommunicationClient))
                Assert.Ignore("The s2-rust driver (inside WSL) cannot open connections to this process: allow the test host through the Windows firewall on the WSL network, or run the tests on Linux.");

            var host = await RustHarness.HostAddressForDriverAsync(CommunicationClient);

            // The certificate WWCP S2 issues for a LAN endpoint (SelfSignedCA, D13); the s2-rust
            // communication client trusts it as an extra root and accepts it as an end entity.
            using var certificate = ServerCertificate.FromWWCP("wwcp-cem.local", [ System.Net.IPAddress.Parse(host) ]);

            var httpPort      = Interop.FreePort();
            var wsPort        = Interop.FreePort();
            var clientNodeId  = Node_Id.NewRandom;
            var accessToken   = TokenGenerator.NewAccessToken();

            // The WWCP_S2 CEM: session initiation API and S2 WebSocket server sharing the token store.
            var tokenStore    = new CommunicationTokenStore();
            var started       = new TaskCompletionSource<S2Session>(TaskCreationOptions.RunContinuationsAsynchronously);

            var wsServer      = new S2WebSocketServer(IPv4Address.Any,
                                                      wsPort,
                                                      EnergyManagementRole.CEM,
                                                      TokenStore:                 tokenStore,
                                                      SessionOptionsFactory:      (connection, identity) => new S2SessionOptions {
                                                                                      Role               = EnergyManagementRole.CEM,
                                                                                      Mode               = S2SessionMode.S2Connect,
                                                                                      NegotiatedVersion  = (identity as S2ConnectSessionIdentity)?.S2MessageVersion ?? Version.S2JSONVersion
                                                                                  },
                                                      ServerCertificateSelector:  (tcpServer, tcpClient) => certificate.Certificate);

            wsServer.OnSessionStarted += (timestamp, sender, session, identity) => {
                                             started.TrySetResult(session);
                                             return Task.CompletedTask;
                                         };

            var httpServer    = new HTTPServer(IPv4Address.Any,
                                               httpPort,
                                               "WWCP S2 interop session initiation server",
                                               ServerCertificateSelector:  (tcpServer, tcpClient) => certificate.Certificate,
                                               AutoStart:                  false);

            var endpoint      = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop CEM endpoint"),
                                                  Deployment.LAN,
                                                  S2BaseURL.Parse($"https://{host}:{httpPort}/pairing/"),
                                                  S2BaseURL.Parse($"https://{host}:{httpPort}/connection/"),
                                                  ServerCertificateFingerprint:  () => certificate.Fingerprint,
                                                  CACertificateFingerprint:      () => certificate.Fingerprint);

            var cem           = endpoint.AddNode(new NodeDescription(Node_Id.NewRandom, "GraphDefined", "EMS", "WWCP_S2", EnergyManagementRole.CEM));
            var store         = new InMemoryS2Store();

            await store.AddOrReplacePairingAsync(new Pairing(cem.Id,
                                                             new NodeDescription(clientNodeId, "s2-rust interop", "test node", "s2-rust-harness", EnergyManagementRole.RM),
                                                             new EndpointDescription("s2-rust interop endpoint"),
                                                             CommunicationRole.CommunicationServer,
                                                             accessToken,
                                                             DateTimeOffset.UtcNow));

            var api           = new SessionInitiationServerAPI(httpServer,
                                                               endpoint,
                                                               store,
                                                               tokenStore,
                                                               URL.Parse($"wss://{host}:{wsPort}/"),
                                                               Options: new SessionInitiationServerOptions());

            var unpaired      = new TaskCompletionSource<Pairing>(TaskCreationOptions.RunContinuationsAsynchronously);

            api.OnUnpaired += (timestamp, sender, pairing, byRemote) => {
                                  unpaired.TrySetResult(pairing);
                                  return Task.CompletedTask;
                              };

            await wsServer.Start();
            await httpServer.Start();

            try
            {

                await using var client = await RustHarness.StartAsync(CommunicationClient,
                                                                      "--url",             $"https://{host}:{httpPort}/connection/",
                                                                      "--client-node-id",  clientNodeId.ToString(),
                                                                      "--server-node-id",  cem.Id.ToString(),
                                                                      "--access-token",    accessToken.Value,
                                                                      "--ca",              RustHarness.PathForDriver(CommunicationClient, certificate.CertificatePem));

                var outcome = await client.WaitForEventAsync(e => e.Value<String>("event") is "connected" or "connect_failed",
                                                             TimeSpan.FromSeconds(60),
                                                             "the outcome of the session initiation");

                Assert.That(outcome.Value<String>("event"), Is.EqualTo("connected"), () => $"{outcome}{Environment.NewLine}{client.Diagnostics}");

                var session = await Interop.WaitAsync(started.Task, Description: "the CEM session");

                Assert.That(outcome.Value<String>("message_version"), Is.EqualTo(session.NegotiatedVersion), "both sides agree on the negotiated S2 JSON version");

                var frbc     = new FRBCEnergyManager();
                var updates  = new List<InstructionStatusUpdate>();

                frbc.OnInstructionStatusUpdate += (s, update, ct) => {
                                                      lock (updates)
                                                          updates.Add(update);
                                                      return Task.CompletedTask;
                                                  };

                session.RegisterControlType(frbc);
                using var recorder = new TrafficRecorder(session);

                // Access token rotation: s2-rust first keeps both tokens (the old one and the
                // pending one after initiateSession), then only the confirmed one.
                var tokens      = await client.WaitForEventAsync(e => e.Value<String>("event") == "access_tokens" && e["tokens"]!.Count() == 1,
                                                                 Description: "the confirmed access token at the s2-rust client");
                var candidates  = await store.GetAccessTokenCandidatesAsync(cem.Id, clientNodeId);

                Assert.Multiple(() => {
                    Assert.That(candidates,          Is.Not.Empty);
                    Assert.That(candidates[0].Value, Is.EqualTo(tokens["tokens"]![0]!.Value<String>()), "the rotated access token is active on both sides");
                    Assert.That(candidates[0],       Is.Not.EqualTo(accessToken), "the token was rotated");
                });

                // The s2-rust RM announces itself; the WWCP_S2 CEM selects FRBC.
                var details = SampleMessages.ResourceManagerDetails(Message_Id.NewRandom);
                await SendAsync(client, details);

                var detailsStatus = await client.WaitForEventAsync(e => e.Value<String>("event") == "reception_status" && e.Value<String>("subject_message_id") == details.MessageId.ToString(),
                                                                   Description: "the ReceptionStatus for the ResourceManagerDetails");
                Assert.That(detailsStatus.Value<String>("status"), Is.EqualTo("OK"));

                var select = await session.SendAndAwaitReceptionStatusAsync(new SelectControlType(ControlType.FillRateBasedControl));
                Assert.That(select.IsOK, Is.True, select.ReceptionStatus?.DiagnosticLabel);
                await WaitForReceivedAsync(client, "SelectControlType");

                // The s2-rust RM sends its system description (a WWCP_S2 sample, re-serialised by s2-rust).
                var systemDescription = SampleMessages.FRBC_SystemDescription(DateTimeOffset.UtcNow, Message_Id.NewRandom);
                await SendAsync(client, systemDescription);

                await Interop.WaitUntilAsync(() => frbc.LastSystemDescription is not null, Description: "the FRBC system description at the CEM");
                Assert.That(frbc.LastSystemDescription!.Actuators[0].Id, Is.EqualTo(systemDescription.Actuators[0].Id));

                // An instruction from the WWCP_S2 CEM, acknowledged by the s2-rust RM with NEW.
                var instruction = new FRBC_Instruction(Instruction_Id.NewRandom,
                                                       systemDescription.Actuators[0].Id,
                                                       systemDescription.Actuators[0].OperationModes[1].Id,
                                                       1.0,
                                                       DateTimeOffset.UtcNow,
                                                       AbnormalCondition: false);

                var instructed = await session.SendAndAwaitReceptionStatusAsync(instruction);
                Assert.That(instructed.IsOK, Is.True, instructed.ReceptionStatus?.DiagnosticLabel);
                await WaitForReceivedAsync(client, "FRBC.Instruction");

                await SendAsync(client, new InstructionStatusUpdate(instruction.Id, InstructionStatus.New, DateTimeOffset.UtcNow));

                await Interop.WaitUntilAsync(() => updates.Any(update => update.InstructionId == instruction.Id && update.StatusType == InstructionStatus.New),
                                             Description: "the InstructionStatusUpdate NEW at the CEM");

                recorder.AssertAllValid();

                // The s2-rust RM unpairs; the WWCP_S2 server drops the pairing and closes the session.
                await client.SendAsync(new JObject { ["cmd"] = "unpair" });
                await client.WaitForEventAsync("unpaired");
                await Interop.WaitAsync(unpaired.Task, Description: "the unpairing at the CEM");

                Assert.That(await store.GetPairingAsync(cem.Id, clientNodeId), Is.Null, "the pairing is removed at the server");

            }
            finally
            {
                await httpServer.Stop();
                await wsServer.Shutdown();
            }

        }

        #endregion

    }

}
