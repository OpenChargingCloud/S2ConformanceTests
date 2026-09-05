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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.WebSockets;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.S2Auth
{

    /// <summary>
    /// A WWCP S2 CEM offering S2 Connect pairing and, optionally, session initiation over one
    /// HTTPS server with a self-signed "localhost" certificate: the counterpart of the s2auth
    /// client. The CEM node has the alias "CEM1" and the static pairing token "ABCD2345".
    /// </summary>
    internal sealed class WWCPConnectServer : IAsyncDisposable
    {

        #region Properties

        public HTTPServer                   HTTPServer     { get; }
        public LocalEndpoint                Endpoint       { get; }
        public HostedNode                   CEM            { get; }
        public InMemoryS2Store              Store          { get; }
        public CommunicationTokenStore      TokenStore     { get; }
        public PairingServerAPI             PairingAPI     { get; }
        public SessionInitiationServerAPI?  SessionAPI     { get; }
        public ServerCertificate            Certificate    { get; }
        public IPPort                       Port           { get; }
        public S2BaseURL                    PairingUrl     { get; }
        public S2BaseURL                    SessionUrl     { get; }
        public PairingToken                 Token          { get; }
        public List<Pairing>                Unpaired       { get; } = [];

        /// <summary>
        /// The pairing URL including the API version, as the s2auth client expects it.
        /// </summary>
        public String                       PairingUrlV1
            => PairingUrl.Value + Version.S2ConnectAPIVersion;

        #endregion

        #region Constructor(s)

        private WWCPConnectServer(HTTPServer                   HTTPServer,
                                  LocalEndpoint                Endpoint,
                                  HostedNode                   CEM,
                                  InMemoryS2Store              Store,
                                  CommunicationTokenStore      TokenStore,
                                  PairingServerAPI             PairingAPI,
                                  SessionInitiationServerAPI?  SessionAPI,
                                  ServerCertificate            Certificate,
                                  IPPort                       Port,
                                  S2BaseURL                    PairingUrl,
                                  S2BaseURL                    SessionUrl,
                                  PairingToken                 Token)
        {
            this.HTTPServer   = HTTPServer;
            this.Endpoint     = Endpoint;
            this.CEM          = CEM;
            this.Store        = Store;
            this.TokenStore   = TokenStore;
            this.PairingAPI   = PairingAPI;
            this.SessionAPI   = SessionAPI;
            this.Certificate  = Certificate;
            this.Port         = Port;
            this.PairingUrl   = PairingUrl;
            this.SessionUrl   = SessionUrl;
            this.Token        = Token;
        }

        #endregion


        #region StartAsync(Deployment, WithSessionAPI = true)

        /// <summary>
        /// Start the server.
        /// </summary>
        /// <param name="Deployment">The deployment of the WWCP S2 endpoint (LAN: challenge-response with the certificate fingerprint; WAN: with the domain name "localhost").</param>
        /// <param name="WithSessionAPI">Whether to offer session initiation as well (under /connection/).</param>
        public static async Task<WWCPConnectServer> StartAsync(Deployment  Deployment,
                                                                Boolean     WithSessionAPI   = true)
        {

            var certificate  = ServerCertificate.Create("localhost", [ System.Net.IPAddress.Loopback ]);
            var port         = Interop.FreePort();
            var pairingUrl   = S2BaseURL.Parse($"https://localhost:{port}/pairing/");
            var sessionUrl   = S2BaseURL.Parse($"https://localhost:{port}/connection/");
            var token        = PairingToken.Parse("ABCD2345");

            var httpServer   = new HTTPServer(IPv4Address.Any,
                                              port,
                                              "WWCP S2 interop S2 Connect server",
                                              ServerCertificateSelector:  (tcpServer, tcpClient) => certificate.Certificate,
                                              AutoStart:                  false);

            var endpoint     = new LocalEndpoint(new EndpointDescription("WWCP_S2 interop CEM endpoint"),
                                                 Deployment,
                                                 pairingUrl,
                                                 sessionUrl,
                                                 ServerCertificateFingerprint:  () => certificate.Fingerprint,
                                                 CACertificateFingerprint:      () => certificate.Fingerprint);

            var cem          = endpoint.AddNode(new NodeDescription(Node_Id.NewRandom, "GraphDefined", "EMS", "WWCP_S2", EnergyManagementRole.CEM),
                                                NodeIdAlias.Parse("CEM1"));

            cem.SetStaticPairingToken(token);

            var store        = new InMemoryS2Store();
            var tokenStore   = new CommunicationTokenStore();

            var pairingAPI   = new PairingServerAPI(httpServer,
                                                    endpoint,
                                                    store,
                                                    Options:       new PairingServerOptions { RequestPairingDelay = TimeSpan.FromMilliseconds(10) },
                                                    SubnetPolicy:  AllowAllSubnetPolicy.Instance);

            var sessionAPI   = WithSessionAPI
                                   ? new SessionInitiationServerAPI(httpServer,
                                                                    endpoint,
                                                                    store,
                                                                    tokenStore,
                                                                    URL.Parse($"wss://localhost:{port}/"),
                                                                    Options: new SessionInitiationServerOptions())
                                   : null;

            var server       = new WWCPConnectServer(httpServer, endpoint, cem, store, tokenStore, pairingAPI, sessionAPI, certificate, port, pairingUrl, sessionUrl, token);

            if (sessionAPI is not null)
                sessionAPI.OnUnpaired += (timestamp, sender, pairing, byRemote) => {
                                             lock (server.Unpaired)
                                                 server.Unpaired.Add(pairing);
                                             return Task.CompletedTask;
                                         };

            await httpServer.Start();

            return server;

        }

        #endregion

        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {

            await HTTPServer.Stop();

            if (SessionAPI is IAsyncDisposable disposableSessionAPI)
                await disposableSessionAPI.DisposeAsync();

            await PairingAPI.DisposeAsync();

            Certificate.Dispose();

        }

        #endregion

    }

}
