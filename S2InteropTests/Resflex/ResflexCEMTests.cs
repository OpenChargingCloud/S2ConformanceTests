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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Node;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Resflex
{

    /// <summary>
    /// The WWCP S2 Resource Manager against the S2 CEM library of the ElaadNL Residential
    /// Flexibility initiative (libs/resflex-open-s2-hems-connector, s2_cem_lib), wired by
    /// tools/resflex-harness/resflex_cem.py the way the library's standalone engine wires it:
    /// S2 Connect pairing over TLS (LAN), session initiation with access token rotation, the
    /// WebSocket session with Power Envelope Based Control (the only control type the library
    /// implements), and unpairing.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Resflex)]
    public sealed class ResflexCEMTests
    {

        #region Data

        private const String CEMDriver  = "resflex_cem.py";
        private const String Token      = "ABCD2345";

        /// <summary>
        /// The S2 message version the library's standalone engine supports (cem_engine/run.py).
        /// </summary>
        private const String S2MessageVersion  = "0.0.2-beta";

        #endregion

        #region Setup

        [OneTimeSetUp]
        public Task RequireResflex()
            => Interop.RequireResflexAsync();

        #endregion


        #region (private) Helpers

        private sealed record CEM(ExternalProcess    Process,
                                  ServerCertificate  Certificate,
                                  IPPort             Port,
                                  Node_Id            NodeId,
                                  Boolean            OwnsCertificate) : IAsyncDisposable
        {

            public S2BaseURL PairingUrl
                => S2BaseURL.Parse($"https://localhost:{Port}/api/s2_pebc/pairing/");

            /// <summary>
            /// The HTTP requests the CEM answered: (path, status).
            /// </summary>
            public IReadOnlyList<(String Path, Int32 Status)> Trail
                => Process.Events.Where (e => e.Value<String>("event") == "http").
                                  Select(e => (e.Value<String>("path")!, e.Value<Int32>("status"))).
                                  ToList();

            /// <summary>
            /// The HTTP requests the CEM answered, once the report of the last request the test
            /// made (the path ending with LastPath) has arrived: the HTTP response can overtake
            /// the driver's report of it (see ExternalProcess.SawEventAsync).
            /// </summary>
            public async Task<IReadOnlyList<(String Path, Int32 Status)>> TrailAsync(String LastPath)
            {
                await Process.SawEventAsync(e => e.Value<String>("event") == "http" && e.Value<String>("path")?.EndsWith(LastPath, StringComparison.Ordinal) == true);
                return Trail;
            }

            /// <summary>
            /// The paired nodes and their access tokens the CEM persisted last.
            /// </summary>
            public JObject? Persisted
                => Process.Events.LastOrDefault(e => e.Value<String>("event") == "persisted")?["nodes"] as JObject;

            public async ValueTask DisposeAsync()
            {
                await Process.DisposeAsync();
                if (OwnsCertificate)
                    Certificate.Dispose();
            }

        }

        /// <summary>
        /// Start the ElaadNL CEM with a self-signed "localhost" certificate and a fixed pairing
        /// token; or, given the certificate, port, node and state file of a previous one, the
        /// same CEM again (a restart).
        /// </summary>
        private static async Task<CEM> StartCEMAsync(CEM?     Previous   = null,
                                                     String?  State      = null)
        {

            var certificate  = Previous?.Certificate ?? ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);
            var port         = Previous?.Port        ?? Interop.FreePort();
            var nodeId       = Previous?.NodeId      ?? Node_Id.NewRandom;

            var process      = await ResflexHarness.StartAsync(CEMDriver,
                                                               [ "--port",     port.ToString(),
                                                                 "--cert",     certificate.CertificatePem,
                                                                 "--key",      certificate.KeyPem,
                                                                 "--node-id",  nodeId.ToString(),
                                                                 "--token",    Token,
                                                                 .. State is null ? Array.Empty<String>() : [ "--state", State ] ]);

            await process.WaitForEventAsync("listening", TimeSpan.FromSeconds(60));

            return new CEM(process, certificate, port, nodeId, OwnsCertificate: Previous is null);

        }

        /// <summary>
        /// A WWCP S2 endpoint hosting one RM node, with an empty store. The node announces the
        /// legacy version string as well (v1.0.0 and 0.0.2-beta): the ElaadNL CEM supports only
        /// the latter and answers a requestPairing without it with IncompatibleS2MessageVersions.
        /// </summary>
        private static (LocalEndpoint Endpoint, HostedNode Node, InMemoryS2Store Store) WWCPResourceManager()
        {

            var endpoint = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop RM endpoint"),
                                             Deployment.LAN,
                                             S2BaseURL.Parse("https://wwcp-rm.local/pairing/"));

            var node     = endpoint.AddNode(new NodeDescription(Node_Id.NewRandom, "GraphDefined", "EV charger", "WWCP_S2", EnergyManagementRole.RM),
                                            SupportedS2MessageVersions: Version.S2JSONVersions);

            return (endpoint, node, new InMemoryS2Store());

        }

        private static PairingClient NewPairingClient(CEM CEM, LocalEndpoint Endpoint, InMemoryS2Store Store)

            => new (CEM.PairingUrl,
                    Endpoint,
                    Store,
                    Deployment.LAN,
                    new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

        private static Task<JObject> WaitForReceivedAsync(CEM CEM, String MessageType, Func<JObject, Boolean>? Filter = null)
            => CEM.Process.WaitForEventAsync(e => e.Value<String>("event") == "received" &&
                                                  e["message"]?.Value<String>("message_type") == MessageType &&
                                                  (Filter is null || Filter((JObject) e["message"]!)),
                                             Description: $"the ElaadNL CEM receiving a {MessageType}");

        private static Task<JObject> WaitForSentAsync(CEM CEM, String MessageType, Func<JObject, Boolean>? Filter = null)
            => CEM.Process.WaitForEventAsync(e => e.Value<String>("event") == "sent" &&
                                                  e["message"]?.Value<String>("message_type") == MessageType &&
                                                  (Filter is null || Filter((JObject) e["message"]!)),
                                             Description: $"the ElaadNL CEM sending a {MessageType}");

        /// <summary>
        /// ResourceManagerDetails offering PEBC: the library answers any other offer with
        /// PERMANENT_ERROR and closes the connection.
        /// </summary>
        private static ResourceManagerDetails PEBCResourceManagerDetails()

            => new (Resource_Id.NewRandom,
                    [ new Role(RoleType.EnergyConsumer, Commodity.Electricity) ],
                    Duration.FromMilliseconds(3000),
                    [ ControlType.PowerEnvelopeBasedControl, ControlType.NotControllable ],
                    ProvidesForecast:               false,
                    ProvidesPowerMeasurementTypes:  [ CommodityQuantity.ElectricPower3PhaseSymmetric ],
                    Name:                           "WWCP S2 interop RM",
                    Manufacturer:                   "GraphDefined",
                    Model:                          "WWCP_S2",
                    SerialNumber:                   "1",
                    FirmwareVersion:                "v1.0",
                    Currency:                       Currency.EUR);

        /// <summary>
        /// Power constraints valid from now on for an hour: the library drops expired ones.
        /// </summary>
        private static PEBC_PowerConstraints CurrentPowerConstraints()

            => new (PowerConstraints_Id.NewRandom,
                    DateTimeOffset.UtcNow,
                    PEBC_PowerEnvelopeConsequenceType.Vanish,
                    [ new PEBC_AllowedLimitRange(CommodityQuantity.ElectricPower3PhaseSymmetric, PEBC_PowerEnvelopeLimitType.UpperLimit, new NumberRange(0,      11000), false),
                      new PEBC_AllowedLimitRange(CommodityQuantity.ElectricPower3PhaseSymmetric, PEBC_PowerEnvelopeLimitType.LowerLimit, new NumberRange(-11000, 0),     false) ],
                    ValidUntil: DateTimeOffset.UtcNow.AddHours(1));

        #endregion


        #region WWCPPairingClient_PairsWithTheResflexCEM_LAN()

        [Test]
        [S2C("Pairing.LAN.WWCPClient.ResflexCEM")]
        public async Task WWCPPairingClient_PairsWithTheResflexCEM_LAN()
        {

            await using var cem = await StartCEMAsync();

            var (endpoint, node, store) = WWCPResourceManager();
            await using var client      = NewPairingClient(cem, endpoint, store);

            var result = await client.PairAsync(node, PairingToken.Parse(Token), PairingTarget.ByNodeId(cem.NodeId));

            Assert.That(result.IsSuccess, Is.True, () => $"{result}{Environment.NewLine}{cem.Process.Diagnostics}");

            var trail = await cem.TrailAsync("/finalizePairing");

            Assert.Multiple(() => {
                Assert.That(result.Pairing!.RemoteNodeId,                           Is.EqualTo(cem.NodeId),                                         "the server node description names the ElaadNL CEM");
                Assert.That(result.Pairing.LocalCommunicationRole,                  Is.EqualTo(CommunicationRole.CommunicationClient),               "a LAN RM is the communication client");
                Assert.That(result.Pairing.InitiateSessionUrl?.Value.TrimEnd('/'),  Is.EqualTo($"https://localhost:{cem.Port}/api/s2_pebc/connection"), "the CEM announces its session initiation endpoint");
                Assert.That(trail, Does.Contain(($"/api/s2_pebc/pairing/{Version.S2ConnectAPIVersion}/requestPairing",            200)));
                Assert.That(trail, Does.Contain(($"/api/s2_pebc/pairing/{Version.S2ConnectAPIVersion}/requestConnectionDetails",  200)));
                Assert.That(trail, Does.Contain(($"/api/s2_pebc/pairing/{Version.S2ConnectAPIVersion}/finalizePairing",           204)));
            });

        }

        #endregion

        #region WWCPPairingClient_WithAWrongPairingToken_IsRejected()

        /// <summary>
        /// With a wrong token the WWCP S2 client detects that the CEM's response to its
        /// challenge does not verify: it never requests the connection details and reports the
        /// failure with finalizePairing (success: false).
        /// </summary>
        [Test]
        [S2C("Pairing.LAN.WWCPClient.ResflexCEM.WrongToken")]
        public async Task WWCPPairingClient_WithAWrongPairingToken_IsRejected()
        {

            await using var cem = await StartCEMAsync();

            var (endpoint, node, store) = WWCPResourceManager();
            await using var client      = NewPairingClient(cem, endpoint, store);

            var result = await client.PairAsync(node, PairingToken.Parse("WRONG1234"), PairingTarget.ByNodeId(cem.NodeId));
            var trail  = await cem.TrailAsync("/finalizePairing");

            Assert.Multiple(async () => {
                Assert.That(result.IsSuccess,  Is.False, () => result.ToString());
                Assert.That(trail,             Has.None.Matches<(String Path, Int32 Status)>(entry => entry.Path.EndsWith("/requestConnectionDetails")), "the connection details were never requested");
                Assert.That(trail,             Does.Contain(($"/api/s2_pebc/pairing/{Version.S2ConnectAPIVersion}/finalizePairing", 204)),                  "the failure was reported with finalizePairing");
                Assert.That(await store.GetPairingAsync(node.Id, cem.NodeId), Is.Null,                                                                     "no pairing was stored on the WWCP S2 side");
            });

            // S2 Connect 1.0.0, "Interruption of the process": a pairing attempt that does not
            // complete successfully is a failed attempt. The CEM records the RM as paired at
            // requestPairing, before either challenge has been answered, and keeps it after
            // finalizePairing with success: false (FINDINGS.md, E1).
            var persisted = cem.Persisted;

            Interop.KnownIssue("resflex E1: the CEM keeps the RM of a failed pairing attempt as paired",
                               persisted?.ContainsKey(node.Id.ToString()) == true,
                               $"persisted after finalizePairing (success: false): {persisted?.ToString(Newtonsoft.Json.Formatting.None)}");

        }

        #endregion

        #region WWCPResourceManager_RunsAPEBCSessionWithTheResflexCEM()

        /// <summary>
        /// Pairing, session initiation and the WebSocket session: the CEM answers the
        /// ResourceManagerDetails with SelectControlType PEBC, the WWCP S2 RM sends its power
        /// constraints, the CEM sends a PEBC.Instruction within them, and the RM acknowledges it
        /// with an InstructionStatusUpdate NEW.
        /// </summary>
        [Test]
        [S2C("SessionInitiation.WWCPClient.ResflexCEM")]
        public async Task WWCPResourceManager_RunsAPEBCSessionWithTheResflexCEM()
        {

            await using var cem = await StartCEMAsync();

            var (endpoint, node, store) = WWCPResourceManager();
            await using var pairingClient = NewPairingClient(cem, endpoint, store);

            var paired = await pairingClient.PairAsync(node, PairingToken.Parse(Token), PairingTarget.ByNodeId(cem.NodeId));
            Assert.That(paired.IsSuccess, Is.True, () => $"{paired}{Environment.NewLine}{cem.Process.Diagnostics}");

            await using var client = new SessionInitiationClient(paired.Pairing!.InitiateSessionUrl!.Value,
                                                                 endpoint,
                                                                 store,
                                                                 new SessionInitiationClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) },
                                                                 WebSocketCertificateValidator: Certificates.AcceptAny<IWebSocketClient>());

            // Session initiation (version index, initiateSession, confirmAccessToken) and the WebSocket.
            var connect = await client.ConnectAsync(node, cem.NodeId);

            Assert.That(connect.IsSuccess, Is.True, () => $"{connect.Initiation}{Environment.NewLine}{cem.Process.Diagnostics}");

            await using var s2   = connect.Session!;
            var session          = s2.Session;

            var instructions     = new List<PEBC_Instruction>();
            var pebc             = new PEBCResourceManager(CurrentPowerConstraints);

            pebc.OnInstruction += (s, instruction, ct) => {
                lock (instructions)
                    instructions.Add(instruction);
                return Task.FromResult<ReceptionStatusValue?>(null);
            };

            session.RegisterControlType(pebc);
            using var recorder   = new TrafficRecorder(session);

            await cem.Process.WaitForEventAsync(e => e.Value<String>("event") == "ws_open" && e.Value<String>("node_id") == node.Id.ToString(),
                                                Description: "the WebSocket at the ElaadNL CEM");

            // Access token rotation: the token confirmed with confirmAccessToken is the one the CEM persisted.
            var candidates = await store.GetAccessTokenCandidatesAsync(node.Id, cem.NodeId);

            Assert.Multiple(() => {
                Assert.That(connect.Initiation.SelectedS2MessageVersion,                Is.EqualTo(S2MessageVersion),                "the only version the ElaadNL CEM supports");
                Assert.That(session.NegotiatedVersion,                                  Is.EqualTo(S2MessageVersion));
                Assert.That(candidates,                                                 Is.Not.Empty);
                Assert.That(cem.Persisted?.Value<String>(node.Id.ToString()),           Is.EqualTo(candidates[0].Value),             "the rotated access token is active on both sides");
                Assert.That(candidates[0],                                              Is.Not.EqualTo(paired.Pairing.AccessToken),  "the token was rotated");
            });

            // The RM announces itself; the CEM acknowledges and selects PEBC.
            var details = await session.SendAndAwaitReceptionStatusAsync(PEBCResourceManagerDetails());
            Assert.That(details.IsOK, Is.True, details.ReceptionStatus?.DiagnosticLabel);

            await Interop.WaitUntilAsync(() => session.ActiveControlType == ControlType.PowerEnvelopeBasedControl, Description: "the activation of PEBC");

            // On activation the PEBC handler sends its power constraints; the CEM stores and acknowledges them.
            await Interop.WaitUntilAsync(() => pebc.SentPowerConstraints is not null, Description: "the power constraints accepted by the CEM");

            var constraints = pebc.SentPowerConstraints!;
            var received    = await WaitForReceivedAsync(cem, "PEBC.PowerConstraints");

            Assert.That(received["message"]!.Value<String>("id"), Is.EqualTo(constraints.Id.ToString()));

            // A PEBC.Instruction from the CEM within the constraints, acknowledged by the RM with NEW.
            await cem.Process.SendAsync(new JObject {
                                            ["command"]       = "send_instruction",
                                            ["node_id"]       = node.Id.ToString(),
                                            ["upper_limit"]   = 4000,
                                            ["lower_limit"]   = 0,
                                            ["duration_ms"]   = 900000
                                        });

            var sent = await cem.Process.WaitForEventAsync(e => e.Value<String>("event") is "instruction_sent" or "error",
                                                           Description: "the PEBC.Instruction of the ElaadNL CEM");

            Assert.That(sent.Value<String>("event"), Is.EqualTo("instruction_sent"), () => sent.ToString());

            await Interop.WaitUntilAsync(() => instructions.Count == 1, Description: "the PEBC.Instruction at the RM");

            var instruction = instructions[0];
            var envelope    = instruction.PowerEnvelopes[0];

            Assert.Multiple(() => {
                Assert.That(instruction.Id.ToString(),                  Is.EqualTo(sent.Value<String>("instruction_id")));
                Assert.That(instruction.PowerConstraintsId,             Is.EqualTo(constraints.Id),                                 "the instruction refers to the RM's power constraints");
                Assert.That(envelope.CommodityQuantity,                 Is.EqualTo(CommodityQuantity.ElectricPower3PhaseSymmetric));
                Assert.That(envelope.PowerEnvelopeElements[0].UpperLimit, Is.EqualTo(4000));
                Assert.That(envelope.PowerEnvelopeElements[0].LowerLimit, Is.EqualTo(0));
            });

            var update = await WaitForReceivedAsync(cem, "InstructionStatusUpdate", message => message.Value<String>("instruction_id") == instruction.Id.ToString());
            Assert.That(update["message"]!.Value<String>("status_type"), Is.EqualTo("NEW"));

            await WaitForSentAsync(cem, "ReceptionStatus", message => message.Value<String>("subject_message_id") == update["message"]!.Value<String>("message_id") &&
                                                                       message.Value<String>("status")             == "OK");

            recorder.AssertAllValid();

            // The RM closes the session; the CEM sees the WebSocket close.
            await s2.CloseAsync("test finished");
            await cem.Process.WaitForEventAsync("ws_closed");

        }

        #endregion

        #region WWCPClient_UnpairsAtTheResflexCEM()

        [Test]
        [S2C("Unpairing.WWCPClient.ResflexCEM")]
        public async Task WWCPClient_UnpairsAtTheResflexCEM()
        {

            await using var cem = await StartCEMAsync();

            var (endpoint, node, store) = WWCPResourceManager();
            await using var pairingClient = NewPairingClient(cem, endpoint, store);

            var paired = await pairingClient.PairAsync(node, PairingToken.Parse(Token), PairingTarget.ByNodeId(cem.NodeId));
            Assert.That(paired.IsSuccess, Is.True, () => $"{paired}{Environment.NewLine}{cem.Process.Diagnostics}");

            await using var client = new SessionInitiationClient(paired.Pairing!.InitiateSessionUrl!.Value,
                                                                 endpoint,
                                                                 store,
                                                                 new SessionInitiationClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.UnpairAsync(node.Id, cem.NodeId);

            Assert.That(result.IsSuccess, Is.True, () => $"{result}{Environment.NewLine}{cem.Process.Diagnostics}");

            // The CEM persists before it answers, so its report of the request settles Persisted as well.
            var trail = await cem.TrailAsync("/unpair");

            Assert.Multiple(async () => {
                Assert.That(trail,                                              Does.Contain(($"/api/s2_pebc/connection/{Version.S2ConnectAPIVersion}/unpair", 204)));
                Assert.That(cem.Persisted?.ContainsKey(node.Id.ToString()),     Is.False, "the CEM dropped the pairing");
                Assert.That(await store.GetPairingAsync(node.Id, cem.NodeId),   Is.Null,  "the local pairing is removed");
            });

        }

        #endregion

        #region WWCPResourceManager_ConnectsAfterARestartOfTheResflexCEM()

        /// <summary>
        /// A pairing has to survive a restart of the CEM. The access token the RM holds is the
        /// one of the connection details (requestConnectionDetails); the CEM persists only the
        /// one it created at requestPairing, before the challenges, and replaces it in memory
        /// without persisting it again (FINDINGS.md, E2). Until the first session, which
        /// persists the rotated token, a restarted CEM therefore refuses the RM.
        /// </summary>
        [Test]
        [S2C("SessionInitiation.WWCPClient.ResflexCEM.AfterRestart")]
        public async Task WWCPResourceManager_ConnectsAfterARestartOfTheResflexCEM()
        {

            var state = Path.Combine(Path.GetTempPath(), $"resflex-state-{Guid.NewGuid():N}.json");

            try
            {

                await using var cem = await StartCEMAsync(State: state);

                var (endpoint, node, store) = WWCPResourceManager();
                await using var pairingClient = NewPairingClient(cem, endpoint, store);

                var paired = await pairingClient.PairAsync(node, PairingToken.Parse(Token), PairingTarget.ByNodeId(cem.NodeId));
                Assert.That(paired.IsSuccess, Is.True, () => $"{paired}{Environment.NewLine}{cem.Process.Diagnostics}");

                var persistedBefore = cem.Persisted?.Value<String>(node.Id.ToString());

                // The same CEM again: same certificate, port, node and state file.
                await cem.Process.DisposeAsync();
                await using var restarted = await StartCEMAsync(cem, state);

                await using var client = new SessionInitiationClient(paired.Pairing!.InitiateSessionUrl!.Value,
                                                                     endpoint,
                                                                     store,
                                                                     new SessionInitiationClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) },
                                                                     WebSocketCertificateValidator: Certificates.AcceptAny<IWebSocketClient>());

                var connect = await client.ConnectAsync(node, cem.NodeId);

                if (connect.IsSuccess)
                    await connect.Session!.DisposeAsync();

                var staleTokenPersisted = persistedBefore is not null && persistedBefore != paired.Pairing.AccessToken.Value;

                if (!connect.IsSuccess && !staleTokenPersisted)
                    Assert.Fail($"The session initiation after the restart failed for another reason than E2: {connect.Initiation}{Environment.NewLine}{restarted.Process.Diagnostics}");

                Interop.KnownIssue("resflex E2: the CEM persists the access token of requestPairing, not the one of requestConnectionDetails, so a restart before the first session loses the pairing",
                                   staleTokenPersisted && !connect.IsSuccess,
                                   $"initiateSession after the restart: {connect.Initiation}");

            }
            finally
            {
                File.Delete(state);
            }

        }

        #endregion

    }

}
