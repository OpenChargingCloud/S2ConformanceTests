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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.S2.Connect;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// A self-signed TLS server certificate with its PEM files (rustls on the s2-rust side
    /// reads PEM), following the D13 rule of WWCP_S2: a LAN endpoint presents one self-signed
    /// certificate that is its own CA and whose SHA-256 fingerprint is the pinned value.
    /// </summary>
    public sealed class ServerCertificate : IDisposable
    {

        #region Properties

        /// <summary>
        /// The certificate with its private key (for Hermod's TLS servers).
        /// </summary>
        public X509Certificate2         Certificate     { get; }

        /// <summary>
        /// The PEM file with the certificate.
        /// </summary>
        public String                   CertificatePem  { get; }

        /// <summary>
        /// The PEM file with the PKCS#8 private key.
        /// </summary>
        public String                   KeyPem          { get; }

        /// <summary>
        /// The SHA-256 fingerprint of the certificate.
        /// </summary>
        public CertificateFingerprint   Fingerprint
            => CertificateFingerprint.FromCertificate(Certificate);

        /// <summary>
        /// The fingerprint as lower-case hex without colons (the form s2-rust reports).
        /// </summary>
        public String                   FingerprintHex
            => Convert.ToHexStringLower(Fingerprint.Bytes.Span);

        #endregion

        #region Constructor(s)

        private ServerCertificate(X509Certificate2  Certificate,
                                  String            CertificatePem,
                                  String            KeyPem)
        {
            this.Certificate     = Certificate;
            this.CertificatePem  = CertificatePem;
            this.KeyPem          = KeyPem;
        }

        #endregion


        #region (static) Create(HostName, IPAddresses = null, Directory = null)

        /// <summary>
        /// Create a self-signed server certificate (RSA 2048, 30 days) for the given host name
        /// and optional IP addresses and write its PEM files.
        /// </summary>
        /// <param name="HostName">The DNS name for the subject alternative names.</param>
        /// <param name="IPAddresses">Optional IP addresses for the subject alternative names.</param>
        /// <param name="Directory">The directory of the PEM files (default: a new temporary directory).</param>
        /// <param name="CA">Whether the certificate carries BasicConstraints CA:TRUE ("its own CA"); rustls rejects such a certificate as an end entity.</param>
        public static ServerCertificate Create(String                               HostName,
                                               IEnumerable<System.Net.IPAddress>?   IPAddresses   = null,
                                               String?                              Directory     = null,
                                               Boolean                              CA            = false)
        {

            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest($"CN={HostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(CA, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | (CA ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.None), true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName(HostName);

            foreach (var ipAddress in IPAddresses ?? [])
                subjectAlternativeNames.AddIpAddress(ipAddress);

            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            var notBefore  = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter   = DateTimeOffset.UtcNow.AddDays(30);

            using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);

            // Round-trip through PKCS#12 so that SslStream on Windows gets a usable key.
            var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12),
                                                               null,
                                                               X509KeyStorageFlags.Exportable);

            var directory = Directory ?? Path.Combine(Path.GetTempPath(), "s2interop-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var certificatePem  = Path.Combine(directory, HostName + ".crt.pem");
            var keyPem          = Path.Combine(directory, HostName + ".key.pem");

            File.WriteAllText(certificatePem, ephemeral.ExportCertificatePem() + Environment.NewLine);
            File.WriteAllText(keyPem,         rsa.ExportPkcs8PrivateKeyPem()   + Environment.NewLine);

            return new ServerCertificate(certificate, certificatePem, keyPem);

        }

        #endregion

        #region (static) FromWWCP(HostName, IPAddresses = null, Directory = null)

        /// <summary>
        /// The self-signed LAN server certificate WWCP S2 itself issues
        /// (<see cref="SelfSignedCA.CreateSelfSignedServerCertificate"/>, the D13 decision:
        /// one certificate that is its own CA), with its PEM files for the s2-rust drivers.
        /// </summary>
        /// <param name="HostName">The DNS name for the subject alternative names.</param>
        /// <param name="IPAddresses">Optional IP addresses for the subject alternative names.</param>
        /// <param name="Directory">The directory of the PEM files (default: a new temporary directory).</param>
        public static ServerCertificate FromWWCP(String                               HostName,
                                                 IEnumerable<System.Net.IPAddress>?   IPAddresses   = null,
                                                 String?                              Directory     = null)
        {

            var certificate = SelfSignedCA.CreateSelfSignedServerCertificate(HostName,
                                                                             IPAddresses: IPAddresses?.Select(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                                                                                                        ? (IIPAddress) IPv4Address.Parse(ip.ToString())
                                                                                                                        : IPv6Address.Parse(ip.ToString())));

            var keyPemText  = certificate.GetECDsaPrivateKey()?.ExportPkcs8PrivateKeyPem()
                                  ?? certificate.GetRSAPrivateKey()?.ExportPkcs8PrivateKeyPem()
                                  ?? throw new InvalidOperationException("The WWCP S2 server certificate has no exportable private key!");

            var directory   = Directory ?? Path.Combine(Path.GetTempPath(), "s2interop-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var certificatePem  = Path.Combine(directory, HostName + ".crt.pem");
            var keyPem          = Path.Combine(directory, HostName + ".key.pem");

            File.WriteAllText(certificatePem, certificate.ExportCertificatePem() + Environment.NewLine);
            File.WriteAllText(keyPem,         keyPemText + Environment.NewLine);

            return new ServerCertificate(certificate, certificatePem, keyPem);

        }

        #endregion

        #region Dispose()

        /// <summary>
        /// Release the certificate and delete its PEM files.
        /// </summary>
        public void Dispose()
        {

            Certificate.Dispose();

            try
            {
                var directory = Path.GetDirectoryName(CertificatePem);
                if (directory is not null && Path.GetFileName(directory).StartsWith("s2interop-", StringComparison.Ordinal))
                    System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // A driver may still hold the files; the temporary directory is cleaned up by the OS.
            }

        }

        #endregion

    }


    /// <summary>
    /// TLS helpers of the interoperability tests.
    /// </summary>
    public static class Certificates
    {

        /// <summary>
        /// A server certificate validator that accepts everything (the peer's self-signed
        /// certificate is checked by the tests through its fingerprint instead).
        /// </summary>
        public static RemoteTLSServerCertificateValidationHandler<T> AcceptAny<T>()
            where T : class
            => (sender, certificate, chain, client, errors) => TLSValidationResult.Success();

    }

}
