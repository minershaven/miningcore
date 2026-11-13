using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks.Dataflow;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Telemetry;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Stratum;

public class StratumConnection
{
    public StratumConnection(ILogger logger, RecyclableMemoryStreamManager rmsm, IMasterClock clock, string connectionId, bool gpdrCompliantLogging,
        int maxInboundRequestLength = DefaultMaxInboundRequestLength)
    {
        this.logger = logger;
        this.rmsm = rmsm;

        this.maxInboundRequestLength = maxInboundRequestLength > 0
            ? maxInboundRequestLength
            : DefaultMaxInboundRequestLength;

        // The pause-writer threshold must exceed the maximum line length:
        // the reader only consumes complete newline-terminated requests, so a
        // single request larger than the threshold would otherwise deadlock
        // the fill loop (default PipeOptions pause at 64 KB).
        receivePipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: this.maxInboundRequestLength + 0x10000,
            resumeWriterThreshold: this.maxInboundRequestLength / 2 + 1));

        sendQueue = new BufferBlock<object>(new DataflowBlockOptions
        {
            EnsureOrdered = true,
        });

        this.clock = clock;
        ConnectionId = connectionId;
        IsAlive = true;
        this.gpdrCompliantLogging = gpdrCompliantLogging;
    }

    private readonly ILogger logger;
    private readonly RecyclableMemoryStreamManager rmsm;
    private readonly IMasterClock clock;

    public const int DefaultMaxInboundRequestLength = 0x8000;
    private readonly int maxInboundRequestLength;
    public static readonly Encoding Encoding = new UTF8Encoding(false);

    private Stream networkStream;
    private readonly Pipe receivePipe;
    private readonly BufferBlock<object> sendQueue;
    private WorkerContextBase context;
    private readonly Subject<Unit> terminated = new();
    private bool expectingProxyHeader;
    private bool gpdrCompliantLogging;

    private static readonly Newtonsoft.Json.JsonSerializer serializer = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private const int SendQueueCapacity = 16;
    private static readonly TimeSpan sendTimeout = TimeSpan.FromMilliseconds(5000);

    #region API-Surface

    public async void DispatchAsync(Socket socket, CancellationToken ct,
        StratumEndpoint endpoint, IPEndPoint remoteEndpoint, X509Certificate2 cert,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync,
        Action<StratumConnection> onCompleted,
        Action<StratumConnection, Exception> onError)
    {
        LocalEndpoint = endpoint.IPEndPoint;
        RemoteEndpoint = remoteEndpoint;

        expectingProxyHeader = endpoint.PoolEndpoint.TcpProxyProtocol?.Enable == true;

        try
        {
            // prepare socket
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // create stream
            networkStream = new NetworkStream(socket, true);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            using(var disposables = new CompositeDisposable(networkStream))
            {
                var tls = endpoint.PoolEndpoint.Tls;

                // auto-detect SSL
                if(endpoint.PoolEndpoint.TlsAuto)
                    tls = await DetectSslHandshake(socket, cts.Token);

                if(tls)
                {
                    var sslStream = new SslStream(networkStream, false);
                    disposables.Add(sslStream);

                    // TLS handshake
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = cert,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, cts.Token);

                    networkStream = sslStream;

                    logger.Info(() => $"[{ConnectionId}] {sslStream.SslProtocol.ToString().ToUpper()}-{sslStream.CipherAlgorithm.ToString().ToUpper()} Connection from {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");
                }
                else
                    logger.Info(() => $"[{ConnectionId}] Connection from {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");

                // Async I/O loop(s)
                var tasks = new[]
                {
                    FillReceivePipeAsync(cts.Token),
                    ProcessReceivePipeAsync(cts.Token, endpoint.PoolEndpoint.TcpProxyProtocol, onRequestAsync),
                    ProcessSendQueueAsync(cts.Token)
                };

                await Task.WhenAny(tasks);

                // We are done with this client, make sure all tasks complete
                await receivePipe.Reader.CompleteAsync();
                await receivePipe.Writer.CompleteAsync();
                sendQueue.Complete();

                // additional safety net to ensure remaining tasks don't linger
                cts.Cancel();

                // Signal completion or error
                var error = tasks.FirstOrDefault(t => t.IsFaulted)?.Exception;

                if(error == null)
                    onCompleted(this);
                else
                    onError(this, error);
            }
        }

        catch(Exception ex)
        {
            onError(this, ex);
        }

        finally
        {
            // Release external observables
            IsAlive = false;
            terminated.OnNext(Unit.Default);

            logger.Info(() => $"[{ConnectionId}] Connection closed");
        }
    }

    public string ConnectionId { get; }
    public IPEndPoint LocalEndpoint { get; private set; }
    public IPEndPoint RemoteEndpoint { get; private set; }
    public DateTime? LastReceive { get; set; }
    public bool IsAlive { get; set; }
    public IObservable<Unit> Terminated => terminated.AsObservable();
    public WorkerContextBase Context => context;

    public void SetContext<T>(T value) where T : WorkerContextBase
    {
        context = value;
    }

    public T ContextAs<T>() where T : WorkerContextBase
    {
        return (T) context;
    }

    public Task RespondAsync<T>(T payload, object id)
    {
        return RespondAsync(new JsonRpcResponse<T>(payload, id));
    }

    public Task RespondErrorAsync(StratumError code, string message, object id, object result = null)
    {
        return RespondAsync(new JsonRpcResponse(new JsonRpcError((int) code, message, null), id, result));
    }

    public Task RespondAsync<T>(JsonRpcResponse<T> response)
    {
        return SendAsync(response);
    }

    public Task NotifyAsync<T>(string method, T payload)
    {
        return NotifyAsync(new JsonRpcRequest<T>(method, payload, null));
    }

    public Task NotifyAsync<T>(JsonRpcRequest<T> request)
    {
        return SendAsync(request);
    }

    // Beam stratum API: https://github.com/BeamMW/beam/wiki/Beam-mining-protocol-API-(Stratum)
    public Task NotifyAsync(object request)
    {
        return SendAsync(request);
    }

    public void Disconnect()
    {
        networkStream.Close();
    }

    #endregion // API-Surface

    private Task SendAsync<T>(T payload)
    {
        Contract.RequiresNonNull(payload);

        if(sendQueue.Count >= SendQueueCapacity)
            throw new IOException("Sendqueue stalled");

        return sendQueue.SendAsync(payload);
    }

    private async Task FillReceivePipeAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            logger.Debug(() => $"[{ConnectionId}] [NET] Waiting for data ...");

            var memory = receivePipe.Writer.GetMemory(maxInboundRequestLength + 1);

            // read from network directly into pipe memory
            var cb = await networkStream.ReadAsync(memory, ct);
            if(cb == 0)
                break; // EOF

            logger.Debug(() => $"[{ConnectionId}] [NET] Received data: {Encoding.GetString(memory.Slice(0, cb).Span)}");

            LastReceive = clock.Now;

            // hand off to pipe
            receivePipe.Writer.Advance(cb);

            var result = await receivePipe.Writer.FlushAsync(ct);
            if(result.IsCompleted)
                break;
        }
    }

    private async Task ProcessReceivePipeAsync(CancellationToken ct,
        TcpProxyProtocolConfig proxyProtocol,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync)
    {
        while(!ct.IsCancellationRequested)
        {
            logger.Debug(() => $"[{ConnectionId}] [PIPE] Waiting for data ...");

            var result = await receivePipe.Reader.ReadAsync(ct);

            var buffer = result.Buffer;
            SequencePosition? position;

            if(buffer.Length > maxInboundRequestLength)
                throw new InvalidDataException($"Incoming data exceeds maximum of {maxInboundRequestLength}");

            logger.Debug(() => $"[{ConnectionId}] [PIPE] Received data: {result.Buffer.AsString(Encoding)}");

            do
            {
                // Scan buffer for line terminator
                position = buffer.PositionOf((byte) '\n');

                if(position != null)
                {
                    var slice = buffer.Slice(0, position.Value);

                    if(!expectingProxyHeader || !ProcessProxyHeader(slice, proxyProtocol))
                        await ProcessRequestAsync(ct, onRequestAsync, slice);

                    // Skip consumed section
                    buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                }
            } while(position != null);

            receivePipe.Reader.AdvanceTo(buffer.Start, buffer.End);

            if(result.IsCompleted)
                break;
        }
    }

    private async Task<bool> DetectSslHandshake(Socket socket, CancellationToken ct)
    {
        // https://tls.ulfheim.net/
        // https://tls13.ulfheim.net/

        const int BufSize = 1;
        var buf = ArrayPool<byte>.Shared.Rent(BufSize);

        try
        {
            var cb = await socket.ReceiveAsync(buf.AsMemory()[..BufSize], SocketFlags.Peek, ct);

            if(cb == 0)
                return false;   // End of stream

            if(cb < BufSize)
                throw new Exception($"Failed to peek at connection's first {BufSize} byte(s)");

            switch(buf[0])
            {
                case 0x16: // TLS 1.0 - 1.3
                    return true;
            }
        }

        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return false;
    }

    private async Task ProcessSendQueueAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            if(sendQueue.Count >= SendQueueCapacity)
                throw new IOException($"Send-queue overflow at {sendQueue.Count} of {SendQueueCapacity} items");

            var msg = await sendQueue.ReceiveAsync(ct);

            await SendMessage(msg, ct);
        }
    }

    private async Task SendMessage(object msg, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(sendTimeout);

        using var stream = rmsm.GetStream("stratum-send");
        using(var writer = new StreamWriter(stream, Encoding, 256, true))
        {
            serializer.Serialize(writer, msg);
        }

        stream.WriteByte((byte) '\n');

        logger.Debug(() =>
        {
            stream.Position = 0;
            using var sr = new StreamReader(stream, Encoding, false, 256, true);
            var json = sr.ReadToEnd();
            return $"[{ConnectionId}] Sending: {json.TrimEnd()}";
        });

        stream.Position = 0;
        await stream.CopyToAsync(networkStream, cts.Token);
        await networkStream.FlushAsync(cts.Token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private async Task ProcessRequestAsync(
        CancellationToken ct,
        Func<StratumConnection, JsonRpcRequest, CancellationToken, Task> onRequestAsync,
        ReadOnlySequence<byte> lineBuffer)
    {
        JsonRpcRequest request = null;

        try
        {
            request = FastParseRequest(lineBuffer);
        }
        catch
        {
        }

        if(request == null)
        {
            var line = Encoding.UTF8.GetString(lineBuffer.ToArray()).Trim();
            request = Newtonsoft.Json.JsonConvert.DeserializeObject<JsonRpcRequest>(line);
        }

        else
        {
            // Fast parse succeeded but may have produced a corrupt params slice.
            // Test-parse the params. If it fails, re-parse the whole request
            // with Newtonsoft (full string-based parse with no slicing errors).
            try
            {
                _ = request.ParamsAs<string[]>();
            }
            catch
            {
                var line = Encoding.UTF8.GetString(lineBuffer.ToArray()).Trim();
                request = Newtonsoft.Json.JsonConvert.DeserializeObject<JsonRpcRequest>(line);
            }
        }

        // Trace span — StartActivity returns null immediately when no OTel SDK is
        // registered (zero overhead). When OTel SDK is present, it handles sampling.
        using var activity = MiningcoreTelemetry.ActivitySource.StartActivity("stratum.request");
        activity?.SetTag("stratum.method", request.Method);
        activity?.SetTag("stratum.connection_id", ConnectionId);

        await onRequestAsync(this, request, ct);
    }

    /// <summary>
    /// Fast JSON-RPC request parser using Utf8JsonReader.
    /// Avoids string, StringReader, and JsonTextReader allocations.
    /// Extracts method/id/params and wraps them in a Newtonsoft-compatible JsonRpcRequest.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static JsonRpcRequest FastParseRequest(ReadOnlySequence<byte> lineBuffer)
    {
        // Trim to JSON object boundaries: skip leading chars until '{', find matching '}'
        var jsonSlice = TrimToJsonObject(lineBuffer);
        if(jsonSlice.Length == 0) return null;

        string method = null;
        object id = null;
        ReadOnlySequence<byte> paramsSlice = default;
        ReadOnlySequence<byte> rawParams = default;

        if(jsonSlice.IsSingleSegment)
        {
            var span = jsonSlice.First.Span;
            var reader = new Utf8JsonReader(span);

            while(reader.Read())
            {
                if(reader.TokenType == JsonTokenType.PropertyName)
                {
                    var nameLength = reader.ValueSpan.Length;

                    if(nameLength == 6)
                    {
                        // could be "method" or "params"
                        var firstChar = reader.ValueSpan[0];
                        if(firstChar == (byte) 'm') // "method"
                        {
                            reader.Read();
                            if(reader.TokenType == JsonTokenType.String)
                                method = reader.GetString();
                        }
                        else if(firstChar == (byte) 'p') // "params"
                        {
                            reader.Read();
                            var start = (int) reader.TokenStartIndex;
                            reader.Skip();
                            var end = (int) reader.BytesConsumed;
                            // Copy: the pipe buffer behind jsonSlice is recycled once the
                            // reader advances, which can happen before the request handler
                            // reads its params (large Pearl proofs showed corrupted base64).
                            paramsSlice = new ReadOnlySequence<byte>(jsonSlice.Slice(start, end - start).ToArray());
                        }
                    }
                    else if(nameLength == 2) // "id"
                    {
                        reader.Read();
                        id = ReadJsonRpcId(ref reader);
                    }
                }
            }
        }
        else
        {
            var arr = jsonSlice.ToArray();
            var reader = new Utf8JsonReader(arr);

            while(reader.Read())
            {
                if(reader.TokenType == JsonTokenType.PropertyName)
                {
                    var nameLength = reader.ValueSpan.Length;

                    if(nameLength == 6)
                    {
                        var firstChar = reader.ValueSpan[0];
                        if(firstChar == (byte) 'm')
                        {
                            reader.Read();
                            if(reader.TokenType == JsonTokenType.String)
                                method = reader.GetString();
                        }
                        else if(firstChar == (byte) 'p')
                        {
                            reader.Read();
                            var start = (int) reader.TokenStartIndex;
                            reader.Skip();
                            var end = (int) reader.BytesConsumed;
                            paramsSlice = new ReadOnlySequence<byte>(arr).Slice(start, end - start);
                        }
                    }
                    else if(nameLength == 2)
                    {
                        reader.Read();
                        id = ReadJsonRpcId(ref reader);
                    }
                }
            }
        }

        if(method == null) return null;

        return new JsonRpcRequest
        {
            Method = method,
            Id = id,
            Params = paramsSlice.Length > 0 ? paramsSlice : null
        };
    }

    /// <summary>Find the first JSON object in the buffer (from first '{' to matching '}').</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySequence<byte> TrimToJsonObject(ReadOnlySequence<byte> buffer)
    {
        // Find opening '{'
        var reader = new SequenceReader<byte>(buffer);
        if(!reader.TryAdvanceTo((byte) '{', false))
            return ReadOnlySequence<byte>.Empty;

        var start = reader.Position;
        int depth = 0;
        bool inString = false;
        bool escape = false;

        while(reader.TryPeek(out var b))
        {
            reader.Advance(1);

            if(inString)
            {
                if(escape)
                {
                    escape = false;
                    continue;
                }
                if(b == (byte) '\\')
                {
                    escape = true;
                    continue;
                }
                if(b == (byte) '"')
                {
                    inString = false;
                    continue;
                }
                continue;
            }

            if(b == (byte) '"')
            {
                inString = true;
                continue;
            }
            if(b == (byte) '{')
            {
                depth++;
            }
            else if(b == (byte) '}')
            {
                depth--;
                if(depth == 0)
                {
                    // Found matching closing brace
                    return buffer.Slice(start, reader.Position);
                }
            }
        }

        return ReadOnlySequence<byte>.Empty; // Unmatched braces
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object ReadJsonRpcId(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var i64)
                ? i64
                : (object) reader.GetDouble(),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the line was consumed
    /// </summary>
    private bool ProcessProxyHeader(ReadOnlySequence<byte> seq, TcpProxyProtocolConfig proxyProtocol)
    {
        expectingProxyHeader = false;

        var line = seq.AsString(Encoding);
        var peerAddress = RemoteEndpoint.Address;

        if(line.StartsWith("PROXY "))
        {
            var proxyAddresses = proxyProtocol.ProxyAddresses?.Select(IPAddress.Parse).ToArray();
            if(proxyAddresses == null || !proxyAddresses.Any())
                proxyAddresses = [IPAddress.Loopback, IPUtils.IPv4LoopBackOnIPv6, IPAddress.IPv6Loopback];

            if(proxyAddresses.Any(x => x.Equals(peerAddress)))
            {
                logger.Debug(() => $"[{ConnectionId}] Received Proxy-Protocol header: {line}");

                // split header parts
                var parts = line.Split(" ");
                var remoteAddress = parts[2];
                var remotePort = parts[4];

                // Update client
                RemoteEndpoint = new IPEndPoint(IPAddress.Parse(remoteAddress), int.Parse(remotePort));
                logger.Info(() => $"Real-IP via Proxy-Protocol: {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}");
            }

            else
            {
                throw new InvalidDataException($"Received spoofed Proxy-Protocol header from {peerAddress}");
            }

            return true;
        }

        if(proxyProtocol.Mandatory)
        {
            throw new InvalidDataException($"Missing mandatory Proxy-Protocol header from {peerAddress}. Closing connection.");
        }

        return false;
    }
}
