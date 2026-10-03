/*
    Signal Fish WebGL WebSocket plugin (Emscripten jslib).

    Bridges the browser WebSocket API to SignalFishWebGLTransport, which
    drives it through [DllImport("__Internal")] entry points. The entry
    points below are the contract; scripts/lint-webgl-plugin.ps1 keeps
    them pinned to the C# side and CI compiles the C# against them.

    Contract notes (mirrored in SignalFishWebGLTransport.cs):

    - Handles are positive ints minted here; 0 means "could not create".
    - Status mirrors the WebSocket readyState: 0 connecting, 1 open,
      2 closing, 3 closed.
    - CloseCode returns the observed close code, 0 before one arrives.
    - Send returns 0 on success, -1 when the socket is not open,
      -2 when the browser threw (the payload never reached the wire).
    - Poll drains one queued message per call:
        n > 0   : n bytes copied to dataPtr; kindPtr holds 0 (text) or
                  1 (binary).
        0       : nothing pending; the socket is still usable.
        -1      : the terminal close was observed; kindPtr holds the
                  wire close code.
        -2      : the next message (length in lenPtr) does not fit the
                  dataMax budget; nothing was consumed - grow the buffer
                  and call again.
      Zero-length messages are dropped (a `send("")` peer frame carries
      no payload the caller could surface).
    - Payload string bytes cross the boundary through the standard
      TextEncoder/TextDecoder pair; only the open URL is decoded with
      the runtime's UTF8ToString (always a defined pointer, unlike the
      array-form helper whose signature shifted across compiler
      versions).
    - The plugin never throws across the interop boundary; failures are
      return codes so a browser fault can never unwind into IL2CPP.
*/

var SignalFishWebSocketLibrary = {
    $SignalFishWebSocket: {
        nextHandle: 1,
        sockets: {},
        textEncoder: new TextEncoder(),
        textDecoder: new TextDecoder(),

        open: function (url) {
            var sockets = SignalFishWebSocket.sockets;
            var handle = SignalFishWebSocket.nextHandle;
            try {
                var socket = new WebSocket(UTF8ToString(url));
                socket.binaryType = 'arraybuffer';
                var entry = {
                    socket: socket,
                    status: 0,
                    closeCode: 0,
                    pending: [],
                };
                sockets[handle] = entry;
                socket.onopen = function () {
                    entry.status = 1;
                };
                socket.onclose = function (event) {
                    entry.status = 3;
                    entry.closeCode = event.wasClean ? event.code : (event.code || 1006);
                };
                socket.onerror = function () {
                    if (entry.status === 0) {
                        entry.status = 3;
                        entry.closeCode = 1006;
                    }
                };
                socket.onmessage = function (event) {
                    var bytes =
                        typeof event.data === 'string'
                            ? SignalFishWebSocket.textEncoder.encode(event.data)
                            : new Uint8Array(event.data);
                    entry.pending.push({ isBinary: typeof event.data !== 'string', bytes: bytes });
                };
            } catch (exception) {
                return 0;
            }
            SignalFishWebSocket.nextHandle += 1;
            return handle;
        },

        status: function (handle) {
            var entry = SignalFishWebSocket.sockets[handle];
            return entry ? entry.status : 3;
        },

        closeCode: function (handle) {
            var entry = SignalFishWebSocket.sockets[handle];
            return entry ? entry.closeCode : 0;
        },

        send: function (handle, data, length, isBinary) {
            var entry = SignalFishWebSocket.sockets[handle];
            if (!entry || entry.status !== 1) {
                return -1;
            }
            try {
                var view = HEAPU8.subarray(data, data + length);
                if (isBinary) {
                    // Copy: the heap view must not outlive the call.
                    entry.socket.send(new Uint8Array(view));
                } else {
                    entry.socket.send(SignalFishWebSocket.textDecoder.decode(view));
                }
            } catch (exception) {
                return -2;
            }
            return 0;
        },

        poll: function (handle, kindPtr, lenPtr, data, dataMax) {
            var entry = SignalFishWebSocket.sockets[handle];
            if (!entry) {
                HEAP32[kindPtr >> 2] = 1006;
                return -1;
            }
            while (entry.pending.length > 0) {
                var message = entry.pending.shift();
                if (message.bytes.length === 0) {
                    continue;
                }
                if (message.bytes.length > dataMax) {
                    HEAP32[lenPtr >> 2] = message.bytes.length;
                    entry.pending.unshift(message);
                    return -2;
                }
                HEAPU8.set(message.bytes, data);
                HEAP32[kindPtr >> 2] = message.isBinary ? 1 : 0;
                return message.bytes.length;
            }
            if (entry.status === 3) {
                HEAP32[kindPtr >> 2] = entry.closeCode || 1006;
                return -1;
            }
            return 0;
        },

        close: function (handle, code) {
            var entry = SignalFishWebSocket.sockets[handle];
            if (entry && entry.status === 1) {
                entry.status = 2;
                try {
                    entry.socket.close(code);
                } catch (exception) {
                    entry.status = 3;
                }
            }
        },

        dispose: function (handle) {
            var entry = SignalFishWebSocket.sockets[handle];
            if (!entry) {
                return;
            }
            delete SignalFishWebSocket.sockets[handle];
            var socket = entry.socket;
            if (socket.readyState === 0 || socket.readyState === 1) {
                try {
                    socket.close();
                } catch (exception) { }
            }
            entry.pending.length = 0;
            entry.socket = null;
        },
    },

    SignalFishWebSocketOpen: function (url) {
        return SignalFishWebSocket.open(url);
    },

    SignalFishWebSocketStatus: function (handle) {
        return SignalFishWebSocket.status(handle);
    },

    SignalFishWebSocketCloseCode: function (handle) {
        return SignalFishWebSocket.closeCode(handle);
    },

    SignalFishWebSocketSend: function (handle, data, length, isBinary) {
        return SignalFishWebSocket.send(handle, data, length, isBinary);
    },

    SignalFishWebSocketPoll: function (handle, kindPtr, lenPtr, data, dataMax) {
        return SignalFishWebSocket.poll(handle, kindPtr, lenPtr, data, dataMax);
    },

    SignalFishWebSocketClose: function (handle, code) {
        SignalFishWebSocket.close(handle, code);
    },

    SignalFishWebSocketDispose: function (handle) {
        SignalFishWebSocket.dispose(handle);
    },
};

autoAddDeps(SignalFishWebSocketLibrary, '$SignalFishWebSocket');
mergeInto(LibraryManager.library, SignalFishWebSocketLibrary);
