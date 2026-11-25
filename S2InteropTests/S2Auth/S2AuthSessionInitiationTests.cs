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

using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.S2Auth
{

    /// <summary>
    /// S2 Connect session initiation (initiateSession, confirmAccessToken, unpair) between
    /// WWCP S2 and s2auth 0.1.2, after a pairing in the same test. s2auth implements the
    /// HTTP part of session initiation only; it has no S2 communication server, so no S2
    /// WebSocket session is opened here. The s2auth client never reaches WWCP S2's session
    /// initiation API at all (A1 below: it posts to its pairing URL, and Hermod does not
    /// mount two APIs under one path), so the positive path is only run with the WWCP S2
    /// client against the s2auth server.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.S2Auth)]
    public class S2AuthSessionInitiationTests
    {

        [OneTimeSetUp]
        public Task RequireS2Auth()
            => Interop.RequireS2AuthAsync();


        #region (private) Helpers

        private static async Task<JObject> SendCommandAsync(ExternalProcess  Client,
                                                            String           Command,
                                                            String           ResultEvent)
        {

            await Client.SendAsync(new JObject { ["command"] = Command });

            return await Client.WaitForEventAsync(e => e.Value<String>("event") == ResultEvent || e.Value<String>("event") == "error",
                                                  TimeSpan.FromSeconds(60),
                                                  $"the outcome of '{Command}'");

        }

        private static JObject? LastResponseTo(ExternalProcess Client, String Operation)
            => Client.Events.LastOrDefault(e => e.Value<String>("event") == "http_response" &&
                                                e.Value<String>("url")!.TrimEnd('/').EndsWith("/" + Operation, StringComparison.Ordinal));

        #endregion


        #region S2AuthClient_InitiatesASessionWithTheWWCPServer()

        /// <summary>
        /// WWCP S2 as deployed: pairing under /pairing/, session initiation under /connection/,
        /// the latter announced as initiateSessionUrl in the connection details. s2auth ignores
        /// that URL and posts initiateSession to its pairing URL (A1).
        /// </summary>
        [Test]
        [S2C("SessionInitiation.S2AuthClient.WWCPServer")]
        public async Task S2AuthClient_InitiatesASessionWithTheWWCPServer()
        {

            await using var server  = await WWCPConnectServer.StartAsync(Deployment.LAN);

            var clientNodeId        = Node_Id.NewRandom;
            await using var client  = await S2AuthPairingTests.StartS2AuthClientAsync(server, server.CEM.Id.ToString(), Deployment.LAN, clientNodeId);

            var paired = await S2AuthPairingTests.PairAsync(client);
            Assert.That(paired.Value<Boolean>("success"), Is.True, () => $"{paired}{Environment.NewLine}{client.Diagnostics}");

            var outcome   = await SendCommandAsync(client, "connect", "connect_result");
            var initiate  = LastResponseTo(client, "initiateSession");

            Interop.KnownIssue("s2auth posts initiateSession (and unpair) to its pairing URL instead of the initiateSessionUrl received during pairing",
                               initiate is not null &&
                               initiate.Value<String>("url")!.StartsWith(server.PairingUrlV1, StringComparison.Ordinal) &&
                               initiate.Value<Int32>("status") == 404 &&
                               outcome.Value<String>("event") == "error",
                               $"initiateSession was posted to {initiate?["url"]} => {initiate?["status"]}; outcome: {outcome}");

        }

        #endregion

        #region S2AuthClient_PairedByAlias_CannotInitiateASession()

        /// <summary>
        /// s2auth keeps the pairing target as the server node identification and never stores
        /// the serverNodeDescription.id it received; after pairing by alias, initiateSession is
        /// therefore impossible (A2).
        /// </summary>
        [Test]
        [S2C("SessionInitiation.S2AuthClient.WWCPServer.ByAlias")]
        public async Task S2AuthClient_PairedByAlias_CannotInitiateASession()
        {

            await using var server  = await WWCPConnectServer.StartAsync(Deployment.LAN);

            var clientNodeId        = Node_Id.NewRandom;
            await using var client  = await S2AuthPairingTests.StartS2AuthClientAsync(server, "CEM1", Deployment.LAN, clientNodeId);

            var paired = await S2AuthPairingTests.PairAsync(client);
            Assert.That(paired.Value<Boolean>("success"), Is.True, () => $"{paired}{Environment.NewLine}{client.Diagnostics}");

            var outcome = await SendCommandAsync(client, "connect", "connect_result");

            Interop.KnownIssue("s2auth uses the pairing target instead of the received serverNodeDescription.id as the server node identification, so a pairing by alias cannot initiate a session",
                               outcome.Value<String>("event") == "error" &&
                               outcome.Value<String>("type")  == "ValueError" &&
                               LastResponseTo(client, "initiateSession") is null,
                               outcome.ToString());

        }

        #endregion

        #region WWCPClient_InitiatesASessionWithTheS2AuthServer()

        /// <summary>
        /// The WWCP S2 client follows the initiateSessionUrl s2auth announced (/connection/),
        /// where s2auth serves initiateSession with bearer authorization (A3, fixed in s2auth
        /// a733abf) and confirmAccessToken returns the WebSocketCommunicationDetails. A second
        /// session initiation, with the rotated token, would prove that confirmAccessToken also
        /// activated it; it does not (A4).
        /// </summary>
        [Test]
        [S2C("SessionInitiation.WWCPClient.S2AuthServer")]
        public async Task WWCPClient_InitiatesASessionWithTheS2AuthServer()
        {

            var (server, certificate, port, serverNodeId) = await S2AuthPairingTests.StartS2AuthServerAsync(Deployment.LAN);
            await using var _   = server;
            using       var __  = certificate;

            var (endpoint, node, store) = S2AuthPairingTests.WWCPResourceManager(Deployment.LAN);

            await using var pairingClient = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/pairing/"),
                                                              endpoint,
                                                              store,
                                                              Deployment.LAN,
                                                              new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var paired = await pairingClient.PairAsync(node, PairingToken.Parse("ABCD2345"), PairingTarget.ByAlias(NodeIdAlias.Parse(S2AuthPairingTests.ServerAlias)));

            Assert.That(paired.IsSuccess, Is.True, () => $"{paired}{Environment.NewLine}{server.Diagnostics}");
            Assert.That(paired.Pairing!.InitiateSessionUrl, Is.Not.Null);

            await using var client = new SessionInitiationClient(paired.Pairing.InitiateSessionUrl!.Value,
                                                                 endpoint,
                                                                 store,
                                                                 new SessionInitiationClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) },
                                                                 WebSocketCertificateValidator: Certificates.AcceptAny<IWebSocketClient>());

            // 1. With the access token from pairing.
            var first  = await client.InitiateSessionAsync(node, serverNodeId);
            var trail  = await S2AuthPairingTests.ServerTrailAsync(server, "/confirmAccessToken");

            Assert.That(first.IsSuccess, Is.True, () => $"{first}{Environment.NewLine}trail: {String.Join(", ", trail)}{Environment.NewLine}{server.Diagnostics}");

            Assert.Multiple(() => {
                Assert.That(trail, Does.Contain(($"/connection/{Version.S2ConnectAPIVersion}/initiateSession",      200)), "initiateSession at the announced initiateSessionUrl");
                Assert.That(trail, Does.Contain(($"/connection/{Version.S2ConnectAPIVersion}/confirmAccessToken",   200)), "confirmAccessToken at the announced initiateSessionUrl");
                Assert.That(first.CommunicationDetails, Is.InstanceOf<WebSocketCommunicationDetails>(),                    "confirmAccessToken returns the WebSocketCommunicationDetails");
                Assert.That((first.CommunicationDetails as WebSocketCommunicationDetails)?.WebsocketUrl.ToString(),
                            Is.EqualTo($"wss://localhost:{port}/websocket/"),                                              "the websocketUrl s2auth was configured with (CEM_URL)");
            });

            // 2. With the access token rotated in step 1: only accepted if confirmAccessToken activated it.
            //    s2auth's confirmAccessToken activates the token on a deep copy of the authentication
            //    context (list_contexts) that is never stored, so the server still expects the old
            //    token; the InvalidAccessTokenError that follows is answered with 500, not 401.
            var second    = await client.InitiateSessionAsync(node, serverNodeId);
            var rejected  = !second.IsSuccess &&
                            await server.SawEventAsync(e => e.Value<String>("event") == "http" &&
                                                            e.Value<String>("path")  == $"/connection/{Version.S2ConnectAPIVersion}/initiateSession" &&
                                                            e.Value<Int32>("status") is 401 or 500);

            Interop.KnownIssue("s2auth's confirmAccessToken does not activate the pending access token (it changes a copy of the authentication context), so a second session initiation with the rotated token fails",
                               rejected,
                               $"{second}{Environment.NewLine}trail: {String.Join(", ", S2AuthPairingTests.ServerTrail(server))}");

        }

        #endregion

    }

}
