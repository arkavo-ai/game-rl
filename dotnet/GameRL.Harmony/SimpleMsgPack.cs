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

    /// <summary>
    /// Minimal MessagePack reader that works in Unity/Mono without complex initialization
    /// </summary>
    public class SimpleMsgPackReader
    {
        private readonly byte[] _data;
        private int _position;

        public SimpleMsgPackReader(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public int Position => _position;
        public bool HasMore => _position < _data.Length;

        public object? ReadValue()
        {
            if (_position >= _data.Length)
                return null;

            byte b = _data[_position++];

            // Positive fixint (0x00 - 0x7f)
            if (b <= 0x7f)
                return (int)b;

            // Fixmap (0x80 - 0x8f)
            if (b >= 0x80 && b <= 0x8f)
                return ReadMap(b & 0x0f);

            // Fixarray (0x90 - 0x9f)
            if (b >= 0x90 && b <= 0x9f)
                return ReadArray(b & 0x0f);

            // Fixstr (0xa0 - 0xbf)
            if (b >= 0xa0 && b <= 0xbf)
                return ReadString(b & 0x1f);

            // Negative fixint (0xe0 - 0xff)
            if (b >= 0xe0)
                return (int)(sbyte)b;

            switch (b)
            {
                case 0xc0: return null;           // nil
                case 0xc2: return false;          // false
                case 0xc3: return true;           // true
                case 0xcc: return (int)_data[_position++]; // uint8
                case 0xcd: return (int)ReadUInt16();       // uint16
                case 0xce: return (long)ReadUInt32();      // uint32
                case 0xcf: return ReadUInt64();            // uint64
                case 0xd0: return (int)(sbyte)_data[_position++]; // int8
                case 0xd1: return (int)ReadInt16();        // int16
                case 0xd2: return ReadInt32();             // int32
                case 0xd3: return ReadInt64();             // int64
                case 0xca: return ReadFloat();             // float32
                case 0xcb: return ReadDouble();            // float64
                case 0xd9: return ReadString(_data[_position++]); // str8
                case 0xda: return ReadString(ReadUInt16()); // str16
                case 0xdb: return ReadString((int)ReadUInt32()); // str32
                case 0xdc: return ReadArray(ReadUInt16()); // array16
                case 0xdd: return ReadArray((int)ReadUInt32()); // array32
                case 0xde: return ReadMap(ReadUInt16());   // map16
                case 0xdf: return ReadMap((int)ReadUInt32()); // map32
                case 0xc4: return ReadBinary(_data[_position++]); // bin8
                case 0xc5: return ReadBinary(ReadUInt16()); // bin16
                case 0xc6: return ReadBinary((int)ReadUInt32()); // bin32
                default:
                    throw new NotSupportedException($"Unsupported MessagePack format: 0x{b:X2}");
            }
        }

        private Dictionary<string, object?> ReadMap(int count)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < count; i++)
            {
                var key = ReadValue()?.ToString() ?? "";
                var value = ReadValue();
                dict[key] = value;
            }
            return dict;
        }

        private List<object?> ReadArray(int count)
        {
            var list = new List<object?>();
            for (int i = 0; i < count; i++)
            {
                list.Add(ReadValue());
            }
            return list;
        }

        private string ReadString(int length)
        {
            var str = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return str;
        }

        private byte[] ReadBinary(int length)
        {
            var bytes = new byte[length];
            Array.Copy(_data, _position, bytes, 0, length);
            _position += length;
            return bytes;
        }

        private ushort ReadUInt16()
        {
            ushort value = (ushort)((_data[_position] << 8) | _data[_position + 1]);
            _position += 2;
            return value;
        }

        private uint ReadUInt32()
        {
            uint value = ((uint)_data[_position] << 24) |
                         ((uint)_data[_position + 1] << 16) |
                         ((uint)_data[_position + 2] << 8) |
                         _data[_position + 3];
            _position += 4;
            return value;
        }

        private ulong ReadUInt64()
        {
            ulong value = ((ulong)_data[_position] << 56) |
                          ((ulong)_data[_position + 1] << 48) |
                          ((ulong)_data[_position + 2] << 40) |
                          ((ulong)_data[_position + 3] << 32) |
                          ((ulong)_data[_position + 4] << 24) |
                          ((ulong)_data[_position + 5] << 16) |
                          ((ulong)_data[_position + 6] << 8) |
                          _data[_position + 7];
            _position += 8;
            return value;
        }

        private short ReadInt16()
        {
            return (short)ReadUInt16();
        }

        private int ReadInt32()
        {
            return (int)ReadUInt32();
        }

        private long ReadInt64()
        {
            return (long)ReadUInt64();
        }

        private float ReadFloat()
        {
            var bytes = new byte[4];
            Array.Copy(_data, _position, bytes, 0, 4);
            _position += 4;
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        private double ReadDouble()
        {
            var bytes = new byte[8];
            Array.Copy(_data, _position, bytes, 0, 8);
            _position += 8;
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        // Helper methods to get typed values from a dictionary
        public static string GetString(Dictionary<string, object?> dict, string key, string defaultValue = "")
        {
            return dict.TryGetValue(key, out var val) && val is string s ? s : defaultValue;
        }

        public static int GetInt(Dictionary<string, object?> dict, string key, int defaultValue = 0)
        {
            if (!dict.TryGetValue(key, out var val)) return defaultValue;
            return val switch
            {
                int i => i,
                long l => (int)l,
                ulong ul => (int)ul,
                _ => defaultValue
            };
        }

        public static uint GetUInt(Dictionary<string, object?> dict, string key, uint defaultValue = 0)
        {
            if (!dict.TryGetValue(key, out var val)) return defaultValue;
            return val switch
            {
                int i => (uint)i,
                long l => (uint)l,
                ulong ul => (uint)ul,
                _ => defaultValue
            };
        }

        public static ulong? GetNullableULong(Dictionary<string, object?> dict, string key)
        {
            if (!dict.TryGetValue(key, out var val) || val == null) return null;
            return val switch
            {
                int i => (ulong)i,
                long l => (ulong)l,
                ulong ul => ul,
                _ => null
            };
        }

        public static string? GetNullableString(Dictionary<string, object?> dict, string key)
        {
            return dict.TryGetValue(key, out var val) && val is string s ? s : null;
        }

        public static Dictionary<string, object?>? GetMap(Dictionary<string, object?> dict, string key)
        {
            return dict.TryGetValue(key, out var val) && val is Dictionary<string, object?> d ? d : null;
        }
    }
}
