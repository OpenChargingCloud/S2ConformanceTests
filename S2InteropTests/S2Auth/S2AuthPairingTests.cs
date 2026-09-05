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

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.S2Auth
{

    /// <summary>
    /// S2 Connect pairing between WWCP S2 and s2auth 0.1.0 (libs/s2auth), the Python
    /// implementation of the pairing and session initiation flows: the s2auth client
    /// (an RM) against the WWCP S2 pairing server, and the WWCP S2 pairing client against
    /// the s2auth reference server, in the LAN (certificate fingerprint) and WAN (domain
    /// name) variants of the challenge-response.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.S2Auth)]
    public class S2AuthPairingTests
    {

        #region Data

        internal const String ClientDriver  = "s2auth_client.py";
        internal const String ServerDriver  = "s2auth_server.py";

        /// <summary>
        /// The alias of the s2auth server's pairing node (s2auth requires 8 to 12 characters).
        /// </summary>
        internal const String ServerAlias   = "S2AUTHCEM1";

        #endregion


        [OneTimeSetUp]
        public Task RequireS2Auth()
            => Interop.RequireS2AuthAsync();


        #region (internal) Helpers

        internal static String DeploymentName(Deployment Deployment)
            => Deployment == Deployment.LAN ? "LAN" : "WAN";

        /// <summary>
        /// Start the s2auth pairing client (an RM) against the given WWCP S2 server.
        /// </summary>
        internal static Task<ExternalProcess> StartS2AuthClientAsync(WWCPConnectServer  Server,
                                                                     String             Target,
                                                                     Deployment         Deployment,
                                                                     Node_Id            ClientNodeId,
                                                                     String?            Token   = null)

            => S2AuthHarness.StartAsync(ClientDriver,
                                        "--server-url",      Server.PairingUrlV1,
                                        "--token",           Token ?? Server.Token.Value,
                                        "--target",          Target,
                                        "--client-node-id",  ClientNodeId.ToString(),
                                        "--role",            "RM",
                                        "--deployment",      DeploymentName(Deployment),
                                        "--domain",          "localhost",
                                        "--ca",              Server.Certificate.CertificatePem,
                                        "--s2-version",      Version.S2JSONVersion);

        /// <summary>
        /// Let the s2auth client pair and return the outcome event (pair_result or error).
        /// </summary>
        internal static async Task<JObject> PairAsync(ExternalProcess Client)
        {

            await Client.WaitForEventAsync("ready", TimeSpan.FromSeconds(30));
            await Client.SendAsync(new JObject { ["command"] = "pair" });

            return await Client.WaitForEventAsync(e => e.Value<String>("event") is "pair_result" or "error",
                                                  TimeSpan.FromSeconds(60),
                                                  "the outcome of the pairing");

        }

        /// <summary>
        /// The HTTP responses the s2auth client received: (operation, status).
        /// </summary>
        internal static IReadOnlyList<(String Operation, Int32 Status)> ClientTrail(ExternalProcess Client)
            => Client.Events.Where (e => e.Value<String>("event") == "http_response").
                             Select(e => (e.Value<String>("url")!.TrimEnd('/').Split('/').Last(), e.Value<Int32>("status"))).
                             ToList();

        /// <summary>
        /// The HTTP requests the s2auth server handled: (path, status).
        /// </summary>
        internal static IReadOnlyList<(String Path, Int32 Status)> ServerTrail(ExternalProcess Server)
            => Server.Events.Where (e => e.Value<String>("event") == "http").
                             Select(e => (e.Value<String>("path")!, e.Value<Int32>("status"))).
                             ToList();

        /// <summary>
        /// A WWCP S2 endpoint hosting one RM node, with an empty store.
        /// </summary>
        internal static (LocalEndpoint Endpoint, HostedNode Node, InMemoryS2Store Store) WWCPResourceManager(Deployment Deployment)
        {

            var endpoint = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop RM endpoint"),
                                             Deployment,
                                             S2BaseURL.Parse("https://wwcp-rm.local/pairing/"));

            var node     = endpoint.AddNode(new NodeDescription(Node_Id.NewRandom, "GraphDefined", "EV charger", "WWCP_S2", EnergyManagementRole.RM));

            return (endpoint, node, new InMemoryS2Store());

        }

        /// <summary>
        /// Start the s2auth reference server (a CEM) with a self-signed "localhost"
        /// certificate and a one-time pairing token.
        /// </summary>
        internal static async Task<(ExternalProcess Server, ServerCertificate Certificate, IPPort Port, Node_Id ServerNodeId)>

            StartS2AuthServerAsync(Deployment  Deployment,
                                   String      Token   = "ABCD2345")

        {

            var certificate   = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);
            var port          = Interop.FreePort();

            // s2auth validates its own node identifications as UUID version 4 (pydantic UUID4),
            // while WWCP S2 generates version 7 identifications (Node_Id.NewRandom).
            var serverNodeId  = Node_Id.Parse(Guid.NewGuid().ToString());

            var server = await S2AuthHarness.StartAsync(ServerDriver,
                                                        "--port",             port.ToString(),
                                                        "--cert",             certificate.CertificatePem,
                                                        "--key",              certificate.KeyPem,
                                                        "--domain",           "localhost",
                                                        "--pairing-node-id",  ServerAlias,
                                                        "--server-node-id",   serverNodeId.ToString(),
                                                        "--token",            Token,
                                                        "--deployment",       DeploymentName(Deployment),
                                                        "--s2-version",       Version.S2JSONVersion);

            await server.WaitForEventAsync("listening", TimeSpan.FromSeconds(60));

            return (server, certificate, port, serverNodeId);

        }

        #endregion


        #region S2AuthClient_PairsWithTheWWCPPairingServer_LAN()

        [Test]
        [S2C("Pairing.LAN.S2AuthClient.WWCPServer")]
        public async Task S2AuthClient_PairsWithTheWWCPPairingServer_LAN()
        {

            await using var server  = await WWCPConnectServer.StartAsync(Deployment.LAN);

            var clientNodeId        = Node_Id.NewRandom;
            await using var client  = await StartS2AuthClientAsync(server, "CEM1", Deployment.LAN, clientNodeId);

            var outcome = await PairAsync(client);

            Assert.That(outcome.Value<String>("event"),     Is.EqualTo("pair_result"), () => $"{outcome}{Environment.NewLine}{client.Diagnostics}");
            Assert.That(outcome.Value<Boolean>("success"),  Is.True,                   () => client.Diagnostics);

            var pairing  = await server.Store.GetPairingAsync(server.CEM.Id, clientNodeId);
            var details  = outcome["connection_details"] as JObject;

            Assert.That(pairing, Is.Not.Null, "the WWCP S2 CEM stored the pairing");

            Assert.Multiple(() => {
                Assert.That(details?.Value<String>("access_token"),                         Is.EqualTo(pairing!.AccessToken.Value),           "both sides hold the same access token");
                Assert.That(details?.Value<String>("initiate_session_url")?.TrimEnd('/'),   Is.EqualTo(server.SessionUrl.Value.TrimEnd('/')), "s2auth received the session initiation URL of the WWCP S2 CEM");
                Assert.That(details?.Value<String>("client_s2_node_id"),                    Is.EqualTo(clientNodeId.ToString()));
                Assert.That(pairing!.RemoteNodeId,                                          Is.EqualTo(clientNodeId));
                Assert.That(pairing.LocalCommunicationRole,                                 Is.EqualTo(CommunicationRole.CommunicationServer), "a LAN CEM is the communication server");
                Assert.That(ClientTrail(client),                                            Is.EqualTo(new[] { ("requestPairing", 200), ("requestConnectionDetails", 200), ("finalizePairing", 204) }), "the three requests of an RM pairing client");
            });

        }

        #endregion

        #region S2AuthClient_PairsWithTheWWCPPairingServer_WAN()

        [Test]
        [S2C("Pairing.WAN.S2AuthClient.WWCPServer")]
        public async Task S2AuthClient_PairsWithTheWWCPPairingServer_WAN()
        {

            await using var server  = await WWCPConnectServer.StartAsync(Deployment.WAN);

            // Pairing by node identification this time (sent as nodeId, not nodeIdAlias).
            var clientNodeId        = Node_Id.NewRandom;
            await using var client  = await StartS2AuthClientAsync(server, server.CEM.Id.ToString(), Deployment.WAN, clientNodeId);

            var outcome = await PairAsync(client);

            Assert.That(outcome.Value<String>("event"),     Is.EqualTo("pair_result"), () => $"{outcome}{Environment.NewLine}{client.Diagnostics}");
            Assert.That(outcome.Value<Boolean>("success"),  Is.True,                   () => client.Diagnostics);

            var pairing  = await server.Store.GetPairingAsync(server.CEM.Id, clientNodeId);
            var details  = outcome["connection_details"] as JObject;

            Assert.That(pairing, Is.Not.Null, "the WWCP S2 CEM stored the pairing");

            Assert.Multiple(() => {
                Assert.That(details?.Value<String>("access_token"),  Is.EqualTo(pairing!.AccessToken.Value), "both sides hold the same access token");
                Assert.That(pairing!.RemoteNodeId,                   Is.EqualTo(clientNodeId));
                Assert.That(ClientTrail(client),                     Is.EqualTo(new[] { ("requestPairing", 200), ("requestConnectionDetails", 200), ("finalizePairing", 204) }));
            });

        }

        #endregion

        #region S2AuthClient_WithAWrongPairingToken_DetectsTheMismatch()

        /// <summary>
        /// With a wrong token the server's response to the client challenge does not verify:
        /// s2auth stops after requestPairing, and the WWCP S2 CEM never stores a pairing.
        /// </summary>
        [Test]
        [S2C("Pairing.LAN.S2AuthClient.WWCPServer.WrongToken")]
        public async Task S2AuthClient_WithAWrongPairingToken_DetectsTheMismatch()
        {

            await using var server  = await WWCPConnectServer.StartAsync(Deployment.LAN);

            var clientNodeId        = Node_Id.NewRandom;
            await using var client  = await StartS2AuthClientAsync(server, "CEM1", Deployment.LAN, clientNodeId, Token: "WRONG1234");

            var outcome = await PairAsync(client);

            Assert.Multiple(async () => {
                Assert.That(outcome.Value<String>("event"),  Is.EqualTo("error"),                                        () => $"{outcome}{Environment.NewLine}{client.Diagnostics}");
                Assert.That(outcome.Value<String>("type"),   Is.EqualTo("VerificationError"),                            () => outcome.ToString());
                Assert.That(ClientTrail(client),             Is.EqualTo(new[] { ("requestPairing", 200) }),               "s2auth stops after the failed verification");
                Assert.That(await server.Store.GetPairingAsync(server.CEM.Id, clientNodeId), Is.Null,                    "no pairing was stored");
            });

        }

        #endregion


        #region WWCPPairingClient_PairsWithTheS2AuthPairingServer_LAN()

        [Test]
        [S2C("Pairing.LAN.WWCPClient.S2AuthServer")]
        public async Task WWCPPairingClient_PairsWithTheS2AuthPairingServer_LAN()
        {

            var (server, certificate, port, serverNodeId) = await StartS2AuthServerAsync(Deployment.LAN);
            await using var _   = server;
            using       var __  = certificate;

            var (endpoint, node, store) = WWCPResourceManager(Deployment.LAN);

            await using var client = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/pairing/"),
                                                       endpoint,
                                                       store,
                                                       Deployment.LAN,
                                                       new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.PairAsync(node, PairingToken.Parse("ABCD2345"), PairingTarget.ByAlias(NodeIdAlias.Parse(ServerAlias)));

            Assert.That(result.IsSuccess, Is.True, () => $"{result}{Environment.NewLine}{server.Diagnostics}");

            var requested = server.Events.FirstOrDefault(e => e.Value<String>("event") == "pairing_requested");
            var trail     = ServerTrail(server);

            Assert.Multiple(() => {
                Assert.That(requested?.Value<String>("client_node_id"),          Is.EqualTo(node.Id.ToString()),                     "the s2auth server saw the WWCP S2 node");
                Assert.That(result.Pairing!.RemoteNodeId,                        Is.EqualTo(serverNodeId),                           "the server node description names the s2auth server node");
                Assert.That(result.Pairing.LocalCommunicationRole,               Is.EqualTo(CommunicationRole.CommunicationClient));
                Assert.That(result.Pairing.InitiateSessionUrl?.Value.TrimEnd('/'), Is.EqualTo($"https://localhost:{port}/connection"), "s2auth announces its connection initiation endpoint");
                Assert.That(trail, Does.Contain(($"/pairing/{Version.S2ConnectAPIVersion}/requestPairing",            200)));
                Assert.That(trail, Does.Contain(($"/pairing/{Version.S2ConnectAPIVersion}/requestConnectionDetails",  200)));
                Assert.That(trail, Does.Contain(($"/pairing/{Version.S2ConnectAPIVersion}/finalizePairing",           204)));
            });

        }

        #endregion

        #region WWCPPairingClient_PairsWithTheS2AuthPairingServer_WAN()

        [Test]
        [S2C("Pairing.WAN.WWCPClient.S2AuthServer")]
        public async Task WWCPPairingClient_PairsWithTheS2AuthPairingServer_WAN()
        {

            var (server, certificate, port, serverNodeId) = await StartS2AuthServerAsync(Deployment.WAN);
            await using var _   = server;
            using       var __  = certificate;

            var (endpoint, node, store) = WWCPResourceManager(Deployment.WAN);

            await using var client = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/pairing/"),
                                                       endpoint,
                                                       store,
                                                       Deployment.WAN,
                                                       new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.PairAsync(node, PairingToken.Parse("ABCD2345"), PairingTarget.ByAlias(NodeIdAlias.Parse(ServerAlias)));

            Assert.That(result.IsSuccess, Is.True, () => $"{result}{Environment.NewLine}{server.Diagnostics}");

            Assert.Multiple(() => {
                Assert.That(result.Pairing!.RemoteNodeId,           Is.EqualTo(serverNodeId));
                Assert.That(result.Pairing.LocalCommunicationRole,  Is.EqualTo(CommunicationRole.CommunicationClient));
                Assert.That(ServerTrail(server),                    Does.Contain(($"/pairing/{Version.S2ConnectAPIVersion}/finalizePairing", 204)));
            });

        }

        #endregion

        #region WWCPPairingClient_WithAWrongPairingToken_IsRejected()

        /// <summary>
        /// With a wrong token the WWCP S2 client detects that the server's response to its
        /// challenge does not verify: it never requests the connection details and reports
        /// the failure to the s2auth server with finalizePairing (success: false).
        /// </summary>
        [Test]
        [S2C("Pairing.LAN.WWCPClient.S2AuthServer.WrongToken")]
        public async Task WWCPPairingClient_WithAWrongPairingToken_IsRejected()
        {

            var (server, certificate, port, _) = await StartS2AuthServerAsync(Deployment.LAN);
            await using var __   = server;
            using       var ___  = certificate;

            var (endpoint, node, store) = WWCPResourceManager(Deployment.LAN);

            await using var client = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/pairing/"),
                                                       endpoint,
                                                       store,
                                                       Deployment.LAN,
                                                       new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.PairAsync(node, PairingToken.Parse("WRONG1234"), PairingTarget.ByAlias(NodeIdAlias.Parse(ServerAlias)));

            Assert.Multiple(() => {
                Assert.That(result.IsSuccess,      Is.False,   () => result.ToString());
                Assert.That(ServerTrail(server),   Has.None.Matches<(String Path, Int32 Status)>(entry => entry.Path.EndsWith("/requestConnectionDetails")), "the connection details were never requested");
                Assert.That(ServerTrail(server),   Does.Contain(($"/pairing/{Version.S2ConnectAPIVersion}/finalizePairing", 204)),                                       "the failure was reported with finalizePairing");
            });

        }

        #endregion

    }

}
