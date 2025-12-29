// Simple manual MessagePack serializer for Unity/Mono compatibility
// Only implements what we need for the Game-RL protocol

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GameRL.Harmony
{
    /// <summary>
    /// Minimal MessagePack writer that works in Unity/Mono without complex initialization
    /// </summary>
    public class SimpleMsgPackWriter : IDisposable
    {
        private readonly MemoryStream _stream;

        public SimpleMsgPackWriter()
        {
            _stream = new MemoryStream();
        }

        public byte[] ToArray() => _stream.ToArray();

        public void WriteMapHeader(int count)
        {
            if (count <= 15)
            {
                _stream.WriteByte((byte)(0x80 | count));
            }
            else if (count <= 65535)
            {
                _stream.WriteByte(0xde);
                WriteUInt16BigEndian((ushort)count);
            }
            else
            {
                _stream.WriteByte(0xdf);
                WriteUInt32BigEndian((uint)count);
            }
        }

        public void WriteString(string? value)
        {
            if (value == null)
            {
                WriteNil();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            int len = bytes.Length;

            if (len <= 31)
            {
                _stream.WriteByte((byte)(0xa0 | len));
            }
            else if (len <= 255)
            {
                _stream.WriteByte(0xd9);
                _stream.WriteByte((byte)len);
            }
            else if (len <= 65535)
            {
                _stream.WriteByte(0xda);
                WriteUInt16BigEndian((ushort)len);
            }
            else
            {
                _stream.WriteByte(0xdb);
                WriteUInt32BigEndian((uint)len);
            }

            _stream.Write(bytes, 0, len);
        }

        public void WriteBool(bool value)
        {
            _stream.WriteByte(value ? (byte)0xc3 : (byte)0xc2);
        }

        public void WriteInt(int value)
        {
            if (value >= 0)
            {
                if (value <= 127)
                {
                    _stream.WriteByte((byte)value);
                }
                else if (value <= 255)
                {
                    _stream.WriteByte(0xcc);
                    _stream.WriteByte((byte)value);
                }
                else if (value <= 65535)
                {
                    _stream.WriteByte(0xcd);
                    WriteUInt16BigEndian((ushort)value);
                }
                else
                {
                    _stream.WriteByte(0xce);
                    WriteUInt32BigEndian((uint)value);
                }
            }
            else
            {
                if (value >= -32)
                {
                    _stream.WriteByte((byte)(value & 0xff));
                }
                else if (value >= -128)
                {
                    _stream.WriteByte(0xd0);
                    _stream.WriteByte((byte)value);
                }
                else if (value >= -32768)
                {
                    _stream.WriteByte(0xd1);
                    WriteInt16BigEndian((short)value);
                }
                else
                {
                    _stream.WriteByte(0xd2);
                    WriteInt32BigEndian(value);
                }
            }
        }

        public void WriteULong(ulong value)
        {
            if (value <= 127)
            {
                _stream.WriteByte((byte)value);
            }
            else if (value <= 255)
            {
                _stream.WriteByte(0xcc);
                _stream.WriteByte((byte)value);
            }
            else if (value <= 65535)
            {
                _stream.WriteByte(0xcd);
                WriteUInt16BigEndian((ushort)value);
            }
            else if (value <= uint.MaxValue)
            {
                _stream.WriteByte(0xce);
                WriteUInt32BigEndian((uint)value);
            }
            else
            {
                _stream.WriteByte(0xcf);
                WriteUInt64BigEndian(value);
            }
        }

        public void WriteDouble(double value)
        {
            _stream.WriteByte(0xcb);
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _stream.Write(bytes, 0, 8);
        }

        public void WriteNil()
        {
            _stream.WriteByte(0xc0);
        }

        public void WriteArrayHeader(int count)
        {
            if (count <= 15)
            {
                _stream.WriteByte((byte)(0x90 | count));
            }
            else if (count <= 65535)
            {
                _stream.WriteByte(0xdc);
                WriteUInt16BigEndian((ushort)count);
            }
            else
            {
                _stream.WriteByte(0xdd);
                WriteUInt32BigEndian((uint)count);
            }
        }

        private void WriteUInt16BigEndian(ushort value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        private void WriteUInt32BigEndian(uint value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        private void WriteUInt64BigEndian(ulong value)
        {
            _stream.WriteByte((byte)(value >> 56));
            _stream.WriteByte((byte)(value >> 48));
            _stream.WriteByte((byte)(value >> 40));
            _stream.WriteByte((byte)(value >> 32));
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        private void WriteInt16BigEndian(short value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        private void WriteInt32BigEndian(int value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
