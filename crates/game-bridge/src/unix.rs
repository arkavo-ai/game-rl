//! Unix socket transport implementation for game bridges
//!
//! Used for games that communicate over Unix domain sockets (e.g., .NET games via Harmony).

use crate::transport::{AsyncReader, AsyncWriter};
use async_trait::async_trait;
use game_rl_core::{GameRLError, Result};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::unix::{OwnedReadHalf, OwnedWriteHalf};

/// Unix socket read wrapper
pub struct UnixReadWrapper(pub OwnedReadHalf);

#[async_trait]
impl AsyncReader for UnixReadWrapper {
    async fn read_message(&mut self) -> Result<Vec<u8>> {
        // Read 4-byte length prefix (little-endian)
        let mut len_bytes = [0u8; 4];
        self.0
            .read_exact(&mut len_bytes)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Unix read length failed: {}", e)))?;
        let len = u32::from_le_bytes(len_bytes) as usize;

        // Sanity check on message size (max 64MB)
        if len > 64 * 1024 * 1024 {
            return Err(GameRLError::IpcError(format!(
                "Message too large: {} bytes",
                len
            )));
        }

        // Read message body
        let mut data = vec![0u8; len];
        self.0
            .read_exact(&mut data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Unix read data failed: {}", e)))?;

        Ok(data)
    }
}

/// Unix socket write wrapper
pub struct UnixWriteWrapper(pub OwnedWriteHalf);

#[async_trait]
impl AsyncWriter for UnixWriteWrapper {
    async fn write_message(&mut self, data: &[u8]) -> Result<()> {
        // Write 4-byte length prefix (little-endian)
        let len = (data.len() as u32).to_le_bytes();
        self.0
            .write_all(&len)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Unix write length failed: {}", e)))?;

        // Write message body
        self.0
            .write_all(data)
            .await
            .map_err(|e| GameRLError::IpcError(format!("Unix write data failed: {}", e)))?;

        // Flush to ensure data is sent
        self.0
            .flush()
            .await
            .map_err(|e| GameRLError::IpcError(format!("Unix flush failed: {}", e)))?;

        Ok(())
    }
}
