// Custom Unix domain socket endpoint for .NET Framework 4.7.2 compatibility

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GameRL.Harmony
{
    /// <summary>
    /// Unix domain socket endpoint for .NET Framework.
    /// Compatible with Mono runtime used by Unity/RimWorld.
    /// </summary>
    public class UnixEndPoint : EndPoint
    {
        private readonly string _path;

        public UnixEndPoint(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public string Path => _path;

        public override AddressFamily AddressFamily => AddressFamily.Unix;

        public override EndPoint Create(SocketAddress socketAddress)
        {
            if (socketAddress.Family != AddressFamily.Unix)
                throw new ArgumentException("Invalid address family");

            // Extract path from socket address
            // Format: 2 bytes family + path bytes + null terminator
            int pathLength = socketAddress.Size - 2;
            var pathBytes = new byte[pathLength];
            for (int i = 0; i < pathLength; i++)
            {
                pathBytes[i] = socketAddress[i + 2];
            }

            // Find null terminator
            int nullIndex = Array.IndexOf(pathBytes, (byte)0);
            if (nullIndex >= 0)
                pathLength = nullIndex;

            string path = Encoding.UTF8.GetString(pathBytes, 0, pathLength);
            return new UnixEndPoint(path);
        }

        public override SocketAddress Serialize()
        {
            // Unix socket address format:
            // - 2 bytes: address family (AF_UNIX = 1)
            // - N bytes: path (null-terminated)
            byte[] pathBytes = Encoding.UTF8.GetBytes(_path);
            var socketAddress = new SocketAddress(AddressFamily.Unix, 2 + pathBytes.Length + 1);

            for (int i = 0; i < pathBytes.Length; i++)
            {
                socketAddress[i + 2] = pathBytes[i];
            }
            socketAddress[2 + pathBytes.Length] = 0; // null terminator

            return socketAddress;
        }

        public override string ToString() => _path;

        public override int GetHashCode() => _path.GetHashCode();

        public override bool Equals(object? obj)
        {
            return obj is UnixEndPoint other && _path == other._path;
        }
    }
}
