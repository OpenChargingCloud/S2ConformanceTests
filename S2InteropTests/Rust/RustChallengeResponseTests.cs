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

using cloud.charging.open.protocols.S2.Connect;
using cloud.charging.open.protocols.S2.InteropTests.Harness;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Rust
{

    /// <summary>
    /// The pairing challenge-response function (S2 Connect 1.0.0, "Challenge response process")
    /// cross-checked against s2-rust. The specification defines
    /// <code>
    ///   LAN: R = HMAC(C, T || F)      WAN: R = HMAC(C, T || D)
    /// </code>
    /// with "the pairing token and domain name are strings, which need to be converted into
    /// binary data using the ASCII table". s2-rust (s2energy-connection, pairing) decodes the
    /// pairing token as standard Base64 (PairingToken::from_str) and computes HMAC(C, T) for
    /// WAN servers; both differences are recorded as known issues here so that they show up
    /// as warnings while they exist and as failures once they are fixed upstream.
    /// The s2-hmac driver runs inside WSL on Windows (s2energy-connection is Unix only).
    /// </summary>
    [TestFixture]
    [Category(InteropCategories.Interop)]
    [Category(InteropCategories.Rust)]
    public sealed class RustChallengeResponseTests
    {

        #region Data

        private const String Driver = "s2-hmac";

        private static readonly HmacChallenge           challenge    = HmacChallenge.Parse("q83vASNFZ4mrze8BI0VniavN7wEjRWeJq83vASNFZ4k=");
        private static readonly PairingToken            token        = PairingToken.Parse("ABCD2345");
        private static readonly CertificateFingerprint  fingerprint  = CertificateFingerprint.Parse("A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90");
        private const           String                  domainName   = "pairing.s2.example.com";

        /// <summary>
        /// s2-rust decodes the token text as Base64 instead of taking its ASCII bytes.
        /// </summary>
        public const String TokenEncodingIssue  = "s2-rust decodes the pairing token as Base64 (s2energy_connection::pairing::PairingToken::from_str) " +
                                                  "while S2 Connect 1.0.0 says 'the pairing token and domain name are strings, which need to be converted into binary data using the ASCII table'";

        /// <summary>
        /// s2-rust leaves the domain name out of the WAN formula.
        /// </summary>
        public const String WANFormulaIssue     = "s2-rust computes R = HMAC(C, T) for WAN pairing servers while S2 Connect 1.0.0 defines R = HMAC(C, T || D) " +
                                                  "with D = the domain name of the HTTPS server";

        /// <summary>
        /// s2-rust cannot parse tokens whose length is not a multiple of four.
        /// </summary>
        public const String TokenLengthIssue    = "s2-rust cannot parse a pairing token whose length is not a multiple of four (Base64 padding), " +
                                                  "although S2 Connect 1.0.0 allows dynamic tokens of 4 or more and static tokens of 6 or more characters (^[0-9a-zA-Z]{4,}$)";

        #endregion

        #region Setup

        [OneTimeSetUp]
        public Task BuildHarness()
            => Interop.RequireRustAsync(Driver);

        private static async Task<JObject> ComputeWithS2RustAsync(params String[] Arguments)
        {
            var result = await RustHarness.RunAsync(Driver, Arguments);
            Assert.That(result.IsSuccess, Is.True, () => "s2-hmac failed: " + result.Output);
            return JsonText.ParseObject(result.StandardOutput.Trim());
        }

        #endregion


        #region LAN_WithASCIITokenBytes_TheHMACCoresAgree()

        [Test]
        [S2C("Pairing.ChallengeResponse.LAN")]
        public async Task LAN_WithASCIITokenBytes_TheHMACCoresAgree()
        {

            var expected = ChallengeResponse.ComputeForLAN(HmacHashingAlgorithm.SHA256, challenge, token, fingerprint);

            var rust = await ComputeWithS2RustAsync("--challenge", challenge.Value,
                                                    "--token", token.Value,
                                                    "--token-bytes", "ascii",
                                                    "--fingerprint", fingerprint.ToString());

            Assert.Multiple(() => {
                Assert.That(rust.Value<String>("formula"),  Is.EqualTo("HMAC(C, T || F)"));
                Assert.That(rust.Value<String>("response"), Is.EqualTo(expected.Value), "HMAC-SHA256 over the same key and message must agree");
            });

        }

        #endregion

        #region LAN_S2RustDecodesTheTokenAsBase64_KnownIssue()

        [Test]
        [S2C("Pairing.ChallengeResponse.TokenEncoding")]
        public async Task LAN_S2RustDecodesTheTokenAsBase64_KnownIssue()
        {

            var expected = ChallengeResponse.ComputeForLAN(HmacHashingAlgorithm.SHA256, challenge, token, fingerprint);

            var rust = await ComputeWithS2RustAsync("--challenge", challenge.Value,
                                                    "--token", token.Value,
                                                    "--fingerprint", fingerprint.ToString());

            // What s2-rust really feeds into the HMAC: the Base64 decoding of the token text.
            Assert.That(rust.Value<String>("token_bytes"),
                        Is.EqualTo(Convert.ToHexStringLower(Convert.FromBase64String(token.Value))));

            Interop.KnownIssue(TokenEncodingIssue,
                               StillPresent:  rust.Value<String>("response") != expected.Value,
                               Detail:        $"s2-rust: {rust.Value<String>("response")}, specification: {expected.Value}");

        }

        #endregion

        #region WAN_S2RustOmitsTheDomainName_KnownIssue()

        [Test]
        [S2C("Pairing.ChallengeResponse.WAN")]
        public async Task WAN_S2RustOmitsTheDomainName_KnownIssue()
        {

            var expected = ChallengeResponse.ComputeForWAN(HmacHashingAlgorithm.SHA256, challenge, token, domainName);

            // Even with the token bytes aligned (ASCII), the WAN formulas differ by D.
            var rust = await ComputeWithS2RustAsync("--challenge", challenge.Value,
                                                    "--token", token.Value,
                                                    "--token-bytes", "ascii");

            Assert.That(rust.Value<String>("formula"), Is.EqualTo("HMAC(C, T)"));

            Interop.KnownIssue(WANFormulaIssue,
                               StillPresent:  rust.Value<String>("response") != expected.Value,
                               Detail:        $"s2-rust: {rust.Value<String>("response")}, specification: {expected.Value}");

        }

        #endregion

        #region DynamicTokenOfSixCharacters_IsNotParsableByS2Rust_KnownIssue()

        [Test]
        [S2C("Pairing.PairingToken.Format")]
        public async Task DynamicTokenOfSixCharacters_IsNotParsableByS2Rust_KnownIssue()
        {

            var sixCharacters = PairingToken.Parse("ABCDEF");
            Assert.That(PairingToken.IsValid(sixCharacters.Value), Is.True, "a six character token is a valid S2 Connect pairing token");

            var result = await RustHarness.RunAsync(Driver, "--challenge", challenge.Value, "--token", sixCharacters.Value, "--fingerprint", fingerprint.ToString());

            Interop.KnownIssue(TokenLengthIssue,
                               StillPresent:  !result.IsSuccess,
                               Detail:        result.Output);

        }

        #endregion

    }

}
