//! Vision stream types

use serde::{Deserialize, Serialize};

/// Pixel format for vision streams
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum PixelFormat {
    Rgba8,
    Bgra8,
    Rgb8,
    R32f,
    Rg32f,
}

impl PixelFormat {
    /// Bytes per pixel
    pub fn bytes_per_pixel(&self) -> usize {
        match self {
            PixelFormat::Rgba8 | PixelFormat::Bgra8 => 4,
            PixelFormat::Rgb8 => 3,
            PixelFormat::R32f => 4,
            PixelFormat::Rg32f => 8,
        }
    }
}

/// Vision stream descriptor
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StreamDescriptor {
    /// Stream identifier
    pub stream_id: String,
    /// Width in pixels
    pub width: u32,
    /// Height in pixels
    pub height: u32,
    /// Pixel format
    pub pixel_format: PixelFormat,
    /// Number of ring buffers
    pub ring_count: u32,
    /// Transport mechanism
    pub transport: StreamTransport,
    /// Synchronization mechanism
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sync: Option<StreamSync>,
}

/// Transport mechanism for vision data
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "Type", rename_all = "PascalCase")]
pub enum StreamTransport {
    /// macOS IOSurface
    IOSurface { surface_ids: Vec<u64> },
    /// POSIX shared memory
    Shm { shm_name: String, offsets: Vec<u64> },
    /// Windows DXGI shared texture
    Dxgi { shared_handles: Vec<u64> },
    /// Inline base64 (fallback, slow)
    Inline,
}

/// Synchronization mechanism
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "Type", rename_all = "PascalCase")]
pub enum StreamSync {
    MetalEvent { handle: u64 },
    D3dFence { handle: u64 },
    Semaphore { name: String },
    Polling,
}

/// Named stream profile
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StreamProfile {
    /// Profile name
    pub name: String,
    /// Streams in this profile
    pub streams: Vec<StreamConfig>,
}

/// Configuration for a single stream
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct StreamConfig {
    /// Stream name
    pub name: String,
    /// Stream type
    #[serde(rename = "Type")]
    pub stream_type: StreamType,
    /// Width in pixels
    pub width: u32,
    /// Height in pixels
    pub height: u32,
    /// Camera identifier
    #[serde(skip_serializing_if = "Option::is_none")]
    pub camera: Option<String>,
}

/// Type of vision stream
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum StreamType {
    Rgb,
    Depth,
    Segmentation,
    Flow,
}
