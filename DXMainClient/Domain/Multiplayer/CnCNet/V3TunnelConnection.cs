﻿using Rampastring.Tools;
using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClientCore;

namespace DTAClient.Domain.Multiplayer.CnCNet
{
    /// <summary>
    /// Handles connections to version 3 CnCNet tunnel servers.
    /// </summary>
    internal sealed class V3TunnelConnection
    {
        public V3TunnelConnection(CnCNetTunnel tunnel, GameTunnelHandler gameTunnelHandler, uint senderId)
        {
            this.tunnel = tunnel;
            this.gameTunnelHandler = gameTunnelHandler;
            SenderId = senderId;
        }

        public event EventHandler Connected;
        public event EventHandler ConnectionFailed;
        public event EventHandler ConnectionCut;

        public uint SenderId { get; }

        private bool aborted;
        public bool Aborted
        {
            get
            {
                locker.Wait();

                try
                {
                    return aborted;
                }
                finally
                {
                    locker.Release();
                }
            }
            private set
            {
                locker.Wait();

                try
                {
                    aborted = value;
                }
                finally
                {
                    locker.Release();
                }
            }
        }

        private readonly CnCNetTunnel tunnel;
        private readonly GameTunnelHandler gameTunnelHandler;
        private Socket tunnelSocket;
        private EndPoint tunnelEndPoint;

        private readonly SemaphoreSlim locker = new(1, 1);

        public async Task ConnectAsync()
        {
            Logger.Log($"Attempting to establish connection to V3 tunnel server " +
                $"{tunnel.Name} ({tunnel.Address}:{tunnel.Port})");

            tunnelEndPoint = new IPEndPoint(tunnel.IPAddress, tunnel.Port);
            tunnelSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);
            tunnelSocket.SendTimeout = Constants.TUNNEL_CONNECTION_TIMEOUT;
            tunnelSocket.ReceiveTimeout = Constants.TUNNEL_CONNECTION_TIMEOUT;

            try
            {
                using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(50);
                Memory<byte> buffer = memoryOwner.Memory[..50];

                if (!BitConverter.TryWriteBytes(buffer.Span[..4], SenderId))
                    throw new Exception();

                await tunnelSocket.SendToAsync(buffer, SocketFlags.None, tunnelEndPoint);

                Logger.Log($"Connection to tunnel server established.");
                Connected?.Invoke(this, EventArgs.Empty);
            }
            catch (SocketException ex)
            {
                ProgramConstants.LogException(ex, "Failed to establish connection to tunnel server.");
                tunnelSocket.Close();
                ConnectionFailed?.Invoke(this, EventArgs.Empty);
                return;
            }

            tunnelSocket.ReceiveTimeout = Constants.TUNNEL_RECEIVE_TIMEOUT;

            await ReceiveLoopAsync();
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(4096);

                while (true)
                {
                    if (Aborted)
                    {
                        DoClose();
                        Logger.Log("Exiting receive loop.");
                        return;
                    }

                    Memory<byte> buffer = memoryOwner.Memory[..1024];
                    SocketReceiveFromResult socketReceiveFromResult = await tunnelSocket.ReceiveFromAsync(buffer, SocketFlags.None, tunnelEndPoint);

                    if (socketReceiveFromResult.ReceivedBytes < 8)
                    {
                        Logger.Log("Invalid data packet from tunnel server");
                        continue;
                    }

                    Memory<byte> data = buffer[8..socketReceiveFromResult.ReceivedBytes];
                    uint senderId = BitConverter.ToUInt32(buffer[..4].Span);

                    await gameTunnelHandler.TunnelConnection_MessageReceivedAsync(data, senderId);
                }
            }
            catch (SocketException ex)
            {
                ProgramConstants.LogException(ex, "Socket exception in V3 tunnel receive loop.");
                DoClose();
                ConnectionCut?.Invoke(this, EventArgs.Empty);
            }
        }

        public void CloseConnection()
        {
            Logger.Log("Closing connection to the tunnel server.");
            Aborted = true;
        }

        private void DoClose()
        {
            Aborted = true;

            if (tunnelSocket != null)
            {
                tunnelSocket.Close();
                tunnelSocket = null;
            }

            Logger.Log("Connection to tunnel server closed.");
        }

        public async Task SendDataAsync(ReadOnlyMemory<byte> data, uint receiverId)
        {
            const int idsSize = sizeof(uint) * 2;
            int bufferSize = data.Length + idsSize;
            using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(bufferSize);
            Memory<byte> packet = memoryOwner.Memory[..bufferSize];

            if (!BitConverter.TryWriteBytes(packet.Span[..4], SenderId))
                throw new Exception();

            if (!BitConverter.TryWriteBytes(packet.Span[4..8], receiverId))
                throw new Exception();

            data.CopyTo(packet[8..]);

            await locker.WaitAsync();

            try
            {
                if (!aborted)
                    await tunnelSocket.SendToAsync(packet, SocketFlags.None, tunnelEndPoint);
            }
            finally
            {
                locker.Release();
            }
        }
    }
}