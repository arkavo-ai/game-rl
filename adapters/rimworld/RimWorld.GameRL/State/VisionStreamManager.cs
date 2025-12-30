// Vision stream capture with GPU IOSurface or shared memory fallback

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using Verse;

namespace RimWorld.GameRL.State
{
    /// <summary>
    /// Captures the main camera into a shared memory buffer or IOSurface.
    /// </summary>
    public sealed class VisionStreamManager : IDisposable
    {
        private const int BytesPerPixel = 4;
        private const int RingCount = 1;

        public string StreamId { get; }
        public int Width { get; }
        public int Height { get; }
        public string ShmPath { get; }

        private readonly RenderTexture _renderTexture;
        private readonly VisionTransport _transport;
        private readonly ulong _surfaceId;
        private bool _gpuAvailable;

        private readonly int _frameSize;
        private readonly byte[]? _buffer;
        private readonly Texture2D? _readTexture;
        private readonly FileStream? _fileStream;
        private readonly MemoryMappedFile? _mmf;
        private readonly MemoryMappedViewAccessor? _accessor;

        private enum VisionTransport
        {
            Shm,
            IOSurface
        }

        public VisionStreamManager(string streamId, int width, int height, string shmPath)
        {
            StreamId = streamId;
            Width = width;
            Height = height;
            ShmPath = shmPath;

            _frameSize = width * height * BytesPerPixel;
            _renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            _renderTexture.Create();

            if (VisionNative.TryCreateIOSurface(width, height, out var surfaceId))
            {
                _transport = VisionTransport.IOSurface;
                _surfaceId = surfaceId;
                _gpuAvailable = true;
                return;
            }

            _transport = VisionTransport.Shm;
            _surfaceId = 0;
            _gpuAvailable = false;

            _buffer = new byte[_frameSize];
            _readTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);

            var directory = Path.GetDirectoryName(shmPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileStream = new FileStream(shmPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            _fileStream.SetLength(_frameSize);
            _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, _frameSize, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            _accessor = _mmf.CreateViewAccessor(0, _frameSize, MemoryMappedFileAccess.ReadWrite);
        }

        public void Capture()
        {
            // Safety check: Don't capture during GUI phase to avoid OnGUI conflicts
            if (Event.current != null && Event.current.type != EventType.Ignore)
            {
                return;
            }

            var camera = ResolveCamera();
            if (camera == null || !camera.isActiveAndEnabled)
            {
                return;
            }

            // Additional null checks for render state
            if (_renderTexture == null || !_renderTexture.IsCreated())
            {
                return;
            }

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;

            try
            {
                camera.targetTexture = _renderTexture;
                camera.Render();

                if (_transport == VisionTransport.IOSurface)
                {
                    if (!_gpuAvailable)
                    {
                        return;
                    }

                    var nativeTexture = _renderTexture.GetNativeTexturePtr();
                    if (nativeTexture == IntPtr.Zero)
                    {
                        return;
                    }

                    _gpuAvailable = VisionNative.UpdateIOSurface(nativeTexture, _surfaceId);
                    return;
                }

                if (_readTexture == null || _accessor == null || _buffer == null)
                {
                    return;
                }

                RenderTexture.active = _renderTexture;
                _readTexture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                _readTexture.Apply(false);

                var raw = _readTexture.GetRawTextureData();
                if (raw.Length >= _frameSize)
                {
                    Array.Copy(raw, _buffer, _frameSize);
                    _accessor.WriteArray(0, _buffer, 0, _frameSize);
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
            }
        }

        private static Camera? ResolveCamera()
        {
            var camera = Camera.main;
            if (camera != null)
            {
                return camera;
            }

            var driver = Find.CameraDriver;
            if (driver == null)
            {
                return null;
            }

            var driverType = driver.GetType();
            var cameraProp = driverType.GetProperty("Camera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (cameraProp?.GetValue(driver) is Camera driverCamera)
            {
                return driverCamera;
            }

            var cameraField = driverType.GetField("camera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return cameraField?.GetValue(driver) as Camera;
        }

        public Dictionary<string, object> BuildDescriptor()
        {
            if (_transport == VisionTransport.IOSurface)
            {
                return new Dictionary<string, object>
                {
                    ["StreamId"] = StreamId,
                    ["Width"] = Width,
                    ["Height"] = Height,
                    ["PixelFormat"] = "bgra8",
                    ["RingCount"] = RingCount,
                    ["Transport"] = new Dictionary<string, object>
                    {
                        ["Type"] = "IOSurface",
                        ["SurfaceIds"] = new ulong[] { _surfaceId }
                    },
                    ["Sync"] = new Dictionary<string, object>
                    {
                        ["Type"] = "Polling"
                    }
                };
            }

            return new Dictionary<string, object>
            {
                ["StreamId"] = StreamId,
                ["Width"] = Width,
                ["Height"] = Height,
                ["PixelFormat"] = "rgba8",
                ["RingCount"] = RingCount,
                ["Transport"] = new Dictionary<string, object>
                {
                    ["Type"] = "Shm",
                    ["ShmName"] = ShmPath,
                    ["Offsets"] = new ulong[] { 0 }
                },
                ["Sync"] = new Dictionary<string, object>
                {
                    ["Type"] = "Polling"
                }
            };
        }

        public void Dispose()
        {
            if (_transport == VisionTransport.IOSurface && _surfaceId != 0)
            {
                VisionNative.ReleaseIOSurface(_surfaceId);
            }

            _accessor?.Dispose();
            _mmf?.Dispose();
            _fileStream?.Dispose();
            if (_readTexture != null)
            {
                UnityEngine.Object.Destroy(_readTexture);
            }
            UnityEngine.Object.Destroy(_renderTexture);
        }
    }

    internal static class VisionNative
    {
        public static bool TryCreateIOSurface(int width, int height, out ulong surfaceId)
        {
            surfaceId = 0;
            if (Application.platform != RuntimePlatform.OSXPlayer && Application.platform != RuntimePlatform.OSXEditor)
            {
                return false;
            }

            try
            {
                surfaceId = gamerl_create_iosurface(width, height);
                return surfaceId != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static bool UpdateIOSurface(IntPtr sourceTexture, ulong surfaceId)
        {
            if (sourceTexture == IntPtr.Zero || surfaceId == 0)
            {
                return false;
            }

            try
            {
                return gamerl_update_iosurface(sourceTexture, surfaceId) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        public static void ReleaseIOSurface(ulong surfaceId)
        {
            if (surfaceId == 0)
            {
                return;
            }

            try
            {
                gamerl_release_iosurface(surfaceId);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("gamerl_vision")]
        private static extern ulong gamerl_create_iosurface(int width, int height);

        [DllImport("gamerl_vision")]
        private static extern int gamerl_update_iosurface(IntPtr sourceTexture, ulong surfaceId);

        [DllImport("gamerl_vision")]
        private static extern void gamerl_release_iosurface(ulong surfaceId);
    }
}
