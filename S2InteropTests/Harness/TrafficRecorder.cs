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

using cloud.charging.open.protocols.S2.Session;
using cloud.charging.open.protocols.S2.Tests;

#endregion

namespace cloud.charging.open.protocols.S2.InteropTests.Harness
{

    /// <summary>
    /// Records every message sent and received on a WWCP_S2 session and validates each one
    /// against the embedded s2-json v1.0.0 schemas (PLAN.md Phase 11b: "schema validation of
    /// every outgoing message" and of all traffic). Received messages are taken from the wire
    /// text of the medium, sent messages from the serialisation that went onto the wire.
    /// </summary>
    public sealed class TrafficRecorder : IDisposable
    {

        #region (record) Entry

        /// <summary>
        /// One recorded message.
        /// </summary>
        /// <param name="Direction">"sent" or "received".</param>
        /// <param name="MessageType">The message_type, when present.</param>
        /// <param name="JSON">The message.</param>
        public sealed record Entry(String   Direction,
                                   String?  MessageType,
                                   JObject  JSON);

        #endregion

        #region Data

        private readonly S2Session     session;
        private readonly List<Entry>   entries     = [];
        private readonly List<String>  violations  = [];
        private readonly Lock          gate        = new ();

        #endregion

        #region Properties

        /// <summary>
        /// All recorded messages in order.
        /// </summary>
        public IReadOnlyList<Entry>  Entries
        {
            get
            {
                lock (gate)
                    return [.. entries];
            }
        }

        /// <summary>
        /// All schema violations found so far.
        /// </summary>
        public IReadOnlyList<String>  Violations
        {
            get
            {
                lock (gate)
                    return [.. violations];
            }
        }

        /// <summary>
        /// The recorded messages sent by the local session.
        /// </summary>
        public IEnumerable<Entry>  Sent
            => Entries.Where(entry => entry.Direction == "sent");

        /// <summary>
        /// The recorded messages received from the peer.
        /// </summary>
        public IEnumerable<Entry>  Received
            => Entries.Where(entry => entry.Direction == "received");

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Attach to a session.
        /// </summary>
        /// <param name="Session">The session to record.</param>
        public TrafficRecorder(S2Session Session)
        {
            this.session                    = Session;
            Session.OnMessageSent          += OnMessageSent;
            Session.Medium.OnTextReceived  += OnTextReceived;
        }

        #endregion


        #region (private) OnMessageSent / OnTextReceived / Record

        private Task OnMessageSent(DateTimeOffset  Timestamp,
                                   S2Session       Session,
                                   IS2Message      Message,
                                   S2SendResult    Result)
        {

            if (Result.IsSuccess)
                Record("sent", Message.ToJSON());

            return Task.CompletedTask;

        }

        private Task OnTextReceived(IS2Medium          Medium,
                                    String             Text,
                                    CancellationToken  CancellationToken)
        {

            JObject json;

            try
            {
                json = JsonText.ParseObject(Text);
            }
            catch (JsonException e)
            {

                lock (gate)
                    violations.Add($"received text is not a JSON object ({e.Message}): {Text}");

                return Task.CompletedTask;

            }

            Record("received", json);
            return Task.CompletedTask;

        }

        private void Record(String   Direction,
                            JObject  JSON)
        {

            var messageType = JSON.Value<String>("message_type");

            lock (gate)
            {

                entries.Add(new Entry(Direction, messageType, JSON));

                if (messageType is null)
                {
                    violations.Add($"{Direction}: no message_type: {JSON.ToString(Formatting.None)}");
                    return;
                }

                if (!S2MessageParser.KnownMessageTypes.Contains(messageType))
                {
                    violations.Add($"{Direction}: unknown message_type '{messageType}': {JSON.ToString(Formatting.None)}");
                    return;
                }

                var results = S2SchemaValidator.ValidateMessage(JSON, messageType);

                if (!results.IsValid)
                {

                    var errors = results.Details.
                                     Where (detail => detail.HasErrors).
                                     Select(detail => $"{detail.InstanceLocation}: {String.Join("; ", detail.Errors!.Select(error => error.Key + " " + error.Value))}");

                    violations.Add($"{Direction} {messageType} violates its schema: {String.Join(" | ", errors)}{Environment.NewLine}{JSON.ToString(Formatting.None)}");

                }

            }

        }

        #endregion


        #region Count(Direction, MessageType)

        /// <summary>
        /// The number of recorded messages of the given direction and type.
        /// </summary>
        /// <param name="Direction">"sent" or "received".</param>
        /// <param name="MessageType">The message_type, e.g. "ReceptionStatus".</param>
        public Int32 Count(String  Direction,
                           String  MessageType)
            => Entries.Count(entry => entry.Direction == Direction && entry.MessageType == MessageType);

        #endregion

        #region ReceivedReceptionStatuses(SubjectMessageId)

        /// <summary>
        /// All received ReceptionStatus messages for the given subject message.
        /// </summary>
        /// <param name="SubjectMessageId">The message identification.</param>
        public IEnumerable<JObject> ReceivedReceptionStatuses(Message_Id SubjectMessageId)
            => Received.Where(entry => entry.MessageType == "ReceptionStatus" &&
                                       entry.JSON.Value<String>("subject_message_id") == SubjectMessageId.ToString()).
                        Select(entry => entry.JSON);

        #endregion

        #region AssertAllValid()

        /// <summary>
        /// Assert that every recorded message is schema valid and that no received message
        /// got more than one ReceptionStatus.
        /// </summary>
        public void AssertAllValid()
        {

            Assert.That(Violations, Is.Empty, "schema violations on the wire");

            var duplicateStatuses = Received.Where(entry => entry.MessageType == "ReceptionStatus").
                                             GroupBy(entry => entry.JSON.Value<String>("subject_message_id")).
                                             Where(group => group.Count() > 1).
                                             Select(group => group.Key).
                                             ToList();

            Assert.That(duplicateStatuses, Is.Empty, "the peer sent more than one ReceptionStatus for the same message");

        }

        #endregion

        #region Dispose()

        /// <summary>
        /// Detach from the session.
        /// </summary>
        public void Dispose()
        {
            session.OnMessageSent          -= OnMessageSent;
            session.Medium.OnTextReceived  -= OnTextReceived;
        }

        #endregion

    }

}
