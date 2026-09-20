namespace SignalFish.Client.Protocol
{
    using System;
    using System.Buffers;
    using System.Text;

    /// <summary>
    /// Encodes protocol envelope frames: <c>{ "type": PascalCase, "data": {
    /// /// snake_case } }</c> JSON over UTF-8 bytes. One static writer per
    /// outbound (client→server) message kind; output is byte-identical to
    /// the golden fixture corpus. Writes go to an
    /// <see cref="IBufferWriter{T}"/> with zero intermediate allocations —
    /// the hot path for the M3+ client drivers. Encode misuse (a missing
    /// required field, a non-JSON verbatim payload) throws
    /// <see cref="ArgumentException"/>: encode bugs are programmer errors,
    /// unlike decode, which is total. Frames carrying no payload fields omit
    /// the <c>data</c> member entirely. Verbatim payloads
    /// (<see cref="GameDataMessage"/>, <see cref="SignalMessage"/>,
    /// <see cref="ProvideConnectionInfoMessage"/>) are embedded without
    /// inspection and must be valid UTF-8 JSON.
    /// </summary>
    public static class EnvelopeWriter
    {
        private static readonly byte[] EnvelopeOpen = Encoding.ASCII.GetBytes("{\"type\": \"");
        private static readonly byte[] EnvelopeClosePlain = Encoding.ASCII.GetBytes("\"}");
        private static readonly byte[] EnvelopeDataOpen = Encoding.ASCII.GetBytes(
            "\", \"data\": {"
        );

        private static class TypeNames
        {
            internal static readonly byte[] Authenticate = Encoding.ASCII.GetBytes("Authenticate");
            internal static readonly byte[] AuthorityRequest = Encoding.ASCII.GetBytes(
                "AuthorityRequest"
            );
            internal static readonly byte[] GameData = Encoding.ASCII.GetBytes("GameData");
            internal static readonly byte[] JoinAsSpectator = Encoding.ASCII.GetBytes(
                "JoinAsSpectator"
            );
            internal static readonly byte[] JoinRoom = Encoding.ASCII.GetBytes("JoinRoom");
            internal static readonly byte[] LeaveRoom = Encoding.ASCII.GetBytes("LeaveRoom");
            internal static readonly byte[] LeaveSpectator = Encoding.ASCII.GetBytes(
                "LeaveSpectator"
            );
            internal static readonly byte[] Ping = Encoding.ASCII.GetBytes("Ping");
            internal static readonly byte[] PlayerReady = Encoding.ASCII.GetBytes("PlayerReady");
            internal static readonly byte[] ProvideConnectionInfo = Encoding.ASCII.GetBytes(
                "ProvideConnectionInfo"
            );
            internal static readonly byte[] Reconnect = Encoding.ASCII.GetBytes("Reconnect");
            internal static readonly byte[] RoomOperation = Encoding.ASCII.GetBytes(
                "RoomOperation"
            );
            internal static readonly byte[] Signal = Encoding.ASCII.GetBytes("Signal");
            internal static readonly byte[] StartGame = Encoding.ASCII.GetBytes("StartGame");
            internal static readonly byte[] TransportStatus = Encoding.ASCII.GetBytes(
                "TransportStatus"
            );
        }

        private static class FieldNames
        {
            internal static readonly byte[] AppId = Encoding.ASCII.GetBytes("app_id");
            internal static readonly byte[] AuthToken = Encoding.ASCII.GetBytes("auth_token");
            internal static readonly byte[] ConnectToken = Encoding.ASCII.GetBytes("connect_token");
            internal static readonly byte[] BecomeAuthority = Encoding.ASCII.GetBytes(
                "become_authority"
            );
            internal static readonly byte[] Class = Encoding.ASCII.GetBytes("class");
            internal static readonly byte[] Connected = Encoding.ASCII.GetBytes("connected");
            internal static readonly byte[] ConnectionInfo = Encoding.ASCII.GetBytes(
                "connection_info"
            );
            internal static readonly byte[] Data = Encoding.ASCII.GetBytes("data");
            internal static readonly byte[] GameDataFormat = Encoding.ASCII.GetBytes(
                "game_data_format"
            );
            internal static readonly byte[] GameName = Encoding.ASCII.GetBytes("game_name");
            internal static readonly byte[] Generation = Encoding.ASCII.GetBytes("generation");
            internal static readonly byte[] Key = Encoding.ASCII.GetBytes("key");
            internal static readonly byte[] MaxPlayers = Encoding.ASCII.GetBytes("max_players");
            internal static readonly byte[] Operation = Encoding.ASCII.GetBytes("operation");
            internal static readonly byte[] OperationId = Encoding.ASCII.GetBytes("operation_id");
            internal static readonly byte[] Password = Encoding.ASCII.GetBytes("password");
            internal static readonly byte[] PlayerId = Encoding.ASCII.GetBytes("player_id");
            internal static readonly byte[] PlayerName = Encoding.ASCII.GetBytes("player_name");
            internal static readonly byte[] Platform = Encoding.ASCII.GetBytes("platform");
            internal static readonly byte[] ProtocolVersion = Encoding.ASCII.GetBytes(
                "protocol_version"
            );
            internal static readonly byte[] RequestedCapabilities = Encoding.ASCII.GetBytes(
                "requested_capabilities"
            );
            internal static readonly byte[] RoomCode = Encoding.ASCII.GetBytes("room_code");
            internal static readonly byte[] RoomId = Encoding.ASCII.GetBytes("room_id");
            internal static readonly byte[] RelayTransport = Encoding.ASCII.GetBytes(
                "relay_transport"
            );
            internal static readonly byte[] SdkVersion = Encoding.ASCII.GetBytes("sdk_version");
            internal static readonly byte[] SupportsAuthority = Encoding.ASCII.GetBytes(
                "supports_authority"
            );
            internal static readonly byte[] SignalField = Encoding.ASCII.GetBytes("signal");
            internal static readonly byte[] SpectatorName = Encoding.ASCII.GetBytes(
                "spectator_name"
            );
            internal static readonly byte[] SupportedTopologies = Encoding.ASCII.GetBytes(
                "supported_topologies"
            );
            internal static readonly byte[] SupportedTransports = Encoding.ASCII.GetBytes(
                "supported_transports"
            );
            internal static readonly byte[] To = Encoding.ASCII.GetBytes("to");
            internal static readonly byte[] Transport = Encoding.ASCII.GetBytes("transport");
            internal static readonly byte[] Type = Encoding.ASCII.GetBytes("type");
        }

        // --- v2 lifecycle -----------------------------------------------------

        /// <summary>Writes an <c>Authenticate</c> frame.</summary>
        public static void WriteAuthenticate(
            IBufferWriter<byte> destination,
            in AuthenticateMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.Authenticate);

            bool hasData =
                message.AppId is not null
                || message.SdkVersion is not null
                || message.Platform is not null
                || message.GameDataFormat is not null
                || message.ProtocolVersion is not null
                || message.SupportedTransports is not null
                || message.SupportedTopologies is not null
                || message.RequestedCapabilities is not null
                || message.ConnectToken is not null;
            if (!hasData)
            {
                writer.WriteBytes(EnvelopeClosePlain);
                writer.Flush();
                return;
            }

            writer.WriteBytes(EnvelopeDataOpen);
            bool first = true;
            if (message.AppId is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.AppId);
                writer.WriteString(message.AppId);
            }

            if (message.SdkVersion is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.SdkVersion);
                writer.WriteString(message.SdkVersion);
            }

            if (message.Platform is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.Platform);
                writer.WriteString(message.Platform);
            }

            if (message.GameDataFormat is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.GameDataFormat);
                writer.WriteString(message.GameDataFormat);
            }

            if (message.ProtocolVersion is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.ProtocolVersion);
                writer.WriteUInt32(message.ProtocolVersion.GetValueOrDefault());
            }

            if (message.SupportedTransports is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.SupportedTransports);
                writer.WriteStringArray(message.SupportedTransports);
            }

            if (message.SupportedTopologies is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.SupportedTopologies);
                writer.WriteStringArray(message.SupportedTopologies);
            }

            if (message.RequestedCapabilities is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.RequestedCapabilities);
                writer.WriteStringArray(message.RequestedCapabilities);
            }

            if (message.ConnectToken is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.ConnectToken);
                writer.WriteString(message.ConnectToken);
            }

            CloseData(ref writer);
        }

        /// <summary>Writes a <c>JoinRoom</c> frame.</summary>
        public static void WriteJoinRoom(
            IBufferWriter<byte> destination,
            in JoinRoomMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireText(message.GameName, "game_name");
            RequireText(message.PlayerName, "player_name");

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.JoinRoom);
            writer.WriteBytes(EnvelopeDataOpen);

            bool first = true;
            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.GameName);
            writer.WriteString(message.GameName);

            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.PlayerName);
            writer.WriteString(message.PlayerName);

            if (message.RoomCode is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.RoomCode);
                writer.WriteString(message.RoomCode);
            }

            if (message.MaxPlayers is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.MaxPlayers);
                writer.WriteUInt32(message.MaxPlayers.GetValueOrDefault());
            }

            if (message.SupportsAuthority is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.SupportsAuthority);
                writer.WriteBoolean(message.SupportsAuthority.GetValueOrDefault());
            }

            if (message.RelayTransport is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.RelayTransport);
                writer.WriteString(message.RelayTransport);
            }

            if (message.Password is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.Password);
                writer.WriteString(message.Password);
            }

            CloseData(ref writer);
        }

        /// <summary>Writes a <c>JoinAsSpectator</c> frame.</summary>
        public static void WriteJoinAsSpectator(
            IBufferWriter<byte> destination,
            in JoinAsSpectatorMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireText(message.GameName, "game_name");
            RequireText(message.RoomCode, "room_code");
            RequireText(message.SpectatorName, "spectator_name");

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.JoinAsSpectator);
            writer.WriteBytes(EnvelopeDataOpen);

            bool first = true;
            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.GameName);
            writer.WriteString(message.GameName);

            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.RoomCode);
            writer.WriteString(message.RoomCode);

            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.SpectatorName);
            writer.WriteString(message.SpectatorName);

            if (message.Password is not null)
            {
                writer.WriteMemberSeparator(ref first);
                writer.WriteKey(FieldNames.Password);
                writer.WriteString(message.Password);
            }

            CloseData(ref writer);
        }

        /// <summary>Writes a <c>Reconnect</c> frame.</summary>
        public static void WriteReconnect(
            IBufferWriter<byte> destination,
            in ReconnectMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireText(message.PlayerId, "player_id");
            RequireText(message.RoomId, "room_id");
            RequireText(message.AuthToken, "auth_token");

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.Reconnect);
            writer.WriteBytes(EnvelopeDataOpen);

            bool first = true;
            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.PlayerId);
            writer.WriteString(message.PlayerId);

            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.RoomId);
            writer.WriteString(message.RoomId);

            writer.WriteMemberSeparator(ref first);
            writer.WriteKey(FieldNames.AuthToken);
            writer.WriteString(message.AuthToken);

            CloseData(ref writer);
        }

        /// <summary>Writes an <c>AuthorityRequest</c> frame.</summary>
        public static void WriteAuthorityRequest(
            IBufferWriter<byte> destination,
            in AuthorityRequestMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.AuthorityRequest);
            writer.WriteBytes(EnvelopeDataOpen);
            writer.WriteKey(FieldNames.BecomeAuthority);
            writer.WriteBoolean(message.BecomeAuthority);
            CloseData(ref writer);
        }

        /// <summary>Writes a <c>ProvideConnectionInfo</c> frame.</summary>
        public static void WriteProvideConnectionInfo(
            IBufferWriter<byte> destination,
            in ProvideConnectionInfoMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireJsonObject(message.ConnectionInfo.Span, "connection_info");

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.ProvideConnectionInfo);
            writer.WriteBytes(EnvelopeDataOpen);
            writer.WriteKey(FieldNames.ConnectionInfo);
            writer.WriteBytes(message.ConnectionInfo.Span);
            CloseData(ref writer);
        }

        // --- Payloadless commands ---------------------------------------------

        /// <summary>Writes a <c>PlayerReady</c> frame (no payload).</summary>
        public static void WritePlayerReady(IBufferWriter<byte> destination)
        {
            WritePayloadless(destination, TypeNames.PlayerReady);
        }

        /// <summary>Writes a <c>StartGame</c> frame (no payload).</summary>
        public static void WriteStartGame(IBufferWriter<byte> destination)
        {
            WritePayloadless(destination, TypeNames.StartGame);
        }

        /// <summary>Writes a <c>LeaveRoom</c> frame (no payload).</summary>
        public static void WriteLeaveRoom(IBufferWriter<byte> destination)
        {
            WritePayloadless(destination, TypeNames.LeaveRoom);
        }

        /// <summary>Writes a <c>LeaveSpectator</c> frame (no payload).</summary>
        public static void WriteLeaveSpectator(IBufferWriter<byte> destination)
        {
            WritePayloadless(destination, TypeNames.LeaveSpectator);
        }

        /// <summary>Writes a heartbeat <c>Ping</c> frame (no payload).</summary>
        public static void WritePing(IBufferWriter<byte> destination)
        {
            WritePayloadless(destination, TypeNames.Ping);
        }

        // --- Relay -------------------------------------------------------------

        /// <summary>
        /// Writes a <c>GameData</c> frame. The reliable (relay-floor) class
        /// reproduces the v2 wire form with no delivery metadata;
        /// <see cref="GameDataClass.Latest"/> emits <c>class</c> and
        /// <c>key</c>; <see cref="GameDataClass.Volatile"/> emits
        /// <c>class</c> only.
        /// </summary>
        public static void WriteGameData(
            IBufferWriter<byte> destination,
            in GameDataMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireJsonValue(message.Payload.Span, "data (payload)");

            if (message.Class == default(GameDataClass))
            {
                // default(GameDataMessage) carries no delivery class; refuse
                // it instead of silently encoding the relay floor.
                throw new ArgumentException(
                    "GameData class is unset; construct GameDataMessage with an explicit delivery class.",
                    nameof(message)
                );
            }

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.GameData);
            writer.WriteBytes(EnvelopeDataOpen);

            writer.WriteKey(FieldNames.Data);
            writer.WriteBytes(message.Payload.Span);

            if (message.Class == GameDataClass.Latest)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.Class);
                writer.WriteString(LatestClassToken);
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.Key);
                writer.WriteUInt32(message.Key);
            }
            else if (message.Class == GameDataClass.Volatile)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.Class);
                writer.WriteString(VolatileClassToken);
            }

            CloseData(ref writer);
        }

        // --- v3 -----------------------------------------------------------------

        /// <summary>
        /// Writes a <c>RoomOperation</c> frame wrapping one room command
        /// with its correlation UUID.
        /// </summary>
        public static void WriteRoomOperation(
            IBufferWriter<byte> destination,
            in RoomOperationMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireUuid(message.OperationId, "operation_id");
            RoomOperationCommand command = message.Command;
            ValidateCommand(in command);

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.RoomOperation);
            writer.WriteBytes(EnvelopeDataOpen);

            writer.WriteKey(FieldNames.OperationId);
            writer.WriteString(message.OperationId);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.Operation);
            writer.WriteBytes(JsonObjectOpen);
            writer.WriteKey(FieldNames.Type);
            writer.WriteString(CommandTypeName(in command));

            WriteCommandPayload(ref writer, in command);
            writer.WriteBytes(JsonObjectClose);

            CloseData(ref writer);
        }

        /// <summary>Writes a v3 <c>Signal</c> frame targeting one same-room peer.</summary>
        public static void WriteSignal(IBufferWriter<byte> destination, in SignalMessage message)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireUuid(message.To, "to");
            RequireUuid(message.Generation, "generation");
            RequireJsonValue(message.Signal.Span, "signal");

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.Signal);
            writer.WriteBytes(EnvelopeDataOpen);

            writer.WriteKey(FieldNames.To);
            writer.WriteString(message.To);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.Generation);
            writer.WriteString(message.Generation);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.SignalField);
            writer.WriteBytes(message.Signal.Span);

            CloseData(ref writer);
        }

        /// <summary>Writes a v3 <c>TransportStatus</c> frame.</summary>
        public static void WriteTransportStatus(
            IBufferWriter<byte> destination,
            in TransportStatusMessage message
        )
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            RequireText(message.Transport, "transport");

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(TypeNames.TransportStatus);
            writer.WriteBytes(EnvelopeDataOpen);

            writer.WriteKey(FieldNames.Transport);
            writer.WriteString(message.Transport);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.Connected);
            writer.WriteBoolean(message.Connected);

            CloseData(ref writer);
        }

        // --- Shared helpers -----------------------------------------------------

        private static readonly byte[] CommaSpace = { (byte)',', (byte)' ' };
        private static readonly byte[] JsonObjectOpen = { (byte)'{' };
        private static readonly byte[] JsonObjectClose = { (byte)'}' };
        private static readonly byte[] KickPlayerType = Encoding.ASCII.GetBytes("KickPlayer");
        private static readonly byte[] RegenerateRoomCodeType = Encoding.ASCII.GetBytes(
            "RegenerateRoomCode"
        );
        private static readonly byte[] SetRoomAccessType = Encoding.ASCII.GetBytes("SetRoomAccess");
        private static readonly byte[] BanPlayerType = Encoding.ASCII.GetBytes("BanPlayer");
        private static readonly byte[] UnbanPlayerType = Encoding.ASCII.GetBytes("UnbanPlayer");
        private static readonly byte[] TransferAuthorityType = Encoding.ASCII.GetBytes(
            "TransferAuthority"
        );
        private static readonly byte[] LatestClassToken = Encoding.ASCII.GetBytes("latest");
        private static readonly byte[] VolatileClassToken = Encoding.ASCII.GetBytes("volatile");

        private static void WritePayloadless(IBufferWriter<byte> destination, byte[] typeName)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            JsonWriter writer = new JsonWriter(destination);
            writer.WriteBytes(EnvelopeOpen);
            writer.WriteBytes(typeName);
            writer.WriteBytes(EnvelopeClosePlain);
            writer.Flush();
        }

        private static void CloseData(ref JsonWriter writer)
        {
            writer.WriteByte((byte)'}');
            writer.WriteByte((byte)'}');
            writer.Flush();
        }

        private static void RequireText(string value, string wireField)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    $"The message must set \"{wireField}\" (a non-empty string).",
                    nameof(value)
                );
            }
        }

        /// <summary>
        /// Validates the canonical lowercase hyphenated UUID form
        /// (<c>8-4-4-4-12</c>) the server requires for peer and operation
        /// identifiers; any other encoding is rejected as a malformed frame.
        /// </summary>
        private static void RequireUuid(string value, string wireField)
        {
            RequireText(value, wireField);
            if (value.Length != 36)
            {
                throw new ArgumentException(
                    $"The message \"{wireField}\" must be a canonical 36-character hyphenated UUID.",
                    nameof(value)
                );
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (c != '-')
                    {
                        throw new ArgumentException(
                            $"The message \"{wireField}\" must use the 8-4-4-4-12 hyphen layout.",
                            nameof(value)
                        );
                    }
                }
                else if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    throw new ArgumentException(
                        $"The message \"{wireField}\" must be lowercase hexadecimal.",
                        nameof(value)
                    );
                }
            }
        }

        /// <summary>
        /// Validates a verbatim payload as one complete JSON value
        /// (allocation-free rescan) so a malformed payload fails at the call
        /// site instead of corrupting the whole frame.
        /// </summary>
        private static void RequireJsonValue(ReadOnlySpan<byte> json, string wireField)
        {
            if (json.IsEmpty)
            {
                throw new ArgumentException(
                    $"The message must carry a non-empty UTF-8 JSON value for \"{wireField}\".",
                    nameof(json)
                );
            }

            JsonScanner scanner = new JsonScanner(json);
            if (scanner.ScanValueRaw(1, JsonScanner.MaxDepth, out _) != default(DecodeError))
            {
                throw new ArgumentException(
                    $"The message must carry a valid UTF-8 JSON value for \"{wireField}\".",
                    nameof(json)
                );
            }

            scanner.SkipWhitespace();
            if (!scanner.IsEof)
            {
                throw new ArgumentException(
                    $"The message \"{wireField}\" must carry exactly one JSON value.",
                    nameof(json)
                );
            }
        }

        private static void RequireJsonObject(ReadOnlySpan<byte> json, string wireField)
        {
            RequireJsonValue(json, wireField);
            if (json[0] != (byte)'{')
            {
                throw new ArgumentException(
                    $"The message must carry a UTF-8 JSON object for \"{wireField}\".",
                    nameof(json)
                );
            }
        }

        private static void ValidateCommand(in RoomOperationCommand command)
        {
            switch (command.Kind)
            {
                case RoomOperationCommandKind.JoinRoom:
                    RequireText(command.JoinRoomPayload.GameName, "operation.JoinRoom.game_name");
                    RequireText(
                        command.JoinRoomPayload.PlayerName,
                        "operation.JoinRoom.player_name"
                    );
                    break;
                case RoomOperationCommandKind.JoinAsSpectator:
                    RequireText(
                        command.JoinAsSpectatorPayload.GameName,
                        "operation.JoinAsSpectator.game_name"
                    );
                    RequireText(
                        command.JoinAsSpectatorPayload.RoomCode,
                        "operation.JoinAsSpectator.room_code"
                    );
                    RequireText(
                        command.JoinAsSpectatorPayload.SpectatorName,
                        "operation.JoinAsSpectator.spectator_name"
                    );
                    break;
                case RoomOperationCommandKind.Reconnect:
                    RequireText(command.ReconnectPayload.PlayerId, "operation.Reconnect.player_id");
                    RequireText(command.ReconnectPayload.RoomId, "operation.Reconnect.room_id");
                    RequireText(
                        command.ReconnectPayload.AuthToken,
                        "operation.Reconnect.auth_token"
                    );
                    break;
                case RoomOperationCommandKind.KickPlayer:
                case RoomOperationCommandKind.BanPlayer:
                case RoomOperationCommandKind.UnbanPlayer:
                case RoomOperationCommandKind.TransferAuthority:
                    RequireText(command.PlayerId!, "operation.player_id");
                    break;
            }
        }

        private static byte[] CommandTypeName(in RoomOperationCommand command)
        {
            switch (command.Kind)
            {
                case RoomOperationCommandKind.JoinRoom:
                    return TypeNames.JoinRoom;
                case RoomOperationCommandKind.LeaveRoom:
                    return TypeNames.LeaveRoom;
                case RoomOperationCommandKind.Reconnect:
                    return TypeNames.Reconnect;
                case RoomOperationCommandKind.JoinAsSpectator:
                    return TypeNames.JoinAsSpectator;
                case RoomOperationCommandKind.LeaveSpectator:
                    return TypeNames.LeaveSpectator;
                case RoomOperationCommandKind.KickPlayer:
                    return KickPlayerType;
                case RoomOperationCommandKind.RegenerateRoomCode:
                    return RegenerateRoomCodeType;
                case RoomOperationCommandKind.SetRoomAccess:
                    return SetRoomAccessType;
                case RoomOperationCommandKind.BanPlayer:
                    return BanPlayerType;
                case RoomOperationCommandKind.UnbanPlayer:
                    return UnbanPlayerType;
                case RoomOperationCommandKind.TransferAuthority:
                    return TransferAuthorityType;
                default:
                    throw new ArgumentException(
                        $"Unknown room-operation kind: {command.Kind}.",
                        nameof(command)
                    );
            }
        }

        /// <summary>
        /// Writes the optional <c>data</c> member of the wrapped command
        /// object, keeping the legacy command shapes byte-compatible.
        /// </summary>
        private static void WriteCommandPayload(
            ref JsonWriter writer,
            in RoomOperationCommand command
        )
        {
            switch (command.Kind)
            {
                case RoomOperationCommandKind.JoinRoom:
                {
                    JoinRoomMessage joinRoom = command.JoinRoomPayload;
                    writer.WriteBytes(CommaSpace);
                    writer.WriteKey(FieldNames.Data);
                    writer.WriteBytes(JsonObjectOpen);
                    WriteJoinRoomFields(ref writer, in joinRoom);
                    writer.WriteBytes(JsonObjectClose);
                    break;
                }

                case RoomOperationCommandKind.JoinAsSpectator:
                {
                    JoinAsSpectatorMessage joinAsSpectator = command.JoinAsSpectatorPayload;
                    writer.WriteBytes(CommaSpace);
                    writer.WriteKey(FieldNames.Data);
                    writer.WriteBytes(JsonObjectOpen);
                    WriteJoinAsSpectatorFields(ref writer, in joinAsSpectator);
                    writer.WriteBytes(JsonObjectClose);
                    break;
                }

                case RoomOperationCommandKind.Reconnect:
                {
                    ReconnectMessage reconnect = command.ReconnectPayload;
                    writer.WriteBytes(CommaSpace);
                    writer.WriteKey(FieldNames.Data);
                    writer.WriteBytes(JsonObjectOpen);
                    WriteReconnectFields(ref writer, in reconnect);
                    writer.WriteBytes(JsonObjectClose);
                    break;
                }
                case RoomOperationCommandKind.KickPlayer:
                case RoomOperationCommandKind.BanPlayer:
                case RoomOperationCommandKind.UnbanPlayer:
                case RoomOperationCommandKind.TransferAuthority:
                    writer.WriteBytes(CommaSpace);
                    writer.WriteKey(FieldNames.Data);
                    writer.WriteBytes(JsonObjectOpen);
                    writer.WriteKey(FieldNames.PlayerId);
                    writer.WriteString(command.PlayerId!);
                    writer.WriteBytes(JsonObjectClose);
                    break;
                case RoomOperationCommandKind.SetRoomAccess:
                    writer.WriteBytes(CommaSpace);
                    writer.WriteKey(FieldNames.Data);
                    writer.WriteBytes(JsonObjectOpen);
                    writer.WriteKey(FieldNames.Password);
                    if (command.Password is not null)
                    {
                        writer.WriteString(command.Password);
                    }
                    else
                    {
                        writer.WriteNull();
                    }

                    writer.WriteBytes(JsonObjectClose);
                    break;
            }
        }

        private static void WriteJoinRoomFields(ref JsonWriter writer, in JoinRoomMessage message)
        {
            writer.WriteKey(FieldNames.GameName);
            writer.WriteString(message.GameName);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.PlayerName);
            writer.WriteString(message.PlayerName);
            if (message.RoomCode is not null)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.RoomCode);
                writer.WriteString(message.RoomCode);
            }

            if (message.MaxPlayers is not null)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.MaxPlayers);
                writer.WriteUInt32(message.MaxPlayers.GetValueOrDefault());
            }

            if (message.SupportsAuthority is not null)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.SupportsAuthority);
                writer.WriteBoolean(message.SupportsAuthority.GetValueOrDefault());
            }

            if (message.RelayTransport is not null)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.RelayTransport);
                writer.WriteString(message.RelayTransport);
            }

            if (message.Password is not null)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.Password);
                writer.WriteString(message.Password);
            }
        }

        private static void WriteJoinAsSpectatorFields(
            ref JsonWriter writer,
            in JoinAsSpectatorMessage message
        )
        {
            writer.WriteKey(FieldNames.GameName);
            writer.WriteString(message.GameName);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.RoomCode);
            writer.WriteString(message.RoomCode);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.SpectatorName);
            writer.WriteString(message.SpectatorName);
            if (message.Password is not null)
            {
                writer.WriteBytes(CommaSpace);
                writer.WriteKey(FieldNames.Password);
                writer.WriteString(message.Password);
            }
        }

        private static void WriteReconnectFields(ref JsonWriter writer, in ReconnectMessage message)
        {
            writer.WriteKey(FieldNames.PlayerId);
            writer.WriteString(message.PlayerId);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.RoomId);
            writer.WriteString(message.RoomId);
            writer.WriteBytes(CommaSpace);
            writer.WriteKey(FieldNames.AuthToken);
            writer.WriteString(message.AuthToken);
        }
    }
}
