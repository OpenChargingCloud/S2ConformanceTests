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

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Rust
{

    /// <summary>
    /// S2 Connect pairing against s2-rust (s2energy-connection::pairing) over real TLS, in both
    /// directions: the WWCP_S2 PairingClient against the s2-rust pairing server, and the
    /// s2-rust pairing client against the WWCP_S2 PairingServerAPI. On Windows the s2-rust
    /// drivers run inside WSL (see RustHarness); the reverse direction then needs the Windows
    /// firewall to admit the test host on the WSL network and is skipped otherwise.
    ///
    /// The pairing token is handed to s2-rust as the ASCII bytes of its text, which is what
    /// S2 Connect 1.0.0 prescribes for the challenge-response function; s2-rust's own
    /// PairingToken::from_str (Base64) and its WAN formula are covered as known issues.
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Rust)]
    public sealed class RustPairingTests
    {

        #region Data

        private const String PairingServer  = "s2-pairing-server";
        private const String PairingClient  = "s2-pairing-client";

        private const String TokenEncodingIssue  = RustChallengeResponseTests.TokenEncodingIssue;
        private const String WANFormulaIssue     = RustChallengeResponseTests.WANFormulaIssue;

        #endregion

        #region Setup

        [OneTimeSetUp]
        public Task BuildHarness()
            => Interop.RequireRustAsync(PairingServer);

        #endregion


        #region (private) Helpers

        private static String BindAddress(String Binary)
            => RustHarness.RunsInWSL(Binary) ? "0.0.0.0" : "127.0.0.1";

        /// <summary>
        /// Start an s2-rust pairing server hosting one CEM node with the given alias and token.
        /// </summary>
        private static async Task<ExternalProcess> StartPairingServerAsync(ServerCertificate  Certificate,
                                                                            IPPort             Port,
                                                                            Node_Id            NodeId,
                                                                            String             Alias,
                                                                            PairingToken       Token,
                                                                            String             Deployment      = "LAN",
                                                                            String             TokenEncoding   = "ascii")
        {

            var server = await RustHarness.StartAsync(PairingServer,
                                                      "--bind",           BindAddress(PairingServer),
                                                      "--port",           Port.ToString(),
                                                      "--cert",           RustHarness.PathForDriver(PairingServer, Certificate.CertificatePem),
                                                      "--key",            RustHarness.PathForDriver(PairingServer, Certificate.KeyPem),
                                                      "--deployment",     Deployment,
                                                      "--role",           "CEM",
                                                      "--node-id",        NodeId.ToString(),
                                                      "--alias",          Alias,
                                                      "--token",          Token.Value,
                                                      "--token-encoding", TokenEncoding,
                                                      "--initiate-url",   $"https://localhost:{Port}/connection/");

            await server.WaitForEventAsync("listening", TimeSpan.FromSeconds(30));

            return server;

        }

        /// <summary>
        /// A WWCP_S2 LAN endpoint hosting one RM node, with an empty store.
        /// </summary>
        private static (LocalEndpoint Endpoint, HostedNode Node, InMemoryS2Store Store) WWCPResourceManager()
        {

            var endpoint = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop RM endpoint"),
                                             Deployment.LAN,
                                             S2BaseURL.Parse("https://wwcp-rm.local/pairing/"));

            var node     = endpoint.AddNode(new NodeDescription(Node_Id.NewRandom, "GraphDefined", "EV charger", "WWCP_S2", EnergyManagementRole.RM));

            return (endpoint, node, new InMemoryS2Store());

        }

        #endregion


        #region WWCPPairingClient_PairsWithTheS2RustPairingServer_LAN()

        [Test]
        [S2C("Pairing.LAN.WWCPClient.S2RustServer")]
        public async Task WWCPPairingClient_PairsWithTheS2RustPairingServer_LAN()
        {

            using var certificate = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);

            var port          = Interop.FreePort();
            var serverNodeId  = Node_Id.NewRandom;
            var token         = PairingToken.Parse("ABCD2345");

            await using var server = await StartPairingServerAsync(certificate, port, serverNodeId, "CEM1", token);

            var (endpoint, node, store) = WWCPResourceManager();

            await using var client = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/"),
                                                       endpoint,
                                                       store,
                                                       Deployment.LAN,
                                                       new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.PairAsync(node, token, PairingTarget.ByAlias(NodeIdAlias.Parse("CEM1")));

            Assert.That(result.IsSuccess, Is.True, () => $"{result}{Environment.NewLine}{server.Diagnostics}");

            var paired = await server.WaitForEventAsync("paired");

            Assert.Multiple(() => {
                Assert.That(paired.Value<String>("role"),                 Is.EqualTo("CommunicationServer"), "a LAN CEM is the communication server");
                Assert.That(paired.Value<String>("access_token"),         Is.EqualTo(result.Pairing!.AccessToken.Value), "both sides hold the same access token");
                Assert.That(paired["remote_node"]!.Value<String>("id"),   Is.EqualTo(node.Id.ToString()));
                Assert.That(result.Pairing!.RemoteNodeId,                 Is.EqualTo(serverNodeId));
                Assert.That(result.Pairing.LocalCommunicationRole,        Is.EqualTo(CommunicationRole.CommunicationClient));
                Assert.That(result.Pairing.InitiateSessionUrl?.Value,     Is.EqualTo($"https://localhost:{port}/connection/"));
                Assert.That(result.Pairing.CertificateFingerprints?["SHA256"], Is.EqualTo(certificate.Fingerprint), "the s2-rust server announced its leaf certificate for pinning");
            });

            var stored = await store.GetPairingAsync(node.Id, serverNodeId);
            Assert.That(stored, Is.Not.Null, "the pairing is persisted");

        }

        #endregion

        #region WWCPPairingClient_AgainstS2RustBase64TokenDecoding_KnownIssue()

        [Test]
        [S2C("Pairing.ChallengeResponse.TokenEncoding")]
        public async Task WWCPPairingClient_AgainstS2RustBase64TokenDecoding_KnownIssue()
        {

            using var certificate = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);

            var port   = Interop.FreePort();
            var token  = PairingToken.Parse("ABCD2345");

            // s2-rust's own token decoding: the HMAC responses of both sides differ.
            await using var server = await StartPairingServerAsync(certificate, port, Node_Id.NewRandom, "CEM1", token, TokenEncoding: "base64");

            var (endpoint, node, store) = WWCPResourceManager();

            await using var client = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/"), endpoint, store, Deployment.LAN,
                                                       new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.PairAsync(node, token, PairingTarget.ByAlias(NodeIdAlias.Parse("CEM1")));

            Interop.KnownIssue(TokenEncodingIssue,
                               StillPresent:  !result.IsSuccess,
                               Detail:        result.ToString());

            Assert.That(result.Outcome, Is.EqualTo(PairingClientOutcome.ChallengeResponseMismatch),
                        "the WWCP_S2 client detects the wrong clientHmacChallengeResponse in step 4 and ends the attempt");

        }

        #endregion

        #region WWCPPairingClient_AgainstS2RustWANFormula_KnownIssue()

        [Test]
        [S2C("Pairing.ChallengeResponse.WAN")]
        public async Task WWCPPairingClient_AgainstS2RustWANFormula_KnownIssue()
        {

            using var certificate = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);

            var port   = Interop.FreePort();
            var token  = PairingToken.Parse("ABCD2345");

            await using var server = await StartPairingServerAsync(certificate, port, Node_Id.NewRandom, "CEM1", token, Deployment: "WAN");

            var (endpoint, node, store) = WWCPResourceManager();

            // The WAN formula uses D = "localhost" on the WWCP_S2 side; s2-rust uses no D at all.
            await using var client = new PairingClient(S2BaseURL.Parse($"https://localhost:{port}/"), endpoint, store, Deployment.WAN,
                                                       new PairingClientOptions { ServiceUnavailableRetryDelay = TimeSpan.FromMilliseconds(100) });

            var result = await client.PairAsync(node, token, PairingTarget.ByAlias(NodeIdAlias.Parse("CEM1")));

            Interop.KnownIssue(WANFormulaIssue,
                               StillPresent:  !result.IsSuccess,
                               Detail:        result.ToString());

            Assert.That(result.Outcome, Is.EqualTo(PairingClientOutcome.ChallengeResponseMismatch));

        }

        #endregion


        #region S2RustPairingClient_PairsWithTheWWCPPairingServer_LAN_KnownIssue()

        /// <summary>
        /// The s2-rust LAN pairing client only trusts a root certificate that the server
        /// transmits as the last element of its TLS chain (trust on first use); a server that
        /// presents a self-signed leaf alone, as WWCP S2 on Hermod does (PLAN.md D13), fails
        /// the TLS handshake with UnknownIssuer.
        /// </summary>
        public const String LeafOnlyChainIssue = "the s2-rust LAN pairing client (pairing URLs ending with .local) trusts only a root certificate transmitted as the last element " +
                                                 "of the TLS chain (pairing/transport.rs: intermediates.last(), otherwise an empty trust store) and fails the TLS handshake " +
                                                 "against a server that presents a self-signed leaf alone; Hermod's TLS server sends only the leaf (WWCP_S2 D13), " +
                                                 "so the LAN pairing s2-rust → WWCP_S2 cannot complete";

        [Test]
        [S2C("Pairing.LAN.S2RustClient.WWCPServer")]
        public async Task S2RustPairingClient_PairsWithTheWWCPPairingServer_LAN_KnownIssue()
        {

            var host = await PrepareReverseDirectionAsync();

            // A self-signed leaf without the CA bit: what rustls accepts as an end entity.
            using var certificate = ServerCertificate.Create(WWCPHostName, [ System.Net.IPAddress.Parse(host) ]);

            var (outcome, diagnostics, store, cem, sessionUrl) = await PairS2RustClientWithWWCPServerAsync(host, certificate);

            if (outcome.Value<String>("event") == "paired")
            {

                var pairings = await store.GetPairingsAsync(cem.Id);

                Assert.Multiple(() => {
                    Assert.That(outcome.Value<String>("role"),          Is.EqualTo("CommunicationClient"), "a LAN RM is the communication client");
                    Assert.That(outcome.Value<String>("initiate_url"),  Is.EqualTo(sessionUrl.Value));
                    Assert.That(outcome.Value<String>("root_hash"),     Is.EqualTo(certificate.FingerprintHex), "the WWCP_S2 endpoint announced its self-signed certificate for pinning");
                    Assert.That(pairings,                               Has.Count.EqualTo(1));
                    Assert.That(pairings[0].AccessToken.Value,          Is.EqualTo(outcome.Value<String>("access_token")), "both sides hold the same access token");
                    Assert.That(pairings[0].LocalCommunicationRole,     Is.EqualTo(CommunicationRole.CommunicationServer));
                });

            }

            Interop.KnownIssue(LeafOnlyChainIssue,
                               StillPresent:  outcome.Value<String>("event") != "paired" && outcome.Value<String>("kind") == "TransportFailed",
                               Detail:        $"{outcome}{Environment.NewLine}{diagnostics}");

        }

        #endregion

        #region S2RustPairingClient_AgainstTheWWCPSelfSignedLANCertificate_KnownIssue()

        [Test]
        [S2C("Security.D13.SelfSignedLANCertificate")]
        public async Task S2RustPairingClient_AgainstTheWWCPSelfSignedLANCertificate_KnownIssue()
        {

            var host = await PrepareReverseDirectionAsync();

            // The certificate WWCP S2 issues for a LAN endpoint (SelfSignedCA, D13). rustls accepts
            // it as an end entity (no CA bit), but the .local pairing client still needs a
            // transmitted root, see LeafOnlyChainIssue.
            using var certificate = ServerCertificate.FromWWCP(WWCPHostName, [ System.Net.IPAddress.Parse(host) ]);

            var (outcome, diagnostics, _, _, _) = await PairS2RustClientWithWWCPServerAsync(host, certificate);

            Interop.KnownIssue(LeafOnlyChainIssue,
                               StillPresent:  outcome.Value<String>("event") != "paired" && outcome.Value<String>("kind") == "TransportFailed",
                               Detail:        $"{outcome}{Environment.NewLine}{diagnostics}");

        }

        #endregion

        #region (private) PrepareReverseDirectionAsync() / PairS2RustClientWithWWCPServerAsync(...)

        /// <summary>
        /// The host name of the WWCP_S2 LAN endpoint in the reverse direction. s2-rust selects
        /// the LAN challenge-response formula only for pairing URLs whose host ends with
        /// ".local" (any other host, IP addresses included, gets the WAN formula), so the
        /// endpoint must be addressed by an mDNS-style name that the driver can resolve.
        /// </summary>
        private const String WWCPHostName = "wwcp-cem.local";

        private static async Task<String> PrepareReverseDirectionAsync()
        {

            await Interop.RequireRustAsync(PairingClient);

            if (!await RustHarness.CanReachThisProcessAsync(PairingClient))
                Assert.Ignore("The s2-rust driver (inside WSL) cannot open connections to this process: allow the test host through the Windows firewall on the WSL network, or run the tests on Linux.");

            if (!await RustHarness.EnsureHostAliasAsync(PairingClient, WWCPHostName))
                Assert.Ignore($"'{WWCPHostName}' cannot be made to resolve to this process for the s2-rust driver.");

            return await RustHarness.HostAddressForDriverAsync(PairingClient);

        }

        /// <summary>
        /// Run the s2-rust pairing client against a WWCP_S2 pairing server presenting the given
        /// certificate; returns the outcome event, the driver diagnostics and the server state.
        /// </summary>
        private static async Task<(JObject Outcome, String Diagnostics, InMemoryS2Store Store, HostedNode CEM, S2BaseURL SessionUrl)> PairS2RustClientWithWWCPServerAsync(String             host,
                                                                                                                                                                                  ServerCertificate  certificate)
        {

            var port        = Interop.FreePort();
            var pairingUrl  = S2BaseURL.Parse($"https://{WWCPHostName}:{port}/pairing/");
            var sessionUrl  = S2BaseURL.Parse($"https://{WWCPHostName}:{port}/connection/");
            var token       = PairingToken.Parse("ABCD2345");

            var httpServer  = new HTTPServer(IPv4Address.Any,
                                             port,
                                             "WWCP S2 interop pairing server",
                                             ServerCertificateSelector:  (tcpServer, tcpClient) => certificate.Certificate,
                                             AutoStart:                  false);

            var endpoint    = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop CEM endpoint"),
                                                Deployment.LAN,
                                                pairingUrl,
                                                sessionUrl,
                                                ServerCertificateFingerprint:  () => certificate.Fingerprint,
                                                CACertificateFingerprint:      () => certificate.Fingerprint);

            var cem         = endpoint.AddNode(new NodeDescription(Node_Id.NewRandom, "GraphDefined", "EMS", "WWCP_S2", EnergyManagementRole.CEM),
                                               NodeIdAlias.Parse("CEM1"));

            cem.SetStaticPairingToken(token);

            var store       = new InMemoryS2Store();

            await using var api = new PairingServerAPI(httpServer,
                                                       endpoint,
                                                       store,
                                                       Options:       new PairingServerOptions { RequestPairingDelay = TimeSpan.FromMilliseconds(10) },
                                                       SubnetPolicy:  AllowAllSubnetPolicy.Instance);

            await httpServer.Start();

            try
            {

                await using var client = await RustHarness.StartAsync(PairingClient,
                                                                      "--url",          pairingUrl.Value,
                                                                      "--deployment",   "LAN",
                                                                      "--role",         "RM",
                                                                      "--node-id",      Node_Id.NewRandom.ToString(),
                                                                      "--token",        token.Value,
                                                                      "--remote-alias", "CEM1",
                                                                      "--ca",           RustHarness.PathForDriver(PairingClient, certificate.CertificatePem));

                JObject outcome;

                try
                {
                    outcome = await client.WaitForEventAsync(e => e.Value<String>("event") is "paired" or "pairing_failed",
                                                             TimeSpan.FromSeconds(60),
                                                             "the outcome of the pairing");
                }
                catch (InvalidOperationException e)
                {
                    // The driver exits with 1 after reporting pairing_failed; keep the event when it arrived.
                    outcome = client.Events.LastOrDefault(ev => ev.Value<String>("event") is "paired" or "pairing_failed")
                                  ?? new JObject { ["event"] = "driver_exited", ["error"] = e.Message };
                }

                return (outcome, client.Diagnostics, store, cem, sessionUrl);

            }
            finally
            {
                await httpServer.Stop();
            }

        }

        #endregion

    }

}
